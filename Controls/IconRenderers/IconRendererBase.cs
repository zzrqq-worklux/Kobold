using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FoldRa.Core;

namespace FoldRa.Controls.IconRenderers
{
    /// <summary>
    /// Interface for folder icon rendering - Strategy Pattern.
    /// SOLID: Open/Closed - Add new styles by implementing this interface.
    /// </summary>
    public interface IIconRenderer
    {
        /// <summary>
        /// Renders the folder icon to the canvas
        /// </summary>
        /// <param name="canvas">Canvas to draw on</param>
        /// <param name="color">Folder color hex string</param>
        void Render(Canvas canvas, string color);
    }

    /// <summary>
    /// Base class with common icon rendering utilities
    /// </summary>
    public abstract class IconRendererBase : IIconRenderer
    {
        public abstract void Render(Canvas canvas, string color);

        protected Color BaseColor { get; private set; }
        protected Color DarkColor { get; private set; }
        protected Color DarkerColor { get; private set; }
        protected Color LightColor { get; private set; }
        protected Color HighlightColor { get; private set; }

        /// <summary>
        /// Initialize color palette from base color
        /// </summary>
        protected void InitColors(string hexColor)
        {
            BaseColor = Utils.HexToColor(hexColor);
            DarkColor = Utils.DarkenColor(BaseColor, 0.55);
            DarkerColor = Utils.DarkenColor(BaseColor, 0.35);
            LightColor = Utils.LightenColor(BaseColor, 1.3);
            HighlightColor = Utils.LightenColor(BaseColor, 1.6);
        }

        /// <summary>
        /// Creates a Path element for folder parts
        /// </summary>
        protected System.Windows.Shapes.Path CreatePath(Geometry geometry, Brush fill, double left = 0, double top = 0)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = fill
            };
            Canvas.SetLeft(path, left);
            Canvas.SetTop(path, top);
            return path;
        }
    }

    /// <summary>
    /// Factory for creating icon renderers
    /// </summary>
    public static class IconRendererFactory
    {
        public static IIconRenderer Create(string style)
        {
            switch (style)
            {
                case "modern": return new ModernIconRenderer();
                case "minimal": return new MinimalIconRenderer();
                case "rounded": return new RoundedIconRenderer();
                case "flat": return new FlatIconRenderer();
                case "gradient": return new GradientIconRenderer();
                default: return new ClassicIconRenderer();
            }
        }
    }
}


