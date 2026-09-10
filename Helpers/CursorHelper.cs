using System;
using System.Windows.Input;

namespace Kobold.Helpers
{
    /// <summary>
    /// Grab cursors (open hand on hover, closed hand while dragging) loaded from
    /// embedded resources. Falls back to the built-in pointing hand if a resource
    /// is missing. Cursors are from antiden/macOS-cursors-for-Windows (MIT); see
    /// Resources\cursors.LICENSE.txt.
    /// </summary>
    public static class CursorHelper
    {
        public static Cursor OpenHand { get; } = Load("cursor_open.cur");
        public static Cursor GrabHand { get; } = Load("cursor_grab.cur");

        private static Cursor Load(string fileName)
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Resources/" + fileName));
                if (info != null) return new Cursor(info.Stream);
            }
            catch { /* fall through to the built-in hand */ }
            return Cursors.Hand;
        }
    }
}
