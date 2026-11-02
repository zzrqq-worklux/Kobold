namespace Kobold.Core
{
    /// <summary>
    /// Single source of truth for the visual language: palette, radius scale,
    /// type scale, spacing and motion durations. C# uses these constants
    /// directly; XAML resolves the matching 'Kobold.*' keys that ThemeManager
    /// installs into Application.Resources.
    ///
    /// Rules:
    /// - Every color literal in the app lives here (guarded by UiTokensCheck).
    /// - Dark/Light must expose the same field names.
    /// - OverlayDark/OverlayLight style menus and tooltips per theme.
    /// - The palette is a base theme; IconStyle (classic/handdrawn) is an
    ///   independent axis.
    /// </summary>
    public static class UiTokens
    {
        public static class Dark
        {
            public const string Background = "#191714";
            public const string Surface = "#211E1A";
            public const string Surface2 = "#2A261F";
            public const string Border = "#3D372E";
            public const string TextMuted = "#91887A";
            public const string TextSecondary = "#CBC3B5";
            public const string TextPrimary = "#FAF6EE";
            public const string TextFaint = "#6F675B";
            public const string ControlBackground = "#211E1A";
            public const string ControlBorder = "#3D372E";
            public const string ControlBorderHover = "#51493D";
            public const string SurfaceHover = "#2A261F";
            public const string ButtonSecondary = "#2A261F";
            public const string ButtonSecondaryHover = "#332D25";
            public const string IconFill = "#CCFAF6EE";
            public const string IconStroke = "#40FAF6EE";
            public const string MissingBadge = "#CCE06C75";
            public const string PanelBorder = "#CC3D372E";
            public const string DropIndicator = "#CCD97757";
            public const string SelectionBorder = "#CCD97757";
            public const string SelectionFillTop = "#2AD97757";
            public const string SelectionFillBottom = "#3DD97757";
            public const string LassoFill = "#208AB4DD";
            public const string LassoStroke = "#808AB4DD";
            public const string ScrollThumb = "#3D372E";
            public const string ScrollThumbHover = "#51493D";
            public const string SliderTrack = "#3D372E";
            public const string SliderThumb = "#91887A";
            public const string IslandBackground = "#F2221F1A";
            public const string IslandBackgroundExpanded = "#F8221F1A";
            public const string IslandBorder = "#CC3D372E";
            public const string IslandHover = "#1FFAF6EE";
            public const string IslandDivider = "#263D372E";
            public const string IslandForeground = "#FAF6EE";
            public const string DragGhost = "#642A261F";
            public const string Accent = "#D97757";
            public const string AccentHover = "#E89268";
            public const string AccentSoft = "#3B2A22";
            public const string OnAccent = "#FFFDF8";
            public const string Danger = "#E06C75";
        }

        public static class Light
        {
            public const string Background = "#F7F2E8";
            public const string Surface = "#FFFDF8";
            public const string Surface2 = "#EFE7D9";
            public const string Border = "#DFD4C2";
            public const string TextMuted = "#6F675B";
            public const string TextSecondary = "#403A31";
            public const string TextPrimary = "#1B1813";
            public const string TextFaint = "#948B7D";
            public const string ControlBackground = "#FFFDF8";
            public const string ControlBorder = "#DFD4C2";
            public const string ControlBorderHover = "#C9BCA8";
            public const string SurfaceHover = "#EFE7D9";
            public const string ButtonSecondary = "#EFE7D9";
            public const string ButtonSecondaryHover = "#E7DDCD";
            public const string IconFill = "#B31B1813";
            public const string IconStroke = "#401B1813";
            public const string MissingBadge = "#CCA9583E";
            public const string PanelBorder = "#CCDFD4C2";
            public const string DropIndicator = "#CCCC785C";
            public const string SelectionBorder = "#CCCC785C";
            public const string SelectionFillTop = "#1FCC785C";
            public const string SelectionFillBottom = "#33CC785C";
            public const string LassoFill = "#206A9BCC";
            public const string LassoStroke = "#806A9BCC";
            public const string ScrollThumb = "#C9BCA8";
            public const string ScrollThumbHover = "#B3A48E";
            public const string SliderTrack = "#DFD4C2";
            public const string SliderThumb = "#6F675B";
            public const string IslandBackground = "#F2F6F0E5";
            public const string IslandBackgroundExpanded = "#F8F6F0E5";
            public const string IslandBorder = "#CCDFD4C2";
            public const string IslandHover = "#1F1B1813";
            public const string IslandDivider = "#26DFD4C2";
            public const string IslandForeground = "#1B1813";
            public const string DragGhost = "#64FFFDF8";
            public const string Accent = "#CC785C";
            public const string AccentHover = "#A9583E";
            public const string AccentSoft = "#F3DFD4";
            public const string OnAccent = "#FFFDF8";
            public const string Danger = "#A53E2A";
        }

        /// <summary>Floating surfaces (menus, tooltips) - dark theme.</summary>
        public static class OverlayDark
        {
            public const string Background = "#F2211E1A";
            public const string Border = "#3D372E";
            public const string Hover = "#2A261F";
            public const string Checked = "#332D25";
            public const string Text = "#FAF6EE";
            public const string Muted = "#91887A";
            public const string Divider = "#3D372E";
            public const string TooltipBackground = "#F2191714";
            public const string TooltipBorder = "#3D372E";
        }

        /// <summary>Floating surfaces (menus, tooltips) - light theme.</summary>
        public static class OverlayLight
        {
            public const string Background = "#FFFDF8";
            public const string Border = "#DFD4C2";
            public const string Hover = "#EFE7D9";
            public const string Checked = "#E7DDCD";
            public const string Text = "#1B1813";
            public const string Muted = "#6F675B";
            public const string Divider = "#E9E0D2";
            public const string TooltipBackground = "#F2FFFDF8";
            public const string TooltipBorder = "#DFD4C2";
        }

        /// <summary>Theme-independent brand colors / fallbacks.</summary>
        public static class Shared
        {
            public const string Accent = "#CC785C";
            public const string AccentHover = "#A9583E";
            public const string OnAccent = "#FFFDF8";
            /// <summary>Dark ink for bright accent surfaces (luminance > 0.6).</summary>
            public const string OnAccentDark = "#1B1813";
            public const string White = "#FFFFFF";
            public const string Danger = "#A53E2A";
            public const string PinGold = "#FFD700";
            public const string PinDarkGold = "#B8860B";
        }

        /// <summary>Fallback widget colour (clay).</summary>
        public const string DefaultFolderColor = "#CC785C";

        /// <summary>
        /// Low-saturation Claude Warm widget colours offered by the picker.
        /// Must stay identical to <see cref="FolderIconPalette"/> - same hues,
        /// same order - so both pickers read as one family;
        /// tests/MenuRenderCheck pins the two together.
        /// </summary>
        public static readonly string[] FolderPalette =
        {
            "#CC785C", "#B5654A", "#D4A017", "#788C5D", "#5DB8A6",
            "#6A9BCC", "#9C7CA5", "#B56576", "#948B7D"
        };

        /// <summary>
        /// Folder icon colors offered by the folder-color menu. Must stay
        /// identical to <see cref="FolderPalette"/> - same hues, same order;
        /// tests/MenuRenderCheck pins the two together.
        /// </summary>
        public static readonly string[] FolderIconPalette =
        {
            "#CC785C", "#B5654A", "#D4A017", "#788C5D", "#5DB8A6",
            "#6A9BCC", "#9C7CA5", "#B56576", "#948B7D"
        };

        /// <summary>
        /// The nine Tailwind hues the pre-Claude-Warm picker wrote into saved
        /// configs. Frozen for <see cref="WidgetColorMigration"/> only - never
        /// offer these in the UI; new picks use <see cref="FolderPalette"/>.
        /// </summary>
        public static readonly string[] LegacyFolderPalette =
        {
            "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E",
            "#06B6D4", "#3B82F6", "#8B5CF6", "#94A3B8"
        };

        /// <summary>
        /// Localization keys for the palette above, index-aligned with it: a
        /// colour swatch alone leaves the user guessing which hue is which.
        /// tests/MenuRenderCheck fails when the two arrays drift apart.
        /// </summary>
        public static readonly string[] FolderIconPaletteNames =
        {
            "Color_Clay", "Color_Terracotta", "Color_Ochre", "Color_Sage", "Color_Teal",
            "Color_DustyBlue", "Color_Heather", "Color_Fig", "Color_WarmGray"
        };

        #region Font families

        /// <summary>Body/UI stack: Segoe UI with the Chinese UI fallback.</summary>
        public const string FontFamilyUi = "Segoe UI, Microsoft YaHei UI, Microsoft YaHei";

        /// <summary>
        /// Display stack for titles: the mockup's Fraunces/Noto Serif SC look,
        /// without bundling a font. Chinese falls through to SimSun.
        /// </summary>
        public const string FontFamilyDisplay =
            "Fraunces, Noto Serif SC, Source Han Serif SC, Songti SC, SimSun, Georgia";

        #endregion

        #region Radius scale

        public const double RadiusSmall = 4;
        public const double RadiusControl = 8;
        public const double RadiusWindow = 10;
        public const double RadiusPanel = 12;

        #endregion

        #region Type scale

        public const double FontCaption = 11;
        public const double FontBody = 13;
        public const double FontSubtitle = 14;
        public const double FontTitle = 16;

        #endregion

        #region Spacing scale (4 px grid)

        public const double Space1 = 4;
        public const double Space2 = 8;
        public const double Space3 = 12;
        public const double Space4 = 16;
        public const double Space6 = 24;

        #endregion

        #region Motion durations

        public const int DurationFastMs = 100;
        public const int DurationNormalMs = 150;
        public const int DurationSlowMs = 200;

        #endregion

        #region Panel glow (the search widget's query-in-flight meteor)

        /// <summary>One revolution of the loading comet, in seconds.</summary>
        public const double GlowSeconds = 1.2;

        /// <summary>Fraction of the ring perimeter the faint tail sweeps.</summary>
        public const double GlowTailFraction = 0.28;

        /// <summary>
        /// Largest fraction a layer's dash may span. A fraction of 1 would make
        /// the tail's dash as long as the ring: no gap, nothing to animate.
        /// </summary>
        public const double GlowMaxTailFraction = 0.9;

        /// <summary>Radius of the glow ring: matches the chrome shell's corner.</summary>
        public const double GlowRadius = RadiusPanel;

        /// <summary>Base ring width (DIP): covers the shell border while loading.</summary>
        public const double GlowTrackThickness = 1;

        /// <summary>Soft halo width (DIP) behind the comet head.</summary>
        public const double GlowHaloThickness = 6;

        /// <summary>Head / mid / tail core width (DIP).</summary>
        public const double GlowCoreThickness = 2.5;

        /// <summary>Track alpha over the shell border.</summary>
        public const double GlowTrackAlpha = 0.35;

        /// <summary>Layer opacities: the head is opaque, the halo faintest.</summary>
        public const double GlowHaloAlpha = 0.12;

        public const double GlowTailAlpha = 0.15;

        public const double GlowMidAlpha = 0.45;

        /// <summary>Dash-length shares of the tail: head / mid / tail / halo.</summary>
        public const double GlowHeadShare = 0.1;

        public const double GlowMidShare = 0.35;

        public const double GlowTailShare = 1.0;

        public const double GlowHaloShare = 0.45;

        #endregion
    }
}
