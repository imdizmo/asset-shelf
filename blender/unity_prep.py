"""Works out an asset's materials for Unity, headless:

    blender -b --factory-startup -P unity_prep.py -- job.json

job.json: {"files": [...], "textures": dir or null, "unreal": bool, "out": dir, "pipeline": "urp"|"hdrp"|"builtin"}
Imports like the Blender add-on (same texture matching), then per material writes what Unity needs into
<out>/unity_materials.json: {"materials": [{"name", "base", "normal", "mask", "emission", "alpha_clip"}]}.
Unity's Lit shaders take metallic and smoothness packed in one texture (URP/Built-in: metal in R, smoothness in A;
HDRP mask map: metal R, occlusion G, detail B, smoothness A), so those are composed here from the roughness,
metallic or ORM-style maps and saved as <out>/<material>_mask.png.
"""
import json
import os
import re
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asset_shelf_bridge as bridge  # noqa: E402


def source(socket):
    """The image file feeding a BSDF input, and which channel of it (through Normal Map / Separate Color nodes)."""
    if not socket.links:
        return None, None
    node, channel = socket.links[0].from_node, None
    if node.type == 'SEPARATE_COLOR':
        channel = {'Red': 0, 'Green': 1, 'Blue': 2}.get(socket.links[0].from_socket.name)
        node = node.inputs[0].links[0].from_node if node.inputs[0].links else None
    elif node.type == 'NORMAL_MAP':
        node = node.inputs['Color'].links[0].from_node if node.inputs['Color'].links else None
    if node is not None and node.type == 'TEX_IMAGE' and node.image and not bridge.missing(node.image):
        return bpy.path.abspath(node.image.filepath), channel
    return None, None


def pixels(path):
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = 'Non-Color'
    a = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4), img.size


def channel_of(path, ch, size):
    a, s = pixels(path)
    if tuple(s) != tuple(size):
        img = bpy.data.images.load(path, check_existing=False)
        img.scale(size[0], size[1])
        b = np.empty(size[0] * size[1] * 4, dtype=np.float32)
        img.pixels.foreach_get(b)
        a = b.reshape(size[1], size[0], 4)
    return a[..., ch if ch is not None else 0]


def mask(name, rough, metal, out, pipeline):
    """metal in R, smoothness (1 - roughness) in A; HDRP: occlusion 1 in G."""
    first = rough[0] or metal[0]
    _, size = pixels(first)
    r = channel_of(rough[0], rough[1], size) if rough[0] else np.full((size[1], size[0]), 0.5, np.float32)
    m = channel_of(metal[0], metal[1], size) if metal[0] else np.zeros((size[1], size[0]), np.float32)
    px = np.zeros((size[1], size[0], 4), np.float32)
    px[..., 0] = m
    px[..., 1] = 1.0 if pipeline == 'hdrp' else 0.0
    px[..., 3] = 1.0 - r
    img = bpy.data.images.new(name, size[0], size[1], alpha=True, float_buffer=False)
    img.colorspace_settings.name = 'Non-Color'
    img.pixels.foreach_set(px.ravel())
    path = os.path.join(out, re.sub(r'[^A-Za-z0-9_.-]', '_', name) + '_mask.png')
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    return path


def main():
    job = json.load(open(sys.argv[sys.argv.index('--') + 1]))
    out = job['out']
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_homefile(use_empty=True)
    for f in job['files']:
        bridge.import_file(f)
    objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    bridge.link_textures(objs, job.get('textures'), unreal=bool(job.get('unreal')))
    mats = []
    for m in sorted({s.material for o in objs for s in o.material_slots if s.material}, key=lambda m: m.name):
        b = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None) if m.node_tree else None
        entry = {'name': m.name, 'base': None, 'normal': None, 'mask': None, 'emission': None, 'alpha_clip': False}
        if b is not None:
            entry['base'] = source(b.inputs['Base Color'])[0]
            entry['normal'] = source(b.inputs['Normal'])[0]
            entry['emission'] = source(b.inputs['Emission Color'])[0]
            entry['alpha_clip'] = bool(b.inputs['Alpha'].links)
            rough, metal = source(b.inputs['Roughness']), source(b.inputs['Metallic'])
            if rough[0] or metal[0]:
                try:
                    entry['mask'] = mask(m.name, rough, metal, out, job.get('pipeline', 'urp'))
                except (RuntimeError, ValueError) as e:  # an image Blender cannot read: no mask, the rest still goes
                    print('asset shelf: no mask for', m.name, e)
        elif m.node_tree:
            # an older shader (Diffuse BSDF and the like): the image feeding its colour is the base colour
            for n in m.node_tree.nodes:
                if n.type.startswith('BSDF') and 'Color' in n.inputs:
                    entry['base'] = source(n.inputs['Color'])[0]
                    if entry['base']:
                        break
        mats.append(entry)
    json.dump({'materials': mats}, open(os.path.join(out, 'unity_materials.json'), 'w'), indent=1)
    print('asset shelf: unity materials', [(x['name'], {k: bool(v) for k, v in x.items() if k != 'name'}) for x in mats])


main()
