using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>The shared floating surface behind click-opened launchers.</summary>
    public static class LauncherCard
    {
        public static Border Create(UIElement content, double width = double.NaN)
        {
            var card = new Border
            {
                Background = ThemeManager.SurfaceBrush,
                BorderBrush = ThemeManager.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                Padding = new Thickness(4),
                Child = content,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 5,
                    Opacity = 0.4,
                    Color = Colors.Black,
                    RenderingBias = RenderingBias.Quality
                }
            };
            if (!double.IsNaN(width) && width > 0) card.Width = width;
            return card;
        }
    }
}
