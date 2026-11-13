// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Einträge einfügen, entfernen, bearbeiten und aus Pfaden anlegen. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
        // ------------------------------------------------------------------ Einträge ändern

        public void InsertItem(int index, DockItem item)
        {
            // feste Einträge stehen vor den vorübergehenden (laufende Programme ohne Eintrag)
            index = Math.Max(0, Math.Min(index, item.Temp ? _items.Count : FirstTemp()));
            Dictionary<Slot, float> glide = GlideSnapshot();
            _items.Insert(index, item);
            Slot s = new Slot { Item = item };
            if (glide != null) s.Appear = 0f; // wächst heran, die Nachbarn gleiten zur Seite
            _slots.Insert(index, s);
            Save();
            ApplySettings();
            GlideFrom(glide);
            LoadIconsAsync(new List<Slot> { s });
        }

        private void RemoveItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            DockItem it = _items[index];
            if (!it.IsSeparator &&
                MessageBox.Show("„" + it.Title + "“ aus dem Dock entfernen?\n\nDas Programm selbst bleibt installiert.", AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            Dictionary<Slot, float> glide = GlideSnapshot();
            _undoItem = it;
            _undoIndex = index;
            _undoFiles = null;
            _items.RemoveAt(index);
            _slots[index].DisposeImages();
            _slots.RemoveAt(index);
            _hover = -1;
            Save();
            ApplySettings();
            GlideFrom(glide);
        }

        private void EditItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _menuOpen = true; // Dock bleibt sichtbar, solange der Dialog offen ist
            try
            {
                using (ItemDialog dlg = new ItemDialog(_items[index].Clone()))
                {
                    dlg.TestHotkey = TestItemHotkey;
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        _items[index] = dlg.Result;
                        ReloadSlot(index);
                        Save();
                        ApplySettings();
                    }
                }
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        private void AddFile(int index)
        {
            _menuOpen = true;
            try
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Programm oder Datei zum Dock hinzufügen";
                    dlg.Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc|Alle Dateien|*.*";
                    dlg.Multiselect = true;
                    dlg.DereferenceLinks = false;
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    foreach (string f in dlg.FileNames) InsertItem(index++, ItemFromPath(f));
                }
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        private void AddFolderMenu(int index)
        {
            _menuOpen = true;
            try
            {
                string folder = FolderPicker.Pick(null, "Ordner für das Ordnermenü wählen", null);
                if (string.IsNullOrEmpty(folder)) return;
                DockItem it = new DockItem();
                it.Kind = ItemKind.Ordnermenue;
                it.Folder = folder;
                it.Title = Path.GetFileName(folder.TrimEnd('\\'));
                if (string.IsNullOrEmpty(it.Title)) it.Title = folder;
                InsertItem(index, it);
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        /// <summary>Neuer Eintrag aus einer Datei, einem Ordner oder einer Verknüpfung (Ziel der Verknüpfung wird übernommen).</summary>
        public static DockItem ItemFromPath(string path)
        {
            DockItem it = new DockItem();
            it.Command = path;
            it.Title = MenuBuilder.DisplayName(path);
            if (Directory.Exists(path))
            {
                if (string.IsNullOrEmpty(it.Title)) it.Title = path;
                return it;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lnk")
            {
                ShortcutInfo lnk = ShortcutInfo.Read(path);
                if (lnk != null && !string.IsNullOrEmpty(lnk.Target) && (File.Exists(lnk.Target) || Directory.Exists(lnk.Target)))
                {
                    it.Command = lnk.Target;
                    it.Arguments = lnk.Arguments;
                    it.WorkingDirectory = lnk.WorkingDirectory;
                    if (!string.IsNullOrEmpty(lnk.IconFile)) it.Icon = lnk.IconFile + "?" + lnk.IconIndex;
                }
            }
            else if (ext == ".exe")
            {
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 1) it.Title = desc.Trim();
                }
                catch { }
            }
            return it;
        }
    }
}
