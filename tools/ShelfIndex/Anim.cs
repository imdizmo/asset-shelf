// An animation sequence's keys (UE 4 raw tracks, UE 5.0-5.1 AnimDataModel, UE 5.2+ sequencer curves) on its skeleton's bones, as a glTF with a node per bone
// and one animation; Blender brings it in as an armature with an action. Axes as in Skinned: translation
// (x, z, y) / 100, rotation (-x, -z, -y, w), scale (x, z, y). Bones without a track keep their rest pose.
using System.Numerics;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;

/// <summary>One bone's keys in Unreal's space (centimetres, its own rotation convention).</summary>
sealed record BoneKeys(int Bone, FVector[] Pos, FQuat[] Rot, FVector[] Scale);

static class Anim
{
    public static (byte[] Glb, int Tracks) Write(string name, UAnimSequence anim, USkeleton skeleton)
    {
        var skel = skeleton.ReferenceSkeleton;
        int bones = skel.FinalRefBoneInfo.Length;
        List<BoneKeys> keys;
        int frames;
        float length;
        if (anim.RawAnimationData is { Length: > 0 } r)
        {
            // UE 4: raw tracks in the sequence, with a track-to-bone table
            int[] map = anim.TrackToSkeletonMapTable is { Length: > 0 } native ? [.. native.Select(m => m.BoneTreeIndex)]
                : anim.TryGetValue(out FStructFallback[] table, "TrackToSkeletonMapTable") ? [.. table.Select(m => m.GetOrDefault("BoneTreeIndex", -1))]
                : throw new InvalidOperationException("no track table");
            keys = [.. r.Take(map.Length).Select((t, i) => new BoneKeys(map[i], t.PosKeys, t.RotKeys, t.ScaleKeys))];
            frames = Math.Max(1, anim.GetOrDefault("NumFrames", 1));
            length = anim.GetOrDefault("SequenceLength", 0f);
        }
        else if (anim.Owner?.GetExports().FirstOrDefault(e => e.ExportType == "AnimDataModel") is { } model
                 && model.TryGetValue(out FStructFallback[] boneTracks, "BoneAnimationTracks"))
        {
            // UE 5.0-5.1: the sequence's AnimDataModel holds a track per bone
            keys = [.. boneTracks.Select(t => Track(t) is var tr ? new BoneKeys(t.GetOrDefault("BoneTreeIndex", -1), tr.PosKeys, tr.RotKeys, tr.ScaleKeys) : null!)];
            frames = Math.Max(1, keys.Max(k => Math.Max(k.Pos.Length, Math.Max(k.Rot.Length, k.Scale.Length))));
            length = model.GetOrDefault("PlayLength", 0f);
        }
        else if (anim.Owner?.GetExports().FirstOrDefault(e => e.ExportType == "MovieSceneControlRigParameterSection") is { } section
                 && section.TryGetValue(out FStructFallback[] controls, "TransformParameterNamesAndCurves"))
        {
            // UE 5.2+: AnimationSequencerDataModel, each bone a control (bone_CONTROL) with nine curves
            (keys, frames, float fps) = Sequencer.Keys(controls, new FReferenceSkeletonView(skel));
            length = frames > 1 ? (frames - 1) / fps : 0f;
        }
        else
        {
            throw new NotSupportedException("animation keys in a format not read here");
        }

        float step = frames > 1 && length > 0 ? length / (frames - 1) : 1f / 30f;
        if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null)
            Console.Error.WriteLine($"anim: frames {frames} length {length} keys {string.Join(",", keys.Take(6).Select(t => $"{t.Pos.Length}/{t.Rot.Length}/{t.Scale.Length}"))}");

        var b = new GltfBuilder();
        var nodes = new JsonArray();
        for (int bn = 0; bn < bones; bn++)
        {
            var pose = skel.FinalRefBonePose[bn];
            var node = new JsonObject
            {
                ["name"] = skel.FinalRefBoneInfo[bn].Name.Text,
                ["translation"] = Vec(T(pose.Translation)),
                ["rotation"] = Quat(R(pose.Rotation)),
                ["scale"] = Vec(S(pose.Scale3D)),
            };
            var children = Enumerable.Range(0, bones).Where(c => skel.FinalRefBoneInfo[c].ParentIndex == bn && c != bn).Select(c => (JsonNode) c).ToArray();
            if (children.Length > 0) node["children"] = new JsonArray(children);
            nodes.Add(node);
        }

        var samplers = new JsonArray();
        var channels = new JsonArray();
        var timeCache = new Dictionary<int, int>();
        int Times(int count)
        {
            if (!timeCache.TryGetValue(count, out int a))
            {
                timeCache[count] = a = b.Keys(Enumerable.Range(0, count).Select(i => count == 1 ? 0f : i * step * (frames - 1) / (count - 1)).ToArray(), 1, true);
            }

            return a;
        }

        void Channel(int node, string path, float[] values, int n)
        {
            samplers.Add(new JsonObject { ["input"] = Times(values.Length / n), ["output"] = b.Keys(values, n, false), ["interpolation"] = "LINEAR" });
            channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = node, ["path"] = path } });
        }

        int tracks = 0;
        foreach (BoneKeys t in keys)
        {
            int bone = t.Bone;
            if (bone < 0 || bone >= bones) continue;
            if (t.Pos is { Length: > 0 } pk) Channel(bone, "translation", pk.SelectMany(v => { var x = T(v); return new[] { x.X, x.Y, x.Z }; }).ToArray(), 3);
            if (t.Rot is { Length: > 0 } rk)
            {
                // keep consecutive quaternions in the same hemisphere so linear interpolation takes the short way
                var q = rk.Select(R).ToArray();
                for (int k = 1; k < q.Length; k++) if (Quaternion.Dot(q[k - 1], q[k]) < 0) q[k] = -q[k];
                Channel(bone, "rotation", q.SelectMany(x => new[] { x.X, x.Y, x.Z, x.W }).ToArray(), 4);
            }

            if (t.Scale is { Length: > 0 } sk) Channel(bone, "scale", sk.SelectMany(v => { var x = S(v); return new[] { x.X, x.Y, x.Z }; }).ToArray(), 3);
            tracks++;
        }

        var roots = Enumerable.Range(0, bones).Where(bn => skel.FinalRefBoneInfo[bn].ParentIndex < 0 || skel.FinalRefBoneInfo[bn].ParentIndex >= bones).Select(bn => (JsonNode) bn).ToArray();
        // a skin over the bones, so importers (Blender) build an armature rather than loose nodes
        var global = new Matrix4x4[bones];
        var ibm = new float[16 * bones];
        for (int bn = 0; bn < bones; bn++)
        {
            var pose = skel.FinalRefBonePose[bn];
            var local = Matrix4x4.CreateScale(S(pose.Scale3D)) * Matrix4x4.CreateFromQuaternion(R(pose.Rotation)) * Matrix4x4.CreateTranslation(T(pose.Translation));
            int parent = skel.FinalRefBoneInfo[bn].ParentIndex;
            global[bn] = parent >= 0 && parent < bn ? local * global[parent] : local;
            Matrix4x4.Invert(global[bn], out var inv);
            System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(new[] { inv }).CopyTo(ibm.AsSpan(16 * bn));
        }

        var skin = new JsonObject
        {
            ["joints"] = new JsonArray(Enumerable.Range(0, bones).Select(i => (JsonNode) i).ToArray()),
            ["inverseBindMatrices"] = b.Matrices(ibm),
            ["skeleton"] = roots.FirstOrDefault()?.DeepClone(),
        };
        var animations = new JsonArray(new JsonObject { ["name"] = name, ["samplers"] = samplers, ["channels"] = channels });
        return (b.Finish(name, null, nodes, new JsonArray(roots), new JsonArray(skin), animations), tracks);
    }

    static FRawAnimSequenceTrack Track(FStructFallback t) =>
        t.TryGetValue(out FRawAnimSequenceTrack native, "InternalTrackData") ? native
            : t.TryGetValue(out FStructFallback tagged, "InternalTrackData") ? new FRawAnimSequenceTrack(tagged)
            : throw new InvalidOperationException("unreadable bone track");

    static Vector3 T(FVector v) => new Vector3((float) v.X, (float) v.Z, (float) v.Y) / 100f;
    static Vector3 S(FVector v) => new((float) v.X, (float) v.Z, (float) v.Y);
    static Quaternion R(FQuat q) => Quaternion.Normalize(new Quaternion(-(float) q.X, -(float) q.Z, -(float) q.Y, (float) q.W));
    static JsonArray Vec(Vector3 v) => new(v.X, v.Y, v.Z);
    static JsonArray Quat(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
