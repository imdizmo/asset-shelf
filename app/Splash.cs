using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AssetShelf;

/// <summary>The window shown while the library loads, with what is happening and how far along it is.</summary>
public sealed class Splash : Window
{
    private readonly TextBlock _what = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextSecondary };
    private readonly TextBlock _percent = new() { FontSize = Look.Theme.Caption, Foreground = Look.Theme.TextTertiary, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, IsIndeterminate = true };

    public Splash()
    {
        Title = "Asset Shelf";
        Width = 520;
        Height = 260;
        CanResize = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Look.Theme.SurfacePanel;
        string icon = Path.Combine(AppContext.BaseDirectory, "icon.png");
        var logo = new Image { Width = 64, Height = 64 };
        if (File.Exists(icon))
        {
            Icon = new WindowIcon(icon);
            logo.Source = new Bitmap(icon);
        }

        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        status.Children.Add(_what);
        Grid.SetColumn(_percent, 1);
        status.Children.Add(_percent);
        Content = new Border
        {
            BorderBrush = Look.Theme.BorderStrong,
            BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Margin = new Thickness(24, 0, 24, 20), Spacing = 8, Children = { status, _bar } },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 16,
                        Margin = new Thickness(28, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            logo,
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = "Asset Shelf", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary },
                                    new TextBlock { Text = "Your asset library, ready for Unreal and Blender", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextTertiary },
                                },
                            },
                        },
                    },
                },
            },
        };
        Show("Starting", null);
    }

    /// <summary>Shows <paramref name="what"/> and a fraction from 0 to 1 (null: length unknown).</summary>
    public void Show(string what, double? fraction)
    {
        _what.Text = what;
        _bar.IsIndeterminate = fraction is null;
        _bar.Value = fraction ?? 0;
        _percent.Text = fraction is { } f ? $"{f:P0}" : "";
    }
}
