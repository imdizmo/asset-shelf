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
        // the shared accent ramp (Fluent's pressed and hover states step along it)
        foreach (var (key, hex) in new[] { ("SystemAccentColor", "#EC8219"), ("SystemAccentColorLight1", "#F0953E"), ("SystemAccentColorLight2", "#F4A85C"), ("SystemAccentColorLight3", "#F8C08A"), ("SystemAccentColorDark1", "#D27214"), ("SystemAccentColorDark2", "#B05F10"), ("SystemAccentColorDark3", "#7A4410") })
        {
            app.Resources[key] = Color.Parse(hex);
        }

        app.Resources["ControlCornerRadius"] = new CornerRadius(Tokens.RSm);
        app.Resources["OverlayCornerRadius"] = new CornerRadius(Tokens.RMd);
        app.Resources["TextControlBackground"] = Tokens.Bg;
        app.Resources["TextControlBackgroundPointerOver"] = Tokens.Bg;
        app.Resources["TextControlBackgroundFocused"] = Tokens.Bg;
        app.Resources["TextControlBorderBrush"] = Tokens.Line;
        app.Resources["TextControlBorderBrushPointerOver"] = Tokens.LineStrong;
        app.Resources["TextControlBorderBrushFocused"] = Tokens.Accent;
        app.Resources["TextControlPlaceholderForeground"] = Tokens.Mute;
        app.Resources["TextControlPlaceholderForegroundPointerOver"] = Tokens.Mute;
        app.Resources["TextControlPlaceholderForegroundFocused"] = Tokens.Mute;
        app.Resources["ComboBoxBackground"] = Tokens.Raised;
        app.Resources["ComboBoxDropDownBackground"] = Tokens.Raised;
        app.Resources["ComboBoxDropDownBorderBrush"] = Tokens.LineStrong;
        app.Resources["ComboBoxItemBackgroundSelected"] = Tokens.Selected;
        app.Resources["ListBoxItemBackgroundSelected"] = Tokens.Selected;
        app.Resources["ListBoxItemBackgroundPointerOver"] = Tokens.Hover;
        app.Resources["SystemControlHighlightListAccentLowBrush"] = Tokens.Selected;
        app.Resources["SystemControlHighlightListLowBrush"] = Tokens.Hover;
        app.Resources["CheckBoxCheckBackgroundFillChecked"] = Tokens.Accent;
        app.Resources["CheckBoxCheckGlyphForegroundChecked"] = Tokens.AccentInk;
        app.Resources["MenuFlyoutPresenterBackground"] = Tokens.Raised;
        app.Resources["MenuFlyoutPresenterBorderBrush"] = Tokens.LineStrong;
        app.Resources["MenuFlyoutItemBackgroundPointerOver"] = Tokens.Hover;
        app.Resources["SliderTrackFill"] = Tokens.LineStrong;
        app.Resources["SliderTrackValueFill"] = Tokens.Accent;
        app.Resources["SliderThumbBackground"] = Tokens.Text;
        app.Resources["ScrollBarSize"] = 10d;

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

        // design v2 tag chips: pills; on = the selected wash with an accent-line border
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("chip"), Brushes.Transparent, Tokens.Dim, Tokens.Line, 1, Tokens.Hover, Tokens.Text, Tokens.LineStrong, Tokens.RPill));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("chip-on"), Tokens.Selected, Tokens.Text, Tokens.AccentLine, 1, Tokens.Selected, Tokens.Text, Tokens.Accent, Tokens.RPill));

        // design v2: underline tabs and their More button, the segmented control, the split send button
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("utab"), Brushes.Transparent, Tokens.Dim, Brushes.Transparent, 0, Brushes.Transparent, Tokens.Text, radius: 0));
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("utab").Class(":checked"), Brushes.Transparent, Tokens.Text, Brushes.Transparent, 0, Brushes.Transparent, Tokens.Text, radius: 0));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("utab-more"), Brushes.Transparent, Tokens.Dim, Brushes.Transparent, 0, Tokens.Hover, Tokens.Text, radius: Tokens.RXs));
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("seg"), Brushes.Transparent, Tokens.Dim, Brushes.Transparent, 1, Tokens.Hover, Tokens.Text, radius: Tokens.RPill));
        app.Styles.Add(ButtonLook(r => r.OfType<ToggleButton>().Class("seg").Class(":checked"), Tokens.AccentSoft, Tokens.Accent, Tokens.AccentLine, 1, Tokens.AccentSoft, Tokens.Accent, radius: Tokens.RPill));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-primary").Class("split-main"), Theme.Accent, Theme.AccentInk, Theme.Accent, 1, Theme.AccentHover, Theme.AccentInk, radius: 0));
        app.Styles.Add(ButtonLook(r => r.OfType<Button>().Class("ep").Class("ep-primary").Class("split-menu"), Theme.Accent, Theme.AccentInk, Theme.Accent, 1, Theme.AccentHover, Theme.AccentInk, radius: 0));

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
