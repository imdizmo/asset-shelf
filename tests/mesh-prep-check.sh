#!/usr/bin/env bash
# The mesh prep check (needs Blender): a model made here with known faults (a cube missing a face, a face turned
# inside out, a stray fragment; a dense sphere to lighten), then blender/mesh_prep.py with and without repair.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
s="${HOME}/.cache/asset-shelf/scratch/mesh-prep-check"
rm -rf "${s:?}"; mkdir -p "$s"
cat > "$s/make.py" <<'PY'
import bpy, bmesh, sys
out = sys.argv[sys.argv.index('--') + 1]
bpy.ops.wm.read_homefile(use_empty=True)
bpy.ops.mesh.primitive_cube_add(size=2)
c = bpy.context.object
bm = bmesh.new(); bm.from_mesh(c.data); bm.faces.ensure_lookup_table()
bmesh.ops.delete(bm, geom=[bm.faces[0]], context='FACES_ONLY')
bm.faces.ensure_lookup_table(); bm.faces[1].normal_flip()
bm.faces.new([bm.verts.new((0.9 + x * 0.01, 0.9 + y * 0.01, 3)) for x, y in ((0, 0), (1, 0), (1, 1))])
bm.to_mesh(c.data); bm.free()
bpy.ops.mesh.primitive_uv_sphere_add(segments=128, ring_count=64, location=(4, 0, 0))
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB')
PY
b() { systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 blender -b --factory-startup --disable-autoexec "$@"; }
b -P "$s/make.py" -- "$s/broken.glb" >/dev/null 2>&1
before=$(sha256sum "$s/broken.glb")
prep() { echo "{\"files\":[\"$s/broken.glb\"],\"name\":\"broken\",\"out\":\"$s/$1\",\"fix\":$2,\"maxTris\":5000,\"lods\":1,\"collision\":true,\"target\":\"$3\"}" > "$s/$1.json"
         b -P "$root/blender/mesh_prep.py" -- "$s/$1.json" 2>&1 | grep '^ASSETSHELF_PREP' | cut -d' ' -f2-; }
check() { python3 -I -c "import json,sys; d=json.loads(sys.argv[1]); f,a=d['found'],d['after']; assert $2, d" "$3" && echo "ok $1"; }
fixed=$(prep fixed true godot)
check "faults found" 'f["holes"]==2 and f["flippedFaces"]==2 and f["fragments"]==1 and f["parts"]==3' "$fixed"
check "repaired, lightened, shape kept" 'a["holes"]==0 and a["flippedFaces"]==0 and a["fragments"]==0 and abs(a["triangles"]-5000)<10 and a["size"]==[6.0,2.0,2.0]' "$fixed"
check "LOD and collision written" 'd["lods"][1]["triangles"]<2600 and d["collision"]["triangles"]>0 and "broken_LOD1.glb" in d["files"]' "$fixed"
kept=$(prep kept false unreal)
check "without repair the faults stay, lightened anyway" 'a["flippedFaces"]==2 and abs(a["triangles"]-5000)<10 and d["collision"]["object"]=="UCX_broken_00"' "$kept"
[[ -f "$s/kept/broken.fbx" && -f "$s/fixed/broken.glb" && -f "$s/fixed/broken.report.json" ]] && echo "ok files"
[[ "$(sha256sum "$s/broken.glb")" == "$before" ]] && echo "ok the source is untouched"
echo "all passed"; rm -rf "${s:?}"
