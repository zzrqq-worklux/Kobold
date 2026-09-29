using System;
using System.Text;
using System.Windows;
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
