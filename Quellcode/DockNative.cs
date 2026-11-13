// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DockPilot
{
    /// <summary>Windows-Funktionen für das Dock: Symbole, Prozesse, Fenster, Tastenkürzel.</summary>
    internal static class DockNative
    {
        // ------------------------------------------------------------------ Fensterstile

        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const int MOD_NOREPEAT = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOMOVE_NOSIZE_NOACTIVATE = 0x0001 | 0x0002 | 0x0010;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ------------------------------------------------------------------ Fenster anderer Programme

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        // ---------- Papierkorb ----------

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SHQUERYRBINFO32 { public int cbSize; public long i64Size; public long i64NumItems; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO64 { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        private static extern int SHQueryRecycleBin32(string root, ref SHQUERYRBINFO32 info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        private static extern int SHQueryRecycleBin64(string root, ref SHQUERYRBINFO64 info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, int flags);

        /// <summary>Anzahl der Elemente im Papierkorb (alle Laufwerke); -1 = nicht feststellbar.</summary>
        public static long RecycleBinItems()
        {
            try
            {
                // die Struktur ist unter 32 Bit dicht gepackt (20 Bytes), unter 64 Bit nicht (24 Bytes)
                if (IntPtr.Size == 8)
                {
                    SHQUERYRBINFO64 info = new SHQUERYRBINFO64();
                    info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO64));
                    return SHQueryRecycleBin64(null, ref info) == 0 ? info.i64NumItems : -1;
                }
                else
                {
                    SHQUERYRBINFO32 info = new SHQUERYRBINFO32();
                    info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO32));
                    return SHQueryRecycleBin32(null, ref info) == 0 ? info.i64NumItems : -1;
                }
            }
            catch { return -1; }
        }

        /// <summary>Papierkorb leeren – Windows fragt selbst nach.</summary>
        public static void EmptyRecycleBin(IntPtr owner)
        {
            try { SHEmptyRecycleBin(owner, null, 0); } catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);

        /// <summary>Datei in den Papierkorb legen (ohne Rückfrage, wiederherstellbar). true = erledigt.</summary>
        public static bool Recycle(string path)
        {
            try
            {
                const uint FO_DELETE = 3;
                const ushort flags = 0x0004 | 0x0010 | 0x0040 | 0x0400; // SILENT | NOCONFIRMATION | ALLOWUNDO | NOERRORUI
                string from = path + "\0\0"; // Liste, doppelt abgeschlossen
                // die Struktur ist unter 32 Bit dicht gepackt, unter 64 Bit nicht
                if (IntPtr.Size == 8)
                {
                    SHFILEOPSTRUCT64 op = new SHFILEOPSTRUCT64();
                    op.wFunc = FO_DELETE; op.pFrom = from; op.fFlags = flags;
                    return SHFileOperation64(ref op) == 0 && !op.fAnyOperationsAborted;
                }
                else
                {
                    SHFILEOPSTRUCT32 op = new SHFILEOPSTRUCT32();
                    op.wFunc = FO_DELETE; op.pFrom = from; op.fFlags = flags;
                    return SHFileOperation32(ref op) == 0 && !op.fAnyOperationsAborted;
                }
            }
            catch { return false; }
        }

        // ---------- Dateizuordnung ----------

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
        private static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder buffer, ref uint length);

        /// <summary>Programm (EXE), das Dateien mit dieser Endung öffnet; leer, wenn keins zugeordnet ist.</summary>
        public static string AssociatedExe(string extension)
        {
            try
            {
                uint n = 1024;
                StringBuilder sb = new StringBuilder((int)n);
                return AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, extension, null, sb, ref n) == 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>Titel eines Fensters (leer, wenn es keinen hat).</summary>
        public static string WindowTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 2);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int DWMWA_CLOAKED = 14;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static readonly Dictionary<uint, string> _pathCache = new Dictionary<uint, string>();

        /// <summary>Vollständiger EXE-Pfad eines Prozesses (klappt auch bei Programmen mit Adminrechten).</summary>
        public static string ProcessPath(uint pid)
        {
            string path;
            if (_pathCache.TryGetValue(pid, out path)) return path;
            path = null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            _pathCache[pid] = path;
            return path;
        }

        /// <summary>Sichtbare Hauptfenster (wie in der Taskleiste) je EXE-Pfad, Pfad in Kleinbuchstaben.</summary>
        public static Dictionary<string, List<IntPtr>> TaskWindows()
        {
            Dictionary<string, List<IntPtr>> result = new Dictionary<string, List<IntPtr>>();
            HashSet<uint> seen = new HashSet<uint>();
            uint own = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr shell = GetShellWindow();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || h == shell) return true; // der Desktop („Program Manager“) ist kein Explorer-Fenster
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(h, out pid);
                if (pid == own) return true;
                seen.Add(pid);
                string path = ProcessPath(pid);
                if (string.IsNullOrEmpty(path)) return true;
                string key = path.ToLowerInvariant();
                List<IntPtr> list;
                if (!result.TryGetValue(key, out list)) { list = new List<IntPtr>(); result[key] = list; }
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            // Prozess-IDs werden wiederverwendet: nur Einträge laufender Fenster behalten
            List<uint> stale = new List<uint>();
            foreach (uint pid in _pathCache.Keys) if (!seen.Contains(pid)) stale.Add(pid);
            foreach (uint pid in stale) _pathCache.Remove(pid);
            return result;
        }

        // ---------- Meldungen der Taskleiste (blinkende Fenster) ----------

        public const int HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWACTIVATED = 4, HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006;

        [DllImport("user32.dll")]
        private static extern bool RegisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        /// <summary>
        /// Das Fenster bekommt ab jetzt die Meldungen, die auch die Taskleiste erhält (kein globaler Hook).
        /// Gibt die Nummer der Meldung zurück, 0 = nicht möglich.
        /// </summary>
        public static int RegisterShellHook(IntPtr hwnd)
        {
            try { return RegisterShellHookWindow(hwnd) ? (int)RegisterWindowMessage("SHELLHOOK") : 0; }
            catch { return 0; }
        }

        public static void UnregisterShellHook(IntPtr hwnd)
        {
            try { DeregisterShellHookWindow(hwnd); } catch { }
        }

        /// <summary>Fenster in den Vordergrund (ohne Wiederherstellen), z. B. Menüs des Docks.</summary>
        public static void BringToFront(IntPtr hwnd)
        {
            SetForegroundWindow(hwnd);
        }

        /// <summary>Fenster nach vorne holen (minimierte wiederherstellen).</summary>
        public static void Activate(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        public static bool IsMinimized(IntPtr hwnd)
        {
            return IsIconic(hwnd);
        }

        /// <summary>
        /// Läuft im Vordergrund ein Programm im Vollbild auf diesem Bildschirm (Spiel, Video, Präsentation)?
        /// Der Desktop selbst zählt nicht.
        /// </summary>
        public static bool FullscreenOn(Rectangle monitorBounds)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            StringBuilder cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
            NativeMethods.RECT r;
            if (!NativeMethods.GetWindowRect(fg, out r)) return false;
            Rectangle wr = r.ToRectangle();
            return wr.Left <= monitorBounds.Left && wr.Top <= monitorBounds.Top &&
                   wr.Right >= monitorBounds.Right && wr.Bottom >= monitorBounds.Bottom;
        }

        // ------------------------------------------------------------------ Symbole

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits,
            ref NativeMethods.BITMAPINFOHEADER lpbi, uint uUsage);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_PIDL = 0x8;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int SHIL_LARGE = 0;
        private const int SHIL_EXTRALARGE = 2;
        private const int SHIL_JUMBO = 4;
        private const int ILD_TRANSPARENT = 1;

        /// <summary>Symbol aus EXE/DLL/ICO; index &lt; 0 = Ressourcen-Nummer (wie "imageres.dll,-109").</summary>
        public static Bitmap ExtractIcon(string file, int index, int size)
        {
            IntPtr[] icons = new IntPtr[1];
            int[] ids = new int[1];
            uint n = PrivateExtractIcons(file, index, size, size, icons, ids, 1, 0);
            if (n == 0 || n == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;
            try { return HIconToBitmap(icons[0]); }
            finally { DestroyIcon(icons[0]); }
        }

        /// <summary>Symbol, wie es der Explorer zeigt (Programme, Verknüpfungen, Ordner, Netzwerkpfade, Systemordner).</summary>
        public static Bitmap ShellIcon(string path, bool large)
        {
            return ShellIcon(path, large, 0);
        }

        /// <summary><paramref name="want"/> &gt; 0: gewünschte Größe für Menüs – bis 32 px aus der 32er-Liste (schneller, schärfer).</summary>
        public static Bitmap ShellIcon(string path, bool large, int want)
        {
            int index = -1;
            SHFILEINFO sfi = new SHFILEINFO();
            uint cb = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
            if (path.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                // Webadresse: Symbol des Standardbrowsers
                if (SHGetFileInfo(".html", FILE_ATTRIBUTE_NORMAL, ref sfi, cb, SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero) index = sfi.iIcon;
            }
            else
            {
                // vorhandene Dateien und Ordner direkt (schnell, nutzt den Symbol-Cache von Windows)
                bool local = false;
                try { local = !path.StartsWith("\\\\") && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)); } catch { }
                if (local && SHGetFileInfo(path, 0, ref sfi, cb, SHGFI_SYSICONINDEX) != IntPtr.Zero) index = sfi.iIcon;
                IntPtr pidl;
                uint attrs;
                if (index < 0 && SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) == 0 && pidl != IntPtr.Zero)
                {
                    try
                    {
                        if (SHGetFileInfoPidl(pidl, 0, ref sfi, cb, SHGFI_SYSICONINDEX | SHGFI_PIDL) != IntPtr.Zero) index = sfi.iIcon;
                    }
                    finally { CoTaskMemFree(pidl); }
                }
                if (index < 0)
                {
                    // Ziel (noch) nicht erreichbar: Symbol nach Dateityp
                    bool folder = path.EndsWith("\\") || path.IndexOf('.', Math.Max(0, path.LastIndexOf('\\'))) < 0;
                    if (SHGetFileInfo(path, folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref sfi, cb,
                        SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero) index = sfi.iIcon;
                }
            }
            if (index < 0) return null;

            if (large)
            {
                Bitmap jumbo = FromImageList(SHIL_JUMBO, index);
                if (jumbo != null)
                {
                    // Programme ohne großes Symbol liefern ein kleines Bild in der Ecke – dann lieber 48 px
                    Rectangle ink = InkBounds(jumbo);
                    if (ink.Width > 64 || ink.Height > 64) return jumbo;
                    jumbo.Dispose();
                }
            }
            if (!large && want > 0 && want <= 32) return FromImageList(SHIL_LARGE, index);
            return FromImageList(SHIL_EXTRALARGE, index);
        }

        private static Bitmap FromImageList(int list, int index)
        {
            Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            IImageList il;
            if (SHGetImageList(list, ref iid, out il) != 0 || il == null) return null;
            try
            {
                IntPtr h = IntPtr.Zero;
                if (il.GetIcon(index, ILD_TRANSPARENT, ref h) != 0 || h == IntPtr.Zero) return null;
                try { return HIconToBitmap(h); }
                finally { DestroyIcon(h); }
            }
            finally { Marshal.ReleaseComObject(il); }
        }

        /// <summary>HICON verlustfrei mit Alphakanal in ein Bitmap umwandeln.</summary>
        public static Bitmap HIconToBitmap(IntPtr hIcon)
        {
            ICONINFO ii;
            if (!GetIconInfo(hIcon, out ii)) return null;
            try
            {
                if (ii.hbmColor != IntPtr.Zero)
                {
                    BITMAP bm;
                    GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm);
                    int w = bm.bmWidth, h = bm.bmHeight;
                    if (w > 0 && h > 0 && bm.bmBitsPixel == 32)
                    {
                        Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        BitmapData data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        bool hasAlpha = false;
                        try
                        {
                            NativeMethods.BITMAPINFOHEADER bi = new NativeMethods.BITMAPINFOHEADER();
                            bi.biSize = 40;
                            bi.biWidth = w;
                            bi.biHeight = -h; // von oben nach unten
                            bi.biPlanes = 1;
                            bi.biBitCount = 32;
                            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
                            GetDIBits(hdc, ii.hbmColor, 0, (uint)h, data.Scan0, ref bi, 0);
                            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
                            int[] px = new int[w * h];
                            Marshal.Copy(data.Scan0, px, 0, px.Length);
                            for (int i = 0; i < px.Length; i++) if ((px[i] & unchecked((int)0xFF000000)) != 0) { hasAlpha = true; break; }
                        }
                        finally { result.UnlockBits(data); }
                        if (hasAlpha) return result;
                        result.Dispose();
                    }
                }
                // ältere Symbole ohne Alphakanal: Maske verwenden
                using (Icon ic = Icon.FromHandle(hIcon)) return ic.ToBitmap();
            }
            catch { return null; }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            }
        }

        /// <summary>Rechteck um alle sichtbaren Pixel.</summary>
        public static Rectangle InkBounds(Bitmap bmp)
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] px = new int[bmp.Width * bmp.Height];
            try { Marshal.Copy(data.Scan0, px, 0, px.Length); }
            finally { bmp.UnlockBits(data); }
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * bmp.Width;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (((px[row + x] >> 24) & 0xFF) > 16)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        /// <summary>Pfad aus einer ITEMIDLIST (z. B. aus der KkMenus.ini von RocketDock, als Hex-Text gespeichert).</summary>
        public static string PathFromPidlHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
            byte[] bytes = new byte[hex.Length / 2];
            try
            {
                for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            catch { return null; }
            IntPtr mem = Marshal.AllocCoTaskMem(bytes.Length + 2);
            try
            {
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                Marshal.WriteInt16(mem, bytes.Length, 0); // Abschluss der Liste
                StringBuilder sb = new StringBuilder(1024);
                if (SHGetPathFromIDListW(mem, sb)) return sb.ToString();
            }
            catch { }
            finally { Marshal.FreeCoTaskMem(mem); }
            return null;
        }

        // ------------------------------------------------------------------ Symbol auswählen (Windows-Dialog)

        [DllImport("shell32.dll", EntryPoint = "#62", CharSet = CharSet.Unicode)]
        private static extern int PickIconDlg(IntPtr hwnd, StringBuilder pszIconPath, int cchIconPath, ref int piIconIndex);

        /// <summary>Windows-Dialog "Anderes Symbol". Rückgabe "Datei?Index" oder null.</summary>
        public static string PickIcon(IntPtr owner, string file, int index)
        {
            StringBuilder sb = new StringBuilder(file ?? "", 1024);
            int i = index;
            if (PickIconDlg(owner, sb, sb.Capacity, ref i) == 0) return null;
            string path = Environment.ExpandEnvironmentVariables(sb.ToString());
            return path + "?" + i;
        }
    }
}
