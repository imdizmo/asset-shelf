using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>A small window asking for one line of text (a collection's name, say). Null when cancelled or empty.</summary>
public sealed class Prompt : Window
{
    public static Task<string?> Ask(Window owner, string title, string label, string initial = "") =>
        new Prompt(title, label, initial).ShowDialog<string?>(owner);

    private Prompt(string title, string label, string initial)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        var box = new TextBox { Text = initial, FontSize = Look.Theme.Body };
        Button cancel = Chrome.TextButton("Cancel", "Close without saving.");
        Button ok = Chrome.PrimaryButton("check", "OK", "Save.");
        void Done() => Close((box.Text ?? "").Trim() is { Length: > 0 } t ? t : null);
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) => Done();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Done();
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}
