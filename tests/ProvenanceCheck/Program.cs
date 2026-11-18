// Checks how provenance tells an aggregator's pack (a site that collects other people's assets) and keeps each asset's
// original source. Run: dotnet run --project tests/ProvenanceCheck
using AssetShelf;

string d = Directory.CreateTempSubdirectory("asset-shelf-provenance-").FullName;
try
{
    // an older provenance.json, without the new fields, still loads
    File.WriteAllText(Path.Combine(d, "provenance.json"), """{"version":1,"source":{"site":"x","url":"https://www.patch.gg/create/toolbox/assets","creator":""},"licence":{},"acquired":{},"receipts":[]}""");
    var p = Provenance.Load(d)!;
    Check(p.Source.Originals.Count == 0 && !p.Source.Aggregator, "an older file loads");
    Check(Provenance.IsAggregator(p), "a known aggregator is found by its host (subdomain too)");
    Check(Provenance.OriginalsMissing(p, ["a.fbx", "b.fbx"]), "originals are missing");
    p.Source.Originals["a.fbx"] = "Someone, https://example.com/a";
    p.Source.Originals["b.fbx"] = "Other";
    p.Save(d);
    var q = Provenance.Load(d)!;
    Check(!Provenance.OriginalsMissing(q, ["a.fbx", "b.fbx"]) && q.Source.Originals["a.fbx"].StartsWith("Someone", StringComparison.Ordinal), "originals are saved and read back");
    q.Source.Url = "https://evilpatch.gg/x";
    Check(!Provenance.IsAggregator(q), "a lookalike host is not the aggregator");
    q.Source.Aggregator = true;
    Check(Provenance.IsAggregator(q), "a pack marked by hand is one");
    q.Source.Url = "https://fab.com/x";
    q.Source.Aggregator = false;
    Check(!Provenance.OriginalsMissing(q, ["c.fbx"]), "a store's pack never asks for originals");
    Console.WriteLine("all passed");
}
finally
{
    Directory.Delete(d, recursive: true);
}

static void Check(bool ok, string what)
{
    if (!ok)
    {
        throw new InvalidOperationException("FAIL " + what);
    }

    Console.WriteLine("ok " + what);
}
