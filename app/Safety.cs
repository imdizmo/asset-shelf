using System.Text.Json;

namespace AssetShelf;

/// <summary>A pack's safety findings (tools/safety_scan.py writes index/&lt;pack&gt;/safety.json): anything that can run
/// code once the pack is in a project. Read when a pack is looked at, then kept.</summary>
public static class Safety
{
    public sealed record Finding(string Level, string Rule, string Path, string Detail);

    private static readonly Dictionary<string, (string Level, List<Finding> Findings)?> Cache = [];

    /// <summary>The pack's level ("danger", "review", "ok") and findings, or null when it was not scanned.</summary>
    public static (string Level, List<Finding> Findings)? Of(Pack pack)
    {
        if (Cache.TryGetValue(pack.Id, out var hit))
        {
            return hit;
        }

        (string, List<Finding>)? r = null;
        string f = System.IO.Path.Combine(pack.IndexDir, "safety.json");
        if (File.Exists(f))
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(f));
                var list = d.RootElement.GetProperty("findings").EnumerateArray().Select(x => new Finding(
                    x.GetProperty("level").GetString() ?? "", x.GetProperty("rule").GetString() ?? "",
                    x.GetProperty("path").GetString() ?? "", x.TryGetProperty("detail", out var dt) ? dt.GetString() ?? "" : "")).ToList();
                r = (d.RootElement.GetProperty("level").GetString() ?? "ok", list);
            }
            catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException)
            {
                Log.Write("safety", $"{pack.Id}: unreadable safety.json ({e.Message})");
            }
        }

        return Cache[pack.Id] = r;
    }

    /// <summary>Lines for the details panel and the send preview; empty when the pack is clean or was not scanned.</summary>
    public static List<(string Text, bool Blocking)> Lines(Pack pack)
    {
        if (Of(pack) is not { } s || s.Level == "ok")
        {
            return [];
        }

        var lines = new List<(string, bool)>
        {
            (s.Level == "danger" ? "This pack has files that run code by themselves once in a project (look before you use them):" : "This pack has code that runs once it is built or enabled:", s.Level == "danger"),
        };
        foreach (var g in s.Findings.GroupBy(f => f.Rule).Take(6))
        {
            Finding first = g.First();
            lines.Add(($"  · {g.Key}: {first.Path}{(g.Count() > 1 ? $" (+{g.Count() - 1} more)" : "")}{(first.Detail.Length > 0 ? $" — {first.Detail}" : "")}", false));
        }

        return lines;
    }
}
