using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Shows what Send to Unreal is about to do (files to copy, packages already there, missing) and asks.</summary>
public sealed class SendPreview : Window
{
    private const int ListMax = 60;

    public static Task<bool> Ask(Window owner, string name, string project, Sender.UnrealPlan plan, List<(string Text, bool Blocking)> warnings) =>
        new SendPreview(name, project, plan, warnings).ShowDialog<bool>(owner);

    private SendPreview(string name, string project, Sender.UnrealPlan plan, List<(string Text, bool Blocking)> warnings)
    {
        Title = $"Send {name} to Unreal";
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        string proj = Path.GetFileName(project.TrimEnd('/'));
        string content = Path.Combine(project, "Content");
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = $"{name} into {proj}", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary });
        body.Children.Add(Line(plan.Copy.Count == 0 ? "Nothing to copy: everything is already in the project."
            : $"{plan.Copy.Count} files to copy ({plan.Bytes / 1048576.0:0.0} MB) into {proj}/Content, at the same paths.", Look.Theme.TextPrimary));
        foreach (var (text, blocking) in warnings)
        {
            body.Children.Add(Line((blocking ? "⚠ " : "") + text, blocking ? Look.Theme.Accent : Look.Theme.Warning));
        }

        if (plan.Already.Count > 0)
        {
            body.Children.Add(Line($"{plan.Already.Count} packages already in the project: left as they are, never overwritten.", Look.Theme.TextSecondary));
        }

        if (plan.Missing.Count > 0)
        {
            body.Children.Add(Line($"{plan.Missing.Count} packages it needs are not in the pack's files: {string.Join(", ", plan.Missing.Take(3))}{(plan.Missing.Count > 3 ? "…" : "")}", Look.Theme.Warning));
        }

        var list = new StackPanel { Spacing = 1 };
        foreach (var (_, dst, size) in plan.Copy.Take(ListMax))
        {
            list.Children.Add(new TextBlock { Text = $"{Path.GetRelativePath(content, dst)}   {size / 1024.0:0} KB", FontSize = 11.5, Foreground = Look.Theme.TextSecondary, FontFamily = new FontFamily("monospace") });
        }

        if (plan.Copy.Count > ListMax)
        {
            list.Children.Add(new TextBlock { Text = $"… and {plan.Copy.Count - ListMax} more", FontSize = 11.5, Foreground = Look.Theme.TextTertiary });
        }

        Button cancel = Chrome.TextButton("Cancel", "Send nothing.");
        cancel.Click += (_, _) => Close(false);
        Button send = Chrome.PrimaryButton("download", plan.Copy.Count == 0 ? "Close" : $"Copy {plan.Copy.Count} files", "Copy these files into the project.");
        send.Click += (_, _) => Close(plan.Copy.Count > 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 16), Children = { cancel, send } };
        var dock = new DockPanel();
        DockPanel.SetDock(body, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(body);
        dock.Children.Add(buttons);
        dock.Children.Add(new Border { Margin = new Thickness(16, 0), BorderBrush = Look.Theme.BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Look.Theme.Radius2), Child = new ScrollViewer { Content = list, Padding = new Thickness(8) } });
        Content = dock;
    }

    private static TextBlock Line(string text, IBrush brush) => new() { Text = text, FontSize = Look.Theme.Label, Foreground = brush, TextWrapping = TextWrapping.Wrap };
}
