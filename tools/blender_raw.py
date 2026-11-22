"""Run inside Blender (blender -b --factory-startup --disable-autoexec -P blender_raw.py -- job.json).
For each model file in the job: import it into an empty scene, wire a loose base-colour texture if the model has
none, read its size and triangle count, render a preview. Writes the job's `out` json."""
import bpy, json, math, os, re, sys
from mathutils import Vector

job = json.load(open(sys.argv[sys.argv.index('--') + 1]))
SIZE = job.get('size', 256)
BASE = re.compile(r'(base_?colou?r|albedo|diffuse|_d\b|_col\b)', re.I)


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path):
    ext = os.path.splitext(path)[1].lower()
    if ext == '.fbx':
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in ('.glb', '.gltf'):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == '.obj':
        bpy.ops.wm.obj_import(filepath=path)
    elif ext == '.blend':
        # ponytail: a sculpt file's multires data filled 14 GB on load; such packs carry an export beside it. Read per object if this hides real models.
        if os.path.getsize(path) > 200 << 20:
            raise ValueError('.blend over 200 MB, not opened')
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.objects = src.objects
        for o in dst.objects:
            if o is not None:
                bpy.context.scene.collection.objects.link(o)
    else:
        raise ValueError(ext)


def wire_texture(meshes, images):
    """Many store downloads ship the textures beside the model, not linked to it."""
    by_name = {os.path.basename(p).lower(): p for p in images}
    for im in bpy.data.images:  # relink textures whose stored path is from the author's computer
        if im.source == 'FILE' and not im.packed_file and not os.path.exists(bpy.path.abspath(im.filepath)):
            hit = by_name.get(os.path.basename(im.filepath.replace('\\', '/')).lower())
            if hit:
                im.filepath = hit; im.reload()
            else:
                im.user_clear(); im.filepath = ''
    base = [p for p in images if BASE.search(os.path.basename(p))]
    if not base:
        return False
    img = bpy.data.images.load(base[0], check_existing=True)
    for o in meshes:
        for slot in o.material_slots or []:
            m = slot.material
            if not m or not m.node_tree or any(n.type == 'TEX_IMAGE' and n.image and (n.image.packed_file or os.path.exists(bpy.path.abspath(n.image.filepath))) for n in m.node_tree.nodes):
                continue
            bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if bsdf:
                t = m.node_tree.nodes.new('ShaderNodeTexImage'); t.image = img
                m.node_tree.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
        if not o.material_slots:
            m = bpy.data.materials.new('shelf_base'); m.use_nodes = True
            bsdf = m.node_tree.nodes['Principled BSDF']
            t = m.node_tree.nodes.new('ShaderNodeTexImage'); t.image = img
            m.node_tree.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
            o.data.materials.append(m)
    return True


def render(meshes, out):
    sc = bpy.context.scene
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    ctr, rad = (lo + hi) / 2, max((hi - lo).length / 2, 1e-4)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam)
    cam.data.angle = math.radians(30)
    d = rad / math.sin(cam.data.angle / 2) * 1.05
    direction = Vector((1, -1.25, 0.8)).normalized()
    cam.location = ctr + direction * d
    cam.rotation_euler = (-direction).to_track_quat('-Z', 'Y').to_euler()
    cam.data.clip_start, cam.data.clip_end = d / 100, d * 4
    sc.camera = cam
    sc.render.engine = 'BLENDER_WORKBENCH'
    sh = sc.display.shading
    sh.light, sh.color_type, sh.show_shadows, sh.show_cavity = 'STUDIO', 'TEXTURE', True, True
    sc.render.resolution_x = sc.render.resolution_y = SIZE
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new('w')
    sc.render.image_settings.file_format = 'PNG'
    sc.render.filepath = out
    bpy.ops.render.render(write_still=True)
    return [round((hi - lo)[i] * 100) for i in range(3)]  # cm, like Unreal's ApproxSize


results = []
for item in job['files']:
    clear()
    r = {'file': item['file']}
    try:
        load(item['abs'])
        meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
        if not meshes:
            raise ValueError('no meshes')
        r['textured'] = wire_texture(meshes, job.get('images', []))
        for m in bpy.data.materials:  # Workbench shows each material's active image node
            bsdf = m.node_tree and next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            link = bsdf and bsdf.inputs['Base Color'].links
            if link and link[0].from_node.type == 'TEX_IMAGE':
                m.node_tree.nodes.active = link[0].from_node
        # ponytail: subdivision capped at 1 so a sculpt file with multires cannot fill memory; the triangle count is of that capped mesh
        sc = bpy.context.scene
        sc.render.use_simplify, sc.render.simplify_subdivision, sc.render.simplify_subdivision_render = True, 1, 1
        dg = bpy.context.evaluated_depsgraph_get()
        r['triangles'] = sum(sum(len(p.vertices) - 2 for p in o.evaluated_get(dg).data.polygons) for o in meshes)
        r['meshes'] = len(meshes)
        r['rigged'] = any(o.type == 'ARMATURE' for o in bpy.context.scene.objects)
        r['animations'] = len(bpy.data.actions)
        r['size'] = render(meshes, item['thumb'])
    except Exception as e:  # one bad file must not stop the pack
        r['error'] = f'{type(e).__name__}: {e}'
    results.append(r)
    json.dump(results, open(job['out'], 'w'), indent=1)  # after every file: if Blender dies, the caller sees which one
print('SHELF RAW DONE', len(results))
