// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Vorschau vor dem Wiederherstellen einer Sicherung: zeigt alle Einträge mit Ziel und Parametern, hebt
    /// „Als Administrator“, Tastenkürzel und Netzwerkpfade hervor und fragt, ob diese mit übernommen werden (Standard: nein).
    /// </summary>
    internal sealed class BackupDialog : Form
    {
        private readonly float _sc;
        private readonly Palette _p;
        private readonly ToggleSwitch _admin, _hotkeys, _network;
        private readonly List<Row> _rows = new List<Row>();
        private readonly Font _small, _badge;

        private sealed class Row
        {
            public string Title, Detail, Admin, Hotkey, Network;
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        private BackupDialog(string file, BackupContent content)
        {
            using (Graphics g = CreateGraphics()) _sc = g.DpiX / 96f;
            _p = Theme.Current;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Sicherung wiederherstellen";
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true; // das Dock selbst hat keinen Eintrag in der Taskleiste
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.UiFont(13.5f * _sc, FontStyle.Regular);
            _small = Theme.UiFont(12f * _sc, FontStyle.Regular);
            _badge = Theme.UiFont(11.5f * _sc, FontStyle.Regular);
            BackColor = _p.Back;
            ForeColor = _p.Text;
            try { Icon = TrayContext.LoadIcon(32); } catch { }

            bool several = content.Docks.Count > 1;
            foreach (KeyValuePair<int, List<DockItem>> dock in content.Docks)
                foreach (DockItem it in dock.Value)
                {
                    if (it.IsSeparator) continue;
                    Row r = new Row();
                    r.Title = it.Title.Length > 0 ? it.Title : "(ohne Name)";
                    string where = several ? "Dock " + dock.Key + " · " : "";
                    if (it.Kind == ItemKind.Ordnermenue) r.Detail = where + "Ordnermenü: " + it.Folder;
                    else r.Detail = where + (it.Command + (it.Arguments.Length > 0 ? " " + it.Arguments : ""));
                    if (it.Kind == ItemKind.Programm && it.RunAsAdmin) r.Admin = "Als Administrator";
                    if (it.HotkeyKey > 0) r.Hotkey = ComboHotkeyBox.Describe(it.HotkeyModifiers, it.HotkeyKey);
                    if (Backup.UsesNetwork(it, true)) r.Network = "Netzwerk";
                    _rows.Add(r);
                }

            int w = S(600), x = S(24), inner = w - 2 * x, y = S(20);
            Label head = MakeLabel("Die Sicherung ersetzt alle Einträge und Einstellungen von Dock Pilot. Bitte prüfen Sie die Einträge:", Font, _p.Text);
            head.Bounds = new Rectangle(x, y, inner, S(44));
            Controls.Add(head);
            y += S(46);
            int entries = content.EntryCount;
            string info = Path.GetFileName(file) + " · " + entries + (entries == 1 ? " Eintrag" : " Einträge") +
                          (several ? " in " + content.Docks.Count + " Docks" : "");
            Label sub = MakeLabel(info, _small, _p.SubText);
            sub.AutoEllipsis = true;
            sub.Bounds = new Rectangle(x, y, inner, S(22));
            Controls.Add(sub);
            y += S(30);

            // Liste der Einträge in einer Karte
            Panel card = new Panel();
            card.BackColor = _p.Card;
            card.Padding = new Padding(1);
            card.Bounds = new Rectangle(x, y, inner, S(300));
            card.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(_p.CardBorder)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            ListBox list = new ListBox();
            list.BorderStyle = BorderStyle.None;
            list.BackColor = _p.Card;
            list.ForeColor = _p.Text;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Math.Min(255, S(50));
            list.IntegralHeight = false;
            list.SelectionMode = SelectionMode.None;
            list.Dock = DockStyle.Fill;
            if (_rows.Count == 0) list.Items.Add(new Row { Title = "Keine Einträge", Detail = "" });
            else foreach (Row r in _rows) list.Items.Add(r);
            list.DrawItem += DrawRow;
            list.HandleCreated += delegate { if (_p.Dark) try { SetWindowTheme(list.Handle, "DarkMode_Explorer", null); } catch { } };
            card.Controls.Add(list);
            Controls.Add(card);
            y += card.Height + S(16);

            // Schalter: „Als Administrator“ und Tastenkürzel nur auf ausdrücklichen Wunsch übernehmen
            int admins = content.AdminCount, keys = content.HotkeyCount, nets = content.NetworkCount;
            _admin = AddToggle("„Als Administrator“ übernehmen (" + admins + (admins == 1 ? " Eintrag)" : " Einträge)"), x, inner, ref y, admins > 0);
            bool dockKey = content.DockHotkeyKey > 0;
            string keyText = "Tastenkürzel übernehmen (";
            if (keys > 0) keyText += keys + (keys == 1 ? " Eintrag" : " Einträge") + (dockKey ? ", " : "");
            if (dockKey) keyText += "Dock: " + ComboHotkeyBox.Describe(content.DockHotkeyModifiers, content.DockHotkeyKey);
            _hotkeys = AddToggle(keyText + ")", x, inner, ref y, keys > 0 || dockKey);
            _network = AddToggle("Einträge mit Netzwerkpfaden übernehmen (" + nets + (nets == 1 ? " Eintrag)" : " Einträge)"), x, inner, ref y, nets > 0);
            if (admins > 0 || keys > 0 || dockKey || nets > 0 || content.SkippedIcons > 0)
            {
                // nur nennen, was in dieser Sicherung tatsächlich vorkommt
                List<string> off = new List<string>();
                if (admins > 0) off.Add("Einträge mit „Als Administrator“ kommen ohne Administratorrechte");
                if (keys > 0) off.Add("Einträge mit Tastenkürzel kommen ohne Kürzel");
                if (dockKey) off.Add("das Tastenkürzel des Docks bleibt wie auf diesem PC");
                if (nets > 0) off.Add("Einträge mit Netzwerkpfaden fehlen ganz (ein Symbol von einem Netzwerkpfad wird nur entfernt)");
                string hint = off.Count > 0
                    ? "Ausgeschaltet: " + string.Join("; ", off.ToArray()) + ". Einschalten nur bei Sicherungen, denen Sie vertrauen."
                    : "";
                if (content.SkippedIcons > 0)
                    hint += (hint.Length > 0 ? " " : "") + content.SkippedIcons + (content.SkippedIcons == 1 ? " Datei im Symbolordner ist kein Bild und wird" : " Dateien im Symbolordner sind keine Bilder und werden") + " nicht übernommen.";
                Label h = MakeLabel(hint, _small, _p.SubText);
                Size need = TextRenderer.MeasureText(hint, _small, new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                h.Bounds = new Rectangle(x, y, inner, need.Height + S(4));
                Controls.Add(h);
                y += h.Height + S(8);
            }
            y += S(12);

            ModernButton ok = new ModernButton();
            ok.Text = "Wiederherstellen";
            ok.Primary = true;
            ok.Bounds = new Rectangle(x + inner - S(150), y, S(150), S(34));
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);
            ModernButton cancel = new ModernButton();
            cancel.Text = "Abbrechen";
            cancel.Bounds = new Rectangle(ok.Left - S(10) - S(120), y, S(120), S(34));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            ActiveControl = cancel; // riskante Rückfrage: Vorauswahl „Abbrechen“
            ClientSize = new Size(w, y + S(34) + S(22));
            KeyPreview = true;
        }

        private Label MakeLabel(string text, Font font, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = _p.Back;
            l.UseMnemonic = false;
            l.AutoSize = false;
            return l;
        }

        private ToggleSwitch AddToggle(string text, int x, int inner, ref int y, bool show)
        {
            ToggleSwitch t = new ToggleSwitch();
            t.Checked = false;
            if (!show) return t;
            t.Size = new Size(S(44), S(22));
            t.BackColor = _p.Back;
            t.Location = new Point(x + inner - t.Width - S(2), y + (S(32) - t.Height) / 2);
            Controls.Add(t);
            Label l = MakeLabel(text, Font, _p.Text);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Cursor = Cursors.Hand;
            l.Bounds = new Rectangle(x, y, t.Left - S(8) - x, S(32));
            l.Click += delegate { t.Checked = !t.Checked; };
            Controls.Add(l);
            y += S(36);
            return t;
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            ListBox list = (ListBox)sender;
            Row r = (Row)list.Items[e.Index];
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(_p.Card)) g.FillRectangle(back, e.Bounds);
            if (e.Index > 0)
                using (Pen line = new Pen(_p.CardBorder)) g.DrawLine(line, e.Bounds.Left + S(12), e.Bounds.Top, e.Bounds.Right - S(12), e.Bounds.Top);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int left = e.Bounds.Left + S(14), right = e.Bounds.Right - S(14);
            // Hervorhebungen rechts: Administrator in Orange, Tastenkürzel in Akzentfarbe
            int bx = right;
            Color orange = _p.Dark ? Color.FromArgb(255, 185, 90) : Color.FromArgb(180, 90, 0);
            Color teal = _p.Dark ? Color.FromArgb(90, 205, 195) : Color.FromArgb(0, 118, 108);
            foreach (KeyValuePair<string, Color> b in new[] {
                new KeyValuePair<string, Color>(r.Network, teal),
                new KeyValuePair<string, Color>(r.Hotkey, Theme.Accent),
                new KeyValuePair<string, Color>(r.Admin, orange) })
            {
                if (string.IsNullOrEmpty(b.Key)) continue;
                TextFormatFlags badgeFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                Size sz = TextRenderer.MeasureText(g, b.Key, _badge, new Size(int.MaxValue, int.MaxValue), badgeFlags);
                Rectangle box = new Rectangle(bx - sz.Width - S(18), e.Bounds.Top + (e.Bounds.Height - S(22)) / 2, sz.Width + S(18), S(22));
                using (SolidBrush fill = new SolidBrush(Theme.Blend(_p.Card, b.Value, 0.14f))) g.FillRectangle(fill, box);
                TextRenderer.DrawText(g, b.Key, _badge, new Rectangle(box.X + S(8), box.Y + (box.Height - sz.Height) / 2, sz.Width + S(2), sz.Height),
                    b.Value, badgeFlags);
                bx = box.Left - S(6);
            }
            int width = Math.Max(S(40), bx - S(8) - left);
            int h1 = TextRenderer.MeasureText(g, "Ag", Font, Size.Empty, flags).Height;
            int h2 = TextRenderer.MeasureText(g, "Ag", _small, Size.Empty, flags).Height;
            int top = e.Bounds.Top + (e.Bounds.Height - h1 - h2 - S(2)) / 2;
            TextRenderer.DrawText(g, r.Title, Font, new Rectangle(left, top, width, h1), _p.Text, flags);
            if (!string.IsNullOrEmpty(r.Detail))
                TextRenderer.DrawText(g, r.Detail, _small, new Rectangle(left, top + h1 + S(2), width, h2), _p.SubText, flags | TextFormatFlags.PathEllipsis);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); return; }
            base.OnKeyDown(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { NativeMethods.UseDarkTitleBar(Handle, _p.Dark); } catch { }
        }

        // feste Größe mit normalem Fensterrahmen (globale Vorgabe „Fensterrahmen“)
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        private const int WM_NCHITTEST = 0x0084, HTCLIENT = 1, HTLEFT = 10, HTBOTTOMRIGHT = 17;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
            {
                int hit = m.Result.ToInt32();
                if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT) m.Result = (IntPtr)HTCLIENT;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _small.Dispose(); _badge.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Vorschau zeigen. Rückgabe true = wiederherstellen, mit den gewählten Schaltern.</summary>
        public static bool Ask(IWin32Window owner, string file, BackupContent content, out bool withAdmin, out bool withHotkeys, out bool withNetwork)
        {
            using (BackupDialog d = new BackupDialog(file, content))
            {
                bool ok = (owner != null ? d.ShowDialog(owner) : d.ShowDialog()) == DialogResult.OK;
                withAdmin = ok && d._admin.Checked;
                withHotkeys = ok && d._hotkeys.Checked;
                withNetwork = ok && d._network.Checked;
                return ok;
            }
        }
    }
}
