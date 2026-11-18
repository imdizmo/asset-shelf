// shelf-meaning: search Asset Shelf's library by meaning. `embed` gives each asset's picture a vector (once per pack,
// kept beside its index); `query` and `like` rank assets by closeness. `shelf search --meaning` calls this program
// when it is installed; Asset Shelf itself never needs it.
using System.Text.Json;

namespace AssetShelf;

public static class MeaningCli
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["embed", .. var rest] => await Embed(rest),
                ["query", var text, .. var rest] => await Query(rest, await Meaning.TextAsync(text)),
                ["like", var id, .. var rest] => await Like(id, rest),
                ["tokens", var text] => Print(string.Join(' ', await Meaning.TokensAsync(text))),
                ["--version"] => Print(typeof(MeaningCli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
                _ => Usage(),
            };
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"shelf-meaning: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              shelf-meaning embed --pack <pack id> [--pack ...]   reads the packs' pictures once (--all: every pack, slow)
              shelf-meaning query "<description>" [--limit 100]   assets by how well their picture fits, as JSON
              shelf-meaning like <asset id> [--limit 100]         assets whose picture looks like this one's, as JSON
            The model (Google's SigLIP 2, about 1.5 GB) is downloaded on first use and checked by SHA-256.
            """);
        return 2;
    }

    private static int Print(string s)
    {
        Console.WriteLine(s);
        return 0;
    }

    private static int Limit(string[] args) =>
        Array.IndexOf(args, "--limit") is var i and >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? Math.Clamp(n, 1, 2000) : 100;

    private static async Task<int> Embed(string[] args)
    {
        Library lib = await Shelves.LoadAsync();
        if (Team.Base is not null)
        {
            throw new InvalidOperationException("The team library is read by its owner, on the machine that holds it.");
        }

        var ids = args.Select((a, i) => (a, i)).Where(x => x.a == "--pack" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToHashSet(StringComparer.Ordinal);
        List<Pack> packs = args.Contains("--all") ? [.. lib.Packs.Where(p => p.Indexed && !p.Published)] : [.. lib.Packs.Where(p => ids.Contains(p.Id))];
        if (packs.Count == 0)
        {
            throw new InvalidOperationException("Name packs with --pack <id> (or --all for the whole library, which takes hours).");
        }

        int n = await Meaning.EmbedAsync(packs, lib.Items);
        Console.Error.WriteLine($"{n} pictures read in {packs.Count} packs.");
        return 0;
    }

    private static async Task<int> Query(string[] args, float[] q, Library? loaded = null, string? except = null)
    {
        Library lib = loaded ?? await Shelves.LoadAsync();
        var hits = Meaning.Rank(lib, q, i => i.Published is null && Shelves.Id(i) != except, Limit(args));
        Console.WriteLine(JsonSerializer.Serialize(hits.Select(h => new { id = Shelves.Id(h.Item), score = Math.Round(h.Score, 4) })));
        return 0;
    }

    private static async Task<int> Like(string id, string[] args)
    {
        Library lib = await Shelves.LoadAsync();
        Item item = Shelves.Find(lib, id) ?? throw new InvalidOperationException($"No asset {id}.");
        float[] q = Meaning.VectorOf(item) ?? throw new InvalidOperationException($"{id} has no vector yet: run shelf-meaning embed on its pack.");
        return await Query(args, q, lib, id);
    }
}
