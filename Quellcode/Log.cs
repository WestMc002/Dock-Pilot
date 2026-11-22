// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;

namespace DockPilot
{
    /// <summary>
    /// Fehlerprotokoll Fehler.log im Datenordner (wie Wake on LAN). Schreibt nur bei Fehlern,
    /// wirft selbst nie eine Ausnahme und benennt die Datei ab 256 KB in Fehler.alt.log um.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 256 * 1024;

        public static string FilePath
        {
            get { return Path.Combine(Settings.DataDirectory, "Fehler.log"); }
        }

        public static void Write(string area, string text)
        {
            if (Demo.Active) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.DataDirectory);
                    string path = FilePath;
                    try
                    {
                        // ab 256 KB nicht einlesen und kürzen, sondern in Fehler.alt.log umbenennen
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = Path.Combine(Settings.DataDirectory, "Fehler.alt.log");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                    }
                    catch { }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + AppInfo.Version + "  " + area + ": " +
                        (text ?? "").Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ');
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static void Write(string area, Exception ex)
        {
            if (ex == null) return;
            Write(area, ex.GetType().Name + ": " + ex.Message + " | " + (ex.StackTrace ?? "").Trim());
        }
    }
}