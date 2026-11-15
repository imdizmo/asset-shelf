// A skeletal mesh's editor LOD 0 (sections of soft vertices with bone influences) and its reference skeleton as a
// skinned glTF. Unreal's left-handed Z-up centimetres become glTF's Y-up metres by swapping Y and Z, for vertices and
// for every bone's rest transform: translation (x, z, y) / 100, rotation (-x, -z, -y, w), scale (x, z, y).
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

static class Skinned
{
    public static (byte[] Glb, int Triangles) Write(string name, USkeletalMesh sk, string[] materialNames)
    {
        var lod = sk.LODModels?.FirstOrDefault() ?? throw new InvalidOperationException("no editor LOD model");
        var skel = sk.ReferenceSkeleton ?? throw new InvalidOperationException("no reference skeleton");
        uint[] indices = lod.Indices?.Buffer ?? [];
        int bones = skel.FinalRefBoneInfo.Length;
        if (bones == 0 || lod.Sections.Length == 0 || indices.Length == 0)
        {
            throw new InvalidOperationException("empty skeletal mesh");
        }

        // bones: glTF rest transforms and their globals
        var t = new Vector3[bones];
        var r = new Quaternion[bones];
        var sc = new Vector3[bones];
        var global = new Matrix4x4[bones];
        for (int bi = 0; bi < bones; bi++)
        {
            var pose = skel.FinalRefBonePose[bi];
            t[bi] = new Vector3((float) pose.Translation.X, (float) pose.Translation.Z, (float) pose.Translation.Y) / 100f;
            r[bi] = Quaternion.Normalize(new Quaternion(-(float) pose.Rotation.X, -(float) pose.Rotation.Z, -(float) pose.Rotation.Y, (float) pose.Rotation.W));
            sc[bi] = new Vector3((float) pose.Scale3D.X, (float) pose.Scale3D.Z, (float) pose.Scale3D.Y);
            var local = Matrix4x4.CreateScale(sc[bi]) * Matrix4x4.CreateFromQuaternion(r[bi]) * Matrix4x4.CreateTranslation(t[bi]);
            int parent = skel.FinalRefBoneInfo[bi].ParentIndex;
            global[bi] = parent >= 0 && parent < bi ? local * global[parent] : local;
        }

        // sections: vertices in order (BaseVertexIndex), their triangles by material
        var pos = new List<float>();
        var nrm = new List<float>();
        var uv = new List<float>();
        var joints = new List<ushort>();
        var weights = new List<float>();
        // old packages keep the vertices in chunks (rigid ones first), newer ones in the sections
        var sources = lod.Chunks?.Any(c => (c.SoftVertices?.Length ?? 0) + (c.RigidVertices?.Length ?? 0) > 0) == true
            ? lod.Chunks.Select(c => ((c.RigidVertices ?? []).Concat(c.SoftVertices ?? []).ToArray(), c.BoneMap))
            : lod.Sections.Select(sec => (sec.SoftVertices ?? [], sec.BoneMap));
        foreach (var (vertices, boneMap) in sources)
        {
            foreach (FSoftVertex v in vertices)
            {
                pos.AddRange([(float) v.Pos.X / 100f, (float) v.Pos.Z / 100f, (float) v.Pos.Y / 100f]);
                var n = v.Normal.Length > 2 ? v.Normal[2] : default;
                var nv = Vector3.Normalize(new Vector3(n.X, n.Z, n.Y));
                nrm.AddRange(float.IsFinite(nv.X) ? [nv.X, nv.Y, nv.Z] : [0f, 1f, 0f]);
                uv.AddRange(v.UVs.Length > 0 ? [v.UVs[0].U, v.UVs[0].V] : [0f, 0f]);
                var infl = new List<(int Bone, float W)>();
                if (v.Infs is { } inf)
                {
                    float scale = inf.bUse16BitBoneWeight ? 65535f : 255f;
                    for (int k = 0; k < inf.BoneIndex.Length && k < inf.BoneWeight.Length; k++)
                    {
                        if (inf.BoneWeight[k] > 0 && inf.BoneIndex[k] < boneMap.Length)
                        {
                            infl.Add((boneMap[inf.BoneIndex[k]], inf.BoneWeight[k] / scale));
                        }
                    }
                }

                var top = infl.OrderByDescending(x => x.W).Take(4).ToList();
                float sum = top.Sum(x => x.W);
                for (int k = 0; k < 4; k++)
                {
                    joints.Add(k < top.Count && top[k].Bone < bones ? (ushort) top[k].Bone : (ushort) 0);
                    weights.Add(k < top.Count && sum > 0 ? top[k].W / sum : k == 0 && sum == 0 ? 1f : 0f);
                }
            }
        }

        int vertexCount = pos.Count / 3;
        var b = new GltfBuilder();
        int aPos = b.Floats(pos.ToArray(), 3, true), aNrm = b.Floats(nrm.ToArray(), 3, false), aUv = b.Floats(uv.ToArray(), 2, false);
        int aJoints = b.Joints(joints.ToArray()), aWeights = b.Floats(weights.ToArray(), 4, false);
        var primitives = new JsonArray();
        var materialIndex = new Dictionary<string, int>();
        int triangles = 0;
        foreach (var group in lod.Sections.Select((s, i) => (s, i)).GroupBy(x => (int) x.s.MaterialIndex))
        {
            var tri = new List<uint>();
            foreach (var (section, i) in group)
            {
                for (int k = 0; k < section.NumTriangles * 3; k++)
                {
                    int at = section.BaseIndex + k;
                    if (at < indices.Length && indices[at] < vertexCount) tri.Add(indices[at]);
                }
            }

            tri.RemoveRange(tri.Count - tri.Count % 3, tri.Count % 3);
            if (tri.Count == 0) continue;
            triangles += tri.Count / 3;
            string mat = group.Key >= 0 && group.Key < materialNames.Length ? materialNames[group.Key] : $"Material_{group.Key}";
            if (!materialIndex.TryGetValue(mat, out int mi)) materialIndex[mat] = mi = b.Material(mat);
            primitives.Add(new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = aPos, ["NORMAL"] = aNrm, ["TEXCOORD_0"] = aUv, ["JOINTS_0"] = aJoints, ["WEIGHTS_0"] = aWeights },
                ["indices"] = b.Indices(tri.ToArray()),
                ["material"] = mi,
            });
        }

        // nodes: 0 = the mesh, 1.. = bones
        var nodes = new JsonArray(new JsonObject { ["name"] = name, ["mesh"] = 0, ["skin"] = 0 });
        for (int bn = 0; bn < bones; bn++)
        {
            var node = new JsonObject
            {
                ["name"] = skel.FinalRefBoneInfo[bn].Name.Text,
                ["translation"] = new JsonArray(t[bn].X, t[bn].Y, t[bn].Z),
                ["rotation"] = new JsonArray(r[bn].X, r[bn].Y, r[bn].Z, r[bn].W),
                ["scale"] = new JsonArray(sc[bn].X, sc[bn].Y, sc[bn].Z),
            };
            var children = Enumerable.Range(0, bones).Where(c => skel.FinalRefBoneInfo[c].ParentIndex == bn && c != bn).Select(c => (JsonNode) (c + 1)).ToArray();
            if (children.Length > 0) node["children"] = new JsonArray(children);
            nodes.Add(node);
        }

        var ibm = new float[16 * bones];
        for (int bn = 0; bn < bones; bn++)
        {
            Matrix4x4.Invert(global[bn], out var inv);
            MemoryMarshal.Cast<Matrix4x4, float>(new[] { inv }).CopyTo(ibm.AsSpan(16 * bn));
        }

        var roots = Enumerable.Range(0, bones).Where(bn => skel.FinalRefBoneInfo[bn].ParentIndex < 0 || skel.FinalRefBoneInfo[bn].ParentIndex >= bones).Select(bn => (JsonNode) (bn + 1)).ToList();
        var skin = new JsonObject
        {
            ["joints"] = new JsonArray(Enumerable.Range(1, bones).Select(i => (JsonNode) i).ToArray()),
            ["inverseBindMatrices"] = b.Matrices(ibm),
            ["skeleton"] = roots.FirstOrDefault()?.DeepClone(),
        };
        var sceneNodes = new JsonArray(new JsonNode[] { 0 }.Concat(roots).ToArray());
        return (b.Finish(name, primitives, nodes, sceneNodes, new JsonArray(skin)), triangles);
    }

    /// <summary>The bone positions from Unreal's own transforms (composed as Unreal does, then converted) against the
    /// glTF globals: the largest gap in metres. Near zero when the rotation conversion is right.</summary>
    public static float Check(USkeletalMesh sk)
    {
        var skel = sk.ReferenceSkeleton;
        int bones = skel.FinalRefBoneInfo.Length;
        var ue = new CUE4Parse.UE4.Objects.Core.Math.FTransform[bones];
        var gl = new Matrix4x4[bones];
        float worst = 0;
        for (int b = 0; b < bones; b++)
        {
            var pose = skel.FinalRefBonePose[b];
            int parent = skel.FinalRefBoneInfo[b].ParentIndex;
            ue[b] = parent >= 0 && parent < b ? pose * ue[parent] : pose;
            var t = new Vector3((float) pose.Translation.X, (float) pose.Translation.Z, (float) pose.Translation.Y) / 100f;
            var r = Quaternion.Normalize(new Quaternion(-(float) pose.Rotation.X, -(float) pose.Rotation.Z, -(float) pose.Rotation.Y, (float) pose.Rotation.W));
            var s = new Vector3((float) pose.Scale3D.X, (float) pose.Scale3D.Z, (float) pose.Scale3D.Y);
            var local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
            gl[b] = parent >= 0 && parent < b ? local * gl[parent] : local;
            var u = ue[b].Translation;
            var expect = new Vector3((float) u.X, (float) u.Z, (float) u.Y) / 100f;
            worst = Math.Max(worst, Vector3.Distance(expect, gl[b].Translation));
        }

        return worst;
    }
}

/// <summary>Buffers, accessors and the final .glb for a mesh written piece by piece.</summary>
sealed class GltfBuilder
{
    readonly MemoryStream bin = new();
    readonly JsonArray views = [], accessors = [], materials = [];

    int View(ReadOnlySpan<byte> data, int? target)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
        if (target is { } tg) v["target"] = tg;
        views.Add(v);
        bin.Write(data);
        return views.Count - 1;
    }

    int Add(JsonObject a)
    {
        accessors.Add(a);
        return accessors.Count - 1;
    }

    public int Floats(float[] v, int n, bool bounds)
    {
        var a = new JsonObject { ["bufferView"] = View(MemoryMarshal.AsBytes(v.AsSpan()), 34962), ["componentType"] = 5126, ["count"] = v.Length / n, ["type"] = n switch { 2 => "VEC2", 3 => "VEC3", _ => "VEC4" } };
        if (bounds)
        {
            a["min"] = new JsonArray(Enumerable.Range(0, n).Select(c => (JsonNode) Enumerable.Range(0, v.Length / n).Min(i => v[i * n + c])).ToArray());
            a["max"] = new JsonArray(Enumerable.Range(0, n).Select(c => (JsonNode) Enumerable.Range(0, v.Length / n).Max(i => v[i * n + c])).ToArray());
        }

        return Add(a);
    }

    public int Joints(ushort[] j) => Add(new JsonObject { ["bufferView"] = View(MemoryMarshal.AsBytes(j.AsSpan()), 34962), ["componentType"] = 5123, ["count"] = j.Length / 4, ["type"] = "VEC4" });

    public int Indices(uint[] i) => Add(new JsonObject { ["bufferView"] = View(MemoryMarshal.AsBytes(i.AsSpan()), 34963), ["componentType"] = 5125, ["count"] = i.Length, ["type"] = "SCALAR" });

    /// <summary>Animation keyframe data (no buffer-view target), with min/max when it is the time input.</summary>
    public int Keys(float[] v, int n, bool bounds)
    {
        var a = new JsonObject { ["bufferView"] = View(MemoryMarshal.AsBytes(v.AsSpan()), null), ["componentType"] = 5126, ["count"] = v.Length / n, ["type"] = n switch { 1 => "SCALAR", 3 => "VEC3", _ => "VEC4" } };
        if (bounds)
        {
            a["min"] = new JsonArray(v.Min());
            a["max"] = new JsonArray(v.Max());
        }

        return Add(a);
    }

    public int Matrices(float[] m) => Add(new JsonObject { ["bufferView"] = View(MemoryMarshal.AsBytes(m.AsSpan()), null), ["componentType"] = 5126, ["count"] = m.Length / 16, ["type"] = "MAT4" });

    public int Material(string name)
    {
        materials.Add(new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 0.8 } });
        return materials.Count - 1;
    }

    public byte[] Finish(string name, JsonArray? primitives, JsonArray nodes, JsonArray sceneNodes, JsonArray? skins, JsonArray? animations = null)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Asset Shelf" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodes,
            ["materials"] = materials,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        if (primitives is not null) gltf["meshes"] = new JsonArray(new JsonObject { ["name"] = name, ["primitives"] = primitives });
        if (skins is not null) gltf["skins"] = skins;
        if (animations is not null) gltf["animations"] = animations;
        byte[] json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        int jsonLen = (json.Length + 3) & ~3;
        var glb = new MemoryStream();
        var w = new BinaryWriter(glb);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint) (12 + 8 + jsonLen + 8 + bin.Length));
        w.Write((uint) jsonLen); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonLen; i++) w.Write((byte) ' ');
        w.Write((uint) bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return glb.ToArray();
    }
}
