using System.Text.Json;

namespace AssetShelf;

/// <summary>~/.config/asset-shelf/settings.json: where the packs and the indexes are, and the Unreal project to send to.</summary>
public sealed class Settings
{
    public string Packs { get; set; } = "";
    public string Index { get; set; } = "";
    public string UnrealProject { get; set; } = "";
    public string Tools { get; set; } = "";

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
        Settings s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
        if (s.Tools.Length == 0)
        {
            // the repo's tools/ folder above the build output
            for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            {
                if (File.Exists(Path.Combine(d, "tools", "export_fbx.py")))
                {
                    s.Tools = Path.Combine(d, "tools");
                    break;
                }
            }
        }

        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
