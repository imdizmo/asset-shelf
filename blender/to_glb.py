"""Turns an asset into one self-contained .glb (textures embedded) for Groundwork, headless:

    blender -b --factory-startup -P to_glb.py -- job.json

job.json: {"files": [...], "textures": dir or null, "unreal": bool, "out": "<path>.glb"}
Imports like the Blender add-on does (same materials), puts the origin at the base (the bounds' bottom centre, where
the model meets the ground) and writes glTF's metres with Y up. Only meshes and the armatures they hang on go out.
"""
import json
import os
import sys

import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asset_shelf_bridge as bridge  # noqa: E402


def main():
    job = json.load(open(sys.argv[sys.argv.index('--') + 1]))
    bpy.ops.wm.read_homefile(use_empty=True)
    for f in job['files']:
        bridge.import_file(f)
    sc = bpy.context.scene
    meshes = [o for o in sc.objects if o.type == 'MESH']
    bridge.link_textures(meshes, job.get('textures'), unreal=bool(job.get('unreal')))
    bpy.context.view_layer.update()
    meshes = [o for o in meshes if o.visible_get()]
    roots = [os.path.dirname(os.path.abspath(f)) for f in job['files']] + [job.get('textures')]
    dropped = bridge.drop_outside_images(roots)
    if dropped:
        print(f'left out {dropped} images outside the asset\'s folders', flush=True)
    if not meshes:
        raise SystemExit('no mesh to send')
    pts = [o.matrix_world @ mathutils.Vector(c) for o in meshes for c in o.bound_box]
    lo = mathutils.Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = mathutils.Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    # origin at the base: shift every top-level object so the bounds' bottom centre lands on 0,0,0 (Blender is Z up)
    shift = mathutils.Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    for o in sc.objects:
        if o.parent is None:
            o.location -= shift
    keep = set(meshes) | {o.parent for o in meshes if o.parent and o.parent.type == 'ARMATURE'}
    for o in sc.objects:
        o.select_set(o in keep)
    # ponytail: textures capped at 2048 px (a pack's 4K maps made a 1 m prop 41 MB); raise MAX if a map needs more
    MAX = 2048
    for img in bpy.data.images:
        w, h = img.size
        if max(w, h) > MAX:
            img.scale(max(1, w * MAX // max(w, h)), max(1, h * MAX // max(w, h)))
    out = job['out']
    bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', use_selection=True, export_apply=True,
                              export_yup=True, export_animations=False, export_cameras=False, export_lights=False)
    if not os.path.isfile(out):
        raise SystemExit('the glTF export wrote nothing')
    size = hi - lo
    print('ASSETSHELF_GLB', json.dumps({'size': [size.x, size.y, size.z]}), flush=True)


main()
