#!/usr/bin/env python3
"""Index a pack's raw model files (FBX, glTF, OBJ, .blend) with headless Blender.

    index_raw.py --pack <pack>/raw --out <dir>

Archives in <pack>/raw/<format>/ are unpacked beside themselves (<name>.zip -> <name>/), once. Each model file
becomes one asset in <out>/index.json (same layout as the Unreal index), with a preview in <out>/thumbs/.
"""
import argparse, hashlib, json, os, subprocess, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = ('.fbx', '.glb', '.gltf', '.obj', '.blend')
IMAGES = ('.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff', '.exr', '.webp')


def unpack(raw):
    for d, _, files in os.walk(raw):
        for f in files:
            if f.lower().endswith('.zip'):
                dest = os.path.join(d, f[:-4])
                if not os.path.isdir(dest):
                    with zipfile.ZipFile(os.path.join(d, f)) as z:  # extractall drops absolute and '..' parts
                        z.extractall(dest + '.part')
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
    json.dump({'files': jobs, 'images': images, 'size': o.size, 'out': res}, open(job, 'w'))
    subprocess.run([o.blender, '-b', '--factory-startup', '--disable-autoexec', '-P',
                    os.path.join(HERE, 'blender_raw.py'), '--', job], check=True, stdout=subprocess.DEVNULL)
    assets = []
    for j, r in zip(jobs, json.load(open(res))):
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
        assets = materials(raw, out, images)
    json.dump({'source': 'raw', 'assets': assets}, open(os.path.join(out, 'index.json'), 'w'), indent=1)
    os.remove(job); os.remove(res)
    print(f'{len(assets)} raw assets, {sum("thumb" in a for a in assets)} pictures ->', out)


if __name__ == '__main__':
    main()


def _check():
    got = one_format(['a/Tree.fbx', 'b/Tree.obj', 'c/Tree.gltf', 'a/Rock.obj', 'x/Bush.FBX', 'y/Bush.fbx'])
    assert got == ['a/Rock.obj', 'a/Tree.fbx', 'x/Bush.FBX', 'y/Bush.fbx'], got
