using System.Text.Json;

namespace AssetShelf;

/// <summary>What the user keeps: favourite assets, named collections, a note per asset and saved filters ("smart
/// folders"). Stored in collections.json beside settings.json, written whole after each change.</summary>
public sealed class Collections
{
    public HashSet<string> Favourites { get; set; } = [];
    public SortedDictionary<string, HashSet<string>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Notes { get; set; } = [];
    public SortedDictionary<string, SavedView> Saved { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A filter to come back to: the search, the kind tab, the tags, the packs and licences turned off.</summary>
    public sealed record SavedView(string Search, string? Kind, string[] TagOn, string[] PackOff, string[] LicenceOff, string? Collection);

    public const string FavouritesName = "Favourites";

    public static string FilePath => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "collections.json");

    /// <summary>An asset's key: its pack and its path (the same /Game path can be in several packs).</summary>
    public static string Key(Item i) => $"{i.Pack.Id}|{i.Path}";

    public static Collections Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Collections>(File.ReadAllBytes(FilePath)) ?? new() : new();
        }
        catch (JsonException e)
        {
            Log.Write("app", $"collections.json unreadable, starting empty (kept as .bad): {e.Message}");
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath + ".part", JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".part", FilePath, overwrite: true);
    }

    /// <summary>The keys in a collection by name (Favourites included), or null.</summary>
    public HashSet<string>? Members(string name) => name == FavouritesName ? Favourites : Lists.GetValueOrDefault(name);
}
