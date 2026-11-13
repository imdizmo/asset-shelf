#!/usr/bin/env python3
"""Indexes every pack of a library that has no index yet, one at a time.

    index_library.py --packs <packs root> --index <index root> [--ready-file NAME] [--limit N]

A pack with unreal/Content is indexed in Unreal (index_pack.py); a pack with only raw/ files in Blender
(index_raw.py). A pack whose Unreal index fails with pictures is retried without them, so its assets still show.
--ready-file: only index packs whose folder holds that file (a downloader writes it last). Progress goes to stdout;
state is the index folder itself (a pack is done when <index>/<pack>/index.json exists), so it can be stopped and
started again at any time.
"""
import argparse, os, shutil, signal, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
SHELF_INDEX = os.path.join(HERE, 'ShelfIndex', 'bin', 'Release', 'net10.0', 'ShelfIndex.dll')  # dotnet build -c Release tools/ShelfIndex
DDC_LIMIT = 30e9


def dir_size(p):
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(p) for f in fs if os.path.isfile(os.path.join(d, f)))


def run(cmd, log, timeout):
    """Runs cmd in its own process group, so a timeout also stops the editor or Blender it started."""
    p = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        return p.wait(timeout=timeout) == 0
    except subprocess.TimeoutExpired:
        os.killpg(p.pid, signal.SIGTERM)
        try:
            p.wait(timeout=60)
        except subprocess.TimeoutExpired:
            os.killpg(p.pid, signal.SIGKILL)
            p.wait()
        log.write(f'\nTIMEOUT after {timeout}s\n')
        return False


def main():
    a = argparse.ArgumentParser()
    a.add_argument('--packs', required=True)
    a.add_argument('--index', required=True)
    a.add_argument('--host', default=os.path.expanduser('~/.cache/asset-shelf/host'))
    a.add_argument('--ready-file')
    a.add_argument('--order', help='a text file of pack folder names, indexed first in that order')
    a.add_argument('--limit', type=int, default=0)
    a.add_argument('--timeout', type=int, default=4 * 3600)
    a.add_argument('--embedded', action='store_true', help='with --unreal: only the pictures saved in the asset files')
    a.add_argument('--unreal', action='store_true',
                   help='index Unreal packs with Unreal itself (rendered pictures) instead of ShelfIndex, which needs no Unreal')
    a.add_argument('--redo', help='a text file of pack folder names to index again even though they have an index')
    a.add_argument('--kind', choices=['all', 'unreal', 'raw'], default='all',
                   help='only this kind of pack, so an Unreal run and a raw run can go side by side')
    o = a.parse_args()
    names = sorted(os.listdir(o.packs))
    if o.order and os.path.exists(o.order):
        first = [l.strip() for l in open(o.order) if l.strip() in names]
        names = first + [n for n in names if n not in set(first)]
    redo = {l.strip() for l in open(o.redo)} if o.redo else set()
    ddc = os.path.join(os.path.dirname(os.path.abspath(o.host)), 'ddc')
    done = failed = 0
    for n in names:
        src, out = os.path.join(o.packs, n), os.path.join(o.index, n)
        if os.path.exists(os.path.join(out, 'index.json')) and n not in redo:
            continue
        if o.ready_file and os.path.isdir(os.path.join(src, 'unreal')) and not os.path.exists(os.path.join(src, o.ready_file)):
            continue  # still downloading
        ue = os.path.isdir(os.path.join(src, 'unreal', 'Content'))
        raw = os.path.isdir(os.path.join(src, 'raw'))
        if not ue and not raw or o.kind == 'unreal' and not ue or o.kind == 'raw' and ue:
            continue
        t0 = time.time()
        stale = os.path.join(out, 'safety.json')
        if os.path.exists(stale):
            os.remove(stale)  # whatever happens below, the previous verdict must not survive as current
        log = os.path.join(os.path.dirname(os.path.abspath(o.host)), 'logs', n + '.log')
        os.makedirs(os.path.dirname(log), exist_ok=True)
        print(time.strftime('%H:%M:%S'), 'START', n, 'unreal' if ue else 'raw', flush=True)
        with open(log, 'w') as f:
            if ue and not o.unreal:
                ok = run(['dotnet', SHELF_INDEX, '--pack', os.path.join(src, 'unreal'), '--out', out], f, o.timeout)
            elif ue:
                cmd = [sys.executable, '-I', os.path.join(HERE, 'index_pack.py'), '--host', o.host, '--pack', os.path.join(src, 'unreal'), '--out', out]
                if o.embedded:
                    cmd.append('--embedded')
                ok = run(cmd, f, o.timeout)
                if not ok:
                    print('   failed with pictures, retrying without', flush=True)
                    ok = run(cmd + ['--no-pictures'], f, o.timeout)
            else:
                ok = run([sys.executable, '-I', os.path.join(HERE, 'index_raw.py'), '--pack', os.path.join(src, 'raw'), '--out', out], f, o.timeout)
        ok = ok and os.path.exists(os.path.join(out, 'index.json'))
        # anything in the pack that can run code, whether or not indexing worked: cheap (names and small text files)
        with open(log, 'a') as f:
            if not run([sys.executable, '-I', os.path.join(HERE, 'safety_scan.py'), src, out], f, 600):
                print('   safety scan failed: the pack shows as not checked', flush=True)
        done += ok
        failed += not ok
        print(time.strftime('%H:%M:%S'), 'DONE ' if ok else 'FAIL ', n, f'{time.time() - t0:.0f}s', flush=True)
        if os.path.isdir(ddc) and dir_size(ddc) > DDC_LIMIT:
            shutil.rmtree(ddc)
            print('   cleared the derived-data cache', flush=True)
        if o.limit and done + failed >= o.limit:
            break
    print('INDEX LIBRARY DONE', done, 'indexed,', failed, 'failed', flush=True)


if __name__ == '__main__':
    main()
