using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetShelf;

/// <summary>Where a pack came from and the right to use it: index/&lt;pack&gt;/provenance.json, with dated copies of
/// its licence text and its receipts in index/&lt;pack&gt;/provenance/. Library data: it never leaves the library.
/// Receipts hold personal and payment details: only "on file" and a date are ever shown, never logged.</summary>
public sealed class Provenance
{
    public int Version { get; set; } = 1;
    public SourceInfo Source { get; set; } = new();
    public LicenceInfo Licence { get; set; } = new();
    public AcquiredInfo Acquired { get; set; } = new();
    public List<Receipt> Receipts { get; set; } = [];

    public sealed class SourceInfo
    {
        public string Site { get; set; } = "";
        public string Url { get; set; } = "";
        public string Creator { get; set; } = "";
    }

    public sealed class LicenceInfo
    {
        public string Name { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Url { get; set; } = "";
        public List<LicenceCopy> Copies { get; set; } = [];
    }

    /// <summary>A saved licence text: a file under the pack's provenance/ folder or the library's licences/ folder.</summary>
    public sealed class LicenceCopy
    {
        public string File { get; set; } = "";
        public string From { get; set; } = "";
        public string Saved { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public sealed class AcquiredInfo
    {
        /// <summary>bought, claimed free, free download, subscription, or unknown.</summary>
        public string How { get; set; } = "unknown";
        public string Date { get; set; } = "";
        public string Account { get; set; } = "";
    }

    public sealed class Receipt
    {
        public string Order { get; set; } = "";
        public string Date { get; set; } = "";
        public string Price { get; set; } = "";
        public string Currency { get; set; } = "";
        public string File { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public const string FileName = "provenance.json";

    /// <summary>The pack's provenance, or null when it has none yet (or the file cannot be read).</summary>
    public static Provenance? Load(string indexDir)
    {
        string path = Path.Combine(indexDir, FileName);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length < 1 << 20 ? JsonSerializer.Deserialize<Provenance>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string indexDir)
    {
        string path = Path.Combine(indexDir, FileName);
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".part", path, true);
    }

    /// <summary>A source page we would open: https only.</summary>
    public static bool IsWebLink(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps;

    // ---- status: what the Origin filter and the markers go by ----

    /// <summary>No source page: nothing says where the pack came from.</summary>
    public static bool UnknownOrigin(Provenance? p) => p is null || !IsWebLink(p.Source.Url);

    /// <summary>No saved licence text that still exists in the library.</summary>
    public static bool LicenceNotSaved(Provenance? p, string indexDir, string libraryRoot) =>
        p is null || !p.Licence.Copies.Any(c => CopyPath(c, indexDir, libraryRoot) is { } f && File.Exists(f));

    /// <summary>Bought, with no receipt on file: the only case marked as a warning (free packs only show in the filter).</summary>
    public static bool BoughtWithoutReceipt(Provenance? p) => p is { Acquired.How: "bought", Receipts.Count: 0 };

    public static bool NoReceipt(Provenance? p) => p is null || p.Receipts.Count == 0;

    /// <summary>A copy's file: under the pack's index folder, or the library's shared licences/ folder; null when the
    /// stored path would leave both (the file is library data, but checked like any other input).</summary>
    public static string? CopyPath(LicenceCopy c, string indexDir, string libraryRoot)
    {
        foreach (string root in new[] { indexDir, libraryRoot })
        {
            string full = Path.GetFullPath(Path.Combine(root, c.File));
            if (full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    // ---- saving licence text ----

    public const int MaxText = 256 * 1024;

    /// <summary>Saves licence text as a dated plain-text copy in the pack's provenance/ folder and records it. The text
    /// comes from a listing or a pack file: untrusted, kept as text only and capped.</summary>
    public LicenceCopy SaveLicenceText(string indexDir, string text, string from, string slug)
    {
        string clean = Untrusted.Text(text, MaxText);
        string date = DateTime.Now.ToString("yyyy-MM-dd");
        string rel = Path.Combine("provenance", $"licence-{date}-{Untrusted.Slug(slug)}.txt");
        string full = Path.Combine(indexDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, clean, new UTF8Encoding(false));
        var copy = new LicenceCopy { File = rel, From = from, Saved = date, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clean))) };
        Licence.Copies.Add(copy);
        return copy;
    }

    // ---- receipts ----

    /// <summary>The library's receipts/inbox/: receipt files with a JSON sidecar each, waiting to be given to a pack.</summary>
    public static string InboxDir(string libraryRoot) => Path.Combine(libraryRoot, "receipts", "inbox");

    /// <summary>The inbox's receipts: (sidecar path, its data). Unreadable or oversized sidecars are skipped.</summary>
    public static List<(string Sidecar, Receipt Receipt, string Store, string Items)> Inbox(string libraryRoot)
    {
        var list = new List<(string, Receipt, string, string)>();
        string dir = InboxDir(libraryRoot);
        if (!Directory.Exists(dir))
        {
            return list;
        }

        foreach (string side in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (new FileInfo(side).Length > 64 * 1024)
                {
                    continue;
                }

                using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(side));
                JsonElement r = d.RootElement;
                string S(string k) => r.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? Untrusted.Text(v.GetString()!, 200) : "";
                string items = r.TryGetProperty("items", out JsonElement it) && it.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", it.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Untrusted.Text(x.GetString()!, 120)).Take(8))
                    : "";
                list.Add((side, new Receipt { Order = S("order"), Date = S("date"), Price = S("price"), Currency = S("currency") }, S("store"), items));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // a sidecar that cannot be read stays in the inbox
            }
        }

        return list;
    }

    /// <summary>Moves an inbox receipt (its sidecar and every file with the same name) into the pack's
    /// provenance/receipts/ and records it.</summary>
    public void AttachFromInbox(string indexDir, string sidecar, Receipt data)
    {
        string dir = Path.Combine(indexDir, "provenance", "receipts");
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(sidecar);
        string? main = null;
        // the files that share the sidecar's name exactly (no pattern built from a file name; links skipped)
        foreach (string f in Directory.EnumerateFiles(Path.GetDirectoryName(sidecar)!)
                     .Where(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.Ordinal) && new FileInfo(f).LinkTarget is null))
        {
            string to = Path.Combine(dir, Path.GetFileName(f));
            File.Move(f, to, false);
            if (!f.Equals(sidecar, StringComparison.Ordinal) && main is null)
            {
                main = to;
            }
        }

        Receipts.Add(new Receipt
        {
            Order = data.Order, Date = data.Date, Price = data.Price, Currency = data.Currency,
            File = Path.GetRelativePath(indexDir, main ?? Path.Combine(dir, Path.GetFileName(sidecar))),
        });
    }

    /// <summary>A receipt file picked by hand: copied into provenance/receipts/ under its date and order.</summary>
    public void AttachFile(string indexDir, string file, Receipt data)
    {
        string dir = Path.Combine(indexDir, "provenance", "receipts");
        Directory.CreateDirectory(dir);
        string name = $"{(data.Date.Length > 0 ? data.Date : DateTime.Now.ToString("yyyy-MM-dd"))}_{Untrusted.Slug(data.Order.Length > 0 ? data.Order : Path.GetFileNameWithoutExtension(file))}{Path.GetExtension(file).ToLowerInvariant()}";
        string to = Path.Combine(dir, name);
        File.Copy(file, to, false);
        data.File = Path.GetRelativePath(indexDir, to);
        Receipts.Add(data);
    }

    // ---- the line written into a project on send ----

    /// <summary>One JSON line for a project's AssetShelf.provenance.jsonl: what was sent, from where, under which
    /// licence. No account names, prices or receipt contents.</summary>
    public static string Line(Item item, Provenance? p, string target) => JsonSerializer.Serialize(new
    {
        asset = item.Path.Length > 0 ? item.Path : item.Name,
        name = item.Name,
        pack = item.Pack.Title,
        source = p?.Source.Url is { Length: > 0 } u ? u : item.Pack.Url,
        site = p?.Source.Site ?? "",
        creator = p?.Source.Creator is { Length: > 0 } c ? c : item.Pack.Seller,
        licence = p is null ? item.Pack.Licence : string.Join(" ", new[] { p.Licence.Name, p.Licence.Tier }.Where(x => x.Length > 0)),
        licenceSaved = p?.Licence.Copies.LastOrDefault()?.Saved ?? "",
        acquired = p?.Acquired.Date ?? item.Pack.Acquired,
        sent = DateTime.Now.ToString("yyyy-MM-dd"),
        to = target,
    });
}

/// <summary>Text from someone else (a listing, a licence file, a receipt sidecar): plain text only, capped.</summary>
public static class Untrusted
{
    /// <summary>Control characters (but tabs and line ends) and bidi/zero-width formatting dropped, cut to max characters.</summary>
    public static string Text(string s, int max)
    {
        var b = new StringBuilder(Math.Min(s.Length, max));
        foreach (char c in s)
        {
            if (b.Length >= max)
            {
                break;
            }

            var cat = char.GetUnicodeCategory(c);
            if (c is '\n' or '\t' || (cat is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)))
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }

    /// <summary>A file-name slug: letters, digits and dashes, at most 60.</summary>
    public static string Slug(string s)
    {
        string t = new(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (t.Contains("--", StringComparison.Ordinal)) t = t.Replace("--", "-", StringComparison.Ordinal);
        t = t.Trim('-');
        return t.Length == 0 ? "licence" : t[..Math.Min(60, t.Length)];
    }
}
