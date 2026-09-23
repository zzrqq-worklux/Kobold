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
    /// </summary>
    public static class UiTokens
    {
        public static class Dark
        {
            public const string Background = "#1E1E1E";
            public const string Surface = "#282828";
            public const string Border = "#3C3C3C";
            public const string TextMuted = "#808080";
            public const string TextSecondary = "#B0B0B0";
            public const string TextPrimary = "#F0F0F0";
            public const string TextFaint = "#60FFFFFF";
            public const string ControlBackground = "#2D2D2D";
            public const string ControlBorder = "#3C3C3C";
            public const string ControlBorderHover = "#505050";
            public const string SurfaceHover = "#3C3C3C";
            public const string ButtonSecondary = "#3C3C3C";
            public const string ButtonSecondaryHover = "#4A4A4A";
            public const string IconFill = "#80FFFFFF";
            public const string IconStroke = "#40FFFFFF";
            public const string MissingBadge = "#CCB0B0B0";
            public const string PanelBorder = "#30FFFFFF";
            public const string DropIndicator = "#80FFFFFF";
            public const string SelectionBorder = "#80B4FF";
            public const string SelectionFillTop = "#1580B4FF";
            public const string SelectionFillBottom = "#2580B4FF";
            public const string LassoFill = "#2060A0FF";
            public const string LassoStroke = "#60A0FF";
            public const string ScrollThumb = "#4A4A4A";
            public const string ScrollThumbHover = "#5A5A5A";
            public const string SliderTrack = "#3C3C3C";
            public const string SliderThumb = "#B0B0B0";
            public const string IslandBackground = "#CC202020";
            public const string IslandBackgroundExpanded = "#E6202020";
            public const string IslandBorder = "#00000000";
            public const string IslandHover = "#33FFFFFF";
            public const string IslandDivider = "#33FFFFFF";
            public const string IslandForeground = "#FFFFFF";
            public const string DragGhost = "#643C3C3C";
        }

        public static class Light
        {
            public const string Background = "#F3F3F3";
            public const string Surface = "#FFFFFF";
            public const string Border = "#D6D6D6";
            public const string TextMuted = "#8A8A8A";
            public const string TextSecondary = "#5A5A5A";
            public const string TextPrimary = "#1F1F1F";
            public const string TextFaint = "#60000000";
            public const string ControlBackground = "#FFFFFF";
            public const string ControlBorder = "#C9C9C9";
            public const string ControlBorderHover = "#A8A8A8";
            public const string SurfaceHover = "#ECECEC";
            public const string ButtonSecondary = "#E5E5E5";
            public const string ButtonSecondaryHover = "#D9D9D9";
            public const string IconFill = "#80000000";
            public const string IconStroke = "#40000000";
            public const string MissingBadge = "#CC909090";
            public const string PanelBorder = "#30000000";
            public const string DropIndicator = "#803B82F6";
            public const string SelectionBorder = "#80B4FF";
            public const string SelectionFillTop = "#1580B4FF";
            public const string SelectionFillBottom = "#2580B4FF";
            public const string LassoFill = "#2060A0FF";
            public const string LassoStroke = "#60A0FF";
            public const string ScrollThumb = "#C0C0C0";
            public const string ScrollThumbHover = "#A8A8A8";
            public const string SliderTrack = "#D6D6D6";
            public const string SliderThumb = "#8A8A8A";
            public const string IslandBackground = "#CCFFFFFF";
            public const string IslandBackgroundExpanded = "#E6FFFFFF";
            public const string IslandBorder = "#33000000";
            public const string IslandHover = "#14000000";
            public const string IslandDivider = "#26000000";
            public const string IslandForeground = "#1F1F1F";
            public const string DragGhost = "#64FFFFFF";
        }

        /// <summary>Floating surfaces (menus, tooltips) - dark theme.</summary>
        public static class OverlayDark
        {
            public const string Background = "#282828";
            public const string Border = "#3C3C3C";
            public const string Hover = "#3C3C3C";
            public const string Checked = "#484848";
            public const string Text = "#F0F0F0";
            public const string Muted = "#808080";
            public const string Divider = "#3C3C3C";
            public const string TooltipBackground = "#F0202020";
            public const string TooltipBorder = "#40FFFFFF";
        }

        /// <summary>Floating surfaces (menus, tooltips) - light theme.</summary>
        public static class OverlayLight
        {
            public const string Background = "#F9F9F9";
            public const string Border = "#D6D6D6";
            public const string Hover = "#ECECEC";
            public const string Checked = "#E0E0E0";
            public const string Text = "#1F1F1F";
            public const string Muted = "#6E6E6E";
            public const string Divider = "#E3E3E3";
            public const string TooltipBackground = "#F2FFFFFF";
            public const string TooltipBorder = "#40000000";
        }

        /// <summary>Theme-independent brand colors.</summary>
        public static class Shared
        {
            public const string Accent = "#3B82F6";
            public const string AccentHover = "#2563EB";
            public const string OnAccent = "#FFFFFF";
            public const string White = "#FFFFFF";
            public const string Danger = "#E81123";
            public const string PinGold = "#FFD700";
            public const string PinDarkGold = "#B8860B";
        }

        public const string DefaultFolderColor = "#3B82F6";

        /// <summary>Folder color choices offered in the picker and tray defaults.</summary>
        public static readonly string[] FolderPalette =
        {
            "#3B82F6", "#EF4444", "#22C55E", "#F59E0B", "#8B5CF6", "#EC4899",
            "#06B6D4", "#84CC16", "#F97316", "#6366F1", "#14B8A6", "#A855F7"
        };

        /// <summary>
        /// Folder icon colors offered by the folder-color menu. The hues reuse the
        /// widget palette so the two pickers read as one family; the grey is the
        /// one extra, since a desaturated folder has no widget counterpart.
        /// </summary>
        public static readonly string[] FolderIconPalette =
        {
            "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E",
            "#06B6D4", "#3B82F6", "#8B5CF6", "#94A3B8"
        };

        #region Radius scale

        public const double RadiusSmall = 4;
        public const double RadiusControl = 8;
        public const double RadiusWindow = 12;
        public const double RadiusPanel = 16;

        #endregion

        #region Type scale

        public const double FontCaption = 11;
        public const double FontBody = 12;
        public const double FontSubtitle = 13;
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

        #region Widget menu tint (context menus pick up their widget color)

        public const double MenuTintBackground = 0.14;
        public const double MenuTintState = 0.28;
        public const double MenuTintBorder = 0.25;

        #endregion
    }
}
