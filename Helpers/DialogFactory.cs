using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

        /// <summary>
        /// Modal new-file dialog (style 1): a name box plus an editable
        /// extension selector, a live "will create" preview whose suffix wears
        /// the widget colour, and the shared errorText pattern of ShowInput.
        /// Returns the composed file name, or null when the user cancels.
        /// validate receives the composed name; its message keeps the dialog
        /// open.
        /// </summary>
        public static string ShowNewFile(Window owner, string title, string nameLabel, string extensionLabel,
            string previewFormat, string initialName, string initialExtension,
            IReadOnlyList<string> extensions, string widgetColor, Func<string, string> validate)
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

            var nameBox = new TextBox { Text = initialName ?? string.Empty };
            var combo = new ComboBox
            {
                IsEditable = true,
                Text = initialExtension ?? string.Empty,
                ItemsSource = extensions
            };
            combo.ToolTip = Localization.Get("Dialog_ExtCustomHint");

            var nameStack = new StackPanel { Margin = new Thickness(0, 0, UiTokens.Space2, 0) };
            nameStack.Children.Add(CreateFieldLabel(nameLabel));
            nameBox.Margin = new Thickness(0, UiTokens.Space1, 0, 0);
            nameStack.Children.Add(nameBox);

            var extensionStack = new StackPanel();
            extensionStack.Children.Add(CreateFieldLabel(extensionLabel));
            combo.Margin = new Thickness(0, UiTokens.Space1, 0, 0);
            extensionStack.Children.Add(combo);

            var fields = new Grid { Margin = new Thickness(0, UiTokens.Space2, 0, 0) };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            Grid.SetColumn(nameStack, 0);
            Grid.SetColumn(extensionStack, 1);
            fields.Children.Add(nameStack);
            fields.Children.Add(extensionStack);

            // The preview format wraps the composed name ("Will create: {0}");
            // the text around the placeholder stays muted, the name is primary
            // and the suffix wears the widget colour.
            string prefix = previewFormat ?? string.Empty;
            string suffix = string.Empty;
            int placeholder = prefix.IndexOf("{0}", StringComparison.Ordinal);
            if (placeholder >= 0)
            {
                suffix = prefix.Substring(placeholder + 3);
                prefix = prefix.Substring(0, placeholder);
            }

            var previewNameRun = new Run { Foreground = ThemeManager.TextBrush };
            var previewExtensionRun = new Run();
            var preview = new TextBlock
            {
                FontSize = UiTokens.FontBody,
                Foreground = ThemeManager.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, UiTokens.Space3, 0, 0)
            };
            preview.Inlines.Add(new Run(prefix) { Foreground = ThemeManager.MutedTextBrush });
            preview.Inlines.Add(previewNameRun);
            preview.Inlines.Add(previewExtensionRun);
            if (suffix.Length > 0)
            {
                preview.Inlines.Add(new Run(suffix) { Foreground = ThemeManager.MutedTextBrush });
            }

            string result = null;
            Brush suffixBrush = ThemeManager.AccentBlueBrush;

            Action refresh = () =>
            {
                string ext = NewFileName.NormalizeExtension(combo.Text);
                if (ext == null)
                {
                    // Invalid characters: echo the typed text for the preview;
                    // the error is reported when the user accepts. The composed
                    // name holds the stem only, so no suffix split is possible.
                    previewNameRun.Text = NewFileName.Compose(nameBox.Text, "");
                    previewExtensionRun.Text = combo.Text ?? string.Empty;
                }
                else
                {
                    string full = NewFileName.Compose(nameBox.Text, ext);
                    string extensionText = ext.Length > 0 && full.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(full.Length - ext.Length)
                        : string.Empty;
                    previewNameRun.Text = full.Substring(0, full.Length - extensionText.Length);
                    previewExtensionRun.Text = extensionText;
                }
                previewExtensionRun.Foreground = suffixBrush;
            };

            var errorText = new TextBlock
            {
                Foreground = ThemeManager.DangerBrush,
                FontSize = UiTokens.FontBody,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, UiTokens.Space2, 0, 0)
            };

            Action<string> showError = message =>
            {
                errorText.Text = message;
                errorText.Visibility = Visibility.Visible;
            };

            Action accept = () =>
            {
                string ext = NewFileName.NormalizeExtension(combo.Text);
                if (ext == null)
                {
                    showError(Localization.Get("Dialog_NameInvalid"));
                    combo.Focus();
                    return;
                }

                string full = NewFileName.Compose(nameBox.Text, ext);
                if (full.Length == 0)
                {
                    showError(Localization.Get("Dialog_NameInvalid"));
                    nameBox.Focus();
                    return;
                }

                string error = validate == null ? null : validate(full);
                if (error != null)
                {
                    showError(error);
                    nameBox.Focus();
                    return;
                }

                result = full;
                dialog.Close();
            };

            var okButton = CreateButton("Dialog_OK", "Kobold.Style.PrimaryButton");
            okButton.IsDefault = true;
            okButton.Click += (s, e) => accept();

            var cancelButton = CreateButton("Dialog_Cancel", "Kobold.Style.SecondaryButton");
            cancelButton.IsCancel = true;
            cancelButton.Click += (s, e) => dialog.Close();
            cancelButton.Margin = new Thickness(UiTokens.Space2, 0, 0, 0);

            nameBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    accept();
                    e.Handled = true;
                }
            };
            combo.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    accept();
                    e.Handled = true;
                }
            };
            nameBox.TextChanged += (s, e) => refresh();
            // ComboBox has no CLR TextChanged event; the editable host's
            // TextChanged bubbles up, so AddHandler is the supported hook.
            combo.AddHandler(TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => refresh()));
            combo.SelectionChanged += (s, e) => refresh();

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
            content.Children.Add(fields);
            content.Children.Add(preview);
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

            suffixBrush = ApplyWidgetColor(dialog, widgetColor);
            refresh();

            dialog.Loaded += (s, e) => { nameBox.Focus(); nameBox.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        private static TextBlock CreateFieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = UiTokens.FontCaption,
                Foreground = ThemeManager.SecondaryTextBrush
            };
        }

        /// <summary>
        /// Style 1 follow-the-widget colour: override the dialog's accent,
        /// hover and on-accent resources (frozen brushes) and tint its border
        /// 25% with the widget colour. High contrast or a missing colour keeps
        /// the theme accent and the default border. Returns the brush for the
        /// preview's extension suffix.
        /// </summary>
        private static Brush ApplyWidgetColor(Window dialog, string widgetColor)
        {
            if (ThemeManager.IsHighContrast || string.IsNullOrEmpty(widgetColor))
            {
                return ThemeManager.AccentBlueBrush;
            }

            Color tint = Utils.HexToColor(widgetColor);
            double luminance = (0.299 * tint.R + 0.587 * tint.G + 0.114 * tint.B) / 255.0;
            Color onAccent = luminance > 0.6 ? (Color)ColorConverter.ConvertFromString("#1F1F1F") : Colors.White;

            var accentBrush = new SolidColorBrush(tint);
            accentBrush.Freeze();
            var accentHoverBrush = new SolidColorBrush(Utils.DarkenColor(tint, 0.2));
            accentHoverBrush.Freeze();
            var onAccentBrush = new SolidColorBrush(onAccent);
            onAccentBrush.Freeze();

            dialog.Resources[ThemeManager.BrushKeyPrefix + "Accent"] = accentBrush;
            dialog.Resources[ThemeManager.BrushKeyPrefix + "AccentHover"] = accentHoverBrush;
            dialog.Resources[ThemeManager.BrushKeyPrefix + "OnAccent"] = onAccentBrush;

            var border = dialog.Content as Border;
            if (border != null)
            {
                var borderBrush = new SolidColorBrush(
                    Utils.MixColor(ThemeManager.DialogBorderBrush.Color, tint, 0.25));
                borderBrush.Freeze();
                border.BorderBrush = borderBrush;
            }

            return accentBrush;
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
