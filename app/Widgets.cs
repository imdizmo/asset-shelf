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
    private readonly TextBlock _count = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center };

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
                    new TextBlock { Text = title, Margin = new Thickness(4, 0, 0, 0), FontSize = Look.Theme.Label, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        DockPanel.SetDock(_chevron, Dock.Left);
        ((DockPanel)head.Content).Children.Insert(1, _count);
        DockPanel.SetDock(_count, Dock.Right);
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
        CornerRadius = new CornerRadius(Look.Theme.Radius3);
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
