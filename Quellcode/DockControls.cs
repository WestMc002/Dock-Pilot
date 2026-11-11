// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Auswahlfeld im Windows-11-Stil: Knopf mit Text links und Pfeil rechts, Auswahl über ein Menü
    /// mit Auswahlpunkt (Vorlage: Wake on LAN, ModernButton.DropDown).
    /// </summary>
    internal sealed class ChoiceBox : ModernControl
    {
        private string[] _choices = new string[0];
        private int _index = -1;
        public event EventHandler SelectionChanged;

        public ChoiceBox() { TabStop = true; }

        protected override int BaseHeight { get { return 32; } }

        public string[] Choices
        {
            get { return _choices; }
            set { _choices = value ?? new string[0]; if (_index >= _choices.Length) _index = _choices.Length - 1; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = Math.Max(-1, Math.Min(_choices.Length - 1, value));
                if (v == _index) return;
                _index = v;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled || _choices.Length == 0) return;
            Focus();
            MenuBuilder mb = new MenuBuilder();
            mb.Menu.Items.Add(new MenuSpacer((int)Math.Round(2 * mb.Scale))); // oben gleich viel Luft wie unten (wie Ordnermenüs)
            for (int i = 0; i < _choices.Length; i++)
            {
                int idx = i;
                GlyphMenuItem item = mb.Item(_choices[i], '\0', delegate { SelectedIndex = idx; });
                item.Radio = true;
                item.Checked = i == _index;
                mb.Menu.Items.Add(item);
            }
            mb.End();
            // Menü so breit wie das Feld: die Einträge selbst verbreitern (MinimumSize macht nur das Fenster breiter,
            // die Maus-Markierung bliebe dann schmaler als das Menü)
            int itemW = Width - 2 - mb.Menu.Padding.Horizontal;
            foreach (ToolStripItem it in mb.Menu.Items)
            {
                if (!(it is GlyphMenuItem)) continue;
                Size pref = it.GetPreferredSize(Size.Empty);
                if (pref.Width >= itemW) continue;
                it.AutoSize = false;
                it.Size = new Size(itemW, pref.Height);
            }
            // klappt zu, wenn die Maus Menü und Feld eine Sekunde lang verlässt (wie die Menüs im Dock)
            Timer away = new Timer { Interval = 100 };
            DateTime since = DateTime.MinValue;
            away.Tick += delegate
            {
                if (mb.Menu.IsDisposed || !mb.Menu.Visible) return;
                Point p = Cursor.Position;
                if (mb.Menu.Bounds.Contains(p) || RectangleToScreen(ClientRectangle).Contains(p)) { since = DateTime.MinValue; return; }
                if (since == DateTime.MinValue) since = DateTime.Now;
                else if ((DateTime.Now - since).TotalMilliseconds >= 1000) mb.Menu.Close(ToolStripDropDownCloseReason.AppClicked);
            };
            mb.Menu.Closed += delegate
            {
                away.Stop();
                away.Dispose();
                BeginInvoke((MethodInvoker)delegate { mb.Menu.Dispose(); });
            };
            mb.Menu.Show(this, new Point(0, Height + 2), ToolStripDropDownDirection.BelowRight);
            away.Start();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter && e.Modifiers == Keys.Alt) { OnClick(EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { SelectedIndex = Math.Min(_choices.Length - 1, _index + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, _index - 1); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(r, 5f * sc))
            {
                Color fill = !Enabled ? p.Card : Pressed ? p.Back : Hover ? p.Hover : p.Input;
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = Focused && ShowFocusCues ? Theme.Blend(p.InputBorder, Theme.Accent, 0.6f) : p.InputBorder;
                using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
            string text = _index >= 0 && _index < _choices.Length ? _choices[_index] : "";
            Color tc = Enabled ? p.Text : Theme.WithAlpha(p.SubText, 140);
            int chevron = (int)Math.Round(28 * sc);
            using (Font f = Theme.UiFont(13.5f * sc, FontStyle.Regular))
                TextRenderer.DrawText(g, text, f, new Rectangle((int)(10 * sc), 0, Width - (int)(10 * sc) - chevron, Height), tc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (Font gf = new Font(Theme.IconFontName, 10f * sc, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "", gf, new Rectangle(Width - chevron, 0, chevron - (int)(6 * sc), Height), p.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// Feld für ein Tastenkürzel mit Zusatztasten (z. B. Strg + Alt + R). Klick, dann Tasten drücken;
    /// Entf bzw. Rücktaste löscht das Kürzel, Esc bricht ab.
    /// </summary>
    internal sealed class ComboHotkeyBox : ModernControl
    {
        public const int ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;
        private int _mods, _key;
        private bool _capturing;
        public event EventHandler HotkeyChanged;

        public ComboHotkeyBox() { TabStop = true; Cursor = Cursors.Hand; }

        protected override int BaseHeight { get { return 32; } }

        public int Modifiers { get { return _mods; } }
        public int Key { get { return _key; } }
        public bool Capturing { get { return _capturing; } }

        public void SetHotkey(int mods, int key)
        {
            _mods = mods;
            _key = key;
            Invalidate();
        }

        public static string Describe(int mods, int key)
        {
            if (key <= 0) return "Kein Tastenkürzel";
            StringBuilder sb = new StringBuilder();
            if ((mods & ModWin) != 0) sb.Append("Win + ");
            if ((mods & ModControl) != 0) sb.Append("Strg + ");
            if ((mods & ModAlt) != 0) sb.Append("Alt + ");
            if ((mods & ModShift) != 0) sb.Append("Umschalt + ");
            sb.Append(KeyNames.Get(key));
            return sb.ToString();
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            _capturing = !_capturing;
            Invalidate();
            base.OnClick(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _capturing = false;
            base.OnLostFocus(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (!_capturing)
            {
                if (Focused && (code == Keys.Enter || code == Keys.Space) && (keyData & Keys.Modifiers) == Keys.None)
                {
                    _capturing = true;
                    Invalidate();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (code == Keys.Escape && (keyData & Keys.Modifiers) == Keys.None) { _capturing = false; Invalidate(); return true; }
            if ((code == Keys.Delete || code == Keys.Back) && (keyData & Keys.Modifiers) == Keys.None)
            {
                Set(0, 0);
                return true;
            }
            // reine Zusatztasten abwarten
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin ||
                code == Keys.LControlKey || code == Keys.RControlKey || code == Keys.LShiftKey || code == Keys.RShiftKey ||
                code == Keys.LMenu || code == Keys.RMenu)
                return true;
            int mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= ModControl;
            if ((keyData & Keys.Alt) != 0) mods |= ModAlt;
            if ((keyData & Keys.Shift) != 0) mods |= ModShift;
            Set(mods, (int)code);
            return true;
        }

        private void Set(int mods, int key)
        {
            _mods = mods;
            _key = key;
            _capturing = false;
            Invalidate();
            if (HotkeyChanged != null) HotkeyChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(box, 5f * sc))
            {
                Color fill = !Enabled ? p.Card : (Hover && !_capturing ? p.Hover : p.Input);
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = _capturing ? Theme.Accent : (Focused ? Theme.Blend(p.InputBorder, Theme.Accent, 0.5f) : p.InputBorder);
                using (Pen pen = new Pen(border, _capturing ? 2f * sc : 1f)) g.DrawPath(pen, path);
            }
            string text = _capturing ? "Tasten drücken …" : Describe(_mods, _key);
            Color tc = !Enabled ? Theme.WithAlpha(p.SubText, 120) : (_capturing ? Theme.Accent : _key > 0 ? p.Text : p.SubText);
            using (Font f = Theme.UiFont(13.5f * sc, _capturing || _key <= 0 ? FontStyle.Regular : FontStyle.Bold))
                TextRenderer.DrawText(g, text, f, new Rectangle((int)(6 * sc), 0, Width - (int)(12 * sc), Height), tc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Navigation links im Einstellungsfenster nach den globalen Vorgaben (Vorlage: Wake on LAN / Monitor Dimmer):
    /// oben ☰ zum Aus- und Einklappen und die Stecknadel zum Anpinnen, darunter die Seiten.
    /// Einträge 40 hoch, 4 Abstand, 10 Rand; ausgewählt grau hinterlegt mit Akzentbalken links; Symbole 14 pt.
    /// Eingeklappt (schmal) nur die Symbole. Breite und Anpinnen steuert das Fenster.
    /// </summary>
    internal sealed class NavList : Control
    {
        private const char GlyphMenu = '\uE700', GlyphPin = '\uE718', GlyphUnpin = '\uE77A';
        private const int HitMenu = -2, HitPin = -3, HitExtras = -4;
        private const char GlyphExtras = '\uE713';
        private bool _extrasActive;

        private readonly float _sc;
        private readonly List<KeyValuePair<char, string>> _items = new List<KeyValuePair<char, string>>();
        private int _sel = 0, _hover = -1, _down = -1;
        private bool _pinned = true, _overlay;
        private readonly ToolTip _tip = new ModernToolTip();

        public event EventHandler SelectedChanged, MenuClicked, PinClicked, ExtrasClicked;

        /// <summary>Knopf „Extras“ unten hervorheben, solange sein Menü offen ist.</summary>
        public bool ExtrasActive
        {
            get { return _extrasActive; }
            set { _extrasActive = value; Invalidate(); }
        }

        /// <summary>Lage des Knopfs „Extras“ (für das Menü rechts darüber).</summary>
        public Rectangle ExtrasBounds { get { return ExtrasRect; } }

        public NavList(float scale)
        {
            _sc = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public void Add(char glyph, string text)
        {
            _items.Add(new KeyValuePair<char, string>(glyph, text));
            Invalidate();
        }

        /// <summary>Angepinnt: Stecknadel zeigt „Lösen“.</summary>
        public bool Pinned
        {
            get { return _pinned; }
            set { _pinned = value; Invalidate(); }
        }

        /// <summary>Liegt ausgeklappt über dem Inhalt: feine Linie am rechten Rand.</summary>
        public bool Overlay
        {
            get { return _overlay; }
            set { _overlay = value; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _sel; }
            set
            {
                if (value < 0 || value >= _items.Count || value == _sel) return;
                _sel = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        /// <summary>Ausgeklappt (Text sichtbar)?</summary>
        private bool Wide { get { return Width > S(120); } }

        private Rectangle MenuRect { get { return new Rectangle(S(10), S(4), S(48), S(40)); } }

        private Rectangle PinRect { get { return new Rectangle(Width - S(10) - S(36), S(4) + S(2), S(36), S(36)); } }

        /// <summary>Unten: „Extras“ (Zahnrad), wie die Einträge 40 hoch mit 10 Rand.</summary>
        private Rectangle ExtrasRect { get { return new Rectangle(S(10), Height - S(10) - S(40), Width - S(20), S(40)); } }

        private Rectangle ItemRect(int i)
        {
            int top = S(4) + S(40) + S(10);
            return new Rectangle(S(10), top + i * (S(40) + S(4)), Width - S(20), S(40));
        }

        private int HitTest(Point p)
        {
            if (MenuRect.Contains(p)) return HitMenu;
            if (Wide && PinRect.Contains(p)) return HitPin;
            if (ExtrasRect.Contains(p)) return HitExtras;
            for (int i = 0; i < _items.Count; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h == _hover) return;
            _hover = h;
            Invalidate();
            string tip = h == HitMenu ? "Navigation aus- bzw. einklappen"
                : h == HitPin ? (_pinned ? "Navigation lösen (einklappbar)" : "Navigation anpinnen")
                : h == HitExtras && !Wide ? "Extras"
                : h >= 0 && !Wide ? _items[h].Value : null;
            _tip.SetToolTip(this, tip);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = _down = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _down = HitTest(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = e.Button == MouseButtons.Left ? HitTest(e.Location) : -1;
            bool same = h == _down;
            _down = -1;
            Invalidate();
            if (!same) return;
            if (h == HitMenu) { if (MenuClicked != null) MenuClicked(this, EventArgs.Empty); }
            else if (h == HitPin) { if (PinClicked != null) PinClicked(this, EventArgs.Empty); }
            else if (h == HitExtras) { if (ExtrasClicked != null) ExtrasClicked(this, EventArgs.Empty); }
            else if (h >= 0)
            {
                if (h == _sel) { if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } // auch nochmal: klappt die Navigation ein
                else SelectedIndex = h;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = Theme.Current;
            g.Clear(p.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color hoverFill = p.Dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(234, 234, 234);
            Color downFill = p.Dark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(226, 226, 226);
            Color selFill = p.Dark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(232, 232, 232);

            // ☰ und Stecknadel
            Fill(g, MenuRect, _down == HitMenu ? downFill : _hover == HitMenu ? hoverFill : Color.Empty);
            DrawGlyph(g, GlyphMenu, 14f, new Rectangle(MenuRect.X + S(14), MenuRect.Y, S(20), MenuRect.Height), p.Text);
            if (Wide)
            {
                Fill(g, PinRect, _down == HitPin ? downFill : _hover == HitPin ? hoverFill : Color.Empty);
                DrawGlyph(g, _pinned ? GlyphUnpin : GlyphPin, 12f, PinRect, p.Text);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                Rectangle r = ItemRect(i);
                Fill(g, r, i == _sel ? selFill : _down == i ? downFill : _hover == i ? hoverFill : Color.Empty);
                if (i == _sel)
                {
                    RectangleF bar = new RectangleF(r.X, r.Y + (r.Height - S(16)) / 2f, S(3), S(16));
                    using (GraphicsPath path = Theme.RoundRect(bar, S(1.5f)))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, path);
                }
                DrawGlyph(g, _items[i].Key, 14f, new Rectangle(r.X + S(14), r.Y, S(20), r.Height), p.Text);
                int tx = r.X + S(46);
                if (Wide && r.Right - S(8) - tx > S(20))
                    using (Font f = Theme.UiFont(9.5f * 96f / 72f * _sc, i == _sel ? FontStyle.Bold : FontStyle.Regular))
                        TextRenderer.DrawText(g, _items[i].Value, f, new Rectangle(tx, r.Y, r.Right - S(8) - tx, r.Height), p.Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            // unten: Extras
            {
                Rectangle r = ExtrasRect;
                Fill(g, r, _extrasActive ? selFill : _down == HitExtras ? downFill : _hover == HitExtras ? hoverFill : Color.Empty);
                DrawGlyph(g, GlyphExtras, 14f, new Rectangle(r.X + S(14), r.Y, S(20), r.Height), p.Text);
                int tx = r.X + S(46);
                if (Wide && r.Right - S(8) - tx > S(20))
                    using (Font f = Theme.UiFont(9.5f * 96f / 72f * _sc, FontStyle.Regular))
                        TextRenderer.DrawText(g, "Extras", f, new Rectangle(tx, r.Y, r.Right - S(8) - tx, r.Height), p.Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            if (_overlay)
                using (Pen pen = new Pen(p.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(224, 224, 224)))
                    g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }

        private void Fill(Graphics g, Rectangle r, Color c)
        {
            if (c == Color.Empty) return;
            using (GraphicsPath path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f), S(5)))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        /// <summary>
        /// Symbol wie in WindowGrid (NavButton): Kästchen pt × 96/72 × Skalierung, das Zeichen darin nach seinen
        /// tatsächlich gezeichneten Pixeln mittig (MenuHelper.Glyph) – sonst wirken die Symbole zu groß.
        /// </summary>
        private void DrawGlyph(Graphics g, char glyph, float pt, Rectangle r, Color color)
        {
            int size = (int)Math.Round(pt * 96f / 72f * _sc);
            using (Image img = MenuHelper.Glyph(glyph, color, size))
            {
                if (img == null) return;
                g.DrawImage(img, r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            }
        }
    }

    /// <summary>Kleines Farbfeld (Vorschau der Schriftfarbe), Klick öffnet die Farbauswahl.</summary>
    internal sealed class LabelSwatch : Control
    {
        private readonly float _sc;
        public Color Color = Color.White;

        public LabelSwatch(float scale)
        {
            _sc = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = Theme.Current;
            g.Clear(p.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(r, 5f * _sc))
            {
                using (SolidBrush b = new SolidBrush(Enabled ? Color : Theme.WithAlpha(Color, 90))) g.FillPath(b, path);
                using (Pen pen = new Pen(p.InputBorder, 1f)) g.DrawPath(pen, path);
            }
        }
    }
}
