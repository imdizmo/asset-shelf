using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>A folding group of a side panel (Prop Generator's settings group): a chevron header and a body.</summary>
public sealed class FoldGroup : Border
{
    private readonly Icon _chevron;
    private readonly StackPanel _titleRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = Look.Tokens.FsLabel, Foreground = Look.Tokens.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

    public FoldGroup(string title, bool open = true)
    {
        BorderBrush = Look.Theme.BorderSubtle;
        BorderThickness = new Thickness(0, 0, 0, 1);
        _chevron = new Icon("chevron-right", 13)
        {
            Foreground = Look.Theme.TextTertiary,
            VerticalAlignment = VerticalAlignment.Center,
            Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(90) }],
        };
        var head = new Button
        {
            Height = Look.Theme.ControlHeight,
            MinHeight = 0,
            Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new DockPanel
            {
                Children =
                {
                    _chevron,
                    _titleRow,
                },
            },
        };
        DockPanel.SetDock(_chevron, Dock.Left);
        // design v2 section header: the count right after the title, muted
        _titleRow.Children.Add(new TextBlock { Text = title, Margin = new Thickness(4, 0, 0, 0), FontSize = Look.Theme.Label, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        _titleRow.Children.Add(_count);
        head.Classes.Add("ep");
        head.Classes.Add("ep-group");
        Chrome.Tip(head, $"Show or hide {title.ToLowerInvariant()}.");
        head.Click += (_, _) => IsOpen = !IsOpen;
        // the group's own action ("Unselect all"), beside the header rather than in it, so a click does not fold it
        _action.Classes.Add("ep");
        _action.Classes.Add("ep-text");
        _action.Click += (_, _) => _act?.Invoke();
        Child = new StackPanel { Children = { new Grid { Children = { head, _action } }, Body } };
        IsOpen = open;
    }

    public StackPanel Body { get; } = new() { Margin = new Thickness(6, 0, 6, 8), Spacing = 1 };

    public string Count { set => _count.Text = value; }

    private readonly Button _action = new()
    {
        IsVisible = false,
        MinHeight = 0,
        Height = 20,
        Padding = new Thickness(6, 0),
        Margin = new Thickness(0, 0, 40, 0),
        FontSize = Look.Theme.Caption,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Action? _act;

    /// <summary>Shows a small action in the header (hidden when <paramref name="text"/> is null).</summary>
    public void SetAction(string? text, string tip = "", Action? act = null)
    {
        _action.IsVisible = text is not null;
        _action.Content = text;
        _act = act;
        Chrome.Tip(_action, tip);
    }

    public bool IsOpen
    {
        get => Body.IsVisible;
        set
        {
            Body.IsVisible = value;
            _chevron.RenderTransform = TransformOperations.Parse(value ? "rotate(90deg)" : "rotate(0deg)");
        }
    }
}

/// <summary>A row that is on or off: a check box, a label, a count on the right. The whole row toggles it.</summary>
public sealed class CheckRow : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly Border _box;
    private readonly Icon _tick = new("check", 11) { Foreground = Look.Theme.AccentInk, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public CheckRow(string label, string count, bool on, string tip, Action<bool> changed)
    {
        Height = 24;
        MinHeight = 0;
        Padding = new Thickness(4, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Classes.Add("ep");
        Classes.Add("ep-text");
        _box = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(Look.Theme.Radius1), BorderThickness = new Thickness(1), Child = _tick, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = label, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        var num = new TextBlock { Text = count, FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
        var dock = new DockPanel();
        DockPanel.SetDock(_box, Dock.Left);
        DockPanel.SetDock(num, Dock.Right);
        dock.Children.Add(_box);
        dock.Children.Add(num);
        dock.Children.Add(text);
        Content = dock;
        Chrome.Tip(this, tip);
        On = on;
        Click += (_, _) =>
        {
            On = !On;
            changed(On);
        };
    }

    public bool On
    {
        get;
        set
        {
            field = value;
            _box.Background = value ? Look.Theme.Accent : Look.Theme.SurfaceSunken;
            _box.BorderBrush = value ? Look.Theme.Accent : Look.Theme.BorderStrong;
            _tick.IsVisible = value;
        }
    }
}

/// <summary>A tab of the strip over the grid (Prop Generator's drawer tabs): quiet text, the chosen one on the active wash.</summary>
public sealed class Tab : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    public Tab(string text, string tip)
    {
        Content = new TextBlock { Text = text, FontSize = Look.Theme.Label, VerticalAlignment = VerticalAlignment.Center };
        Height = Look.Theme.ControlHeight;
        MinHeight = 0;
        Padding = new Thickness(9, 0);
        VerticalContentAlignment = VerticalAlignment.Center;
        Classes.Add("ep-toggle");
        Chrome.Tip(this, tip);
    }

    public string Text { set => ((TextBlock)Content!).Text = value; }
}

/// <summary>A sunken text field with an icon and a clear button (Prop Generator's filter box).</summary>
public sealed class SearchField : Border
{
    public SearchField(string placeholder, string tip, string? keys = null)
    {
        Height = Look.Theme.ControlHeight;
        Padding = new Thickness(8, 0);
        CornerRadius = new CornerRadius(Look.Tokens.RSm);
        Background = Look.Theme.SurfaceSunken;
        BorderBrush = Look.Theme.BorderSubtle;
        BorderThickness = new Thickness(1);
        Box = new TextBox
        {
            PlaceholderText = placeholder,
            FontSize = Look.Theme.Label,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        })
        {
            Box.Resources[key] = Brushes.Transparent;
        }

        foreach (string key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
        {
            Box.Resources[key] = Look.Theme.TextTertiary;
        }

        Box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        Box.Resources["TextControlThemeMinHeight"] = 0d;
        Chrome.Tip(Box, tip);
        var clear = new Button
        {
            Width = 18,
            Height = 18,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Icon("x", 12),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        clear.Classes.Add("ep");
        clear.Classes.Add("ep-clear");
        Chrome.Tip(clear, "Clear");
        clear.Click += (_, _) =>
        {
            Box.Text = "";
            Box.Focus();
        };
        Box.TextChanged += (_, _) => clear.IsVisible = !string.IsNullOrEmpty(Box.Text);
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(Box.Text))
            {
                Box.Text = "";
                e.Handled = true;
            }
        };
        var dock = new DockPanel();
        var icon = new Icon("search", 13) { Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        if (keys is not null)
        {
            Border chip = Chrome.KeyChip(keys);
            chip.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(chip, Dock.Right);
            dock.Children.Add(chip);
        }

        DockPanel.SetDock(clear, Dock.Right);
        dock.Children.Add(clear);
        dock.Children.Add(Box);
        Child = dock;
    }

    public TextBox Box { get; }
}

/// <summary>An underline tab (design v2): label and a muted count; the chosen one is semibold with a 2 px accent line.</summary>
public sealed class UnderlineTab : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    private readonly TextBlock _label = new() { FontSize = Look.Tokens.FsBody, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = Look.Tokens.FsLabel, Foreground = Look.Tokens.Mute, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _line = new() { Height = 2, CornerRadius = new CornerRadius(2), Background = Look.Tokens.Accent, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -1), IsVisible = false };

    public UnderlineTab(string text, string? count, string tip)
    {
        Height = Look.Tokens.Tab;
        MinHeight = 0;
        Padding = new Thickness(10, 0);
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Classes.Add("utab");
        _label.Text = text;
        _count.Text = count ?? "";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _label, _count } };
        Content = new Grid { Children = { row, _line } };
        Chrome.Tip(this, tip);
        IsCheckedChanged += (_, _) => Show();
        Show();
    }

    public string Name2 => _label.Text ?? "";

    /// <summary>Chooses the tab as a click would (from the More menu).</summary>
    public void Pick() => OnClick();

    public string Count => _count.Text ?? "";

    private void Show()
    {
        bool on = IsChecked == true;
        _line.IsVisible = on;
        _label.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        _label.Foreground = on ? Look.Tokens.Text : Look.Tokens.Dim;
    }
}

/// <summary>A row of tabs that never wraps or scrolls: what does not fit goes into a "More (n)" menu at the end.
/// The first tab ("All") and the chosen one always stay.</summary>
public sealed class TabStrip : Panel
{
    private readonly Button _more;
    private readonly List<UnderlineTab> _hidden = [];

    public TabStrip()
    {
        _more = new Button { Height = Look.Tokens.Tab, MinHeight = 0, Padding = new Thickness(10, 0), Classes = { "utab-more" } };
        _more.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (UnderlineTab t in _hidden)
            {
                var item = new MenuItem { Header = $"{t.Name2}  {t.Count}", FontSize = Look.Tokens.FsLabel };
                item.Click += (_, _) => t.Pick();
                menu.Items.Add(item);
            }

            menu.ShowAt(_more);
        };
        Children.Add(_more);
    }

    public IEnumerable<UnderlineTab> Tabs => Children.OfType<UnderlineTab>();

    public void SetTabs(IEnumerable<UnderlineTab> tabs)
    {
        Children.Clear();
        foreach (UnderlineTab t in tabs) Children.Add(t);
        Children.Add(_more);
    }

    protected override Size MeasureOverride(Size available)
    {
        double h = 0;
        foreach (Control c in Children)
        {
            c.Measure(Size.Infinity);
            h = Math.Max(h, c.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(available.Width) ? Children.Sum(c => c.DesiredSize.Width) : available.Width, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var tabs = Tabs.ToList();
        double gap = 2, all = tabs.Sum(t => t.DesiredSize.Width + gap);
        var show = new HashSet<UnderlineTab>(tabs);
        if (all > final.Width && tabs.Count > 0)
        {
            SetMoreText(tabs.Count);
            double room = final.Width - _more.DesiredSize.Width - gap;
            var must = tabs.Where((t, i) => i == 0 || t.IsChecked == true).ToHashSet();
            room -= must.Sum(t => t.DesiredSize.Width + gap);
            show = [.. must];
            foreach (UnderlineTab t in tabs.Where(t => !must.Contains(t)))
            {
                if (t.DesiredSize.Width + gap > room) break;
                show.Add(t);
                room -= t.DesiredSize.Width + gap;
            }
        }

        _hidden.Clear();
        _hidden.AddRange(tabs.Where(t => !show.Contains(t)));
        SetMoreText(_hidden.Count);
        double x = 0;
        foreach (UnderlineTab t in tabs)
        {
            if (show.Contains(t))
            {
                t.Arrange(new Rect(x, 0, t.DesiredSize.Width, final.Height));
                x += t.DesiredSize.Width + gap;
            }
            else
            {
                t.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        _more.Arrange(_hidden.Count > 0 ? new Rect(x, 0, _more.DesiredSize.Width, final.Height) : new Rect(0, 0, 0, 0));
        return final;
    }

    private int _moreShown = -1;

    private void SetMoreText(int n)
    {
        if (n == _moreShown) return;
        _moreShown = n;
        _more.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "More", FontSize = Look.Tokens.FsBody, Foreground = Look.Tokens.Dim, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = n.ToString(), FontSize = Look.Tokens.FsLabel, Foreground = Look.Tokens.Mute, VerticalAlignment = VerticalAlignment.Center },
                new Look.Icon("chevron-down", 14),
            },
        };
    }
}

/// <summary>A segmented control (design v2): a pill group on the page background; the chosen one accent-soft with an
/// accent-line border.</summary>
public sealed class Segmented : Border
{
    public Segmented(params ToggleButton[] buttons)
    {
        Padding = new Thickness(2);
        CornerRadius = new CornerRadius(Look.Tokens.RPill);
        Background = Look.Tokens.Bg;
        BorderBrush = Look.Tokens.Line;
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (ToggleButton b in buttons)
        {
            b.Classes.Remove("ep-toggle");
            b.Classes.Add("seg");
            b.Height = 22;
            b.Padding = new Thickness(10, 0);
            if (b.Content is TextBlock tb)
            {
                b.IsCheckedChanged += (_, _) => tb.FontWeight = b.IsChecked == true ? FontWeight.SemiBold : FontWeight.Normal;
                tb.FontWeight = b.IsChecked == true ? FontWeight.SemiBold : FontWeight.Normal;
            }

            row.Children.Add(b);
        }

        Child = row;
    }
}
