using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>
/// Prop Generator's layout: a top bar (brand, File/View/Help, the search, Send to Blender and Send to Unreal), the
/// filters down the left, kind tabs over the picture grid in the middle, the picked asset down the right, a status bar.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly (string Kind, string Plural)[] TabKinds =
    [
        ("Mesh", "Meshes"), ("Character", "Characters"), ("Animation", "Animations"), ("VFX", "Effects"), ("Sound", "Sounds"),
        ("Blueprint", "Blueprints"), ("Map", "Maps"), ("Prop", "Props"), ("Material", "Materials"), ("Texture", "Textures"), ("Other", "Other"),
    ];

    private readonly Settings _settings;
    private Library _lib = new();
    private List<Item> _shown = [];
    private Item? _picked;
    private string? _kind;  // null: All (the default kinds)
    private double _picture = 128;

    private readonly SearchField _search = new("Search assets, packs, tags…", "Type words from an asset's name, its pack or its tags; every word must match. Esc clears it.", "Ctrl+K");
    private readonly SearchField _tagFilter = new("Filter tags", "Type part of a tag to find it in the list.");
    private readonly SearchField _packFilter = new("Filter packs", "Type part of a pack's name or seller to find it in the list.") { Margin = new Thickness(0, 2, 0, 6) };
    private readonly StackPanel _packRows = new() { Spacing = 1 };
    private readonly FoldGroup _tagsGroup = new("Tags");
    private readonly FoldGroup _packsGroup = new("Packs", open: false);
    private readonly FoldGroup _licenceGroup = new("Licence");
    private readonly FoldGroup _originGroup = new("Origin");
    private readonly HashSet<string> _originOn = [];  // unknown, licence, receipt: show only packs with that gap

    // per pack: (no source page, no saved licence copy, no receipt), worked out once (file checks on the share)
    private readonly Dictionary<string, (bool Unknown, bool Licence, bool Receipt, bool Originals)> _originOf = [];
    private readonly WrapPanel _activeTags = new() { Margin = new Thickness(8, 8, 8, 2) };
    private readonly HashSet<string> _packOff = [];
    private readonly HashSet<string> _licenceOff = [];
    private readonly HashSet<string> _tagOn = new(StringComparer.OrdinalIgnoreCase);
    private readonly TabStrip _tabs = new();  // never wraps: what does not fit goes into More
    private readonly ListBox _grid = new();
    private readonly StackPanel _details = new();
    private readonly TextBlock _status = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counts = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _projectButton = Chrome.TextButton("", "The Unreal project assets are sent to. Click to pick another.");
    private Button _fileMenu = null!;
    private readonly TextBlock _libraryCaption = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _toUnity = Chrome.IconTextButton("box", "Send to Unity", "Copy the picked asset into the Unity project with its textures and materials for the project's render pipeline.");
    private readonly Button _toGroundwork = Chrome.IconTextButton("trees", "Send to Groundwork", "Turn the picked meshes into .glb files and hand them to the open Groundwork map (through its inbox).");
    private readonly Button _toBlender = Chrome.IconTextButton("send", "Send to Blender", "Import the picked asset into the open Blender (the Asset Shelf Bridge add-on picks it up).");
    private readonly Button _toUnreal = Chrome.PrimaryButton("download", "Send to Unreal", "Copy the picked asset and everything it needs into the Unreal project's Content folder.");
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<Item, Border> _tileOf = [];
    private readonly Collections _keep = Collections.Load();
    private string? _collection;                      // showing only this collection (Favourites included), or all
    private readonly FoldGroup _keepGroup = new("Collections");
    private readonly HashSet<Item> _selected = [];  // Ctrl/Shift-clicked tiles, sent together; holds the picked one too
    private Item? _anchor;                            // where a Shift-click range starts
    private HashSet<string> _inProject = new(StringComparer.OrdinalIgnoreCase);  // packages already in the Unreal project
    private List<(CheckBox Box, List<Item> Items)> _extras = [];
    private int _perRow = 1;
    private readonly PacksView _packsView = new();
    private readonly ContentControl _middle = new();
    private readonly ScrollViewer _rightScroll = new();
    private Border _leftPanel = null!;
    private Border _rightPanel = null!;
    private Control _assetsMiddle = null!;
    private readonly Tab _assetsTab = new("Assets", "Every asset of every pack, by kind.");
    private readonly Tab _packsTabTop = new("Packs", "Every pack in the library, with its store pictures.");
    private bool _packsMode;
    private readonly ProgressBar _progress = new()
    {
        Width = 80,
        Height = 4,
        MinHeight = 4,
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Look.Theme.Accent,
        Background = Look.Theme.SurfaceRaised,
        IsVisible = false,
    };

    private readonly TextBlock _progressText = new() { FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, VerticalAlignment = VerticalAlignment.Center };
    private StackPanel? _progressRowPanel;

    // "Reading 12 of 687" and an 80 px bar, at the left of the status bar's counters
    private StackPanel _progressRow => _progressRowPanel ??= new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _progressText, _progress } };

    // the split send button: the main half sends to the last-used target, the menu half picks another
    private readonly Button _sendMain = Chrome.PrimaryButton("download", "Send to Unreal", "Send the picked asset to the last-used target.");
    private readonly Button _sendMenu = Chrome.PrimaryButton("chevron-down", "", "Send to another target.");
    private static readonly (string Target, string Icon, string? Keys)[] SendTargets = [("Unreal", "download", "Ctrl+Shift+U"), ("Blender", "send", null), ("Unity", "box", null), ("Groundwork", "trees", null)];

    public MainWindow(Settings settings, Library lib)
    {
        _settings = settings;
        _lib = lib;
        Title = "Asset Shelf";
        Width = 1560;
        Height = 960;
        MinWidth = 900;
        MinHeight = 560;
        Background = Look.Theme.SurfacePage;
        string icon = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(icon))
        {
            Icon = new WindowIcon(icon);
        }

        _projectButton.Height = 20;
        _projectButton.Click += async (_, _) => await PickProjectAsync();

        _grid.ItemTemplate = new FuncDataTemplate<Item[]?>((row, _) => MakeRow(row), supportsRecycling: false);
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _grid.Background = Brushes.Transparent;
        _grid.Padding = new Thickness(8, 6, 8, 8);
        _grid.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0) },
        });
        foreach (string state in new[] { ":pointerover", ":selected", ":selected:pointerover", ":pressed" })
        {
            _grid.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        }

        _grid.SizeChanged += (_, e) => Relayout(e.NewSize.Width);
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Refresh();
        };
        _search.Box.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        _tagFilter.Box.TextChanged += (_, _) => FillTags();
        _packFilter.Box.TextChanged += (_, _) =>
        {
            FillPacksAndLicences();
            GroupActions();
        };
        _toBlender.Click += async (_, _) => await SendToBlenderAsync();
        _toGroundwork.Click += async (_, _) => await SendToGroundworkAsync();
        _toUnity.Click += async (_, _) => await SendToUnityAsync();
        _toUnreal.Click += async (_, _) => await SendToUnrealAsync();
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
            {
                Step(e.Key == Key.Left ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.U && _toUnreal.IsEnabled && !_packsMode)
            {
                _toUnreal.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.L)
            {
                LogWindow.Open(this);
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers != KeyModifiers.Control)
            {
                return;
            }

            if (e.Key == Key.K)
            {
                _search.Box.Focus();
                _search.Box.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.R)
            {
                _ = ReloadAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Q)
            {
                Close();
                e.Handled = true;
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions($"{Look.Theme.MenuBarHeight},*,{Look.Theme.StatusBarHeight}") };
        root.Children.Add(TopBar());
        Control body = Body();
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Control status = StatusBar();
        Grid.SetRow(status, 2);
        root.Children.Add(status);
        Content = root;

        _packsView.ShowAssets += (p, item) =>
        {
            SetMode(packs: false);
            OnlyPack(p);
            if (item is not null)
            {
                Pick(item);
                ScrollTo(item);
            }
        };
        _packsView.Picked += _ => Record();
        // the mouse's back and forward buttons, and Alt+Left/Right
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            PointerUpdateKind k = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (k is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)
            {
                Step(k == PointerUpdateKind.XButton1Pressed ? -1 : 1);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _packsView.OpenPage += async u => await Launcher.LaunchUriAsync(u);
        _packsView.Changed += () => _counts.Text = $"{_packsView.ShownCount:N0} packs shown";

        ShowDetails(null);
        UseLibrary();
        _ = ScanProjectAsync();
    }

    /// <summary>Learns which packages the Unreal project already has (its Content folder), for the badges and the send preview.</summary>
    private async Task ScanProjectAsync()
    {
        string project = _settings.UnrealProject;
        if (project.Length == 0)
        {
            _inProject = new(StringComparer.OrdinalIgnoreCase);
            return;
        }

        using (var step = Log.Time("unreal", $"scan {project}"))
        {
            _inProject = await Task.Run(() => Sender.ProjectPackages(project));
            step.Then($"{_inProject.Count:N0} packages already in {Path.GetFileName(project.TrimEnd('/'))}");
        }

        FillGrid();
        if (_picked is { } item)
        {
            ShowDetails(item);
        }
    }

    // The bar in the status line: a fraction from 0 to 1, or null for "busy, length unknown"; hidden when done.
    private void Busy(double? fraction, string? label = null)
    {
        _progressText.Text = label ?? "";
        _progressText.IsVisible = label is not null;
        _progress.IsVisible = true;
        _progress.IsIndeterminate = fraction is null;
        _progress.Maximum = 1;
        _progress.Value = fraction ?? 0;
    }

    private void Idle()
    {
        _progress.IsVisible = false;
        _progressText.IsVisible = false;
    }

    // ---- layout ----

    private Control TopBar()
    {
        var brand = new Button
        {
            Width = 24,
            Height = 24,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        brand.Classes.Add("ep");
        string iconFile = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(iconFile))
        {
            // the app icon itself, so the bar matches the taskbar
            brand.Content = new Image { Source = new Avalonia.Media.Imaging.Bitmap(iconFile), Width = 22, Height = 22 };
            brand.Background = Brushes.Transparent;
        }
        else
        {
            brand.Content = new Icon("layout-grid", 14);
            brand.Classes.Add("ep-primary");
        }

        Chrome.Tip(brand, "Show everything: clear the search, the tags and the kind tab.");
        brand.Click += (_, _) => ClearFilters();

        Button file = _fileMenu = Menu("File", "Reload the library, pick the Unreal project, quit.",
            ("Reload library", "Ctrl+R", () => _ = ReloadAsync()),
            ("Connect to a shared library…", null, () => _ = ConnectAsync()),
            ("Team server…", null, () => _ = TeamServerAsync()),
            ("Unreal project…", null, () => _ = PickProjectAsync()),
            ("Sign in to Epic…", null, () => _ = SignInAsync()),
            ("Sync Fab library…", null, () => _ = SyncAsync()),
            ("Sign in to itch.io…", null, () => _ = ItchSignInAsync()),
            ("Sync itch.io library…", null, () => _ = SyncAsync(itch: true)),
            ("Unity project…", null, () => _ = PickUnityAsync()),
            ("Unity folder…", null, () => _ = UnityFolderAsync()),
            ("Unity: pack's own folders on/off", null, () =>
            {
                _settings.UnityMirrorPack = !_settings.UnityMirrorPack;
                _settings.Save();
                _status.Text = _settings.UnityMirrorPack ? $"Unity: assets go into the pack's own folders under {_settings.UnityFolder}" : $"Unity: assets go into {_settings.UnityFolder}/<pack>/<asset>";
            }),
            ("Quit", "Ctrl+Q", Close));
        Button view = Menu("View", "Picture size, the debug log.",
            ("Small pictures", null, () => SetPicture(96)),
            ("Medium pictures", null, () => SetPicture(128)),
            ("Large pictures", null, () => SetPicture(180)),
            ("Debug log", "Ctrl+Shift+L", () => LogWindow.Open(this)));
        Button help = Menu("Help", "About Asset Shelf.",
            ("About Asset Shelf", null, () => _status.Text = "Asset Shelf: every asset of your packs in one grid; send one to Unreal or Blender. MIT licence."));

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(brand);
        left.Children.Add(new TextBlock { Text = "Asset Shelf", FontSize = Look.Theme.Body, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(file);
        left.Children.Add(view);
        left.Children.Add(help);
        left.Children.Add(new Border { Width = 1, Height = 18, Background = Look.Tokens.Line, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new Segmented(_assetsTab, _packsTabTop));
        _assetsTab.IsChecked = true;
        _assetsTab.Click += (_, _) => SetMode(packs: false);
        _packsTabTop.Click += (_, _) => SetMode(packs: true);

        _search.MaxWidth = 380;
        _search.MinWidth = 160;
        _search.Margin = new Thickness(12, 0);
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.Background = Look.Theme.SurfacePanel;

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        right.Children.Add(SendButton());

        // the search takes what the menus and the send button leave (380 at most, 160 at least): nothing overlaps at 1024
        var bar = new Grid { Background = Look.Tokens.Panel, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        bar.Children.Add(new Border { Child = left, Padding = new Thickness(Look.Theme.Space2, 0, 0, 0) });
        Grid.SetColumn(_search, 1);
        bar.Children.Add(_search);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        return new Border { Child = bar, BorderBrush = Look.Theme.BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private static Button Menu(string title, string tip, params (string Text, string? Keys, Action Run)[] items)
    {
        Button b = Chrome.TextButton(title, tip);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (text, keys, run) in items)
        {
            var mi = new MenuItem { Header = text, InputGesture = keys is null ? null : KeyGesture.Parse(keys), FontSize = Look.Theme.Label };
            mi.Click += (_, _) => run();
            flyout.Items.Add(mi);
        }

        b.Flyout = flyout;
        return b;
    }

    private Control Body()
    {
        var leftHead = new Border
        {
            Padding = new Thickness(8, 6, 8, 8),
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Filters", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, Margin = new Thickness(2, 3, 0, 0) },
                    _tagFilter,
                },
            },
        };
        var filters = new StackPanel { Children = { leftHead, _activeTags, _keepGroup, _tagsGroup, _licenceGroup, _originGroup, _packsGroup } };
        var leftPanel = new Border
        {
            Width = 270,
            Background = Look.Theme.SurfacePanel,
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = filters },
        };

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
        _assetsMiddle = middle;
        _middle.Content = middle;

        _rightScroll.Content = _details;
        // the inspector: 240 to 520 wide, dragged by its left edge, the width kept in the settings
        var grip = new Border { Width = 5, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
        var rightPanel = new Border
        {
            Width = Math.Clamp(_settings.InspectorWidth, Look.Tokens.PanelMin, Look.Tokens.PanelMax),
            Background = Look.Theme.SurfacePanel,
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new Panel { Children = { _rightScroll, grip } },
        };
        double? dragFrom = null;
        double widthFrom = 0;
        grip.PointerPressed += (_, e) => { dragFrom = e.GetPosition(this).X; widthFrom = rightPanel.Width; e.Pointer.Capture(grip); };
        grip.PointerMoved += (_, e) =>
        {
            if (dragFrom is { } from)
            {
                rightPanel.Width = Math.Clamp(widthFrom - (e.GetPosition(this).X - from), Look.Tokens.PanelMin, Look.Tokens.PanelMax);
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            dragFrom = null;
            e.Pointer.Capture(null);
            _settings.InspectorWidth = rightPanel.Width;
            _settings.Save();
        };
        _rightPanel = rightPanel;
        _leftPanel = leftPanel;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(leftPanel);
        Grid.SetColumn(_middle, 1);
        grid.Children.Add(_middle);
        Grid.SetColumn(rightPanel, 2);
        grid.Children.Add(rightPanel);
        return grid;
    }

    private Control StatusBar()
    {
        var dock = new DockPanel { Margin = new Thickness(10, 0, 4, 0) };
        // the right side never shrinks; the message on the left ellipsizes first (full text in its tooltip)
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(_progressRow);
        right.Children.Add(_counts);
        right.Children.Add(_libraryCaption);
        right.Children.Add(_projectButton);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status[!ToolTip.TipProperty] = _status[!TextBlock.TextProperty];
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = Look.Tokens.Success, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var left = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Children = { dot, _status } };
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(left);
        return new Border { Child = dock, Background = Look.Tokens.Panel, BorderBrush = Look.Theme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // ---- filters ----

    private bool _reloading;

    internal async Task ReloadAsync()
    {
        if (_reloading)
        {
            return;
        }

        _reloading = true;
        _status.Text = "Reading the library…";
        var progress = new Progress<(int Done, int Total)>(p => Busy(p.Total == 0 ? null : (double)p.Done / p.Total, p.Total == 0 ? null : $"Reading {p.Done:N0} of {p.Total:N0}"));
        try
        {
            using (Log.Time("library", "reload (Ctrl+R)"))
            {
                var (packs, index) = await TeamLibraryAsync(progress) ?? (_settings.Packs, _settings.Index);
                List<Team.Published> published = [];
                if (Team.Base is not null)
                {
                    try
                    {
                        published = await Team.PublishedAsync();
                    }
                    catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
                    {
                        _teamNote = $"team maps and props not read: {e.Message}";
                    }
                }

                _lib = await Task.Run(() =>
                {
                    if (Team.Base is null)
                    {
                        Shared.Reconnect(_settings);
                    }

                    Library lib = Library.Load(packs, index, progress);
                    if (Team.Base is not null)
                    {
                        lib.AddPublished(published);
                    }

                    return lib;
                });
            }
        }
        finally
        {
            _reloading = false;
            Idle();
        }

        UseLibrary();
    }

    private void UseLibrary()
    {
        _originOf.Clear();
        _packsView.OriginFlag = p => OriginOf(p) switch { { Unknown: true } => "Unknown origin", { Originals: true } => "Originals not recorded", { Licence: true } => "Licence not saved", _ => p.Provenance is { Acquired.How: "bought", Receipts.Count: 0 } ? "No receipt" : null };
        _libraryCaption.Text = $"{_lib.Items.Count:N0} assets · {_lib.Packs.Count} packs ({_lib.Packs.Count(p => p.Indexed)} indexed)";
        // member mode: a library this user cannot change (a share they only read) is synced and indexed by its owner
        bool member = Team.Base is not null || !Shared.Writable(_settings.Index);
        _member = member;

        foreach (MenuItem mi in ((MenuFlyout)_fileMenu.Flyout!).Items.OfType<MenuItem>().Where(m => m.Header is string h && (h.StartsWith("Sync", StringComparison.Ordinal) || h.StartsWith("Sign in", StringComparison.Ordinal))))
        {
            mi.IsVisible = !member;
        }

        _status.Text = Team.Who is { } who ? $"Ready · team library as {who.Name} ({who.Role})"
            : _lib.Packs.Count == 0 ? $"No indexes in {_settings.Index}" : member ? "Ready · a shared library, read-only: its owner syncs it" : "Ready";
        if (_teamNote is { } note)
        {
            _status.Text += $" · {note}";
        }
        _picked = null;
        _history.Clear();  // places point at the old library's objects
        _at = -1;
        ShowDetails(null);
        FillPacksAndLicences();
        Refresh();
    }

    // ---- back and forward ----

    /// <summary>Where the user was: the view, the filters and what was picked.</summary>
    private sealed record Place(bool Packs, string? Kind, string Search, string[] PackOff, string[] LicenceOff, string[] TagOn, Item? Item, Pack? Pack, string? Collection)
    {
        public string Key => $"{Packs}|{Kind}|{Search}|{string.Join(',', PackOff)}|{string.Join(',', LicenceOff)}|{string.Join(',', TagOn)}|{Item?.Package}|{Pack?.Id}|{Collection}";
    }

    private const int HistoryMax = 100;
    private readonly List<Place> _history = [];
    private int _at = -1;
    private bool _restoring;
    private long _lastRecord;

    private Place Here() => new(_packsMode, _kind, _search.Box.Text ?? "", [.. _packOff.Order(StringComparer.Ordinal)],
        [.. _licenceOff.Order(StringComparer.Ordinal)], [.. _tagOn.Order(StringComparer.OrdinalIgnoreCase)], _picked, _packsView.Current, _collection);

    /// <summary>Adds where the user is now to the history. Changes within a moment of each other (a pack filter and then
    /// the asset picked in it) make one step.</summary>
    private void Record()
    {
        if (_restoring)
        {
            return;
        }

        Place now = Here();
        if (_at >= 0 && _history[_at].Key == now.Key)
        {
            return;
        }

        long t = Environment.TickCount64;
        bool merge = _at >= 1 && t - _lastRecord < 400;
        _lastRecord = t;
        _history.RemoveRange(_at + 1, _history.Count - _at - 1);
        if (merge)
        {
            _history[_at] = now;
            return;
        }

        _history.Add(now);
        if (_history.Count > HistoryMax)
        {
            _history.RemoveAt(0);
        }

        _at = _history.Count - 1;
    }

    private void Step(int by)
    {
        int to = _at + by;
        if (to < 0 || to >= _history.Count)
        {
            return;
        }

        _at = to;
        Place p = _history[to];
        Log.Write("nav", $"{(by < 0 ? "back" : "forward")} to step {to + 1} of {_history.Count}: {(p.Packs ? "Packs" : "Assets")}{(p.Item is { } it ? ", " + it.Name : p.Pack is { } pk ? ", " + pk.Title : "")}");
        _restoring = true;
        Place now = Here();
        // only the picked asset or pack differs: the grid stays as it is
        bool sameView = now.Packs == p.Packs && now.Kind == p.Kind && now.Search == p.Search && now.PackOff.SequenceEqual(p.PackOff)
            && now.LicenceOff.SequenceEqual(p.LicenceOff) && now.TagOn.SequenceEqual(p.TagOn) && now.Collection == p.Collection;
        try
        {
            if (sameView)
            {
                _packsView.Select(p.Pack);
                if (p.Item is not null)
                {
                    Pick(p.Item);
                    ScrollTo(p.Item);
                }
                else
                {
                    Unpick();
                }

                return;
            }

            _kind = p.Kind;
            _collection = p.Collection;
            _packOff.Clear();
            _packOff.UnionWith(p.PackOff);
            _licenceOff.Clear();
            _licenceOff.UnionWith(p.LicenceOff);
            _tagOn.Clear();
            _tagOn.UnionWith(p.TagOn);
            if ((_search.Box.Text ?? "") != p.Search)
            {
                _search.Box.Text = p.Search;  // only when it differs: setting it starts the search timer, a second refresh
            }

            _searchDelay.Stop();
            FillPacksAndLicences();
            SetMode(p.Packs);
            _packsView.Select(p.Pack);
            if (p.Item is not null)
            {
                Pick(p.Item);
                ScrollTo(p.Item);
            }
            else
            {
                Unpick();
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ScrollTo(Item item)
    {
        // after the layout pass, when the rows exist at the current width
        Dispatcher.UIThread.Post(() =>
        {
            int i = _shown.IndexOf(item);
            if (i >= 0 && _grid.ItemsSource is List<Item[]> rows)
            {
                _grid.ScrollIntoView(rows[Math.Min(rows.Count - 1, i / Math.Max(1, _perRow))]);
            }
        }, DispatcherPriority.Background);
    }

    private void ClearFilters()
    {
        _search.Box.Text = "";
        _tagOn.Clear();
        _kind = null;
        Refresh();
    }

    private IEnumerable<Item> Pool()
    {
        string[] words = (_search.Box.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _lib.Items.Where(i => !_packOff.Contains(i.Pack.Id) && !_licenceOff.Contains(LicenceOf(i.Pack)) && OriginMatch(i.Pack)
            && _tagOn.All(t => i.Tags.Contains(t)) && words.All(w => i.SearchText.Contains(w, StringComparison.Ordinal))
            && (_collection is null || (_keep.Members(_collection)?.Contains(Collections.Key(i)) ?? false)));
    }

    private void SetMode(bool packs)
    {
        _packsMode = packs;
        _assetsTab.IsChecked = !packs;
        _packsTabTop.IsChecked = packs;
        _leftPanel.IsVisible = !packs;
        _middle.Content = packs ? _packsView.Middle : _assetsMiddle;
        _rightScroll.Content = packs ? _packsView.Details : _details;
        _rightPanel.Width = packs ? 660 : 330;
        _toUnreal.IsVisible = !packs;
        _toBlender.IsVisible = !packs;
        _toGroundwork.IsVisible = !packs;
        _sendBox!.IsVisible = !packs;
        _search.Box.PlaceholderText = packs ? "Search packs, sellers, tags…" : "Search assets, packs, tags…";
        Refresh();
    }

    private void Refresh()
    {
        using var step = Log.Time("grid", "refresh");
        if (_packsMode)
        {
            _packsView.Show(_lib.Packs, _search.Box.Text ?? "");
            _counts.Text = $"{_packsView.ShownCount:N0} packs shown";
            UpdateProjectButton();
            Record();
            return;
        }

        List<Item> pool = Pool().ToList();
        Dictionary<string, int> perKind = pool.CountBy(i => i.Kind).ToDictionary(k => k.Key, k => k.Value);
        _shown = pool.Where(i => _kind is null ? Library.DefaultKinds.Contains(i.Kind) : i.Kind == _kind)
            .OrderBy(i => Array.IndexOf(Library.Kinds, i.Kind)).ThenBy(i => i.Pack.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        int allCount = pool.Count(i => Library.DefaultKinds.Contains(i.Kind));
        var tabs = new List<UnderlineTab> { MakeTab("All", allCount, null, "Meshes, characters, animations, effects, sounds, blueprints and maps. Materials, textures and the rest have their own tabs.") };
        foreach (var (kind, plural) in TabKinds)
        {
            tabs.Add(MakeTab(plural, perKind.GetValueOrDefault(kind), kind, $"Only {plural.ToLowerInvariant()}."));
        }

        _tabs.SetTabs(tabs);

        _activeTags.Children.Clear();
        Pack? only = _lib.Packs.Count > 1 && _packOff.Count == _lib.Packs.Count - 1 ? _lib.Packs.FirstOrDefault(p => !_packOff.Contains(p.Id)) : null;
        _activeTags.IsVisible = _tagOn.Count > 0 || only is not null || _collection is not null;
        if (_collection is { } col)
        {
            _activeTags.Children.Add(Chip($"{col}", on: true, "Showing only this collection. Click to show everything again.", () => _collection = null));
        }

        if (only is not null)
        {
            _activeTags.Children.Add(Chip($"Pack: {ShortTitle(only.Title)}", on: true, "Showing only this pack. Click to show every pack again.", () =>
            {
                _packOff.Clear();
                FillPacksAndLicences();
            }));
        }

        foreach (string t in _tagOn.Order(StringComparer.OrdinalIgnoreCase))
        {
            _activeTags.Children.Add(Chip(t, on: true, $"Showing only assets tagged {t}. Click to drop it.", () => _tagOn.Remove(t)));
        }

        FillTags();
        FillGrid();
        FillKeep();
        GroupActions();
        step.Then($"refresh: {_shown.Count:N0} shown of {pool.Count:N0} matching, kind {_kind ?? "All"}, {_tagOn.Count} tags, {_packOff.Count} packs off");
        _counts.Text = $"{_shown.Count:N0} shown";
        UpdateProjectButton();
        Record();
    }

    private UnderlineTab MakeTab(string name, int count, string? kind, string tip)
    {
        var tab = new UnderlineTab(name, count.ToString("N0"), tip) { IsChecked = _kind == kind };
        tab.Click += (_, _) =>
        {
            _kind = kind;
            Refresh();
        };
        return tab;
    }

    private void FillTags()
    {
        string want = (_tagFilter.Box.Text ?? "").Trim().ToLowerInvariant();
        _tagsGroup.Body.Children.Clear();
        int total = _shown.Count;
        var tags = _shown.SelectMany(i => i.Tags.Select(t => t.ToLowerInvariant()).Distinct())
            .Where(t => !_tagOn.Contains(t) && !Library.Kinds.Contains(t, StringComparer.OrdinalIgnoreCase) && (want.Length == 0 || t.Contains(want, StringComparison.Ordinal)))
            .CountBy(t => t)
            .Where(t => t.Value < total || total < 2)  // a tag every shown asset has narrows nothing
            .OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Take(want.Length > 0 ? 80 : 40).ToList();
        var wrap = new WrapPanel();
        foreach (var (tag, n) in tags)
        {
            wrap.Children.Add(Chip($"{tag}  {n}", on: false, $"Show only assets tagged {tag} ({n} of those shown).", () => _tagOn.Add(tag)));
        }

        _tagsGroup.Body.Children.Add(wrap);
        _tagsGroup.Count = tags.Count == 0 ? "" : $"{tags.Count}";
    }

    private void FillPacksAndLicences()
    {
        if (_packsGroup.Body.Children.Count == 0)
        {
            _packsGroup.Body.Children.Add(_packFilter);
            _packsGroup.Body.Children.Add(_packRows);
        }

        _packRows.Children.Clear();
        string want = (_packFilter.Box.Text ?? "").Trim();
        var perPack = _lib.Items.CountBy(i => i.Pack.Id).ToDictionary(k => k.Key, k => k.Value);
        foreach (Pack p in _lib.Packs.Where(p => p.Indexed && (want.Length == 0 || p.Title.Contains(want, StringComparison.OrdinalIgnoreCase) || p.Seller.Contains(want, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(p => ShortTitle(p.Title), StringComparer.OrdinalIgnoreCase))
        {
            _packRows.Children.Add(new CheckRow(ShortTitle(p.Title), $"{perPack.GetValueOrDefault(p.Id):N0}", !_packOff.Contains(p.Id),
                $"{p.Title}\n{p.Category}\n{p.Licence}", on => Toggle(_packOff, p.Id, !on)));
        }

        _packsGroup.Count = $"{_lib.Packs.Count(p => p.Indexed)}";
        _licenceGroup.Body.Children.Clear();
        foreach (var (lic, n) in _lib.Packs.Where(p => p.Indexed).CountBy(LicenceOf).OrderByDescending(l => l.Value))
        {
            _licenceGroup.Body.Children.Add(new CheckRow(lic, n == 1 ? "1 pack" : $"{n} packs", !_licenceOff.Contains(lic), $"Packs under {lic}.", on => Toggle(_licenceOff, lic, !on)));
        }

        // Origin: the gaps in where packs came from; ticking one shows only the packs with it
        _originGroup.Body.Children.Clear();
        var indexed = _lib.Packs.Where(p => p.Indexed).ToList();
        foreach (var (key, label, tip, test) in new (string, string, string, Func<(bool Unknown, bool Licence, bool Receipt, bool Originals), bool>)[]
                 {
                     ("unknown", "Unknown origin", "Packs with no source page recorded: nothing says where they came from.", o => o.Unknown),
                     ("licence", "Licence not saved", "Packs with no saved copy of their licence text.", o => o.Licence),
                     ("receipt", "No receipt", "Packs with no receipt on file (free packs have none).", o => o.Receipt),
                     ("originals", "Originals not recorded", "Packs from a site that collects other people's assets, with assets whose own source is not recorded.", o => o.Originals),
                 })
        {
            int n = indexed.Count(p => test(OriginOf(p)));
            _originGroup.Body.Children.Add(new CheckRow(label, n == 1 ? "1 pack" : $"{n} packs", _originOn.Contains(key), tip, on => Toggle(_originOn, key, on)));
        }

        _originGroup.Count = indexed.Count(p => OriginOf(p) is { Unknown: true } or { Licence: true }).ToString();
    }

    /// <summary>Each filter group's "unselect" action, worded for what it would do now.</summary>
    private void GroupActions()
    {
        _tagsGroup.SetAction(_tagOn.Count > 0 ? "Unselect" : null, "Drop the chosen tags.", () =>
        {
            _tagOn.Clear();
            Refresh();
        });
        List<string> licences = [.. _lib.Packs.Where(p => p.Indexed).Select(LicenceOf).Distinct()];
        string want = (_packFilter.Box.Text ?? "").Trim();  // with a filter typed, only the packs it shows
        List<string> packs = [.. _lib.Packs.Where(p => p.Indexed && (want.Length == 0 || p.Title.Contains(want, StringComparison.OrdinalIgnoreCase)
            || p.Seller.Contains(want, StringComparison.OrdinalIgnoreCase))).Select(p => p.Id)];
        Every(_licenceGroup, _licenceOff, licences, "licence");
        Every(_packsGroup, _packOff, packs, "pack");
    }

    private void Every(FoldGroup group, HashSet<string> off, List<string> all, string what)
    {
        bool anyOn = all.Any(k => !off.Contains(k));
        group.SetAction(anyOn ? "Unselect all" : "Select all",
            anyOn ? $"Untick every {what}, then tick the ones you want." : $"Tick every {what} again.", () =>
            {
                if (anyOn)
                {
                    off.UnionWith(all);
                }
                else
                {
                    off.ExceptWith(all);
                }

                FillPacksAndLicences();
                Refresh();
            });
    }

    private void OnlyPack(Pack pack)
    {
        _packOff.Clear();
        _packOff.UnionWith(_lib.Packs.Where(p => p != pack).Select(p => p.Id));
        FillPacksAndLicences();
        Refresh();
    }

    private void Toggle(HashSet<string> off, string key, bool hide)
    {
        if (hide)
        {
            off.Add(key);
        }
        else
        {
            off.Remove(key);
        }

        Refresh();
    }

    private (bool Unknown, bool Licence, bool Receipt, bool Originals) OriginOf(Pack p)
    {
        if (p.Published)
        {
            return default;  // the team's own maps and props: no store origin to record
        }

        if (!_originOf.TryGetValue(p.Id, out var o))
        {
            o = (Provenance.UnknownOrigin(p.Provenance), Provenance.LicenceNotSaved(p.Provenance, p.IndexDir, LibraryRoot), Provenance.NoReceipt(p.Provenance),
                Provenance.OriginalsMissing(p.Provenance, p.ByPackage.Values.Select(i => i.Path)));
            _originOf[p.Id] = o;
        }

        return o;
    }

    private bool OriginMatch(Pack p)
    {
        if (_originOn.Count == 0)
        {
            return true;
        }

        var o = OriginOf(p);
        return (_originOn.Contains("unknown") && o.Unknown) || (_originOn.Contains("licence") && o.Licence) || (_originOn.Contains("receipt") && o.Receipt)
            || (_originOn.Contains("originals") && o.Originals);
    }

    private static string LicenceOf(Pack p) => p.Licence.Length == 0 ? "Unknown" : p.Licence.Split(':')[0];

    // Fab titles repeat their keywords in brackets: "Medieval Tavern ( Medieval Tavern Interior ... )"
    internal static string ShortTitle(string t) => t.Contains(" (", StringComparison.Ordinal) && t.EndsWith(')') ? t[..t.IndexOf(" (", StringComparison.Ordinal)] : t;

    private Button Chip(string text, bool on, string tip, Action toggle)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new TextBlock { Text = text, FontSize = Look.Theme.Caption, VerticalAlignment = VerticalAlignment.Center });
        if (on)
        {
            content.Children.Add(new Icon("x", 11) { VerticalAlignment = VerticalAlignment.Center });
        }

        var b = new Button
        {
            Height = 22,
            MinHeight = 0,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(0, 0, 4, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content,
        };
        b.Classes.Add("ep");
        b.Classes.Add(on ? "chip-on" : "chip");
        Chrome.Tip(b, tip);
        b.Click += (_, _) =>
        {
            toggle();
            Refresh();
        };
        return b;
    }

    // ---- grid ----

    private double _tileW = 146;

    private double TileW => _tileW;

    private double Side => _tileW - 10;  // the picture inside a tile

    private double TileH => Side + 58;

    // As many tiles of the chosen picture size as fit, then stretched to fill the row.
    private void Relayout(double width)
    {
        double room = width - 30;
        int n = Math.Max(1, (int)(room / (_picture + 22)));
        double w = Math.Floor(room / n) - 4;
        if (n != _perRow || Math.Abs(w - _tileW) > 0.5)
        {
            _perRow = n;
            _tileW = w;
            FillGrid();
        }
    }

    private void SetPicture(double side)
    {
        _picture = side;
        _perRow = 0;
        Relayout(_grid.Bounds.Width);
    }

    private void FillGrid()
    {
        _tileOf.Clear();
        int per = Math.Max(1, _perRow);
        var rows = new List<Item[]>();
        for (int i = 0; i < _shown.Count; i += per)
        {
            rows.Add(_shown.Skip(i).Take(per).ToArray());
        }

        _grid.ItemsSource = rows;
    }

    private Control MakeRow(Item[]? row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = TileH + 4 };
        foreach (Item item in row ?? [])
        {
            panel.Children.Add(MakeTile(item));
        }

        return panel;
    }

    private Control MakeTile(Item item)
    {
        Control inner;
        if (item.Thumb is null)
        {
            inner = new Icon(KindIcon(item.Kind), 28) { Foreground = Look.Theme.TextTertiary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            var img = new Image { Width = Side, Height = Side, Stretch = Stretch.Uniform };
            Thumbs.Bind(img, item.Thumb);
            // a published map or prop may have no picture: its kind's icon shows until one covers it
            inner = item.Published is null ? img : new Panel { Children = { new Icon(KindIcon(item.Kind), 28) { Foreground = Look.Theme.TextTertiary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, img } };
        }

        var frame = new Border { Width = Side, Height = Side, CornerRadius = new CornerRadius(Look.Tokens.RSm), ClipToBounds = true, Background = Look.Theme.SurfaceSunken, Child = inner, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
        Control picture = frame;
        if (!item.Raw && _inProject.Contains(item.Package))
        {
            var badge = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(9),
                Background = Look.Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Icon("check", 11) { Foreground = Look.Theme.AccentInk, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Chrome.Tip(badge, $"Already in {Path.GetFileName(_settings.UnrealProject.TrimEnd('/'))}.");
            picture = new Panel { Width = Side, Height = Side, Children = { picture, badge } };
        }

        // caption 12/600 on two lines then an ellipsis, kind line 11.5 muted (design v2 asset tile)
        var name = new TextBlock { Text = item.Name, FontSize = Look.Tokens.FsLabel, FontWeight = FontWeight.SemiBold, Foreground = Look.Tokens.Text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Height = 32, Margin = new Thickness(0, 6, 0, 0), MaxWidth = Side };
        string sub = item.Raw ? $"{item.Kind} · {Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant()}" : item.Kind;
        var kind = new TextBlock { Text = sub, FontSize = Look.Tokens.FsCaption, Foreground = Look.Tokens.Mute, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Side };
        var border = new Border
        {
            Width = TileW,
            Height = TileH,
            Margin = new Thickness(4),
            Padding = new Thickness(4, 4, 4, 8),
            CornerRadius = new CornerRadius(Look.Tokens.RMd),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { picture, name, kind } },
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = frame,
        };
        PaintTile(border, item == _picked || _selected.Contains(item));
        Chrome.Tip(border, $"{item.Name}\n{item.Class} · {ShortTitle(item.Pack.Title)}");
        border.PointerEntered += (_, _) => { if (border.BorderBrush != Look.Tokens.AccentLine) border.Background = Look.Tokens.Hover; };
        border.PointerExited += (_, _) => { if (border.BorderBrush != Look.Tokens.AccentLine) border.Background = Brushes.Transparent; };
        border.PointerPressed += (_, e) => Click(item, e.KeyModifiers);
        _tileOf[item] = border;
        return border;
    }

    /// <summary>A tile at rest has no fill; selected: the selected wash, an accent-line border and a 2 px accent ring on the picture.</summary>
    private static void PaintTile(Border tile, bool selected)
    {
        tile.Background = selected ? Look.Tokens.Selected : Brushes.Transparent;
        tile.BorderBrush = selected ? Look.Tokens.AccentLine : Brushes.Transparent;
        if (tile.Tag is Border frame)
        {
            frame.BorderBrush = selected ? Look.Tokens.Accent : Brushes.Transparent;
        }
    }

    private static string KindIcon(string kind) => kind switch
    {
        "Mesh" => "box",
        "Character" => "person-standing",
        "Animation" => "move",
        "VFX" => "sun",
        "Sound" => "wind",
        "Blueprint" => "settings-2",
        "Map" => "frame",
        "Material" => "layers",
        "Texture" => "grid-2x2",
        _ => "shapes",
    };

    private void Unpick()
    {
        _picked = null;
        _selected.Clear();
        Highlight();
        ShowDetails(null);
    }

    // ---- collections, favourites, saved filters ----

    /// <summary>Under the picture: the favourite star and "Add to…" for the picked asset (or the whole selection).</summary>
    private Control KeepBar(Item item)
    {
        List<Item> targets = Targets().Count > 1 ? Targets() : [item];
        string what = targets.Count > 1 ? $"these {targets.Count}" : "this asset";
        bool fav = targets.All(t => _keep.Favourites.Contains(Collections.Key(t)));
        Button star = Chrome.IconTextButton("star", fav ? "Favourite" : "Add to favourites", fav ? $"Take {what} out of the favourites." : $"Mark {what} as a favourite.");
        if (fav)
        {
            star.Foreground = Look.Theme.Accent;
        }

        star.Click += (_, _) =>
        {
            foreach (Item t in targets)
            {
                if (fav)
                {
                    _keep.Favourites.Remove(Collections.Key(t));
                }
                else
                {
                    _keep.Favourites.Add(Collections.Key(t));
                }
            }

            KeepChanged($"{(fav ? "unfavourited" : "favourited")} {targets.Count}");
        };

        var items = new List<(string Text, string? Keys, Action Run)>();
        foreach (string name in _keep.Lists.Keys)
        {
            string n = name;
            bool inIt = targets.All(t => _keep.Lists[n].Contains(Collections.Key(t)));
            items.Add((inIt ? $"Remove from {n}" : $"Add to {n}", null, () =>
            {
                foreach (Item t in targets)
                {
                    if (inIt)
                    {
                        _keep.Lists[n].Remove(Collections.Key(t));
                    }
                    else
                    {
                        _keep.Lists[n].Add(Collections.Key(t));
                    }
                }

                KeepChanged($"{(inIt ? "removed" : "added")} {targets.Count} {(inIt ? "from" : "to")} {n}");
            }));
        }

        items.Add(("New collection…", null, async () =>
        {
            if (await Prompt.Ask(this, "New collection", $"A name for the new collection ({what} goes in it):") is { } name)
            {
                if (!_keep.Lists.TryGetValue(name, out var set))
                {
                    _keep.Lists[name] = set = [];
                }

                set.UnionWith(targets.Select(Collections.Key));
                KeepChanged($"collection {name}: {targets.Count} added");
            }
        }));
        Button add = Menu("Add to…", $"Put {what} in a collection.", [.. items]);
        add.Classes.Add("ep-raised");
        star.HorizontalAlignment = HorizontalAlignment.Stretch;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Margin = new Thickness(10, 0, 10, 6) };
        g.Children.Add(star);
        Grid.SetColumn(add, 2);
        g.Children.Add(add);
        return g;
    }

    private void KeepChanged(string what)
    {
        _keep.Save();
        Log.Write("keep", what);
        if (_collection is not null)
        {
            Refresh();  // the collection being shown may have changed
        }
        else
        {
            FillKeep();
        }

        if (_picked is { } p)
        {
            ShowDetails(p);
        }
    }

    /// <summary>The Collections group: Favourites, each collection and each saved filter, with how many assets are in it.</summary>
    private void FillKeep()
    {
        _keepGroup.Body.Children.Clear();
        var present = new HashSet<string>(_lib.Items.Select(Collections.Key));
        void Row(string text, string count, string tip, Action open, Action? delete, bool active)
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 2),
                MinHeight = 0,
                Content = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { Text = count, [DockPanel.DockProperty] = Dock.Right, FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = text, FontSize = Look.Theme.Label, Foreground = active ? Look.Theme.Accent : Look.Theme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            b.Classes.Add("ep");
            b.Classes.Add(active ? "ep-raised" : "ep-text");
            Chrome.Tip(b, tip);
            b.Click += (_, _) => open();
            if (delete is not null)
            {
                var del = new MenuItem { Header = "Delete", FontSize = Look.Theme.Label };
                del.Click += (_, _) => delete();
                b.ContextMenu = new ContextMenu { Items = { del } };
            }

            _keepGroup.Body.Children.Add(b);
        }

        void Show(string name)
        {
            _collection = _collection == name ? null : name;
            Refresh();
        }

        Row("★ " + Collections.FavouritesName, $"{_keep.Favourites.Count(present.Contains)}", "Show only your favourites (click again to show everything).",
            () => Show(Collections.FavouritesName), null, _collection == Collections.FavouritesName);
        foreach (var (name, set) in _keep.Lists)
        {
            string n = name;
            Row(n, $"{set.Count(present.Contains)}", $"Show only the collection {n}. Right-click to delete it.", () => Show(n), () =>
            {
                _keep.Lists.Remove(n);
                if (_collection == n)
                {
                    _collection = null;
                }

                KeepChanged($"deleted collection {n}");
                Refresh();
            }, _collection == n);
        }

        foreach (var (name, v) in _keep.Saved)
        {
            string n = name;
            Row("⌕ " + n, "", $"Saved filter: {Describe(v)}. Click to apply it; right-click to delete it.", () => Apply(v), () =>
            {
                _keep.Saved.Remove(n);
                KeepChanged($"deleted saved filter {n}");
            }, false);
        }

        _keepGroup.Count = $"{1 + _keep.Lists.Count + _keep.Saved.Count}";
        _keepGroup.SetAction("Save filter…", "Keep the current search, kind, tags, packs and licences as a saved filter.", async () =>
        {
            if (await Prompt.Ask(this, "Save this filter", $"A name for this filter ({Describe(Current())}):") is { } name)
            {
                _keep.Saved[name] = Current();
                KeepChanged($"saved filter {name}");
            }
        });
    }

    private Collections.SavedView Current() => new(_search.Box.Text ?? "", _kind, [.. _tagOn.Order(StringComparer.OrdinalIgnoreCase)],
        [.. _packOff.Order(StringComparer.Ordinal)], [.. _licenceOff.Order(StringComparer.Ordinal)], _collection);

    private static string Describe(Collections.SavedView v)
    {
        var parts = new List<string>();
        if (v.Search.Length > 0)
        {
            parts.Add($"\"{v.Search}\"");
        }

        if (v.Kind is not null)
        {
            parts.Add(v.Kind);
        }

        if (v.TagOn.Length > 0)
        {
            parts.Add("tags " + string.Join(", ", v.TagOn));
        }

        if (v.PackOff.Length > 0)
        {
            parts.Add($"{v.PackOff.Length} packs off");
        }

        if (v.Collection is not null)
        {
            parts.Add("in " + v.Collection);
        }

        return parts.Count == 0 ? "everything" : string.Join("; ", parts);
    }

    private void Apply(Collections.SavedView v)
    {
        _kind = v.Kind;
        _collection = v.Collection;
        _tagOn.Clear();
        _tagOn.UnionWith(v.TagOn);
        _packOff.Clear();
        _packOff.UnionWith(v.PackOff.Where(id => _lib.Packs.Any(p => p.Id == id)));
        _licenceOff.Clear();
        _licenceOff.UnionWith(v.LicenceOff);
        if ((_search.Box.Text ?? "") != v.Search)
        {
            _search.Box.Text = v.Search;
        }

        _searchDelay.Stop();
        FillPacksAndLicences();
        Refresh();
    }

    /// <summary>A tile clicked: alone it picks; with Ctrl it is added to or taken out of the selection; with Shift the
    /// tiles from the last click to this one are selected.</summary>
    private void Click(Item item, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift) && _anchor is not null)
        {
            int a = _shown.IndexOf(_anchor), b = _shown.IndexOf(item);
            if (a >= 0 && b >= 0)
            {
                if (!mods.HasFlag(KeyModifiers.Control))
                {
                    _selected.Clear();
                }

                _selected.UnionWith(_shown.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1));
            }
        }
        else if (mods.HasFlag(KeyModifiers.Control))
        {
            if (_selected.Count == 0 && _picked is not null)
            {
                _selected.Add(_picked);
            }

            if (!_selected.Remove(item))
            {
                _selected.Add(item);
            }

            _anchor = item;
        }
        else
        {
            _selected.Clear();
            _anchor = item;
        }

        Pick(item, keepSelection: true);
    }

    private void Pick(Item item, bool keepSelection = false)
    {
        if (!keepSelection)
        {
            _selected.Clear();
            _anchor = item;
        }

        _picked = item;
        Highlight();
        ShowDetails(item);
        Record();
    }

    /// <summary>The assets a send acts on: the selection, or the picked one.</summary>
    private List<Item> Targets() => _selected.Count > 1 ? [.. _shown.Where(i => _selected.Contains(i) && i.Published is null)] : _picked is { Published: null } p ? [p] : [];

    private Border? _sendBox;

    private Button Target(string t) => t switch { "Blender" => _toBlender, "Unity" => _toUnity, "Groundwork" => _toGroundwork, _ => _toUnreal };

    private Control SendButton()
    {
        _sendMain.Classes.Add("split-main");
        _sendMenu.Classes.Add("split-menu");
        _sendMenu.Width = 24;
        _sendMenu.Padding = new Thickness(0);
        _sendMenu.BorderThickness = new Thickness(1, 0, 0, 0);
        _sendMain.Click += (_, _) =>
        {
            if (Target(_settings.LastSend) is { IsEnabled: true } b)
            {
                b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };
        _sendMenu.Click += (_, _) =>
        {
            int n = Targets().Count;
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            foreach (var (t, icon, keys) in SendTargets)
            {
                bool last = t == _settings.LastSend;
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = n > 1 ? $"Send {n} to {t}" : $"Send to {t}", VerticalAlignment = VerticalAlignment.Center });
                if (last)
                {
                    header.Children.Add(new TextBlock { Text = "last used", FontSize = Look.Tokens.FsCaption, Foreground = Look.Tokens.Mute, VerticalAlignment = VerticalAlignment.Center });
                }

                var item = new MenuItem
                {
                    Header = header,
                    Icon = last ? new Icon("check", 16) { Foreground = Look.Tokens.Accent } : new Icon(icon, 16),
                    InputGesture = keys is null ? null : KeyGesture.Parse(keys),
                    IsEnabled = Target(t).IsEnabled,
                    FontSize = Look.Theme.Label,
                };
                item.Click += (_, _) =>
                {
                    _settings.LastSend = t;
                    _settings.Save();
                    UpdateSend();
                    Target(t).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(_sendMenu);
        };
        UpdateSend();
        return _sendBox = new Border
        {
            CornerRadius = new CornerRadius(Look.Tokens.RSm),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _sendMain, _sendMenu } },
        };
    }

    /// <summary>The main half's label, icon and enabled state follow the last-used target and the selection.</summary>
    private void UpdateSend()
    {
        string t = SendTargets.Any(x => x.Target == _settings.LastSend) ? _settings.LastSend : "Unreal";
        int n = Targets().Count;
        var label = Chrome.IconAndText(SendTargets.First(x => x.Target == t).Icon, n > 1 ? $"Send {n} to {t}" : $"Send to {t}");
        foreach (TextBlock tb in label.Children.OfType<TextBlock>())
        {
            tb.FontWeight = FontWeight.SemiBold;  // the primary button's label is 600, as the spec's
        }

        _sendMain.Content = label;
        _sendMain.IsEnabled = Target(t).IsEnabled;
    }

    private void Highlight()
    {
        foreach (var (it, b) in _tileOf)
        {
            PaintTile(b, it == _picked || _selected.Contains(it));
        }

        UpdateSend();

        int n = Targets().Count;
        _toUnreal.Content = Chrome.IconAndText("download", n > 1 ? $"Send {n} to Unreal" : "Send to Unreal");
        _toBlender.Content = Chrome.IconAndText("send", n > 1 ? $"Send {n} to Blender" : "Send to Blender");
        _toGroundwork.Content = Chrome.IconAndText("trees", n > 1 ? $"Send {n} to Groundwork" : "Send to Groundwork");
    }

    // ---- details ----

    private void ShowDetails(Item? item)
    {
        using var step = Log.Time("details", item is null ? "cleared" : $"{item.Name} ({item.Class}, {item.Pack.Id})");
        _details.Children.Clear();
        _extras = [];
        bool asset = item is { Published: null };
        _toUnreal.IsEnabled = asset && item is { Raw: false };
        _toBlender.IsEnabled = asset && (item!.Raw || item.Kind is "Mesh" or "Character" or "Animation");
        _toUnity.IsEnabled = asset && (item!.Raw || item.Kind is "Mesh" or "Character");
        _toGroundwork.IsEnabled = asset && (item!.Raw || item.Kind == "Mesh");
        UpdateSend();
        if (item is null)
        {
            _details.Children.Add(new TextBlock
            {
                Text = "Pick an asset to see what it is and what comes with it, and to send it to Unreal or Blender.",
                Margin = new Thickness(14, 16),
                FontSize = Look.Theme.Label,
                Foreground = Look.Theme.TextTertiary,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _details.Children.Add(new Border
        {
            Padding = new Thickness(10, 8),
            BorderBrush = Look.Theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new SelectableTextBlock { Text = item.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{item.Kind} · {item.Class}", FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary },
                },
            },
        });

        Control big;
        if (item.Thumb is not null && File.Exists(item.Thumb))
        {
            // decoded at a bounded width: pictures come from third-party files and may claim any size
            var img = new Image { Stretch = Stretch.Uniform };
            Thumbs.Get(item.Thumb, b => img.Source = b, 600);
            big = img;
        }
        else
        {
            big = new Icon(KindIcon(item.Kind), 48) { Foreground = Look.Theme.TextTertiary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var pictureBox = new Border { Margin = new Thickness(10, 10, 10, 6), Height = 300, CornerRadius = new CornerRadius(Look.Theme.Radius3), ClipToBounds = true, Background = Look.Theme.SurfaceSunken, Child = big };
        _details.Children.Add(pictureBox);
        if (item.Published is { } published)
        {
            PublishedDetails(item, published);
            return;
        }
        if (item.Raw || item.Kind is "Mesh" or "Character")
        {
            Button spin = Chrome.IconTextButton("rotate-ccw", "Spin in 3D", "Turn the model round: rendered once by Blender (an Unreal asset is exported first), then drag to turn it.");
            spin.HorizontalAlignment = HorizontalAlignment.Stretch;
            spin.Margin = new Thickness(10, 0, 10, 6);
            spin.Click += async (_, _) => await SpinAsync(item, pictureBox, spin);
            _details.Children.Add(spin);
        }

        _details.Children.Add(KeepBar(item));
        Button open = Chrome.IconTextButton("box", "Open in Blender", "Start a new Blender window with only this asset in it (an Unreal asset is exported to FBX first, about 20 seconds).");
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        open.Margin = new Thickness(10, 0, 10, 10);
        open.IsEnabled = item.Raw || item.Kind is "Mesh" or "Character" or "Animation";
        open.Click += async (_, _) => await OpenInBlenderAsync(item, open);
        _details.Children.Add(open);

        var notes = new FoldGroup("Notes", open: _keep.Notes.ContainsKey(Collections.Key(item)));
        var note = new TextBox { Text = _keep.Notes.GetValueOrDefault(Collections.Key(item)) ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, FontSize = Look.Theme.Label, PlaceholderText = "Your note about this asset" };
        note.LostFocus += (_, _) =>
        {
            string k = Collections.Key(item), t = (note.Text ?? "").Trim();
            if (t == (_keep.Notes.GetValueOrDefault(k) ?? ""))
            {
                return;
            }

            if (t.Length == 0)
            {
                _keep.Notes.Remove(k);
            }
            else
            {
                _keep.Notes[k] = t;
            }

            _keep.Save();
            Log.Write("keep", $"note on {item.Name}: {t.Length} characters");
        };
        notes.Body.Children.Add(note);

        var about = new FoldGroup("About");
        Pack pack = item.Pack;
        var packLink = new Button
        {
            Content = Chrome.LinkText(ShortTitle(pack.Title), Look.Theme.Label),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(-4, 0, 0, 0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        packLink.Classes.Add("ep");
        packLink.Classes.Add("ep-text");
        Chrome.Tip(packLink, $"Show only {ShortTitle(pack.Title)}'s assets.");
        packLink.Click += (_, _) => OnlyPack(pack);
        about.Body.Children.Add(InfoRow("Pack", packLink));
        AddOrigin(about, item.Pack, item);

        if (item.Twins.Count > 0)
        {
            var twinLinks = new StackPanel { Spacing = 0 };
            foreach (Item t in item.Twins.DistinctBy(t => t.Pack).Take(8))
            {
                var link = new Button
                {
                    Content = Chrome.LinkText(ShortTitle(t.Pack.Title), Look.Theme.Label),
                    Padding = new Thickness(4, 1),
                    Margin = new Thickness(-4, 0, 0, 0),
                    MinHeight = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                link.Classes.Add("ep");
                link.Classes.Add("ep-text");
                Chrome.Tip(link, $"Show this copy: {t.Pack.Title}\n{t.Pack.Licence}");
                link.Click += (_, _) =>
                {
                    OnlyPack(t.Pack);
                    Pick(t);
                    ScrollTo(t);
                };
                twinLinks.Children.Add(link);
            }

            about.Body.Children.Add(InfoRow("Also in", twinLinks));
        }

        if (!item.Raw && EngineCheck.Parse(item.Pack.Engine) is { } made)
        {
            about.Body.Children.Add(InfoRow("Unreal", $"saved with {made.Major}.{made.Minor}"));
        }

        foreach (var (text, blocking) in Safety.Lines(item.Pack))
        {
            about.Body.Children.Add(new TextBlock { Text = (blocking ? "⚠ " : "") + text, FontSize = Look.Theme.Label, Foreground = blocking ? Look.Theme.Accent : Look.Theme.Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1) });
        }

        if (Safety.Of(item.Pack) is null)
        {
            about.Body.Children.Add(Caption("Not checked for code yet (no safety scan for this pack)."));
        }

        if (!item.Raw)
        {
            foreach (var (text, blocking) in EngineCheck.Check(item.Pack, _settings.UnrealProject))
            {
                // blockers in colour; notes about the pack as a whole stay quiet (they rarely matter for one asset)
                about.Body.Children.Add(new TextBlock { Text = (blocking ? "⚠ " : "") + text, FontSize = Look.Theme.Label, Foreground = blocking ? Look.Theme.Accent : Look.Theme.TextTertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4) });
            }
        }

        if (!item.Raw && _settings.UnrealProject.Length > 0)
        {
            List<Item> need = [item, .. Library.Needs(item)];
            int have = need.Count(i => _inProject.Contains(i.Package));
            string proj = Path.GetFileName(_settings.UnrealProject.TrimEnd('/'));
            about.Body.Children.Add(InfoRow("In project", have == need.Count ? $"Yes, with all it needs ({proj})"
                : have == 0 ? $"No ({proj})"
                : $"Partly: {have} of {need.Count} packages ({proj})"));
        }

        foreach (var (key, label) in new[] { ("Triangles", "Triangles"), ("Vertices", "Vertices"), ("ApproxSize", "Size (cm)"), ("Meshes", "Meshes"), ("Animations", "Animations"), ("SequenceLength", "Length (s)"), ("Duration", "Length (s)") })
        {
            if (item.Info.TryGetValue(key, out string? v) && v.Length > 0)
            {
                about.Body.Children.Add(InfoRow(label, Pretty(key, v)));
            }
        }

        // design v2 path line: read-only, mono, one line; trims from the start so the file name stays (full path in the tooltip)
        var path = new SelectableTextBlock { Text = item.Path, FontSize = Look.Tokens.FsCaption, FontFamily = new FontFamily("DejaVu Sans Mono, Consolas, Menlo, monospace"), Foreground = Look.Tokens.Dim, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.PrefixCharacterEllipsis };
        Chrome.Tip(path, item.Path);
        about.Body.Children.Add(new Border { Background = Look.Tokens.Bg, CornerRadius = new CornerRadius(Look.Tokens.RXs), Padding = new Thickness(6, 4), Margin = new Thickness(0, 6, 0, 0), Child = path });
        // pack.json is data from disk: only ever open a web address from it, never a file or another scheme
        if (Uri.TryCreate(item.Pack.Url, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
        {
            Button page = Chrome.TextButton("Open the pack's page", url.AbsoluteUri);
            page.HorizontalAlignment = HorizontalAlignment.Left;
            page.Margin = new Thickness(-4, 4, 0, 0);
            page.Click += async (_, _) => await Launcher.LaunchUriAsync(url);
            about.Body.Children.Add(page);
        }

        _details.Children.Add(about);
        _details.Children.Add(notes);
        if (item.Raw)
        {
            return;
        }

        List<Item> needs = Library.Needs(item);
        var comes = new FoldGroup("Comes with it") { Count = needs.Count == 0 ? "" : $"{needs.Count}" };
        if (needs.Count == 0)
        {
            comes.Body.Children.Add(Caption("Needs nothing else from its pack."));
        }
        else
        {
            // "Other" is too vague here: name those by their class (Skeleton, PhysicsAsset...)
            foreach (var (kind, n) in needs.CountBy(x => x.Kind == "Other" ? x.Class : x.Kind).OrderByDescending(k => k.Value))
            {
                comes.Body.Children.Add(InfoRow(Plural(kind, n), $"{n}"));
            }

            var list = new SelectableTextBlock { Text = string.Join('\n', needs.Select(n => n.Name)), FontSize = 10.5, Foreground = Look.Theme.TextSecondary, IsVisible = false, Margin = new Thickness(2, 2, 0, 0) };
            Button every = Chrome.TextButton("Show every asset", "List every asset that comes with it.");
            every.HorizontalAlignment = HorizontalAlignment.Left;
            every.Margin = new Thickness(-4, 4, 0, 0);
            every.Click += (_, _) =>
            {
                list.IsVisible = !list.IsVisible;
                ((TextBlock)every.Content!).Text = list.IsVisible ? "Hide the list" : "Show every asset";
            };
            comes.Body.Children.Add(every);
            comes.Body.Children.Add(list);
        }

        _details.Children.Add(comes);
        Dictionary<string, List<Item>> works = Library.WorksWith(item);
        if (works.Count == 0)
        {
            return;
        }

        var also = new FoldGroup("Also bring");
        foreach (var (group, list) in works)
        {
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = $"{group} ({list.Count})", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextPrimary },
                IsChecked = group.StartsWith("Animations", StringComparison.Ordinal),
                MinHeight = 0,
            };
            Chrome.Tip(cb, string.Join('\n', list.Take(30).Select(i => i.Name)) + (list.Count > 30 ? "\n…" : ""));
            _extras.Add((cb, list));
            also.Body.Children.Add(cb);
        }

        _details.Children.Add(also);
    }

    private static string Plural(string kind, int n) => kind switch
    {
        "Mesh" => n == 1 ? "Mesh" : "Meshes",
        "VFX" => n == 1 ? "Effect" : "Effects",
        "Other" => "Other",
        "PhysicsAsset" => n == 1 ? "Physics asset" : "Physics assets",
        "MaterialFunction" => n == 1 ? "Material function" : "Material functions",
        _ => n == 1 ? kind : kind + "s",
    };

    private static string Pretty(string key, string v) => key switch
    {
        "Triangles" or "Vertices" when long.TryParse(v, out long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
        "ApproxSize" => v.Replace("x", " × ", StringComparison.Ordinal),
        _ => v,
    };

    private bool _member;

    /// <summary>Adds a line per sent asset (asset, pack, source, licence, acquired) to the project's
    /// AssetShelf.provenance.jsonl, so a project can list what it uses and credit it. Asks once per project, showing
    /// the path; the answer is kept in the settings.</summary>
    private async Task RecordSentAsync(IReadOnlyList<Item> items, string project, string target)
    {
        if (items.Count == 0 || project.Length == 0)
        {
            return;
        }

        string root = project.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(project)! : project.TrimEnd('/', '\\');
        if (!Directory.Exists(root) || _settings.ProvenanceOff.Contains(root, StringComparer.Ordinal))
        {
            return;
        }

        string file = Path.Combine(root, "AssetShelf.provenance.jsonl");
        if (!_settings.ProvenanceOn.Contains(root, StringComparer.Ordinal))
        {
            bool yes = await Prompt.Confirm(this, "Keep a list of what you send?",
                $"Asset Shelf can add a line to {file} for each asset you send to this project: the asset, its pack, the source page, the licence and when it was acquired (no prices, receipts or accounts). It is what you need for credits and for showing where an asset came from.",
                "Keep the list");
            (yes ? _settings.ProvenanceOn : _settings.ProvenanceOff).Add(root);
            _settings.Save();
            if (!yes)
            {
                return;
            }
        }

        try
        {
            await File.AppendAllLinesAsync(file, items.Select(i => Provenance.Line(i, i.Pack.Provenance, target)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("provenance", $"could not add to {file}: {e.Message}");
        }
    }

    /// <summary>An aggregator's asset: its original source, editable (saved on leaving the box) unless the library is
    /// read-only here; flagged while empty.</summary>
    private Control OriginalBox(Item item, Pack pack, Provenance p)
    {
        string now = p.Source.Originals.GetValueOrDefault(item.Path) ?? "";
        if (_member)
        {
            return now.Length > 0 ? new SelectableTextBlock { Text = now, FontSize = Look.Theme.Label, Foreground = Look.Tokens.Text, TextWrapping = TextWrapping.Wrap }
                : Flag("Not recorded", "Nobody recorded where this asset was made before the site collected it.");
        }

        var box = new TextBox { Text = now, PlaceholderText = "Creator, https://… where it was first published (Enter saves)", FontSize = Look.Theme.Label, TextWrapping = TextWrapping.Wrap };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Save();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Save();
        return box;

        void Save()
        {
            string t = Untrusted.Text((box.Text ?? "").Trim(), 500);
            if (t == (p.Source.Originals.GetValueOrDefault(item.Path) ?? ""))
            {
                return;
            }

            if (t.Length == 0)
            {
                p.Source.Originals.Remove(item.Path);
            }
            else
            {
                p.Source.Originals[item.Path] = t;
            }

            try
            {
                p.Save(pack.IndexDir);
                _originOf.Remove(pack.Id);
                Log.Write("provenance", $"{pack.Id}: original source of {item.Name} {(t.Length == 0 ? "cleared" : "recorded")}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status.Text = $"Could not save the original source: {e.Message}";
            }
        }
    }

    /// <summary>The library folder holding packs/ and index/ (where the shared licences/ and receipts/ live).</summary>
    private string LibraryRoot => Path.GetDirectoryName(_settings.Index.TrimEnd('/', '\\')) ?? _settings.Index;

    /// <summary>Source, licence, acquired and receipt rows: where the pack came from and the right to use it. A pack
    /// with no source page or no saved licence copy says so plainly; a missing receipt is only flagged when bought.</summary>
    private void AddOrigin(FoldGroup about, Pack pack, Item? item = null)
    {
        Provenance? p = pack.Provenance;
        string url = p?.Source.Url is { Length: > 0 } u ? u : pack.Url;
        if (Provenance.IsWebLink(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? link))
        {
            var open = new Button { Content = Chrome.LinkText(p?.Source.Site is { Length: > 0 } site ? site : link.Host, Look.Theme.Label), Padding = new Thickness(4, 1), Margin = new Thickness(-4, 0, 0, 0), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Hand) };
            open.Classes.Add("ep");
            open.Classes.Add("ep-text");
            Chrome.Tip(open, link.AbsoluteUri);
            open.Click += async (_, _) => await Launcher.LaunchUriAsync(link);
            about.Body.Children.Add(InfoRow("Source", open));
        }
        else
        {
            about.Body.Children.Add(InfoRow("Source", Flag("Unknown origin", "No source page is recorded for this pack: nothing says where it came from.")));
        }

        if (Provenance.IsAggregator(p) && item is not null)
        {
            about.Body.Children.Add(InfoRow("Site", Flag("Collects others' assets", "This pack comes from a site that gathers other people's assets. Keep each asset's original source, so anything it had no right to can be traced and removed.")));
            about.Body.Children.Add(InfoRow("Original", OriginalBox(item, pack, p!)));
        }

        string licence = p is { Licence.Name.Length: > 0 } ? string.Join(" ", new[] { p.Licence.Name, p.Licence.Tier }.Where(x => x.Length > 0)) : pack.Licence;
        var lic = new StackPanel { Spacing = 2 };
        lic.Children.Add(new TextBlock { Text = licence.Length > 0 ? licence : "Not stated", FontSize = Look.Theme.Label, Foreground = Look.Tokens.Text, TextWrapping = TextWrapping.Wrap });
        lic.Children.Add(Provenance.SummaryOnly(p, pack.IndexDir, LibraryRoot)
            ? Flag("Summary only", "Only the store's summary of the licence is saved, not the binding text. Save the full licence (Origin…) for this pack, or once in the library's licences folder.")
            : Provenance.LicenceNotSaved(p, pack.IndexDir, LibraryRoot)
            ? Flag("Licence not saved", "No copy of the licence terms is kept in the library. Store terms change and listings vanish: the copy is what shows the right to use it.")
            : new TextBlock { Text = $"Copy saved {p!.Licence.Copies.Last(c => c.Kind == "licence").Saved}", FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute });
        foreach (string cond in p?.Licence.Conditions ?? [])
        {
            lic.Children.Add(Flag(cond switch { "no-ai-training" => "Not for AI training", "no-resale" => "No resale", "credit-required" => "Credit required", _ => cond }, "A condition stated in the licence text."));
        }

        about.Body.Children.Add(InfoRow("Licence", lic));
        if (p?.Licence.Credit is { Length: > 0 } credit)
        {
            about.Body.Children.Add(InfoRow("Credit", new SelectableTextBlock { Text = credit, FontSize = Look.Theme.Label, Foreground = Look.Tokens.Text, TextWrapping = TextWrapping.Wrap }));
        }

        string how = p?.Acquired.How is { Length: > 0 } h ? h : "unknown";
        string date = p?.Acquired.Date is { Length: > 0 } d ? d : pack.Acquired;
        string acquired = how == "unknown" ? (date.Length > 0 ? $"{ShortDate(date)} (how not recorded)" : "Not recorded") : date.Length > 0 ? $"{Capital(how)}, {ShortDate(date)}" : Capital(how);
        if (p?.Acquired.Via is { Length: > 0 } via)
        {
            acquired += $" (through {via})";
        }
        about.Body.Children.Add(InfoRow("Acquired", acquired));
        if (p is { Receipts.Count: > 0 })
        {
            about.Body.Children.Add(InfoRow("Receipt", $"On file, {ShortDate(p.Receipts[^1].Date)}"));
        }
        else if (Provenance.BoughtWithoutReceipt(p))
        {
            about.Body.Children.Add(InfoRow("Receipt", Flag("No receipt on file", "This pack was bought: attach its receipt with Origin…")));
        }

        if (!_member)
        {
            Button edit = Chrome.TextButton("Origin…", "Record or fix where this pack came from, its licence text, how it was acquired and its receipt.");
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            edit.Margin = new Thickness(-4, 2, 0, 0);
            edit.Click += async (_, _) =>
            {
                if (await OriginDialog.Ask(this, pack, LibraryRoot))
                {
                    _originOf.Remove(pack.Id);
                    FillPacksAndLicences();
                    if (_picked is { } it) ShowDetails(it);
                    Refresh();
                }
            };
            about.Body.Children.Add(edit);
        }
    }

    private static string ShortDate(string iso) => iso.Length >= 10 ? iso[..10] : iso;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A warning chip: warning-soft pill, its text in the warning colour.</summary>
    private static Control Flag(string text, string tip)
    {
        var chip = new Border
        {
            Background = Look.Tokens.WarningSoft,
            CornerRadius = new CornerRadius(Look.Tokens.RPill),
            Padding = new Thickness(8, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = Look.Theme.Caption, FontWeight = FontWeight.SemiBold, Foreground = Look.Tokens.Warning },
        };
        Chrome.Tip(chip, tip);
        return chip;
    }

    internal static Grid InfoRow(string label, string value) =>
        InfoRow(label, new SelectableTextBlock { Text = value, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextPrimary, TextWrapping = TextWrapping.Wrap });

    internal static Grid InfoRow(string label, Control v)
    {
        // design v2 key/value table: the label column 40% (at least 88), values wrap
        var g = new Grid { MinHeight = Look.Theme.RowHeight - 4 };
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Star)) { MinWidth = Look.Tokens.LabelMin });
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6, GridUnitType.Star)));
        g.Children.Add(new TextBlock { Text = label, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        v.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    private static TextBlock Caption(string s) => new() { Text = s, FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) };

    // ---- sending ----

    private async Task SendToUnrealAsync()
    {
        List<Item> targets = [.. Targets().Where(t => !t.Raw)];
        if (targets.Count == 0 || _picked is not { } item)
        {
            return;
        }

        if (targets.Count > 1)
        {
            item = targets[0];
        }

        string label = targets.Count > 1 ? $"{targets.Count} assets" : item.Name;

        if (_settings.UnrealProject.Length == 0 && !await PickProjectAsync())
        {
            return;
        }

        var all = new List<Item>();
        foreach (Item t in targets)
        {
            all.Add(t);
            all.AddRange(Library.Needs(t));
        }

        foreach (var (_, list) in _extras.Where(e => e.Box.IsChecked == true))
        {
            foreach (Item x in list)
            {
                all.Add(x);
                all.AddRange(Library.Needs(x));
            }
        }

        if (!await FetchAsync(all))
        {
            return;
        }

        _toUnreal.IsEnabled = false;
        string project = _settings.UnrealProject;
        try
        {
            Sender.UnrealPlan plan;
            using (Log.Time("unreal", $"{label}: plan"))
            {
                plan = await Task.Run(() => Sender.PlanUnreal(all, project));
            }

            var warnings = targets.Select(t => t.Pack).Distinct().SelectMany(p => EngineCheck.Check(p, project).Concat(Safety.Lines(p))).Distinct().ToList();
            if (!await SendPreview.Ask(this, label, project, plan, warnings))
            {
                _status.Text = $"{label}: not sent";
                return;
            }

            _status.Text = $"{label}: copying {plan.Copy.Count} files…";
            Busy(0);
            var progress = new Progress<double>(f => Busy(f));
            using var step = Log.Time("unreal", $"{label}: send to {project}");
            int copied = await Task.Run(() => Sender.ToUnreal(plan, progress));
            step.Then($"{label}: {copied} files copied, {plan.Already.Count} packages already there, {plan.Missing.Count} missing");
            _status.Text = $"{label}: {copied} files copied into {Path.GetFileName(project.TrimEnd('/'))}/Content, {plan.Already.Count} packages already there"
                + (plan.Missing.Count > 0 ? $", {plan.Missing.Count} missing ({plan.Missing[0]})" : "");
            await RecordSentAsync(targets, _settings.UnrealProject, "Unreal");
            _ = ScanProjectAsync();
        }
        catch (IOException e)
        {
            _status.Text = $"{label}: copy failed: {e.Message}";
        }
        finally
        {
            Idle();
            _toUnreal.IsEnabled = _picked is { Raw: false };
        }
    }

    private (string File, string TexDir) RawFile(Item item)
    {
        string file = Path.Combine(item.Pack.FilesDir, "raw", item.Path);
        // textures sit anywhere in the unpacked archive (raw/<format>/<archive>/...): hand Blender its top folder
        string[] parts = item.Path.Split('/');
        return (file, Path.Combine(item.Pack.FilesDir, "raw", Path.Combine(parts[..Math.Min(2, parts.Length - 1)])));
    }

    private async Task SpinAsync(Item item, Border box, Button button)
    {
        if (!await FetchAsync([item]))
        {
            return;
        }

        string blenderDir = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_settings.Tools))!, "blender");
        button.IsEnabled = false;
        _status.Text = $"{item.Name}: rendering a turntable{(item.Raw ? "" : " (exporting through Unreal first)")}…";
        Busy(null);
        try
        {
            List<string> frames;
            if (item.Raw)
            {
                var (file, tex) = RawFile(item);
                frames = await Turntable.FramesAsync(item, [file], tex, unreal: false, blenderDir, _settings);
            }
            else
            {
                var (dir, fbx) = await Sender.ExportAsync(item, _settings, UnrealEngine);
                frames = await Turntable.FramesAsync(item, fbx, dir, unreal: true, blenderDir, _settings);
            }

            if (_picked == item)
            {
                box.Child = new TurntableView(frames);
            }

            _status.Text = $"{item.Name}: drag the picture to turn it";
            button.IsVisible = false;
        }
        catch (InvalidOperationException e)
        {
            _status.Text = $"{item.Name}: {e.Message}";
            button.IsEnabled = true;
        }
        finally
        {
            Idle();
        }
    }

    private async Task OpenInBlenderAsync(Item item, Button button)
    {
        if (!await FetchAsync([item]))
        {
            return;
        }

        string blenderDir = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_settings.Tools))!, "blender");
        button.IsEnabled = false;
        try
        {
            if (item.Raw)
            {
                var (file, tex) = RawFile(item);
                Sender.OpenInBlender(item.Name, [file], tex, blenderDir, _settings);
            }
            else
            {
                _status.Text = $"{item.Name}: exporting through Unreal (about 20 seconds)…";
                Busy(null);
                var (dir, fbx) = await Sender.ExportAsync(item, _settings, UnrealEngine);
                Sender.OpenInBlender(item.Name, fbx, dir, blenderDir, _settings, unreal: true);
            }

            _status.Text = $"{item.Name}: opening in a new Blender window…";
        }
        catch (InvalidOperationException e)
        {
            _status.Text = $"{item.Name}: {e.Message}";
        }
        finally
        {
            Idle();
            button.IsEnabled = true;
        }
    }

    private async Task SendToBlenderAsync()
    {
        List<Item> targets = Targets();
        if (targets.Count == 0)
        {
            return;
        }

        if (!await FetchAsync(targets))
        {
            return;
        }

        _toBlender.IsEnabled = false;
        Busy(targets.Count > 1 ? 0 : null);
        int sent = 0;
        try
        {
            foreach (Item item in targets)
            {
                if (item.Raw)
                {
                    var (file, texDir) = RawFile(item);
                    Sender.ToBlender(item.Name, [file], texDir, provenance: Provenance.Line(item, item.Pack.Provenance, "Blender"));
                }
                else
                {
                    _status.Text = $"{item.Name}: exporting through Unreal{(targets.Count > 1 ? $" ({sent + 1} of {targets.Count})" : "")}…";
                    await Sender.UnrealToBlenderAsync(item, _settings, UnrealEngine, Provenance.Line(item, item.Pack.Provenance, "Blender"));
                }

                sent++;
                if (targets.Count > 1)
                {
                    Busy((double)sent / targets.Count);
                }
            }

            _status.Text = targets.Count > 1 ? $"{sent} assets sent to Blender" : $"{targets[0].Name}: sent to Blender";
        }
        catch (InvalidOperationException e)
        {
            _status.Text = $"{sent} sent, then: {e.Message}";
        }
        finally
        {
            Idle();
            _toBlender.IsEnabled = true;
        }
    }

    /// <summary>The Unreal Engine for exports that need it, asked for on the UI thread (it may show the install window).</summary>
    private Task<string> UnrealEngine() => Dispatcher.UIThread.InvokeAsync(() => UnrealSetup.EnsureAsync(this, _settings));

    private async Task SendToGroundworkAsync()
    {
        List<Item> targets = [.. Targets().Where(t => t.Raw || t.Kind == "Mesh").Take(GroundworkSender.MaxFiles)];
        if (targets.Count == 0)
        {
            _status.Text = "Send to Groundwork takes meshes";
            return;
        }

        if (!await FetchAsync(targets))
        {
            return;
        }

        string blenderDir = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_settings.Tools))!, "blender");
        _toGroundwork.IsEnabled = false;
        Busy(targets.Count > 1 ? 0 : null);
        try
        {
            var parts = new List<GroundworkSender.Part>();
            foreach (Item item in targets)
            {
                if (item.Raw)
                {
                    var (file, tex) = RawFile(item);
                    parts.Add(new(item, [file], tex, false));
                }
                else
                {
                    _status.Text = $"{item.Name}: exporting through Unreal{(targets.Count > 1 ? $" ({parts.Count + 1} of {targets.Count})" : "")}…";
                    var (dir, fbx) = await Sender.ExportAsync(item, _settings, UnrealEngine);
                    parts.Add(new(item, fbx, dir, true));
                }
            }

            string id = await GroundworkSender.DeliverAsync(parts, blenderDir, _settings, i => Dispatcher.UIThread.Post(() =>
            {
                _status.Text = $"{parts[i].Item.Name}: to .glb for Groundwork{(parts.Count > 1 ? $" ({i + 1} of {parts.Count})" : "")}…";
                if (parts.Count > 1)
                {
                    Busy((double)i / parts.Count);
                }
            }));
            string what = targets.Count > 1 ? $"{targets.Count} assets" : targets[0].Name;
            _status.Text = $"{what}: sent to Groundwork, waiting for its answer…";
            _status.Text = $"{what}: {await GroundworkSender.AnswerAsync(id, TimeSpan.FromSeconds(6))}";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Groundwork: {e.Message}";
        }
        finally
        {
            Idle();
            _toGroundwork.IsEnabled = true;
        }
    }

    // ---- Fab library sync (tools/fab_sync.py) ----

    private bool _syncing;

    private async Task SignInAsync()
    {
        if (!await FabSync.ReadyAsync(this, _settings, l => Dispatcher.UIThread.Post(() => _status.Text = $"Sync: {l}")))
        {
            return;
        }

        string url = "";
        await FabSync.RunAsync(_settings, "fab_sync.py", ["login-url"], l => url = l.StartsWith("https://", StringComparison.Ordinal) ? l : url);
        if (url.Length == 0)
        {
            _status.Text = "Could not start the Epic sign-in (see the debug log)";
            return;
        }

        await Launcher.LaunchUriAsync(new Uri(url));
        if (await Prompt.Ask(this, "Sign in to Epic", "Sign in on the page that just opened in your browser. It ends on a short text with \"authorizationCode\": paste that code (or the whole text) here:") is not { } code)
        {
            return;
        }

        string result = "";
        // the code goes on stdin: a command line can be read by other processes while it runs
        int exit = await FabSync.RunAsync(_settings, "fab_sync.py", ["login", "-"], l => result = l, stdin: code);
        _status.Text = exit == 0 ? $"Epic: {result}" : $"Epic sign-in failed: {result}";
    }

    private async Task ItchSignInAsync()
    {
        if (!await FabSync.ReadyAsync(this, _settings, l => Dispatcher.UIThread.Post(() => _status.Text = $"Sync: {l}")))
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri("https://itch.io/user/settings/api-keys"));
        if (await Prompt.Ask(this, "Sign in to itch.io", "On the itch.io page that just opened, generate an API key (or copy an existing one) and paste it here:") is not { } k)
        {
            return;
        }

        string result = "";
        int exit = await FabSync.RunAsync(_settings, "itch_sync.py", ["login", "-"], l => result = l, stdin: k);
        _status.Text = exit == 0 ? $"itch.io: {result}" : $"itch.io sign-in failed: {result}";
    }

    private async Task SyncAsync(bool itch = false)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        if (!await FabSync.ReadyAsync(this, _settings, l => Dispatcher.UIThread.Post(() => _status.Text = $"Sync: {l}")))
        {
            _syncing = false;
            return;
        }

        string work = Path.Combine(Path.GetDirectoryName(Sender.Outbox())!, itch ? "sync-itch" : "sync");
        string script = itch ? "itch_sync.py" : "fab_sync.py", source = itch ? "itch.io" : "Fab";
        Directory.CreateDirectory(work);
        string planFile = Path.Combine(work, "plan.json"), changed = Path.Combine(work, "changed.txt");
        void Status(string l) => Dispatcher.UIThread.Post(() => _status.Text = $"{source} sync: " + (l.Length > 9 && l[2] == ':' ? l[9..] : l));
        try
        {
            Busy(null);
            using (Log.Time("fab", "compare the library"))
            {
                string[] args = itch ? ["sync", "--packs", _settings.Packs, "--index", _settings.Index, "--plan", planFile] : ["sync", "--packs", _settings.Packs, "--plan", planFile];
                File.Delete(planFile);
                if (await FabSync.RunAsync(_settings, script, args, Status) != 0 || !File.Exists(planFile))
                {
                    _status.Text = $"{source} sync failed: sign in first (File > Sign in to {(itch ? "itch.io" : "Epic")}…), or see the debug log";
                    return;
                }
            }

            Idle();
            if (!await SyncPreview.Ask(this, planFile, source))
            {
                _status.Text = $"{source} sync: nothing downloaded";
                return;
            }

            Busy(null);
            File.Delete(changed);
            using (Log.Time("fab", "download"))
            {
                await FabSync.RunAsync(_settings, script, ["sync", "--packs", _settings.Packs, "--index", _settings.Index, "--download", "--changed", changed], Status);
            }

            string[] slugs = File.Exists(changed) ? File.ReadAllLines(changed).Where(l => l.Length > 0).ToArray() : [];
            if (slugs.Length > 0)
            {
                using (Log.Time("fab", $"index {slugs.Length} packs"))
                {
                    await FabSync.RunAsync(_settings, "index_library.py", ["--kind", itch ? "raw" : "unreal", "--redo", changed, "--order", changed, "--limit", $"{slugs.Length}",
                        "--packs", _settings.Packs, "--index", _settings.Index], Status, "6G");
                }

                await ReloadAsync();
            }

            _status.Text = $"{source} sync: {slugs.Length} packs downloaded and indexed";
        }
        finally
        {
            _syncing = false;
            Idle();
        }
    }

    private async Task<bool> PickUnityAsync()
    {
        IReadOnlyList<IStorageFolder> f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The Unity project's folder (holds Assets/)" });
        if (f.Count == 0 || f[0].TryGetLocalPath() is not { } path)
        {
            return false;
        }

        if (!Directory.Exists(Path.Combine(path, "Assets")))
        {
            _status.Text = $"{path} has no Assets folder: not a Unity project";
            return false;
        }

        _settings.UnityProject = path;
        _settings.Save();
        _status.Text = $"Unity project: {Path.GetFileName(path.TrimEnd('/'))} ({UnitySender.Pipeline(path).ToUpperInvariant()})";
        return true;
    }

    private async Task UnityFolderAsync()
    {
        if (await Prompt.Ask(this, "Unity folder", "Where in the Unity project assets go (inside Assets/):", _settings.UnityFolder) is { } folder)
        {
            folder = folder.Replace('\\', '/').Trim('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal) || folder.Split('/').Contains(".."))
            {
                _status.Text = "The Unity folder has to be inside Assets/";
                return;
            }

            _settings.UnityFolder = folder;
            _settings.Save();
            _status.Text = $"Unity folder: {folder}";
        }
    }

    private async Task SendToUnityAsync()
    {
        List<Item> targets = [.. Targets().Where(t => t.Raw || t.Kind is "Mesh" or "Character")];
        if (targets.Count == 0)
        {
            _status.Text = "Send to Unity takes meshes and characters";
            return;
        }

        if (_settings.UnityProject.Length == 0 && !await PickUnityAsync())
        {
            return;
        }

        if (!await FetchAsync(targets))
        {
            return;
        }

        string blenderDir = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_settings.Tools))!, "blender");
        _toUnity.IsEnabled = false;
        Busy(targets.Count > 1 ? 0 : null);
        var lines = new List<string>();
        try
        {
            foreach (Item item in targets)
            {
                _status.Text = $"{item.Name}: into Unity{(targets.Count > 1 ? $" ({lines.Count + 1} of {targets.Count})" : "")}…";
                if (item.Raw)
                {
                    var (file, tex) = RawFile(item);
                    lines.Add(await UnitySender.SendAsync(item, [file], tex, unreal: false, _settings, blenderDir));
                }
                else
                {
                    var (dir, fbx) = await Sender.ExportAsync(item, _settings, UnrealEngine);
                    lines.Add(await UnitySender.SendAsync(item, fbx, dir, unreal: true, _settings, blenderDir));
                }

                if (targets.Count > 1)
                {
                    Busy((double)lines.Count / targets.Count);
                }
            }

            _status.Text = targets.Count == 1 ? lines[0] : $"{lines.Count} assets sent to Unity ({Path.GetFileName(_settings.UnityProject.TrimEnd('/'))})";
            await RecordSentAsync(targets, _settings.UnityProject, "Unity");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            _status.Text = $"Unity: {e.Message}";
        }
        finally
        {
            Idle();
            _toUnity.IsEnabled = true;
        }
    }

    private void UpdateProjectButton()
    {
        var text = (TextBlock)_projectButton.Content!;
        text.FontSize = Look.Theme.Caption;
        text.Text = _settings.UnrealProject.Length == 0 ? "No Unreal project: pick one" : $"Unreal: {Path.GetFileName(_settings.UnrealProject.TrimEnd('/'))}";
        Chrome.Tip(_projectButton, (_settings.UnrealProject.Length == 0 ? "" : _settings.UnrealProject + "\n") + "Click to pick the Unreal project assets are sent to.");
    }

    private string? _teamNote;

    /// <summary>The details of a map or prop on the team server: who published it, its versions, Save and Delete.</summary>
    private void PublishedDetails(Item item, Team.Published p)
    {
        string tool = p.Kind == "maps" ? "Groundwork" : "Prop Generator";
        Button save = Chrome.IconTextButton("download", "Save the zip…", $"Download this version's zip into your Downloads folder. {tool} opens it from its own team list.");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.Margin = new Thickness(10, 0, 10, 10);
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                _status.Text = $"{p.Name}: downloading {Team.Size(p.Size)}…";
                string file = await Team.SaveAsync(p, downloads);
                _status.Text = $"{p.Name}: saved as {file}";
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
            {
                _status.Text = $"{p.Name}: {e.Message}";
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        _details.Children.Add(save);

        var about = new FoldGroup("About");
        about.Body.Children.Add(InfoRow("Published by", p.PublishedBy.Name));
        about.Body.Children.Add(InfoRow("On", DateTime.TryParse(p.PublishedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime at) ? at.ToLocalTime().ToString("d MMM yyyy, HH:mm") : p.PublishedAt));
        about.Body.Children.Add(InfoRow("Version", $"{p.Version}"));
        about.Body.Children.Add(InfoRow("Size", $"{Team.Size(p.Size)}"));
        about.Body.Children.Add(InfoRow("From", tool));
        _details.Children.Add(about);

        var versions = new FoldGroup("Versions");
        _details.Children.Add(versions);
        _ = FillVersionsAsync();

        async Task FillVersionsAsync()
        {
            List<Team.Published> all;
            try
            {
                all = await Team.VersionsAsync(p);
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
            {
                versions.Body.Children.Add(new TextBlock { Text = e.Message, FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, TextWrapping = TextWrapping.Wrap });
                return;
            }

            foreach (Team.Published v in Enumerable.Reverse(all))
            {
                Control value = new TextBlock { Text = $"{v.PublishedBy.Name}, {Team.Size(v.Size)}", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
                if (Team.CanDelete && all.Count > 1)
                {
                    Button del = Chrome.IconButton("trash-2", $"Delete version {v.Version} from the team server.");
                    del.Click += async (_, _) => await DeletePublishedAsync(v, v.Version);
                    value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { value, del } };
                }

                versions.Body.Children.Add(InfoRow($"v{v.Version}", value));
            }
        }

        if (Team.CanDelete)
        {
            Button delete = Chrome.TextButton("Delete from the team…", $"Remove {p.Name} and every version of it from the team server. Copies people already have are kept.");
            delete.HorizontalAlignment = HorizontalAlignment.Left;
            delete.Margin = new Thickness(10, 6, 10, 10);
            delete.Click += async (_, _) => await DeletePublishedAsync(p, null);
            _details.Children.Add(delete);
        }
    }

    private async Task DeletePublishedAsync(Team.Published p, int? version)
    {
        string what = version is { } v ? $"version {v} of {p.Name}" : $"{p.Name} and all its versions";
        if (!await Prompt.Confirm(this, "Delete from the team", $"Delete {what} from the team server? Nobody on the team can fetch it afterwards; copies people already have are kept.", "Delete"))
        {
            return;
        }

        try
        {
            await Team.DeleteAsync(p, version);
            Log.Write("team", $"deleted {p.Kind}/{p.Id}{(version is { } n ? $" version {n}" : "")}");
            _status.Text = $"Deleted {what}";
            await ReloadAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
        {
            _status.Text = $"Could not delete {what}: {e.Message}";
        }
    }

    /// <summary>With a team address (settings, else JATIFY_TEAM): asks the team server who we are and mirrors its index.
    /// Returns the mirror's (packs, index), or null to use the library in settings (offline, or not on the team).</summary>
    private async Task<(string, string)?> TeamLibraryAsync(IProgress<(int Done, int Total)> progress)
    {
        _teamNote = null;
        Team.Leave();
        if (Team.Address(_settings) is not { } address)
        {
            return null;
        }

        while (true)
        {
            _status.Text = $"Connecting to the team server {address}…";
            try
            {
                Team.Me me = await Team.WhoAmIAsync(address);
                if (me.Role is null)
                {
                    _teamNote = $"Tailscale says you are {me.Login}, not on the team at {address} yet: ask the owner to add you in Questlog (Members > Waiting to join). Working offline.";
                    return null;
                }

                Team.Join(address, me);
                Log.Write("team", $"{address}: {me.Login} ({me.Role}), server {me.Service} {me.Version}");
                if (Team.Outdated(me) is { } old)
                {
                    _teamNote = old;
                }

                _status.Text = $"Reading the team library as {me.Name}…";
                return await Team.SyncIndexAsync(progress);
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
            {
                Team.Leave();
                Log.Write("team", e.Message);
                if (!await Prompt.Confirm(this, "Team server", $"{e.Message} Retry, or cancel to carry on offline with the library on this computer.", "Retry"))
                {
                    _teamNote = "working offline";
                    return null;
                }
            }
        }
    }

    private async Task TeamServerAsync()
    {
        string now = Team.Address(_settings) ?? "";
        string from = _settings.Team is null && now.Length > 0 ? " (from the Launcher)" : "";
        if (await Prompt.Ask(this, "Team server", $"The team server's address (host or host:port; port {Team.Port} when none is given){from}. Tailscale tells it who you are. Type off to work offline.", now) is not { } address)
        {
            return;
        }

        _settings.Team = address.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : address;
        _settings.Save();
        await ReloadAsync();
    }

    /// <summary>For a team library: fetches what these assets need before a send. False (status says why) when it failed.</summary>
    private async Task<bool> FetchAsync(IEnumerable<Item> items)
    {
        if (Team.Base is null)
        {
            return true;
        }

        var all = items.SelectMany(i => Library.Needs(i).Prepend(i)).Distinct().ToList();
        try
        {
            await Team.FetchAsync(all, new Progress<string>(s => _status.Text = s));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            _status.Text = $"Could not fetch from the team library: {e.Message}";
            return false;
        }
    }

    private async Task ConnectAsync()
    {
        if (await ConnectDialog.Ask(this, _settings.Share, _settings.ShareUser) is not var (address, user, password))
        {
            return;
        }

        _status.Text = $"Connecting to {address}…";
        try
        {
            string root = await Task.Run(() => Shared.Connect(address, user, password));
            (_settings.Share, _settings.ShareUser) = (address, user);
            (_settings.Packs, _settings.Index) = (Path.Combine(root, "packs"), Path.Combine(root, "index"));
            _settings.Save();
            Log.Write("shared", $"connected to {address} as {user}: {root}");
            await ReloadAsync();
        }
        catch (InvalidOperationException e)
        {
            _status.Text = e.Message;
        }
    }

    private async Task<bool> PickProjectAsync()
    {
        IReadOnlyList<IStorageFolder> f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The Unreal project's folder (holds the .uproject)" });
        if (f.Count == 0 || f[0].TryGetLocalPath() is not { } path)
        {
            return false;
        }

        _settings.UnrealProject = path;
        _settings.Save();
        UpdateProjectButton();
        _ = ScanProjectAsync();
        return true;
    }
}
