using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetShelf;

/// <summary>~/.config/asset-shelf/settings.json: where the packs and the indexes are, and the Unreal project to send to.</summary>
public sealed class Settings
{
    public string Packs { get; set; } = "";
    public string Index { get; set; } = "";
    public string UnrealProject { get; set; } = "";

    /// <summary>The split Send button's main target: Unreal, Blender, Unity or Groundwork (the last one used).</summary>
    public string LastSend { get; set; } = "Unreal";

    /// <summary>The inspector's width (240 to 520).</summary>
    public double InspectorWidth { get; set; } = 320;

    /// <summary>The Unreal Engine folder that exports assets; empty: found (see <see cref="UnrealSetup"/>).</summary>
    public string UnrealEngine { get; set; } = "";

    /// <summary>A library shared from a file server (\\server\share\folder) and the account used there; empty: a local
    /// folder. See <see cref="Shared"/>.</summary>
    public string Share { get; set; } = "";

    public string ShareUser { get; set; } = "";

    /// <summary>The tools/ folder beside the app (a release) or above the build output (the repo). Found on every start
    /// and never saved: an AppImage runs from a mount point that changes each time, and a "Tools" entry an older version
    /// saved in settings.json could point at an old copy.</summary>
    [JsonIgnore]
    public string Tools => FoundTools.Value;

    private static readonly Lazy<string> FoundTools = new(() =>
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
        {
            if (File.Exists(Path.Combine(d, "tools", "export_fbx.py")))
            {
                return Path.Combine(d, "tools");
            }
        }

        return "";
    });

    /// <summary>Blender's executable; empty: "blender" on the PATH.</summary>
    public string Blender { get; set; } = "";

    /// <summary>The Unity project to send to (the folder holding Assets/).</summary>
    public string UnityProject { get; set; } = "";

    /// <summary>Where in the Unity project assets go, relative to its folder.</summary>
    public string UnityFolder { get; set; } = "Assets/AssetShelf";

    /// <summary>False: &lt;folder&gt;/&lt;pack&gt;/&lt;asset&gt;/. True: the pack's own folders under &lt;folder&gt;.</summary>
    public bool UnityMirrorPack { get; set; }

    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "asset-shelf", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
