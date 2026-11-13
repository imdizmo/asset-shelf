// Adapted from easy-props (MIT, © 2026 AHS12).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AssetShelf.Look;

/// <summary>The application-wide styles of the look: tooltips, the scrollbar thumb, the <see cref="Chrome"/> buttons and the drawer's toggles.</summary>
public static class LookStyles
{
    /// <summary>Adds the look's styles and resource overrides to the application.</summary>
    /// <param name="app">The application.</param>
    public static void Apply(Application app)
    {
        app.Resources["ContentControlThemeFontFamily"] = Theme.Font;
        foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
        {
            app.Resources[key] = ((Avalonia.Media.Immutable.ImmutableSolidColorBrush)Theme.Accent).Color;
        }

        app.Resources["ScrollBarThumbFill"] = Theme.ScrollbarThumb;
        app.Resources["ScrollBarThumbFillPointerOver"] = Theme.ScrollbarThumbHover;
        app.Resources["ScrollBarThumbFillPressed"] = Theme.ScrollbarThumbHover;
        app.Resources["ToolTipBackground"] = Theme.SurfaceRaised;
        app.Resources["ToolTipBorderBrush"] = Theme.BorderStrong;
        app.Resources["ToolTipForeground"] = Theme.TextPrimary;

        app.Styles.Add(new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Theme.SurfaceRaised),
                new Setter(TemplatedControl.BorderBrushProperty, Theme.BorderStrong),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(Theme.Radius2)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 5)),
                new Setter(TemplatedControl.ForegroundProperty, Theme.TextPrimary),
                new Setter(TemplatedControl.FontSizeProperty, Theme.Label),
                new Setter(Layoutable.MaxWidthProperty, Theme.TooltipMaxWidth),
            },
        });

        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep"), Brushes.Transparent, Theme.TextSecondary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-raised"), Theme.SurfaceRaised, Theme.TextPrimary, Theme.BorderStrong, 1, Theme.SurfaceActive, Theme.TextPrimary));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-primary"), Theme.Accent, Theme.AccentInk, Theme.Accent, 1, Theme.AccentHover, Theme.AccentInk));

        // The top bar: an open menu's title, the menu rows, the prop name and the palette button.
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-open"), Theme.SurfaceActive, Theme.TextPrimary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-menu-item"), Brushes.Transparent, Theme.TextPrimary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-name"), Brushes.Transparent, Theme.TextPrimary, Brushes.Transparent, 1, Brushes.Transparent, Theme.TextPrimary, Theme.BorderStrong));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-palette"), Theme.SurfacePanel, Theme.TextTertiary, Theme.BorderSubtle, 1, Theme.SurfacePanel, Theme.TextSecondary, Theme.BorderStrong, Theme.Radius3));

        // The controls panel: a group's header, the filter's clear button, Surprise me, and Restore to preset.
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-group"), Brushes.Transparent, Theme.TextPrimary, Brushes.Transparent, 0, Theme.SurfaceHover, Theme.TextPrimary, radius: 0));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-clear"), Brushes.Transparent, Theme.TextTertiary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary, radius: 9));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-outline"), Brushes.Transparent, Theme.TextSecondary, Theme.BorderSubtle, 1, Brushes.Transparent, Theme.TextPrimary, Theme.BorderStrong));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-text"), Brushes.Transparent, Theme.TextSecondary, Brushes.Transparent, 0, Theme.SurfaceHover, Theme.TextPrimary));

        // The tools over the 3D view (easy-props' .tool), and the times of day in the sun's popover.
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-tool"), Brushes.Transparent, Theme.OverlayMuted, Brushes.Transparent, 0, Theme.ToolHover, Theme.OverlayText));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-tool").Class("ep-pressed"), Theme.ToolPressed, Brushes.White, Brushes.Transparent, 0, Theme.ToolPressed, Brushes.White));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-tool").Class("ep-unavailable"), Brushes.Transparent, Theme.ToolUnavailable, Brushes.Transparent, 0, Theme.ToolHover, Theme.ToolUnavailable));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-time"), Brushes.Transparent, Theme.TextSecondary, Theme.BorderSubtle, 1, Brushes.Transparent, Theme.TextPrimary, Theme.BorderStrong));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-time").Class("ep-pressed"), Theme.SurfaceActive, Theme.TextPrimary, Theme.BorderSubtle, 1, Theme.SurfaceActive, Theme.TextPrimary, Theme.BorderStrong));

        // The drawer's tabs and its Objects toggle: quiet as a menu's title, the chosen tab (and the open drawer's
        // toggle) on the active wash in the primary text colour, as an open menu's title. No accent colour.
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("ep-toggle"), Brushes.Transparent, Theme.TextSecondary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary));
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("ep-toggle").Class(":checked"), Theme.SurfaceActive, Theme.TextPrimary, Brushes.Transparent, 0, Theme.SurfaceActive, Theme.TextPrimary));
    }

    // One button look: resting colours, and the colours while the pointer is over it (the border too, when given).
    private static Styles ButtonLook(
        Func<Selector?, Selector> button, IBrush back, IBrush fore, IBrush border, double borderWidth, IBrush hoverBack, IBrush hoverFore,
        IBrush? hoverBorder = null, double radius = Theme.Radius2)
    {
        var rest = new Style(x => button(x).Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, back),
                new Setter(ContentPresenter.ForegroundProperty, fore),
                new Setter(ContentPresenter.BorderBrushProperty, border),
                new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(borderWidth)),
                new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(radius)),
            },
        };
        var hover = new Style(x => button(x).Class(":pointerover").Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, hoverBack),
                new Setter(ContentPresenter.ForegroundProperty, hoverFore),
                new Setter(ContentPresenter.BorderBrushProperty, hoverBorder ?? border),
            },
        };
        return [rest, hover];
    }
}
