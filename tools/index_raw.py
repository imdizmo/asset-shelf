#!/usr/bin/env python3
"""Index a pack's raw model files (FBX, glTF, OBJ, .blend) with headless Blender.

    index_raw.py --pack <pack>/raw --out <dir>

Archives in <pack>/raw/<format>/ are unpacked beside themselves (<name>.zip -> <name>/), once. Each model file
becomes one asset in <out>/index.json (same layout as the Unreal index), with a preview in <out>/thumbs/.
A folder of 2D pictures that belongs to no model (an interface kit, icons, sprites) is one asset too, pictured by a
contact sheet (needs ImageMagick; .7z, .rar and split zips need 7z).
"""
import argparse, hashlib, json, math, os, re, shutil, subprocess, sys, tempfile, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = ('.fbx', '.glb', '.gltf', '.obj', '.blend')
IMAGES = ('.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff', '.exr', '.webp')


def unpack(raw):
    """.zip with Python; .7z, .rar and split zips (.zip + .z01…) with 7z when it is installed. An archive that cannot
    be read is reported and left alone. Symbolic links an archive brought along are removed."""
    for d, _, files in os.walk(raw):
        for f in files:
            src = os.path.join(d, f)
            dest, ext = os.path.splitext(src)
            if ext.lower() not in ('.zip', '.7z', '.rar') or os.path.isdir(dest):
                continue
            try:
                if ext.lower() == '.zip' and not os.path.exists(dest + '.z01'):
                    with zipfile.ZipFile(src) as z:  # extractall drops absolute and '..' parts
                        z.extractall(dest + '.part')
                elif shutil.which('7z'):
                    subprocess.run(['7z', 'x', '-y', '-bd', '-o' + dest + '.part', src], check=True, stdout=subprocess.DEVNULL)
                else:
                    print('not unpacked (7z is not installed):', f)
                    continue
            except Exception as e:
                print(f'not unpacked: {f} ({type(e).__name__})')
                shutil.rmtree(dest + '.part', ignore_errors=True)
                continue
            for dd, ds, fs in os.walk(dest + '.part'):
                for n in ds + fs:
                    if os.path.islink(os.path.join(dd, n)):
                        os.remove(os.path.join(dd, n))
            os.rename(dest + '.part', dest)


def one_format(models):
    """Packs often ship each model in several formats (FBX, OBJ, glTF): keep one per model name, the best one."""
    rank = {e: i for i, e in enumerate(('.fbx', '.blend', '.glb', '.gltf', '.obj'))}
    groups = {}
    for p in models:
        groups.setdefault(os.path.splitext(os.path.basename(p))[0].lower(), []).append(p)
    keep = []
    for ps in groups.values():
        best = min(rank[os.path.splitext(p)[1].lower()] for p in ps)
        keep += [p for p in ps if rank[os.path.splitext(p)[1].lower()] == best]
    return sorted(keep)


def materials(raw, out, images):
    """A pack of texture maps and no model (a material from ambientCG or Poly Haven): each folder of maps is one
    material asset, pictured by the pack's cover when it has one."""
    by_dir = {}
    for p in images:
        by_dir.setdefault(os.path.dirname(p), []).append(p)
    cover = next((os.path.join(out, c) for c in ('cover.png', 'cover.jpg') if os.path.exists(os.path.join(out, c))), None)
    found = []
    for d, maps in sorted(by_dir.items()):
        rel = os.path.relpath(d, raw)
        a = {'path': rel, 'package': rel, 'name': os.path.basename(os.path.dirname(raw)) if rel in ('.', 'textures', 'material') else os.path.basename(d),
             'class': 'Material', 'source': 'raw', 'format': 'maps', 'tags': {'Maps': str(len(maps))}, 'deps': [], 'soft': [], 'refs': []}
        if cover:
            name = hashlib.md5(rel.encode()).hexdigest() + os.path.splitext(cover)[1]
            os.makedirs(os.path.join(out, 'thumbs'), exist_ok=True)
            with open(cover, 'rb') as src, open(os.path.join(out, 'thumbs', name), 'wb') as dst:
                dst.write(src.read())
            a['thumb'] = 'thumbs/' + name
        found.append(a)
    return found


MAPS = re.compile(r'normal|_nor|rough|metal|displace|height|_ao|occlusion|base_?colou?r|albedo|diffuse|_arm', re.I)


def is_material(maps):
    """A folder of texture maps (a few files, at least two named like maps) rather than a folder of 2D art."""
    return len(maps) <= 24 and sum(bool(MAPS.search(os.path.basename(p))) for p in maps) >= 2


def sheets(raw, out, images, models, size):
    """2D art (interface kits, icons, sprites, backgrounds): each folder of pictures is one asset, pictured by a contact
    sheet of up to 16 of them. A folder beside or above a model is that model's textures and is left out."""
    by_dir, near_model = {}, set()
    for p in images:
        by_dir.setdefault(os.path.dirname(p), []).append(p)
    for m in models:
        d = os.path.dirname(m)
        while len(d) >= len(raw):
            near_model.add(d)
            d = os.path.dirname(d)
    magick = shutil.which('magick')  # ponytail: ImageMagick makes the sheets; without it the assets have no picture
    found = []
    for d, pics in sorted(by_dir.items()):
        if d in near_model or os.path.dirname(d) in near_model or (not models and is_material(pics)):
            continue
        rel = os.path.relpath(d, raw)
        parts = rel.split(os.sep)
        a = {'path': rel, 'package': rel, 'name': ' / '.join(parts[-2:]) if len(parts) > 2 else parts[-1], 'class': 'Texture2D',
             'source': 'raw', 'format': 'pictures', 'tags': {'Pictures': str(len(pics))}, 'deps': [], 'soft': [], 'refs': []}
        thumb = os.path.join(out, 'thumbs', hashlib.md5(rel.encode()).hexdigest() + '.png')
        if magick and sheet(magick, pics[::max(1, len(pics) // 16)][:16], thumb, size):
            a['thumb'] = 'thumbs/' + os.path.basename(thumb)
        found.append(a)
    return found


def sheet(magick, pics, thumb, size):
    """One picture at a time is shrunk to a cell (a 10,000-pixel background must not sit in memory 16 times over),
    then the cells are laid out in a square grid."""
    n = max(1, math.ceil(math.sqrt(len(pics))))
    cell = size // n
    limits = ['-limit', 'memory', '1GiB', '-limit', 'map', '1GiB']
    with tempfile.TemporaryDirectory() as tmp:
        cells = []
        for i, p in enumerate(pics):
            c = os.path.join(tmp, f'{i:02d}.png')
            r = subprocess.run([magick, *limits, p + '[0]', '-thumbnail', f'{cell}x{cell}', '-background', 'none', '-gravity', 'center',
                                '-extent', f'{cell}x{cell}', c], capture_output=True, timeout=120)
            if r.returncode == 0 and os.path.exists(c):
                cells.append(c)
        if not cells:
            return False
        r = subprocess.run([magick, 'montage', *cells, '-tile', f'{n}x{n}', '-geometry', '+0+0', '-background', '#777777', thumb], capture_output=True, timeout=120)
        return r.returncode == 0 and os.path.exists(thumb)


def main():
    a = argparse.ArgumentParser()
    a.add_argument('--pack', required=True, help='the pack\'s raw folder')
    a.add_argument('--out', required=True)
    a.add_argument('--blender', default='blender')
    a.add_argument('--size', type=int, default=256)
    o = a.parse_args()
    raw, out = os.path.abspath(o.pack), os.path.abspath(o.out)
    unpack(raw)
    os.makedirs(os.path.join(out, 'thumbs'), exist_ok=True)
    models, images = [], []
    for d, _, files in os.walk(raw):
        for f in sorted(files):
            p = os.path.join(d, f); ext = os.path.splitext(f)[1].lower()
            if ext in MODELS and not f.endswith('.blend1'):
                models.append(p)
            elif ext in IMAGES:
                images.append(p)
    models = one_format(models)
    jobs = []
    for p in models:
        rel = os.path.relpath(p, raw)
        name = hashlib.md5(rel.encode()).hexdigest() + '.png'
        jobs.append({'file': rel, 'abs': p, 'thumb': os.path.join(out, 'thumbs', name)})
    job = os.path.join(out, 'blender_job.json')
    res = os.path.join(out, 'blender_result.json')
    results = []
    for i in range(0, len(jobs), 200):  # a fresh Blender every 200 models: its memory grows over a long pack
        json.dump({'files': jobs[i:i + 200], 'images': images, 'size': o.size, 'out': res}, open(job, 'w'))
        subprocess.run([o.blender, '-b', '--factory-startup', '--disable-autoexec', '-P',
                        os.path.join(HERE, 'blender_raw.py'), '--', job], check=True, stdout=subprocess.DEVNULL)
        results += json.load(open(res))
    assets = []
    for j, r in zip(jobs, results):
        fmt = os.path.splitext(j['file'])[1][1:].lower()
        a = {'path': j['file'], 'package': j['file'], 'name': os.path.splitext(os.path.basename(j['file']))[0],
             'class': 'SkeletalMesh' if r.get('rigged') else 'StaticMesh', 'source': 'raw', 'format': fmt,
             'tags': {}, 'deps': [], 'soft': [], 'refs': []}
        if 'error' in r:
            a['error'] = r['error']
        else:
            a['tags'] = {'Triangles': str(r['triangles']), 'ApproxSize': 'x'.join(map(str, r['size'])),
                         'Meshes': str(r['meshes']), 'Animations': str(r['animations'])}
            if os.path.exists(j['thumb']):
                a['thumb'] = 'thumbs/' + os.path.basename(j['thumb'])
        assets.append(a)
    # a material's own .blend (ambientCG ships one) holds no mesh: it is not a model, and its folder is a material
    assets = [a for a in assets if 'no meshes' not in a.get('error', '')]
    if not assets:
        by_dir = {}
        for p in images:
            by_dir.setdefault(os.path.dirname(p), []).append(p)
        assets = materials(raw, out, [p for ps in by_dir.values() if is_material(ps) for p in ps])
    assets += sheets(raw, out, images, models, o.size)
    json.dump({'source': 'raw', 'assets': assets}, open(os.path.join(out, 'index.json'), 'w'), indent=1)
    for f in (job, res):
        if os.path.exists(f):
            os.remove(f)
    print(f'{len(assets)} raw assets, {sum("thumb" in a for a in assets)} pictures ->', out)


if __name__ == '__main__':
    main()


def _check():
    got = one_format(['a/Tree.fbx', 'b/Tree.obj', 'c/Tree.gltf', 'a/Rock.obj', 'x/Bush.FBX', 'y/Bush.fbx'])
    assert got == ['a/Rock.obj', 'a/Tree.fbx', 'x/Bush.FBX', 'y/Bush.fbx'], got
