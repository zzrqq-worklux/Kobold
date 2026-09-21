using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kobold.Services
{
    /// <summary>
    /// Hosts the real shell context menu (item and folder background) in-process.
    /// Every failure is silent by design: the fallback is simply "no menu".
    /// </summary>
    public static class ShellContextMenuService
    {
        private const int WM_DRAWITEM = 0x002B;
        private const int WM_MEASUREITEM = 0x002C;
        private const int WM_INITMENUPOPUP = 0x0117;

        private const uint CMF_NORMAL = 0x00000000;
        private const uint CMF_EXTENDEDVERBS = 0x00000100;
        private const uint TPM_RETURNCMD = 0x0100;

        private const uint CMIC_MASK_UNICODE = 0x00004000;
        private const uint CMIC_MASK_PTINVOKE = 0x20000000;
        private const int SW_SHOWNORMAL = 1;

        private const uint ID_FIRST = 1;
        private const uint ID_LAST = 0x7FFF;

        private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IID_IShellFolder = new Guid("000214e6-0000-0000-c000-000000000046");
        private static readonly Guid IID_IContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");
        private static readonly Guid BHID_SFObject = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
        private static readonly Guid BHID_SFUIObject = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");

        private static IContextMenu3 _activeMenu3;
        private static IContextMenu2 _activeMenu2;
        private static bool _sessionActive;

        public static bool ShowForItem(IntPtr ownerHwnd, string path, int xPx, int yPx, bool extendedVerbs = false)
        {
            object item = null;
            object handler = null;
            try
            {
                item = CreateShellItem(path);
                if (item == null) return false;

                handler = BindToHandler(item, BHID_SFUIObject, IID_IContextMenu);
                var menu = handler as IContextMenu;
                if (menu == null)
                {
                    Debug.WriteLine("[Kobold] Shell menu: no IContextMenu for " + path);
                    return false;
                }
                return Show(menu, ownerHwnd, xPx, yPx, extendedVerbs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell item menu failed: " + ex.Message);
                return false;
            }
            finally
            {
                // Release eagerly: the shell objects are big and their RCWs are
                // tiny, so the GC alone would let repeated menu opens pile up
                // unmanaged memory, worker threads and handles.
                ReleaseComObject(handler);
                ReleaseComObject(item);
            }
        }

        public static bool ShowForFolderBackground(IntPtr ownerHwnd, string folder, int xPx, int yPx, bool extendedVerbs = false)
        {
            object item = null;
            object shellFolderObject = null;
            object menuObject = null;
            try
            {
                item = CreateShellItem(folder);
                if (item == null) return false;

                shellFolderObject = BindToHandler(item, BHID_SFObject, IID_IShellFolder);
                var shellFolder = shellFolderObject as IShellFolder;
                if (shellFolder == null)
                {
                    Debug.WriteLine("[Kobold] Shell menu: no IShellFolder for " + folder);
                    return false;
                }

                // CreateViewObject on the folder itself is the documented way to
                // get Explorer's background menu (Directory\Background verbs).
                IntPtr ptr;
                Guid iid = IID_IContextMenu;
                int hr = shellFolder.CreateViewObject(ownerHwnd, ref iid, out ptr);
                if (hr != 0 || ptr == IntPtr.Zero)
                {
                    Debug.WriteLine("[Kobold] Shell menu: CreateViewObject failed 0x" + hr.ToString("X8"));
                    return false;
                }

                try { menuObject = Marshal.GetObjectForIUnknown(ptr); }
                finally { Marshal.Release(ptr); }

                var menu = menuObject as IContextMenu;
                if (menu == null) return false;
                return Show(menu, ownerHwnd, xPx, yPx, extendedVerbs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell background menu failed: " + ex.Message);
                return false;
            }
            finally
            {
                ReleaseComObject(menuObject);
                ReleaseComObject(shellFolderObject);
                ReleaseComObject(item);
            }
        }

        /// <summary>Drops every reference the RCW holds on the unmanaged object.</summary>
        private static void ReleaseComObject(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell menu: release failed: " + ex.Message);
            }
        }

        /// <summary>Forwards menu messages to the active IContextMenu2/3 session.</summary>
        public static bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (!_sessionActive) return false;
            if (msg != WM_DRAWITEM && msg != WM_MEASUREITEM && msg != WM_INITMENUPOPUP) return false;

            try
            {
                if (_activeMenu3 != null)
                {
                    IntPtr lr;
                    if (_activeMenu3.HandleMenuMsg2(msg, wParam, lParam, out lr) == 0)
                    {
                        result = lr;
                        return true;
                    }
                }
                else if (_activeMenu2 != null && _activeMenu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell menu message failed: " + ex.Message);
            }
            return false;
        }

        private static bool Show(IContextMenu menu, IntPtr ownerHwnd, int xPx, int yPx, bool extendedVerbs)
        {
            IntPtr hMenu = IntPtr.Zero;
            try
            {
                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;

                uint flags = CMF_NORMAL | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
                int hr = menu.QueryContextMenu(hMenu, 0, ID_FIRST, ID_LAST, flags);
                if (hr < 0)
                {
                    Debug.WriteLine("[Kobold] Shell menu: QueryContextMenu failed 0x" + hr.ToString("X8"));
                    return false;
                }
                if (GetMenuItemCount(hMenu) <= 0) return false;

                _activeMenu3 = menu as IContextMenu3;
                _activeMenu2 = _activeMenu3 == null ? menu as IContextMenu2 : null;
                _sessionActive = true;
                try
                {
                    SetForegroundWindow(ownerHwnd);
                    uint command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD, xPx, yPx, ownerHwnd, IntPtr.Zero);
                    if (command == 0) return false;

                    Invoke(menu, command - ID_FIRST, xPx, yPx, ownerHwnd);
                    return true;
                }
                finally
                {
                    _sessionActive = false;
                    _activeMenu3 = null;
                    _activeMenu2 = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell menu failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            }
        }

        private static void Invoke(IContextMenu menu, uint commandOffset, int xPx, int yPx, IntPtr ownerHwnd)
        {
            int size = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = size,
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = ownerHwnd,
                    lpVerb = new IntPtr(commandOffset),
                    lpVerbW = new IntPtr(commandOffset),
                    nShow = SW_SHOWNORMAL,
                    ptInvoke = new POINT { x = xPx, y = yPx }
                };
                Marshal.StructureToPtr(info, buffer, false);
                menu.InvokeCommand(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static object CreateShellItem(string path)
        {
            object item;
            Guid iid = IID_IShellItem;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (hr != 0 || item == null)
            {
                Debug.WriteLine("[Kobold] Shell menu: cannot create item for " + path + " (0x" + hr.ToString("X8") + ")");
                return null;
            }
            return item;
        }

        private static object BindToHandler(object shellItem, Guid handler, Guid iid)
        {
            IntPtr ptr;
            int hr = ((IShellItem)shellItem).BindToHandler(IntPtr.Zero, ref handler, ref iid, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("000214e6-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid,
                IntPtr rgfReserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(int uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
