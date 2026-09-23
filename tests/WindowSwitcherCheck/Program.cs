using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Kobold.Controls;
using Kobold.Core;

namespace Kobold.WindowSwitcherCheck
{
    /// <summary>
    /// Checks that the app's top-level windows are kept out of Alt+Tab / Task View
    /// by a hidden tool-window owner, while the windows themselves stay normal
    /// (activatable) windows. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/WindowSwitcherCheck
    /// </summary>
    internal static class Program
    {
        private const int GwlExStyle = -20;
        private const uint GwOwner = 4;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExAppWindow = 0x00040000;
        private const int WsExNoActivate = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

        private static readonly List<string> Errors = new List<string>();

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                var app = new App();
                app.InitializeComponent();
                WidgetManager.Instance.ToString(); // installs the theme tokens like App.OnStartup

                WidgetsAreExcludedByAHiddenToolWindowOwner();
                OwnersAreNotShared();
                IslandIsExcludedToo();
                ClosingReleasesTheOwner();
            }
            catch (Exception ex)
            {
                Console.WriteLine("TOP FAIL " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL WINDOW-SWITCHER CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static IntPtr HandleOf(System.Windows.Window window)
        {
            return new WindowInteropHelper(window).EnsureHandle();
        }

        private static IntPtr OwnerOf(IntPtr handle)
        {
            return GetWindow(handle, GwOwner);
        }

        private static void WidgetsAreExcludedByAHiddenToolWindowOwner()
        {
            var widget = new FolderWidget(new FolderData());
            IntPtr handle = HandleOf(widget);
            IntPtr owner = OwnerOf(handle);

            Check(owner != IntPtr.Zero, "widget: the window has a hidden owner");
            Check(owner != IntPtr.Zero && (GetWindowLong(owner, GwlExStyle) & WsExToolWindow) != 0,
                "widget: the owner is a tool window, so the switcher skips the pair");
            Check(owner != IntPtr.Zero && !IsWindowVisible(owner),
                "widget: the owner is never visible");

            int exStyle = GetWindowLong(handle, GwlExStyle);
            Check((exStyle & WsExToolWindow) == 0,
                "widget: the window itself is not a tool window (stays activatable)");
            Check((exStyle & WsExNoActivate) == 0,
                "widget: the window itself is not no-activate (panels need focus)");
            Check((exStyle & WsExAppWindow) == 0,
                "widget: the window does not force a taskbar entry");

            widget.Close();
        }

        private static void OwnersAreNotShared()
        {
            var first = new FolderWidget(new FolderData());
            var second = new FolderWidget(new FolderData());
            IntPtr firstOwner = OwnerOf(HandleOf(first));
            IntPtr secondOwner = OwnerOf(HandleOf(second));

            Check(firstOwner != IntPtr.Zero && secondOwner != IntPtr.Zero && firstOwner != secondOwner,
                "owners: two widgets never share one hidden owner");

            first.Close();
            second.Close();
        }

        private static void IslandIsExcludedToo()
        {
            var island = new IslandWindow();
            IntPtr owner = OwnerOf(HandleOf(island));

            Check(owner != IntPtr.Zero && (GetWindowLong(owner, GwlExStyle) & WsExToolWindow) != 0,
                "island: the owner is a tool window, so the switcher skips it");

            island.Close();
        }

        private static void ClosingReleasesTheOwner()
        {
            var widget = new FolderWidget(new FolderData());
            IntPtr handle = HandleOf(widget);
            IntPtr owner = OwnerOf(handle);
            if (owner == IntPtr.Zero)
            {
                Errors.Add("release: the widget has no hidden owner to release");
                return;
            }

            widget.Close();

            Check(!IsWindow(owner), "release: closing the widget destroys its hidden owner");
        }
    }
}
