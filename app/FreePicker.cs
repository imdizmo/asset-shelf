using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Get free assets…: a free source's catalog (tools/free_sync.py catalog) to search and tick, then the ticked
/// ones downloaded. Returns the chosen ids and the resolution, or null.</summary>
public sealed class FreePicker : Window
{
    public sealed record Row(string Id, string Title, string Kind, string Categories, string Author, string? Have, string Search);

    public static readonly (string Key, string Name, string Note)[] Sources =
    [
        ("polyhaven", "Poly Haven", "Models and textures, CC0. Powered by Poly Haven (polyhaven.com)."),
        ("ambientcg", "ambientCG", "PBR materials, CC0 (ambientcg.com)."),
    ];

    public static Task<(List<string> Ids, string Res)?> Ask(Window owner, string source, string catalogFile) =>
        new FreePicker(source, catalogFile).ShowDialog<(List<string>, string)?>(owner);

    private FreePicker(string source, string catalogFile)
    {
        var (_, name, note) = Sources.First(s => s.Key == source);
        Title = $"Get free assets from {name}";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;

        List<Row> all;
        using (JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(catalogFile)))
        {
            string S(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? Untrusted.Text(v.GetString() ?? "", 200) : "";
            string L(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.Array
                ? Untrusted.Text(string.Join(", ", v.EnumerateArray().Select(x => x.ToString())), 200) : "";
            all = [.. d.RootElement.EnumerateArray().Select(e => new Row(S(e, "id"), S(e, "title"), S(e, "kind"), L(e, "categories"), S(e, "author"), S(e, "have") is { Length: > 0 } h ? h : null,
                $"{S(e, "id")} {S(e, "title")} {L(e, "categories")} {L(e, "tags")} {S(e, "author")}".ToLowerInvariant()))];
        }

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var search = new TextBox { PlaceholderText = "Search names, categories, tags…", FontSize = Look.Theme.Body };
        var kind = new ComboBox { ItemsSource = new[] { "Everything", "Models", "Materials and textures", "Updated since downloaded" }, SelectedIndex = 0 };
        var res = new ComboBox { ItemsSource = new[] { "1k", "2k", "4k" }, SelectedIndex = 1 };
        ToolTip.SetTip(res, "Texture resolution to download (2k: a good start; 4k files are four times larger).");
        var list = new ListBox { FontSize = Look.Theme.Label };
        var count = new TextBlock { FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Button get = Chrome.PrimaryButton("download", "Download", "Download the ticked assets into the library, then index them.");
        Button cancel = Chrome.TextButton("Cancel", "Close without downloading.");

        void Fill()
        {
            string[] words = (search.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = all.Where(r => words.All(w => r.Search.Contains(w, StringComparison.Ordinal))
                && kind.SelectedIndex switch { 1 => r.Kind == "model", 2 => r.Kind == "material", 3 => r.Have == "updated", _ => true }).Take(500).ToList();
            list.ItemsSource = shown.Select(r =>
            {
                var box = new CheckBox { IsChecked = chosen.Contains(r.Id), VerticalAlignment = VerticalAlignment.Center };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) chosen.Add(r.Id); else chosen.Remove(r.Id);
                    count.Text = $"{chosen.Count} ticked";
                    get.IsEnabled = chosen.Count > 0;
                };
                string mark = r.Have switch { "same" => " · already here", "updated" => " · updated since downloaded", _ => "" };
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        box,
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = r.Title, FontWeight = FontWeight.SemiBold, Foreground = Look.Tokens.Text },
                                new TextBlock { Text = $"{(r.Kind == "model" ? "Model" : "Material")} · {r.Categories}{(r.Author.Length > 0 ? $" · {r.Author}" : "")}{mark}", FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute },
                            },
                        },
                    },
                };
            }).ToList();
            count.Text = $"{chosen.Count} ticked · {shown.Count}{(shown.Count == 500 ? "+" : "")} shown of {all.Count}";
        }

        search.TextChanged += (_, _) => Fill();
        kind.SelectionChanged += (_, _) => Fill();
        get.IsEnabled = false;
        get.Click += (_, _) => Close(((List<string>, string)?)([.. chosen], (string)res.SelectedItem!));
        cancel.Click += (_, _) => Close(null);
        Fill();

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto,8,Auto"), Children = { search, Col(kind, 2), Col(res, 4) } };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto"), Children = { count, Col(cancel, 1), Col(get, 3) } };
        var credit = new TextBlock { Text = note, FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(credit, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(16), Children = { top, credit, bottom, new Border { Margin = new Thickness(0, 8), Child = list } } };
        credit.Margin = new Thickness(0, 6, 0, 0);
    }

    private static Control Col(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }
}
