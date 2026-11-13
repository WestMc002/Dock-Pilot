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
    /// <summary>Starten, Fenstervorschau und Kontextmenü. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
        // ------------------------------------------------------------------ Starten

        private void Launch(int index, bool forceNew)
        {
            if (index < 0 || index >= _slots.Count) return;
            Slot s = _slots[index];
            DockItem it = s.Item;
            if (it.Kind == ItemKind.Ordnermenue)
            {
                ShowFolderMenu(index);
                return;
            }
            if (!forceNew && _s.ActivateRunning)
            {
                RefreshRunning();
                if (s.Windows.Count > 0)
                {
                    // wie die Taskleiste: nach vorne holen, aktives minimieren; bei mehreren Fenstern Auswahl zeigen
                    if (s.Windows.Count > 1)
                    {
                        if (_s.Preview) ShowPreview(index); else ShowWindowMenu(index);
                        return;
                    }
                    HidePreview();
                    _previewBlock = index;
                    IntPtr fg = DockNative.GetForegroundWindow();
                    if (s.Windows[0] != fg) DockNative.Activate(s.Windows[0]);
                    else NativeMethods.ShowWindow(fg, 6); // SW_MINIMIZE
                    return;
                }
            }
            string err = Shell.Start(it.Command, it.Arguments, it.WorkingDirectory, it.ShowCmd, it.RunAsAdmin);
            if (err != null)
            {
                MessageBox.Show("„" + it.Title + "“ konnte nicht gestartet werden.\n\n" + err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_s.Animate) s.BounceStart = DateTime.Now;
            _nextRunning = DateTime.Now.AddMilliseconds(1200);
            _dirty = true;
        }

        /// <summary>Bildschirmrechteck eines Symbols (für die Position von Menüs).</summary>
        private Rectangle ScreenRect(Slot s)
        {
            RectangleF r = Map(s.Pos, _edgeDist + _padA, s.Size, s.Size);
            return new Rectangle(_winRect.Left + (int)r.X, _winRect.Top + (int)r.Y, (int)r.Width, (int)r.Height);
        }

        private void ShowAt(ContextMenuStrip menu, Slot anchor)
        {
            HidePreview();
            _menuOpen = true;
            menu.Opened += delegate { _menuOpen = true; _dirty = true; };
            menu.Closed += delegate
            {
                _menuOpen = false;
                _lastInside = DateTime.Now;
                _dirty = true;
            };
            // Das vorige Menü erst jetzt freigeben – ein Klick darin kann noch einen Dialog offen haben
            if (_lastMenu != null && _lastMenu != menu && !_lastMenu.Visible)
            {
                MenuBuilder.DisposeDeep(_lastMenu.Items);
                _lastMenu.Dispose();
            }
            _lastMenu = menu;
            _openMenu = menu;
            _menuAwaySince = DateTime.MinValue;
            menu.Closed += delegate { if (_openMenu == menu) _openMenu = null; };
            // Menü nach vorne holen: dann schließt es wie gewohnt beim Klick irgendwo anders (oder mit Esc).
            // Das Dock selbst wird nie aktiv, ohne das bekäme das Menü vom Wegklicken nichts mit.
            menu.Opened += delegate { DockNative.BringToFront(menu.Handle); };
            if (anchor == null)
            {
                menu.Show(Cursor.Position);
                return;
            }
            Rectangle r = ScreenRect(anchor);
            int gap = (int)R(6);
            float shelfOuter = _edgeDist + Math.Max(_shelfThick, _padA + anchor.Size) + gap;
            // Menü innerhalb des Bildschirms halten, auf dem das Dock liegt. Windows prüft nur den gesamten Desktop –
            // beim letzten Symbol am Rand ragte das Menü sonst auf den Nachbarbildschirm.
            Size size = menu.GetPreferredSize(Size.Empty);
            Rectangle area = Screen.FromRectangle(_monitor.Width > 0 ? _monitor : _winRect).WorkingArea;
            int margin = (int)R(4);
            int x = Math.Max(area.Left + margin, Math.Min(r.Left, area.Right - margin - size.Width));
            int y = Math.Max(area.Top + margin, Math.Min(r.Top, area.Bottom - margin - size.Height));
            switch (_s.Edge)
            {
                case DockEdge.Oben: menu.Show(new Point(x, _winRect.Top + (int)shelfOuter), ToolStripDropDownDirection.BelowRight); break;
                case DockEdge.Unten: menu.Show(new Point(x, _winRect.Bottom - (int)shelfOuter), ToolStripDropDownDirection.AboveRight); break;
                case DockEdge.Links: menu.Show(new Point(_winRect.Left + (int)shelfOuter, y), ToolStripDropDownDirection.Right); break;
                default: menu.Show(new Point(_winRect.Right - (int)shelfOuter, y), ToolStripDropDownDirection.Left); break;
            }
        }

        // ------------------------------------------------------------------ Fenstervorschau

        private static int _previewDelay = -1;

        /// <summary>
        /// Verzögerung bis zur ersten Vorschau. Wie die Taskleiste: der Wert „ExtendedUIHoverTime“ aus der Registry,
        /// falls jemand ihn gesetzt hat, sonst 300 ms (minus ein Tick des Timers, damit es nicht später kommt).
        /// </summary>
        private static int PreviewDelay
        {
            get
            {
                if (_previewDelay >= 0) return _previewDelay;
                int ms = 300;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    {
                        object v = key != null ? key.GetValue("ExtendedUIHoverTime") : null;
                        if (v is int && (int)v >= 0 && (int)v <= 5000) ms = (int)v;
                    }
                }
                catch { }
                _previewDelay = Math.Max(0, ms - 15);
                return _previewDelay;
            }
        }

        private bool PreviewVisible { get { return _preview != null && !_preview.IsDisposed && _preview.Visible; } }

        /// <summary>
        /// Fenstervorschau wie in der Taskleiste: Bleibt die Maus kurz auf einem laufenden Programm, erscheint die Vorschau.
        /// Sie verschwindet, wenn die Maus Symbol und Vorschau verlässt.
        /// </summary>
        private void PreviewTick(DateTime now, int hover, bool overPreview)
        {
            bool shown = PreviewVisible;
            if (!_s.Preview || _menuOpen || _dragging || _extDrag || _show < 0.95f)
            {
                _previewWant = -1;
                if (shown) HidePreview();
                return;
            }
            int target = hover >= 0 && hover < _slots.Count && !_slots[hover].Item.IsSeparator && _slots[hover].Windows.Count > 0 ? hover : -1;
            if (target != _previewBlock) _previewBlock = -1;
            else if (!shown) { _previewWant = -1; return; }
            if (target >= 0)
            {
                _previewAway = DateTime.MinValue;
                if (shown && target == _previewIndex) return;
                if (target != _previewWant) { _previewWant = target; _previewSince = now; }
                // erste Vorschau nach kurzem Verweilen, der Wechsel zum Nachbarn geht schneller
                if ((now - _previewSince).TotalMilliseconds >= (shown ? 100 : PreviewDelay) && (MouseButtons & MouseButtons.Left) == 0) ShowPreview(target);
                return;
            }
            _previewWant = -1;
            if (!shown) return;
            if (overPreview) { _previewAway = DateTime.MinValue; return; }
            if (_previewAway == DateTime.MinValue) _previewAway = now;
            else if ((now - _previewAway).TotalMilliseconds >= 350) HidePreview();
        }

        private void ShowPreview(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            Slot s = _slots[index];
            if (s.Windows.Count == 0) return;
            if (_preview == null || _preview.IsDisposed)
            {
                _preview = new PreviewPopup();
                _preview.Dismissed += delegate { _previewIndex = -1; _previewWant = -1; _lastInside = DateTime.Now; _dirty = true; };
            }
            // Abstand zum Symbol: knapp über der Leiste bzw. dem vergrößerten Symbol (wie bei den Menüs)
            Rectangle r = ScreenRect(s);
            Rectangle anchor;
            int outer = (int)(_edgeDist + Math.Max(_shelfThick, _padA + Math.Max(s.Size, _zoomPx)));
            switch (_s.Edge)
            {
                case DockEdge.Oben: anchor = Rectangle.FromLTRB(r.Left, _winRect.Top, r.Right, _winRect.Top + outer); break;
                case DockEdge.Links: anchor = Rectangle.FromLTRB(_winRect.Left, r.Top, _winRect.Left + outer, r.Bottom); break;
                case DockEdge.Rechts: anchor = Rectangle.FromLTRB(_winRect.Right - outer, r.Top, _winRect.Right, r.Bottom); break;
                default: anchor = Rectangle.FromLTRB(r.Left, _winRect.Bottom - outer, r.Right, _winRect.Bottom); break;
            }
            _preview.ShowFor(s.Windows, s.Item.Title, anchor, _s.Edge, (int)R(6), _monitor, _sc);
            _previewIndex = PreviewVisible ? index : -1;
            _previewWant = index;
            _previewAway = DateTime.MinValue;
            _dirty = true;
        }

        private void HidePreview()
        {
            _previewIndex = -1;
            if (_preview == null || _preview.IsDisposed || !_preview.Visible) return;
            _preview.Hide();
            _dirty = true;
        }

        /// <summary>Mehrere Fenster eines laufenden Programms: Menü mit den Fenstertiteln, darunter „Neues Fenster“.</summary>
        private void ShowWindowMenu(int index)
        {
            Slot s = _slots[index];
            MenuBuilder mb = new MenuBuilder();
            IntPtr fg = DockNative.GetForegroundWindow();
            foreach (IntPtr h in s.Windows)
            {
                IntPtr hwnd = h;
                string title = DockNative.WindowTitle(hwnd);
                if (string.IsNullOrEmpty(title)) title = s.Item.Title;
                if (title.Length > 70) title = title.Substring(0, 68) + "…";
                GlyphMenuItem item = mb.Item(title.Replace("&", "&&"), '\uE737', delegate { DockNative.Activate(hwnd); });
                if (hwnd == fg) item.Font = new Font(mb.Menu.Font, FontStyle.Bold);
                mb.Menu.Items.Add(item);
            }
            mb.Separator();
            mb.Menu.Items.Add(mb.Item("Neues Fenster", '\uE710', delegate { Launch(index, true); }));
            mb.End();
            ShowAt(mb.Menu, s);
        }

        private void ShowFolderMenu(int index)
        {
            Slot s = _slots[index];
            string folder = IconLoader.Expand(s.Item.Folder);
            MenuBuilder mb = new MenuBuilder();
            // Sortierung; eigene Reihenfolge per Ziehen im Menü – wird sofort gespeichert
            DockItem item = s.Item;
            mb.Sorting = new FolderSort(item.Sort, folder, item.Order);
            mb.Sorting.Changed = delegate (string order)
            {
                item.Sort = 4;
                item.Order = order;
                Save();
            };
            // „Einträge sperren“ schützt auch die Reihenfolge in den Ordnermenüs
            mb.AllowReorder = !_s.Locked;
            // aus dem Menü herausziehen: über dem Dock wird daraus ein Dock-Eintrag
            mb.DragOver = MenuDragOver;
            mb.DragDrop = MenuDragDrop;
            mb.Removed = delegate (List<KeyValuePair<string, byte[]>> files)
            {
                // für „Entfernen rückgängig“ merken (ersetzt einen gemerkten Dock-Eintrag)
                _undoFiles = files;
                _undoItem = null;
            };
            mb.FillFolder(mb.Menu.Items, folder, item.FoldersFirst, item.FilesOnly, false); // "Ordner öffnen" nur im Kontextmenü
            mb.EnableReorder(mb.Menu, folder);
            mb.End();
            ContextMenuStrip menu = mb.Menu;
            // Tippen springt zum passenden Eintrag
            MenuTypeAhead typing = new MenuTypeAhead(menu);
            Application.AddMessageFilter(typing);
            menu.Closed += delegate { Application.RemoveMessageFilter(typing); };
            menu.Closed += delegate { if (_folderMenu == menu) { _folderMenu = null; _folderIndex = -1; } };
            ShowAt(menu, s);
            _folderMenu = menu;
            _folderIndex = index;
            _switchSince = DateTime.MinValue;
            _switchTo = -1;
        }

        /// <summary>
        /// Bei offenem Menü (Kontextmenü oder Ordnermenü): Verlässt die Maus Menü und Dock eine Sekunde lang, klappt es zu.
        /// Bei einem Ordnermenü: Fährt die Maus im Dock auf ein anderes Ordnermenü, wird direkt dorthin gewechselt
        /// (wie in einer Menüleiste). Gibt true zurück, wenn das Menü gewechselt wurde (der Rest des Ticks entfällt dann).
        /// </summary>
        private bool MenuTick(DateTime now, int x, int y, float along, float across)
        {
            ContextMenuStrip menu = _openMenu;
            if (menu.IsDisposed || !menu.Visible) { _openMenu = null; return false; }
            if (MenuBuilder.DragActive) { _menuAwaySince = DateTime.MinValue; return false; } // beim Ziehen offen lassen
            Point p = new Point(x, y);
            float depth = _edgeDist + Math.Max(_shelfThick, _padA + Math.Max(_zoomPx, _basePx) + _padA);
            bool overDock = across >= 0 && across <= depth && along >= _shelfStart - _spacing && along <= _shelfEnd + _spacing;
            bool overMenu = InMenus(menu, p);

            // zuklappen, wenn die Maus weg ist
            if (overDock || overMenu) _menuAwaySince = DateTime.MinValue;
            else if (_menuAwaySince == DateTime.MinValue) _menuAwaySince = now;
            else if ((now - _menuAwaySince).TotalMilliseconds >= 1000)
            {
                _menuAwaySince = DateTime.MinValue;
                menu.Close(ToolStripDropDownCloseReason.AppClicked);
                return false;
            }

            // auf ein anderes Ordnermenü wechseln
            if (menu != _folderMenu) return false;
            int h = overDock && !overMenu ? HitIndex(along, across) : -1;
            bool other = h >= 0 && h != _folderIndex && h < _slots.Count && !_slots[h].Item.IsSeparator && _slots[h].Item.Kind == ItemKind.Ordnermenue;
            if (!other) { _switchTo = -1; return false; }
            if (h != _switchTo) { _switchTo = h; _switchSince = now; return false; }
            if ((now - _switchSince).TotalMilliseconds < 120) return false; // nur kurzes Verweilen, kein Wechsel im Vorbeiflug
            _switchTo = -1;
            menu.Close(ToolStripDropDownCloseReason.AppClicked);
            _hover = h;
            ShowFolderMenu(h);
            _dirty = true;
            return true;
        }

        /// <summary>
        /// Ein Eintrag wird aus einem Ordnermenü gezogen: Ist die Maus über dem Dock, die Einfügestelle zeigen
        /// (blaue Linie wie beim Ziehen aus dem Explorer). Gibt zurück, ob die Maus über dem Dock ist.
        /// </summary>
        private bool MenuDragOver()
        {
            float along, across;
            CursorDock(out along, out across);
            float depth = _edgeDist + Math.Max(_shelfThick, _padA + Math.Max(_zoomPx, _basePx) + _padA);
            bool over = across >= 0 && across <= depth && along >= _shelfStart - _spacing && along <= _shelfEnd + _spacing;
            int index = -1;
            if (over)
            {
                index = _slots.Count;
                for (int i = 0; i < _slots.Count; i++)
                    if (along < _slots[i].Pos + _slots[i].Len / 2f) { index = i; break; }
            }
            if (_extDrag != over || _dropIndex != index || _dropOnto != -1)
            {
                _extDrag = over;
                _dropOnto = -1;
                _dropIndex = index;
                _dirty = true;
            }
            return over;
        }

        /// <summary>
        /// Ziehen aus dem Ordnermenü beendet: über dem Dock dort einfügen (paths == null: abgebrochen).
        /// Gibt zurück, ob die Einträge im Dock gelandet sind.
        /// </summary>
        private bool MenuDragDrop(List<string> paths)
        {
            int index = _extDrag ? _dropIndex : -1;
            _extDrag = false;
            _dropIndex = _dropOnto = -1;
            _dirty = true;
            if (paths == null || index < 0) return false;
            foreach (string path in paths) InsertItem(Math.Min(index++, _items.Count), ItemFromPath(path));
            return true;
        }

        /// <summary>Aus einem Ordnermenü entfernte Dateien wieder an ihren Platz legen (die Kopie im Papierkorb bleibt dort).</summary>
        private void RestoreFiles()
        {
            List<KeyValuePair<string, byte[]>> files = _undoFiles;
            _undoFiles = null;
            if (files == null) return;
            string error = null;
            foreach (KeyValuePair<string, byte[]> f in files)
            {
                try
                {
                    if (File.Exists(f.Key) || Directory.Exists(f.Key)) continue; // inzwischen wieder da
                    File.WriteAllBytes(f.Key, f.Value);
                }
                catch (Exception ex) { error = ex.Message; }
            }
            if (error != null) MessageBox.Show(error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Liegt der Punkt im Menü oder in einem seiner offenen Untermenüs?</summary>
        private static bool InMenus(ToolStripDropDown dd, Point p)
        {
            if (dd == null || !dd.Visible) return false;
            if (dd.Bounds.Contains(p)) return true;
            foreach (ToolStripItem item in dd.Items)
            {
                ToolStripDropDownItem di = item as ToolStripDropDownItem;
                if (di != null && di.HasDropDownItems && di.DropDown.Visible && InMenus(di.DropDown, p)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Kontextmenü

        private void ShowContextMenu(int index)
        {
            ShowAt(BuildContextMenu(index), null);
        }

        /// <summary>Kontextmenü eines Eintrags (index &lt; 0: Rechtsklick auf die freie Leiste).</summary>
        internal ContextMenuStrip BuildContextMenu(int index)
        {
            MenuBuilder mb = new MenuBuilder();
            ToolStripItemCollection items = mb.Menu.Items;
            DockItem it = index >= 0 ? _items[index] : null;
            int insertAt = index >= 0 ? index + 1 : _items.Count;

            if (it != null && it.Kind == ItemKind.Programm)
            {
                GlyphMenuItem open = mb.Item("Öffnen", '', delegate { Launch(index, true); });
                open.Font = new Font(mb.Menu.Font, FontStyle.Bold);
                items.Add(open);
                items.Add(mb.Item("Als Administrator ausführen", MenuHelper.Shield, delegate
                {
                    string err = Shell.Start(it.Command, it.Arguments, it.WorkingDirectory, it.ShowCmd, true);
                    if (err != null) MessageBox.Show(err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
                string cmd = IconLoader.Expand(it.Command);
                bool local = false;
                try { local = Path.IsPathRooted(cmd) && (File.Exists(cmd) || Directory.Exists(cmd)); } catch { }
                if (local) items.Add(mb.Item("Speicherort öffnen", MenuHelper.Folder, delegate { Shell.ShowInExplorer(cmd); }));
                if (IsRecycleBin(it))
                {
                    GlyphMenuItem empty = mb.Item("Papierkorb leeren", '\uE74D', delegate
                    {
                        DockNative.EmptyRecycleBin(IntPtr.Zero); // Windows fragt selbst nach
                        _nextBin = DateTime.MinValue;
                    });
                    empty.Enabled = index >= _slots.Count || _slots[index].BinState != 0;
                    items.Add(empty);
                }
                // zuletzt verwendete Dateien, die mit diesem Programm geöffnet werden
                // (für die Screenshots gibt es das Menü auch ohne geladene Symbole)
                HashSet<string> exes = index < _slots.Count ? _slots[index].Exes : new HashSet<string>();
                if (exes.Count > 0 && !_demo)
                    items.Add(mb.Submenu("Zuletzt verwendet", MenuHelper.History, delegate (ToolStripItemCollection sub)
                    {
                        List<string> files = RecentFiles.For(exes, 10);
                        foreach (string f in files)
                        {
                            string file = f;
                            GlyphMenuItem entry = mb.Item(Path.GetFileName(file).Replace("&", "&&"), MenuHelper.Document, delegate
                            {
                                string err = Shell.Start(it.Command, "\"" + file + "\"", it.WorkingDirectory, it.ShowCmd, false);
                                if (err != null) MessageBox.Show(err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            });
                            sub.Add(entry);
                        }
                        if (files.Count == 0)
                        {
                            GlyphMenuItem none = mb.Item("(keine zuletzt verwendeten Dateien)", '\0', null);
                            none.Enabled = false;
                            sub.Add(none);
                        }
                    }));
                mb.Separator();
            }
            else if (it != null && it.Kind == ItemKind.Ordnermenue)
            {
                items.Add(mb.Item("Ordner öffnen", MenuHelper.Folder, delegate { Shell.Open(it.Folder, "", ""); }));
                mb.Separator();
            }
            if (it != null && it.Temp)
            {
                // läuft nur gerade, hat aber keinen eigenen Eintrag
                items.Add(mb.Item("Im Dock behalten", '\uE718', delegate { KeepItem(index); }));
                mb.Separator();
            }
            if (it != null && !it.Temp)
            {
                if (!it.IsSeparator) items.Add(mb.Item("Eigenschaften …", MenuHelper.Settings, delegate { EditItem(index); }));
                items.Add(mb.Item(it.IsSeparator ? "Trennlinie entfernen" : "Aus dem Dock entfernen", '', delegate { RemoveItem(index); }));
                mb.Separator();
            }
            items.Add(mb.Item("Programm oder Datei hinzufügen …", MenuHelper.Add, delegate { AddFile(insertAt); }));
            items.Add(mb.Item("Ordnermenü hinzufügen …", '', delegate { AddFolderMenu(insertAt); }));
            items.Add(mb.Item("Trennlinie einfügen", '', delegate { InsertItem(insertAt, new DockItem { Kind = ItemKind.Trennlinie }); }));
            if (_undoFiles != null && _undoFiles.Count > 0)
            {
                string what = _undoFiles.Count == 1
                    ? "„" + MenuBuilder.DisplayName(_undoFiles[0].Key).Replace("&", "&&") + "“"
                    : _undoFiles.Count + " Einträge im Ordnermenü";
                items.Add(mb.Item("Entfernen rückgängig: " + what, '\uE7A7', delegate { RestoreFiles(); }));
            }
            if (_undoItem != null)
            {
                string what = _undoItem.IsSeparator ? "Trennlinie" : "„" + _undoItem.Title.Replace("&", "&&") + "“";
                items.Add(mb.Item("Entfernen rückgängig: " + what, '\uE7A7', delegate
                {
                    DockItem back = _undoItem;
                    if (back == null) return;
                    _undoItem = null;
                    InsertItem(_undoIndex, back);
                }));
            }
            mb.Separator();

            GlyphMenuItem hide = mb.Item("Automatisch ausblenden", '', delegate
            {
                // immer die gemeinsamen, gespeicherten Einstellungen ändern (auch bei einem weiteren Dock oder während
                // einer Vorschau aus dem Einstellungsfenster)
                Settings shared = Shared ?? _saved;
                shared.AutoHide = !shared.AutoHide;
                _saved.AutoHide = _s.AutoHide = shared.AutoHide;
                shared.Save();
                ApplySettings();
                if (SharedChanged != null) SharedChanged();
            });
            MenuBuilder.MakeToggle(hide, _s.AutoHide);
            items.Add(hide);
            GlyphMenuItem lockItem = mb.Item("Einträge sperren", '', delegate
            {
                Settings shared = Shared ?? _saved;
                shared.Locked = !shared.Locked;
                _saved.Locked = _s.Locked = shared.Locked;
                shared.Save();
                if (SharedChanged != null) SharedChanged();
            });
            MenuBuilder.MakeToggle(lockItem, _s.Locked);
            items.Add(lockItem);
            mb.Separator();
            items.Add(mb.Item("Einstellungen …", MenuHelper.Settings, delegate { if (OpenSettings != null) OpenSettings(); }));
            items.Add(mb.Item("Info über Dock Pilot", MenuHelper.Info, delegate { if (ShowAbout != null) ShowAbout(); }));
            items.Add(mb.Item("Beenden", MenuHelper.Power, delegate { if (ExitApp != null) ExitApp(); }));
            mb.End();
            return mb.Menu;
        }
    }
}
