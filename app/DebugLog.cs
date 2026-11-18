using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>The debug window: the log live, newest at the bottom, with a filter, and the slow steps in orange.</summary>
public sealed class LogWindow : Window
{
    private static LogWindow? _open;
    private readonly List<Log.Entry> _all = [];
    private readonly List<Log.Entry> _pending = [];
    private readonly ListBox _list = new();
    private readonly SearchField _filter = new("Filter (area or text)", "Show only lines containing this.");
    private readonly TextBlock _count = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _follow = new() { Content = "Follow", IsChecked = true, FontSize = Look.Theme.Label };
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>Opens the window, or brings it forward if it is open.</summary>
    public static void Open(Window? owner)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new LogWindow();
        _open.Closed += (_, _) => _open = null;
        if (owner is not null)
        {
            _open.Show(owner);
        }
        else
        {
            _open.Show();
        }
    }

    private LogWindow()
    {
        Title = "Asset Shelf: debug log";
        Width = 980;
        Height = 560;
        Background = Look.Theme.SurfacePage;
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _list.ItemTemplate = new FuncDataTemplate<Log.Entry>((e, _) => Line(e), supportsRecycling: true);
        _list.Background = Brushes.Transparent;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);  // lines wrap
        _list.FontFamily = new FontFamily("monospace");
        _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(10, 1)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0d) },
        });

        Button clear = Chrome.TextButton("Clear", "Empty the log.");
        clear.Click += (_, _) =>
        {
            Log.Clear();
            _all.Clear();
            Refill();
        };
        Button copy = Chrome.TextButton("Copy", "Copy the lines shown to the clipboard.");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } cb)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join('\n', Shown().Select(Format)));
            }
        };
        _filter.Width = 280;
        _filter.Box.TextChanged += (_, _) => Refill();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 8), Children = { _filter, _follow, clear, copy, _count } };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(new Border { BorderBrush = Look.Theme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0), Child = _list });
        Content = dock;

        _all.AddRange(Log.Snapshot());
        Log.Added += OnAdded;
        Closed += (_, _) =>
        {
            Log.Added -= OnAdded;
            _flush.Stop();
        };
        // lines arrive from any thread, often in bursts: gather them and redraw a few times a second
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        Refill();
    }

    private void OnAdded(Log.Entry e)
    {
        lock (_pending)
        {
            _pending.Add(e);
        }
    }

    private void Flush()
    {
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            _all.AddRange(_pending);
            _pending.Clear();
        }

        if (_all.Count > Log.Max)
        {
            _all.RemoveRange(0, _all.Count - Log.Max);
        }

        Refill();
    }

    private IEnumerable<Log.Entry> Shown()
    {
        string f = (_filter.Box.Text ?? "").Trim();
        return f.Length == 0 ? _all : _all.Where(e => e.Area.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Text.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private void Refill()
    {
        List<Log.Entry> shown = [.. Shown()];
        _list.ItemsSource = shown;
        _count.Text = $"{shown.Count:N0} lines";
        if (_follow.IsChecked == true && shown.Count > 0)
        {
            _list.ScrollIntoView(shown[^1]);
        }
    }

    private static string Format(Log.Entry e) =>
        $"{e.At:HH:mm:ss.fff}  {(e.Ms is { } ms ? Duration(ms) : ""),9}  {e.Area,-8}  {e.Text}";

    private static string Duration(double ms) => ms >= 1000 ? $"{ms / 1000:0.00} s" : $"{ms:0} ms";

    private static Control Line(Log.Entry e)
    {
        IBrush time = e.Ms switch
        {
            >= 1000 => Look.Theme.Accent,
            >= 100 => Look.Theme.Warning,
            _ => Look.Theme.TextTertiary,
        };
        // the text column takes what is left of the window's width and wraps there
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,80,80,*") };
        void Add(Control c, int col)
        {
            Grid.SetColumn(c, col);
            g.Children.Add(c);
        }

        Add(new TextBlock { Text = e.At.ToString("HH:mm:ss.fff"), Foreground = Look.Theme.TextTertiary, FontSize = 12 }, 0);
        Add(new TextBlock { Text = e.Ms is { } ms ? Duration(ms) : "", Margin = new Thickness(0, 0, 10, 0), TextAlignment = TextAlignment.Right, Foreground = time, FontSize = 12 }, 1);
        Add(new TextBlock { Text = e.Area, Foreground = Look.Theme.AccentText, FontSize = 12 }, 2);
        Add(new TextBlock { Text = e.Text, Foreground = Look.Theme.TextPrimary, FontSize = 12, TextWrapping = TextWrapping.Wrap }, 3);
        return g;
    }
}
