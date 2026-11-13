#!/usr/bin/env python3
"""Looks through a pack for anything that can run code once it is in a project, and writes <index>/safety.json.

    safety_scan.py <pack folder> <index folder> [<pack folder> <index folder> ...]

Levels: "danger" (runs by itself: startup Python, compiled binaries, executables), "review" (runs once built or
enabled: C++ sources, plugins, project modules, loose scripts), "ok". Only names and small text files are read
(.ini, .uproject, .uplugin, .py up to 1 MB); archives are listed, not unpacked.
"""
import json, os, re, sys, zipfile

EXEC = ('.exe', '.dll', '.so', '.dylib', '.bat', '.cmd', '.ps1', '.vbs', '.sh', '.jar', '.msi', '.com', '.scr', '.app')
SCRIPT = ('.py', '.pyc', '.js', '.lua', '.rb', '.pl')
# any array-op prefix Unreal accepts (+ - . !), a byte-order mark, spaces
STARTUP_INI = re.compile(r'^\ufeff?\s*[^A-Za-z0-9\s]*\s*(StartupScripts|ExecCmds)\s*=', re.I | re.M)
DANGEROUS_PY = re.compile(r'\b(subprocess|os\.system|os\.popen|urllib|requests|socket|ctypes|eval\(|exec\(|base64)\b')


def ini_texts(b):
    """Every way Unreal could read an .ini: by its byte-order mark (UTF-32/UTF-16/UTF-8), else UTF-8, plus lenient
    UTF-8 and UTF-16 (both orders) with NULs dropped. Returns (texts, clean): clean is False when the strict decode
    failed or left NUL bytes. Line endings are normalised so a bare CR ends a line, as it does for Unreal."""
    strict = None
    for bom, enc in ((b'\xff\xfe\x00\x00', 'utf-32-le'), (b'\x00\x00\xfe\xff', 'utf-32-be'), (b'\xff\xfe', 'utf-16-le'),
                     (b'\xfe\xff', 'utf-16-be'), (b'\xef\xbb\xbf', 'utf-8')):
        if b.startswith(bom):
            body, codec = b[len(bom):], enc
            break
    else:
        body, codec = b, 'utf-8'
    try:
        strict = body.decode(codec)
    except UnicodeDecodeError:
        pass
    clean = strict is not None and '\x00' not in strict
    texts = [strict] if strict is not None else []
    texts += [b.decode(c, errors='replace') for c in ('utf-8', 'utf-16-le', 'utf-16-be')]
    return [t.replace('\x00', '').replace('\r\n', '\n').replace('\r', '\n') for t in texts], clean


def modules(path):
    """The code modules a .uplugin/.uproject declares; raises on anything that is not the expected JSON shape."""
    obj = json.load(open(path))
    if not isinstance(obj, dict):
        raise ValueError('not a JSON object')
    mods = obj.get('Modules') or []
    if not isinstance(mods, list):
        raise ValueError('Modules is not a list')
    return [m.get('Name', '?') if isinstance(m, dict) else '?' for m in mods]


def scan(pack):
    findings = []

    def add(level, rule, path, detail=''):
        findings.append({'level': level, 'rule': rule, 'path': os.path.relpath(path, pack), 'detail': detail})

    def inspect(p, f):
        low = f.lower()
        ext = os.path.splitext(low)[1]
        if ext in EXEC:
            add('danger', 'Executable or library', p)
        elif low == 'init_unreal.py':
            add('danger', 'init_unreal.py: Unreal runs it every time the editor starts', p)
        elif ext in SCRIPT:
            level, detail = 'review', ''
            if ext == '.py' and os.path.getsize(p) <= 1 << 20:
                hits = sorted(set(DANGEROUS_PY.findall(open(p, errors='replace').read())))
                if hits:
                    level, detail = 'danger', 'uses ' + ', '.join(hits)
            add(level, 'Script', p, detail)
        elif ext == '.ini':
            if os.path.getsize(p) > 1 << 20:
                add('review', 'Config file too large to inspect', p)
            else:
                # every reading counts and the worst one wins: a decode problem never lowers the level
                texts, clean = ini_texts(open(p, 'rb').read())
                low = [t.lower() for t in texts]
                if any(STARTUP_INI.search(t) for t in texts) or any('startupscripts' in t or 'execcmds' in t for t in low):
                    add('danger', 'Config runs commands or Python when the editor starts (StartupScripts/ExecCmds)', p)
                elif any('pythonscriptplugin' in t for t in low):
                    add('review', 'Config mentions Python start-up settings', p)
                if not clean:
                    add('review', 'Config file that does not read as clean text (encoding or NUL bytes)', p)
        elif ext == '.uplugin':
            mods = modules(p)
            if mods:
                add('review', 'Plugin with code modules', p, ', '.join(mods))
        elif ext == '.uproject':
            mods = modules(p)
            if mods:
                add('review', 'Project with C++ modules', p, ', '.join(mods))
        elif ext == '.zip':
            names = zipfile.ZipFile(p).namelist()
            bad = [n for n in names if os.path.splitext(n.lower())[1] in EXEC or n.lower().endswith('init_unreal.py')]
            if bad:
                add('danger', 'Archive holds executables or startup scripts', p, ', '.join(bad[:5]))
            elif any(os.path.splitext(n.lower())[1] in SCRIPT for n in names):
                add('review', 'Archive holds scripts', p)

    for d, dirs, files in os.walk(pack):
        rel = os.path.relpath(d, pack).replace('\\', '/')
        if rel.endswith('/Source') or rel == 'unreal/Source':
            add('review', 'C++ source code: compiled into the project when it is built', d)
        if rel.endswith('/Binaries'):
            add('danger', 'Compiled binaries: native code that loads with the plugin or project', d)
        for name in dirs:
            if os.path.islink(os.path.join(d, name)):
                add('review', 'Symbolic link (not followed)', os.path.join(d, name))
        for f in files:
            p = os.path.join(d, f)
            if os.path.islink(p) or not os.path.isfile(p):
                add('review', 'Symbolic link or special file (not inspected)', p)
                continue
            try:
                inspect(p, f)
            except Exception as e:  # fail closed: what cannot be read is listed for review, never passed as clean
                add('review', 'Could not inspect', p, f'{type(e).__name__}: {e}'[:200])
    level = 'danger' if any(f['level'] == 'danger' for f in findings) else 'review' if findings else 'ok'
    return {'level': level, 'findings': findings}


def main():
    args = sys.argv[1:]
    if len(args) < 2 or len(args) % 2:
        sys.exit(__doc__)
    for pack, index in zip(args[::2], args[1::2]):
        r = scan(pack)
        os.makedirs(index, exist_ok=True)
        with open(os.path.join(index, 'safety.json.part'), 'w') as f:
            json.dump(r, f, indent=1)
        os.replace(os.path.join(index, 'safety.json.part'), os.path.join(index, 'safety.json'))
        print(f"{os.path.basename(pack.rstrip('/'))}: {r['level']}, {len(r['findings'])} findings")
        for x in r['findings'][:8]:
            print(f"  [{x['level']}] {x['rule']}: {x['path']} {x['detail']}")


if __name__ == '__main__':
    main()
