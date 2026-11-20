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

        // Grenzen beim Einlesen: Eine Sicherung enthält nur kleine Textdateien und Symbole (beim Sichern höchstens 4 MB je Symbol)
        private const long MaxFile = 64L * 1024 * 1024;     // ganze Datei
        private const int MaxParts = 5000;                  // Abschnitte
        private const int MaxText = 2 * 1024 * 1024;        // Einstellungen bzw. Einträge eines Docks
        private const int MaxIcon = 4 * 1024 * 1024;        // ein Symbol
        private const long MaxTotal = 64L * 1024 * 1024;    // alle Inhalte zusammen
        private static readonly string[] IconTypes = { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".icl" };

        /// <summary>
        /// Sicherung lesen und vollständig prüfen, ohne etwas zu ändern. Wirft eine Ausnahme mit verständlichem Text,
        /// wenn die Datei keine (gültige) Sicherung ist oder zu groß.
        /// </summary>
        public static BackupContent Open(string file)
        {
            FileInfo fi = new FileInfo(file);
            if (!fi.Exists) throw new Exception("Die Datei wurde nicht gefunden.");
            if (fi.Length > MaxFile) throw new Exception("Die Datei ist zu groß für eine Sicherung von Dock Pilot.");
            Dictionary<string, byte[]> parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            using (StreamReader reader = new StreamReader(file, Encoding.UTF8))
            {
                string first = reader.ReadLine();
                if (first == null || !first.StartsWith("; Dock Pilot Sicherung", StringComparison.Ordinal))
                    throw new Exception("Die Datei ist keine Sicherung von Dock Pilot.");
                string name = null, raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
                    {
                        name = line.Substring(2, line.Length - 4);
                        continue;
                    }
                    if (name == null || line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
                    bool icon = name.StartsWith(SecIcon, StringComparison.OrdinalIgnoreCase);
                    int limit = icon ? MaxIcon : MaxText;
                    if ((long)line.Length * 3 / 4 > limit + 4)
                        throw new Exception("Die Sicherung enthält einen zu großen Abschnitt („" + Short(name) + "“).");
                    byte[] data;
                    try { data = Convert.FromBase64String(line); }
                    catch { throw new Exception("Die Sicherung ist beschädigt (Abschnitt „" + Short(name) + "“)."); }
                    total += data.Length;
                    if (total > MaxTotal) throw new Exception("Die Sicherung ist zu groß.");
                    parts[name] = data;
                    if (parts.Count > MaxParts) throw new Exception("Die Sicherung enthält zu viele Abschnitte.");
                    name = null;
                }
            }
            if (!parts.ContainsKey(SecSettings) || !parts.ContainsKey(SecDock))
                throw new Exception("Die Sicherung ist unvollständig (Einstellungen oder Einträge fehlen).");

            BackupContent c = new BackupContent();
            c.SettingsData = parts[SecSettings];
            // globales Tastenkürzel des Docks (Tastatursteuerung) aus den Einstellungen der Sicherung
            Dictionary<string, string> flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in new UTF8Encoding(false).GetString(StripBom(c.SettingsData)).Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith(";", StringComparison.Ordinal)) flat[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            c.DockHotkeyModifiers = Ini.GetInt(flat, "TastenkuerzelZusatz", 0, 0, 15);
            c.DockHotkeyKey = Ini.GetInt(flat, "TastenkuerzelTaste", 0, 0, 254);
            // Symbole: nur Bilddateien mit einfachem Dateinamen, nichts anderes landet im Symbolordner
            foreach (KeyValuePair<string, byte[]> part in parts)
            {
                if (!part.Key.StartsWith(SecIcon, StringComparison.OrdinalIgnoreCase)) continue;
                string iconName = part.Key.Substring(SecIcon.Length).Trim();
                bool ok = iconName.Length > 0 && iconName.Length <= 120 && iconName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                          iconName.Trim('.').Length > 0 && Array.IndexOf(IconTypes, Path.GetExtension(iconName).ToLowerInvariant()) >= 0;
                if (ok) c.Icons.Add(new KeyValuePair<string, byte[]>(iconName, part.Value));
                else c.SkippedIcons++;
            }
            // Einträge: Pfad des Symbolordners auf diesen PC bzw. dieses Benutzerkonto umschreiben, dann einlesen
            byte[] oldDir;
            string from = parts.TryGetValue(SecIconDir, out oldDir) ? Encoding.UTF8.GetString(oldDir).TrimEnd('\\') : "";
            string to = Settings.IconDirectory.TrimEnd('\\');
            for (int n = 1; n <= Settings.ExtraDocks + 1; n++)
            {
                byte[] data;
                if (!parts.TryGetValue(n == 1 ? SecDock : SecDock + n, out data)) continue;
                string text = new UTF8Encoding(false).GetString(StripBom(data));
                if (from.Length > 3 && !from.Equals(to, StringComparison.OrdinalIgnoreCase))
                    text = ReplaceIgnoreCase(text, from + "\\", to + "\\");
                List<DockItem> items = DockStore.ParseItems(Ini.ParseSections(text.Split('\n')));
                c.Docks.Add(new KeyValuePair<int, List<DockItem>>(n, items));
            }
            return c;
        }

        /// <summary>
        /// Verweist der Pfad (nach Auflösen der Umgebungsvariablen) auf einen anderen Rechner? Schon das Laden des
        /// Symbols würde ihn ansprechen, und Windows meldet sich dabei unter Umständen mit dem eigenen Konto an.
        /// Erkannt werden \\Server\Freigabe, \\?\UNC\…, //Server/… und file://Server/…; verbundene Laufwerke
        /// (z. B. Z:) gelten als lokal.
        /// </summary>
        public static bool IsNetwork(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string s;
            try { s = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')); }
            catch { s = path.Trim(); }
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal))
                return s.Substring(4).StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
            if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal) ||
                s.StartsWith(@"\/", StringComparison.Ordinal) || s.StartsWith(@"/\", StringComparison.Ordinal))
                return true;
            if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (!Uri.TryCreate(s, UriKind.Absolute, out uri)) return true; // unklar: lieber als Netzwerk behandeln
                return uri.IsUnc || (uri.Host.Length > 0 && !uri.IsLoopback);
            }
            return false;
        }

        /// <summary>Verwendet der Eintrag einen Netzwerkpfad (Ziel, Ordner, „Ausführen in“, mit <paramref name="icon"/> auch das Symbol)?</summary>
        public static bool UsesNetwork(DockItem it, bool icon)
        {
            if (it.IsSeparator) return false;
            if (IsNetwork(it.Kind == ItemKind.Ordnermenue ? it.Folder : it.Command) || IsNetwork(it.WorkingDirectory)) return true;
            return icon && IsNetwork(IconLoader.SplitSpecRaw(it.Icon));
        }

        private static string Short(string name)
        {
            return name.Length > 40 ? name.Substring(0, 40) + "…" : name;
        }

        /// <summary>
        /// Geprüfte Sicherung einspielen: ersetzt „Dock Pilot.ini“ und die Einträge der Docks und ergänzt den Ordner
        /// „Symbole“. Ohne <paramref name="withAdmin"/> bzw. <paramref name="withHotkeys"/> kommen die Einträge ohne
        /// „Als Administrator“ bzw. ohne Tastenkürzel; ohne <paramref name="withNetwork"/> fehlen Einträge mit
        /// Netzwerkpfaden (ein Symbol von einem Netzwerkpfad wird nur entfernt). Alles wird erst vollständig in einen Hilfsordner geschrieben;
        /// die bisherigen Einstellungen und Einträge bleiben als „.vor-import“ daneben liegen. Geht beim Ersetzen etwas
        /// schief, kommt der bisherige Stand zurück. Rückgabe: Fehlertext oder null.
        /// </summary>
        public static string Import(BackupContent c, bool withAdmin, bool withHotkeys, bool withNetwork)
        {
            string stage = Path.Combine(Settings.DataDirectory, "Wiederherstellen-" + Guid.NewGuid().ToString("N"));
            List<string[]> plan = new List<string[]>();   // { Hilfsdatei, Ziel, Sicherung des alten Stands oder null }
            List<string[]> done = new List<string[]>();
            try
            {
                // 1. alles in den Hilfsordner schreiben (hier scheitert es am ehesten, z. B. bei vollem Datenträger)
                Directory.CreateDirectory(stage);
                int no = 0;
                foreach (KeyValuePair<string, byte[]> icon in c.Icons)
                {
                    string staged = Path.Combine(stage, "s" + (++no));
                    File.WriteAllBytes(staged, icon.Value);
                    plan.Add(new[] { staged, Path.Combine(Settings.IconDirectory, icon.Key), null });
                }
                foreach (KeyValuePair<int, List<DockItem>> dock in c.Docks)
                {
                    List<DockItem> items = new List<DockItem>();
                    foreach (DockItem it in dock.Value)
                    {
                        if (!withNetwork && UsesNetwork(it, false)) continue;
                        DockItem copy = it.Clone();
                        if (!withNetwork && IsNetwork(IconLoader.SplitSpecRaw(copy.Icon))) copy.Icon = "";
                        if (!withAdmin) copy.RunAsAdmin = false;
                        if (!withHotkeys) { copy.HotkeyKey = 0; copy.HotkeyModifiers = 0; }
                        items.Add(copy);
                    }
                    string staged = Path.Combine(stage, "d" + dock.Key);
                    File.WriteAllText(staged, DockStore.Serialize(items), new UTF8Encoding(false));
                    plan.Add(new[] { staged, DockStore.FileFor(dock.Key), null });
                }
                string settings = Path.Combine(stage, "e");
                File.WriteAllBytes(settings, c.SettingsData);
                plan.Add(new[] { settings, Settings.DataPath, null }); // zuletzt

                // 2. bisherigen Stand sichern – klappt das nicht, wird nichts ersetzt
                no = 0;
                foreach (string[] step in plan)
                {
                    if (!File.Exists(step[1])) continue;
                    bool icon = step[1].StartsWith(Settings.IconDirectory, StringComparison.OrdinalIgnoreCase);
                    // Symbole: alter Stand im Hilfsordner; Einstellungen und Einträge als „.vor-import“ daneben
                    step[2] = icon ? Path.Combine(stage, "alt" + (++no)) : step[1] + ".vor-import";
                    File.Copy(step[1], step[2], true);
                }

                // 3. ersetzen
                Directory.CreateDirectory(Settings.IconDirectory);
                Directory.CreateDirectory(DockStore.DirectoryOf(DockStore.Profile));
                foreach (string[] step in plan)
                {
                    done.Add(step);
                    File.Copy(step[0], step[1], true);
                }
            }
            catch (Exception ex)
            {
                // bisherigen Stand zurückholen: ersetzte Dateien zurückkopieren, neu angelegte entfernen
                bool restored = true;
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (done[i][2] != null) File.Copy(done[i][2], done[i][1], true);
                        else if (File.Exists(done[i][1])) File.Delete(done[i][1]);
                    }
                    catch { restored = false; }
                }
                if (restored) TryDelete(stage);
                return ex.Message + (done.Count == 0 ? "" : restored
                    ? "\n\nDer bisherige Stand wurde wiederhergestellt."
                    : "\n\nDer bisherige Stand konnte nicht vollständig wiederhergestellt werden. Die alten Dateien liegen in:\n" + stage);
            }
            TryDelete(stage);
            return null;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
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

    /// <summary>Geprüfter Inhalt einer Sicherung (noch nicht eingespielt).</summary>
    internal sealed class BackupContent
    {
        public byte[] SettingsData;
        /// <summary>Einträge je Dock (1 = Hauptdock), Pfade schon auf diesen PC umgeschrieben.</summary>
        public readonly List<KeyValuePair<int, List<DockItem>>> Docks = new List<KeyValuePair<int, List<DockItem>>>();
        public readonly List<KeyValuePair<string, byte[]>> Icons = new List<KeyValuePair<string, byte[]>>();
        /// <summary>Globales Tastenkürzel des Docks in der Sicherung (0 = keins); nur mit „Tastenkürzel übernehmen“.</summary>
        public int DockHotkeyModifiers, DockHotkeyKey;
        /// <summary>Abschnitte mit Symbolen, die nicht übernommen werden (kein Bild oder ungültiger Name).</summary>
        public int SkippedIcons;

        public int Count(Func<DockItem, bool> test)
        {
            int n = 0;
            foreach (KeyValuePair<int, List<DockItem>> dock in Docks)
                foreach (DockItem it in dock.Value)
                    if (test(it)) n++;
            return n;
        }

        public int EntryCount { get { return Count(delegate (DockItem it) { return !it.IsSeparator; }); } }
        public int AdminCount { get { return Count(delegate (DockItem it) { return !it.IsSeparator && it.Kind == ItemKind.Programm && it.RunAsAdmin; }); } }
        public int HotkeyCount { get { return Count(delegate (DockItem it) { return !it.IsSeparator && it.HotkeyKey > 0; }); } }
        public int NetworkCount { get { return Count(delegate (DockItem it) { return Backup.UsesNetwork(it, true); }); } }
    }
}
