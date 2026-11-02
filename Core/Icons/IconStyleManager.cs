using System;

namespace Kobold.Core.Icons
{
    /// <summary>
    /// The two icon-style ids persisted in AppConfig.IconStyle. Kept as
    /// strings because existing configs and the 64px renderer factory already
    /// speak them; this type only centralises the vocabulary.
    /// </summary>
    public static class IconStyleIds
    {
        public const string Classic = "classic";
        public const string HandDrawn = "handdrawn";

        public static string Normalize(string style)
        {
            return string.Equals(style, HandDrawn, StringComparison.Ordinal)
                ? HandDrawn
                : Classic;
        }

        public static bool IsHandDrawn(string style)
        {
            return string.Equals(style, HandDrawn, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Process-wide current icon style for every 24px IconPresenter. The
    /// Settings window and widget refresh paths set it; presenters repaint
    /// while loaded. This is independent of the colour theme.
    /// </summary>
    public static class IconStyleManager
    {
        private static string _current = IconStyleIds.Classic;

        public static string Current
        {
            get { return _current; }
        }

        public static bool IsHandDrawn
        {
            get { return IconStyleIds.IsHandDrawn(_current); }
        }

        /// <summary>Raised only when the effective style actually changes.</summary>
        public static event Action Changed;

        public static void Set(string style)
        {
            string normalized = IconStyleIds.Normalize(style);
            if (string.Equals(_current, normalized, StringComparison.Ordinal)) return;

            _current = normalized;
            Action handler = Changed;
            if (handler != null) handler();
        }
    }
}
