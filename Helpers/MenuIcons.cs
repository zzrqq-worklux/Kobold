using System.Windows.Media;
using Kobold.Controls;
using Kobold.Core.Icons;

namespace Kobold.Helpers
{
    /// <summary>
    /// One place that builds menu icons. A null brush means the presenter
    /// follows the live theme's muted ink; danger entries pass the theme
    /// danger brush explicitly.
    /// </summary>
    public static class MenuIcons
    {
        public static IconPresenter Create(IconId icon, Brush brush = null, double size = 15)
        {
            return new IconPresenter
            {
                Icon = icon,
                Size = size,
                Brush = brush
            };
        }
    }
}
