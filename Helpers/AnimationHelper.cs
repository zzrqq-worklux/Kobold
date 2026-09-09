using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FoldRa.Helpers
{
    /// <summary>
    /// Centralized animation helper for consistent animations across the app
    /// </summary>
    public static class AnimationHelper
    {
        // Duration presets
        public static readonly Duration Fast = new Duration(TimeSpan.FromMilliseconds(100));
        public static readonly Duration Normal = new Duration(TimeSpan.FromMilliseconds(150));
        public static readonly Duration Slow = new Duration(TimeSpan.FromMilliseconds(200));

        /// <summary>
        /// Creates a fade animation
        /// </summary>
        public static DoubleAnimation Fade(double to, Duration? duration = null)
        {
            return new DoubleAnimation(to, duration ?? Normal)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
        }

        /// <summary>
        /// Creates a fade animation with from value
        /// </summary>
        public static DoubleAnimation Fade(double from, double to, Duration? duration = null)
        {
            return new DoubleAnimation(from, to, duration ?? Normal)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
        }

        /// <summary>
        /// Creates a scale animation
        /// </summary>
        public static DoubleAnimation Scale(double to, Duration? duration = null)
        {
            return new DoubleAnimation(to, duration ?? Fast)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
        }

        /// <summary>
        /// Animates element opacity
        /// </summary>
        public static void FadeElement(UIElement element, double to, Duration? duration = null, Action onComplete = null)
        {
            var anim = Fade(to, duration);
            if (onComplete != null)
            {
                anim.Completed += (s, e) => onComplete();
            }
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// Animates element opacity from a value
        /// </summary>
        public static void FadeElement(UIElement element, double from, double to, Duration? duration = null, Action onComplete = null)
        {
            var anim = Fade(from, to, duration);
            if (onComplete != null)
            {
                anim.Completed += (s, e) => onComplete();
            }
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// Animates a ScaleTransform
        /// </summary>
        public static void ScaleElement(ScaleTransform transform, double to, Duration? duration = null)
        {
            var anim = Scale(to, duration);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>
        /// Window drag opacity effect
        /// </summary>
        public static void StartDrag(Window window)
        {
            FadeElement(window, 0.7, Fast);
        }

        /// <summary>
        /// Window drag end effect
        /// </summary>
        public static void EndDrag(Window window)
        {
            FadeElement(window, 1.0, Normal);
        }

        /// <summary>
        /// Hover enter effect for folder icon
        /// </summary>
        public static void HoverEnter(ScaleTransform transform)
        {
            var anim = new DoubleAnimation(1.08, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>
        /// Hover leave effect for folder icon
        /// </summary>
        public static void HoverLeave(ScaleTransform transform)
        {
            var anim = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>
        /// Drop target highlight effect - Windows-like bounce/pulse
        /// </summary>
        public static void DropHighlight(UIElement glowElement, ScaleTransform iconScale)
        {
            // Glow effect - smooth fade in
            var glowAnim = new DoubleAnimation(0, 0.85, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            glowElement.BeginAnimation(UIElement.OpacityProperty, glowAnim);
            
            // Bounce scale effect - slightly overshoot then settle
            var scaleUp = new DoubleAnimation(1.0, 1.15, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            };
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        }

        /// <summary>
        /// Drop target reset effect - smooth return
        /// </summary>
        public static void DropReset(UIElement glowElement, ScaleTransform iconScale)
        {
            // Fade glow smoothly
            var glowAnim = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            glowElement.BeginAnimation(UIElement.OpacityProperty, glowAnim);
            
            // Smooth scale back
            var scaleDown = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(280)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        }

        /// <summary>
        /// Panel open animation - fade in only (no scale to prevent text blur)
        /// </summary>
        public static void PanelOpen(FrameworkElement panel, bool fromLeft = false)
        {
            // Set initial state
            panel.Opacity = 0;
            panel.RenderTransform = null; // No transform - keeps text crisp
            
            // Fade in smoothly
            var fadeAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        /// <summary>
        /// Panel close animation - fade out only (no scale to prevent text blur)
        /// </summary>
        public static void PanelClose(FrameworkElement panel, bool toLeft = false, Action onComplete = null)
        {
            // Fade out smoothly
            var fadeAnim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            
            if (onComplete != null)
            {
                fadeAnim.Completed += (s, e) => onComplete();
            }
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }
    }
}


