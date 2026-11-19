#!/usr/bin/env python3
"""Your itch.io library: what you own, compared with the packs on disk, and the new or changed files downloaded.

    itch_sync.py login -                  an itch.io API key (itch.io/user/settings/api-keys) read from stdin, kept in the
                                          system keyring (secret-tool service asset-shelf, account itch)
    itch_sync.py sync --packs <dir> --index <dir> [--plan <out.json>] [--download] [--changed <out.txt>]
                                          [--only <text in the pack's folder name, such as a creator>]
                                          [--free <file of game page URLs>: free games you do not own, instead of your library]

Per game, one model format is kept (FBX, else Blend, glTF, OBJ, else a zip of unknown content; engine copies skipped),
in <packs>/itch_<creator>_<title>_<game id>/raw/<format>/; a game shipping only a .unitypackage gets its models and
textures pulled out into raw/unity. <pack>/itch.json records each kept file's upload id and MD5, so a later sync knows
what changed; files from before it existed are recognised by name and size. Every download is checked against the MD5
itch.io gives. The store page (cover, screenshots, licence words) goes to <index>/<pack>/pack.json.
"""
import html, json, os, re, shutil, subprocess, sys, tarfile, time, urllib.parse, urllib.request

UA = {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) Gecko/20100101 Firefox/140.0'}
RANK = ['fbx', 'blender', 'gltf', 'obj', 'other']
MODEL_EXT = ('.fbx', '.obj', '.blend', '.glb', '.gltf', '.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff')


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


KEY_FILE = os.path.expanduser('~/.config/asset-shelf/itch.key')


def key():
    if shutil.which('secret-tool'):
        for args in (['service', 'asset-shelf', 'account', 'itch'], ['service', 'itch']):  # the second: an older setup
            r = subprocess.run(['secret-tool', 'lookup', *args], capture_output=True, text=True)
            if r.stdout.strip():
                return r.stdout.strip()
    if os.path.exists(KEY_FILE):
        return open(KEY_FILE).read().strip()
    sys.exit('no itch.io API key: run "itch_sync.py login -" with the key on stdin')


def store_key(k):
    """The system keyring where there is one (secret-tool); otherwise a file only the user can read."""
    if shutil.which('secret-tool'):
        subprocess.run(['secret-tool', 'store', '--label', 'Asset Shelf: itch.io', 'service', 'asset-shelf', 'account', 'itch'], input=k, text=True, check=True)
        return
    os.makedirs(os.path.dirname(KEY_FILE), exist_ok=True)
    fd = os.open(KEY_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as f:
        f.write(k)


def api(path):
    req = urllib.request.Request('https://api.itch.io' + path, headers={'Authorization': 'Bearer ' + key()})
    return json.load(urllib.request.urlopen(req, timeout=60))


def slug(s):
    return re.sub(r'[^A-Za-z0-9._ -]+', '', s).strip().replace(' ', '_')[:90] or 'x'


def short(s):
    return re.sub(r'[^A-Za-z0-9]+', '_', re.sub(r'[\(\[].*?[\)\]]', '', s)).strip('_')[:50]


def fmt(name):
    """The model format an upload holds, judged by its file name; None for engine copies and non-model files."""
    n = name.lower()
    if n.endswith('.unitypackage') or re.search(r'godot|unity|unreal|urp|hdrp|\.pck|\.exe|\.apk', n):
        return None
    for k, f in (('fbx', 'fbx'), ('blend', 'blender'), ('gltf', 'gltf'), ('glb', 'gltf'), ('obj', 'obj')):
        if k in n:
            return f
    return 'other' if re.search(r'\.(zip|7z|rar|z\d\d)$', n) else None  # .z01, .z02…: the other parts of a split zip


def wanted(uploads):
    """The uploads to keep: those of the best model format, else the largest .unitypackage (as 'unity')."""
    have = [(u, fmt(u.get('filename') or '')) for u in uploads if u.get('filename')]
    have = [(u, f) for u, f in have if f]
    if have:
        best = min(RANK.index(f) for _, f in have)
        return [(u, f) for u, f in have if RANK.index(f) == best]
    unity = [u for u in uploads if (u.get('filename') or '').lower().endswith('.unitypackage')]
    return [(max(unity, key=lambda u: u.get('size') or 0), 'unity')] if unity else []


def library():
    keys, page = [], 1
    while True:
        d = api(f'/profile/owned-keys?page={page}')
        keys += d['owned_keys']
        log(f'library: {len(keys)} keys')
        if len(d['owned_keys']) < d['per_page']:
            break
        page += 1
    games, seen = [], set()
    for k in keys:
        g = k['game']
        if g['id'] in seen:
            continue  # a game bought twice (bundles) is one pack
        if g.get('classification') == 'tool':
            continue  # a program (an editor, a generator) is not a pack of assets
        seen.add(g['id'])
        uploads = api(f"/games/{g['id']}/uploads?download_key_id={k['id']}").get('uploads', [])
        creator = g['user'].get('display_name') or g['user']['username']
        games.append({'game': g, 'key': k['id'], 'acquired': k.get('created_at', ''), 'creator': creator, 'uploads': uploads,
                      'slug': f"itch_{short(creator)}_{short(g['title'])}_{g['id']}"})
    return games


def free(listing):
    """Free games you do not own, from a text file of their page URLs (one a line): itch.io's API lists and serves a
    free game's files to any signed-in account. One page request per game, paced."""
    games = []
    for url in [l.strip().rstrip('/') for l in open(listing) if l.strip() and not l.startswith('#')]:
        time.sleep(10)
        try:
            gid = json.loads(get(url + '/data.json') or b'{}').get('id')
            g = api(f'/games/{gid}')['game']
            uploads = api(f'/games/{gid}/uploads').get('uploads', [])
        except Exception as e:
            log(f'  not found or not free: {url} ({type(e).__name__})')
            continue
        creator = g['user'].get('display_name') or g['user']['username']
        games.append({'game': g, 'key': None, 'acquired': '', 'creator': creator, 'uploads': uploads,
                      'slug': f"itch_{short(creator)}_{short(g['title'])}_{g['id']}"})
        log(f"free: {g['title']} ({len(uploads)} files)")
    return games


def record_path(packs, s):
    return os.path.join(packs, s, 'itch.json')


def known(packs, g, keep):
    """The pack's record of its files; built from the files on disk the first time (name and size)."""
    rp = record_path(packs, g['slug'])
    if os.path.exists(rp):
        return json.load(open(rp))
    raw = os.path.join(packs, g['slug'], 'raw')
    if not os.path.isdir(raw):
        return None
    files = {}
    for u, f in keep:
        p = os.path.join(raw, f, slug(u['filename']))
        # itch.io gives no size for some uploads (externally stored ones): then the file being there is enough
        same_size = os.path.exists(p) and (not u.get('size') or os.path.getsize(p) == u.get('size'))
        if f == 'unity' and os.path.isdir(os.path.join(raw, 'unity')) or same_size:
            files[str(u['id'])] = {'md5': u.get('md5_hash'), 'filename': u['filename'], 'format': f}
    if not files:
        return None
    rec = {'game': g['game']['id'], 'files': files}
    json.dump(rec, open(rp, 'w'), indent=1)
    return rec


def plan(games, packs):
    out = {'new': [], 'updated': [], 'same': [], 'newer_engine': []}
    for g in games:
        keep = wanted(g['uploads'])
        if not keep:
            continue
        row = {'title': g['game']['title'], 'slug': g['slug'], 'game': g['game']['id']}
        rec = known(packs, g, keep)
        if rec is None:
            out['new'].append(row)
            continue
        now = {str(u['id']): u.get('md5_hash') for u, _ in keep}
        had = {i: f.get('md5') for i, f in rec['files'].items()}
        if now == had:
            out['same'].append(row)
        else:
            changed = sorted(u['filename'] for u, _ in keep if had.get(str(u['id'])) != u.get('md5_hash'))
            out['updated'].append(dict(row, had=f"{len(had)} files", build=', '.join(changed)[:200]))
    return out


def download_url(g, u):
    req = urllib.request.Request(f"https://api.itch.io/uploads/{u['id']}/download" + (f"?download_key_id={g['key']}" if g['key'] else ''),
                                 headers={'Authorization': 'Bearer ' + key()})

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a, **k):
            return None
    try:
        urllib.request.build_opener(NoRedirect).open(req, timeout=60)
    except urllib.error.HTTPError as e:
        loc = e.headers.get('Location')
        if loc and 'dropbox.com' in loc:
            loc = loc.replace('dl=0', 'dl=1')
        return loc
    return None


def fetch(g, u, dest):
    import hashlib
    url = download_url(g, u)
    if not url or urllib.parse.urlparse(url).scheme != 'https' or not urllib.parse.urlparse(url).hostname:
        raise RuntimeError(f"no https download link for {u['filename']}")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    r = subprocess.run(['curl', '-sSL', '--fail', '--retry', '3', '--proto', '=https', '--proto-redir', '=https', '-o', dest + '.part', '--', url], capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError(f"{u['filename']}: curl {r.returncode} {r.stderr.strip()[:120]}")
    h = hashlib.md5()
    with open(dest + '.part', 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    if u.get('md5_hash') and h.hexdigest() != u['md5_hash']:
        os.remove(dest + '.part')
        raise RuntimeError(f"{u['filename']}: MD5 does not match what itch.io gives")
    os.replace(dest + '.part', dest)


def from_unitypackage(pkg, dest):
    """Models and textures out of a .unitypackage (a tar.gz of <guid>/asset + <guid>/pathname), streamed; each asset
    parked under its guid until its path is known. Paths outside dest are refused."""
    park, paths = os.path.join(dest, '.guid'), {}
    os.makedirs(park, exist_ok=True)
    with tarfile.open(pkg, 'r|gz') as t:
        for m in t:
            parts = m.name.strip('./').split('/')
            if len(parts) != 2 or not m.isfile():
                continue
            guid, kind = parts
            if not re.fullmatch(r'[0-9a-fA-F]{32}', guid):
                continue
            if kind == 'pathname':
                paths[guid] = t.extractfile(m).read().decode('utf-8', 'replace').splitlines()[0]
            elif kind == 'asset':
                with open(os.path.join(park, guid), 'wb') as f:
                    shutil.copyfileobj(t.extractfile(m), f)
    root = os.path.realpath(dest)
    for guid, path in paths.items():
        src = os.path.join(park, guid)
        to = os.path.realpath(os.path.join(dest, path.lstrip('/')))
        if not os.path.exists(src) or not to.startswith(root + os.sep) or not to.lower().endswith(MODEL_EXT):
            continue
        os.makedirs(os.path.dirname(to), exist_ok=True)
        os.replace(src, to)
    shutil.rmtree(park)


def itch_host(url):
    """Only https URLs on itch.io's own hosts (the pages and img.itch.zone) are fetched for store pages: links scraped
    from a page could otherwise point anywhere, file:// included."""
    u = urllib.parse.urlparse(url)
    host = (u.hostname or '').lower()
    return u.scheme == 'https' and (host == 'itch.io' or host.endswith('.itch.io') or host == 'itch.zone' or host.endswith('.itch.zone'))


def get(url):
    if not itch_host(url):
        log(f'  skipped a link that is not https on itch.io: {url[:80]}')
        return None
    for i in range(3):
        try:
            return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=30).read()
        except Exception:
            time.sleep(2 * (i + 1))
    return None


def licence(creator, text):
    t = text.lower()
    if re.search(r'\bcc0\b|creative commons zero|public domain', t):
        return 'CC0: any use, no credit needed'
    if 'emaceart' in creator.lower():
        return 'EmaceArt Asset License: commercial use, no credit; no resale or redistribution'
    if re.search(r'cc[- ]by\b|creative commons attribution', t):
        return 'CC-BY: credit the author'
    return 'Unknown: read the game page'


def store_page(g, index):
    d = os.path.join(index, g['slug'])
    if os.path.exists(os.path.join(d, 'pack.json')):
        return
    os.makedirs(os.path.join(d, 'gallery'), exist_ok=True)
    game = g['game']
    time.sleep(10)  # itch.io answers 429 to a run of page requests: one game page every ten seconds at most
    page = (get(game['url']) or b'').decode('utf-8', 'replace')
    m = re.search(r'formatted_description.*?</div>\s*</div>', page, re.S)
    text = re.sub(r'\s+', ' ', re.sub(r'<[^>]+>', ' ', html.unescape(m.group(0) if m else ''))).strip()
    cover = None
    if game.get('cover_url') and (b := get(game['cover_url'])):
        open(os.path.join(d, 'cover.jpg'), 'wb').write(b)
        cover = 'cover.jpg'
    shots = re.findall(r'<a[^>]+data-image_lightbox[^>]+href="([^"]+)"', page)
    gallery = []
    for i, u in enumerate(shots[:12]):
        if (b := get(html.unescape(u))):
            p = f'gallery/{i + 1:02d}.jpg'
            open(os.path.join(d, p), 'wb').write(b)
            gallery.append(p)
    desc = ((game.get('short_text') or '') + '\n\n' + text[:900]).strip()
    json.dump({'title': game['title'], 'seller': g['creator'], 'category': 'itch.io', 'tags': ['itch.io'],
               'licence': licence(g['creator'], text), 'url': game['url'], 'acquired': g['acquired'], 'description': desc,
               'cover': cover, 'gallery': gallery, 'source': 'itch.io'}, open(os.path.join(d, 'pack.json'), 'w'), indent=1)


def download(g, packs, index):
    keep = wanted(g['uploads'])
    raw = os.path.join(packs, g['slug'], 'raw')
    rec = known(packs, g, keep) or {'game': g['game']['id'], 'files': {}}
    stage = os.path.expanduser('~/.cache/asset-shelf/staging/itch')
    for u, f in keep:
        if rec['files'].get(str(u['id']), {}).get('md5') == u.get('md5_hash') and u.get('md5_hash'):
            continue
        log(f"  {u['filename']} ({(u.get('size') or 0) / 1e6:.0f} MB)")
        if f == 'unity':
            pkg = os.path.join(stage, slug(u['filename']))
            fetch(g, u, pkg)
            shutil.rmtree(os.path.join(raw, 'unity'), ignore_errors=True)
            from_unitypackage(pkg, os.path.join(raw, 'unity.part'))
            os.rename(os.path.join(raw, 'unity.part'), os.path.join(raw, 'unity'))
            os.remove(pkg)
        else:
            dest = os.path.join(raw, f, slug(u['filename']))
            fetch(g, u, dest)
            unpacked = dest[:-4] if dest.lower().endswith('.zip') else None
            if unpacked and os.path.isdir(unpacked):
                shutil.rmtree(unpacked)  # the indexer unpacks it again from the new archive
        rec['files'][str(u['id'])] = {'md5': u.get('md5_hash'), 'filename': u['filename'], 'format': f}
    json.dump(rec, open(record_path(packs, g['slug']), 'w'), indent=1)
    store_page(g, index)


def main():
    args = sys.argv[1:]

    def opt(name):
        return args[args.index(name) + 1] if name in args else None
    if args[:2] == ['login', '-']:
        k = sys.stdin.read().strip()
        me = json.load(urllib.request.urlopen(urllib.request.Request('https://api.itch.io/profile', headers={'Authorization': 'Bearer ' + k}), timeout=30))
        if 'user' not in me:
            sys.exit('itch.io did not accept that key')
        store_key(k)
        print(f"signed in as {me['user'].get('username')}")
        return
    if not args or args[0] != 'sync' or not opt('--packs') or not opt('--index'):
        sys.exit(__doc__)
    packs, index = opt('--packs'), opt('--index')
    games = free(opt('--free')) if opt('--free') else library()
    if opt('--only'):  # only the packs whose folder name holds this text (itch_<creator>_<title>_<id>)
        games = [g for g in games if opt('--only').lower() in g['slug'].lower()]
    p = plan(games, packs)
    log(f"{len(p['new'])} new, {len(p['updated'])} updated, {len(p['same'])} up to date")
    for r in p['new'][:20]:
        log(f"  new: {r['title']}")
    for r in p['updated'][:20]:
        log(f"  updated: {r['title']} ({r['build']})")
    if opt('--plan'):
        json.dump(p, open(opt('--plan'), 'w'), indent=1)
    if '--download' not in args:
        return
    by = {g['slug']: g for g in games}
    changed, failed = [], []
    for row in p['new'] + p['updated']:
        log(f"DOWNLOAD {row['title']}")
        try:
            download(by[row['slug']], packs, index)
            changed.append(row['slug'])
        except Exception as e:  # one game failing does not stop the others
            log(f"FAIL {row['title']}: {e}")
            failed.append(row['title'])
    if opt('--changed'):
        open(opt('--changed'), 'w').write(''.join(c + '\n' for c in changed))
    log(f'SYNC DONE {len(changed)} downloaded, {len(failed)} failed')


if __name__ == '__main__':
    main()
