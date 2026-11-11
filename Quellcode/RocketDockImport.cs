// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Übernimmt Einträge und Einstellungen aus RocketDock 1.3.5: Settings.ini (Einträge, Trennlinien, Größen, Verhalten),
    /// Docklets\Settings.ini und Docklets\KkMenu\KkMenus.ini (Ordnermenüs). Eigene Symbole (PNG/ICO aus dem
    /// RocketDock-Ordner) werden nach %LOCALAPPDATA%\Dock Pilot\Symbole kopiert.
    /// </summary>
    internal static class RocketDockImport
    {
        internal sealed class Result
        {
            public List<DockItem> Items = new List<DockItem>();
            public int Programs, Separators, Menus, IconsCopied;
            public List<string> Notes = new List<string>();
        }

        /// <summary>Settings.ini einer RocketDock-Installation an den üblichen Orten.</summary>
        public static string Find()
        {
            string[] roots =
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };
            foreach (string r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string p = Path.Combine(Path.Combine(r, "RocketDock"), "Settings.ini");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>Settings.ini auswählen lassen (Startordner: gefundene Installation).</summary>
        public static string Ask(IWin32Window owner)
        {
            string found = Find();
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Settings.ini von RocketDock auswählen";
                dlg.Filter = "RocketDock-Einstellungen (Settings.ini)|Settings.ini|INI-Dateien|*.ini";
                if (found != null)
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(found);
                    dlg.FileName = "Settings.ini";
                }
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
        }

        public static Result Import(string settingsIni, Settings target)
        {
            Result res = new Result();
            string root = Path.GetDirectoryName(settingsIni);
            Encoding ansi = Encoding.Default; // RocketDock schreibt in der Windows-Codepage (BOM wird erkannt)
            List<KeyValuePair<string, Dictionary<string, string>>> sections = Ini.ReadSections(settingsIni, ansi);
            if (sections == null) throw new Exception("Die Datei konnte nicht gelesen werden.");

            Dictionary<string, string> general = null, icons = null;
            foreach (KeyValuePair<string, Dictionary<string, string>> s in sections)
            {
                if (s.Key.Equals("Software\\RocketDock", StringComparison.OrdinalIgnoreCase)) general = s.Value;
                else if (s.Key.Equals("Software\\RocketDock\\Icons", StringComparison.OrdinalIgnoreCase)) icons = s.Value;
            }
            if (icons == null) throw new Exception("In der Datei stehen keine RocketDock-Einträge (Abschnitt [Software\\RocketDock\\Icons] fehlt).\n\n" +
                "Speichert RocketDock seine Einstellungen in der Registry, bitte in RocketDock unter Einstellungen › Allgemein " +
                "„Einstellungen in der Settings.ini speichern“ wählen.");

            // Ordnermenüs (KkMenu): Nummer des Menüs je Docklet und die Menüs selbst
            Dictionary<int, Dictionary<string, string>> docklets = new Dictionary<int, Dictionary<string, string>>();
            List<KeyValuePair<string, Dictionary<string, string>>> ds = Ini.ReadSections(Path.Combine(root, "Docklets\\Settings.ini"), ansi);
            if (ds != null)
                foreach (KeyValuePair<string, Dictionary<string, string>> s in ds)
                {
                    int n;
                    if (s.Key.StartsWith("Docklet", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.Key.Substring(7), out n)) docklets[n] = s.Value;
                }
            List<string> menus = new List<string>();
            List<KeyValuePair<string, Dictionary<string, string>>> ms = Ini.ReadSections(Path.Combine(root, "Docklets\\KkMenu\\KkMenus.ini"), ansi);
            if (ms != null) foreach (KeyValuePair<string, Dictionary<string, string>> s in ms) menus.Add(s.Key);

            int count = Ini.GetInt(icons, "count", 0, 0, 10000);
            if (count == 0)
                for (int i = 0; i < 10000 && icons.ContainsKey(i + "-Title"); i++) count = i + 1;

            for (int i = 0; i < count; i++)
            {
                string pre = i + "-";
                if (!icons.ContainsKey(pre + "Title") && !icons.ContainsKey(pre + "IsSeparator")) continue;
                if (Ini.GetBool(icons, pre + "IsSeparator", false))
                {
                    res.Items.Add(new DockItem { Kind = ItemKind.Trennlinie });
                    res.Separators++;
                    continue;
                }
                string title = Ini.Get(icons, pre + "Title", "");
                string docklet = Ini.Get(icons, pre + "DockletFile", "");
                if (docklet.Length > 0)
                {
                    DockItem menu = FolderMenu(title, docklet, i, docklets, menus, root, res);
                    if (menu != null) { res.Items.Add(menu); res.Menus++; }
                    continue;
                }
                DockItem it = new DockItem();
                it.Title = title;
                it.Command = Ini.Get(icons, pre + "Command", "");
                it.Arguments = Ini.Get(icons, pre + "Arguments", "").Replace("<QUOTE>", "\"");
                it.WorkingDirectory = Ini.Get(icons, pre + "WorkingDirectory", "");
                it.ShowCmd = Ini.GetInt(icons, pre + "ShowCmd", 0, 0, 2);
                it.Icon = IconSpec(Ini.Get(icons, pre + "FileName", ""), it.Command, root, res);
                res.Items.Add(it);
                res.Programs++;
            }

            if (general != null && target != null) ApplySettings(general, target, res);
            return res;
        }

        private static DockItem FolderMenu(string title, string docklet, int index, Dictionary<int, Dictionary<string, string>> docklets,
            List<string> menus, string root, Result res)
        {
            Dictionary<string, string> d;
            if (docklet.IndexOf("kkmenu", StringComparison.OrdinalIgnoreCase) < 0 || !docklets.TryGetValue(index, out d))
            {
                res.Notes.Add("„" + title + "“ (Docklet " + Path.GetFileName(docklet) + ") wurde übersprungen.");
                return null;
            }
            int mi = Ini.GetInt(d, "KkMenuIndex", -1, -1, 10000);
            if (mi < 0 || mi >= menus.Count)
            {
                res.Notes.Add("Ordnermenü „" + title + "“: Menü nicht gefunden, übersprungen.");
                return null;
            }
            // Abschnitt: Name*KKPIDL::<Hex>**Sort By Name*Hide Hidden*Show Files Only
            string[] parts = menus[mi].Split('*');
            string name = parts[0];
            string folder = null;
            foreach (string p in parts)
                if (p.StartsWith("KKPIDL::", StringComparison.OrdinalIgnoreCase)) folder = DockNative.PathFromPidlHex(p.Substring(8));
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(Path.Combine(root, "Verknüpfungen"), name);
            DockItem it = new DockItem();
            it.Kind = ItemKind.Ordnermenue;
            it.Title = title.Length > 0 ? title : name;
            it.Folder = folder;
            string opts = menus[mi].ToLowerInvariant();
            it.FilesOnly = opts.Contains("show files only");
            it.FoldersFirst = !it.FilesOnly;
            it.Icon = IconSpec(Ini.Get(d, "KkMenuIconFileName", ""), "", root, res);
            if (!Directory.Exists(folder)) res.Notes.Add("Ordnermenü „" + it.Title + "“: Ordner nicht gefunden (" + folder + ").");
            return it;
        }

        /// <summary>
        /// Symbolangabe von RocketDock umsetzen. "Icons\x.png" wird kopiert; "Programm.exe?123456789" (keine echte
        /// Nummer) und das Symbol des Ziels selbst werden zu "Standard" (leer).
        /// </summary>
        private static string IconSpec(string fileName, string command, string root, Result res)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            string file = fileName;
            string index = null;
            int q = fileName.LastIndexOf('?');
            if (q > 0)
            {
                file = fileName.Substring(0, q);
                int i;
                if (int.TryParse(fileName.Substring(q + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i > -100000 && i < 100000)
                    index = i.ToString(CultureInfo.InvariantCulture);
            }
            if (!Path.IsPathRooted(file)) file = Path.Combine(root, file);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            // eigene Bilddateien aus dem RocketDock-Ordner mitnehmen
            bool inRocketDock = file.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            if (inRocketDock && (ext == ".png" || ext == ".ico" || ext == ".jpg" || ext == ".bmp" || ext == ".gif") && File.Exists(file))
            {
                try
                {
                    Directory.CreateDirectory(Settings.IconDirectory);
                    string dest = Path.Combine(Settings.IconDirectory, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                    res.IconsCopied++;
                    return Path.GetFileName(file); // relativ = im Symbole-Ordner
                }
                catch { return file; }
            }
            if (index == null && string.Equals(file, IconLoader.Expand(command), StringComparison.OrdinalIgnoreCase)) return "";
            if (index == null || index == "0") return file;
            return file + "?" + index;
        }

        private static void ApplySettings(Dictionary<string, string> v, Settings s, Result res)
        {
            s.IconSize = Ini.GetInt(v, "IconMin", s.IconSize, 16, 128);
            s.ZoomSize = Math.Max(s.IconSize, Ini.GetInt(v, "IconMax", s.ZoomSize, 16, 256));
            s.ZoomWidth = Ini.GetInt(v, "ZoomWidth", s.ZoomWidth, 1, 6);
            s.Labels = !Ini.GetBool(v, "HideLabels", !s.Labels);
            s.ShowRunning = Ini.GetBool(v, "ShowRunning", s.ShowRunning);
            s.ActivateRunning = Ini.GetBool(v, "OpenRunning", s.ActivateRunning);
            s.AutoHide = Ini.GetBool(v, "AutoHide", s.AutoHide);
            s.HideDelay = Ini.GetInt(v, "AutoHideDelay", s.HideDelay, 0, 5000);
            s.ShowDelay = Ini.GetInt(v, "PopupDelay", s.ShowDelay, 0, 5000);
            s.Locked = Ini.GetBool(v, "LockIcons", s.Locked);
            s.EdgeDistance = Ini.GetInt(v, "vOffset", s.EdgeDistance, 0, 400);
            s.CenterPercent = Ini.GetInt(v, "Offset", s.CenterPercent, -100, 100); // Zentrierung in Prozent
            s.Edge = (DockEdge)Ini.GetInt(v, "Side", (int)s.Edge, 0, 3);
            int mon = Ini.GetInt(v, "Monitor", -1, -1, 16);
            if (mon >= 0 && mon < Screen.AllScreens.Length) s.Monitor = mon;
            else if (mon >= 0) res.Notes.Add("Bildschirm " + (mon + 1) + " aus RocketDock gibt es nicht – das Dock steht auf dem Hauptbildschirm.");

            int mods, key;
            if (ParseHotkey(Ini.Get(v, "HotKey-Toggle", ""), out mods, out key))
            {
                s.HotkeyModifiers = mods;
                s.HotkeyKey = key;
            }
        }

        /// <summary>"Control+Alt+R" → Zusatztasten und Tastencode.</summary>
        public static bool ParseHotkey(string text, out int mods, out int key)
        {
            mods = 0;
            key = 0;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string raw in text.Split('+'))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t == "control" || t == "ctrl" || t == "strg") mods |= ComboHotkeyBox.ModControl;
                else if (t == "alt") mods |= ComboHotkeyBox.ModAlt;
                else if (t == "shift" || t == "umschalt") mods |= ComboHotkeyBox.ModShift;
                else if (t == "win" || t == "windows") mods |= ComboHotkeyBox.ModWin;
                else if (t.Length == 1 && char.IsLetterOrDigit(t[0])) key = char.ToUpperInvariant(t[0]);
                else if (t.Length >= 2 && t[0] == 'f')
                {
                    int f;
                    if (int.TryParse(t.Substring(1), out f) && f >= 1 && f <= 24) key = 0x6F + f;
                }
                else
                {
                    Keys k;
                    if (Enum.TryParse(raw.Trim(), true, out k)) key = (int)(k & Keys.KeyCode);
                }
            }
            return key > 0;
        }
    }
}
