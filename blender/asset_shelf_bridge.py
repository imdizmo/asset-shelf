"""Asset Shelf bridge: imports what the Asset Shelf app sends to Blender.

The app writes a job (a small JSON file: the model files and a folder of textures) into the outbox folder
(ASSET_SHELF_OUTBOX, or ~/.cache/asset-shelf/outbox). This add-on looks there every second, imports each job's
files into the open scene in a collection named after the asset, links loose textures it can match, and moves the
job to outbox/done/.
"""
import json
import os
import difflib
import re

import bpy

bl_info = {
    "name": "Asset Shelf Bridge",
    "author": "Asset Shelf contributors",
    "version": (0, 1, 0),
    "blender": (4, 2, 0),
    "location": "Runs in the background",
    "description": "Imports assets sent from the Asset Shelf app",
    "category": "Import-Export",
}

IMAGES = ('.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff', '.exr', '.webp')
# texture roles by file name (Unreal's T_X_D/_N/_R/_M/_ORM habits and the usual long names)
ROLES = [
    ('orm', re.compile(r'(_orm|_arm|occlusion_?roughness_?metal)', re.I)),
    ('mra', re.compile(r'(_mra|metal_?rough_?(ao|occlusion))', re.I)),
    ('rma', re.compile(r'(_rma\b|rough_?metal_?(ao|occlusion))', re.I)),
    ('base', re.compile(r'(base_?colou?r|albedo|diffu|_d\b|_bc\b|_col\b|_basecolor)', re.I)),
    ('normal', re.compile(r'(normal|_n\b|_nrm\b|_nor\b)', re.I)),
    ('rough', re.compile(r'(rough|_r\b)', re.I)),
    ('metal', re.compile(r'(metal|_m\b)', re.I)),
    ('emit', re.compile(r'(emissi|emission|_e\b)', re.I)),
    ('opacity', re.compile(r'(opacity|_alpha\b|_o\b)', re.I)),
]
# which channel of a packed map holds what
PACKED = {'orm': {'G': 'Roughness', 'B': 'Metallic'}, 'mra': {'R': 'Metallic', 'G': 'Roughness'},
          'rma': {'R': 'Roughness', 'G': 'Metallic'}}
WORD = re.compile(r'[a-z]{3,}')
NOISE = {'mat', 'material', 'inst', 'instance', 'tex', 'texture', 'master'}


def outbox():
    return os.environ.get('ASSET_SHELF_OUTBOX') or os.path.expanduser('~/.cache/asset-shelf/outbox')


def import_file(path):
    ext = os.path.splitext(path)[1].lower()
    if ext == '.fbx':
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in ('.glb', '.gltf'):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == '.obj':
        bpy.ops.wm.obj_import(filepath=path)
    elif ext == '.blend':
        # a pack's .blend may carry drivers or scripts: no auto-run while it comes in (preferences not saved)
        prefs = bpy.context.preferences.filepaths
        was = prefs.use_scripts_auto_execute
        prefs.use_scripts_auto_execute = False
        before = all_ids()
        try:
            with bpy.data.libraries.load(path, link=False) as (src, dst):
                dst.objects = src.objects
            # Python-expression drivers would still run later if auto-run is on in this Blender: drop every one that
            # came in, on any new data block, its node tree and its shape keys
            for idb in all_ids() - before:
                for holder in filter(None, (idb, getattr(idb, 'node_tree', None), getattr(idb, 'shape_keys', None))):
                    ad = getattr(holder, 'animation_data', None)
                    for fc in list(ad.drivers) if ad else []:
                        if fc.driver.type == 'SCRIPTED':
                            ad.drivers.remove(fc)
        finally:
            prefs.use_scripts_auto_execute = was
        for o in dst.objects:
            if o is not None:
                bpy.context.scene.collection.objects.link(o)
                o.select_set(True)
    else:
        raise ValueError(f'cannot import {ext}')


def all_ids():
    """Every data block in the file, across all of bpy.data's collections."""
    ids = set()
    for name in dir(bpy.data):
        coll = getattr(bpy.data, name, None)
        if isinstance(coll, bpy.types.bpy_prop_collection) and name not in ('window_managers', 'screens', 'workspaces'):
            try:
                ids.update(x for x in coll if isinstance(x, bpy.types.ID))
            except TypeError:
                pass
    return ids


def missing(img):
    return img is None or (img.source == 'FILE' and not img.packed_file and not os.path.exists(bpy.path.abspath(img.filepath)))


def role_of(path):
    name = os.path.splitext(os.path.basename(path))[0]
    return next((r for r, rx in ROLES if rx.search(name)), None)


def words(s):
    return set(WORD.findall(s.lower())) - NOISE


def wire(m, files, by_role, single, unreal):
    """Plugs the textures that belong to material m into its Principled BSDF: base colour, normal (DirectX for
    Unreal), roughness, metallic, packed ORM/MRA/RMA, emission, opacity. Inputs that already work are left alone."""
    nt = m.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return []
    mw = words(m.name)

    def pick(role):
        cands = by_role.get(role, [])
        scored = sorted(((len(mw & words(os.path.basename(p))), p) for p in cands), key=lambda x: -x[0])
        if scored and (scored[0][0] > 0 or single):
            return scored[0][1]
        return None

    def works(name):
        links = bsdf.inputs[name].links
        if not links:
            return False
        src = links[0].from_node
        while src.type in ('NORMAL_MAP', 'SEPARATE_COLOR'):
            feed = src.inputs['Color'] if src.type == 'NORMAL_MAP' else src.inputs[0]
            if not feed.links:
                return False  # an empty Normal Map / Separate Color node (the FBX importer leaves those) is not a texture
            src = feed.links[0].from_node
        return not (src.type == 'TEX_IMAGE' and missing(src.image))

    def image(path, color=True, x=-700, y=0):
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = bpy.data.images.load(path, check_existing=True)
        if not color:
            t.image.colorspace_settings.name = 'Non-Color'
        t.location = (bsdf.location.x + x, bsdf.location.y + y)
        return t

    done = []
    if not works('Base Color') and (p := pick('base')):
        t = image(p, y=300)
        nt.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
        done.append('base')
        if any(k in m.name.lower() for k in ('leaf', 'leaves', 'foliage', 'grass', 'plant', 'hair', 'fur', 'fern', 'branch')) and not bsdf.inputs['Alpha'].links:
            nt.links.new(t.outputs['Alpha'], bsdf.inputs['Alpha'])  # cut-out foliage keeps its mask in the alpha
            done.append('alpha')
    if not works('Normal') and (p := pick('normal')):
        t = image(p, color=False, x=-900, y=-400)
        nm = nt.nodes.new('ShaderNodeNormalMap')
        nm.location = (bsdf.location.x - 300, bsdf.location.y - 400)
        if unreal:
            nm.convention = 'DIRECTX'  # Unreal's normal maps have green flipped against Blender's
        nt.links.new(t.outputs['Color'], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
        done.append('normal')
    packed = next(((r, p) for r in PACKED if (p := pick(r))), None)
    if packed and not (works('Roughness') and works('Metallic')):
        role, p = packed
        t = image(p, color=False, x=-900, y=0)
        sep = nt.nodes.new('ShaderNodeSeparateColor')
        sep.location = (bsdf.location.x - 400, bsdf.location.y)
        nt.links.new(t.outputs['Color'], sep.inputs['Color'])
        for ch, inp in PACKED[role].items():
            nt.links.new(sep.outputs[{'R': 'Red', 'G': 'Green', 'B': 'Blue'}[ch]], bsdf.inputs[inp])
        done.append(role)
    else:
        for role, inp, y in (('rough', 'Roughness', -100), ('metal', 'Metallic', 100)):
            if not works(inp) and (p := pick(role)):
                t = image(p, color=False, y=y)
                nt.links.new(t.outputs['Color'], bsdf.inputs[inp])
                done.append(role)
    if not works('Emission Color') and (p := pick('emit')):
        t = image(p, y=-700)
        nt.links.new(t.outputs['Color'], bsdf.inputs['Emission Color'])
        bsdf.inputs['Emission Strength'].default_value = 1.0
        done.append('emit')
    if not bsdf.inputs['Alpha'].links and (p := pick('opacity')):
        t = image(p, color=False, y=-900)
        nt.links.new(t.outputs['Color'], bsdf.inputs['Alpha'])
        done.append('opacity')
    return done


def link_textures(objects, tex_dir, unreal=False):
    """Relinks textures whose stored path is from the author's computer, then wires the rest by name: each material
    gets the textures whose names share its words (any, when there is only one material)."""
    if not tex_dir or not os.path.isdir(tex_dir):
        return {}
    files = [os.path.join(d, f) for d, _, fs in os.walk(tex_dir) for f in fs if f.lower().endswith(IMAGES)]
    by_name = {os.path.basename(p).lower(): p for p in files}
    by_role = {}
    for p in files:
        if (r := role_of(p)):
            by_role.setdefault(r, []).append(p)
    mats = {s.material for o in objects for s in o.material_slots if s.material and s.material.node_tree}
    report = {}
    for m in mats:
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image and missing(n.image):
                want = os.path.basename(n.image.filepath.replace('\\', '/')).lower()
                hit = by_name.get(want)
                if not hit and want:
                    # ponytail: packs often ship the texture under a slightly different name (typos, a suffix):
                    # the closest name of the same role, if close enough
                    same = [k for k in by_name if role_of(k) == role_of(want)] or list(by_name)
                    close = difflib.get_close_matches(want, same, n=1, cutoff=0.6)
                    hit = by_name[close[0]] if close else None
                if hit:
                    n.image.filepath = hit
                    n.image.reload()
            if unreal and n.type == 'NORMAL_MAP':
                n.convention = 'DIRECTX'  # the FBX importer wires Unreal's normal maps as OpenGL
        report[m.name] = wire(m, files, by_role, single=len(mats) == 1, unreal=unreal)
    return report


def run_job(path):
    job = json.load(open(path))
    name = job.get('name') or 'Asset'
    before = set(bpy.data.objects)
    bpy.ops.object.select_all(action='DESELECT')
    for f in job['files']:
        import_file(f)
    new = [o for o in bpy.data.objects if o not in before]
    coll = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(coll)
    for o in new:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        coll.objects.link(o)
        o.select_set(True)
    print('asset shelf: materials', link_textures(new, job.get('textures'), unreal=bool(job.get('unreal'))))
    if new:
        bpy.context.view_layer.objects.active = new[0]
    return name, len(new)


def poll():
    box = outbox()
    if os.path.isdir(box):
        for f in sorted(os.listdir(box)):
            if not f.endswith('.json'):
                continue
            path = os.path.join(box, f)
            done = os.path.join(box, 'done')
            os.makedirs(done, exist_ok=True)
            try:
                name, n = run_job(path)
                print(f'Asset Shelf: imported {name} ({n} objects)')
            except Exception as e:  # a bad job must not stop the bridge
                print(f'Asset Shelf: {f} failed: {e}')
            os.replace(path, os.path.join(done, f))
    return 1.0


def register():
    if not bpy.app.timers.is_registered(poll):
        bpy.app.timers.register(poll, first_interval=1.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(poll):
        bpy.app.timers.unregister(poll)


if __name__ == '__main__':
    register()
