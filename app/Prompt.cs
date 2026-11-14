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

    /// <summary>A yes/no question: true when <paramref name="ok"/> is pressed.</summary>
    public static async Task<bool> Confirm(Window owner, string title, string label, string ok) =>
        await new Prompt(title, label, "", ok).ShowDialog<string?>(owner) is not null;

    private Prompt(string title, string label, string initial, string? confirm = null)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        var box = new TextBox { Text = initial, FontSize = Look.Theme.Body };
        Button cancel = Chrome.TextButton("Cancel", "Close without saving.");
        Button ok = Chrome.PrimaryButton("check", confirm ?? "OK", confirm is null ? "Save." : $"{confirm}.");
        box.IsVisible = confirm is null;
        void Done() => Close(confirm ?? ((box.Text ?? "").Trim() is { Length: > 0 } t ? t : null));
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
                new TextBlock { Text = label, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) =>
        {
            if (confirm is not null)
            {
                ok.Focus();
                return;
            }

            box.Focus();
            box.SelectAll();
        };
    }
}
