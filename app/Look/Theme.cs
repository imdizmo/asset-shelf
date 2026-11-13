// Adapted from easy-props (MIT, © 2026 AHS12).
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AssetShelf.Look;

/// <summary>The design tokens, in jatify.com's look (near-black, orange accent, Space Grotesk; source of truth:
/// jatify-landing's global.css): colours as brushes, plus spacing, radii, heights and type sizes. The structure is
/// easy-props'.</summary>
public static class Theme
{
    private static ImmutableSolidColorBrush Hex(string rgb) => new(Color.Parse(rgb));

    private static ImmutableSolidColorBrush Rgba(byte r, byte g, byte b, double alpha) => new(Color.FromArgb((byte)Math.Round(alpha * 255), r, g, b));

    /// <summary>The window behind everything (surface-page).</summary>
    public static readonly IBrush SurfacePage = Hex("#0F0F10");

    /// <summary>Panels and cards (surface-panel).</summary>
    public static readonly IBrush SurfacePanel = Hex("#151518");

    /// <summary>Sunken fields and key chips (surface-sunken).</summary>
    public static readonly IBrush SurfaceSunken = Hex("#0F0F10");

    /// <summary>Raised controls, menus and tooltips (surface-raised).</summary>
    public static readonly IBrush SurfaceRaised = Hex("#1A1A1E");

    /// <summary>A control under the pointer (surface-hover).</summary>
    public static readonly IBrush SurfaceHover = Rgba(255, 255, 255, .08);

    /// <summary>A pressed or open control (surface-active).</summary>
    public static readonly IBrush SurfaceActive = Rgba(255, 255, 255, .11);

    /// <summary>A selected row (accent wash).</summary>
    public static readonly IBrush SurfaceSelected = Rgba(236, 130, 25, .17);

    /// <summary>Hairlines between areas (border-subtle).</summary>
    public static readonly IBrush BorderSubtle = Hex("#2A2A30");

    /// <summary>Outlines of controls and popovers (border-strong).</summary>
    public static readonly IBrush BorderStrong = Hex("#3A3A42");

    /// <summary>Main text (text-primary).</summary>
    public static readonly IBrush TextPrimary = Hex("#FFFFFF");

    /// <summary>Quieter text (text-secondary).</summary>
    public static readonly IBrush TextSecondary = Hex("#A7A7AD");

    /// <summary>The quietest text (text-tertiary).</summary>
    public static readonly IBrush TextTertiary = Hex("#86868D");

    /// <summary>Jatify orange (--accent).</summary>
    public static readonly IBrush Accent = Hex("#EC8219");

    /// <summary>Text and icons on the accent (dark ink).</summary>
    public static readonly IBrush AccentInk = Hex("#1A0E03");

    /// <summary>The accent under the pointer.</summary>
    public static readonly IBrush AccentHover = Hex("#F0953E");

    /// <summary>Accent-coloured text (links).</summary>
    public static readonly IBrush AccentText = Hex("#F0953E");

    /// <summary>A soft accent wash.</summary>
    public static readonly IBrush AccentSoft = Rgba(236, 130, 25, .14);

    /// <summary>The outline of a selection (the accent).</summary>
    public static readonly IBrush Selection = Hex("#EC8219");

    /// <summary>Destructive actions and errors (danger).</summary>
    public static readonly IBrush Danger = Hex("#c9372c");

    /// <summary>A destructive action under the pointer (danger-hover).</summary>
    public static readonly IBrush DangerHover = Hex("#d9483d");

    /// <summary>A soft red wash (danger-soft).</summary>
    public static readonly IBrush DangerSoft = Rgba(242, 120, 110, .14);

    /// <summary>Warnings (warning).</summary>
    public static readonly IBrush Warning = Hex("#e8b96a");

    /// <summary>Good news (success).</summary>
    public static readonly IBrush Success = Hex("#5BC07A");

    /// <summary>The outer ring of the keyboard focus ring (the panel colour, 2 px).</summary>
    public static readonly IBrush FocusRingInner = SurfacePanel;

    /// <summary>The inner ring of the keyboard focus ring (2 px).</summary>
    public static readonly IBrush FocusRing = Hex("#EC8219");

    /// <summary>The scrollbar thumb (scrollbar-thumb).</summary>
    public static readonly IBrush ScrollbarThumb = Hex("#2A2A30");

    /// <summary>The scrollbar thumb under the pointer.</summary>
    public static readonly IBrush ScrollbarThumbHover = Hex("#3A3A42");

    /// <summary>The background of chrome drawn over the 3D view (overlay-bg).</summary>
    public static readonly IBrush OverlayBg = Rgba(24, 25, 27, .8);

    /// <summary>The border of chrome drawn over the 3D view.</summary>
    public static readonly IBrush OverlayBorder = Rgba(255, 255, 255, .11);

    /// <summary>The text of chrome drawn over the 3D view.</summary>
    public static readonly IBrush OverlayText = Hex("#e9e9eb");

    /// <summary>The quieter text of chrome drawn over the 3D view.</summary>
    public static readonly IBrush OverlayMuted = Rgba(233, 233, 235, .68);

    /// <summary>A tool over the 3D view under the pointer.</summary>
    public static readonly IBrush ToolHover = Rgba(255, 255, 255, .10);

    /// <summary>A tool over the 3D view that is on (pressed).</summary>
    public static readonly IBrush ToolPressed = Rgba(255, 255, 255, .17);

    /// <summary>The icon of a tool over the 3D view that cannot be used yet.</summary>
    public static readonly IBrush ToolUnavailable = Rgba(233, 233, 235, .42);

    /// <summary>The first shadow layer of a popover (dark theme shadow-2: 0 4 12 at .45).</summary>
    public static readonly BoxShadow Shadow2A = new() { OffsetX = 0, OffsetY = 4, Blur = 12, Color = Color.FromArgb(115, 0, 0, 0) };

    /// <summary>The second shadow layer of a popover (0 12 32 at .4).</summary>
    public static readonly BoxShadow Shadow2B = new() { OffsetX = 0, OffsetY = 12, Blur = 32, Color = Color.FromArgb(102, 0, 0, 0) };

    /// <summary>The shadow of a popover or tooltip (both layers).</summary>
    public static BoxShadows Shadow2 => new(Shadow2A, [Shadow2B]);

    /// <summary>Spacing step 1.</summary>
    public const double Space1 = 4;

    /// <summary>Spacing step 2.</summary>
    public const double Space2 = 8;

    /// <summary>Spacing step 3.</summary>
    public const double Space3 = 12;

    /// <summary>Spacing step 4.</summary>
    public const double Space4 = 16;

    /// <summary>Spacing step 5.</summary>
    public const double Space5 = 24;

    /// <summary>Spacing step 6.</summary>
    public const double Space6 = 36;

    /// <summary>The smallest corner radius.</summary>
    public const double Radius1 = 6;

    /// <summary>The usual control corner radius.</summary>
    public const double Radius2 = 8;

    /// <summary>The radius of popovers and the search button.</summary>
    public const double Radius3 = 11;

    /// <summary>The radius of dialogs.</summary>
    public const double Radius4 = 16;

    /// <summary>A radius that makes any control a pill.</summary>
    public const double RadiusPill = 999;

    /// <summary>Space Grotesk, jatify.com's typeface (SIL OFL, app/Assets/Fonts).</summary>
    public static readonly FontFamily Font = new("avares://AssetShelf/Assets/Fonts#Space Grotesk");

    /// <summary>Height of the top bar.</summary>
    public const double MenuBarHeight = 36;

    /// <summary>Height of the status bar.</summary>
    public const double StatusBarHeight = 24;

    /// <summary>Height of a row of the controls panel.</summary>
    public const double RowHeight = 28;

    /// <summary>Height of a button, field or menu title.</summary>
    public const double ControlHeight = 26;

    /// <summary>Body text size.</summary>
    public const double Body = 13;

    /// <summary>Label text size.</summary>
    public const double Label = 12;

    /// <summary>Caption text size.</summary>
    public const double Caption = 11.5;

    /// <summary>Key chip text size.</summary>
    public const double Keys = 10.5;

    /// <summary>The longest a tooltip gets before it wraps.</summary>
    public const double TooltipMaxWidth = 260;

    /// <summary>How long the pointer rests before a tooltip shows, in milliseconds.</summary>
    public const int TooltipDelayMs = 450;
}
