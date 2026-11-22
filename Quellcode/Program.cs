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
        /// <summary>Versuchskennung aus „--nach-update &lt;Kennung&gt;“ (ab 1.71), sonst null.</summary>
        internal static string UpdateAttempt;

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
                // ab 1.71 folgt die Versuchskennung (32 Hex-Zeichen): nur das Ergebnis genau dieses Versuchs lesen
                int at = Array.FindIndex(args, a => string.Equals(a, "--nach-update", StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && at + 1 < args.Length && Updater.IsAttemptId(args[at + 1])) UpdateAttempt = args[at + 1];
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
                // /tray: nach dem Update nur im Infobereich starten (Einstellungsfenster war vorher zu)
                bool tray = Array.Exists(args, a => string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));
                Application.Run(new TrayContext(afterUpdate, tray));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Technische Details gehören ins Protokoll, nicht in die Meldung.</summary>
        internal static void Log(Exception ex)
        {
            // über das gemeinsame Fehlerprotokoll (ab 256 KB wird es in Fehler.alt.log umbenannt)
            DockPilot.Log.Write("Fehler", ex);
        }
    }
}
