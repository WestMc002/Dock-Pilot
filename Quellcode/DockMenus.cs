// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Menüs im einheitlichen Windows-11-Stil (Maße wie Monitor Dimmer / WindowGrid):
    /// Kontextmenü des Docks, Tray-Menü und Ordnermenüs (Ersatz für das KkMenu-Docklet).
    /// </summary>
    internal sealed class MenuBuilder
    {
        public readonly ContextMenuStrip Menu;
        public readonly MenuTheme Colors;
        public readonly float Scale;
        private readonly ModernRenderer _renderer;

        public MenuBuilder() : this(null) { }

        /// <summary>Vorhandenes Menü neu gestalten (z. B. das Tray-Menü bei jedem Öffnen).</summary>
        public MenuBuilder(ContextMenuStrip existing)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            Colors = MenuTheme.Current();
            _renderer = new ModernRenderer(Colors, Scale);
            Menu = existing ?? new ContextMenuStrip();
            Style(Menu);
            if (existing == null) Menu.Opened += delegate { MenuHelper.RoundCorners(Menu); };
        }

        /// <summary>Gleiche Darstellung für Haupt- und Untermenüs.</summary>
        public void Style(ToolStripDropDownMenu dd)
        {
            dd.Renderer = _renderer;
            dd.Font = new Font("Segoe UI", 9.5f);
            dd.ShowCheckMargin = false;
            dd.ShowImageMargin = true;
            dd.ShowItemToolTips = false;
            // Ab Windows 11 zeichnet das System selbst einen Schatten – den alten WinForms-Schatten abschalten
            if (Environment.OSVersion.Version.Build >= 22000) dd.DropShadowEnabled = false;
            dd.ImageScalingSize = new Size(IconWidth, (int)(16 * Scale));
            dd.Padding = new Padding(0, (int)(4 * Scale), 0, (int)(4 * Scale));
            // Mausrad in langen Menüs (WinForms scrollt sonst nur über die Pfeile oben und unten)
            dd.MouseWheel -= OnMenuWheel;
            dd.MouseWheel += OnMenuWheel;
            // Scroll-Pfeile langer Menüs: größer, mit Abstand zum Rand und in den Menüfarben
            MenuTheme colors = Colors;
            float scale = Scale;
            dd.Opened += delegate { StyleScrollButtons(dd, colors, scale); };
        }

        private static readonly PropertyInfo UpButtonProp = typeof(ToolStripDropDownMenu).GetProperty("UpScrollButton", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo DownButtonProp = typeof(ToolStripDropDownMenu).GetProperty("DownScrollButton", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// WinForms zeigt bei langen Menüs winzige Pfeile direkt am Rand. Hier bekommen sie ein Chevron in Menügröße
        /// (Symbolschrift, graue Textfarbe) – die Schaltfläche wird dadurch höher und der Pfeil rückt vom Rand weg.
        /// </summary>
        private static void StyleScrollButtons(ToolStripDropDownMenu dd, MenuTheme colors, float scale)
        {
            if (UpButtonProp == null || DownButtonProp == null) return;
            bool changed = false;
            try
            {
                foreach (KeyValuePair<PropertyInfo, char> b in new[]
                {
                    new KeyValuePair<PropertyInfo, char>(UpButtonProp, '\uE70E'),
                    new KeyValuePair<PropertyInfo, char>(DownButtonProp, '\uE70D')
                })
                {
                    ToolStripControlHost host = b.Key.GetValue(dd, null) as ToolStripControlHost;
                    if (host == null || host.Control == null || "styled".Equals(host.Control.Tag)) continue;
                    Label label = host.Control as Label;
                    if (label == null) continue;
                    int h = (int)Math.Round(22 * scale);
                    Bitmap img = new Bitmap(h, h);
                    using (Graphics g = Graphics.FromImage(img))
                    using (Image glyph = MenuHelper.Glyph(b.Value, colors.Muted, (int)Math.Round(10 * scale)))
                        g.DrawImage(glyph, (h - glyph.Width) / 2, (h - glyph.Height) / 2, glyph.Width, glyph.Height);
                    label.Image = img;
                    label.ImageAlign = ContentAlignment.MiddleCenter;
                    label.BackColor = colors.Back;
                    label.Tag = "styled";
                    changed = true;
                }
            }
            catch { }
            if (changed) dd.PerformLayout();
        }

        private static readonly MethodInfo ScrollMethod = typeof(ToolStripDropDownMenu).GetMethod("ScrollInternal",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);

        /// <summary>Je Raste des Mausrads drei Einträge weiter (wie in Windows-Menüs).</summary>
        private static void OnMenuWheel(object sender, MouseEventArgs e)
        {
            ToolStripDropDownMenu dd = sender as ToolStripDropDownMenu;
            if (dd == null || ScrollMethod == null || e.Delta == 0) return;
            bool up = e.Delta > 0;
            int steps = Math.Max(1, Math.Abs(e.Delta) / 120) * 3;
            try
            {
                for (int i = 0; i < steps && CanScroll(dd, up); i++) ScrollMethod.Invoke(dd, new object[] { up });
            }
            catch { }
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        /// <summary>Nur so weit scrollen, bis der erste bzw. letzte Eintrag ganz zu sehen ist.</summary>
        private static bool CanScroll(ToolStripDropDownMenu dd, bool up)
        {
            if (dd.Items.Count == 0) return false;
            Rectangle view = dd.DisplayRectangle;
            if (up) return dd.Items[0].Bounds.Top < view.Top;
            return dd.Items[dd.Items.Count - 1].Bounds.Bottom > view.Bottom;
        }

        public int IconWidth
        {
            get { return (int)(36 * Scale); }
        }

        /// <summary>Einheitliche Zeilenhöhe wie im Monitor Dimmer: Schrifthöhe + je 4 Einheiten oben und unten.</summary>
        public int RowHeight
        {
            get { return TextRenderer.MeasureText("Ag", Menu.Font).Height + (int)(8 * Scale); }
        }

        public GlyphMenuItem Item(string text, char glyph, MethodInvoker action)
        {
            GlyphMenuItem item = Base(text, action);
            if (glyph != '\0') item.Glyph = MenuHelper.Glyph(glyph, Colors.Text, (int)(16 * Scale));
            return item;
        }

        /// <summary>Eintrag mit Programm- bzw. Dateisymbol statt Symbolschrift.</summary>
        public GlyphMenuItem ImageItem(string text, Image icon, MethodInvoker action)
        {
            GlyphMenuItem item = Base(text, action);
            item.Glyph = icon;
            return item;
        }

        private GlyphMenuItem Base(string text, MethodInvoker action)
        {
            GlyphMenuItem item = new GlyphMenuItem(text);
            int pad = (int)(5 * Scale);
            item.Padding = new Padding(0, pad, (int)(8 * Scale), pad);
            item.ShowShortcutKeys = true;
            item.FixedHeight = RowHeight;
            // Unsichtbarer Platzhalter: reserviert die volle Breite der Symbolspalte
            item.Image = MenuHelper.Blank(IconWidth, (int)(16 * Scale));
            item.ImageScaling = ToolStripItemImageScaling.None;
            if (action != null) item.Click += delegate { action(); };
            return item;
        }

        /// <summary>Eintrag mit Untermenü, das erst beim Aufklappen gefüllt wird.</summary>
        public GlyphMenuItem Submenu(string text, char glyph, Action<ToolStripItemCollection> fill)
        {
            GlyphMenuItem item = Item(text, glyph, null);
            item.DropDownItems.Add(new ToolStripMenuItem("…"));
            ToolStripDropDownMenu dd = (ToolStripDropDownMenu)item.DropDown;
            Style(dd);
            dd.Opened += delegate { MenuHelper.RoundCorners(dd); };
            bool filled = false;
            item.DropDownOpening += delegate
            {
                if (filled) return;
                filled = true;
                dd.SuspendLayout();
                item.DropDownItems.Clear();
                dd.Items.Add(new MenuSpacer((int)Math.Round(2 * Scale)));
                fill(dd.Items);
                dd.Items.Add(new MenuSpacer((int)(6 * Scale)));
                dd.ResumeLayout();
            };
            return item;
        }

        /// <summary>Schalter rechts im Menüeintrag statt Haken.</summary>
        public static void MakeToggle(GlyphMenuItem item, bool on)
        {
            item.IsToggle = true;
            item.ToggleOn = on;
            item.ShortcutKeyDisplayString = "      ";
        }

        public void Separator()
        {
            Menu.Items.Add(new ToolStripSeparator());
        }

        /// <summary>Unsichtbare Leerzeile unter dem letzten Eintrag (Padding.Bottom wird von Windows ignoriert).</summary>
        public void End()
        {
            // oben gleich viel Luft wie unten: Padding oben (4) + 2 = Leerzeile unten (6), wie bei den Ordnermenüs
            // (nicht beim Tray-Menü mit Kopfzeile – dessen Maße folgen der Vorlage Monitor Dimmer)
            if (Menu.Items.Count > 0 && !(Menu.Items[0] is MenuSpacer) && !(Menu.Items[0] is MenuHeader))
                Menu.Items.Insert(0, new MenuSpacer((int)Math.Round(2 * Scale)));
            Menu.Items.Add(new MenuSpacer((int)(6 * Scale)));
        }

        // ------------------------------------------------------------------ Ordnermenüs

        private static readonly HashSet<string> HiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "desktop.ini", "thumbs.db", ".ds_store"
        };

        /// <summary>
        /// Inhalt eines Ordners als Menü (wie KkMenu): Verknüpfungen ohne Endung, Unterordner als Untermenüs,
        /// nach Namen sortiert. Unterordner werden erst beim Aufklappen gelesen.
        /// </summary>
        public void FillFolder(ToolStripItemCollection items, string folder, bool foldersFirst, bool filesOnly, bool openEntry)
        {
            // Dateisymbole in Menügröße (16 px wie die Symbole der übrigen Menüs) – größere füllen die Zeile bis an den Rand
            int iconSize = (int)Math.Round(16 * Scale);
            List<KeyValuePair<GlyphMenuItem, string>> pending = new List<KeyValuePair<GlyphMenuItem, string>>();
            // etwas mehr Luft über dem ersten Eintrag (passend zur Leerzeile unten)
            if (items.Count == 0) items.Add(new MenuSpacer((int)Math.Round(2 * Scale)));
            List<string> dirs = new List<string>();
            List<string> files = new List<string>();
            try
            {
                if (!filesOnly)
                    foreach (string d in Directory.GetDirectories(folder))
                        if (Visible(d)) dirs.Add(d);
                foreach (string f in Directory.GetFiles(folder))
                    if (Visible(f) && !HiddenNames.Contains(Path.GetFileName(f))) files.Add(f);
            }
            catch (Exception ex)
            {
                GlyphMenuItem err = Item("Ordner nicht erreichbar", MenuHelper.Warning, null);
                err.Enabled = false;
                err.ToolTipText = ex.Message;
                items.Add(err);
                return;
            }

            List<KeyValuePair<string, bool>> all = SortEntries(folder, dirs, files, foldersFirst);

            foreach (KeyValuePair<string, bool> e in all)
            {
                string path = e.Key;
                // "&" wäre sonst ein Tastenkürzel-Zeichen und verschwindet ("Command & Conquer")
                GlyphMenuItem item = ImageItem(DisplayName(path).Replace("&", "&&"), CachedIcon(path, iconSize), null);
                item.Tag = path; // für das Verschieben per Ziehen
                if (item.Glyph == null) pending.Add(new KeyValuePair<GlyphMenuItem, string>(item, path));
                if (e.Value)
                {
                    // Untermenü: erst beim Aufklappen füllen
                    item.DropDownItems.Add(new ToolStripMenuItem("…"));
                    ToolStripDropDownMenu dd = (ToolStripDropDownMenu)item.DropDown;
                    Style(dd);
                    dd.Opened += delegate { MenuHelper.RoundCorners(dd); };
                    bool filled = false;
                    item.DropDownOpening += delegate
                    {
                        if (filled) return;
                        filled = true;
                        dd.SuspendLayout();
                        item.DropDownItems.Clear();
                        FillFolder(item.DropDownItems, path, foldersFirst, filesOnly, false);
                        dd.Items.Add(new MenuSpacer((int)(6 * Scale)));
                        EnableReorder(dd, path);
                        dd.ResumeLayout();
                    };
                }
                else
                {
                    item.Click += delegate
                    {
                        if (_reorderSuppress) return;
                        // Strg + Klick markiert (mehrere Einträge auf einmal herausziehen) statt zu starten
                        if (Control.ModifierKeys == Keys.Control && Sorting != null && AllowReorder) { ToggleMark(item); return; }
                        Shell.Open(path, "", folder);
                    };
                }
                items.Add(item);
            }

            if (all.Count == 0)
            {
                GlyphMenuItem empty = Item("(leer)", '\0', null);
                empty.Enabled = false;
                items.Add(empty);
            }
            if (openEntry)
            {
                items.Add(new ToolStripSeparator());
                items.Add(Item("Ordner öffnen", MenuHelper.Folder, delegate { Shell.Open(folder, "", ""); }));
            }
            LoadIconsLater(pending, iconSize);
        }

        // ------------------------------------------------------------------ Sortierung und eigene Reihenfolge

        /// <summary>Sortierung des Ordnermenüs (Settings: DockItem.Sort/Order). Ohne Angabe: nach Namen.</summary>
        public FolderSort Sorting;

        /// <summary>Verschieben per Ziehen erlaubt (aus bei „Einträge sperren“).</summary>
        public bool AllowReorder = true;

        /// <summary>Einträge eines Ordners in der gewünschten Reihenfolge (Wert: true = Unterordner).</summary>
        private List<KeyValuePair<string, bool>> SortEntries(string folder, List<string> dirs, List<string> files, bool foldersFirst)
        {
            int mode = Sorting != null ? Sorting.Mode : 0;
            // Name wie im Explorer (Zahlen nach ihrem Wert), bei Gleichstand nach Namen
            Comparison<string> byName = delegate (string a, string b) { return NativeSort.Compare(DisplayName(a), DisplayName(b)); };
            Comparison<string> cmp;
            switch (mode)
            {
                case 1: cmp = delegate (string a, string b) { return -byName(a, b); }; break;
                case 2: cmp = delegate (string a, string b) { int c = Changed(b).CompareTo(Changed(a)); return c != 0 ? c : byName(a, b); }; break;
                case 3: cmp = delegate (string a, string b) { int c = Changed(a).CompareTo(Changed(b)); return c != 0 ? c : byName(a, b); }; break;
                default: cmp = byName; break; // 0 und 4 (eigene Reihenfolge: Grundlage für noch nicht verschobene Einträge)
            }
            List<KeyValuePair<string, bool>> all = new List<KeyValuePair<string, bool>>();
            if (foldersFirst)
            {
                dirs.Sort(cmp);
                files.Sort(cmp);
                foreach (string d in dirs) all.Add(new KeyValuePair<string, bool>(d, true));
                foreach (string f in files) all.Add(new KeyValuePair<string, bool>(f, false));
            }
            else
            {
                foreach (string d in dirs) all.Add(new KeyValuePair<string, bool>(d, true));
                foreach (string f in files) all.Add(new KeyValuePair<string, bool>(f, false));
                all.Sort(delegate (KeyValuePair<string, bool> a, KeyValuePair<string, bool> b) { return cmp(a.Key, b.Key); });
            }
            if (mode != 4 || Sorting == null) return all;

            // eigene Reihenfolge: gemerkte Einträge in ihrer Reihenfolge, neue danach (nach Namen)
            List<string> order = Sorting.OrderOf(folder);
            List<KeyValuePair<string, bool>> result = new List<KeyValuePair<string, bool>>();
            foreach (string name in order)
            {
                int k = all.FindIndex(e => string.Equals(Path.GetFileName(e.Key.TrimEnd('\\')), name, StringComparison.OrdinalIgnoreCase));
                if (k < 0) continue; // gelöscht oder umbenannt
                result.Add(all[k]);
                all.RemoveAt(k);
            }
            result.AddRange(all);
            return result;
        }

        private static DateTime Changed(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        // Ziehen im Menü: Eintrag an eine andere Stelle verschieben
        private static bool _reorderSuppress;   // nach dem Ziehen: kein Start und kein Schließen durch das Loslassen
        private static DragGhost _ghost;        // schwebendes Symbol beim Herausziehen

        /// <summary>Gerade wird ein Eintrag gezogen (das Menü soll dabei nicht von selbst zuklappen).</summary>
        public static bool DragActive;

        /// <summary>Herausziehen: wird laufend gefragt, ob die Maus über dem Dock ist (dort landet der Eintrag im Dock).</summary>
        public Func<bool> DragOver;

        /// <summary>
        /// Herausziehen beendet: Pfade der losgelassenen Einträge, null = abgebrochen.
        /// Rückgabe true: Sie sind im Dock gelandet.
        /// </summary>
        public Func<List<string>, bool> DragDrop;

        /// <summary>Dateien wurden aus dem Ordner entfernt (Pfad und Inhalt, für „Entfernen rückgängig“).</summary>
        public Action<List<KeyValuePair<string, byte[]>>> Removed;

        // mit Strg + Klick markierte Einträge: werden gemeinsam herausgezogen
        private readonly HashSet<ToolStripItem> _marked = new HashSet<ToolStripItem>();

        private void ToggleMark(ToolStripItem item)
        {
            if (!_marked.Remove(item)) _marked.Add(item);
            if (item.Owner != null) item.Owner.Invalidate();
        }

        /// <summary>Was gezogen wird: alle markierten Einträge dieses Menüs, wenn der angefasste dazugehört, sonst nur er.</summary>
        private List<ToolStripItem> Dragged(ToolStrip strip, ToolStripItem item)
        {
            List<ToolStripItem> list = new List<ToolStripItem>();
            if (_marked.Contains(item))
                foreach (ToolStripItem it in strip.Items)
                    if (_marked.Contains(it)) list.Add(it);
            if (list.Count == 0) list.Add(item);
            return list;
        }

        /// <summary>Symbol des Eintrags an der Maus; remove: mit rotem Entfernen-Zeichen.</summary>
        private static void ShowGhost(ToolStripItem item, float scale, bool remove, int count)
        {
            int size = Math.Max(16, (int)Math.Round(32 * scale));
            int badge = Math.Max(14, (int)Math.Round(18 * scale));
            int w = size + badge / 2 + (int)(4 * scale);
            using (Bitmap img = new Bitmap(w, w, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    Image src = item.Image;
                    if (src != null)
                        using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix33 = 0.8f;
                            ia.SetColorMatrix(cm);
                            g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                        }
                    if (remove)
                    {
                        Rectangle br = new Rectangle(w - badge - 1, w - badge - 1, badge, badge);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(196, 43, 28))) g.FillEllipse(b, br);
                        using (Pen pen = new Pen(Color.White, Math.Max(1.5f, 2 * scale)))
                        {
                            float k = badge * 0.28f;
                            float cx = br.X + badge / 2f, cy = br.Y + badge / 2f;
                            g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                            g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
                        }
                    }
                    if (count > 1)
                    {
                        // Anzahl oben rechts
                        Rectangle cr = new Rectangle(w - badge - 1, 0, badge, badge);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 103, 192))) g.FillEllipse(b, cr);
                        using (Font f = new Font("Segoe UI", Math.Max(6f, badge * 0.55f), FontStyle.Bold, GraphicsUnit.Pixel))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                            g.DrawString(count > 9 ? "9+" : count.ToString(), f, Brushes.White, cr, sf);
                        }
                    }
                }
                if (_ghost == null || _ghost.IsDisposed) _ghost = new DragGhost();
                Point c = Cursor.Position;
                _ghost.Show(img, new Point(c.X + (w - size) / 2, c.Y + (w - size) / 2));
            }
        }

        private static void HideGhost()
        {
            if (_ghost != null && !_ghost.IsDisposed && _ghost.Visible) _ghost.Hide();
        }

        /// <summary>
        /// Einträge eines Ordnermenüs (bzw. Untermenüs) per Ziehen mit der Maus verschieben. Die neue Reihenfolge wird
        /// über Sorting gespeichert, die Sortierung wechselt auf „Eigene Reihenfolge“. Nur Dateien und Unterordner,
        /// keine Trennlinien oder „Ordner öffnen“.
        /// Aus dem Menü herausgezogen: Der Eintrag wird aus dem Ordner entfernt (Papierkorb) oder, über dem Dock
        /// losgelassen, dort eingefügt (DragOver/DragDrop) – siehe TakeOut. Mit Strg + Klick markierte Einträge
        /// werden gemeinsam gezogen.
        /// </summary>
        public void EnableReorder(ToolStripDropDown strip, string folder)
        {
            if (Sorting == null || !AllowReorder) return;
            // Menüs geben Mausereignisse nicht zuverlässig weiter. Deshalb verfolgt ein Timer, solange das Menü offen ist,
            // Maustaste und Mausposition selbst: Drücken über einem Eintrag, Ziehen, Loslassen.
            ToolStripItem down = null;
            Point downPt = Point.Empty;
            bool dragging = false;
            int insertAt = -1; // Index in strip.Items, vor dem eingefügt wird
            bool outside = false; // aus dem Menü herausgezogen
            MenuTheme colors = Colors;
            float scale = Scale;
            Timer track = new Timer { Interval = 15 };
            bool wasDown = true; // erst ein neues Drücken zählt (nicht das Loslassen nach dem Öffnen)
            strip.Opened += delegate { wasDown = true; track.Start(); };
            strip.Closed += delegate
            {
                track.Stop();
                if (dragging)
                {
                    DragActive = false;
                    HideGhost();
                    if (outside && DragDrop != null) DragDrop(null);
                }
                down = null; dragging = false; outside = false;
            };

            track.Tick += delegate
            {
                if (strip.IsDisposed || !strip.Visible) { track.Stop(); return; }
                Point cur = Cursor.Position;
                bool isDown = (Control.MouseButtons & MouseButtons.Left) != 0;
                if (down == null)
                {
                    // neues Drücken über einem Eintrag dieses Menüs?
                    if (isDown && !wasDown && strip.Bounds.Contains(cur))
                    {
                        ToolStripItem it = strip.GetItemAt(strip.PointToClient(cur));
                        if (it != null && it.Tag is string)
                        {
                            down = it;
                            downPt = cur;
                            dragging = false;
                            insertAt = -1;
                        }
                    }
                    wasDown = isDown;
                    return;
                }
                wasDown = isDown;
                if (isDown)
                {
                    if (!dragging)
                    {
                        Size drag = SystemInformation.DragSize;
                        if (Math.Abs(cur.Y - downPt.Y) < drag.Height * 2 && Math.Abs(cur.X - downPt.X) < drag.Width * 2) return;
                        dragging = true;
                        DragActive = true;
                        _reorderSuppress = true; // Loslassen startet nichts und schließt das Menü nicht
                    }
                    // deutlich neben dem Menü: herausziehen
                    Rectangle near = strip.Bounds;
                    near.Inflate((int)(24 * scale), (int)(24 * scale));
                    bool isOut = !near.Contains(cur) && Exists(down.Tag as string);
                    if (isOut)
                    {
                        bool dock = DragOver != null && DragOver();
                        ShowGhost(down, scale, !dock, Dragged(strip, down).Count); // folgt der Maus
                        outside = true;
                        if (insertAt != -1) { insertAt = -1; strip.Invalidate(); }
                        return;
                    }
                    if (outside)
                    {
                        outside = false;
                        HideGhost();
                        if (DragDrop != null) DragDrop(null);
                    }
                    int at = InsertIndex(strip, strip.PointToClient(cur));
                    if (at != insertAt) { insertAt = at; strip.Invalidate(); }
                    return;
                }
                // losgelassen
                ToolStripItem moved = down;
                int target = insertAt;
                bool wasDragging = dragging, wasOutside = outside;
                down = null;
                dragging = false;
                outside = false;
                insertAt = -1;
                strip.Invalidate();
                if (!wasDragging) return;
                DragActive = false;
                HideGhost();
                try
                {
                    if (wasOutside) TakeOut(strip, Dragged(strip, moved));
                    else MoveItem(strip, moved, target, folder);
                }
                finally
                {
                    // Das Loslassen (Klick) kann noch in der Warteschlange stehen – erst kurz danach wieder normal klicken lassen
                    Timer release = new Timer { Interval = 300 };
                    release.Tick += delegate { release.Stop(); release.Dispose(); _reorderSuppress = false; };
                    release.Start();
                }
            };
            strip.Disposed += delegate { track.Dispose(); };
            strip.Closing += delegate (object s, ToolStripDropDownClosingEventArgs e)
            {
                // nach dem Ziehen nicht schließen (Loslassen über einem Eintrag gilt sonst als Klick)
                if ((_reorderSuppress || dragging) && e.CloseReason != ToolStripDropDownCloseReason.Keyboard
                    && e.CloseReason != ToolStripDropDownCloseReason.CloseCalled) e.Cancel = true;
                // Strg + Klick markiert nur: Menü bleibt offen
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && Control.ModifierKeys == Keys.Control) e.Cancel = true;
            };
            strip.Closed += delegate
            {
                // Markierungen dieses Menüs gelten nur, solange es offen ist
                foreach (ToolStripItem it in strip.Items) _marked.Remove(it);
            };
            strip.Paint += delegate (object s, PaintEventArgs e)
            {
                // markierte Einträge (Strg + Klick): Rahmen in der Akzentfarbe
                if (_marked.Count > 0)
                    foreach (ToolStripItem it in strip.Items)
                    {
                        if (!_marked.Contains(it)) continue;
                        Rectangle b = it.Bounds;
                        b.Inflate(-(int)(5 * scale), -1);
                        using (SolidBrush markFill = new SolidBrush(Color.FromArgb(40, colors.Accent))) e.Graphics.FillRectangle(markFill, b);
                        using (Pen markPen = new Pen(colors.Accent, Math.Max(1f, scale))) e.Graphics.DrawRectangle(markPen, b.X, b.Y, b.Width - 1, b.Height - 1);
                    }
                if (!dragging || insertAt < 0) return;
                int y = LineY(strip, insertAt);
                using (Pen pen = new Pen(colors.Accent, Math.Max(2f, 2f * scale)))
                    e.Graphics.DrawLine(pen, (int)(10 * scale), y, strip.Width - (int)(10 * scale), y);
            };
        }

        private static bool FileExists(string path)
        {
            try { return path != null && File.Exists(path); } catch { return false; }
        }

        private static bool Exists(string path)
        {
            try { return path != null && (File.Exists(path) || Directory.Exists(path)); } catch { return false; }
        }

        /// <summary>
        /// Herausgezogen und losgelassen.
        /// Über dem Dock: Die Einträge werden dort eingefügt (DragDrop). Verknüpfungen wandern dabei aus dem Ordner in den
        /// Papierkorb (der Dock-Eintrag zeigt auf ihr Ziel); echte Dateien und Unterordner bleiben liegen, weil der
        /// Dock-Eintrag auf sie selbst zeigt.
        /// Woanders: Die Einträge kommen in den Papierkorb – Unterordner erst nach Rückfrage. Kleine Dateien
        /// (Verknüpfungen) werden vorher gemerkt, damit „Entfernen rückgängig“ sie zurücklegen kann (Removed).
        /// </summary>
        private void TakeOut(ToolStrip strip, List<ToolStripItem> moved)
        {
            List<string> paths = new List<string>();
            foreach (ToolStripItem it in moved) paths.Add((string)it.Tag);
            bool onDock = DragDrop != null && DragDrop(paths);
            List<KeyValuePair<string, byte[]>> removed = new List<KeyValuePair<string, byte[]>>();
            foreach (ToolStripItem it in moved)
            {
                string path = (string)it.Tag;
                bool dir = !FileExists(path);
                if (onDock)
                {
                    if (dir || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue; // bleibt im Ordner
                }
                else if (dir)
                {
                    if (MessageBox.Show("Den Ordner „" + DisplayName(path) + "“ mit seinem ganzen Inhalt in den Papierkorb legen?",
                            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        continue;
                }
                byte[] content = null;
                if (!dir && !onDock)
                {
                    try { if (new FileInfo(path).Length <= 1024 * 1024) content = File.ReadAllBytes(path); } catch { }
                }
                if (!DockNative.Recycle(path) || Exists(path)) continue; // nicht entfernt (z. B. schreibgeschützter Ordner)
                _marked.Remove(it);
                try { if (!strip.IsDisposed) strip.Items.Remove(it); } catch { }
                if (content != null) removed.Add(new KeyValuePair<string, byte[]>(path, content));
            }
            if (removed.Count > 0 && Removed != null) Removed(removed);
        }

        /// <summary>Einfügestelle unter der Maus: vor dem Eintrag, dessen obere Hälfte getroffen ist, sonst dahinter.</summary>
        private static int InsertIndex(ToolStrip strip, Point p)
        {
            int last = -1;
            for (int i = 0; i < strip.Items.Count; i++)
            {
                ToolStripItem it = strip.Items[i];
                if (!(it.Tag is string) || !it.Visible) continue;
                Rectangle b = it.Bounds;
                if (p.Y < b.Top + b.Height / 2) return i;
                last = i;
            }
            return last + 1;
        }

        private static int LineY(ToolStrip strip, int index)
        {
            if (index < strip.Items.Count && strip.Items[index].Tag is string) return strip.Items[index].Bounds.Top;
            // hinter dem letzten Eintrag
            for (int i = Math.Min(index, strip.Items.Count) - 1; i >= 0; i--)
                if (strip.Items[i].Tag is string) return strip.Items[i].Bounds.Bottom;
            return 0;
        }

        private void MoveItem(ToolStrip strip, ToolStripItem moved, int target, string folder)
        {
            int from = strip.Items.IndexOf(moved);
            if (from < 0 || target < 0) return;
            if (target == from || target == from + 1) return; // gleiche Stelle
            strip.SuspendLayout();
            strip.Items.RemoveAt(from);
            if (target > from) target--;
            strip.Items.Insert(Math.Min(target, strip.Items.Count), moved);
            strip.ResumeLayout();
            List<string> names = new List<string>();
            foreach (ToolStripItem it in strip.Items)
            {
                string path = it.Tag as string;
                if (path != null) names.Add(Path.GetFileName(path.TrimEnd('\\')));
            }
            Sorting.SetOrder(folder, names);
        }

        // ------------------------------------------------------------------ Symbole der Ordnermenüs

        /// <summary>
        /// Symbole bleiben zwischengespeichert (je Datei, Größe und Änderungszeit): das Menü öffnet sofort, beim ersten
        /// Mal werden fehlende Symbole im Hintergrund nachgeladen und erscheinen nach und nach.
        /// </summary>
        private static readonly Dictionary<string, Bitmap> IconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static Control _invoker;

        private static string CacheKey(string path, int size)
        {
            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
            return path + "|" + size + "|" + stamp;
        }

        private static Bitmap CachedIcon(string path, int size)
        {
            Bitmap b;
            return IconCache.TryGetValue(CacheKey(path, size), out b) ? new Bitmap(b) : null;
        }

        private static void LoadIconsLater(List<KeyValuePair<GlyphMenuItem, string>> pending, int size)
        {
            if (pending.Count == 0) return;
            if (_invoker == null)
            {
                _invoker = new Control();
                IntPtr h = _invoker.Handle; // Fenster für BeginInvoke im UI-Thread anlegen
                GC.KeepAlive(h);
            }
            Control invoker = _invoker;
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                foreach (KeyValuePair<GlyphMenuItem, string> p in pending)
                {
                    if (p.Key.IsDisposed) continue;
                    Bitmap icon = IconLoader.Small(p.Value, size);
                    string key = CacheKey(p.Value, size);
                    GlyphMenuItem item = p.Key;
                    try
                    {
                        invoker.BeginInvoke((MethodInvoker)delegate
                        {
                            if (!IconCache.ContainsKey(key)) IconCache[key] = icon;
                            if (item.IsDisposed) return;
                            item.Glyph = new Bitmap(icon);
                            item.Invalidate();
                        });
                    }
                    catch { icon.Dispose(); return; }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        /// <summary>Menü samt Symbolen freigeben (nach dem Schließen).</summary>
        public static void DisposeDeep(ToolStripItemCollection items)
        {
            ToolStripItem[] all = new ToolStripItem[items.Count];
            items.CopyTo(all, 0);
            foreach (ToolStripItem it in all)
            {
                GlyphMenuItem g = it as GlyphMenuItem;
                if (g != null)
                {
                    if (g.HasDropDownItems) DisposeDeep(g.DropDownItems);
                    if (g.Glyph != null) { g.Glyph.Dispose(); g.Glyph = null; }
                }
                it.Dispose();
            }
        }

        private static bool Visible(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & (FileAttributes.Hidden | FileAttributes.System)) == 0;
            }
            catch { return false; }
        }

        /// <summary>Name wie im Explorer: Verknüpfungen und Internetverknüpfungen ohne Endung.</summary>
        public static string DisplayName(string path)
        {
            string name = Path.GetFileName(path.TrimEnd('\\'));
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".lnk" || ext == ".url" || ext == ".pif" || ext == ".appref-ms" || ext == ".website")
                return Path.GetFileNameWithoutExtension(name);
            return name;
        }
    }

    /// <summary>
    /// Sortierung eines Ordnermenüs mit eigener Reihenfolge: je Ordner (relativ zum Ordner des Menüs) die Dateinamen
    /// in der gewünschten Reihenfolge. Gespeichert als ein Text „Unterordner\Name|Name|…“ (DockItem.Order).
    /// </summary>
    internal sealed class FolderSort
    {
        public int Mode;          // wie DockItem.Sort
        public string Root = "";  // Ordner des Menüs
        public List<string> Entries = new List<string>();
        /// <summary>Wird nach dem Verschieben aufgerufen (Mode ist dann 4, Text = neuer Wert für DockItem.Order).</summary>
        public Action<string> Changed;

        public FolderSort(int mode, string root, string order)
        {
            Mode = mode;
            Root = (root ?? "").TrimEnd('\\');
            foreach (string e in (order ?? "").Split('|'))
                if (e.Trim().Length > 0) Entries.Add(e.Trim());
        }

        private string Rel(string folder)
        {
            string f = (folder ?? "").TrimEnd('\\');
            if (f.Equals(Root, StringComparison.OrdinalIgnoreCase)) return "";
            if (f.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) return f.Substring(Root.Length + 1);
            return f;
        }

        private static string Dir(string entry)
        {
            int k = entry.LastIndexOf('\\');
            return k < 0 ? "" : entry.Substring(0, k);
        }

        /// <summary>Gemerkte Dateinamen eines Ordners in ihrer Reihenfolge.</summary>
        public List<string> OrderOf(string folder)
        {
            string rel = Rel(folder);
            List<string> names = new List<string>();
            foreach (string e in Entries)
                if (string.Equals(Dir(e), rel, StringComparison.OrdinalIgnoreCase))
                    names.Add(rel.Length == 0 ? e : e.Substring(rel.Length + 1));
            return names;
        }

        /// <summary>Neue Reihenfolge eines Ordners merken; die übrigen Ordner bleiben unverändert.</summary>
        public void SetOrder(string folder, List<string> names)
        {
            string rel = Rel(folder);
            Entries.RemoveAll(e => string.Equals(Dir(e), rel, StringComparison.OrdinalIgnoreCase));
            foreach (string n in names) Entries.Add(rel.Length == 0 ? n : rel + "\\" + n);
            Mode = 4;
            if (Changed != null) Changed(string.Join("|", Entries.ToArray()));
        }
    }

    /// <summary>
    /// Tippen im offenen Ordnermenü: springt zum Eintrag, der mit den getippten Buchstaben beginnt (sonst: sie enthält).
    /// Mehrere Buchstaben kurz nacheinander verfeinern die Suche, derselbe Buchstabe mehrmals blättert durch die Treffer.
    /// Der Eintrag wird nur markiert – Enter startet ihn. Gesucht wird im zuletzt aufgeklappten Untermenü.
    /// Als Nachrichtenfilter, weil Menüs Tastendrücke sonst als Tastenkürzel deuten und den Eintrag sofort starten.
    /// </summary>
    internal sealed class MenuTypeAhead : IMessageFilter
    {
        private const int WM_CHAR = 0x0102;
        private static readonly MethodInfo ChangeSelection = typeof(ToolStrip).GetMethod("ChangeSelection",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ToolStripItem) }, null);

        private readonly ToolStripDropDown _root;
        private string _typed = "";
        private DateTime _last = DateTime.MinValue;

        public MenuTypeAhead(ToolStripDropDown root)
        {
            _root = root;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_CHAR || _root.IsDisposed || !_root.Visible) return false;
            char c = char.ToLower((char)(m.WParam.ToInt64() & 0xFFFF));
            if (char.IsControl(c)) return false; // Enter, Esc usw. wie gewohnt
            DateTime now = DateTime.Now;
            bool fresh = _typed.Length == 0 || (now - _last).TotalMilliseconds > 1000;
            if (c == ' ' && fresh) return false;
            _last = now;

            ToolStripDropDown dd = Deepest(_root);
            List<ToolStripItem> entries = new List<ToolStripItem>();
            int selected = -1;
            foreach (ToolStripItem it in dd.Items)
            {
                if (!(it.Tag is string) || !it.Visible) continue;
                if (it.Selected) selected = entries.Count;
                entries.Add(it);
            }
            if (entries.Count == 0) return true;

            ToolStripItem hit = null;
            bool sameLetter = !fresh && _typed.Trim(c).Length == 0;
            if (sameLetter)
            {
                // derselbe Buchstabe noch einmal: nächster Eintrag mit diesem Anfangsbuchstaben
                _typed = c.ToString();
                for (int k = 1; k <= entries.Count && hit == null; k++)
                {
                    ToolStripItem it = entries[(Math.Max(selected, -1) + k + entries.Count) % entries.Count];
                    if (Name(it).StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase)) hit = it;
                }
            }
            else
            {
                string text = fresh ? c.ToString() : _typed + c;
                hit = Find(entries, text);
                if (hit == null && !fresh)
                {
                    // passt nicht mehr: mit diesem Buchstaben neu anfangen
                    text = c.ToString();
                    hit = Find(entries, text);
                }
                _typed = text;
            }
            if (hit != null) Select(dd, hit);
            return true;
        }

        private static string Name(ToolStripItem item)
        {
            return MenuBuilder.DisplayName((string)item.Tag);
        }

        private static ToolStripItem Find(List<ToolStripItem> entries, string text)
        {
            foreach (ToolStripItem it in entries)
                if (Name(it).StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return it;
            foreach (ToolStripItem it in entries)
                if (Name(it).IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0) return it;
            return null;
        }

        /// <summary>Das zuletzt aufgeklappte Untermenü (sonst das Menü selbst).</summary>
        private static ToolStripDropDown Deepest(ToolStripDropDown dd)
        {
            foreach (ToolStripItem it in dd.Items)
            {
                ToolStripDropDownItem di = it as ToolStripDropDownItem;
                if (di != null && di.HasDropDownItems && di.DropDown.Visible) return Deepest(di.DropDown);
            }
            return dd;
        }

        /// <summary>Eintrag markieren und in langen Menüs in den sichtbaren Bereich holen.</summary>
        private static void Select(ToolStripDropDown dd, ToolStripItem item)
        {
            try
            {
                if (ChangeSelection != null) { ChangeSelection.Invoke(dd, new object[] { item }); return; }
            }
            catch { }
            item.Select();
        }
    }

    /// <summary>Sortierung wie im Explorer (Zahlen nach ihrem Wert).</summary>
    internal static class NativeSort
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        public static int Compare(string a, string b)
        {
            try { return StrCmpLogicalW(a, b); }
            catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
        }
    }

    /// <summary>Programme, Dateien, Ordner und Webadressen starten.</summary>
    internal static class Shell
    {
        /// <summary>Startet ein Ziel. Rückgabe: Fehlermeldung oder null.</summary>
        public static string Open(string command, string arguments, string workingDirectory)
        {
            return Start(command, arguments, workingDirectory, 0, false);
        }

        public static string Start(string command, string arguments, string workingDirectory, int showCmd, bool asAdmin)
        {
            string cmd = IconLoader.Expand(command);
            if (cmd.Length == 0) return "Es ist kein Ziel eingetragen.";
            try
            {
                ProcessStartInfo psi;
                if (cmd.StartsWith("::", StringComparison.Ordinal))
                    psi = new ProcessStartInfo("explorer.exe", "shell:" + cmd); // Systemordner (z. B. Energieoptionen)
                else
                    psi = new ProcessStartInfo(cmd, Environment.ExpandEnvironmentVariables(arguments ?? ""));
                psi.UseShellExecute = true;
                string dir = IconLoader.Expand(workingDirectory);
                if (dir.Length == 0)
                {
                    try
                    {
                        if (Path.IsPathRooted(cmd) && File.Exists(cmd)) dir = Path.GetDirectoryName(cmd);
                    }
                    catch { }
                }
                if (dir.Length > 0 && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                psi.WindowStyle = showCmd == 1 ? ProcessWindowStyle.Minimized : showCmd == 2 ? ProcessWindowStyle.Maximized : ProcessWindowStyle.Normal;
                if (asAdmin) psi.Verb = "runas";
                Process.Start(psi);
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return null; // Windows-Nachfrage (Administrator) abgebrochen
                if (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3) return "Das Ziel wurde nicht gefunden:\n" + cmd;
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Speicherort im Explorer zeigen (Datei markiert).</summary>
        public static void ShowInExplorer(string path)
        {
            string p = IconLoader.Expand(path);
            try
            {
                if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else if (Directory.Exists(p)) Process.Start("explorer.exe", "\"" + p + "\"");
            }
            catch { }
        }
    }
}
