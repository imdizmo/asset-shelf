// ShelfIndex sounds: measures every SoundWave of a pack from the audio the editor package keeps (the imported WAV), and
// writes <out>/sounds.json. Plain measurement, no model: length, loudness, a pitch estimate, brightness, attack, and
// whether it loops cleanly. `--extract <asset path> --wav <file>` writes one sound's WAV, for playing it; `--measure
// <file.wav>` measures one plain WAV file.
// The packages come from third-party sellers: sizes read from them are bounded.
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Versions;

static class Sounds
{
    const int MaxWav = 512 << 20;
    static string _pack = "";

    public static int Run(string[] args)
    {
        if (Arg(args, "--measure") is { } file)
        {
            // one plain WAV file (a loose sound in a raw pack, or a check): its measurements as JSON
            if (new FileInfo(file).Length > MaxWav || Pcm.Parse(File.ReadAllBytes(file)) is not { } one)
            {
                Console.Error.WriteLine($"{file}: not a WAV file this reads (PCM 8/16/24/32-bit or 32-bit float)");
                return 1;
            }

            Console.WriteLine(Measure.Of(one).ToJsonString());
            return 0;
        }

        string? pack = Arg(args, "--pack"), outDir = Arg(args, "--out"), extract = Arg(args, "--extract"), wav = Arg(args, "--wav");
        if (pack is null || (outDir is null && (extract is null || wav is null)))
        {
            Console.Error.WriteLine("usage: ShelfIndex sounds --pack <pack>/unreal --out <index dir>\n       ShelfIndex sounds --pack <pack>/unreal --extract <asset path> --wav <file>");
            return 2;
        }

        _pack = pack;
        var provider = new DefaultFileProvider(new DirectoryInfo(Path.Combine(pack, "Content")), SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_LATEST));
        provider.Initialize();
        provider.Mount();

        if (extract is not null)
        {
            string rel = extract.Split('.')[0];
            string? key = rel.StartsWith("/Game/", StringComparison.Ordinal)
                ? provider.Files.Keys.FirstOrDefault(k => k.Equals("Content/" + rel["/Game/".Length..] + ".uasset", StringComparison.OrdinalIgnoreCase))
                : null;
            if (key is null || Wav(provider, key) is not { } bytes)
            {
                Console.Error.WriteLine($"no sound data for {extract}");
                return 1;
            }

            File.WriteAllBytes(wav!, bytes);
            return 0;
        }

        var sounds = new JsonObject();
        int read = 0, skipped = 0;
        // Only the packages the index lists as sounds, when there is an index: opening every package of a large pack
        // to ask what it holds read tens of GB for a handful of sounds.
        HashSet<string>? listed = File.Exists(Path.Combine(outDir!, "index.json"))
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(outDir!, "index.json")))?["assets"]?.AsArray()
                .Where(a => (string?)a?["class"] == "SoundWave" && ((string?)a["path"])?.StartsWith("/Game/", StringComparison.Ordinal) == true)
                .Select(a => "Content/" + ((string)a!["path"]!).Split('.')[0]["/Game/".Length..] + ".uasset")
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (string key in provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && listed?.Contains(k) != false).Order(StringComparer.Ordinal))
        {
            byte[]? bytes = Wav(provider, key, out string? name);
            if (name is null)
            {
                continue;  // not a sound
            }

            string path = "/Game/" + key["Content/".Length..^".uasset".Length] + "." + name;
            if (bytes is null || Pcm.Parse(bytes) is not { } pcm)
            {
                skipped++;
                continue;  // compressed or unreadable: the index keeps what the asset registry says
            }

            sounds[path] = Measure.Of(pcm);
            read++;
        }

        Directory.CreateDirectory(outDir!);
        File.WriteAllText(Path.Combine(outDir!, "sounds.json"), new JsonObject { ["version"] = 1, ["sounds"] = sounds }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"{read} sounds measured, {skipped} without readable audio");
        return 0;
    }

    static string? Arg(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    static byte[]? Wav(DefaultFileProvider provider, string key) => Wav(provider, key, out _);

    /// <summary>A WAV kept whole inside the package (some editor packages store it in a form the reader does not
    /// follow): the first "RIFF....WAVE" whose stated size fits in the file.</summary>
    static byte[]? Inline(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length > MaxWav)
        {
            return null;
        }

        byte[] b = File.ReadAllBytes(file);
        for (int i = 0; i + 12 <= b.Length; i++)
        {
            if (b[i] == 'R' && b[i + 1] == 'I' && b[i + 2] == 'F' && b[i + 3] == 'F' && b[i + 8] == 'W' && b[i + 9] == 'A' && b[i + 10] == 'V' && b[i + 11] == 'E')
            {
                long size = BitConverter.ToUInt32(b, i + 4) + 8L;
                if (size > 44 && i + size <= b.Length)
                {
                    return b[i..(int)(i + size)];
                }
            }
        }

        return null;
    }

    /// <summary>The WAV an editor package keeps for its sound (null when it has none it can give); name is the sound's
    /// object name, null when the package holds no sound.</summary>
    static byte[]? Wav(DefaultFileProvider provider, string key, out string? name)
    {
        name = null;
        try
        {
            if (provider.LoadPackage(key).GetExports().OfType<USoundWave>().FirstOrDefault() is not { } wave)
            {
                return null;
            }

            name = wave.Name;
            string file = Path.Combine(_pack, key);  // keys are Content/…, relative to the pack's unreal folder
            if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null)
            {
                Console.Error.WriteLine($"{key}: streaming {wave.bStreaming}, editor {wave.EditorRawData?.PayloadSize} ({wave.EditorRawData?.Flags}), raw {wave.RawData?.Data?.Length}, compressed {wave.CompressedFormatData?.Formats.Count}");
            }

            if (wave.EditorRawData is { PayloadSize: > 0 and <= MaxWav } ed && ed.Payload?.Decompress() is { Length: > 12 } d && d[0] == 'R' && d[1] == 'I')
            {
                return d;
            }

            if (wave.RawData?.Data is { Length: > 12 and <= MaxWav } r && r[0] == 'R' && r[1] == 'I')
            {
                return r;
            }

            return Inline(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null)
            {
                Console.Error.WriteLine($"{key}: {e.GetType().Name}: {e.Message}");
            }

            return null;
        }
    }
}

/// <summary>Audio as mono float samples, with what the file said about itself.</summary>
sealed record Pcm(float[] Mono, int Rate, int Channels, int Bits)
{
    /// <summary>A RIFF WAVE file: 8, 16, 24 or 32-bit integer or 32-bit float PCM. Null for anything else.</summary>
    public static Pcm? Parse(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
        {
            return null;
        }

        int format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
        for (int at = 12; at + 8 <= b.Length;)
        {
            int len = BitConverter.ToInt32(b, at + 4);
            if (len < 0 || at + 8L + len > b.Length)
            {
                len = b.Length - at - 8;  // a truncated last chunk: take what is there
            }

            string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
            if (id == "fmt " && len >= 16)
            {
                format = BitConverter.ToUInt16(b, at + 8);
                channels = BitConverter.ToUInt16(b, at + 10);
                rate = BitConverter.ToInt32(b, at + 12);
                bits = BitConverter.ToUInt16(b, at + 22);
                if (format == 0xFFFE && len >= 26)
                {
                    format = BitConverter.ToUInt16(b, at + 32);  // WAVE_FORMAT_EXTENSIBLE: the sub-format's first two bytes
                }
            }
            else if (id == "data")
            {
                (dataAt, dataLen) = (at + 8, len);
            }

            at += 8 + len + (len & 1);
        }

        if (dataAt < 0 || channels is < 1 or > 16 || rate is < 1000 or > 384000 || (format != 1 && format != 3) || bits is not (8 or 16 or 24 or 32))
        {
            return null;
        }

        int bytes = bits / 8, frames = dataLen / (bytes * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = dataAt + (f * channels + c) * bytes;
                sum += (format, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(b, o),
                    (_, 8) => (b[o] - 128) / 128f,
                    (_, 16) => BitConverter.ToInt16(b, o) / 32768f,
                    (_, 24) => ((b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16))) / 8388608f,
                    _ => BitConverter.ToInt32(b, o) / 2147483648f,
                };
            }

            mono[f] = sum / channels;
        }

        return new Pcm(mono, rate, channels, bits);
    }
}

/// <summary>What can be measured from the samples.</summary>
static class Measure
{
    public static JsonObject Of(Pcm p)
    {
        float[] x = p.Mono;
        double seconds = (double)x.Length / p.Rate;
        float peak = 0;
        double energy = 0;
        foreach (float v in x)
        {
            peak = Math.Max(peak, Math.Abs(v));
            energy += v * v;
        }

        double rms = x.Length > 0 ? Math.Sqrt(energy / x.Length) : 0;

        // loudness over 400 ms windows: the loudest one says how loud it gets, ignoring silence around it
        int win = Math.Max(1, p.Rate * 2 / 5);
        double loudest = 0;
        int loudestAt = 0;
        for (int s = 0; s + win <= x.Length || s == 0; s += win / 2)
        {
            double e = 0;
            int n = Math.Min(win, x.Length - s);
            for (int i = 0; i < n; i++)
            {
                e += x[s + i] * x[s + i];
            }

            double r = n > 0 ? Math.Sqrt(e / n) : 0;
            if (r > loudest)
            {
                (loudest, loudestAt) = (r, s);
            }

            if (s + win > x.Length)
            {
                break;
            }
        }

        // attack: from the first sound (above -40 dB of the peak) to 90% of the loudest 10 ms
        int ten = Math.Max(1, p.Rate / 100);
        double[] env = [.. Enumerable.Range(0, Math.Max(1, x.Length / ten)).Select(k => Rms(x, k * ten, ten))];
        double envPeak = env.Max();
        int start = Array.FindIndex(env, e => e >= envPeak * 0.01);
        int top = Array.FindIndex(env, e => e >= envPeak * 0.9);
        double? attack = start >= 0 && top >= start ? (top - start) * 10.0 : null;

        var (pitch, clarity) = Pitch(x, p.Rate, loudestAt, win);
        return new JsonObject
        {
            ["seconds"] = Math.Round(seconds, 3),
            ["rate"] = p.Rate,
            ["channels"] = p.Channels,
            ["bits"] = p.Bits,
            ["peakDb"] = Db(peak),
            ["rmsDb"] = Db(rms),
            ["loudDb"] = Db(loudest),
            ["pitchHz"] = pitch is null ? null : Math.Round(pitch.Value, 1),
            ["pitchClarity"] = Math.Round(clarity, 2),
            ["brightnessHz"] = Math.Round(Centroid(x, p.Rate)),
            ["attackMs"] = attack,
            ["leadMs"] = start >= 0 ? start * 10 : null,
            ["loops"] = Loops(x, p.Rate),
        };
    }

    static double Db(double a) => Math.Round(a > 0 ? Math.Max(-120, 20 * Math.Log10(a)) : -120, 1);

    static double Rms(float[] x, int from, int n)
    {
        double e = 0;
        int end = Math.Min(x.Length, from + n);
        for (int i = from; i < end; i++)
        {
            e += x[i] * x[i];
        }

        return end > from ? Math.Sqrt(e / (end - from)) : 0;
    }

    /// <summary>The fundamental of the loudest stretch by normalised autocorrelation, 50-2000 Hz; null when nothing
    /// repeats clearly (noise, hits, most effects). Clarity is the correlation at that period, 0 to 1.</summary>
    static (double? Hz, double Clarity) Pitch(float[] x, int rate, int at, int win)
    {
        int n = Math.Min(win, Math.Min(4096, x.Length - at));
        if (n < 512)
        {
            return (null, 0);
        }

        int minLag = rate / 2000, maxLag = Math.Min(n / 2, rate / 50);
        double best = 0;
        int bestLag = 0;
        double[] r = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i + lag < n; i++)
            {
                float a = x[at + i], b = x[at + i + lag];
                xy += a * b;
                xx += a * a;
                yy += b * b;
            }

            r[lag] = xx > 0 && yy > 0 ? xy / Math.Sqrt(xx * yy) : 0;
        }

        // the first strong peak, not the highest (octave errors)
        double max = r.Skip(minLag).DefaultIfEmpty(0).Max();
        for (int lag = minLag + 1; lag < maxLag; lag++)
        {
            if (r[lag] >= 0.9 * max && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1])
            {
                (best, bestLag) = (r[lag], lag);
                break;
            }
        }

        return bestLag > 0 && best >= 0.6 ? ((double)rate / bestLag, best) : (null, Math.Max(0, best));
    }

    /// <summary>Spectral centroid in Hz, energy-weighted over up to 64 frames across the sound: how bright it is.</summary>
    static double Centroid(float[] x, int rate)
    {
        const int size = 2048;
        if (x.Length < size)
        {
            return 0;
        }

        int frames = Math.Min(64, x.Length / size), step = (x.Length - size) / Math.Max(1, frames - 1);
        double weighted = 0, total = 0;
        var re = new double[size];
        var im = new double[size];
        for (int f = 0; f < frames; f++)
        {
            int o = f * step;
            for (int i = 0; i < size; i++)
            {
                re[i] = x[o + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)));
                im[i] = 0;
            }

            Fft(re, im);
            for (int k = 1; k < size / 2; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                weighted += mag * k * rate / size;
                total += mag;
            }
        }

        return total > 0 ? weighted / total : 0;
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double a = -2 * Math.PI / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < len / 2; k++)
                {
                    double c = Math.Cos(a * k), s = Math.Sin(a * k);
                    double tr = re[i + k + len / 2] * c - im[i + k + len / 2] * s, ti = re[i + k + len / 2] * s + im[i + k + len / 2] * c;
                    (re[i + k + len / 2], im[i + k + len / 2]) = (re[i + k] - tr, im[i + k] - ti);
                    re[i + k] += tr;
                    im[i + k] += ti;
                }
            }
        }
    }

    /// <summary>Plays on into itself: sound at both ends at a similar level (within 6 dB over 50 ms), and the
    /// last sample close to the first. A one-shot fades out, so it does not.</summary>
    static bool Loops(float[] x, int rate)
    {
        int n = rate / 20;
        if (x.Length < 4 * n)
        {
            return false;
        }

        double head = Rms(x, 0, n), tail = Rms(x, x.Length - n, n);
        if (head < 0.003 || tail < 0.003)
        {
            return false;
        }

        double ratio = 20 * Math.Abs(Math.Log10(head / tail));
        return ratio <= 6 && Math.Abs(x[^1] - x[0]) <= 0.1 + 2 * Math.Max(head, tail) * 0.25;
    }
}
