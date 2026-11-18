"""Mesh prep, headless: looks a model over, optionally repairs and lightens it, makes what an engine wants, and exports
it for one target, with a report of everything found and done. The source files are only read; everything is written
into the job's out folder.

    blender -b --factory-startup --disable-autoexec -P mesh_prep.py -- job.json

job.json: {"files": [...], "textures": dir or null, "unreal": bool, "name": str, "out": dir,
           "fix": bool, "maxTris": int or null, "lods": 0-4, "collision": bool,
           "target": "unreal" | "godot" | "groundwork" | "blender"}
The report goes to <out>/<name>.report.json and to stdout as one line ASSETSHELF_PREP {...}.
Axes and units, in the words of Groundwork's docs/unreal.md: Blender works in metres with Z up; glTF is metres with
Y up; Unreal is centimetres with Z up.
"""
import json
import os
import sys

import bmesh
import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asset_shelf_bridge as bridge  # noqa: E402

HOLE_MAX_SIDES = 64          # holes up to this many edges are closed; bigger openings are left (a cup, a window)
FRAGMENT_SHARE = 0.02        # a separate part smaller than 2% of the whole model's size counts as a stray fragment


def look(objs):
    """What is wrong or worth knowing, over all meshes: triangles, holes (open loops), open and non-manifold edges,
    faces pointing the wrong way, separate parts and stray fragments, loose vertices, size in metres."""
    pts = [o.matrix_world @ mathutils.Vector(c) for o in objs for c in o.bound_box]
    lo = mathutils.Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))) if pts else mathutils.Vector()
    hi = mathutils.Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))) if pts else mathutils.Vector()
    diag = (hi - lo).length or 1.0
    r = dict(objects=len(objs), triangles=0, holes=0, openEdges=0, nonManifoldEdges=0, flippedFaces=0, parts=0,
             fragments=0, looseVertices=0, size=[round(v, 4) for v in (hi - lo)])
    for o in objs:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        r['triangles'] += sum(max(0, len(f.verts) - 2) for f in bm.faces)
        # files split vertices along every seam (glTF always does): judge the shape by where the vertices are
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=diag * 1e-6)
        open_edges = [e for e in bm.edges if len(e.link_faces) == 1]
        r['openEdges'] += len(open_edges)
        r['nonManifoldEdges'] += sum(1 for e in bm.edges if len(e.link_faces) > 2)
        r['holes'] += loops(open_edges)
        r['looseVertices'] += sum(1 for v in bm.verts if not v.link_faces)
        before = {f.index: f.normal.copy() for f in bm.faces}
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        r['flippedFaces'] += sum(1 for f in bm.faces if before[f.index].dot(f.normal) < 0)
        for part in parts(bm):
            r['parts'] += 1
            ps = [v.co for f in part for v in f.verts]
            span = (mathutils.Vector((max(p.x for p in ps), max(p.y for p in ps), max(p.z for p in ps)))
                    - mathutils.Vector((min(p.x for p in ps), min(p.y for p in ps), min(p.z for p in ps)))).length
            r['fragments'] += span < FRAGMENT_SHARE * diag
        bm.free()
    return r, diag


def loops(edges):
    """How many separate boundary loops these open edges make: each is one hole (or one outer rim)."""
    left, n = set(edges), 0
    while left:
        n += 1
        todo = [left.pop()]
        while todo:
            e = todo.pop()
            for v in e.verts:
                for f in v.link_edges:
                    if f in left:
                        left.discard(f)
                        todo.append(f)
    return n


def parts(bm):
    """The faces of each connected piece."""
    left = set(bm.faces)
    while left:
        seed = left.pop()
        piece, todo = [seed], [seed]
        while todo:
            f = todo.pop()
            for e in f.edges:
                for g in e.link_faces:
                    if g in left:
                        left.discard(g)
                        piece.append(g)
                        todo.append(g)
        yield piece


def repair(objs, diag):
    """Drops loose vertices and edges and stray fragments, joins vertices at the same place, makes normals face out,
    closes holes up to HOLE_MAX_SIDES edges. Returns what it did, in words."""
    done = {'looseRemoved': 0, 'fragmentsRemoved': 0, 'verticesJoined': 0, 'facesTurned': 0, 'holesClosed': 0}
    for o in objs:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        scale = max(o.matrix_world.to_scale()) or 1.0
        # first join vertices at the same place (seams split by the file), so pieces are judged by their real shape
        n = len(bm.verts)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=diag * 1e-6 / scale)
        done['verticesJoined'] += n - len(bm.verts)
        loose = [v for v in bm.verts if not v.link_faces]
        done['looseRemoved'] += len(loose)
        bmesh.ops.delete(bm, geom=loose, context='VERTS')
        for part in list(parts(bm)):
            ps = [v.co for f in part for v in f.verts]
            span = (mathutils.Vector((max(p.x for p in ps), max(p.y for p in ps), max(p.z for p in ps)))
                    - mathutils.Vector((min(p.x for p in ps), min(p.y for p in ps), min(p.z for p in ps)))).length * scale
            if span < FRAGMENT_SHARE * diag:
                done['fragmentsRemoved'] += 1
                bmesh.ops.delete(bm, geom=part, context='FACES')
        before = {f.index: f.normal.copy() for f in bm.faces}
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        done['facesTurned'] += sum(1 for f in bm.faces if before[f.index].dot(f.normal) < 0)
        open_before = loops([e for e in bm.edges if len(e.link_faces) == 1])
        bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if len(e.link_faces) == 1], sides=HOLE_MAX_SIDES)
        done['holesClosed'] += open_before - loops([e for e in bm.edges if len(e.link_faces) == 1])
        bm.to_mesh(o.data)
        o.data.update()
        bm.free()
    return done


def triangles(objs):
    return sum(max(0, len(p.vertices) - 2) for o in objs for p in o.data.polygons)


def weld(objs, diag):
    """Joins vertices at the same place (files split them along seams); a mesh must be whole before it is lightened."""
    joined = 0
    for o in objs:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        n = len(bm.verts)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=diag * 1e-6 / (max(o.matrix_world.to_scale()) or 1.0))
        joined += n - len(bm.verts)
        bm.to_mesh(o.data)
        o.data.update()
        bm.free()
    return joined


SMALL = 64  # objects this light are left as they are: the triangle budget goes to the heavy ones


def decimate(objs, target):
    """Brings the triangles down to about target, the shape kept (Blender's Decimate, collapse). Light objects are
    left alone; the heavy ones share what is left of the budget in proportion."""
    small = [o for o in objs if triangles([o]) <= SMALL]
    heavy = [o for o in objs if o not in small]
    budget = max(1, target - triangles(small))
    total = triangles(heavy)
    if total <= budget:
        return 0
    dropped = 0
    for o in heavy:
        if o.data.shape_keys:
            # Blender cannot lighten a mesh that has shape keys: this working copy loses them (the source keeps them)
            dropped += len(o.data.shape_keys.key_blocks)
            o.shape_key_clear()
        m = o.modifiers.new('prep_decimate', 'DECIMATE')
        m.decimate_type = 'COLLAPSE'
        m.ratio = max(0.0001, min(1.0, budget / total))
        m.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_apply(modifier=m.name)
    return dropped


def copy_of(objs, suffix):
    out = []
    for o in objs:
        c = o.copy()
        c.data = o.data.copy()
        c.name = o.name + suffix
        bpy.context.scene.collection.objects.link(c)
        out.append(c)
    return out


def hull(objs, name):
    """One simple collision shape: the convex hull of every vertex."""
    bm = bmesh.new()
    for o in objs:
        for v in o.data.vertices:
            bm.verts.new(o.matrix_world @ v.co)
    if len(bm.verts) >= 4:
        bmesh.ops.convex_hull(bm, input=bm.verts)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def export(objs, path, target):
    """Writes the selected objects for the target. Returns the axes and units in words."""
    for o in bpy.context.scene.objects:
        o.select_set(o in objs)
    if target == 'unreal':
        bpy.ops.export_scene.fbx(filepath=path, use_selection=True, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Y',
                                 axis_up='Z', mesh_smooth_type='FACE', add_leaf_bones=False, bake_anim=False)
        return ('FBX for Unreal: Blender metres written as Unreal centimetres (1 m = 100 Unreal units), Z up, '
                'Unreal X = Blender X, Unreal Y = -Blender Y (north is -Y, as in docs/unreal.md)')
    if target == 'blender':
        bpy.ops.wm.save_as_mainfile(filepath=path, copy=True)
        return 'a .blend: Blender metres, Z up, as the model was brought in'
    bpy.ops.export_scene.gltf(filepath=path, export_format='GLB', use_selection=True, export_apply=True, export_yup=True,
                              export_animations=False, export_cameras=False, export_lights=False)
    return 'glTF (.glb): metres, Y up (Blender Z up becomes glTF Y up, Blender -Y becomes glTF +Z)' + (
        '; origin at the base, where the model meets the ground (Groundwork)' if target == 'groundwork' else '')


def main():
    job = json.load(open(sys.argv[sys.argv.index('--') + 1]))
    out, name, target = job['out'], job['name'], job.get('target', 'godot')
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_homefile(use_empty=True)
    for f in job['files']:
        bridge.import_file(f)
    sc = bpy.context.scene
    objs = [o for o in sc.objects if o.type == 'MESH']
    bridge.link_textures(objs, job.get('textures'), unreal=bool(job.get('unreal')))
    bpy.context.view_layer.update()
    objs = [o for o in objs if o.visible_get()]
    if not objs:
        raise SystemExit('no mesh in the asset')
    report = {'name': name, 'target': target, 'source': [os.path.basename(f) for f in job['files']], 'done': []}
    report['found'], diag = look(objs)

    if job.get('fix'):
        report['repair'] = repair(objs, diag)
        report['done'].append('repaired (see repair)')
    max_tris = job.get('maxTris')
    if max_tris and triangles(objs) > max_tris:
        before = triangles(objs)
        if not job.get('fix'):
            report['done'].append(f'joined {weld(objs, diag)} vertices split along seams (needed to lighten)')
        dropped = decimate(objs, max_tris)
        report['done'].append(f'lightened from {before} to {triangles(objs)} triangles')
        if dropped:
            report['done'].append(f'dropped {dropped} shape keys (a mesh with shape keys cannot be lightened; the source keeps them)')
    if target == 'groundwork':
        pts = [o.matrix_world @ mathutils.Vector(c) for o in objs for c in o.bound_box]
        shift = mathutils.Vector(((min(p.x for p in pts) + max(p.x for p in pts)) / 2,
                                  (min(p.y for p in pts) + max(p.y for p in pts)) / 2, min(p.z for p in pts)))
        for o in sc.objects:
            if o.parent is None:
                o.location -= shift
        report['done'].append('origin moved to the base')
    bpy.context.view_layer.update()
    report['after'], _ = look(objs) if report['done'] else (report['found'], 0)

    ext = {'unreal': '.fbx', 'blender': '.blend'}.get(target, '.glb')
    files = []
    keep = list(objs)
    if job.get('collision'):
        # Unreal finds a mesh's collision by the UCX_ prefix in the same FBX; glTF targets get it as its own file
        col = hull(objs, f'UCX_{name}_00' if target == 'unreal' else f'{name}_collision')
        report['collision'] = {'object': col.name, 'triangles': triangles([col])}
        if target == 'unreal':
            keep.append(col)
        else:
            p = os.path.join(out, f'{name}_collision{ext}')
            export([col], p, target)
            files.append(os.path.basename(p))
    path = os.path.join(out, name + ext)
    report['axes'] = export(keep, path, target)
    files.insert(0, os.path.basename(path))
    report['lods'] = [{'level': 0, 'file': os.path.basename(path), 'triangles': triangles(objs)}]
    for level in range(1, int(job.get('lods') or 0) + 1):
        lod = copy_of(objs, f'_LOD{level}')
        weld(lod, diag)
        decimate(lod, max(1, int(triangles(objs) * 0.5 ** level)))
        p = os.path.join(out, f'{name}_LOD{level}{ext}')
        export(lod, p, target)
        files.append(os.path.basename(p))
        report['lods'].append({'level': level, 'file': os.path.basename(p), 'triangles': triangles(lod)})
        for o in lod:
            bpy.data.objects.remove(o)
    report['files'] = files
    with open(os.path.join(out, name + '.report.json'), 'w') as f:
        json.dump(report, f, indent=1)
    print('ASSETSHELF_PREP', json.dumps(report), flush=True)


main()
