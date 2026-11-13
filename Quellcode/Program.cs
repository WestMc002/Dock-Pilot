// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DockPilot
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Screenshots für GitHub: eigener Lauf ohne Dock, Tray-Symbol und Einzelinstanz-Sperre
            if (args.Length > 0 && string.Equals(args[0], "--screenshots", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string dir = args.Length > 1 ? args[1] : Path.Combine(AppInfo.ExeDirectory, "Screenshots");
                return Demo.RunScreenshots(dir) ? 0 : 1;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "DockPilot_SingleInstance", out created))
            {
                // Neustart durch das Update-Skript: warten, bis sich die alte Instanz beendet hat
                bool afterUpdate = Array.Exists(args, a => string.Equals(a, "--nach-update", StringComparison.OrdinalIgnoreCase));
                if (!created && afterUpdate)
                {
                    try { created = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    // Läuft schon – dort das Dock einblenden
                    try
                    {
                        EventWaitHandle ev = EventWaitHandle.OpenExisting(TrayContext.ShowSettingsEventName);
                        ev.Set();
                        ev.Dispose();
                    }
                    catch { }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    Log(e.Exception);
                    MessageBox.Show("Unerwarteter Fehler:\n\n" + e.Exception.Message + "\n\nDetails stehen in Fehler.log im Datenordner.",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log(e.ExceptionObject as Exception);
                };
                Application.Run(new TrayContext(afterUpdate));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Technische Details gehören ins Protokoll, nicht in die Meldung.</summary>
        internal static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Settings.DataDirectory);
                File.AppendAllText(Path.Combine(Settings.DataDirectory, "Fehler.log"),
                    DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " · " + AppInfo.Version + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }
}
