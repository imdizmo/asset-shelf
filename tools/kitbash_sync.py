#!/usr/bin/env python3
"""The KitBash3D kits you bought (the site's "My Kits"), compared with the packs on disk, and the new or changed ones
downloaded. Kits of the free Cargo library are not covered: the site gives no full-kit download for them.

    kitbash_sync.py login --firefox       take the session of cargo-app.kitbash3d.com from Firefox (sign in there first)
    kitbash_sync.py login -               the same from stdin: the site's Cookie header (accessToken=...)
    kitbash_sync.py sync --packs <dir> --index <dir> [--plan <out.json>] [--download] [--changed <out.txt>]
                         [--only <kit>...] [--dcc "Unreal (5.1 and earlier)"] [--resolution "2k UAsset"]

KitBash3D has no public API: this asks the site what its own "Download Full Kit" button asks, with your browser
session, and may stop working when the site changes. The session is kept in the system keyring (secret-tool service
asset-shelf, account kitbash) and is never renewed here, so your browser stays signed in; when it has run out (a few
days), open the site in the browser and run login again.

An Unreal download is unpacked to <packs>/kitbash_<kit>/unreal/Content; any other --dcc (for example "FBX + OBJ" with
--resolution "4K PNGs") is kept as zips in <pack>/raw/. <pack>/kitbash.json records the kit's version and files, so a
later sync knows what changed. Every file is checked against the size, and the MD5 where Amazon gives one. The kit's
page (cover, pictures, description) goes to <index>/<pack>/pack.json. Mind your licence: an Individual licence is for
one person, so keep those packs out of a library other people can read.
"""
import concurrent.futures as cf, glob, hashlib, json, os, re, shutil, sqlite3, subprocess, sys, tempfile, time, zipfile
import urllib.error, urllib.parse, urllib.request

HOST = 'https://cargo-app.kitbash3d.com'
PAGE = HOST + '/settings/kits'
UA = 'Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0'
KEY_FILE = os.path.expanduser('~/.config/asset-shelf/kitbash.key')
KEYRING = ['service', 'asset-shelf', 'account', 'kitbash']
FIREFOX = ['~/.mozilla/firefox', '~/.config/mozilla/firefox', '~/.var/app/org.mozilla.firefox/.mozilla/firefox',
           '~/snap/firefox/common/.mozilla/firefox', '~/Library/Application Support/Firefox/Profiles',
           os.path.join(os.environ.get('APPDATA', '~'), 'Mozilla', 'Firefox', 'Profiles')]


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def session():
    if shutil.which('secret-tool'):
        r = subprocess.run(['secret-tool', 'lookup', *KEYRING], capture_output=True, text=True)
        if r.stdout.strip():
            return r.stdout.strip()
    if os.path.exists(KEY_FILE):
        return open(KEY_FILE).read().strip()
    sys.exit('not signed in: run "kitbash_sync.py login --firefox"')


def store_session(s):
    """The system keyring where there is one (secret-tool); otherwise a file only the user can read."""
    if shutil.which('secret-tool'):
        subprocess.run(['secret-tool', 'store', '--label', 'Asset Shelf: KitBash3D', *KEYRING], input=s, text=True, check=True)
        return
    os.makedirs(os.path.dirname(KEY_FILE), exist_ok=True)
    with os.fdopen(os.open(KEY_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as f:
        f.write(s)


def firefox_session():
    """The site's accessToken cookie from the most recently used Firefox profile. The cookie store is copied first
    (Firefox keeps it locked, and its newest rows are in the -wal file), into a folder only the user can read."""
    dbs = [p for root in FIREFOX for p in glob.glob(os.path.join(os.path.expanduser(root), '*', 'cookies.sqlite'))]
    for db in sorted(dbs, key=os.path.getmtime, reverse=True):
        with tempfile.TemporaryDirectory() as tmp:
            for f in glob.glob(db + '*'):
                shutil.copy(f, tmp)
            con = sqlite3.connect(os.path.join(tmp, 'cookies.sqlite'))
            try:
                row = con.execute("select value from moz_cookies where host = 'cargo-app.kitbash3d.com' and name = 'accessToken'").fetchone()
            finally:
                con.close()
        if row:
            return 'accessToken=' + row[0]
    sys.exit('no KitBash3D session in Firefox: sign in on cargo-app.kitbash3d.com there first')


def call(path, body=None, headers=None, cookie=None):
    """GET, or POST when there is a body, on the site only: the session never goes to another host."""
    h = {'User-Agent': UA, 'Cookie': cookie or session(), **(headers or {})}
    req = urllib.request.Request(HOST + path, data=body, headers=h)
    try:
        return urllib.request.urlopen(req, timeout=60).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        if e.code in (401, 403):
            sys.exit('KitBash3D did not accept the session: open the site in the browser, then run login again')
        raise


def page_list(text, name):
    """A named list out of the page's embedded data."""
    i = text.find(f'"{name}":[')
    return json.JSONDecoder().raw_decode(text, i + len(name) + 3)[0] if i >= 0 else []


def site():
    """The kits page: its data (what you bought, the catalogue) and the ids of the two download calls, which change
    with every release of the site and so are read from its scripts each time."""
    html = call('/settings/kits')
    data = ''.join(json.loads(p) for p in re.findall(r'self\.__next_f\.push\(\[1,("(?:[^"\\]|\\.)*")\]\)', html))
    chunks = sorted(set(re.findall(r'/_next/static/[A-Za-z0-9_./-]+\.js', html)))

    def refs(c):
        js = urllib.request.urlopen(urllib.request.Request(HOST + c, headers={'User-Agent': UA}), timeout=60).read().decode('utf-8', 'replace')
        return re.findall(r'createServerReference\)\("([0-9a-f]{20,})",[^)]*?"(getKitLink|getLegacyKitDownloadLink)"\)', js)
    with cf.ThreadPoolExecutor(8) as pool:
        actions = {name: i for found in pool.map(refs, chunks) for i, name in found}
    if 'getKitLink' not in actions:
        sys.exit('the site has changed: its download call was not found (this tool needs an update)')
    return page_list(data, 'purchasedProducts'), page_list(data, 'cargoKits'), actions


def kits():
    products, catalogue, actions = site()
    by_name = {k['name'].lower(): k for k in catalogue}
    out = {}
    for p in products:
        m = p.get('metafields') or {}
        name = (m.get('kitName') or '').lower()
        if not name:
            continue
        s3 = m.get('s3FileName')
        out[name] = {'kit': name, 'title': p['title'], 'legacy': True, 'renderers': m.get('renderers') or {},
                     's3': s3 if s3 and not s3.startswith('$') else None, 'separate': bool(m.get('hasAwsSeparateDownloads')),
                     'product': p, 'cargo': by_name.get(name)}
    for k in out.values():
        k['slug'] = 'kitbash_' + re.sub(r'[^a-z0-9]+', '_', re.sub(r'^kb3d_', '', k['kit'].lower())).strip('_')
        k['version'] = (k['cargo'] or {}).get('version') or ''
    return list(out.values()), actions


def strings(o):
    if isinstance(o, str):
        yield o
    elif isinstance(o, (list, dict)):
        for v in (o.values() if isinstance(o, dict) else o):
            yield from strings(v)


def links(k, actions, dcc, resolution):
    """The download links for a kit: what the "Download Full Kit" button gets. Only https links on Amazon are kept,
    as the site itself does."""
    if k['legacy'] and not k['renderers']:
        if not k['s3'] or 'getLegacyKitDownloadLink' not in actions:
            raise RuntimeError('the site offers no download for this kit')
        action, arg = actions['getLegacyKitDownloadLink'], {'s3FileName': k['s3']}
    else:
        if k['legacy'] and not k['separate']:
            resolution, pref = '4K PNGs', 'Assets'
        else:
            pref = 'All files'
        action, arg = actions['getKitLink'], {'kitName': k['kit'], 'dcc': dcc, 'renderEngine': 'Native', 'resolution': resolution, 'downloadPreference': pref}
    text = call('/settings/kits', json.dumps([arg]).encode(), {'Next-Action': action, 'Accept': 'text/x-component',
                                                             'Content-Type': 'text/plain;charset=UTF-8', 'Origin': HOST})
    found, error = [], None
    for line in text.splitlines():
        try:
            row = json.loads(line.split(':', 1)[1])
        except (IndexError, ValueError):
            continue
        if isinstance(row, dict) and row.get('serverError'):
            error = str(row['serverError'])[:200]
        for s in strings(row):
            u = urllib.parse.urlparse(s)
            if u.scheme == 'https' and (u.hostname or '').endswith('.amazonaws.com'):
                found.append(s)
    if not found:
        raise RuntimeError(error or 'the site gave no download link (is this format offered for the kit?)')
    return found


def fetch(url, dest):
    """Streams one file to dest; its size, and its MD5 where Amazon's ETag is one, must match."""
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    for attempt in range(3):
        try:
            h = hashlib.md5()
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=120) as r, open(dest + '.part', 'wb') as f:
                size, etag = int(r.headers.get('Content-Length') or 0), (r.headers.get('ETag') or '').strip('"')
                for b in iter(lambda: r.read(1 << 20), b''):
                    h.update(b)
                    f.write(b)
            if size and os.path.getsize(dest + '.part') != size:
                raise RuntimeError('the download is cut short')
            if re.fullmatch(r'[0-9a-f]{32}', etag) and h.hexdigest() != etag:
                raise RuntimeError('MD5 does not match what Amazon gives')
            os.replace(dest + '.part', dest)
            return os.path.getsize(dest)
        except Exception as e:
            if os.path.exists(dest + '.part'):
                os.remove(dest + '.part')
            if attempt == 2:
                raise RuntimeError(f'{os.path.basename(dest)}: {e}')
            time.sleep(5 * (attempt + 1))


def unpack_unreal(archive, content):
    """A kit's Unreal zip into <content>: whatever lies under its Content folder (or the whole zip when it has none).
    Entries that would land outside are refused; files are streamed one at a time."""
    root = os.path.realpath(content)
    with zipfile.ZipFile(archive) as z:
        names = [i for i in z.infolist() if not i.is_dir()]
        has_content = any('/Content/' in '/' + i.filename for i in names)
        for i in names:
            rel = i.filename.replace('\\', '/')
            if has_content:
                if '/Content/' not in '/' + rel:
                    continue
                rel = ('/' + rel).split('/Content/', 1)[1]
            to = os.path.realpath(os.path.join(content, rel))
            if not to.startswith(root + os.sep):
                raise RuntimeError(f'{os.path.basename(archive)}: an entry points outside the pack')
            os.makedirs(os.path.dirname(to), exist_ok=True)
            with z.open(i) as src, open(to, 'wb') as dst:
                shutil.copyfileobj(src, dst, 1 << 20)


def plan(all_kits, packs):
    out = {'new': [], 'updated': [], 'same': []}
    for k in all_kits:
        row = {'title': k['title'], 'slug': k['slug'], 'kit': k['kit'], 'build': k['version']}
        rp = os.path.join(packs, k['slug'], 'kitbash.json')
        if not os.path.exists(rp):
            out['new'].append(row)
            continue
        had = json.load(open(rp)).get('version') or ''
        out['updated' if k['version'] and had != k['version'] else 'same'].append(dict(row, had=had))
    return out


def picture(url, dest):
    """Pictures only from https on the hosts the site uses for them."""
    u = urllib.parse.urlparse(url or '')
    if u.scheme != 'https' or not re.search(r'(^|\.)(shopify\.com|cloudfront\.net)$', u.hostname or ''):
        return False
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=30) as r, open(dest, 'wb') as f:
            shutil.copyfileobj(r, f)
        return True
    except Exception:
        return False


def store_page(k, index):
    d = os.path.join(index, k['slug'])
    if os.path.exists(os.path.join(d, 'pack.json')):
        return
    os.makedirs(os.path.join(d, 'gallery'), exist_ok=True)
    p, c = k['product'] or {}, k['cargo'] or {}
    images = [i.get('url') for i in p.get('images') or []] or [c.get('posterImageUrl')]
    cover = 'cover.jpg' if images and picture(images[0], os.path.join(d, 'cover.jpg')) else None
    gallery = [f'gallery/{n:02d}.jpg' for n, u in enumerate(images[1:13], 1) if picture(u, os.path.join(d, f'gallery/{n:02d}.jpg'))]
    genre = (p.get('metafields') or {}).get('genre')
    json.dump({'title': k['title'], 'seller': 'KitBash3D', 'category': p.get('productType') or c.get('kitType') or 'KitBash3D',
               'tags': ['KitBash3D'] + ([genre] if genre else []),
               'licence': 'KitBash3D EULA, the tier you bought (Individual: one person, no sharing); no redistribution',
               'url': f"https://kitbash3d.com/products/{p['handle']}" if p.get('handle') else HOST, 'description': (p.get('description') or c.get('description') or '')[:900],
               'cover': cover, 'gallery': gallery, 'source': 'KitBash3D'}, open(os.path.join(d, 'pack.json'), 'w'), indent=1)


def download(k, actions, packs, index, dcc, resolution):
    root = os.path.join(packs, k['slug'])
    stage = os.path.expanduser(os.path.join('~/.cache/asset-shelf/staging/kitbash', k['slug']))
    unreal = 'Unreal' in dcc and not (k['legacy'] and not k['renderers'])
    files = []
    try:
        for url in links(k, actions, dcc, resolution):
            name = re.sub(r'[^A-Za-z0-9._ -]+', '', os.path.basename(urllib.parse.unquote(urllib.parse.urlparse(url).path))) or 'kit.zip'
            size = fetch(url, os.path.join(stage, name))
            log(f'  {name} ({size / 1e6:.0f} MB)')
            files.append({'name': name, 'bytes': size})
        if unreal:
            new = os.path.join(root, 'unreal.new')
            shutil.rmtree(new, ignore_errors=True)
            for f in files:
                unpack_unreal(os.path.join(stage, f['name']), os.path.join(new, 'Content'))
            shutil.rmtree(os.path.join(root, 'unreal'), ignore_errors=True)
            os.rename(new, os.path.join(root, 'unreal'))
        else:
            raw = os.path.join(root, 'raw', 'fbx' if 'FBX' in dcc else 'other')
            shutil.rmtree(raw, ignore_errors=True)  # the indexer unpacks the zips again
            os.makedirs(raw)
            for f in files:
                if zipfile.is_zipfile(os.path.join(stage, f['name'])) and zipfile.ZipFile(os.path.join(stage, f['name'])).testzip():
                    raise RuntimeError(f"{f['name']}: the zip is damaged")
                shutil.move(os.path.join(stage, f['name']), os.path.join(raw, f['name']))
    finally:
        shutil.rmtree(stage, ignore_errors=True)
    json.dump({'kit': k['kit'], 'title': k['title'], 'version': k['version'], 'dcc': dcc, 'resolution': resolution, 'files': files},
              open(os.path.join(root, 'kitbash.json'), 'w'), indent=1)  # written last: a pack with it is whole
    store_page(k, index)


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default
    if args[:1] == ['login'] and args[1:2] in (['--firefox'], ['-']):
        s = firefox_session() if args[1] == '--firefox' else sys.stdin.read().strip()
        got = json.loads(call('/api/access', cookie=s))
        store_session(s)
        print(f"signed in: {len(got.get('purchased_kit_slugs', []))} bought kits")
        return
    if not args or args[0] != 'sync' or not opt('--packs') or not opt('--index'):
        sys.exit(__doc__)
    packs, index = opt('--packs'), opt('--index')
    dcc, resolution = opt('--dcc', 'Unreal (5.1 and earlier)'), opt('--resolution', '2k UAsset')
    all_kits, actions = kits()
    if '--only' in args:
        only = {a.lower() for a in args[args.index('--only') + 1:] if not a.startswith('--')}
        all_kits = [k for k in all_kits if k['kit'].lower() in only or k['slug'] in only]
    p = plan(all_kits, packs)
    log(f"{len(p['new'])} new, {len(p['updated'])} updated, {len(p['same'])} up to date")
    for r in p['new']:
        log(f"  new: {r['title']} ({r['kit']})")
    for r in p['updated']:
        log(f"  updated: {r['title']} ({r['had']} -> {r['build']})")
    if opt('--plan'):
        json.dump(p, open(opt('--plan'), 'w'), indent=1)
    if '--download' not in args:
        return
    by = {k['slug']: k for k in all_kits}
    changed, failed = [], []
    for row in p['new'] + p['updated']:
        log(f"DOWNLOAD {row['title']}")
        try:
            download(by[row['slug']], actions, packs, index, dcc, resolution)
            changed.append(row['slug'])
        except Exception as e:  # one kit failing does not stop the others
            log(f"FAIL {row['title']}: {e}")
            failed.append(row['title'])
    if opt('--changed'):
        open(opt('--changed'), 'w').write(''.join(c + '\n' for c in changed))
    log(f'SYNC DONE {len(changed)} downloaded, {len(failed)} failed')


if __name__ == '__main__':
    main()
