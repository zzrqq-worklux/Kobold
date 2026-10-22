using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.Helpers
{
    /// <summary>
    /// The one colour picker: a 3x3 palette of the shared widget/folder hues
    /// with a ring and a check on the current colour, a caption that names the
    /// colour under the cursor, and an optional "back to default" button.
    /// <see cref="Build"/> returns an unshown window (pins inspect it without
    /// a message loop); <see cref="Show"/> is the modal entry point.
    /// </summary>
    public static class ColorPaletteDialog
    {
        /// <summary>
        /// Builds the palette window without showing it. The caller owns the
        /// window: picking a colour or the default button runs the callback
        /// and then closes it.
        /// </summary>
        public static Window Build(string title, IReadOnlyList<string> palette, string current,
            bool allowDefault, string defaultText, Action<string> onPick, Action onDefault)
        {
            var window = new Window
            {
                Title = title,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var colors = palette ?? Array.Empty<string>();
            string currentName = CurrentColorName(colors, current);

            // The caption starts on the current colour and follows the hover.
            var caption = new TextBlock
            {
                Tag = "PaletteCaption",
                Text = currentName,
                FontSize = UiTokens.FontCaption,
                Foreground = ThemeManager.MutedTextBrush,
                Margin = new Thickness(0, UiTokens.Space3, 0, 0)
            };

            var content = new StackPanel { Margin = new Thickness(UiTokens.Space6) };
            content.Children.Add(CreateHeader(window, title));

            var grid = new UniformGrid { Columns = 3 };
            for (int i = 0; i < colors.Count; i++)
            {
                grid.Children.Add(CreateSwatch(window, colors[i], ColorName(i), current,
                    currentName, caption, onPick));
            }
            content.Children.Add(grid);
            content.Children.Add(caption);

            if (allowDefault)
            {
                var defaultButton = new Button
                {
                    Tag = "PaletteDefault",
                    Content = defaultText ?? string.Empty,
                    Style = Application.Current?.TryFindResource("Kobold.Style.SecondaryButton") as Style,
                    MinWidth = 84,
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, UiTokens.Space4, 0, 0)
                };
                defaultButton.Click += (s, e) =>
                {
                    onDefault?.Invoke();
                    window.Close();
                };
                content.Children.Add(defaultButton);
            }

            window.Content = new Border
            {
                Background = ThemeManager.DialogBackgroundBrush,
                BorderBrush = ThemeManager.DialogBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
                Child = content
            };

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                window.Close();
                e.Handled = true;
            };

            return window;
        }

        /// <summary>
        /// Shows the palette modally, centred on the owner when there is one.
        /// Picking runs the callback before the window closes.
        /// </summary>
        public static void Show(Window owner, string title, IReadOnlyList<string> palette, string current,
            bool allowDefault, string defaultText, Action<string> onPick, Action onDefault)
        {
            var window = Build(title, palette, current, allowDefault, defaultText, onPick, onDefault);
            window.Owner = owner;
            window.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            window.ShowDialog();
        }

        private static Grid CreateHeader(Window window, string title)
        {
            var header = new Grid { Margin = new Thickness(0, 0, 0, UiTokens.Space3) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Tag = "PaletteTitle",
                Text = title ?? string.Empty,
                FontSize = UiTokens.FontSubtitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeManager.TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(titleText);

            var close = new TextBlock
            {
                Tag = "PaletteClose",
                Text = "\u00D7",
                FontSize = 14,
                Foreground = ThemeManager.MutedTextBrush,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(UiTokens.Space2, 0, 0, 0)
            };
            close.MouseLeftButtonUp += (s, e) => window.Close();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);

            return header;
        }

        private static Border CreateSwatch(Window window, string color, string name, string current,
            string currentName, TextBlock caption, Action<string> onPick)
        {
            bool isCurrent = string.Equals(color, current, StringComparison.OrdinalIgnoreCase);
            var fill = new SolidColorBrush(Utils.HexToColor(color));
            fill.Freeze();

            var swatch = new Border
            {
                Width = 52,
                Height = 52,
                Margin = new Thickness(7),
                CornerRadius = new CornerRadius(9),
                Background = fill,
                Tag = color,
                Cursor = Cursors.Hand,
                BorderBrush = isCurrent ? ThemeManager.TextBrush : ThemeManager.BorderBrush,
                BorderThickness = isCurrent ? new Thickness(3) : new Thickness(1)
            };

            if (isCurrent)
            {
                swatch.Child = new TextBlock
                {
                    Tag = "PaletteCheck",
                    Text = "\u2713",
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeManager.TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            swatch.MouseEnter += (s, e) =>
            {
                swatch.Opacity = 0.85;
                caption.Text = name;
            };
            swatch.MouseLeave += (s, e) =>
            {
                swatch.Opacity = 1.0;
                caption.Text = currentName;
            };
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                onPick?.Invoke(color);
                window.Close();
            };

            return swatch;
        }

        private static string CurrentColorName(IReadOnlyList<string> palette, string current)
        {
            if (palette == null || string.IsNullOrEmpty(current)) return string.Empty;

            for (int i = 0; i < palette.Count; i++)
            {
                if (string.Equals(palette[i], current, StringComparison.OrdinalIgnoreCase))
                    return ColorName(i);
            }

            return string.Empty;
        }

        private static string ColorName(int index)
        {
            if (index < 0 || index >= UiTokens.FolderIconPaletteNames.Length) return string.Empty;
            return Localization.Get(UiTokens.FolderIconPaletteNames[index]);
        }
    }
}
