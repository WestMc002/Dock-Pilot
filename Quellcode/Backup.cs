// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later
// Sicherung: Einträge, Einstellungen und übernommene Symbole in einer Datei

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DockPilot
{
    /// <summary>
    /// Sichert „Dock Pilot.ini“, „Dock.ini“ und den Ordner „Symbole“ in eine einzige Textdatei (*.dockpilot)
    /// und spielt sie wieder ein, z. B. auf einem zweiten PC. Aufbau: Abschnitte „[[Name]]“, Inhalt jeweils Base64 in einer Zeile.
    /// Der Pfad des Symbolordners wird mitgesichert und beim Einspielen in der Dock.ini auf den neuen Ordner umgeschrieben.
    /// </summary>
    internal static class Backup
    {
        public const string Extension = ".dockpilot";
        public const string Filter = "Dock-Pilot-Sicherung (*.dockpilot)|*.dockpilot|Alle Dateien (*.*)|*.*";
        private const string Header = "; Dock Pilot Sicherung 1";
        private const string SecSettings = "Einstellungen", SecDock = "Dock", SecIconDir = "Symbolordner", SecIcon = "Symbol:";

        /// <summary>Sicherung schreiben. Rückgabe: Fehlertext oder null.</summary>
        public static string Export(string file)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(Header);
                Add(sb, SecSettings, File.Exists(Settings.DataPath) ? File.ReadAllBytes(Settings.DataPath) : new byte[0]);
                Add(sb, SecDock, File.Exists(DockStore.FilePath) ? File.ReadAllBytes(DockStore.FilePath) : new byte[0]);
                // weitere Docks (aktives Profil) – ältere Versionen überlesen diese Abschnitte
                for (int n = 2; n <= Settings.ExtraDocks + 1; n++)
                    if (DockStore.ExistsFor(n)) Add(sb, SecDock + n, File.ReadAllBytes(DockStore.FileFor(n)));
                Add(sb, SecIconDir, Encoding.UTF8.GetBytes(Settings.IconDirectory));
                if (Directory.Exists(Settings.IconDirectory))
                    foreach (string f in Directory.GetFiles(Settings.IconDirectory))
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.Length > 4 * 1024 * 1024) continue; // nur Symbole, keine großen Dateien
                        Add(sb, SecIcon + fi.Name, File.ReadAllBytes(f));
                    }
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void Add(StringBuilder sb, string name, byte[] data)
        {
            sb.AppendLine("[[" + name + "]]");
            sb.AppendLine(Convert.ToBase64String(data));
        }

        /// <summary>Ist die Datei eine Sicherung von Dock Pilot? Liest die Abschnitte.</summary>
        private static Dictionary<string, byte[]> Read(string file)
        {
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            if (lines.Length == 0 || !lines[0].StartsWith("; Dock Pilot Sicherung", StringComparison.Ordinal))
                throw new Exception("Die Datei ist keine Sicherung von Dock Pilot.");
            Dictionary<string, byte[]> parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string name = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
                {
                    name = line.Substring(2, line.Length - 4);
                    continue;
                }
                if (name == null || line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
                parts[name] = Convert.FromBase64String(line);
                name = null;
            }
            if (!parts.ContainsKey(SecSettings) || !parts.ContainsKey(SecDock))
                throw new Exception("Die Sicherung ist unvollständig (Einstellungen oder Einträge fehlen).");
            return parts;
        }

        /// <summary>
        /// Sicherung einspielen: ersetzt „Dock Pilot.ini“, „Dock.ini“ und ergänzt den Ordner „Symbole“.
        /// Die bisherigen Dateien bleiben als „.vor-import“ daneben liegen. Rückgabe: Fehlertext oder null.
        /// </summary>
        public static string Import(string file)
        {
            try
            {
                Dictionary<string, byte[]> parts = Read(file);
                Directory.CreateDirectory(Settings.DataDirectory);
                // Symbole
                foreach (KeyValuePair<string, byte[]> part in parts)
                {
                    if (!part.Key.StartsWith(SecIcon, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(part.Key.Substring(SecIcon.Length)); // nur der Dateiname, kein Pfad
                    if (string.IsNullOrEmpty(name)) continue;
                    Directory.CreateDirectory(Settings.IconDirectory);
                    File.WriteAllBytes(Path.Combine(Settings.IconDirectory, name), part.Value);
                }
                // Einträge: Pfad des Symbolordners auf diesen PC bzw. dieses Benutzerkonto umschreiben
                string dock = new UTF8Encoding(false).GetString(StripBom(parts[SecDock]));
                byte[] oldDir;
                if (parts.TryGetValue(SecIconDir, out oldDir))
                {
                    string from = Encoding.UTF8.GetString(oldDir).TrimEnd('\\');
                    string to = Settings.IconDirectory.TrimEnd('\\');
                    if (from.Length > 3 && !from.Equals(to, StringComparison.OrdinalIgnoreCase))
                        dock = ReplaceIgnoreCase(dock, from + "\\", to + "\\");
                }
                Keep(DockStore.FilePath);
                Keep(Settings.DataPath);
                Directory.CreateDirectory(DockStore.DirectoryOf(DockStore.Profile));
                File.WriteAllText(DockStore.FilePath, dock, new UTF8Encoding(false));
                // weitere Docks, falls in der Sicherung enthalten
                for (int n = 2; n <= Settings.ExtraDocks + 1; n++)
                {
                    byte[] more;
                    if (!parts.TryGetValue(SecDock + n, out more)) continue;
                    string text = new UTF8Encoding(false).GetString(StripBom(more));
                    if (parts.TryGetValue(SecIconDir, out oldDir))
                    {
                        string from = Encoding.UTF8.GetString(oldDir).TrimEnd('\\');
                        string to = Settings.IconDirectory.TrimEnd('\\');
                        if (from.Length > 3 && !from.Equals(to, StringComparison.OrdinalIgnoreCase))
                            text = ReplaceIgnoreCase(text, from + "\\", to + "\\");
                    }
                    Keep(DockStore.FileFor(n));
                    File.WriteAllText(DockStore.FileFor(n), text, new UTF8Encoding(false));
                }
                File.WriteAllBytes(Settings.DataPath, parts[SecSettings]);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void Keep(string path)
        {
            try { if (File.Exists(path)) File.Copy(path, path + ".vor-import", true); } catch { }
        }

        private static byte[] StripBom(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                byte[] rest = new byte[data.Length - 3];
                Array.Copy(data, 3, rest, 0, rest.Length);
                return rest;
            }
            return data;
        }

        private static string ReplaceIgnoreCase(string text, string from, string to)
        {
            StringBuilder sb = new StringBuilder();
            int pos = 0;
            while (true)
            {
                int i = text.IndexOf(from, pos, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                sb.Append(text, pos, i - pos);
                sb.Append(to);
                pos = i + from.Length;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }
    }
}