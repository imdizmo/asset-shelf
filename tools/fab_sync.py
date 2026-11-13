#!/usr/bin/env python3
"""Your Fab library through your Epic account, the way the Epic launcher (and legendary/Heroic) reach it.

    fab_sync.py login-url                 the page to sign in on (it ends on a page showing an authorizationCode)
    fab_sync.py login -                   trade the one-time code (read from stdin, never the command line, which other
                                          processes can read) for tokens, kept in the system keyring
    fab_sync.py whoami                    the signed-in account
    fab_sync.py library <out.json>        every library entry, written as JSON
    fab_sync.py logout
    fab_sync.py sync --packs <packs dir> [--index <index dir>] [--plan <out.json>] [--download] [--changed <out.txt>]
                     [--only <listing uid>...]
        compares the library with the packs on disk: new (no pack yet), updated (the seller shipped a new build of the
        same artifact), up to date. --download fetches the new and updated ones (Epic chunk manifests, every file SHA1-
        checked) into <packs>/<slug>/unreal and writes the changed pack folders to --changed, for index_library --redo.
        Needs legendary-gl and requests (tools/requirements.txt).

Tokens are kept with secret-tool (service asset-shelf, account epic); without it, in ~/.config/asset-shelf/epic.json
readable only by you. The access token is refreshed with the refresh token when it has under ten minutes left.
These are Epic's launcher endpoints: undocumented, used by the community launchers for years, and they may change.
"""
import base64, collections, concurrent.futures as cf, datetime, hashlib, json, os, re, shutil, subprocess, sys, threading, time
import urllib.parse, urllib.request

CLIENT_ID = '34a02cf8f4414e29b15921876da36f9a'      # launcherAppClient2: the public launcher client legendary uses
CLIENT_SECRET = 'daafbccc737745039dffe53d94fc76cf'
TOKEN_URL = 'https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token'
LOGIN_URL = 'https://www.epicgames.com/id/login?redirectUrl=' + urllib.parse.quote(
    f'https://www.epicgames.com/id/api/redirect?clientId={CLIENT_ID}&responseType=code')
UA = 'UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit'
FILE = os.path.expanduser('~/.config/asset-shelf/epic.json')


def store(tokens):
    data = json.dumps(tokens)
    if shutil.which('secret-tool'):
        subprocess.run(['secret-tool', 'store', '--label', 'Asset Shelf: Epic account', 'service', 'asset-shelf', 'account', 'epic'],
                       input=data, text=True, check=True)
    else:
        os.makedirs(os.path.dirname(FILE), exist_ok=True)
        fd = os.open(FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as f:
            f.write(data)


def load():
    if shutil.which('secret-tool'):
        r = subprocess.run(['secret-tool', 'lookup', 'service', 'asset-shelf', 'account', 'epic'], capture_output=True, text=True)
        return json.loads(r.stdout) if r.stdout.strip() else None
    return json.load(open(FILE)) if os.path.exists(FILE) else None


def forget():
    if shutil.which('secret-tool'):
        subprocess.run(['secret-tool', 'clear', 'service', 'asset-shelf', 'account', 'epic'])
    if os.path.exists(FILE):
        os.remove(FILE)


def token_call(form):
    req = urllib.request.Request(TOKEN_URL, data=urllib.parse.urlencode(dict(form, token_type='eg1')).encode(), headers={
        'Authorization': 'Basic ' + base64.b64encode(f'{CLIENT_ID}:{CLIENT_SECRET}'.encode()).decode(),
        'Content-Type': 'application/x-www-form-urlencoded', 'User-Agent': UA})
    try:
        return json.load(urllib.request.urlopen(req, timeout=30))
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors='replace')
        try:
            msg = json.loads(body).get('errorMessage') or body
        except ValueError:
            msg = body
        sys.exit(f'Epic refused the sign-in ({e.code}): {msg[:300]}')


def session():
    """A valid access token and the account id, refreshing them when they are about to run out."""
    t = load()
    if not t:
        sys.exit('not signed in: run "fab_sync.py login-url", sign in, then "fab_sync.py login <code>"')
    left = datetime.datetime.fromisoformat(t['expires_at'].replace('Z', '+00:00')) - datetime.datetime.now(datetime.timezone.utc)
    if left.total_seconds() < 600:
        t = token_call({'grant_type': 'refresh_token', 'refresh_token': t['refresh_token']})
        store(t)
    return t['access_token'], t['account_id'], t.get('displayName', '')


def fab_get(url, token):
    req = urllib.request.Request(url, headers={'Authorization': f'bearer {token}', 'Accept': 'application/json', 'User-Agent': UA})
    return json.load(urllib.request.urlopen(req, timeout=60))


def library(token, account):
    items, cursor = [], None
    while True:
        url = f'https://www.fab.com/e/accounts/{account}/ue/library?count=100' + (f'&cursor={urllib.parse.quote(cursor)}' if cursor else '')
        page = fab_get(url, token)
        items += page.get('results') or []
        cursor = (page.get('cursors') or {}).get('next')
        print(f'library: {len(items)} entries', file=sys.stderr, flush=True)
        if not cursor:
            return items


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def fab_post(url, token, body):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={
        'Authorization': f'bearer {token}', 'Content-Type': 'application/json', 'Accept': 'application/json', 'User-Agent': UA})
    return json.load(urllib.request.urlopen(req, timeout=60))


def listing_of(entry):
    m = re.search(r'listings/([0-9a-f-]{36})', entry.get('url') or '')
    return m.group(1) if m else None


def newest_artifact(entry):
    def key(pv):
        vs = [tuple(int(x) for x in re.findall(r'\d+', e)) for e in pv.get('engineVersions') or []]
        return max(vs) if vs else ()
    pvs = entry.get('projectVersions') or []
    return max(pvs, key=key) if pvs else None


def slug_for(entry, artifact):
    base = re.sub(r'[^A-Za-z0-9]+', '_', re.sub(r'\(.*?\)', '', entry.get('title') or 'pack')).strip('_')[:60]
    return f"{base}_{hashlib.sha1((artifact or entry['assetId']).encode()).hexdigest()[:6]}"


def plan(entries, packs):
    """Each library entry against the packs on disk (their pack.json: listing, app = artifact, build)."""
    local = {}
    for d in os.listdir(packs):
        pj = os.path.join(packs, d, 'pack.json')
        if os.path.exists(os.path.join(packs, d, 'unreal.manifest')) and os.path.exists(pj):
            try:
                j = json.load(open(pj))
            except ValueError:
                continue
            if j.get('listing'):
                local[j['listing']] = (d, j)
    out = {'new': [], 'updated': [], 'same': [], 'newer_engine': []}
    for e in entries:
        if e.get('source') == 'uem':
            continue  # Epic's own editor plugins from the old marketplace (Quixel Bridge, the Fab plugin): not assets
        lid = listing_of(e)
        have = local.get(lid)
        if not have:
            pv = newest_artifact(e)
            if pv:
                out['new'].append({'title': e['title'], 'listing': lid, 'asset': e['assetId'], 'namespace': e['assetNamespace'],
                                   'artifact': pv['artifactId'], 'build': (pv.get('buildVersions') or [{}])[0].get('buildVersion'),
                                   'slug': slug_for(e, pv['artifactId'])})
            continue
        slug, j = have
        same_art = next((pv for pv in e.get('projectVersions') or [] if pv['artifactId'] == j.get('app')), None)
        row = {'title': e['title'], 'listing': lid, 'asset': e['assetId'], 'namespace': e['assetNamespace'], 'slug': slug}
        if same_art:
            build = (same_art.get('buildVersions') or [{}])[0].get('buildVersion')
            row.update(artifact=same_art['artifactId'], build=build, had=j.get('build'))
            out['updated' if build and build != j.get('build') else 'same'].append(row)
        else:
            out['same'].append(row)
        top = newest_artifact(e)
        if top and top['artifactId'] != j.get('app'):
            out['newer_engine'].append(dict(row, artifact=top['artifactId'], engines=top.get('engineVersions')))
    return out


_http = threading.local()


def http_get(url):
    import requests
    if not hasattr(_http, 's'):
        _http.s = requests.Session()
        _http.s.headers['User-Agent'] = UA
    return _http.s.get(url, timeout=60)


def download(item, packs, token, stage_root):
    """Fetch one artifact: manifest from Fab, chunks to a local staging folder, files assembled and SHA1-checked into
    <packs>/<slug>/unreal.new, then swapped in. The pack's pack.json and unreal.manifest are written last."""
    from legendary.models.chunk import Chunk
    from legendary.models.manifest import Manifest
    info = fab_post(f"https://www.fab.com/e/artifacts/{item['artifact']}/manifest", token,
                    {'item_id': item['asset'], 'namespace': item['namespace'], 'platform': 'Windows'})['downloadInfo'][0]
    raw = None
    for dp in info['distributionPoints']:
        r = http_get(dp['manifestUrl'])
        if r.ok:
            raw = r.content
            break
    if raw is None or hashlib.sha1(raw).hexdigest() != info.get('manifestHash', hashlib.sha1(raw).hexdigest()):
        raise RuntimeError('manifest could not be fetched or does not match its hash')
    m = Manifest.read_all(raw)
    bases = [dp['manifestUrl'].split('?')[0].rsplit('/', 1)[0] for dp in info['distributionPoints']]
    need = {p.guid_num for fm in m.file_manifest_list.elements for p in fm.chunk_parts}
    chunks = [c for c in m.chunk_data_list.elements if c.guid_num in need]
    size = sum(fm.file_size for fm in m.file_manifest_list.elements)
    if shutil.disk_usage(stage_root).free < size * 1.1 + 5e9:
        raise RuntimeError(f'not enough space in {stage_root} for the chunks (~{size * 1.1 / 1e9:.0f} GB)')
    cdir = os.path.join(stage_root, item['slug'])
    os.makedirs(cdir, exist_ok=True)
    log(f"  {item['title']}: {size / 1e9:.2f} GB, {len(m.file_manifest_list.elements)} files, {len(chunks)} chunks")

    def fetch(c):
        dest = os.path.join(cdir, f'{c.guid_num:032x}.chunk')
        if os.path.exists(dest):
            return 0
        for attempt in range(6):
            try:
                r = http_get(f'{bases[attempt % len(bases)]}/{c.path}')
                if r.ok:
                    open(dest + '.part', 'wb').write(r.content)
                    os.replace(dest + '.part', dest)
                    return len(r.content)
            except Exception:
                pass
            time.sleep(2 * (attempt + 1))
        raise RuntimeError(f'chunk {c.guid_str} could not be fetched')

    t0, got = time.time(), 0
    with cf.ThreadPoolExecutor(12) as ex:
        for i, n in enumerate(ex.map(fetch, chunks), 1):
            got += n
            if i % 1000 == 0 or i == len(chunks):
                log(f'  {i}/{len(chunks)} chunks, {got / 1e6 / max(1, time.time() - t0):.1f} MB/s')
    root = os.path.join(packs, item['slug'])
    fresh = os.path.join(root, 'unreal.new')
    cache = collections.OrderedDict()

    def chunk(num):
        if num not in cache:
            cache[num] = Chunk.read_buffer(open(os.path.join(cdir, f'{num:032x}.chunk'), 'rb').read()).data
            if len(cache) > 48:  # decompressed chunks are ~1 MB: assembly holds ~50 MB at most
                cache.popitem(last=False)
        cache.move_to_end(num)
        return cache[num]

    for fm in m.file_manifest_list.elements:
        out = os.path.realpath(os.path.join(fresh, fm.filename))
        if not out.startswith(os.path.realpath(fresh) + os.sep):
            raise RuntimeError(f'manifest file outside the pack: {fm.filename}')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        h = hashlib.sha1()
        with open(out, 'wb') as f:
            for p in fm.chunk_parts:
                piece = chunk(p.guid_num)[p.offset:p.offset + p.size]
                f.write(piece)
                h.update(piece)
        if fm.sha_hash and any(fm.sha_hash) and h.digest() != fm.sha_hash:
            raise RuntimeError(f'{fm.filename} failed its SHA1 check')
    old = os.path.join(root, 'unreal')
    if os.path.isdir(old):
        shutil.rmtree(old)
    os.rename(fresh, old)
    open(os.path.join(root, 'unreal.manifest'), 'wb').write(raw)
    json.dump({'listing': item['listing'], 'title': item['title'], 'app': item['artifact'], 'build': m.meta.build_version,
               'bytes': size, 'files': len(m.file_manifest_list.elements)}, open(os.path.join(root, 'pack.json'), 'w'), indent=1)
    shutil.rmtree(cdir)
    log(f"  {item['title']}: done in {time.time() - t0:.0f}s")


def store_page(item, index_root):
    """index/<slug>/pack.json from the public listing (title, seller, category, tags, url, description, cover, up to
    12 pictures), for the Packs view. No login: the same data anyone sees on the listing page."""
    d = os.path.join(index_root, item['slug'])
    os.makedirs(os.path.join(d, 'gallery'), exist_ok=True)
    try:
        listing = json.loads(http_get(f"https://www.fab.com/i/listings/{item['listing']}").content)
    except Exception as e:
        log(f"  no store page for {item['title']}: {e}")
        listing = {}

    def pick(images, want):
        imgs = [i for i in images or [] if i.get('url')]
        big = [i for i in imgs if i.get('width', 0) >= want]
        return (min(big, key=lambda i: i['width']) if big else max(imgs, key=lambda i: i.get('width', 0))) if imgs else None

    def save(url, rel):
        r = http_get(url)
        if r.ok:
            open(os.path.join(d, rel), 'wb').write(r.content)
            return rel
        return None

    cover = pick(((listing.get('thumbnails') or [{}])[0]).get('images'), 640)
    gallery = [g for i, m in enumerate((listing.get('medias') or [])[:12])
               if (im := pick(m.get('images'), 1280)) and (g := save(im['url'], f'gallery/{i + 1:02d}.jpg'))]
    text = re.sub(r'\s+', ' ', re.sub(r'<[^>]+>', ' ', listing.get('description') or '')).strip()[:900]
    json.dump({'title': item['title'], 'seller': (listing.get('user') or {}).get('sellerName', ''),
               'category': ((listing.get('category') or {}).get('path') or ''), 'tags': [t.get('name') for t in listing.get('tags') or [] if t.get('name')],
               'licence': '', 'url': f"https://www.fab.com/listings/{item['listing']}", 'acquired': '', 'description': text,
               'cover': save(cover['url'], 'cover.jpg') if cover else None, 'gallery': gallery},
              open(os.path.join(d, 'pack.json'), 'w'), indent=1)


def sync(args):
    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default
    packs = opt('--packs')
    if not packs:
        sys.exit('sync needs --packs <packs dir>')
    token, account, name = session()
    log(f'signed in as {name}; reading the library')
    p = plan(library(token, account), packs)
    only = {a for i, a in enumerate(args) if i and args[i - 1] == '--only'}
    if only:
        p = {k: [r for r in v if r['listing'] in only] for k, v in p.items()}
    log(f"{len(p['new'])} new, {len(p['updated'])} updated, {len(p['same'])} up to date, "
        f"{len(p['newer_engine'])} with a build for a newer engine")
    for r in p['new'][:20]:
        log(f"  new: {r['title']}")
    for r in p['updated'][:20]:
        log(f"  updated: {r['title']} ({r.get('had')} -> {r.get('build')})")
    if opt('--plan'):
        json.dump(p, open(opt('--plan'), 'w'), indent=1)
    if '--download' not in args:
        return
    stage = os.path.expanduser('~/.cache/asset-shelf/staging')
    os.makedirs(stage, exist_ok=True)
    changed, failed = [], []
    for item in p['new'] + p['updated']:
        log(f"DOWNLOAD {item['title']}")
        try:
            download(item, packs, session()[0], stage)
            changed.append(item['slug'])
            if opt('--index'):
                store_page(item, opt('--index'))
        except Exception as e:  # one pack failing does not stop the others
            log(f"FAIL {item['title']}: {e}")
            failed.append(item['title'])
            shutil.rmtree(os.path.join(packs, item['slug'], 'unreal.new'), ignore_errors=True)
    if opt('--changed'):
        open(opt('--changed'), 'w').write(''.join(c + '\n' for c in changed))
    log(f'SYNC DONE {len(changed)} downloaded, {len(failed)} failed')


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    if cmd == 'login-url':
        print(LOGIN_URL)
    elif cmd == 'login' and len(sys.argv) > 2:
        code = (sys.stdin.read() if sys.argv[2] == '-' else sys.argv[2]).strip()
        if code.startswith('{'):  # the whole JSON page pasted: take its authorizationCode
            code = json.loads(code).get('authorizationCode') or ''
        t = token_call({'grant_type': 'authorization_code', 'code': code})
        store(t)
        print(f"signed in as {t.get('displayName', '?')}")
    elif cmd == 'whoami':
        _, account, name = session()
        print(f'{name} ({account})')
    elif cmd == 'library' and len(sys.argv) > 2:
        token, account, _ = session()
        items = library(token, account)
        json.dump(items, open(sys.argv[2], 'w'), indent=1)
        print(f'{len(items)} library entries -> {sys.argv[2]}')
    elif cmd == 'sync':
        sync(sys.argv[2:])
    elif cmd == 'logout':
        forget()
        print('signed out')
    else:
        sys.exit(__doc__)


if __name__ == '__main__':
    main()
