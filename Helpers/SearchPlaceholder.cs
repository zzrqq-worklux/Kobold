using System;

namespace Kobold.Helpers
{
    /// <summary>
    /// The search box's hint rule: the placeholder is only for an unfocused,
    /// empty box. While the box has keyboard focus the hint must disappear
    /// even with empty text, or it overdraws the IME composition string.
    /// </summary>
    public static class SearchPlaceholder
    {
        public static bool ShouldShow(string text, bool hasKeyboardFocus)
        {
            return string.IsNullOrEmpty(text) && !hasKeyboardFocus;
        }
    }
}
