using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kobold.Helpers
{
    /// <summary>
    /// Keeps a window out of Alt+Tab / Task View without giving the window itself a
    /// tool-window style: the window is handed a hidden, never-shown tool-window
    /// owner. Owned windows are skipped by the switcher, while the window keeps its
    /// normal, activatable character - a window with WS_EX_TOOLWINDOW would also be
    /// skipped when Windows picks the next window to activate.
    /// Ported from PaperTodo's WindowNative (ApplyWindowSwitcherVisibility).
    /// </summary>
    public static class WindowSwitcher
    {
        private const int GwlExStyle = -20;
        private const int GwlpHwndParent = -8;
        private const int WsExAppWindow = 0x00040000;
        private const int WsExToolWindow = 0x00000080;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoOwnerZOrder = 0x0200;

        // One owner per window: windows sharing an owner become one native window
        // group, so activating one could raise the others as well.
        private static readonly Dictionary<IntPtr, IntPtr> Owners = new Dictionary<IntPtr, IntPtr>();

        /// <summary>
        /// Gives the window a hidden tool-window owner. Safe to call twice; the
        /// window keeps the owner it already has.
        /// </summary>
        public static void HideFromSwitcher(Window window)
        {
            IntPtr handle = HandleOf(window);
            if (handle == IntPtr.Zero) return;

            IntPtr owner;
            if (!Owners.TryGetValue(handle, out owner) || !IsWindow(owner))
            {
                owner = CreateHiddenOwner();
                if (owner == IntPtr.Zero) return;
                Owners[handle] = owner;
            }

            SetWindowLongPtr(handle, GwlpHwndParent, owner);

            // WPF marks some windows as app windows; an owned window must not carry
            // that flag or the shell keeps an entry for it anyway.
            int exStyle = GetWindowLong(handle, GwlExStyle);
            int cleaned = exStyle & ~WsExAppWindow;
            if (cleaned != exStyle) SetWindowLong(handle, GwlExStyle, cleaned);

            // Commit the owner change without moving, resizing or activating.
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate |
                SwpFrameChanged | SwpNoOwnerZOrder);
        }

        /// <summary>Destroys the hidden owner this window was given, if any.</summary>
        public static void ReleaseOwner(Window window)
        {
            IntPtr handle = HandleOf(window);
            if (handle == IntPtr.Zero) return;

            IntPtr owner;
            if (!Owners.TryGetValue(handle, out owner)) return;
            Owners.Remove(handle);

            if (owner != IntPtr.Zero && IsWindow(owner)) DestroyWindow(owner);
        }

        /// <summary>The hidden owner this window was given, or zero.</summary>
        public static IntPtr HiddenOwnerOf(Window window)
        {
            IntPtr handle = HandleOf(window);
            if (handle == IntPtr.Zero) return IntPtr.Zero;

            IntPtr owner;
            return Owners.TryGetValue(handle, out owner) ? owner : IntPtr.Zero;
        }

        private static IntPtr HandleOf(Window window)
        {
            if (window == null) return IntPtr.Zero;

            try { return new WindowInteropHelper(window).Handle; }
            catch (Exception) { return IntPtr.Zero; }
        }

        private static IntPtr CreateHiddenOwner()
        {
            // A 0x0 "Static" window parked off-screen. It is never shown, so it has
            // no presence of its own; its only job is to be the window's owner.
            return CreateWindowEx(WsExToolWindow, "Static", "", 0, -100, -100, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, index, value)
                : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
    }
}
