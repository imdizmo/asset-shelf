// UE 5.2+ animation data (AnimationSequencerDataModel): every bone is a control of an FK control rig, named
// <bone, lower case>_CONTROL, with three translation, three rotation (Euler degrees: roll X, pitch Y, yaw Z) and three
// scale curves (FMovieSceneFloatChannel) keyed in frames. Sampled at every frame as the engine's
// IterateTransformControlCurve does: a curve without keys gives its default, one without any data keeps the last value.
using System.Numerics;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.MovieScene;
using CUE4Parse.UE4.Objects.UObject;

static class Sequencer
{
    public static (List<BoneKeys> Keys, int Frames, float Fps) Keys(FStructFallback[] controls, FReferenceSkeletonView skel)
    {
        var byName = new Dictionary<string, FMovieSceneChannel<float>[]>(StringComparer.OrdinalIgnoreCase);
        float fps = 30f;
        int frames = 1;
        foreach (FStructFallback c in controls)
        {
            string name = c.TryGetValue(out FName n, "ParameterName") ? n.Text : "";
            var ch = new FMovieSceneChannel<float>[9];
            foreach (var prop in c.Properties)
            {
                int at = prop.Name.Text switch { "Translation" => 0, "Rotation" => 3, "Scale" => 6, _ => -1 };
                if (at >= 0 && prop.ArrayIndex is >= 0 and < 3 && prop.Tag?.GenericValue is FScriptStruct { StructType: FMovieSceneChannel<float> channel })
                {
                    ch[at + prop.ArrayIndex] = channel;
                    if (channel.TickResolution.Numerator > 0 && channel.TickResolution.Denominator > 0)
                        fps = (float) channel.TickResolution.Numerator / channel.TickResolution.Denominator;
                    if (channel.Times is { Length: > 0 } times)
                        frames = Math.Max(frames, times[^1].Value + 1);
                }
            }

            byName[name] = ch;
        }

        var keys = new List<BoneKeys>();
        for (int bone = 0; bone < skel.Count; bone++)
        {
            if (!byName.TryGetValue(skel.Name(bone) + "_CONTROL", out var ch) || ch.All(c => !HasData(c)))
                continue;
            var pos = new FVector[frames];
            var rot = new FQuat[frames];
            var scale = new FVector[frames];
            var v = new float[9] { 0, 0, 0, 0, 0, 0, 1, 1, 1 };
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < 9; i++) v[i] = Evaluate(ch[i], f, v[i]);
                pos[f] = new FVector(v[0], v[1], v[2]);
                rot[f] = FromEuler(v[3], v[4], v[5]);
                scale[f] = new FVector(v[6], v[7], v[8]);
            }

            keys.Add(new BoneKeys(bone, pos, rot, scale));
        }

        return (keys, frames, fps);
    }

    static bool HasData(FMovieSceneChannel<float> c) => c.Times is { Length: > 0 } || c.bHasDefaultValue;

    /// <summary>The curve at a frame: stored keys (constant, linear or cubic), the default when it has no keys, else the previous value.</summary>
    static float Evaluate(FMovieSceneChannel<float> c, int frame, float previous)
    {
        var times = c.Times;
        var values = c.Values;
        if (times is not { Length: > 0 } || values is not { Length: > 0 } || values.Length != times.Length)
            return c.bHasDefaultValue && c.DefaultValue is float d ? d : previous;
        if (frame <= times[0].Value) return values[0].Value;
        if (frame >= times[^1].Value) return values[^1].Value;
        int hi = Array.FindIndex(times, t => t.Value > frame);
        int lo = hi - 1;
        var k1 = values[lo];
        var k2 = values[hi];
        float span = times[hi].Value - times[lo].Value;
        float a = (frame - times[lo].Value) / span;
        switch (k1.InterpMode)
        {
            case ERichCurveInterpMode.RCIM_Constant:
                return k1.Value;
            case ERichCurveInterpMode.RCIM_Cubic:
                // the engine's unweighted cubic: a Bezier with control points a third of the span along each tangent
                float p0 = k1.Value, p1 = p0 + k1.Tangent.LeaveTangent * span / 3f, p3 = k2.Value, p2 = p3 - k2.Tangent.ArriveTangent * span / 3f;
                float u = 1 - a;
                return u * u * u * p0 + 3 * u * u * a * p1 + 3 * u * a * a * p2 + a * a * a * p3;
            default:
                return k1.Value + (k2.Value - k1.Value) * a;
        }
    }

    /// <summary>FQuat::MakeFromEuler(roll, pitch, yaw in degrees), as FRotator::Quaternion computes it.</summary>
    static FQuat FromEuler(float roll, float pitch, float yaw)
    {
        double r = roll * Math.PI / 360, p = pitch * Math.PI / 360, y = yaw * Math.PI / 360;
        double sr = Math.Sin(r), cr = Math.Cos(r), sp = Math.Sin(p), cp = Math.Cos(p), sy = Math.Sin(y), cy = Math.Cos(y);
        return new FQuat((float) (cr * sp * sy - sr * cp * cy), (float) (-cr * sp * cy - sr * cp * sy), (float) (cr * cp * sy - sr * sp * cy), (float) (cr * cp * cy + sr * sp * sy));
    }
}

/// <summary>Bone names by index, from the skeleton.</summary>
readonly record struct FReferenceSkeletonView(CUE4Parse.UE4.Assets.Exports.Animation.FReferenceSkeleton Skeleton)
{
    public int Count => Skeleton.FinalRefBoneInfo.Length;
    public string Name(int bone) => Skeleton.FinalRefBoneInfo[bone].Name.Text;
}
