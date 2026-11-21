// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>
    /// Autostart über HKCU\...\Run, immer mit normalen Benutzerrechten. Einen Autostart mit
    /// Administratorrechten (Aufgabenplanung) gibt es bewusst nicht: Sonst liefe das ganze Dock und
    /// jedes daraus gestartete Programm mit Administratorrechten. Für einzelne Programme gibt es
    /// „Als Administrator“ in den Eigenschaften des Eintrags.
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Dock Pilot";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        /// <summary>Läuft dieses Programm gerade mit Administratorrechten? (Anzeige im Info-Fenster)</summary>
        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>Startet Dock Pilot (diese EXE) mit Windows?</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (key == null) return false;
                        string v = key.GetValue(ValueName) as string;
                        return v != null && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>Autostart einrichten bzw. entfernen. Rückgabe false, wenn das nicht geklappt hat.</summary>
        public static bool Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(ValueName, Command);
                    else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ alte Aufgabe (bis 1.62 im Code)

        /// <summary>Name der früheren Aufgabe für den Autostart mit Administratorrechten.</summary>
        private const string OldTaskName = "Dock Pilot";

        private static string Schtasks
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); }
        }

        /// <summary>
        /// Gibt es noch die frühere Aufgabe „Dock Pilot“ in der Aufgabenplanung? Rückgabe: ihr Ziel (Programm), "" wenn
        /// das Ziel nicht lesbar ist, null wenn es die Aufgabe nicht gibt. Dauert etwas – nicht im UI-Thread aufrufen.
        /// </summary>
        public static string OldTaskCommand()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Schtasks, "/Query /TN \"" + OldTaskName + "\" /XML");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string xml = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000) || p.ExitCode != 0) return null;
                    Match m = Regex.Match(xml, "<Command>(.*?)</Command>", RegexOptions.Singleline);
                    if (!m.Success) return "";
                    SecurityElement e = SecurityElement.FromString("<c>" + m.Groups[1].Value + "</c>");
                    return (e != null ? e.Text : m.Groups[1].Value).Trim().Trim('"');
                }
            }
            catch { return null; }
        }

        /// <summary>Zeigt das Ziel auf ein Dock Pilot (diese oder eine andere Kopie)?</summary>
        public static bool IsDockPilot(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            try { return string.Equals(Path.GetFileName(Environment.ExpandEnvironmentVariables(command)), "Dock Pilot.exe", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Frühere Aufgabe entfernen (fragt nach Administratorrechten). Rückgabe true = entfernt.</summary>
        public static bool RemoveOldTask()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Schtasks, "/Delete /TN \"" + OldTaskName + "\" /F");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    if (p == null || !p.WaitForExit(20000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception) { return false; } // Windows-Nachfrage abgebrochen
            catch { return false; }
        }

        /// <summary>
        /// Nach dem Verschieben des Programmordners: Zeigt der Autostart-Eintrag auf eine EXE, die es
        /// nicht mehr gibt, wird er auf den aktuellen Pfad umgestellt. Ein Eintrag auf eine vorhandene
        /// andere EXE (z. B. eine zweite Kopie) bleibt unverändert.
        /// </summary>
        public static void UpdateMovedPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    string v = key.GetValue(ValueName) as string;
                    if (v == null || string.Equals(v, Command, StringComparison.OrdinalIgnoreCase)) return;
                    string old = v.Trim().Trim('"');
                    if (!File.Exists(old)) key.SetValue(ValueName, Command);
                }
            }
            catch { }
        }
    }
}
