using System;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Controls;
using Kobold.Core;
using Kobold.Windows;

namespace Kobold.XamlLoadCheck
{
    /// <summary>
    /// Loads the XAML windows that consume the runtime-installed theme tokens
    /// (Kobold.Radius.*, Kobold.Font.*, Kobold.Brush.*) so resource/type
    /// mismatches fail here instead of crashing the app when a user opens a
    /// window. Exit code 0 = all loaded; 1 = failures. Run via:
    /// dotnet run --project tests/XamlLoadCheck
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                Console.WriteLine("step: start");
                var app = new App();
                Console.WriteLine("step: app created");
                app.InitializeComponent(); // Application resources: App.xaml + Themes/Controls.xaml
                Console.WriteLine("step: app resources loaded");

                // Mirror App.OnStartup: WidgetManager initializes ThemeManager and
                // installs the token resources before any window is constructed.
                WidgetManager.Instance.ToString();
                Console.WriteLine("step: theme tokens installed");

                int failures = 0;
                failures += TryLoad("SettingsWindow", () => new SettingsWindow());
                failures += TryLoad("IslandWindow", () => new IslandWindow());
                failures += TryLoad("FolderWidget", () => new FolderWidget(new FolderData()));

                // The shared widget shell must construct and repaint for every
                // theme a host window can be in.
                failures += TryLoad("WidgetChrome [dark]", () => LoadChrome("dark"));
                failures += TryLoad("WidgetChrome [light]", () => LoadChrome("light"));
                failures += TryLoad("WidgetChrome [high contrast]", () => LoadChrome("high contrast"));

                // The first kind-specific host must load its chrome + empty
                // content host under every theme too.
                failures += TryLoad("TodoWidget [dark]", () => LoadTodo("dark"));
                failures += TryLoad("TodoWidget [light]", () => LoadTodo("light"));
                failures += TryLoad("TodoWidget [high contrast]", () => LoadTodo("high contrast"));

                // Regression pins for the drag-anchor defect the manual smoke
                // found (see report): a forwarded move must never move the
                // window before its start event, and the chrome must raise the
                // start while its internal drag flag is still false.
                failures += TryCheck("drag: move without start is ignored", DragMoveWithoutStartIsIgnored);
                failures += TryCheck("drag: start then move moves by the DPI-scaled delta", DragStartThenMoveMoves);
                failures += TryCheck("chrome: start event precedes the drag flag", ChromeRaisesStartBeforeDragActive);

                Console.WriteLine();
                if (failures == 0)
                {
                    Console.WriteLine("ALL XAML LOAD CHECKS PASSED");
                    return 0;
                }

                Console.WriteLine("FAILURES: " + failures);
                return 1;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.WriteLine("TOP FAIL " + ex.GetType().FullName + ": " + ex.Message);
                }
                catch
                {
                    Console.WriteLine("TOP FAIL " + ex.GetType().FullName + " (message unavailable)");
                }
                return 1;
            }
        }

        /// <summary>
        /// Exercises the shared chrome the way a host window does: theme first,
        /// then title/lock/pin, then a repaint at 80% opacity. WPF cannot toggle
        /// system high contrast, so that pass rebuilds the brushes under whatever
        /// the session reports (the palette itself is covered by HighContrastCheck).
        /// </summary>
        private static WidgetChrome LoadChrome(string theme)
        {
            if (theme == "dark") ThemeManager.Initialize("dark");
            else if (theme == "light") ThemeManager.Initialize("light");
            else ThemeManager.Initialize(ThemeManager.IsDarkTheme ? "dark" : "light");

            var chrome = new WidgetChrome
            {
                Title = "Chrome check",
                IsLocked = true,
                IsPinned = true
            };
            chrome.ApplyTheme();
            chrome.ApplyOpacity(0.8);
            return chrome;
        }

        /// <summary>
        /// Builds a Todo host the way WidgetManager does: its data first, then
        /// the window (which composes the shared chrome and its empty content
        /// host) under the requested theme.
        /// </summary>
        private static TodoWidget LoadTodo(string theme)
        {
            if (theme == "dark") ThemeManager.Initialize("dark");
            else if (theme == "light") ThemeManager.Initialize("light");
            else ThemeManager.Initialize(ThemeManager.IsDarkTheme ? "dark" : "light");

            return new TodoWidget(new TodoData());
        }

        /// <summary>
        /// A forwarded move without a start event must leave the window alone
        /// (the chrome can pump a pending move while capturing the header), and
        /// a normal start + move must still follow the cursor by the DPI-scaled
        /// delta (20/30 DIP here). The window is never shown.
        /// </summary>
        private static void DragMoveWithoutStartIsIgnored()
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);
            window.Left = 0;
            window.Top = 0;
            var moved = PrivateBaseMethod("OnHeaderDragMoved");

            // No start: even a far-away cursor must not move the window.
            moved.Invoke(window, new object[] { new Point(5000, 4000) });
            if (window.Left != 0 || window.Top != 0)
            {
                throw new InvalidOperationException(
                    "a move without a start changed Left/Top to " + window.Left + "," + window.Top);
            }
        }

        /// <summary>
        /// After a start event the same private move path must work: the window
        /// moves by the screen-pixel delta divided by its DPI scale.
        /// </summary>
        private static void DragStartThenMoveMoves()
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);
            window.Left = 0;
            window.Top = 0;
            var started = PrivateBaseMethod("OnHeaderDragStarted");
            var moved = PrivateBaseMethod("OnHeaderDragMoved");

            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            started.Invoke(window, new object[] { new Point(100, 100) });
            moved.Invoke(window, new object[] { new Point(100 + 20 * scale, 100 + 30 * scale) });

            if (Math.Abs(window.Left - 20) > 0.5 || Math.Abs(window.Top - 30) > 0.5)
            {
                throw new InvalidOperationException(
                    "start+move moved to " + window.Left + "," + window.Top +
                    " (wanted 20,30 at scale " + scale + ")");
            }
        }

        /// <summary>
        /// A window that was never shown reports neither a real DPI nor a real
        /// position; creating its handle (without showing it) gives the drag
        /// maths the monitor scale a visible panel would have.
        /// </summary>
        private static void EnsureRealDpi(Window window)
        {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }

        /// <summary>
        /// Pins the chrome's handler order: when HeaderDragStarted is raised,
        /// the internal drag flag must still be false - it may only be set
        /// after Mouse.Capture returned. Otherwise a move pumped during the
        /// capture is forwarded before the host has its anchor.
        /// </summary>
        private static void ChromeRaisesStartBeforeDragActive()
        {
            var chrome = new WidgetChrome();
            bool? dragActiveAtStart = null;
            chrome.HeaderDragStarted += _ =>
                dragActiveAtStart = PrivateField<bool>(chrome, "_isDragActive");

            var down = typeof(WidgetChrome).GetMethod("Header_MouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (down == null) throw new InvalidOperationException("Header_MouseLeftButtonDown not found");

            try
            {
                var downArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                };
                down.Invoke(chrome, new object[] { chrome, downArgs });
            }
            finally
            {
                // The handler may have taken the (test-local) capture.
                Mouse.Capture(null);
                Mouse.OverrideCursor = null;
            }

            if (dragActiveAtStart != false)
            {
                throw new InvalidOperationException(
                    "HeaderDragStarted saw _isDragActive=" +
                    (dragActiveAtStart.HasValue ? dragActiveAtStart.Value.ToString() : "never raised"));
            }
        }

        private static MethodInfo PrivateBaseMethod(string name)
        {
            var method = typeof(WidgetWindowBase).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException(name + " not found on WidgetWindowBase");
            return method;
        }

        private static T PrivateField<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException(name + " field not found");
            return (T)field.GetValue(target);
        }

        private static int TryCheck(string name, Action check)
        {
            try
            {
                check();
                Console.WriteLine("PASS " + name);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                {
                    Console.WriteLine("  INNER " + inner.GetType().FullName + ": " + inner.Message);
                }
                return 1;
            }
        }

        private static int TryLoad(string name, Func<object> create)
        {
            try
            {
                create();
                Console.WriteLine("PASS " + name + " loads");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                {
                    Console.WriteLine("  INNER " + inner.GetType().FullName + ": " + inner.Message);
                }
                return 1;
            }
        }
    }
}
