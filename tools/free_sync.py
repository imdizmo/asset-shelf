#!/usr/bin/env python3
"""Free asset sources (all CC0): Poly Haven (models and textures) and ambientCG (materials). One pack per asset.

    free_sync.py catalog --source polyhaven|ambientcg --out <catalog.json> [--packs <dir>]
    free_sync.py download --source polyhaven|ambientcg --packs <dir> --index <dir> --ids <file> [--res 2k] [--changed <file>]

catalog lists what the source offers ([{id, title, kind, categories, tags, author, thumb, version, url, have}], have
being "same", "updated" or null against the packs on disk). download fetches the chosen ids into
<packs>/<source>_<id>/raw/ (Poly Haven: glTF or the texture maps; ambientCG: the material's zip), checks every file
(Poly Haven's MD5, ambientCG's size), and writes <pack>/free.json (what was fetched, so a later catalog knows what
changed), and in <index>/<pack>/ the pack.json, the source's picture as cover.png, and provenance.json (where it came
from, by whom, CC0, a copy of the CC0 text in the library's licences/ folder).
Poly Haven's API asks every app to identify itself (the User-Agent below) and to credit it where its content shows
("Powered by Poly Haven"), which the app does.
"""
import datetime, hashlib, json, os, re, sys, time, urllib.parse, urllib.request

UA = {'User-Agent': 'AssetShelf (+https://github.com/imdizmo/asset-shelf)'}
MAX_FILE = 2 << 30
CC0_URL = 'https://creativecommons.org/publicdomain/zero/1.0/legalcode.txt'
SOURCES = {'polyhaven': 'Poly Haven', 'ambientcg': 'ambientCG'}


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def get(url, binary=False):
    if not url.startswith('https://'):
        raise ValueError('only https: ' + url)
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=120) as r:
        data = r.read(MAX_FILE + 1)
    if len(data) > MAX_FILE:
        raise ValueError('too large: ' + url)
    return data if binary else json.loads(data)


def slug(source, aid):
    return f"{source}_{re.sub(r'[^A-Za-z0-9_-]+', '_', aid)[:80]}"


def safe_rel(rel):
    """A path a source gives inside an asset, or None when it would leave the asset's folder."""
    parts = rel.replace('\\', '/').split('/')
    return None if not rel or rel.startswith('/') or any(p in ('', '.', '..') or ':' in p for p in parts) else '/'.join(parts)


# ---- the catalogs ----

def catalog_polyhaven():
    out = []
    for t, kind in (('models', 'model'), ('textures', 'material')):
        for aid, a in get(f'https://api.polyhaven.com/assets?t={t}').items():
            out.append({'id': aid, 'title': a.get('name') or aid, 'kind': kind, 'categories': a.get('categories', []),
                        'tags': a.get('tags', []), 'author': ', '.join(a.get('authors', {})),
                        'thumb': f'https://cdn.polyhaven.com/asset_img/thumbs/{urllib.parse.quote(aid)}.png?width=256&height=256',
                        'version': a.get('files_hash') or str(a.get('date_published', '')), 'url': f'https://polyhaven.com/a/{urllib.parse.quote(aid)}'})
    return out


def catalog_ambientcg():
    out, offset = [], 0
    while True:
        page = get(f'https://ambientcg.com/api/v2/full_json?type=Material&limit=250&offset={offset}&include=previewData,tagData')
        for a in page.get('foundAssets', []):
            aid = a['assetId']
            out.append({'id': aid, 'title': aid, 'kind': 'material', 'categories': [a.get('category')] if a.get('category') else [],
                        'tags': a.get('tags', []), 'author': 'ambientCG', 'thumb': (a.get('previewImage') or {}).get('256-PNG', ''),
                        'version': a.get('releaseDate', ''), 'url': f'https://ambientcg.com/view?id={urllib.parse.quote(aid)}'})
        offset += 250
        if offset >= page.get('numberOfResults', 0) or not page.get('foundAssets'):
            return out


def have(packs, source, row):
    try:
        rec = json.load(open(os.path.join(packs, slug(source, row['id']), 'free.json')))
    except (OSError, ValueError):
        return None
    return 'same' if rec.get('version') == row['version'] else 'updated'


# ---- downloading ----

def fetch(url, dest, md5=None, size=None):
    data = get(url, binary=True)
    if md5 and hashlib.md5(data).hexdigest() != md5:
        raise ValueError(f'{os.path.basename(dest)}: MD5 does not match')
    if size and len(data) != size:
        raise ValueError(f'{os.path.basename(dest)}: size does not match')
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest + '.part', 'wb') as f:
        f.write(data)
    os.replace(dest + '.part', dest)
    return len(data)


def files_polyhaven(aid, kind, res):
    """(relative path, url, md5, size) of what to fetch: a model's glTF with its textures at res, or a texture's maps."""
    f = get(f'https://api.polyhaven.com/files/{urllib.parse.quote(aid)}')
    if kind == 'model':
        g = (f.get('gltf') or {}).get(res) or next(iter((f.get('gltf') or {}).values()), None)
        if not g:
            raise ValueError('no glTF for this model')
        main = g['gltf']
        yield f'gltf/{aid}.gltf', main['url'], main.get('md5'), main.get('size')
        for rel, inc in (main.get('include') or {}).items():
            if (r := safe_rel(rel)):
                yield f'gltf/{r}', inc['url'], inc.get('md5'), inc.get('size')
        return
    for mapname, by_res in f.items():
        if mapname in ('blend', 'gltf', 'mtlx', 'usd') or not isinstance(by_res, dict):
            continue
        pick = by_res.get(res) or next(iter(by_res.values()), {})
        fmt = pick.get('jpg') or pick.get('png') or next(iter(pick.values()), None) if isinstance(pick, dict) else None
        if fmt and fmt.get('url'):
            name = safe_rel(os.path.basename(urllib.parse.urlparse(fmt['url']).path))
            if name:
                yield f'textures/{name}', fmt['url'], fmt.get('md5'), fmt.get('size')


def files_ambientcg(aid, res):
    a = get(f'https://ambientcg.com/api/v2/full_json?id={urllib.parse.quote(aid)}&include=downloadData')['foundAssets'][0]
    downloads = a['downloadFolders']['default']['downloadFiletypeCategories']['zip']['downloads']
    want = f'{res.upper()}-JPG'
    d = next((x for x in downloads if x.get('attribute') == want), None) or next((x for x in downloads if x.get('attribute', '').endswith('-JPG')), None)
    if not d:
        raise ValueError('no JPG zip for this material')
    if (name := safe_rel(d['fileName'])):
        yield f'material/{name}', d['downloadLink'], None, d.get('size')


def cc0_copy(index):
    """The CC0 text, once, in the library's licences/ folder (beside index/)."""
    lib = os.path.dirname(os.path.abspath(index.rstrip('/')))
    dest = os.path.join(lib, 'licences', 'cc0-1.0.txt')
    if not os.path.exists(dest):
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        fetch(CC0_URL, dest)
    data = open(dest, 'rb').read()
    return {'file': 'licences/cc0-1.0.txt', 'from': CC0_URL,  # relative to the library (the app looks there too) 'saved': datetime.date.today().isoformat(),
            'sha256': hashlib.sha256(data).hexdigest(), 'kind': 'licence'}


def download(source, row, packs, index, res):
    s = slug(source, row['id'])
    raw = os.path.join(packs, s, 'raw')
    files = list(files_polyhaven(row['id'], row['kind'], res) if source == 'polyhaven' else files_ambientcg(row['id'], res))
    got = {}
    for rel, url, md5, size in files:
        if size and size > MAX_FILE:
            raise ValueError(f'{rel}: too large')
        n = fetch(url, os.path.join(raw, rel), md5, size)
        got[rel] = md5 or n
    json.dump({'source': source, 'id': row['id'], 'version': row['version'], 'res': res, 'files': got}, open(os.path.join(packs, s, 'free.json'), 'w'), indent=1)
    out = os.path.join(index, s)
    os.makedirs(out, exist_ok=True)
    cover = None
    if row.get('thumb', '').startswith('https://'):
        try:
            fetch(row['thumb'], os.path.join(out, 'cover.png'))
            cover = 'cover.png'
        except Exception as e:  # a missing picture does not stop the pack
            log(f'  no picture: {e}')
    site = SOURCES[source]
    json.dump({'title': row['title'], 'licence': 'CC0', 'url': row['url'], 'seller': row.get('author') or site,
               'category': '/'.join(row.get('categories') or []), 'tags': row.get('tags') or [], 'cover': cover,
               'acquired': datetime.date.today().isoformat(), 'source': source}, open(os.path.join(out, 'pack.json'), 'w'), indent=1)
    prov = {'version': 1, 'source': {'site': site, 'url': row['url'], 'creator': row.get('author') or site, 'aggregator': False, 'originals': {}},
            'licence': {'name': 'CC0 1.0', 'tier': '', 'url': 'https://creativecommons.org/publicdomain/zero/1.0/', 'copies': [cc0_copy(index)],
                        'credit': f"{row['title']} by {row.get('author') or site}, from {site} (CC0)", 'conditions': []},
            'acquired': {'how': 'free download', 'date': datetime.date.today().isoformat(), 'account': '', 'via': site, 'purchase': ''},
            'receipts': []}
    json.dump(prov, open(os.path.join(out, 'provenance.json'), 'w'), indent=1)
    return s


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args and args.index(name) + 1 < len(args) else default
    source = opt('--source')
    if not args or args[0] not in ('catalog', 'download') or source not in SOURCES:
        sys.exit(__doc__)
    if args[0] == 'catalog':
        rows = catalog_polyhaven() if source == 'polyhaven' else catalog_ambientcg()
        if opt('--packs'):
            for r in rows:
                r['have'] = have(opt('--packs'), source, r)
        json.dump(rows, open(opt('--out'), 'w'))
        log(f'{len(rows)} in {SOURCES[source]}, {sum(1 for r in rows if r.get("have"))} already here')
        return
    packs, index = opt('--packs'), opt('--index')
    ids = [l.strip() for l in open(opt('--ids')) if l.strip()]
    rows = {r['id']: r for r in (catalog_polyhaven() if source == 'polyhaven' else catalog_ambientcg())}
    changed, failed = [], []
    for aid in ids:
        if aid not in rows:
            log(f'FAIL {aid}: not in {SOURCES[source]}')
            failed.append(aid)
            continue
        log(f'DOWNLOAD {rows[aid]["title"]}')
        try:
            changed.append(download(source, rows[aid], packs, index, opt('--res', '2k')))
        except Exception as e:  # one asset failing does not stop the others
            log(f'FAIL {aid}: {e}')
            failed.append(aid)
    if opt('--changed'):
        open(opt('--changed'), 'w').write(''.join(c + '\n' for c in changed))
    log(f'SYNC DONE {len(changed)} downloaded, {len(failed)} failed')


if __name__ == '__main__':
    main()


def _check():
    """Offline: names a source gives never leave the asset's folder; pack names stay plain."""
    assert safe_rel('textures/a_1k.jpg') == 'textures/a_1k.jpg'
    for bad in ('../x', '/etc/passwd', 'a/../../b', 'a//b', 'C:/x', ''):
        assert safe_rel(bad) is None, bad
    assert slug('polyhaven', 'Arm Chair/../01') == 'polyhaven_Arm_Chair_01'
    print('ok free_sync')
