"""Fills index/<pack>/provenance.json for packs already in the library, from what is known about them:

    provenance_backfill.py --index <library>/index --packs <library>/packs [--only id,id] [--dry-run] [--no-network]

- source: the store page and creator from pack.json; the site from the pack id (itch_, patreon_) or the address.
- licence: name and tier from pack.json's licence words; a dated copy of its text from (a) licence and readme files
  shipped at the top of the pack, (b) for itch.io, the listing's description (where creators state their terms), and
  (c) for Fab, one shared copy of Fab's licence page saved in <library>/licences/.
- acquired: the date from pack.json; for itch.io, bought or free download (the download key has a purchase or not)
  and the account, from the API (key in the keyring). Fab's library does not say whether a pack was bought or
  claimed free, so it stays "unknown".
Only empty fields are filled: whatever was entered by hand stays. Pages and files are untrusted: https only, at most
2 MB read, text only (scripts and markup dropped), licence files at most 256 KB.
"""
import argparse
import datetime
import hashlib
import html
import json
import os
import re
import shutil
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

MAX_PAGE = 2 << 20
MAX_TEXT = 256 << 10
# Fab's page serves the Standard License summary as HTML; the binding full EULA is drawn by a script from another
# service, so it is saved by hand (any file in <library>/licences/ referenced from a pack counts as a copy)
FAB_LICENCE = 'https://www.fab.com/eula'
LICENCE_FILE = re.compile(r'^(licen[cs]e|copying|eula|terms|readme)[^/]*\.(txt|md|rtf|html?)$|^(licen[cs]e|copying)$', re.I)
TODAY = datetime.date.today().isoformat()


def clean(text, cap=MAX_TEXT):
    """Plain text only: control and bidi/format characters dropped (tabs and line ends kept), capped."""
    out = []
    for ch in text:
        if ch in '\n\t' or (ord(ch) >= 32 and not (0x200B <= ord(ch) <= 0x200F or 0x202A <= ord(ch) <= 0x202E or 0x2066 <= ord(ch) <= 0x2069 or ord(ch) == 0x7F)):
            out.append(ch)
        if len(out) >= cap:
            break
    return ''.join(out)


def page_text(url):
    """A web page as text: https only, 2 MB at most, scripts, styles and tags dropped."""
    if not url.startswith('https://'):
        return None
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'AssetShelf-provenance/1'})
        with urllib.request.urlopen(req, timeout=30) as r:
            raw = r.read(MAX_PAGE + 1)[:MAX_PAGE].decode('utf-8', 'replace')
    except Exception as e:  # a page that cannot be read is skipped, not fatal
        print(f'  page not read: {url} ({type(e).__name__})')
        return None
    return raw


def to_text(markup):
    t = re.sub(r'(?is)<(script|style|noscript)[^>]*>.*?</\1>', ' ', markup)
    t = re.sub(r'(?i)<br\s*/?>|</p>|</li>|</h\d>', '\n', t)
    t = html.unescape(re.sub(r'<[^>]+>', ' ', t))
    t = re.sub(r'[ \t]+', ' ', t)
    return clean(re.sub(r'\n\s*\n+', '\n\n', t).strip())


def slug(s):
    t = re.sub(r'[^a-z0-9]+', '-', s.lower()).strip('-')
    return (t or 'licence')[:60]


LICENCE_WORDS = re.compile(r'licen[cs]e|terms of use|free for (personal|commercial)|commercial use|you may (not )?(use|modify|sell|redistribute)|public domain|\bcc0\b|creative commons', re.I)


def kind_of(text, claimed):
    """licence when the text holds the licence the pack claims (or licence wording at all), else listing."""
    t = text.lower()
    c = claimed.lower()
    if c.startswith('cc0'):
        return 'licence' if re.search(r'\bcc0\b|public domain|creative commons zero', t) else 'listing'
    if c.startswith('cc-by'):
        return 'licence' if re.search(r'creative commons attribution|\bcc[- ]by\b', t) and 'legal code' in t else 'listing'
    return 'licence' if LICENCE_WORDS.search(t) else 'listing'


def conditions(text):
    t = text.lower()
    found = []
    if re.search(r'\bai\b[^.]{0,30}train|not for ai|machine.learning|dataset', t):
        found.append('no-ai-training')
    if re.search(r'resell|resale|redistribut', t):
        found.append('no-resale')
    if re.search(r'(?<!no )(?<!without )(attribution|credit)( is)? (required|needed)|must (give )?credit', t):
        found.append('credit-required')
    return found


def save_copy(folder, rel_root, text, frm, name, kind='licence'):
    """Writes a dated text copy under folder; returns the copy record (path relative to rel_root)."""
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, f'licence-{TODAY}-{slug(name)}.txt')
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    return {'file': os.path.relpath(path, rel_root), 'from': frm, 'saved': TODAY,
            'sha256': hashlib.sha256(text.encode('utf-8')).hexdigest(), 'kind': kind}


def fab_summary(text):
    """Fab's licence page as text, cut to the Standard License summary (the site's menus and footer dropped)."""
    start = text.find('Fab Standard License')
    if start < 0:
        return text
    ends = [i for i in (text.find(k, start) for k in ('Cookies Settings', 'Privacy Policy', 'Terms of Service', '\u00a9')) if i > 0]
    return text[start:min(ends)].strip() if ends else text[start:].strip()


def listing_description(raw):
    """An itch.io page's own description block, without the page's "More information" table."""
    m = re.search(r'<div class="formatted_description[^"]*"[^>]*>(.*?)</div>\s*</div>', raw, re.S)
    if not m:
        return ''
    text = to_text(m.group(1))
    cut = text.find('More information')
    return text[:cut].strip() if cut > 0 else text


def shipped_licences(pack_dir):
    """Licence and readme files near the top of a pack (three levels at most), as (relative path, text)."""
    found = []
    if not os.path.isdir(pack_dir):
        return found
    base = pack_dir.rstrip('/').count('/')
    for root, dirs, files in os.walk(pack_dir):
        if root.count('/') - base >= 3:
            dirs[:] = []
        dirs[:] = [d for d in dirs if d not in ('Content', 'Binaries', 'Intermediate', 'Saved', 'DerivedDataCache')] if root == pack_dir else dirs
        for f in files:
            p = os.path.join(root, f)
            if LICENCE_FILE.match(f) and not os.path.islink(p) and os.path.getsize(p) <= MAX_TEXT:
                try:
                    raw = open(p, 'rb').read(MAX_TEXT).decode('utf-8', 'replace')
                except OSError:
                    continue
                text = to_text(raw) if f.lower().endswith(('.htm', '.html')) else clean(raw)
                if text.strip():
                    found.append((os.path.relpath(p, pack_dir), text))
        if len(found) >= 6:
            break
    return found


def licence_parts(words, site):
    """pack.json's licence words ("Standard (Professional): any use", "CC0: …") as (name, tier)."""
    head = words.split(':')[0].strip()
    m = re.match(r'Standard \((\w+)\)', head)
    if m:
        return 'Fab Standard License', m.group(1)
    if head.lower() == 'unknown' or not head:
        return '', ''
    return head, ''


def itch_owned():
    """game id -> (bought, account) from the itch.io API; empty when there is no key."""
    try:
        import itch_sync
        me = itch_sync.api('/profile').get('user', {})
        account = 'itch:' + (me.get('username') or '')
        owned, page = {}, 1
        while True:
            d = itch_sync.api(f'/profile/owned-keys?page={page}')
            for k in d.get('owned_keys', []):
                gid = k['game']['id']
                before = owned.get(gid, (False, account, ''))
                purchase = str(k.get('purchase_id') or '') or before[2]
                owned[gid] = (before[0] or bool(k.get('purchase_id')), account, purchase)
            if len(d.get('owned_keys', [])) < d.get('per_page', 50):
                break
            page += 1
        return owned
    except Exception as e:
        print(f'itch.io API not used ({type(e).__name__})')
        return {}


def fill(dst, key, value):
    if value and not dst.get(key):
        dst[key] = value


def main():
    a = argparse.ArgumentParser()
    a.add_argument('--index', required=True)
    a.add_argument('--packs', required=True)
    a.add_argument('--only', default='')
    a.add_argument('--dry-run', action='store_true')
    a.add_argument('--no-network', action='store_true')
    a.add_argument('--redo', action='store_true', help='rebuild the automatic licence copies of these packs (hand-made ones stay)')
    o = a.parse_args()
    library = os.path.dirname(os.path.abspath(o.index.rstrip('/')))
    only = set(filter(None, o.only.split(',')))
    ids = sorted(d for d in os.listdir(o.index) if os.path.isfile(os.path.join(o.index, d, 'pack.json')) and (not only or d in only))
    owned = {} if o.no_network or not any(i.startswith('itch_') for i in ids) else itch_owned()
    fab_copy = None
    full_fab = sorted(f for f in os.listdir(os.path.join(library, 'licences')) if f.startswith('fab-eula-full')) if os.path.isdir(os.path.join(library, 'licences')) else []
    shared = {}  # purchase id -> games, to tell bundles
    for gid, (_, _, purchase) in owned.items():
        if purchase:
            shared.setdefault(purchase, []).append(gid)
    inbox = []
    inbox_dir = os.path.join(library, 'receipts', 'inbox')
    for f in sorted(os.listdir(inbox_dir)) if os.path.isdir(inbox_dir) else []:
        if f.endswith('.json') and os.path.getsize(os.path.join(inbox_dir, f)) < 65536:
            try:
                inbox.append((f, json.load(open(os.path.join(inbox_dir, f)))))
            except (OSError, ValueError):
                pass
    counts = {'packs': 0, 'source': 0, 'licence copy': 0, 'how known': 0, 'written': 0}
    for pid in ids:
        idx = os.path.join(o.index, pid)
        try:
            pack = json.load(open(os.path.join(idx, 'pack.json')))
        except (OSError, ValueError):
            continue
        counts['packs'] += 1
        prov_path = os.path.join(idx, 'provenance.json')
        try:
            prov = json.load(open(prov_path)) if os.path.exists(prov_path) else {}
        except (OSError, ValueError):
            print(f'  {pid}: provenance.json unreadable, left alone')
            continue
        prov.setdefault('version', 1)
        src, lic, acq = prov.setdefault('source', {}), prov.setdefault('licence', {}), prov.setdefault('acquired', {})
        for d, keys in ((src, ('site', 'url', 'creator')), (lic, ('name', 'tier', 'url')), (acq, ('how', 'date', 'account'))):
            for k in keys:
                d.setdefault(k, '')
        prov.setdefault('receipts', [])
        lic.setdefault('copies', [])
        lic.setdefault('credit', '')
        lic.setdefault('conditions', [])
        acq.setdefault('via', '')
        acq.setdefault('purchase', '')
        if o.redo and not o.dry_run:
            for c in [c for c in lic['copies'] if c.get('from', '').startswith(('pack:', 'https://'))]:
                # only a real file inside this pack's own provenance/ folder (the entry is data and could point anywhere)
                p = os.path.join(idx, c['file'])
                root = os.path.realpath(os.path.join(idx, 'provenance'))
                if os.path.isfile(p) and not os.path.islink(p) and os.path.realpath(p).startswith(root + os.sep):
                    os.remove(p)
                lic['copies'].remove(c)
            lic['conditions'] = []
        url = pack.get('url', '')
        site = 'itch.io' if pid.startswith('itch_') else 'Patreon' if pid.startswith('patreon_') else 'Fab' if 'fab.com' in url else (re.sub(r'^www\.', '', url.split('/')[2]) if url.startswith('https://') else '')
        fill(src, 'site', site)
        fill(src, 'url', url if url.startswith('https://') else '')
        fill(src, 'creator', pack.get('seller', ''))
        name, tier = licence_parts(pack.get('licence', ''), site)
        fill(lic, 'name', name)
        fill(lic, 'tier', tier)
        fill(acq, 'date', (pack.get('acquired') or '')[:10])
        if not acq.get('how'):
            acq['how'] = 'unknown'
        if site == 'itch.io':
            try:
                game = json.load(open(os.path.join(o.packs, pid, 'itch.json')))['game']
                gid = game['id'] if isinstance(game, dict) else game  # itch_sync stores the id itself
            except (OSError, ValueError, KeyError, TypeError):
                gid = None
            if gid in owned:
                if acq['how'] == 'unknown':
                    acq['how'] = 'bought' if owned[gid][0] else 'free download'
                fill(acq, 'account', owned[gid][1])
                fill(acq, 'purchase', owned[gid][2])
        if site == 'itch.io' and acq.get('purchase'):
            # a receipt in the inbox for this purchase (P-<id>): its bundle name, and a copy for the pack
            for f, side in inbox:
                if str(side.get('order', '')) == 'P-' + acq['purchase']:
                    items = side.get('items') or []
                    if len(shared.get(acq['purchase'], [])) > 1 and items:
                        fill(acq, 'via', str(items[0])[:120])
                    if not any(r.get('order') == side.get('order') for r in prov['receipts']) and not o.dry_run:
                        rdir = os.path.join(idx, 'provenance', 'receipts')
                        os.makedirs(rdir, exist_ok=True)
                        stem = f[:-5]
                        main_file = ''
                        for g in os.listdir(inbox_dir):
                            if os.path.splitext(g)[0] == stem and not os.path.islink(os.path.join(inbox_dir, g)):
                                shutil.copyfile(os.path.join(inbox_dir, g), os.path.join(rdir, g))
                                if not g.endswith('.json'):
                                    main_file = g
                        prov['receipts'].append({'order': str(side.get('order', '')), 'date': str(side.get('date') or ''),
                                                 'price': str(side.get('price') or ''), 'currency': str(side.get('currency') or ''),
                                                 'file': os.path.join('provenance', 'receipts', main_file or f)})
        claimed = pack.get('licence', '')
        if not o.dry_run and not any(c.get('from', '').startswith(('pack:', 'https://')) for c in lic['copies']):
            prov_dir = os.path.join(idx, 'provenance')
            texts = []
            for rel, text in shipped_licences(os.path.join(o.packs, pid)):
                kind = kind_of(text, claimed)
                lic['copies'].append(save_copy(prov_dir, idx, text, f'pack:{rel}', os.path.splitext(os.path.basename(rel))[0], kind))
                texts.append((kind, text))
            if site == 'itch.io' and not o.no_network and (raw := page_text(url)) and (text := listing_description(raw)):
                kind = kind_of(text, claimed)
                lic['copies'].append(save_copy(prov_dir, idx, text, url, 'itch-listing', kind))
                texts.append((kind, text))
            if site == 'Fab':
                if full_fab and name == 'Fab Standard License':
                    # the binding EULA, saved by hand once for the library
                    lic['copies'].append({'file': os.path.join('licences', full_fab[-1]), 'from': 'saved by hand', 'saved': full_fab[-1][-14:-4], 'sha256': '', 'kind': 'licence'})
                elif not o.no_network:
                    if fab_copy is None and (raw := page_text(FAB_LICENCE)) and (text := fab_summary(to_text(raw))):
                        fab_copy = save_copy(os.path.join(library, 'licences'), library, text, FAB_LICENCE, 'fab-standard-license-summary', 'summary')
                    if fab_copy:
                        lic['copies'].append(dict(fab_copy))
                        fill(lic, 'url', FAB_LICENCE)
            for kind, text in texts:
                if kind == 'licence':
                    lic['conditions'] = sorted(set(lic['conditions']) | set(conditions(text)))
            if not lic.get('name') and any(k == 'licence' for k, _ in texts):
                lic['name'] = f"{pack.get('seller', 'Creator')} terms (from the listing)"[:120]
        if claimed.lower().startswith('cc-by'):
            # attribution is the whole condition: the credit to give, from the pack's own data
            fill(lic, 'credit', f"{pack.get('title', pid)} by {pack.get('seller', '')}, {url}".strip(', '))
            if 'credit-required' not in lic['conditions']:
                lic['conditions'].append('credit-required')
        counts['source'] += bool(src.get('url'))
        counts['licence copy'] += any(c.get('kind', 'licence') == 'licence' for c in lic['copies'])
        counts['how known'] += acq.get('how', 'unknown') != 'unknown'
        if not o.dry_run:
            tmp = prov_path + '.part'
            with open(tmp, 'w', encoding='utf-8') as f:
                json.dump(prov, f, indent=2)
            os.replace(tmp, prov_path)
            counts['written'] += 1
    print('ASSETSHELF_PROVENANCE', json.dumps(counts))


if __name__ == '__main__':
    main()
