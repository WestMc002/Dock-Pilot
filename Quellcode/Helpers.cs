// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>Ziel, Parameter und Symbol einer Verknüpfung (.lnk) lesen – über WScript.Shell, ohne zusätzliche Verweise.</summary>
    internal sealed class ShortcutInfo
    {
        public string Target = "", Arguments = "", WorkingDirectory = "", IconFile = "";
        public int IconIndex;

        public static ShortcutInfo Read(string path)
        {
            object shell = null, link = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type lt = link.GetType();
                ShortcutInfo info = new ShortcutInfo();
                info.Target = (lt.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string) ?? "";
                info.Arguments = (lt.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string) ?? "";
                info.WorkingDirectory = (lt.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null) as string) ?? "";
                string icon = (lt.InvokeMember("IconLocation", BindingFlags.GetProperty, null, link, null) as string) ?? "";
                // Format "Datei,Index"; ",0" ohne Datei = Symbol des Ziels
                int comma = icon.LastIndexOf(',');
                if (comma >= 0)
                {
                    int.TryParse(icon.Substring(comma + 1).Trim(), out info.IconIndex);
                    icon = icon.Substring(0, comma);
                }
                info.IconFile = Environment.ExpandEnvironmentVariables(icon.Trim());
                if (info.IconFile.Length > 0 && !File.Exists(info.IconFile)) info.IconFile = "";
                return info;
            }
            catch { return null; }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
    }

    /// <summary>Moderner Windows-Ordnerdialog (IFileOpenDialog, Vorlage: Wake on LAN Quellcode\Dialogs.cs).</summary>
    internal static class FolderPicker
    {
        public static string Pick(IWin32Window owner, string title, string initial = null)
        {
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogCom();
                try
                {
                    uint opts;
                    dlg.GetOptions(out opts);
                    dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                    if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                    if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
                    {
                        IShellItem folder;
                        Guid iid = typeof(IShellItem).GUID;
                        if (SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out folder) == 0) dlg.SetFolder(folder);
                    }
                    int hr = dlg.Show(owner != null ? owner.Handle : IntPtr.Zero);
                    if (hr != 0) return null; // abgebrochen
                    IShellItem item;
                    dlg.GetResult(out item);
                    string path;
                    item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    return path;
                }
                finally { Marshal.ReleaseComObject(dlg); }
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            using (var old = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(initial)) old.SelectedPath = initial;
                return old.ShowDialog(owner) == DialogResult.OK ? old.SelectedPath : null;
            }
        }

        private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
