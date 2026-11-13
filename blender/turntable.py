"""Renders a turntable of an asset for Asset Shelf's details panel, headless:

    blender -b --factory-startup -P turntable.py -- job.json

job.json: {"name", "files": [...], "textures": dir or null, "unreal": bool, "out": dir, "frames": 24, "size": 384}
Imports like the Blender add-on does (same materials), frames the model, renders <out>/NN.png around it with EEVEE
and writes <out>/.done.
"""
import json
import math
import os
import sys

import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asset_shelf_bridge as bridge  # noqa: E402


def main():
    job = json.load(open(sys.argv[sys.argv.index('--') + 1]))
    out, frames, size = job['out'], int(job.get('frames', 24)), int(job.get('size', 384))
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_homefile(use_empty=True)
    for f in job['files']:
        bridge.import_file(f)
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    bridge.link_textures(meshes, job.get('textures'), unreal=bool(job.get('unreal')))
    sc = bpy.context.scene
    if not meshes:
        raise SystemExit('nothing to show')
    # bounds of everything, in world space (world matrices are only valid after an update)
    bpy.context.view_layer.update()
    meshes = [o for o in meshes if o.visible_get() and not o.hide_render]
    pts = [o.matrix_world @ mathutils.Vector(c) for o in meshes for c in o.bound_box]
    lo = mathutils.Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = mathutils.Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    centre, radius = (lo + hi) / 2, max((hi - lo).length / 2, 1e-3)
    pivot = bpy.data.objects.new('pivot', None)
    sc.collection.objects.link(pivot)
    pivot.location = centre
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    cam.data.lens = 50
    cam.parent = pivot
    dist = radius / math.tan(math.radians(39.6) / 2) * 0.9  # 50 mm lens: 39.6 degrees across
    cam.location = (0, -dist * math.cos(math.radians(20)), dist * math.sin(math.radians(20)))
    cam.rotation_euler = (math.radians(70), 0, 0)
    cam.data.clip_end = dist * 10
    cam.data.clip_start = dist / 1000
    sc.camera = cam
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN'))
    sun.data.energy = 4
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
    sc.collection.objects.link(sun)
    world = bpy.data.worlds.new('w')
    sc.world = world
    world.use_nodes = True
    # a grey studio: ambient from the world, one sun for shape
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.22, 0.22, 0.23, 1)
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 1.0
    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = 'PNG'
    for i in range(frames):
        pivot.rotation_euler = (0, 0, 2 * math.pi * i / frames)
        sc.render.filepath = os.path.join(out, f'{i:02d}.png')
        bpy.ops.render.render(write_still=True)
    open(os.path.join(out, '.done'), 'w').write(str(frames))


main()
