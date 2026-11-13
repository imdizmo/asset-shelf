#!/usr/bin/env python3
"""Checks a downloaded Fab pack against its Epic manifest: every file there, the right size, the right SHA1.

    verify_pack.py <pack folder> [<pack folder> ...] [--quick]

The pack folder holds unreal.manifest (the Epic chunk manifest it was downloaded from) and unreal/ (the files).
--quick compares sizes only (no file is read). Needs legendary-gl (pip install legendary-gl) to read manifests.
Exit status 1 when any pack has a missing, wrong-size or wrong-hash file.
"""
import hashlib, json, os, sys

from legendary.models.json_manifest import JSONManifest
from legendary.models.manifest import Manifest


def load(path):
    b = open(path, 'rb').read()
    if b.lstrip()[:1] != b'{':
        return Manifest.read_all(b)
    j = json.loads(b)
    # some old JSON manifests lack these lists; they only matter for downloading, not for checking files
    j.setdefault('ChunkShaList', {g: '0' * 40 for g in j.get('ChunkHashList', {})})
    j.setdefault('ChunkFilesizeList', {g: '000' * 8 for g in j.get('ChunkHashList', {})})
    return JSONManifest.read_all(json.dumps(j).encode())


def sha1(path):
    h = hashlib.sha1()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.digest()


def verify(pack, quick):
    m = load(os.path.join(pack, 'unreal.manifest'))
    root = os.path.join(pack, 'unreal')
    missing, size, hashes, ok = [], [], [], 0
    for fm in m.file_manifest_list.elements:
        p = os.path.join(root, fm.filename)
        if not os.path.realpath(p).startswith(os.path.realpath(root) + os.sep):
            missing.append(fm.filename + ' (outside the pack)')
        elif not os.path.exists(p):
            missing.append(fm.filename)
        elif os.path.getsize(p) != fm.file_size:
            size.append(fm.filename)
        elif not quick and fm.sha_hash and any(fm.sha_hash) and sha1(p) != fm.sha_hash:
            hashes.append(fm.filename)
        else:
            ok += 1
    return ok, missing, size, hashes


def main():
    quick = '--quick' in sys.argv
    bad = False
    for pack in [a for a in sys.argv[1:] if not a.startswith('--')]:
        ok, missing, size, hashes = verify(pack, quick)
        name = os.path.basename(pack.rstrip('/'))
        print(f'{name}: {ok} ok, {len(missing)} missing, {len(size)} wrong size' + ('' if quick else f', {len(hashes)} wrong hash'))
        for label, files in (('missing', missing), ('wrong size', size), ('wrong hash', hashes)):
            for f in files[:10]:
                print(f'  {label}: {f}')
        bad |= bool(missing or size or hashes)
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
