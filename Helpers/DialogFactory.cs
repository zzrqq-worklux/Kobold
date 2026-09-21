using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.Helpers
{
    /// <summary>
    /// Shared dark dialogs so every prompt looks the same instead of mixing
    /// the themed rounded dialog with a raw WPF window.
    /// </summary>
    public static class DialogFactory
    {
        /// <summary>
        /// Modal single-line input. Returns the entered text, or null when the
        /// user cancels. validate may return an error message; it is shown
        /// inside the dialog and the dialog stays open. Callers decide how to
        /// validate the value.
        /// </summary>
        public static string ShowInput(Window owner, string title, string prompt, string initialValue,
            Func<string, string> validate = null)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = owner
            };

            var textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Margin = new Thickness(0, UiTokens.Space3, 0, 0)
            };

            string result = null;

            var errorText = new TextBlock
            {
                Foreground = ThemeManager.DangerBrush,
                FontSize = UiTokens.FontBody,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, UiTokens.Space2, 0, 0)
            };

            Action accept = () =>
            {
                string value = textBox.Text;
                string error = validate == null ? null : validate(value);
                if (error != null)
                {
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                    textBox.Focus();
                    return;
                }
                result = value;
                dialog.Close();
            };

            var okButton = CreateButton("Dialog_OK", "Kobold.Style.PrimaryButton");
            okButton.IsDefault = true;
            okButton.Click += (s, e) => accept();

            var cancelButton = CreateButton("Dialog_Cancel", "Kobold.Style.SecondaryButton");
            cancelButton.IsCancel = true;
            cancelButton.Click += (s, e) => dialog.Close();
            cancelButton.Margin = new Thickness(UiTokens.Space2, 0, 0, 0);

            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    accept();
                    e.Handled = true;
                }
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, UiTokens.Space4, 0, 0)
            };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            var content = new StackPanel { Margin = new Thickness(UiTokens.Space6) };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = UiTokens.FontSubtitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeManager.TextBrush
            });
            content.Children.Add(new TextBlock
            {
                Text = prompt,
                FontSize = UiTokens.FontBody,
                Foreground = ThemeManager.SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, UiTokens.Space2, 0, 0)
            });
            content.Children.Add(textBox);
            content.Children.Add(errorText);
            content.Children.Add(buttons);

            dialog.Content = new Border
            {
                Background = ThemeManager.DialogBackgroundBrush,
                BorderBrush = ThemeManager.DialogBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
                Child = content
            };

            dialog.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        private static Button CreateButton(string localizationKey, string styleKey)
        {
            var button = new Button
            {
                Content = Localization.Get(localizationKey),
                Width = 84,
                Height = 30,
                Style = Application.Current?.TryFindResource(styleKey) as Style
            };
            return button;
        }
    }
}
