#!/usr/bin/env python3
"""Index one Unreal asset pack with the AssetShelf commandlet.

    index_pack.py --engine ~/UnrealEngine --host ~/.cache/asset-shelf/host --pack <pack>/unreal --out <dir>

The pack folder is the one holding `Content/` (an asset pack or a whole sample project). Its top-level content
folders are linked into a small host project, so assets keep their /Game/<Folder>/... paths, then the commandlet
writes <out>/index.json and <out>/thumbs/. Run it under a memory cap; it starts an editor with rendering.
"""
import argparse, contextlib, fcntl, json, os, re, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN = os.path.join(HERE, '..', 'unreal', 'AssetShelf')
PLUGINS_ON = ['PythonScriptPlugin', 'Niagara', 'PCG', 'Water', 'ChaosCloth', 'ControlRig', 'GeometryScripting']

RUN = '''import unreal
state = {{'ticks': 0}}
def tick(dt, state=state):
    state['ticks'] += 1
    if state['ticks'] == 60:
        # always quit: a broken blueprint in the pack can surface here as a Python error after the index is written
        try:
            ok = unreal.AssetShelfLibrary.index_paths({roots!r}, {out!r}, {size})
            unreal.log_warning('ASSETSHELF RESULT %s' % ok)
        finally:
            unreal.SystemLibrary.quit_editor()
unreal.register_slate_post_tick_callback(tick)
'''


def editor(engine, cmd=False):
    """Unreal's editor binary for this system (Linux, Windows, macOS)."""
    name = 'UnrealEditor-Cmd' if cmd else 'UnrealEditor'
    if sys.platform.startswith('win'):
        return os.path.join(engine, 'Engine', 'Binaries', 'Win64', name + '.exe')
    if sys.platform == 'darwin':
        return os.path.join(engine, 'Engine', 'Binaries', 'Mac', name)
    return os.path.join(engine, 'Engine', 'Binaries', 'Linux', name)


def capped(memory, cmd):
    """The command under a memory cap where systemd can do it, as it is elsewhere."""
    if sys.platform.startswith('linux') and shutil.which('systemd-run'):
        return ['systemd-run', '--user', '--scope', '-q', '-p', f'MemoryMax={memory}', '-p', 'MemorySwapMax=0', *cmd]
    return cmd


@contextlib.contextmanager
def host_lock(host):
    """One editor job on the host project at a time (indexing and FBX exports share it)."""
    os.makedirs(host, exist_ok=True)
    with open(os.path.join(host, '.lock'), 'w') as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        yield


def make_host(host):
    os.makedirs(os.path.join(host, 'Plugins'), exist_ok=True)
    os.makedirs(os.path.join(host, 'Content'), exist_ok=True)
    os.makedirs(os.path.join(host, 'Config'), exist_ok=True)
    up = os.path.join(host, 'AssetShelfHost.uproject')
    if not os.path.exists(up):
        json.dump({'FileVersion': 3, 'EngineAssociation': '', 'Category': '', 'Description': 'Asset Shelf indexing host',
                   'Plugins': [{'Name': p, 'Enabled': True} for p in PLUGINS_ON + ['AssetShelf']]}, open(up, 'w'), indent=1)
    # a copy, not a link: the build accelerator fails on a plugin reached through a symlink
    dst = os.path.join(host, 'Plugins', 'AssetShelf')
    if os.path.islink(dst):
        os.unlink(dst)
    subprocess.run(['rsync', '-a', '--exclude', 'Intermediate', '--exclude', 'Binaries', PLUGIN + '/', dst + '/'], check=True)
    ini = os.path.join(host, 'Config', 'DefaultEngine.ini')
    if not os.path.exists(ini):
        open(ini, 'w').write('[/Script/Engine.RendererSettings]\nr.Nanite.ProjectEnabled=True\n')
    return up


def mount(host, pack):
    content = os.path.join(host, 'Content')
    for n in os.listdir(content):  # only links are ever put here
        p = os.path.join(content, n)
        if os.path.isdir(p) and not os.path.islink(p) and not any(f for _, _, f in os.walk(p)):
            shutil.rmtree(p)  # empty folders the editor makes (Developers, Collections)
            continue
        assert os.path.islink(p), f'{p} is not a link; refusing to clear the host Content folder'
        os.unlink(p)
    roots = []
    src = os.path.join(pack, 'Content')
    for n in sorted(os.listdir(src)):
        os.symlink(os.path.join(src, n), os.path.join(content, n))
        if os.path.isdir(os.path.join(src, n)):
            roots.append('/Game/' + n)
    return roots


def main():
    a = argparse.ArgumentParser()
    a.add_argument('--engine', default=os.path.expanduser('~/UnrealEngine'))
    a.add_argument('--host', default=os.path.expanduser('~/.cache/asset-shelf/host'))
    a.add_argument('--pack', required=True)
    a.add_argument('--out', required=True)
    a.add_argument('--size', type=int, default=256)
    a.add_argument('--no-pictures', action='store_true')
    a.add_argument('--embedded', action='store_true',
                   help='only the pictures saved inside the asset files: no rendering, no GPU, the command-line editor')
    o = a.parse_args()
    with host_lock(o.host):
        run(o)


def run(o):
    up = make_host(o.host)
    roots = mount(o.host, os.path.abspath(o.pack))
    if not roots:
        sys.exit('no content folders in ' + o.pack)
    out = os.path.abspath(o.out)
    if os.path.isdir(os.path.join(out, 'thumbs')) and not o.embedded:  # embedded keeps earlier renders as a fallback
        shutil.rmtree(os.path.join(out, 'thumbs'))
    # The full editor, rendering off-screen: in a commandlet the pictures come out black. The script waits for
    # the editor to tick a while before indexing, then quits.
    env = dict(os.environ, **{'UE-LocalDataCachePath': os.path.join(os.path.dirname(os.path.abspath(o.host)), 'ddc')})
    # Unreal re-joins and re-parses its command line: a content folder with a space, quote, '=' or '+' in its name
    # could inject a parameter, so such packs are refused
    if any(re.search(r'[\s"\'=+]', r) for r in roots):
        sys.exit('refusing content folder names with spaces, quotes, = or +: ' + ', '.join(roots))
    if o.embedded and not o.no_pictures:
        exe = editor(o.engine, cmd=True)
        cmd = [exe, up, '-run=AssetShelfIndex', '-Roots=' + '+'.join(roots), '-Out=' + out, '-Embedded', '-nullrhi',
               '-notraceserver', '-unattended', '-nosplash', '-nosound', '-stdout', '-FullStdOutLogOutput']
        r = subprocess.run(cmd, env=env)
        ok = os.path.exists(os.path.join(out, 'index.json'))
        print('index', 'written' if ok else 'MISSING', out, 'exit', r.returncode)
        if not ok:
            sys.exit(1)
        return
    script = os.path.join(o.host, 'Saved', 'asset_shelf_run.py')
    os.makedirs(os.path.dirname(script), exist_ok=True)
    open(script, 'w').write(RUN.format(roots=roots, out=out, size=0 if o.no_pictures else o.size))
    exe = editor(o.engine)
    cmd = [exe, up, '-RenderOffscreen', '-notraceserver', '-unattended', '-nosplash', '-nosound', '-stdout', '-FullStdOutLogOutput',
           f'-ExecCmds=py {script}']  # not -ExecutePythonScript: that quits as soon as the script returns
    # its own derived-data cache, so indexing never fills the one the user's own projects share
    env = dict(os.environ, **{'UE-LocalDataCachePath': os.path.join(os.path.dirname(os.path.abspath(o.host)), 'ddc')})
    r = subprocess.run(cmd, env=env)
    ok = os.path.exists(os.path.join(out, 'index.json'))
    print('index', 'written' if ok else 'MISSING', out, 'exit', r.returncode)
    if not ok:
        sys.exit(1)


if __name__ == '__main__':
    main()
