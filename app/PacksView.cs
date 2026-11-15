using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace AssetShelf;

/// <summary>
/// The Packs view: a card for every pack (its store cover, title, seller, how many assets), tabs for indexed or not,
/// and on the right the picked pack's pictures, description and a button to show its assets.
/// </summary>
public sealed class PacksView
{
    private const double CardMin = 250;
    private readonly ListBox _grid = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Dictionary<Pack, Border> _cardOf = [];
    private List<Pack> _all = [];
    private List<Pack> _shown = [];
    private string _tab = "all";
    private string _search = "";
    private string _sort = "acquired";
    private Pack? _picked;
    private double _cardW = CardMin;
    private int _perRow = 1;

    /// <summary>A pack's origin gap to mark on its card ("Unknown origin", "Licence not saved"), or null; set by the window.</summary>
    public Func<Pack, string?> OriginFlag { get; set; } = _ => null;

    public PacksView()
    {
        _grid.ItemTemplate = new FuncDataTemplate<Pack[]?>((row, _) => MakeRow(row), supportsRecycling: false);
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _grid.Background = Brushes.Transparent;
        _grid.Padding = new Thickness(8, 6, 8, 8);
        _grid.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)), new Setter(Layoutable.MinHeightProperty, 0.0) },
        });
        foreach (string state in new[] { ":pointerover", ":selected", ":selected:pointerover", ":pressed" })
        {
            _grid.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        }

        _grid.SizeChanged += (_, e) => Relayout(e.NewSize.Width);
        var tabsBar = new Border
        {
            Padding = new Thickness(10, 6),
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _tabs,
        };
        var middle = new DockPanel();
        DockPanel.SetDock(tabsBar, Dock.Top);
        middle.Children.Add(tabsBar);
        middle.Children.Add(_grid);
        Middle = middle;
        ShowPack(null);
    }

    /// <summary>Asked to show a pack's assets in the Assets view.</summary>
    /// <summary>Show the pack in the Assets view, with this asset picked when not null.</summary>
    public event Action<Pack, Item?>? ShowAssets;

    /// <summary>The shown packs changed (a tab or the order).</summary>
    public event Action? Changed;

    /// <summary>Asked to open a pack's store page.</summary>
    public event Action<Uri>? OpenPage;

    /// <summary>Raised when the user picks a pack.</summary>
    public event Action<Pack>? Picked;

    public Pack? Current => _picked;

    /// <summary>Picks <paramref name="p"/> (or nothing) without raising <see cref="Picked"/>.</summary>
    public void Select(Pack? p)
    {
        if (p is not null)
        {
            Pick(p, raise: false);
            return;
        }

        if (_picked is not null && _cardOf.TryGetValue(_picked, out Border? old))
        {
            old.BorderBrush = Brushes.Transparent;
        }

        _picked = null;
        ShowPack(null);
    }

    public Control Middle { get; }

    public StackPanel Details { get; } = new();

    public int ShownCount => _shown.Count;

    /// <summary>Shows the packs matching every word of <paramref name="search"/> (title, seller, category, tags).</summary>
    public void Show(IEnumerable<Pack> packs, string search)
    {
        _all = packs.ToList();
        _search = search;
        string[] words = search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<Pack> matching = _all.Where(p =>
        {
            string text = string.Join(' ', [p.Title, p.Seller, p.Category, .. p.Tags]).ToLowerInvariant();
            return words.All(w => text.Contains(w, StringComparison.Ordinal));
        }).ToList();
        IEnumerable<Pack> pick = _tab switch
        {
            "indexed" => matching.Where(p => p.Indexed),
            "waiting" => matching.Where(p => !p.Indexed),
            _ => matching,
        };
        _shown = (_sort == "name"
            ? pick.OrderBy(p => MainWindow.ShortTitle(p.Title), StringComparer.OrdinalIgnoreCase)
            : pick.OrderByDescending(p => p.Acquired, StringComparer.Ordinal)).ToList();

        _tabs.Children.Clear();
        _tabs.Children.Add(MakeTab($"All {matching.Count:N0}", "all", "Every pack in the library."));
        _tabs.Children.Add(MakeTab($"Indexed {matching.Count(p => p.Indexed):N0}", "indexed", "Packs whose assets are in the Assets view."));
        _tabs.Children.Add(MakeTab($"Not indexed yet {matching.Count(p => !p.Indexed):N0}", "waiting", "Packs whose assets have not been indexed yet."));
        _tabs.Children.Add(new Border { Width = 18 });
        _tabs.Children.Add(MakeSort("Newest first", "acquired", "The packs added to the library last come first."));
        _tabs.Children.Add(MakeSort("A to Z", "name", "By name."));
        FillGrid();
    }

    private Tab MakeTab(string text, string key, string tip)
    {
        var t = new Tab(text, tip) { IsChecked = _tab == key };
        t.Click += (_, _) =>
        {
            _tab = key;
            Show(_all, _search);
            Changed?.Invoke();
        };
        return t;
    }

    private Tab MakeSort(string text, string key, string tip)
    {
        var t = new Tab(text, tip) { IsChecked = _sort == key };
        t.Click += (_, _) =>
        {
            _sort = key;
            Show(_all, _search);
            Changed?.Invoke();
        };
        return t;
    }

    private static string Assets(int n) => n == 1 ? "1 asset" : $"{n:N0} assets";

    // ---- grid ----

    private double CoverH => Math.Round((_cardW - 16) * 9 / 16);

    private void Relayout(double width)
    {
        double room = width - 30;
        int n = Math.Max(1, (int)(room / (CardMin + 4)));
        double w = Math.Floor(room / n) - 4;
        if (n != _perRow || Math.Abs(w - _cardW) > 0.5)
        {
            _perRow = n;
            _cardW = w;
            FillGrid();
        }
    }

    private void FillGrid()
    {
        _cardOf.Clear();
        var rows = new List<Pack[]>();
        for (int i = 0; i < _shown.Count; i += _perRow)
        {
            rows.Add(_shown.Skip(i).Take(_perRow).ToArray());
        }

        _grid.ItemsSource = rows;
    }

    private Control MakeRow(Pack[]? row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = CoverH + 82 };
        foreach (Pack p in row ?? [])
        {
            panel.Children.Add(MakeCard(p));
        }

        return panel;
    }

    private Border MakeCard(Pack p)
    {
        var cover = new Image { Stretch = Stretch.UniformToFill };
        if (p.Cover is not null)
        {
            Thumbs.Bind(cover, p.Cover, 400);
        }

        Control picture = new Border
        {
            Height = CoverH,
            CornerRadius = new CornerRadius(Look.Theme.Radius2),
            ClipToBounds = true,
            Background = Look.Theme.SurfaceSunken,
            Child = p.Cover is null ? new Look.Icon("box", 28) { Foreground = Look.Theme.TextTertiary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : cover,
        };
        if (OriginFlag(p) is { } flag)
        {
            // an undocumented pack never looks the same as a documented one
            var chip = new Border
            {
                Background = Look.Tokens.Overlay,
                BorderBrush = Look.Tokens.Warning,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Look.Tokens.RPill),
                Padding = new Thickness(8, 1),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = flag, FontSize = Look.Theme.Caption, FontWeight = FontWeight.SemiBold, Foreground = Look.Tokens.Warning },
            };
            picture = new Panel { Height = CoverH, Children = { picture, chip } };
        }
        var title = new TextBlock
        {
            Text = MainWindow.ShortTitle(p.Title),
            FontSize = Look.Theme.Label,
            FontWeight = FontWeight.SemiBold,
            Foreground = Look.Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 6, 2, 0),
        };
        string count = p.Indexed ? Assets(p.AssetCount) : "not indexed yet";
        var sub = new TextBlock
        {
            Text = p.Seller.Length > 0 ? $"{p.Seller} · {count}" : count,
            FontSize = Look.Theme.Caption,
            Foreground = Look.Theme.TextTertiary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 2, 2, 0),
        };
        var card = new Border
        {
            Width = _cardW,
            Height = CoverH + 76,
            Margin = new Thickness(2),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = p == _picked ? Look.Theme.Selection : Brushes.Transparent,
            Background = Look.Theme.SurfacePanel,
            Child = new StackPanel { Children = { picture, title, sub } },
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        Look.Chrome.Tip(card, p.Title);
        card.PointerEntered += (_, _) => card.Background = Look.Theme.SurfaceRaised;
        card.PointerExited += (_, _) => card.Background = Look.Theme.SurfacePanel;
        card.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2 && p.Indexed)
            {
                ShowAssets?.Invoke(p, null);
                return;
            }

            Pick(p);
        };
        _cardOf[p] = card;
        return card;
    }

    private void Pick(Pack p, bool raise = true)
    {
        if (_picked is not null && _cardOf.TryGetValue(_picked, out Border? old))
        {
            old.BorderBrush = Brushes.Transparent;
        }

        _picked = p;
        if (_cardOf.TryGetValue(p, out Border? now))
        {
            now.BorderBrush = Look.Theme.Selection;
        }

        ShowPack(p);
        if (raise)
        {
            Picked?.Invoke(p);
        }
    }

    // ---- details ----

    private void ShowPack(Pack? p)
    {
        Details.Children.Clear();
        if (p is null)
        {
            Details.Children.Add(new TextBlock
            {
                Text = "Pick a pack to see its pictures from the store and what it holds. Double-click a pack to see its assets.",
                Margin = new Thickness(14, 16),
                FontSize = Look.Theme.Label,
                Foreground = Look.Theme.TextTertiary,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        Details.Children.Add(new Border
        {
            Padding = new Thickness(10, 8),
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new SelectableTextBlock { Text = MainWindow.ShortTitle(p.Title), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = p.Seller.Length > 0 ? $"by {p.Seller}" : p.Id, FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary },
                },
            },
        });

        List<string> pictures = p.Gallery.Count > 0 ? p.Gallery.ToList() : p.Cover is null ? [] : [p.Cover];
        if (pictures.Count > 0)
        {
            var big = new Image { Stretch = Stretch.Uniform };
            int at = 0;
            void ShowAt(int i)
            {
                at = i;
                Thumbs.Get(pictures[i], b => big.Source = b, 800);
            }

            var bigHost = new Border
            {
                Margin = new Thickness(10, 10, 10, 6),
                Height = 360,
                CornerRadius = new CornerRadius(Look.Theme.Radius3),
                ClipToBounds = true,
                Background = Look.Theme.SurfaceSunken,
                Child = big,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            Look.Chrome.Tip(bigHost, "Click to see it full size (arrow keys for the others).");
            bigHost.PointerPressed += (s, _) => PictureWindow.Show((Control)s!, pictures, at);
            Details.Children.Add(bigHost);
            ShowAt(0);

            var strip = new WrapPanel { Margin = new Thickness(8, 0, 8, 8) };
            for (int i = 0; i < pictures.Count; i++)
            {
                int index = i;
                var small = new Image { Width = 66, Height = 37, Stretch = Stretch.UniformToFill };
                Thumbs.Get(pictures[i], b => small.Source = b, 160);
                var frame = new Border
                {
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(Look.Theme.Radius1),
                    ClipToBounds = true,
                    Child = small,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                frame.PointerPressed += (_, _) => ShowAt(index);
                strip.Children.Add(frame);
            }

            Details.Children.Add(strip);
        }

        if (p.Indexed && p.AssetCount > 0)
        {
            Details.Children.Add(AssetPreview(p));
        }
        else
        {
            Details.Children.Add(new TextBlock { Text = "Not indexed yet: its assets show here once it is.", Margin = new Thickness(10, 4, 10, 10), FontSize = Look.Theme.Label, Foreground = Look.Theme.TextTertiary });
        }

        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 10, 10) };
        if (Uri.TryCreate(p.Url, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
        {
            Button page = Look.Chrome.IconTextButton("send", "Open the store page", url.AbsoluteUri);
            page.HorizontalAlignment = HorizontalAlignment.Stretch;
            page.Click += (_, _) => OpenPage?.Invoke(url);
            buttons.Children.Add(page);
        }

        Details.Children.Add(buttons);

        if (Safety.Lines(p) is { Count: > 0 } safety)
        {
            var box = new StackPanel { Margin = new Thickness(10, 0, 10, 10), Spacing = 1 };
            foreach (var (text, blocking) in safety)
            {
                box.Children.Add(new TextBlock { Text = (blocking ? "⚠ " : "") + text, FontSize = Look.Theme.Label, Foreground = blocking ? Look.Theme.Accent : Look.Theme.Warning, TextWrapping = TextWrapping.Wrap });
            }

            Details.Children.Add(box);
        }

        var about = new FoldGroup("About", open: false);
        if (p.Licence.Length > 0)
        {
            about.Body.Children.Add(MainWindow.InfoRow("Licence", p.Licence));
        }

        if (p.Category.Length > 0)
        {
            about.Body.Children.Add(MainWindow.InfoRow("Category", p.Category.Replace("/", " › ", StringComparison.Ordinal)));
        }

        if (DateTime.TryParse(p.Acquired, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when))
        {
            about.Body.Children.Add(MainWindow.InfoRow("Added", when.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)));
        }

        about.Body.Children.Add(MainWindow.InfoRow("Assets", p.Indexed ? $"{p.AssetCount:N0}" : "not indexed yet"));
        if (p.Tags.Count > 0)
        {
            about.Body.Children.Add(MainWindow.InfoRow("Tags", string.Join(", ", p.Tags)));
        }

        if (p.Description.Length > 0)
        {
            var desc = new FoldGroup("Description", open: false);
            desc.Body.Children.Add(new SelectableTextBlock { Text = p.Description, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) });
            Details.Children.Add(desc);
        }

        Details.Children.Add(about);
    }

    private const int PreviewCount = 12;

    /// <summary>A link to the pack's assets in the Assets view, with a few of their pictures (one kind after another,
    /// so a pack of meshes and sounds shows both). A picture opens the Assets view on that asset.</summary>
    private Control AssetPreview(Pack p)
    {
        var byKind = p.ByPackage.Values.Where(i => i.Thumb is not null && Library.DefaultKinds.Contains(i.Kind))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .GroupBy(i => i.Kind).OrderBy(g => Array.IndexOf(Library.Kinds, g.Key)).Select(g => new Queue<Item>(g)).ToList();
        var pick = new List<Item>();
        while (pick.Count < PreviewCount && byKind.Any(q => q.Count > 0))
        {
            foreach (Queue<Item> q in byKind.Where(q => q.Count > 0))
            {
                if (pick.Count < PreviewCount)
                {
                    pick.Add(q.Dequeue());
                }
            }
        }

        var link = new Button
        {
            Content = Look.Chrome.LinkText($"Its {Assets(p.AssetCount)} in the Assets view  ›", Look.Theme.Label, wrap: false, weight: FontWeight.SemiBold),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(-4, 0, 0, 0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        link.Classes.Add("ep");
        link.Classes.Add("ep-text");
        Look.Chrome.Tip(link, "Switch to the Assets view, showing only this pack.");
        link.Click += (_, _) => ShowAssets?.Invoke(p, null);

        var strip = new WrapPanel { Margin = new Thickness(-2, 4, 0, 0) };
        foreach (Item item in pick)
        {
            var img = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };
            Thumbs.Bind(img, item.Thumb!);
            var tile = new Border
            {
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(Look.Theme.Radius2),
                ClipToBounds = true,
                Background = Look.Theme.SurfaceSunken,
                Child = img,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            Look.Chrome.Tip(tile, $"{item.Name}\n{item.Kind} · click to open it in the Assets view");
            tile.PointerPressed += (_, _) => ShowAssets?.Invoke(p, item);
            strip.Children.Add(tile);
        }

        return new StackPanel
        {
            Margin = new Thickness(10, 4, 10, 10),
            Children = { link, strip },
        };
    }
}

/// <summary>A window showing pictures full size; Left and Right step through them, Esc closes it.</summary>
public sealed class PictureWindow : Window
{
    private readonly IReadOnlyList<string> _pictures;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private int _at;

    private PictureWindow(IReadOnlyList<string> pictures, int at)
    {
        _pictures = pictures;
        Title = "Asset Shelf";
        Width = 1400;
        Height = 820;
        Background = Look.Theme.SurfacePage;
        Content = _image;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key is Key.Right or Key.Left)
            {
                Load((_at + (e.Key == Key.Right ? 1 : -1) + _pictures.Count) % _pictures.Count);
            }
        };
        PointerPressed += (_, _) => Load((_at + 1) % _pictures.Count);
        Load(at);
    }

    public static void Show(Control owner, IReadOnlyList<string> pictures, int at)
    {
        var w = new PictureWindow(pictures, at);
        if (TopLevel.GetTopLevel(owner) is Window parent)
        {
            w.Show(parent);
        }
        else
        {
            w.Show();
        }
    }

    private void Load(int i)
    {
        _at = i;
        Title = $"Asset Shelf: picture {i + 1} of {_pictures.Count}";
        try
        {
            (_image.Source as IDisposable)?.Dispose();
            _image.Source = new Bitmap(_pictures[i]);
        }
        catch (IOException)
        {
            _image.Source = null;
        }
    }
}
