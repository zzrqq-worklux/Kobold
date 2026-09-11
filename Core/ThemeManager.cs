using System;
using System.Windows;
using System.Windows.Media;

namespace Kobold.Core
{
    /// <summary>
    /// Centralized theme management. Builds the current palette from
    /// UiTokens, exposes brushes to code, and installs 'Kobold.*' keys into
    /// Application.Resources so XAML can switch surfaces via DynamicResource.
    ///
    /// Floating surfaces (menus, tooltips) follow the active theme too.
    /// </summary>
    public static class ThemeManager
    {
        public const string BrushKeyPrefix = "Kobold.Brush.";
        public const string RadiusKeyPrefix = "Kobold.Radius.";
        public const string FontKeyPrefix = "Kobold.Font.";
        public const string SpaceKeyPrefix = "Kobold.Space.";

        #region Theme Changed Event

        /// <summary>
        /// Fired when theme changes - widgets subscribe to this for automatic updates
        /// </summary>
        public static event Action ThemeChanged;

        #endregion

        #region Theme State

        private static bool _isDarkTheme = true;

        /// <summary>
        /// Returns true if current theme is dark
        /// </summary>
        public static bool IsDarkTheme => _isDarkTheme;

        /// <summary>
        /// Sets the theme and notifies all subscribers
        /// </summary>
        public static void SetTheme(string theme)
        {
            bool newIsDark = theme == "dark";
            if (_isDarkTheme == newIsDark) return;

            _isDarkTheme = newIsDark;
            UpdateBrushes();
            InstallScalarTokens();
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// Initialize theme from config (call once at startup)
        /// </summary>
        public static void Initialize(string theme)
        {
            _isDarkTheme = theme == "dark";
            UpdateBrushes();
            InstallScalarTokens();
        }

        #endregion

        #region Theme-aware brushes

        public static SolidColorBrush BackgroundBrush { get; private set; }
        public static SolidColorBrush SurfaceBrush { get; private set; }
        public static SolidColorBrush SurfaceHoverBrush { get; private set; }
        public static SolidColorBrush BorderBrush { get; private set; }
        public static SolidColorBrush TextBrush { get; private set; }
        public static SolidColorBrush SecondaryTextBrush { get; private set; }
        public static SolidColorBrush MutedTextBrush { get; private set; }
        public static SolidColorBrush TextFaintBrush { get; private set; }
        public static SolidColorBrush ControlBackgroundBrush { get; private set; }
        public static SolidColorBrush ControlBorderBrush { get; private set; }
        public static SolidColorBrush ControlBorderHoverBrush { get; private set; }
        public static SolidColorBrush ButtonSecondaryBrush { get; private set; }
        public static SolidColorBrush ButtonSecondaryHoverBrush { get; private set; }
        public static SolidColorBrush IconFillBrush { get; private set; }
        public static SolidColorBrush IconStrokeBrush { get; private set; }
        public static SolidColorBrush MissingBadgeBrush { get; private set; }
        public static SolidColorBrush PanelBorderBrush { get; private set; }
        public static SolidColorBrush DropIndicatorBrush { get; private set; }
        public static SolidColorBrush SelectionBorderBrush { get; private set; }
        public static SolidColorBrush LassoFillBrush { get; private set; }
        public static SolidColorBrush LassoStrokeBrush { get; private set; }
        public static SolidColorBrush ScrollThumbBrush { get; private set; }
        public static SolidColorBrush ScrollThumbHoverBrush { get; private set; }
        public static SolidColorBrush SliderTrackBrush { get; private set; }
        public static SolidColorBrush SliderThumbBrush { get; private set; }
        public static SolidColorBrush IslandBackgroundBrush { get; private set; }
        public static SolidColorBrush IslandBackgroundExpandedBrush { get; private set; }
        public static SolidColorBrush IslandBorderBrush { get; private set; }
        public static SolidColorBrush IslandHoverBrush { get; private set; }
        public static SolidColorBrush IslandDividerBrush { get; private set; }
        public static SolidColorBrush IslandForegroundBrush { get; private set; }
        public static SolidColorBrush DragGhostBrush { get; private set; }

        // Floating surfaces (menus, tooltips)
        public static SolidColorBrush OverlayBackgroundBrush { get; private set; }
        public static SolidColorBrush OverlayBorderBrush { get; private set; }
        public static SolidColorBrush OverlayHoverBrush { get; private set; }
        public static SolidColorBrush OverlayCheckedBrush { get; private set; }
        public static SolidColorBrush OverlayTextBrush { get; private set; }
        public static SolidColorBrush OverlayMutedBrush { get; private set; }
        public static SolidColorBrush OverlayDividerBrush { get; private set; }
        public static SolidColorBrush TooltipBackgroundBrush { get; private set; }
        public static SolidColorBrush TooltipBorderBrush { get; private set; }

        // Pin states reuse the icon overlay colors.
        public static SolidColorBrush PinUnpinnedFillBrush => IconFillBrush;
        public static SolidColorBrush PinUnpinnedStrokeBrush => IconStrokeBrush;

        // Dialog aliases (semantic names for the same palette).
        public static SolidColorBrush DialogBackgroundBrush => BackgroundBrush;
        public static SolidColorBrush DialogBorderBrush => BorderBrush;
        public static SolidColorBrush InputBackgroundBrush => ControlBackgroundBrush;
        public static SolidColorBrush InputBorderBrush => ControlBorderBrush;

        /// <summary>Hover background for panel items - theme-dependent.</summary>
        public static SolidColorBrush HoverBrush => _isDarkTheme ? DarkHoverBrush : LightHoverBrush;

        #endregion

        #region Theme-independent brushes (always-dark overlays + brand colors)

        public static readonly SolidColorBrush WhiteBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.White));
        public static readonly SolidColorBrush TransparentBrush = FreezeColor(Colors.Transparent);
        public static readonly SolidColorBrush OnAccentBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.OnAccent));
        public static readonly SolidColorBrush AccentBlueBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.Accent));
        public static readonly SolidColorBrush AccentHoverBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.AccentHover));
        public static readonly SolidColorBrush DangerBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.Danger));
        public static readonly SolidColorBrush PinGoldBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.PinGold));
        public static readonly SolidColorBrush PinDarkGoldBrush = FreezeColor(Utils.HexToColor(UiTokens.Shared.PinDarkGold));

        public static readonly SolidColorBrush DarkHoverBrush = FreezeColor(Color.FromArgb(35, 255, 255, 255));
        public static readonly SolidColorBrush LightHoverBrush = FreezeColor(Color.FromArgb(25, 0, 0, 0));

        #endregion

        #region Brush building & resource installation

        /// <summary>
        /// Rebuilds every theme-aware brush and installs it as a
        /// 'Kobold.Brush.*' resource key for XAML DynamicResource lookups.
        /// </summary>
        private static void UpdateBrushes()
        {
            BackgroundBrush = Themed("Background", UiTokens.Dark.Background, UiTokens.Light.Background);
            SurfaceBrush = Themed("Surface", UiTokens.Dark.Surface, UiTokens.Light.Surface);
            SurfaceHoverBrush = Themed("SurfaceHover", UiTokens.Dark.SurfaceHover, UiTokens.Light.SurfaceHover);
            BorderBrush = Themed("Border", UiTokens.Dark.Border, UiTokens.Light.Border);
            TextBrush = Themed("TextPrimary", UiTokens.Dark.TextPrimary, UiTokens.Light.TextPrimary);
            SecondaryTextBrush = Themed("TextSecondary", UiTokens.Dark.TextSecondary, UiTokens.Light.TextSecondary);
            MutedTextBrush = Themed("TextMuted", UiTokens.Dark.TextMuted, UiTokens.Light.TextMuted);
            TextFaintBrush = Themed("TextFaint", UiTokens.Dark.TextFaint, UiTokens.Light.TextFaint);
            ControlBackgroundBrush = Themed("ControlBackground", UiTokens.Dark.ControlBackground, UiTokens.Light.ControlBackground);
            ControlBorderBrush = Themed("ControlBorder", UiTokens.Dark.ControlBorder, UiTokens.Light.ControlBorder);
            ControlBorderHoverBrush = Themed("ControlBorderHover", UiTokens.Dark.ControlBorderHover, UiTokens.Light.ControlBorderHover);
            ButtonSecondaryBrush = Themed("ButtonSecondary", UiTokens.Dark.ButtonSecondary, UiTokens.Light.ButtonSecondary);
            ButtonSecondaryHoverBrush = Themed("ButtonSecondaryHover", UiTokens.Dark.ButtonSecondaryHover, UiTokens.Light.ButtonSecondaryHover);
            IconFillBrush = Themed("IconFill", UiTokens.Dark.IconFill, UiTokens.Light.IconFill);
            IconStrokeBrush = Themed("IconStroke", UiTokens.Dark.IconStroke, UiTokens.Light.IconStroke);
            MissingBadgeBrush = Themed("MissingBadge", UiTokens.Dark.MissingBadge, UiTokens.Light.MissingBadge);
            PanelBorderBrush = Themed("PanelBorder", UiTokens.Dark.PanelBorder, UiTokens.Light.PanelBorder);
            DropIndicatorBrush = Themed("DropIndicator", UiTokens.Dark.DropIndicator, UiTokens.Light.DropIndicator);
            SelectionBorderBrush = Themed("SelectionBorder", UiTokens.Dark.SelectionBorder, UiTokens.Light.SelectionBorder);
            LassoFillBrush = Themed("LassoFill", UiTokens.Dark.LassoFill, UiTokens.Light.LassoFill);
            LassoStrokeBrush = Themed("LassoStroke", UiTokens.Dark.LassoStroke, UiTokens.Light.LassoStroke);
            ScrollThumbBrush = Themed("ScrollThumb", UiTokens.Dark.ScrollThumb, UiTokens.Light.ScrollThumb);
            ScrollThumbHoverBrush = Themed("ScrollThumbHover", UiTokens.Dark.ScrollThumbHover, UiTokens.Light.ScrollThumbHover);
            SliderTrackBrush = Themed("SliderTrack", UiTokens.Dark.SliderTrack, UiTokens.Light.SliderTrack);
            SliderThumbBrush = Themed("SliderThumb", UiTokens.Dark.SliderThumb, UiTokens.Light.SliderThumb);
            IslandBackgroundBrush = Themed("IslandBackground", UiTokens.Dark.IslandBackground, UiTokens.Light.IslandBackground);
            IslandBackgroundExpandedBrush = Themed("IslandBackgroundExpanded", UiTokens.Dark.IslandBackgroundExpanded, UiTokens.Light.IslandBackgroundExpanded);
            IslandBorderBrush = Themed("IslandBorder", UiTokens.Dark.IslandBorder, UiTokens.Light.IslandBorder);
            IslandHoverBrush = Themed("IslandHover", UiTokens.Dark.IslandHover, UiTokens.Light.IslandHover);
            IslandDividerBrush = Themed("IslandDivider", UiTokens.Dark.IslandDivider, UiTokens.Light.IslandDivider);
            IslandForegroundBrush = Themed("IslandForeground", UiTokens.Dark.IslandForeground, UiTokens.Light.IslandForeground);
            DragGhostBrush = Themed("DragGhost", UiTokens.Dark.DragGhost, UiTokens.Light.DragGhost);

            OverlayBackgroundBrush = Themed("OverlayBackground", UiTokens.OverlayDark.Background, UiTokens.OverlayLight.Background);
            OverlayBorderBrush = Themed("OverlayBorder", UiTokens.OverlayDark.Border, UiTokens.OverlayLight.Border);
            OverlayHoverBrush = Themed("OverlayHover", UiTokens.OverlayDark.Hover, UiTokens.OverlayLight.Hover);
            OverlayCheckedBrush = Themed("OverlayChecked", UiTokens.OverlayDark.Checked, UiTokens.OverlayLight.Checked);
            OverlayTextBrush = Themed("OverlayText", UiTokens.OverlayDark.Text, UiTokens.OverlayLight.Text);
            OverlayMutedBrush = Themed("OverlayMuted", UiTokens.OverlayDark.Muted, UiTokens.OverlayLight.Muted);
            OverlayDividerBrush = Themed("OverlayDivider", UiTokens.OverlayDark.Divider, UiTokens.OverlayLight.Divider);
            TooltipBackgroundBrush = Themed("TooltipBackground", UiTokens.OverlayDark.TooltipBackground, UiTokens.OverlayLight.TooltipBackground);
            TooltipBorderBrush = Themed("TooltipBorder", UiTokens.OverlayDark.TooltipBorder, UiTokens.OverlayLight.TooltipBorder);

            InstallOverlayResources();
        }

        /// <summary>Installs radii, type and spacing tokens (theme-independent).</summary>
        private static void InstallScalarTokens()
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return;

            // Radii are installed as CornerRadius: BAML cannot convert a boxed
            // double resource into the CornerRadius property type.
            resources[RadiusKeyPrefix + "Small"] = new CornerRadius(UiTokens.RadiusSmall);
            resources[RadiusKeyPrefix + "Control"] = new CornerRadius(UiTokens.RadiusControl);
            resources[RadiusKeyPrefix + "Window"] = new CornerRadius(UiTokens.RadiusWindow);
            resources[RadiusKeyPrefix + "Panel"] = new CornerRadius(UiTokens.RadiusPanel);

            resources[FontKeyPrefix + "Caption"] = UiTokens.FontCaption;
            resources[FontKeyPrefix + "Body"] = UiTokens.FontBody;
            resources[FontKeyPrefix + "Subtitle"] = UiTokens.FontSubtitle;
            resources[FontKeyPrefix + "Title"] = UiTokens.FontTitle;

            resources[SpaceKeyPrefix + "1"] = UiTokens.Space1;
            resources[SpaceKeyPrefix + "2"] = UiTokens.Space2;
            resources[SpaceKeyPrefix + "3"] = UiTokens.Space3;
            resources[SpaceKeyPrefix + "4"] = UiTokens.Space4;
            resources[SpaceKeyPrefix + "6"] = UiTokens.Space6;
        }

        private static void InstallOverlayResources()
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return;

            resources[BrushKeyPrefix + "Accent"] = AccentBlueBrush;
            resources[BrushKeyPrefix + "AccentHover"] = AccentHoverBrush;
            resources[BrushKeyPrefix + "OnAccent"] = OnAccentBrush;
            resources[BrushKeyPrefix + "Danger"] = DangerBrush;
            resources[BrushKeyPrefix + "PinGold"] = PinGoldBrush;
            resources[BrushKeyPrefix + "PinDarkGold"] = PinDarkGoldBrush;

            // Gradient stops need Color (not Brush) resources; selection colors
            // are identical in both themes.
            resources["Kobold.Color.SelectionFillTop"] = Utils.HexToColor(UiTokens.Dark.SelectionFillTop);
            resources["Kobold.Color.SelectionFillBottom"] = Utils.HexToColor(UiTokens.Dark.SelectionFillBottom);
        }

        private static SolidColorBrush Themed(string key, string darkHex, string lightHex)
        {
            var brush = FreezeColor(Utils.HexToColor(_isDarkTheme ? darkHex : lightHex));
            var resources = Application.Current?.Resources;
            if (resources != null) resources[BrushKeyPrefix + key] = brush;
            return brush;
        }

        private static SolidColorBrush BrandBrush(string hex)
        {
            return FreezeColor(Utils.HexToColor(hex));
        }

        private static SolidColorBrush FreezeColor(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        #endregion
    }
}
