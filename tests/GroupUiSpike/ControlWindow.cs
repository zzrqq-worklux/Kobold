using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.GroupUiSpike
{
    /// <summary>
    /// T0.5 control window: a small always-on-top panel that drives the spike
    /// group window (Show / Hide / Topmost / Pin / Lock / sizes) and reads its
    /// state. It stands in for the production island/menu wiring, so the spike
    /// does not have to touch production code.
    /// </summary>
    internal sealed class ControlWindow : Window
    {
        private readonly ISpikeGroupWindow _group;
        private readonly TextBlock _readout;
        private readonly TextBlock _hint;

        internal ControlWindow(ISpikeGroupWindow group)
        {
            _group = group;

            Title = "组控制 (T0)";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 244;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = ThemeManager.UiFontFamily;

            var shell = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.BorderBrush,
                Background = ThemeManager.BackgroundBrush,
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                Padding = new Thickness(12),
                Effect = null
            };
            Content = shell;

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "组件组 · 控制",
                FontSize = UiTokens.FontSubtitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeManager.TextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(NewButton("显示 (Show)", () =>
            {
                _group.Window.Show();
                _group.Window.Activate();
                RefreshReadout();
            }));
            panel.Children.Add(NewButton("隐藏 (Hide)", () =>
            {
                _group.Window.Hide();
                RefreshReadout();
            }));
            panel.Children.Add(NewButton("切换置顶 (Topmost)", () =>
            {
                _group.ToggleTopmost();
                RefreshReadout();
            }));
            panel.Children.Add(NewButton("切换固定 (Pin)", () =>
            {
                _group.TogglePin();
                RefreshReadout();
            }));
            panel.Children.Add(NewButton("切换锁定 (Lock)", () =>
            {
                _group.ToggleLock();
                RefreshReadout();
            }));

            var sizes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            sizes.Children.Add(NewSizeButton("700×500", 700, 500));
            sizes.Children.Add(NewSizeButton("760×540", 760, 540));
            sizes.Children.Add(NewSizeButton("820×600", 820, 600));
            panel.Children.Add(sizes);

            _readout = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = UiTokens.FontCaption,
                Foreground = ThemeManager.SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(_readout);

            _hint = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = UiTokens.FontCaption,
                Foreground = ThemeManager.MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Text = "手动项：IME 候选位置、切换闪烁、右键菜单。Ctrl+Z / Esc / 外部点击提交。\n" +
                       "笔记标签：标题右侧的 ▾ 打开笔记标题菜单（Esc 关闭）。\n" +
                       "未固定时点击本窗口会收起组（与独立组件一致），先点“固定”。"
            };
            panel.Children.Add(_hint);

            panel.Children.Add(NewButton("关闭 (Close)", Close));

            shell.Child = panel;

            // A light poll keeps the readout honest without a group-state event.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) => RefreshReadout();
            timer.Start();
            Closed += (s, e) => timer.Stop();

            RefreshReadout();
        }

        private void RefreshReadout()
        {
            Window group = _group.Window;
            _readout.Text =
                "可见: " + (group.IsVisible ? "是" : "否") +
                " · 置顶: " + (group.Topmost ? "是" : "否") + "\n" +
                "固定: " + (_group.IsPinned ? "是" : "否") +
                " · 锁定: " + (_group.IsLocked ? "是" : "否") + "\n" +
                "标签: " + (_group.IsActiveTodo ? "待办" : "笔记") +
                " · 尺寸: " + Math.Round(group.Width) + "×" + Math.Round(group.Height);
        }

        private Button NewButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ThemeManager.ButtonSecondaryBrush,
                BorderBrush = ThemeManager.BorderBrush,
                Foreground = ThemeManager.TextBrush,
                Cursor = Cursors.Hand
            };
            button.Click += (s, e) => action();
            return button;
        }

        private Button NewSizeButton(string text, double width, double height)
        {
            var button = NewButton(text, () =>
            {
                _group.TryResize(width, height);
                _group.ApplyContentSize();
                RefreshReadout();
            });
            button.Width = 64;
            button.Padding = new Thickness(0, 4, 0, 4);
            button.Margin = new Thickness(0, 0, 4, 4);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            return button;
        }
    }
}
