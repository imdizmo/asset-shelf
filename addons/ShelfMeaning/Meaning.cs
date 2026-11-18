// Search by meaning: each asset's preview picture becomes a vector (Google's SigLIP 2, base, 224 px, run with ONNX
// Runtime on the CPU), kept per pack beside its index; a typed description becomes a vector the same way, and the
// assets whose pictures are closest come first. The model (about 1.5 GB) is downloaded once, checked by SHA-256.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SkiaSharp;

namespace AssetShelf;

public static class Meaning
{
    public const string Model = "siglip2-base-patch16-224";
    private const string Repo = "onnx-community/siglip2-base-patch16-224-ONNX";
    private const string Revision = "ba1f3b0843f24bc5417d38e19c37b287d719b2f4";
    private const int Side = 224, Dim = 768, TextLength = 64;

    private static readonly (string File, long Size, string Sha256)[] Files =
    [
        ("onnx/vision_model.onnx", 371807752, "c0573e3f4140c3a7c4e9cc5912bd6b26a033b46a6a8e8af26cbea262b163bcad"),
        ("onnx/text_model.onnx", 1129469657, "baf12d941beabafafb14f7b4adb38dc15be18681b964a84410ec53d9d65e6293"),
        ("tokenizer.model", 4241003, "61a7b147390c64585d6c3543dd6fc636906c9af3865a5548f27f31aee1d4c8e2"),
    ];

    private static string ModelDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "asset-shelf", "models", Model);

    /// <summary>Downloads the model files that are missing, each checked by size and SHA-256 before it is kept.</summary>
    public static async Task EnsureModelAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        foreach (var (file, size, sha) in Files)
        {
            string dest = Path.Combine(ModelDir, file);
            if (File.Exists(dest) && new FileInfo(dest).Length == size)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Console.Error.WriteLine($"Downloading {file} ({size >> 20} MB, once)…");
            string part = dest + ".part";
            using (HttpResponseMessage r = await http.GetAsync($"https://huggingface.co/{Repo}/resolve/{Revision}/{file}", HttpCompletionOption.ResponseHeadersRead))
            {
                r.EnsureSuccessStatusCode();
                await using FileStream f = File.Create(part);
                await r.Content.CopyToAsync(f);
            }

            await using (FileStream check = File.OpenRead(part))
            {
                if (check.Length != size || Convert.ToHexStringLower(await SHA256.HashDataAsync(check)) != sha)
                {
                    File.Delete(part);
                    throw new InvalidOperationException($"{file} did not match its checksum; nothing was kept. Try again.");
                }
            }

            File.Move(part, dest, overwrite: true);
        }
    }

    private static SessionOptions Options() => new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

    // ---- pictures ----

    /// <summary>The vector file of a pack (float16, Dim per asset) and its list of asset paths in the same order.</summary>
    private static (string Vectors, string Items) Store(Pack pack) =>
        (Path.Combine(pack.IndexDir, $"meaning-{Model}.f16"), Path.Combine(pack.IndexDir, $"meaning-{Model}.json"));

    /// <summary>Gives every asset of these packs with a picture a vector. A pack already done for the same assets is
    /// skipped. Returns how many pictures were read.</summary>
    public static async Task<int> EmbedAsync(IEnumerable<Pack> packs, IReadOnlyList<Item> items)
    {
        await EnsureModelAsync();
        using var vision = new InferenceSession(Path.Combine(ModelDir, "onnx", "vision_model.onnx"), Options());
        string input = vision.InputMetadata.Keys.First();
        int total = 0;
        foreach (Pack pack in packs)
        {
            List<Item> withPicture = [.. items.Where(i => i.Pack == pack && i.Thumb is not null && File.Exists(i.Thumb)).OrderBy(i => i.Path, StringComparer.Ordinal)];
            var (vecFile, listFile) = Store(pack);
            string[] paths = [.. withPicture.Select(i => i.Path)];
            if (File.Exists(listFile) && File.Exists(vecFile) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(listFile)) is { } done && done.SequenceEqual(paths))
            {
                continue;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var vectors = new byte[paths.Length * Dim * 2];
            const int batch = 16;
            for (int s = 0; s < withPicture.Count; s += batch)
            {
                List<Item> part = withPicture.GetRange(s, Math.Min(batch, withPicture.Count - s));
                var pixels = new DenseTensor<float>([part.Count, 3, Side, Side]);
                for (int b = 0; b < part.Count; b++)
                {
                    Pixels(part[b].Thumb!, pixels, b);
                }

                using var results = vision.Run([NamedOnnxValue.CreateFromTensor(input, pixels)]);
                Tensor<float> output = Pooled(results);
                for (int b = 0; b < part.Count; b++)
                {
                    WriteNormalised(output, b, vectors.AsSpan((s + b) * Dim * 2, Dim * 2));
                }
            }

            // written whole, then renamed: a pack is either done or not
            await File.WriteAllBytesAsync(vecFile + ".tmp", vectors);
            await File.WriteAllTextAsync(listFile + ".tmp", JsonSerializer.Serialize(paths));
            File.Move(vecFile + ".tmp", vecFile, overwrite: true);
            File.Move(listFile + ".tmp", listFile, overwrite: true);
            total += paths.Length;
            Console.Error.WriteLine($"{pack.Id}: {paths.Length} pictures in {watch.Elapsed.TotalSeconds:0.0} s");
        }

        return total;
    }

    /// <summary>The model's pooled output ("pooler_output", or the second output when named otherwise).</summary>
    private static Tensor<float> Pooled(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results) =>
        (results.FirstOrDefault(r => r.Name == "pooler_output") ?? results.ElementAt(Math.Min(1, results.Count - 1))).AsTensor<float>();

    /// <summary>A picture as SigLIP wants it: squeezed to 224×224 (bilinear), RGB scaled to -1..1. Transparency is laid
    /// on mid grey; a picture that cannot be read stays grey.</summary>
    private static void Pixels(string file, DenseTensor<float> t, int b)
    {
        using var canvas = new SKBitmap(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(canvas))
        {
            c.Clear(new SKColor(128, 128, 128));
            using SKBitmap? src = SKBitmap.Decode(file);
            if (src is not null && src.Width <= 8192 && src.Height <= 8192)
            {
                using SKImage image = SKImage.FromBitmap(src);
                c.DrawImage(image, new SKRect(0, 0, Side, Side), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
        }

        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                SKColor p = canvas.GetPixel(x, y);
                t[b, 0, y, x] = p.Red / 127.5f - 1f;
                t[b, 1, y, x] = p.Green / 127.5f - 1f;
                t[b, 2, y, x] = p.Blue / 127.5f - 1f;
            }
        }
    }

    private static void WriteNormalised(Tensor<float> output, int row, Span<byte> dest)
    {
        double norm = 0;
        for (int d = 0; d < Dim; d++)
        {
            norm += output[row, d] * output[row, d];
        }

        float inv = (float)(1 / Math.Sqrt(Math.Max(norm, 1e-12)));
        for (int d = 0; d < Dim; d++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(dest.Slice(d * 2, 2), (Half)(output[row, d] * inv));
        }
    }

    // ---- words ----

    /// <summary>A description as a vector: lower-cased (SigLIP 2 was trained so), Gemma's tokens with the end mark,
    /// padded to 64.</summary>
    public static async Task<float[]> TextAsync(string text)
    {
        await EnsureModelAsync();
        await using FileStream model = File.OpenRead(Path.Combine(ModelDir, "tokenizer.model"));
        var tokenizer = LlamaTokenizer.Create(model, addBeginOfSentence: false, addEndOfSentence: true);
        long[] ids = [.. tokenizer.EncodeToIds(text.ToLowerInvariant()).Take(TextLength - 1).Select(i => (long)i)];
        if (ids.Length == 0 || ids[^1] != 1)
        {
            ids = [.. ids, 1];  // Gemma's end of sentence
        }

        var input = new DenseTensor<long>([1, TextLength]);
        for (int i = 0; i < ids.Length && i < TextLength; i++)
        {
            input[0, i] = ids[i];
        }

        using var session = new InferenceSession(Path.Combine(ModelDir, "onnx", "text_model.onnx"), Options());
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
        Tensor<float> output = Pooled(results);
        var v = new float[Dim];
        double norm = 0;
        for (int d = 0; d < Dim; d++)
        {
            v[d] = output[0, d];
            norm += v[d] * v[d];
        }

        float inv = (float)(1 / Math.Sqrt(Math.Max(norm, 1e-12)));
        for (int d = 0; d < Dim; d++)
        {
            v[d] *= inv;
        }

        return v;
    }

    /// <summary>The token ids a description becomes (for the check against the reference tokenizer).</summary>
    public static async Task<int[]> TokensAsync(string text)
    {
        await EnsureModelAsync();
        await using FileStream model = File.OpenRead(Path.Combine(ModelDir, "tokenizer.model"));
        return [.. LlamaTokenizer.Create(model, addBeginOfSentence: false, addEndOfSentence: true).EncodeToIds(text.ToLowerInvariant())];
    }

    // ---- ranking ----

    /// <summary>The stored vector of one asset, or null when its pack has none for it.</summary>
    public static float[]? VectorOf(Item item)
    {
        var (vecFile, listFile) = Store(item.Pack);
        string[] paths = File.Exists(listFile) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(listFile)) ?? [] : [];
        int i = Array.IndexOf(paths, item.Path);
        if (i < 0)
        {
            return null;
        }

        using FileStream f = File.OpenRead(vecFile);
        var buf = new byte[Dim * 2];
        f.Position = (long)i * Dim * 2;
        f.ReadExactly(buf);
        return [.. Enumerable.Range(0, Dim).Select(d => (float)BinaryPrimitives.ReadHalfLittleEndian(buf.AsSpan(d * 2, 2)))];
    }

    /// <summary>Assets ranked by how close their picture is to <paramref name="q"/> (cosine), best first, among the
    /// packs that have vectors. Reads one pack's file at a time.</summary>
    public static List<(Item Item, float Score)> Rank(Library lib, float[] q, Func<Item, bool> keep, int limit)
    {
        var best = new PriorityQueue<(Item, float), float>();
        var byPath = lib.Items.GroupBy(i => i.Pack).ToDictionary(g => g.Key, g => g.ToDictionary(i => i.Path, StringComparer.Ordinal));
        int packs = 0;
        foreach (Pack pack in lib.Packs)
        {
            var (vecFile, listFile) = Store(pack);
            if (!File.Exists(listFile) || !File.Exists(vecFile) || !byPath.TryGetValue(pack, out var items))
            {
                continue;
            }

            packs++;
            string[] paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(listFile)) ?? [];
            byte[] vectors = File.ReadAllBytes(vecFile);
            for (int i = 0; i < paths.Length && (i + 1) * Dim * 2 <= vectors.Length; i++)
            {
                if (!items.TryGetValue(paths[i], out Item? item) || !keep(item))
                {
                    continue;
                }

                float score = 0;
                ReadOnlySpan<byte> v = vectors.AsSpan(i * Dim * 2, Dim * 2);
                for (int d = 0; d < Dim; d++)
                {
                    score += (float)BinaryPrimitives.ReadHalfLittleEndian(v.Slice(d * 2, 2)) * q[d];
                }

                best.Enqueue((item, score), score);
                if (best.Count > limit)
                {
                    best.Dequeue();
                }
            }
        }

        if (packs == 0)
        {
            throw new InvalidOperationException("No pack has meaning vectors yet: run `shelf-meaning embed --pack <id>` first.");
        }

        var list = new List<(Item, float)>();
        while (best.TryDequeue(out var e, out _))
        {
            list.Add(e);
        }

        list.Reverse();
        return list;
    }
}
