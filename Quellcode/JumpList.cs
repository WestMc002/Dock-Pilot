// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DockPilot
{
    /// <summary>
    /// Sprunglisten von Windows lesen: die zuletzt mit einem Programm geöffneten Dateien, so wie sie Windows selbst
    /// für die Taskleiste führt (Recent\AutomaticDestinations\&lt;Kennung&gt;.automaticDestinations-ms).
    /// Die Kennung ist eine Prüfsumme (CRC-64) über den Programmpfad in Großbuchstaben; bekannte Ordner stehen darin
    /// als GUID. Das gilt nur für Programme, die keine eigene Kennung (AppUserModelID) setzen – für alle anderen
    /// gibt es hier nichts, der Aufrufer nimmt dann die Liste „Zuletzt verwendet“.
    /// Das Dateiformat ist nicht dokumentiert; jeder Fehler führt nur dazu, dass nichts gefunden wird.
    /// </summary>
    internal static class JumpList
    {
        private static readonly ulong[] Table = MakeTable();

        private static ulong[] MakeTable()
        {
            const ulong poly = 0x92C64265D32139A4UL;
            ulong[] t = new ulong[256];
            for (int i = 0; i < 256; i++)
            {
                ulong c = (ulong)i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>Kennung, unter der Windows die Sprungliste führt (16 Hex-Ziffern).</summary>
        internal static string AppId(string text)
        {
            byte[] data = Encoding.Unicode.GetBytes(text.ToUpperInvariant());
            ulong c = ulong.MaxValue;
            foreach (byte b in data) c = Table[(int)((c ^ b) & 0xFF)] ^ (c >> 8);
            return c.ToString("x16");
        }

        /// <summary>Mögliche Schreibweisen des Programmpfads: bekannte Ordner als GUID, sonst der Pfad selbst.</summary>
        private static List<string> Spellings(string exe)
        {
            List<string> list = new List<string>();
            string windows = Environment.GetEnvironmentVariable("windir") ?? "";
            string[,] known =
            {
                { Environment.SystemDirectory, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" },
                { windows.Length > 0 ? Path.Combine(windows, "SysWOW64") : "", "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}" },
                { Environment.GetEnvironmentVariable("ProgramW6432") ?? "", "{6D809377-6AF0-444B-8957-A3773F02200E}" },
                { Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}" },
                { Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "{6D809377-6AF0-444B-8957-A3773F02200E}" },
                { windows, "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" },
            };
            for (int i = 0; i < known.GetLength(0); i++)
            {
                string dir = known[i, 0].TrimEnd('\\');
                if (dir.Length == 0 || !exe.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                string s = known[i, 1] + exe.Substring(dir.Length);
                if (!list.Contains(s)) list.Add(s);
            }
            list.Add(exe);
            return list;
        }

        /// <summary>
        /// Zuletzt geöffnete Dateien aus der Sprungliste eines der Programme, neueste zuerst.
        /// null = keine Sprungliste gefunden.
        /// </summary>
        public static List<string> RecentFor(ICollection<string> exePaths, int max)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "AutomaticDestinations");
                if (!Directory.Exists(dir)) return null;
                foreach (string exe in exePaths)
                    foreach (string spelling in Spellings(exe))
                    {
                        string file = Path.Combine(dir, AppId(spelling) + ".automaticDestinations-ms");
                        if (!File.Exists(file)) continue;
                        List<string> found = Read(file, max);
                        if (found != null) return found;
                    }
            }
            catch { }
            return null;
        }

        private static List<string> Read(string file, int max)
        {
            // Der Explorer hält die Datei offen – deshalb eine Kopie lesen
            string copy = Path.Combine(Path.GetTempPath(), "DockPilot-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(file, copy, true);
                byte[] data = ReadStream(copy, "DestList");
                if (data == null || data.Length < 32) return null;
                int version = BitConverter.ToInt32(data, 0);
                int count = BitConverter.ToInt32(data, 4);
                // Länge eines Eintrags bis einschließlich der Längenangabe des Pfads: Windows 7/8 114 Bytes, ab Windows 10 130
                int fixedLen = version >= 2 ? 130 : 114;
                int tail = version >= 2 ? 4 : 0;
                List<KeyValuePair<long, string>> entries = new List<KeyValuePair<long, string>>();
                int pos = 32;
                for (int i = 0; i < count && pos + fixedLen <= data.Length; i++)
                {
                    long used = BitConverter.ToInt64(data, pos + 100);
                    int len = BitConverter.ToUInt16(data, pos + fixedLen - 2);
                    int start = pos + fixedLen;
                    if (len == 0 || start + len * 2 > data.Length) break;
                    entries.Add(new KeyValuePair<long, string>(used, Encoding.Unicode.GetString(data, start, len * 2)));
                    pos = start + len * 2 + tail;
                }
                entries.Sort(delegate (KeyValuePair<long, string> a, KeyValuePair<long, string> b) { return b.Key.CompareTo(a.Key); });
                List<string> result = new List<string>();
                foreach (KeyValuePair<long, string> e in entries)
                {
                    if (result.Count >= max) break;
                    string path = e.Value;
                    try
                    {
                        if (path.Length < 3 || !Path.IsPathRooted(path) || !File.Exists(path)) continue; // nur Dateien, die es noch gibt
                    }
                    catch { continue; }
                    if (!result.Contains(path)) result.Add(path);
                }
                return result;
            }
            catch { return null; }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        // ---------- Verbunddatei (OLE Structured Storage) ----------

        [ComImport, Guid("0000000b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStorage
        {
            // nur die ersten beiden Methoden werden gebraucht; die Reihenfolge muss stimmen
            [PreserveSig]
            int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out IStream stream);

            [PreserveSig]
            int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, int mode, int reserved2, out IStream stream);
        }

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int StgOpenStorage(string name, IntPtr priority, int mode, IntPtr exclude, int reserved, out IStorage storage);

        private static byte[] ReadStream(string file, string streamName)
        {
            const int STGM_READ = 0, STGM_SHARE_EXCLUSIVE = 0x10;
            IStorage storage = null;
            IStream stream = null;
            IntPtr read = IntPtr.Zero;
            try
            {
                if (StgOpenStorage(file, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out storage) != 0 || storage == null) return null;
                if (storage.OpenStream(streamName, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out stream) != 0 || stream == null) return null;
                System.Runtime.InteropServices.ComTypes.STATSTG stat;
                stream.Stat(out stat, 1); // ohne Namen
                if (stat.cbSize <= 0 || stat.cbSize > 8 * 1024 * 1024) return null;
                byte[] data = new byte[(int)stat.cbSize];
                read = Marshal.AllocHGlobal(8);
                Marshal.WriteInt32(read, 0);
                stream.Read(data, data.Length, read);
                int got = Marshal.ReadInt32(read);
                if (got < data.Length) Array.Resize(ref data, Math.Max(0, got));
                return data;
            }
            catch { return null; }
            finally
            {
                if (read != IntPtr.Zero) Marshal.FreeHGlobal(read);
                if (stream != null) Marshal.ReleaseComObject(stream);
                if (storage != null) Marshal.ReleaseComObject(storage);
            }
        }
    }
}
