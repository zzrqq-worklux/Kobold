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
