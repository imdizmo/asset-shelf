namespace AssetShelf;

/// <summary>The library as the app sees it, for the command-line programs (shelf, and the shelf-meaning add-on): its
/// settings, or the team library when a team server is set; and asset ids, &lt;pack id&gt;/&lt;asset path&gt;.</summary>
public static class Shelves
{
    public static Settings? Settings { get; private set; }

    public static async Task<Library> LoadAsync()
    {
        Settings = Settings.Load();
        string packs = Settings.Packs, index = Settings.Index;
        if (Team.Address(Settings) is { } address)
        {
            Team.Me me = await Team.WhoAmIAsync(address);
            if (me.Role is null)
            {
                throw new InvalidOperationException($"Tailscale says you are {me.Login}, not on the team at {address} yet: ask the owner to add you.");
            }

            Team.Join(address, me);
            (packs, index) = await Team.SyncIndexAsync();
        }

        if (!Directory.Exists(index))
        {
            throw new InvalidOperationException($"No library index at {index}: set Packs and Index in Asset Shelf first (or a team server).");
        }

        return Library.Load(packs, index);
    }

    public static string LibraryRoot => Path.GetDirectoryName(Settings!.Index.TrimEnd('/', '\\')) ?? Settings.Index;

    public static string Id(Item i) => $"{i.Pack.Id}/{i.Path}";

    public static Item? Find(Library lib, string id)
    {
        int slash = id.IndexOf('/');
        return slash <= 0 ? null : lib.Items.FirstOrDefault(i => i.Pack.Id == id[..slash] && i.Path == id[(slash + 1)..]);
    }
}
