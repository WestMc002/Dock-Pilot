// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>
    /// Autostart: normal über HKCU\...\Run, mit Administratorrechten über eine Aufgabe in der
    /// Aufgabenplanung ("Mit höchsten Privilegien ausführen"). Nur so startet ein Programm mit
    /// Adminrechten ohne UAC-Nachfrage bei jeder Anmeldung.
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Dock Pilot";
        public const string TaskName = "Dock Pilot";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        /// <summary>Läuft dieses Programm gerade mit Administratorrechten?</summary>
        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        public static bool RunKeyEnabled
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

        /// <summary>Gibt es die Aufgabe "Dock Pilot" in der Aufgabenplanung?</summary>
        public static bool TaskExists
        {
            get
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    using (Process p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(5000)) return false;
                        return p.ExitCode == 0;
                    }
                }
                catch { return false; }
            }
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

        public static bool IsEnabled
        {
            get { return RunKeyEnabled || TaskExists; }
        }

        /// <summary>
        /// Autostart einrichten bzw. entfernen. Für die Aufgabe mit höchsten Rechten fragt Windows
        /// einmal per UAC nach. Rückgabe false, wenn das nicht geklappt hat (z. B. Nachfrage abgebrochen).
        /// </summary>
        public static bool Set(bool enabled, bool asAdmin)
        {
            bool ok = true;
            bool taskExists = TaskExists;

            if (enabled && asAdmin)
            {
                ok = CreateTask();
                if (ok) SetRunKey(false);
            }
            else
            {
                if (taskExists) ok = RunElevated("/Delete /TN \"" + TaskName + "\" /F");
                SetRunKey(enabled);
            }
            return ok;
        }

        private static void SetRunKey(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(ValueName, Command);
                    else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }

        private static bool CreateTask()
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string exe = Application.ExecutablePath;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Author>Günther Schoder</Author>\r\n" +
                "    <Description>Dock Pilot bei der Anmeldung mit Administratorrechten starten</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <UserId>" + SecurityElement.Escape(user) + "</UserId>\r\n" +
                "      <Delay>PT5S</Delay>\r\n" +
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <UserId>" + SecurityElement.Escape(user) + "</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>" + SecurityElement.Escape(exe) + "</Command>\r\n" +
                "      <WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>";
            string file = Path.Combine(Path.GetTempPath(), "DockPilot_Autostart.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                return RunElevated("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F");
            }
            catch { return false; }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>schtasks mit Adminrechten ausführen (UAC-Nachfrage, falls nötig).</summary>
        private static bool RunElevated(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                if (IsElevated)
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                }
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(20000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception) { return false; } // UAC-Nachfrage abgebrochen
            catch { return false; }
        }

        /// <summary>Dock Pilot mit Adminrechten neu starten. true = neuer Prozess wurde gestartet.</summary>
        public static bool RestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "/neustart");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }
    }
}
