using System;
using System.Windows;
using System.Windows.Media.Animation;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>
    /// Motion presets shared by all windows. Durations come from UiTokens so
    /// everything animates on the same rhythm.
    /// </summary>
    public static class AnimationHelper
    {
        public static readonly Duration Fast = new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationFastMs));
        public static readonly Duration Normal = new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationNormalMs));
        public static readonly Duration Slow = new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationSlowMs));

        /// <summary>
        /// Panel open animation - fade in only (no scale to prevent text blur).
        /// Fades to the configured panel opacity instead of a hardcoded 1.
        /// </summary>
        public static void PanelOpen(FrameworkElement panel, double targetOpacity = 1.0)
        {
            panel.Opacity = 0;
            panel.RenderTransform = null; // No transform - keeps text crisp

            var fadeAnim = new DoubleAnimation(0, targetOpacity, Slow)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            panel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        /// <summary>
        /// Panel close animation - fade out only (no scale to prevent text blur).
        /// Starts from the current opacity (which may be a user-configured value).
        /// </summary>
        public static void PanelClose(FrameworkElement panel, Action onComplete = null)
        {
            var fadeAnim = new DoubleAnimation(panel.Opacity, 0, Fast)
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
