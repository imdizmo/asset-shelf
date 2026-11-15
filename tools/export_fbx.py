#!/usr/bin/env python3
"""Export one Unreal asset to FBX, with the textures it uses as PNG, headless and lean.

    export_fbx.py --job job.json

job.json: {"pack": "<pack>/unreal", "asset": "/Game/X/SM_Y.SM_Y", "class": "StaticMesh", "out": "<dir>",
           "packages": ["/Game/X/SM_Y", ...the hard dependencies...], "textures": ["/Game/T/T_A.T_A", ...]}

Only the asset's own packages are copied to local disk, into a small project of its own (export-host: engine plugins
off but Python), so Unreal scans a handful of files instead of a whole pack on the NAS. Meshes, textures and the rest go
through the command-line editor (no renderer, no GPU); skeletal meshes and animations need a renderer and use the
editor off-screen. A finished export is kept (out/.done) and reused; the export cache is trimmed to CACHE_MAX bytes,
oldest first.
"""
import argparse, json, os, re, shutil, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from index_pack import capped, editor, host_lock  # noqa: E402

CACHE_MAX = 5 << 30
NEEDS_RENDERER = ('SkeletalMesh', 'AnimSequence', 'AnimMontage', 'AnimComposite')
EXTS = ('.uasset', '.umap', '.uexp', '.ubulk', '.uptnl')

UE_SCRIPT = '''import unreal, os
path, out, textures, cls = {path!r}, {out!r}, {textures!r}, {cls!r}
os.makedirs(out, exist_ok=True)
def export(obj, filename, options=None):
    t = unreal.AssetExportTask()
    t.object, t.filename, t.automated, t.prompt, t.replace_identical = obj, filename, True, False, True
    if options is not None:
        t.options = options
    return unreal.Exporter.run_asset_export_task(t)
asset = unreal.load_asset(path)
ok, n = False, 0
if asset is None:
    unreal.log_error('ASSETSHELF no asset ' + path)
else:
    opts = unreal.FbxExportOption()
    opts.collision, opts.level_of_detail, opts.vertex_color = False, False, True
    ok = export(asset, os.path.join(out, path.split('.')[-1] + '.fbx'), opts)
    for t in textures:
        tex = unreal.load_asset(t)
        # PNG first; a texture whose source is JPEG or 16-bit has no PNG exporter, so try the others Unreal has
        for ext in ('png', 'tga', 'jpg', 'exr', 'bmp'):
            if tex is not None and export(tex, os.path.join(out, t.split('.')[-1] + '.' + ext)):
                n += 1
                break
    if ok:
        open(os.path.join(out, '.done'), 'w').write(cls)
unreal.log_warning('ASSETSHELF EXPORT %s fbx=%s textures=%d' % (path, ok, n))
if {quit!r}:
    unreal.SystemLibrary.quit_editor()
'''


def make_export_host(host):
    """A bare blueprint project: engine plugins off by default (they are most of the start-up time), Python on."""
    up = os.path.join(host, 'AssetShelfExport.uproject')
    want = {'FileVersion': 3, 'EngineAssociation': '', 'Category': '', 'Description': 'Asset Shelf export host',
            'DisableEnginePluginsByDefault': True,
            'Plugins': [{'Name': n, 'Enabled': True} for n in ('PythonScriptPlugin', 'Niagara', 'Paper2D')]}
    os.makedirs(os.path.join(host, 'Content'), exist_ok=True)
    if not os.path.exists(up) or json.load(open(up)) != want:
        json.dump(want, open(up, 'w'), indent=1)
    return up


def stage(host, pack, packages):
    """Copies the asset's packages from the pack (on the NAS) into the host's Content, fresh each time."""
    content = os.path.join(host, 'Content')
    assert os.path.basename(os.path.dirname(content)) == 'export-host', content  # never clear anything else
    shutil.rmtree(content)
    os.makedirs(content)
    src_root = os.path.join(pack, 'Content')
    copied = 0
    for pkg in packages:
        if not pkg.startswith('/Game/') or '..' in pkg.split('/'):
            continue
        rel = pkg[len('/Game/'):]
        for ext in EXTS:
            src = os.path.join(src_root, rel + ext)
            if os.path.exists(src):
                dst = os.path.join(content, rel + ext)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copyfile(src, dst)
                copied += 1
    return copied


def trim_cache(root, keep):
    """Drops the oldest exports until the cache is under CACHE_MAX."""
    dirs = []
    for pack in os.listdir(root):
        if not os.path.isdir(os.path.join(root, pack)):
            continue
        for name in os.listdir(os.path.join(root, pack)):
            d = os.path.join(root, pack, name)
            if not os.path.isdir(d):
                continue
            size = sum(os.path.getsize(os.path.join(a, f)) for a, _, fs in os.walk(d) for f in fs)
            dirs.append((os.path.getmtime(d), size, d))
    total = sum(s for _, s, _ in dirs)
    for _, size, d in sorted(dirs):
        if total <= CACHE_MAX:
            break
        if os.path.abspath(d) != os.path.abspath(keep) and os.path.dirname(os.path.dirname(d)) == root:
            shutil.rmtree(d)
            total -= size


def default_engine():
    """$UNREAL_ENGINE, else ~/UnrealEngine (a source build), else the newest launcher install (Epic Games/UE_5.x)."""
    if os.environ.get('UNREAL_ENGINE'):
        return os.environ['UNREAL_ENGINE']
    home = os.path.expanduser('~/UnrealEngine')
    if os.path.isdir(home):
        return home
    roots = [os.path.join(os.environ.get(v, ''), 'Epic Games') for v in ('ProgramFiles', 'ProgramW6432')] 
    found = [os.path.join(r, d) for r in roots if os.path.isdir(r) for d in os.listdir(r) if re.fullmatch(r'UE_5\.\d+', d)]
    return max(found, key=lambda p: int(p.rsplit('.', 1)[1]), default=home)


def main():
    a = argparse.ArgumentParser()
    a.add_argument('--engine', default=default_engine())
    a.add_argument('--host', default=os.path.expanduser('~/.cache/asset-shelf/export-host'))
    a.add_argument('--job', required=True)
    o = a.parse_args()
    job = json.load(open(o.job))
    out = os.path.abspath(job['out'])
    if os.path.exists(os.path.join(out, '.done')):
        os.utime(out)
        print('ASSETSHELF EXPORT reused', out)
        return
    if not re.fullmatch(r'/Game/[^\s"\'=+]+', job['asset']):
        sys.exit('refusing asset path ' + job['asset'])
    t0 = time.time()
    with host_lock(o.host):
        up = make_export_host(o.host)
        n = stage(o.host, os.path.abspath(job['pack']), job['packages'])
        renderer = job.get('class') in NEEDS_RENDERER
        script = os.path.join(o.host, 'Saved', 'asset_shelf_export.py')
        os.makedirs(os.path.dirname(script), exist_ok=True)
        open(script, 'w').write(UE_SCRIPT.format(path=job['asset'], out=out, textures=job.get('textures', []),
                                                 cls=job.get('class', ''), quit=renderer))
        if renderer:  # exporting a skeletal mesh asserts without a renderer
            cmd = [editor(o.engine), up, '-RenderOffscreen', f'-ExecCmds=py {script}']
        else:
            cmd = [editor(o.engine, cmd=True), up, '-run=pythonscript', f'-script={script}', '-nullrhi']
        cmd += ['-unattended', '-nosplash', '-nosound', '-notraceserver', '-stdout']
        env = dict(os.environ, **{'UE-LocalDataCachePath': os.path.join(os.path.dirname(os.path.abspath(o.host)), 'ddc')})
        log = os.path.join(o.host, 'Saved', 'asset_shelf_export.log')
        with open(log, 'w') as f:  # a file, not a pipe: the editor's forked trace server can keep a pipe open
            subprocess.run(capped('8G', cmd), stdout=f, stderr=subprocess.STDOUT, env=env)
    text = open(log, errors='replace').read()
    lines = [l for l in text.splitlines() if 'ASSETSHELF' in l]
    print(f'{n} files staged, {time.time() - t0:.0f}s, {"editor" if renderer else "command line"}')
    print('\n'.join(lines) or text[-2000:])
    ok = os.path.exists(os.path.join(out, '.done'))
    if ok:
        trim_cache(os.path.dirname(os.path.dirname(out)), out)
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
