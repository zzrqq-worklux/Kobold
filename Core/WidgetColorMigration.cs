using System;

namespace Kobold.Core
{
    /// <summary>
    /// Maps widget colours written with the pre-Claude-Warm palette onto the
    /// current palette. The old picker offered exactly nine Tailwind hues; a
    /// colour outside that set is a custom pick and is left untouched.
    /// </summary>
    public static class WidgetColorMigration
    {
        /// <summary>
        /// Returns the current-palette colour for a legacy hue, or the input
        /// unchanged when it is a new-palette or custom colour.
        /// </summary>
        public static string Upgrade(string color)
        {
            if (string.IsNullOrEmpty(color)) return color;

            // The two arrays are index-aligned: red -> clay, orange ->
            // terracotta, amber -> ochre, lime -> sage, green -> teal, cyan ->
            // dusty blue, blue -> heather, violet -> fig, slate -> warm gray.
            string[] legacy = UiTokens.LegacyFolderPalette;
            for (int i = 0; i < legacy.Length; i++)
            {
                if (string.Equals(legacy[i], color, StringComparison.OrdinalIgnoreCase))
                {
                    return UiTokens.FolderPalette[i];
                }
            }
            return color;
        }

        /// <summary>
        /// Applies <see cref="Upgrade"/> to every widget (and the legacy
        /// Folders list, which is migrated into Widgets right after). Called
        /// from the config load repair pass, so it runs on both the main file
        /// and the backup and is idempotent.
        /// </summary>
        public static void Apply(AppConfig config)
        {
            if (config == null) return;

            if (config.Folders != null)
            {
                foreach (var folder in config.Folders)
                {
                    if (folder != null) folder.Color = Upgrade(folder.Color);
                }
            }

            if (config.Widgets == null) return;
            foreach (var widget in config.Widgets)
            {
                if (widget != null) widget.Color = Upgrade(widget.Color);
            }
        }
    }
}
