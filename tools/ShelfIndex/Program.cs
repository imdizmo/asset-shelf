// ShelfIndex: indexes an Unreal asset pack without Unreal. Reads the start of each editor package (.uasset/.umap) with
// CUE4Parse and writes <out>/index.json and <out>/thumbs/ in the layout the app reads.
//
//     ShelfIndex --pack <pack>/unreal --out <dir>
//
// Per asset: path, package, name, class, the Content Browser's details (the package's asset-registry block), the
// packages it needs (hard imports) and the ones that need it (within the pack), and the picture saved in the package.
// Where a package has no saved picture but an earlier rendering pass left one in <out>/thumbs, that one is kept.
// The files come from third-party sellers: every offset, count and length read from them is bounded.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;

string? pack = null, outDir = null;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--pack": pack = args[i + 1]; break;
        case "--out": outDir = args[i + 1]; break;
    }
}

if (pack is null || outDir is null)
{
    Console.Error.WriteLine("usage: ShelfIndex --pack <pack>/unreal --out <dir>");
    return 2;
}

string content = Path.Combine(pack, "Content");
if (!Directory.Exists(content))
{
    Console.Error.WriteLine("no Content folder in " + pack);
    return 1;
}

Directory.CreateDirectory(Path.Combine(outDir, "thumbs"));
var watch = Stopwatch.StartNew();
var files = Directory.EnumerateFiles(content, "*.*", SearchOption.AllDirectories)
    .Where(f => (f.EndsWith(".uasset", StringComparison.Ordinal) || f.EndsWith(".umap", StringComparison.Ordinal))
        && !f.Contains("/__External", StringComparison.Ordinal))
    .Order(StringComparer.Ordinal).ToList();
var results = new ConcurrentBag<Indexer.Asset>();
var failures = new ConcurrentBag<string>();
var engines = new ConcurrentBag<string>();
// the index is usually on a network share: several packages read at once hide the round trips
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 16 }, f =>
{
    try
    {
        foreach (Indexer.Asset a in Indexer.Read(f, content, outDir, engines))
        {
            results.Add(a);
        }
    }
    catch (Exception e)
    {
        failures.Add($"{Path.GetRelativePath(content, f)}: {e.GetType().Name}: {e.Message}");
    }
});

// who needs whom, within the pack
var assets = results.OrderBy(a => a.Package, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
var refs = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
foreach (Indexer.Asset a in assets)
{
    foreach (string d in a.Deps)
    {
        if (!refs.TryGetValue(d, out var set))
        {
            refs[d] = set = new SortedSet<string>(StringComparer.Ordinal);
        }

        set.Add(a.Package);
    }
}

using (var stream = File.Create(Path.Combine(outDir, "index.json.part")))
using (var w = new Utf8JsonWriter(stream))
{
    w.WriteStartObject();
    // the newest engine that saved any of its packages: a project older than that cannot open the pack
    w.WriteString("engine", engines.Distinct().OrderByDescending(Indexer.VersionKey).FirstOrDefault() ?? "");
    w.WriteString("reader", "ShelfIndex");
    w.WriteStartArray("assets");
    foreach (Indexer.Asset a in assets)
    {
        w.WriteStartObject();
        w.WriteString("path", a.Path);
        w.WriteString("package", a.Package);
        w.WriteString("name", a.Name);
        w.WriteString("class", a.Class);
        w.WriteStartObject("tags");
        foreach (var (k, v) in a.Tags)
        {
            w.WriteString(k, v);
        }

        w.WriteEndObject();
        w.WriteStartArray("deps");
        foreach (string d in a.Deps)
        {
            w.WriteStringValue(d);
        }

        w.WriteEndArray();
        w.WriteStartArray("soft");
        w.WriteEndArray();
        w.WriteStartArray("refs");
        foreach (string r in refs.GetValueOrDefault(a.Package) ?? [])
        {
            if (r != a.Package)
            {
                w.WriteStringValue(r);
            }
        }

        w.WriteEndArray();
        if (a.Thumb is not null)
        {
            w.WriteString("thumb", a.Thumb);
            w.WriteString("thumbsrc", a.ThumbSource);
        }

        w.WriteEndObject();
    }

    w.WriteEndArray();
    w.WriteEndObject();
}

File.Move(Path.Combine(outDir, "index.json.part"), Path.Combine(outDir, "index.json"), overwrite: true);
int pictures = assets.Count(a => a.Thumb is not null);
Console.WriteLine($"SHELFINDEX DONE {files.Count} packages, {assets.Count} assets, {pictures} pictures, {failures.Count} failed, {watch.Elapsed.TotalSeconds:0.0}s");
foreach (string f in failures.Take(10))
{
    Console.WriteLine("  failed " + f);
}

return 0;

/// <summary>Reads one package's header: its assets, their details and dependencies, and its saved pictures.</summary>
internal static class Indexer
{
    private const int Probe = 4096;             // enough for the summary's fixed start, where the header size is
    private const int MaxHeader = 256 << 20;    // a header larger than this is refused
    private const int MaxString = 1 << 16;
    private const int MaxImage = 16 << 20;

    public sealed record Asset(string Path, string Package, string Name, string Class, Dictionary<string, string> Tags, List<string> Deps, string? Thumb, string? ThumbSource);

    public static List<Asset> Read(string file, string content, string outDir, ConcurrentBag<string> engines)
    {
        string rel = System.IO.Path.GetRelativePath(content, file).Replace('\\', '/');
        string package = "/Game/" + rel[..rel.LastIndexOf('.')];
        string shortName = package[(package.LastIndexOf('/') + 1)..];
        // read only the header (names, imports, exports, asset-registry block, saved pictures), not the bulk data
        // after it: on a network share that is most of the time
        byte[] bytes;
        using (var f = File.OpenRead(file))
        {
            byte[] probe = new byte[(int)Math.Min(f.Length, Probe)];
            f.ReadExactly(probe);
            int header = HeaderSize(probe);
            if (header < 0)
            {
                // ponytail: a summary longer than the probe; read 1 MB and let the package reader find the rest
                header = (int)Math.Min(f.Length, 1 << 20);
            }

            if (header <= probe.Length || header > f.Length)
            {
                bytes = probe;
            }
            else
            {
                bytes = new byte[header];
                probe.CopyTo(bytes, 0);
                f.ReadExactly(bytes, probe.Length, header - probe.Length);
            }
        }

        var pkg = new Package(shortName, bytes, null);

        if (pkg.Summary.SavedByEngineVersion is { } v && v.ToString() is { Length: > 0 } ev && !ev.StartsWith("0.", StringComparison.Ordinal))
        {
            engines.Add(ev);
        }

        var deps = pkg.ImportMap.Where(i => i.ClassName.Text == "Package" && !i.ObjectName.Text.StartsWith("/Script/", StringComparison.Ordinal))
            .Select(i => i.ObjectName.Text).Where(d => d != package).Distinct().Order(StringComparer.Ordinal).ToList();
        var tags = AssetTags(bytes, pkg.Summary.AssetRegistryDataOffset, pkg.Summary.FileVersionUE.FileVersionUE5 > 0);
        var pictures = Thumbnails(bytes, pkg.Summary.ThumbnailTableOffset);
        if (tags.Count == 0)
        {
            // no asset-registry block: the export named like the package is the asset
            var main = pkg.ExportMap.FirstOrDefault(e => e?.OuterIndex is { IsNull: true } && e.ObjectName.Text == shortName);
            if (main is not null && main.ClassIndex.IsImport && -main.ClassIndex.Index - 1 < pkg.ImportMap.Length)
            {
                tags.Add((shortName, pkg.ImportMap[-main.ClassIndex.Index - 1].ObjectName.Text, []));
            }
        }

        var list = new List<Asset>();
        foreach (var (obj, cls, kv) in tags)
        {
            if (cls.EndsWith("GeneratedClass", StringComparison.Ordinal))
            {
                continue;  // a blueprint's compiled class: the blueprint itself is the asset (as in Unreal's registry)
            }

            string name = obj.Contains('.') ? obj[(obj.LastIndexOf('.') + 1)..] : obj;
            string path = $"{package}.{name}";
            string file2 = Md5(path);
            string? thumb = null, src = null;
            if (pictures.TryGetValue(name, out var img))
            {
                thumb = $"thumbs/{file2}{img.Ext}";
                File.WriteAllBytes(System.IO.Path.Combine(outDir, thumb), img.Data);
                src = "embedded";
                string old = System.IO.Path.Combine(outDir, "thumbs", file2 + ".png");
                if (img.Ext != ".png" && File.Exists(old))
                {
                    File.Delete(old);  // an earlier pass's copy of the same picture
                }
            }
            else if (File.Exists(System.IO.Path.Combine(outDir, "thumbs", file2 + ".png")))
            {
                thumb = $"thumbs/{file2}.png";
                src = "rendered";
            }

            list.Add(new Asset(path, package, name, ShortClass(cls), kv, deps, thumb, src));
        }

        return list;
    }

    /// <summary>"5.4.4-35576357+++UE5+Release-5.4" sorts by 5, 4, 4.</summary>
    public static (int, int, int) VersionKey(string v)
    {
        var parts = v.Split('-')[0].Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).Concat([0, 0, 0]).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    private static string ShortClass(string cls) => cls.Contains('.') ? cls[(cls.LastIndexOf('.') + 1)..] : cls;

    // the same file name the Unreal indexer used (MD5 of the object path), so its renders are found and replaced
    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant();

    /// <summary>TotalHeaderSize, from the package summary at the start of the file (CUE4Parse reads every format
    /// version of it). The probe may be too short for a long summary: then the whole header is read another way.</summary>
    internal static int HeaderSize(byte[] b)
    {
        try
        {
            int size = new FPackageFileSummary(new CUE4Parse.UE4.Readers.FByteArchive("summary", b)).TotalHeaderSize;
            return size > 0 && size <= MaxHeader ? size : throw new InvalidDataException("header size " + size);
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>The asset-registry block: (UE5: an int64 offset), object count; per object its path, class, tag count
    /// and the tags as key/value strings.</summary>
    private static List<(string Obj, string Cls, Dictionary<string, string> Tags)> AssetTags(byte[] b, int offset, bool ue5)
    {
        var all = new List<(string, string, Dictionary<string, string>)>();
        if (offset <= 0 || offset >= b.Length)
        {
            return all;
        }

        try
        {
            using var r = new BinaryReader(new MemoryStream(b, writable: false));
            r.BaseStream.Position = offset;
            if (ue5)
            {
                r.ReadInt64();
            }

            int count = r.ReadInt32();
            for (int i = 0; i < count && i < 1024; i++)
            {
                string obj = FString(r), cls = FString(r);
                int n = r.ReadInt32();
                var kv = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int t = 0; t < n && t < 1024; t++)
                {
                    string k = FString(r), v = FString(r);
                    if (v.Length <= 200)
                    {
                        kv[k] = v;
                    }
                }

                all.Add((obj, cls, kv));
            }
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            // ponytail: a block we cannot read keeps what was read before it
        }

        return all;
    }

    /// <summary>The thumbnail table: count; per entry class, object path (without the package), file offset. At the
    /// offset: width, height (negative: JPEG), then the compressed image as a byte array.</summary>
    private static Dictionary<string, (byte[] Data, string Ext)> Thumbnails(byte[] b, int offset)
    {
        var pics = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        if (offset <= 0 || offset >= b.Length)
        {
            return pics;
        }

        try
        {
            using var r = new BinaryReader(new MemoryStream(b, writable: false));
            r.BaseStream.Position = offset;
            int count = r.ReadInt32();
            for (int i = 0; i < count && i < 1024; i++)
            {
                FString(r);
                string obj = FString(r);
                int at = r.ReadInt32();
                long back = r.BaseStream.Position;
                if (at > 0 && at + 12 <= b.Length)
                {
                    r.BaseStream.Position = at;
                    int w = r.ReadInt32(), h = r.ReadInt32(), size = r.ReadInt32();
                    if (w > 0 && w <= 4096 && Math.Abs(h) <= 4096 && size > 0 && size <= MaxImage && at + 12L + size <= b.Length)
                    {
                        byte[] img = r.ReadBytes(size);
                        bool jpeg = img.Length > 2 && img[0] == 0xFF && img[1] == 0xD8;
                        bool png = img.Length > 4 && img[0] == 0x89 && img[1] == (byte)'P';
                        // the size the image itself declares, not the table's: a small file can claim a huge picture
                        if ((jpeg || png) && ImageSize(img, png) is { } dim && dim.W is > 0 and <= 4096 && dim.H is > 0 and <= 4096)
                        {
                            pics[obj[(obj.LastIndexOf('.') + 1)..]] = (img, jpeg ? ".jpg" : ".png");
                        }
                    }
                }

                r.BaseStream.Position = back;
            }
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            // ponytail: a table we cannot read keeps the pictures read before it
        }

        return pics;
    }

    /// <summary>Width and height from a PNG's IHDR or a JPEG's first SOF marker; null when not found.</summary>
    internal static (int W, int H)? ImageSize(byte[] b, bool png)
    {
        if (png)
        {
            return b.Length >= 24 ? (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19], b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]) : null;
        }

        // markers the way libjpeg walks them: fill bytes (FF FF ...) skipped, standalone markers (01, D0-D9) carry no
        // length, every other segment's length is at least 2; the first frame header (SOF) gives the size
        int i = 2;
        while (i < b.Length)
        {
            if (b[i] != 0xFF)
            {
                return null;
            }

            while (i < b.Length && b[i] == 0xFF)
            {
                i++;
            }

            if (i >= b.Length)
            {
                return null;
            }

            int marker = b[i++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                if (marker == 0xD9)
                {
                    return null;  // end of image before any frame header
                }

                continue;
            }

            if (i + 2 > b.Length)
            {
                return null;
            }

            int len = b[i] << 8 | b[i + 1];
            if (len < 2 || i + len > b.Length)
            {
                return null;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return len >= 7 ? (b[i + 5] << 8 | b[i + 6], b[i + 3] << 8 | b[i + 4]) : null;
            }

            i += len;
        }

        return null;
    }

    private static string FString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0)
        {
            return "";
        }

        if (len == int.MinValue || Math.Abs(len) > MaxString)
        {
            throw new InvalidDataException("string length " + len);
        }

        return len > 0
            ? Encoding.Latin1.GetString(r.ReadBytes(len)).TrimEnd('\0')
            : Encoding.Unicode.GetString(r.ReadBytes(-len * 2)).TrimEnd('\0');
    }
}
