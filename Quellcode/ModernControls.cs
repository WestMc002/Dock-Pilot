// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>Basis für selbst gezeichnete Steuerelemente.</summary>
    internal abstract class ModernControl : Control
    {
        protected bool Hover;
        protected bool Pressed;

        protected ModernControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
        }

        protected float UiFactor
        {
            get { return Height / (float)BaseHeight; }
        }

        protected abstract int BaseHeight { get; }

        protected Graphics Prepare(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            return g;
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// <summary>Ein-/Aus-Schalter im Windows-11-Stil.</summary>
    internal sealed class ToggleSwitch : ModernControl
    {
        private bool _checked;
        private float _pos;
        private readonly Timer _anim;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            _anim = new Timer();
            _anim.Interval = 12;
            _anim.Tick += delegate
            {
                float target = _checked ? 1f : 0f;
                _pos += (target - _pos) * 0.35f;
                if (Math.Abs(target - _pos) < 0.02f) { _pos = target; _anim.Stop(); }
                Invalidate();
            };
            TabStop = true;
        }

        protected override int BaseHeight { get { return 22; } }

        [DefaultValue(false)]
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (IsHandleCreated && Visible) _anim.Start();
                else _pos = value ? 1f : 0f;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF track = new RectangleF(1f * sc, 1f * sc, Width - 2f * sc - 1, Height - 2f * sc - 1);
            float r = track.Height / 2f;
            Color accent = Enabled ? Theme.Accent : p.ToggleOff;

            using (GraphicsPath path = Theme.RoundRect(track, r))
            {
                Color offFill = Hover ? p.Hover : p.Input;
                Color fill = Theme.Blend(offFill, accent, _pos);
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (_pos < 0.99f)
                {
                    using (Pen pen = new Pen(Theme.WithAlpha(p.ToggleOff, (int)(255 * (1 - _pos))), Math.Max(1f, sc)))
                        g.DrawPath(pen, path);
                }
            }

            float knobOff = track.Height * 0.5f;
            float knobOn = track.Height * 0.64f;
            if (Pressed) { knobOff *= 1.1f; knobOn *= 1.05f; }
            float knob = knobOff + (knobOn - knobOff) * _pos;
            float minX = track.X + track.Height / 2f;
            float maxX = track.Right - track.Height / 2f;
            float cx = minX + (maxX - minX) * _pos;
            float cy = track.Y + track.Height / 2f;
            Color knobColor = Theme.Blend(p.ToggleOff, Theme.ContrastText(accent), _pos);
            using (SolidBrush b = new SolidBrush(knobColor))
                g.FillEllipse(b, cx - knob / 2f, cy - knob / 2f, knob, knob);

            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath path = Theme.RoundRect(RectangleF.Inflate(track, -0.5f, -0.5f), r))
                using (Pen pen = new Pen(Theme.WithAlpha(p.Text, 90), 1f) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Textfeld im Stil der übrigen Eingaben: abgerundeter Rahmen, Innenabstand, Akzent bei Fokus.</summary>
    internal sealed class TextField : ModernControl
    {
        private readonly TextBox _box;

        public TextField()
        {
            Cursor = Cursors.IBeam;
            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.GotFocus += delegate { Invalidate(); };
            _box.LostFocus += delegate { Invalidate(); };
            Controls.Add(_box);
        }

        protected override int BaseHeight { get { return 32; } }

        public override string Text
        {
            get { return _box.Text; }
            set { _box.Text = value; }
        }

        public int MaxLength
        {
            get { return _box.MaxLength; }
            set { _box.MaxLength = value; }
        }

        public override Font Font
        {
            get { return base.Font; }
            set { base.Font = value; _box.Font = value; LayoutBox(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _box.Focus();
        }

        private void LayoutBox()
        {
            int padX = (int)Math.Round(10f * UiFactor);
            int h = _box.PreferredHeight;
            _box.Bounds = new Rectangle(padX, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - padX * 2), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            if (_box.BackColor != p.Input) _box.BackColor = p.Input;
            if (_box.ForeColor != p.Text) _box.ForeColor = p.Text;
            bool focus = _box.Focused;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(box, 5f * sc))
            {
                using (SolidBrush b = new SolidBrush(p.Input)) g.FillPath(b, path);
                using (Pen pen = new Pen(focus ? Theme.Accent : p.InputBorder, focus ? 1.5f * sc : 1f)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Zahleneingabe mit − und + (Mausrad und Pfeiltasten funktionieren auch).</summary>
    internal sealed class Stepper : ModernControl
    {
        private int _value;
        private int _min;
        private int _max = 100;
        private int _hoverPart; // -1 minus, 0 mitte, 1 plus
        private readonly Timer _repeat;
        private int _repeatDir;
        /// <summary>Schrittweite der Pfeile und des Mausrads (Pfeiltasten: immer 1).</summary>
        public int Step = 1;
        public event EventHandler ValueChanged;

        public Stepper()
        {
            _repeat = new Timer();
            _repeat.Tick += delegate
            {
                _repeat.Interval = 60;
                Value += _repeatDir * Step;
            };
            TabStop = true;
        }

        protected override int BaseHeight { get { return 32; } }

        public int Minimum { get { return _min; } set { _min = value; Value = _value; } }
        public int Maximum { get { return _max; } set { _max = value; Value = _value; } }
        public string Suffix { get; set; }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private int PartAt(int x)
        {
            int btn = Height;
            if (x < btn) return -1;
            if (x > Width - btn) return 1;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int p = PartAt(e.X);
            if (p != _hoverPart) { _hoverPart = p; Invalidate(); }
            Cursor = p == 0 ? Cursors.Default : Cursors.Hand;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverPart = 0;
            _repeat.Stop();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                int p = PartAt(e.X);
                if (p != 0)
                {
                    _repeatDir = p;
                    Value += p * Step;
                    _repeat.Interval = 400;
                    _repeat.Start();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _repeat.Stop();
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value += e.Delta > 0 ? Step : -Step;
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) { Value++; e.Handled = true; }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) { Value--; e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { Value += 10; e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { Value -= 10; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float r = 5f * sc;
            using (GraphicsPath path = Theme.RoundRect(box, r))
            {
                using (SolidBrush b = new SolidBrush(p.Input)) g.FillPath(b, path);
                Color border = Focused ? Theme.Accent : p.InputBorder;
                using (Pen pen = new Pen(border, Focused ? 1.5f * sc : 1f)) g.DrawPath(pen, path);
            }

            float btn = Height;
            DrawButton(g, new RectangleF(3f * sc, 3f * sc, btn - 6f * sc, Height - 6f * sc), false, _hoverPart == -1 && Hover, _value <= _min, p, sc);
            DrawButton(g, new RectangleF(Width - btn + 3f * sc, 3f * sc, btn - 6f * sc, Height - 6f * sc), true, _hoverPart == 1 && Hover, _value >= _max, p, sc);

            string text = _value.ToString() + (string.IsNullOrEmpty(Suffix) ? "" : " " + Suffix);
            using (Font f = Theme.UiFont(14f * sc, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(p.Text))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, b, new RectangleF(btn, 0, Width - 2 * btn, Height), sf);
        }

        private static void DrawButton(Graphics g, RectangleF r, bool plus, bool hover, bool disabled, Palette p, float sc)
        {
            if (hover && !disabled)
            {
                using (GraphicsPath path = Theme.RoundRect(r, 4f * sc))
                using (SolidBrush b = new SolidBrush(p.Hover)) g.FillPath(b, path);
            }
            Color c = disabled ? Theme.WithAlpha(p.SubText, 90) : p.Text;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, len = 5f * sc;
            using (Pen pen = new Pen(c, 1.6f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx - len, cy, cx + len, cy);
                if (plus) g.DrawLine(pen, cx, cy - len, cx, cy + len);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Schieberegler.</summary>
    internal sealed class Slider : ModernControl
    {
        private int _value;
        private int _min;
        private int _max = 100;
        private bool _drag;
        public event EventHandler ValueChanged;

        public Slider() { TabStop = true; }

        protected override int BaseHeight { get { return 24; } }

        public int Minimum { get { return _min; } set { _min = value; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = value; Invalidate(); } }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private float TrackLeft { get { return Height / 2f; } }
        private float TrackRight { get { return Width - Height / 2f; } }

        private void SetFromX(int x)
        {
            float t = (x - TrackLeft) / Math.Max(1f, TrackRight - TrackLeft);
            t = Math.Max(0f, Math.Min(1f, t));
            Value = (int)Math.Round(_min + t * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { _drag = true; SetFromX(e.X); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value += e.Delta > 0 ? 5 : -5;
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value++; e.Handled = true; }
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value--; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            float cy = Height / 2f;
            float th = 4f * sc;
            float t = _max > _min ? (_value - _min) / (float)(_max - _min) : 0f;
            float x = TrackLeft + (TrackRight - TrackLeft) * t;

            using (GraphicsPath path = Theme.RoundRect(new RectangleF(TrackLeft, cy - th / 2f, TrackRight - TrackLeft, th), th / 2f))
            using (SolidBrush b = new SolidBrush(p.InputBorder)) g.FillPath(b, path);
            if (x > TrackLeft + 0.5f)
            {
                using (GraphicsPath path = Theme.RoundRect(new RectangleF(TrackLeft, cy - th / 2f, x - TrackLeft, th), th / 2f))
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            }

            float outer = 20f * sc;
            float inner = (Hover || _drag ? 12f : 10f) * sc;
            if (_drag) inner = 9f * sc;
            using (SolidBrush b = new SolidBrush(p.Input)) g.FillEllipse(b, x - outer / 2f, cy - outer / 2f, outer, outer);
            using (Pen pen = new Pen(p.InputBorder, 1f)) g.DrawEllipse(pen, x - outer / 2f, cy - outer / 2f, outer, outer);
            using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, x - inner / 2f, cy - inner / 2f, inner, inner);
        }
    }

    /// <summary>Feld zum Zuweisen einer Taste: anklicken, dann gewünschte Taste drücken (Esc bricht ab).</summary>
    internal sealed class HotkeyBox : ModernControl
    {
        private int _key = 0x20;
        private bool _capturing;
        public event EventHandler KeyChanged;

        public HotkeyBox() { TabStop = true; }

        protected override int BaseHeight { get { return 32; } }

        public int Key
        {
            get { return _key; }
            set { _key = value; Invalidate(); }
        }

        public bool Capturing
        {
            get { return _capturing; }
        }

        private void StartCapture()
        {
            if (!Enabled) return;
            Focus();
            _capturing = true;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            if (_capturing) { _capturing = false; Invalidate(); }
            else StartCapture();
            base.OnClick(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _capturing = false;
            base.OnLostFocus(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) _capturing = false;
            base.OnEnabledChanged(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (_capturing)
            {
                if (code == Keys.Escape)
                {
                    _capturing = false;
                    Invalidate();
                    return true;
                }
                int vk = (int)code;
                if (vk > 6)
                {
                    _key = vk;
                    _capturing = false;
                    Invalidate();
                    if (KeyChanged != null) KeyChanged(this, EventArgs.Empty);
                }
                return true;
            }
            if (Focused && (code == Keys.Enter || code == Keys.Space) && (keyData & Keys.Modifiers) == Keys.None)
            {
                StartCapture();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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

            string text = _capturing ? "Taste drücken …" : KeyNames.Get(_key);
            Color tc = !Enabled ? Theme.WithAlpha(p.SubText, 120) : (_capturing ? Theme.Accent : p.Text);
            using (Font f = Theme.UiFont(13.5f * sc, _capturing ? FontStyle.Regular : FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(tc))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, f, b, new RectangleF(6f * sc, 0, Width - 12f * sc, Height), sf);
        }
    }

    /// <summary>Flache Schaltfläche, optional in Akzentfarbe.</summary>
    internal sealed class ModernButton : ModernControl
    {
        public bool Primary { get; set; }

        /// <summary>Optionales Symbol aus "Segoe Fluent Icons" (z. B. "\uE8A5" Dokument, "\uE8B7" Ordner).</summary>
        public string Glyph { get; set; }

        public ModernButton() { TabStop = true; }

        protected override int BaseHeight { get { return 34; } }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, border, text;
            if (Primary)
            {
                fill = Theme.Accent;
                if (Hover) fill = Theme.Blend(fill, p.Dark ? Color.White : Color.Black, 0.10f);
                if (Pressed) fill = Theme.Blend(fill, p.Dark ? Color.Black : Color.White, 0.18f);
                border = fill;
                text = Theme.ContrastText(fill);
            }
            else
            {
                fill = Pressed ? p.Back : (Hover ? p.Hover : p.Input);
                border = p.InputBorder;
                text = p.Text;
            }
            using (GraphicsPath path = Theme.RoundRect(r, 5f * sc))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (Pen pen = new Pen(Focused && ShowFocusCues ? p.Text : border, 1f)) g.DrawPath(pen, path);
            }
            using (Font f = Theme.UiFont(13.5f * sc, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(text))
            {
                if (string.IsNullOrEmpty(Glyph))
                {
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(Text, f, b, new RectangleF(0, 0, Width, Height), sf);
                }
                else
                {
                    // Symbol + Text als Gruppe zentrieren
                    using (Font gf = new Font(Theme.IconFontName, 14f * sc, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        SizeF gs = g.MeasureString(Glyph, gf, PointF.Empty, StringFormat.GenericTypographic);
                        SizeF ts = g.MeasureString(Text, f, PointF.Empty, StringFormat.GenericTypographic);
                        float gap = 10f * sc;
                        float x = (Width - (gs.Width + gap + ts.Width)) / 2f;
                        g.DrawString(Glyph, gf, b, x, (Height - gs.Height) / 2f, StringFormat.GenericTypographic);
                        g.DrawString(Text, f, b, x + gs.Width + gap, (Height - ts.Height) / 2f, StringFormat.GenericTypographic);
                    }
                }
            }
        }
    }

    /// <summary>Kleine Pille mit der Versionsnummer – Klick öffnet das Info-Fenster.</summary>
    internal sealed class VersionPill : ModernControl
    {
        public VersionPill() { TabStop = true; }

        protected override int BaseHeight { get { return 21; } }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color accent = Theme.Accent;
            Color textColor = p.Dark ? Theme.Blend(accent, Color.White, 0.45f) : Theme.Blend(accent, Color.Black, 0.25f);
            using (GraphicsPath path = Theme.RoundRect(r, r.Height / 2f))
            {
                using (SolidBrush b = new SolidBrush(Theme.WithAlpha(accent, Hover ? 55 : 28))) g.FillPath(b, path);
                using (Pen pen = new Pen(Theme.WithAlpha(accent, Hover ? 140 : 70), 1f)) g.DrawPath(pen, path);
            }
            // Text waagrecht und senkrecht mittig, mit TextRenderer ohne Innenabstand (wie gemessen)
            using (Font f = Theme.UiFont(10.5f * sc, FontStyle.Regular))
                TextRenderer.DrawText(g, Text, f, new Rectangle(0, 0, Width, Height), textColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Farbfeld, öffnet beim Klick die Farbauswahl.</summary>
    internal sealed class ColorSwatch : ModernControl
    {
        private Color _color = Color.Teal;
        public event EventHandler ColorChanged;

        public ColorSwatch() { TabStop = true; }

        protected override int BaseHeight { get { return 28; } }

        public Color Color
        {
            get { return _color; }
            set { _color = value; Invalidate(); if (ColorChanged != null) ColorChanged(this, EventArgs.Empty); }
        }

        protected override void OnClick(EventArgs e)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = _color;
                dlg.FullOpen = true;
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Color = dlg.Color;
            }
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prepare(e);
            Palette p = Theme.Current;
            float sc = UiFactor;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(r, 5f * sc))
            {
                using (SolidBrush b = new SolidBrush(Hover ? p.Hover : p.Input)) g.FillPath(b, path);
                using (Pen pen = new Pen(p.InputBorder, 1f)) g.DrawPath(pen, path);
            }
            RectangleF sw = new RectangleF(4f * sc, 4f * sc, Height - 8f * sc, Height - 8f * sc);
            Color c = Enabled ? _color : Theme.Blend(_color, p.Input, 0.6f);
            using (GraphicsPath path = Theme.RoundRect(sw, 3f * sc))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, path);

            string hex = "#" + _color.R.ToString("X2") + _color.G.ToString("X2") + _color.B.ToString("X2");
            using (Font f = Theme.UiFont(12.5f * sc, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Enabled ? p.Text : p.SubText))
            using (StringFormat sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString(hex, f, b, new RectangleF(sw.Right + 6f * sc, 0, Width, Height), sf);
        }
    }

    /// <summary>Abgerundete "Karte" als Gruppierung.</summary>
    internal sealed class Card : Panel
    {
        public string Title { get; set; }
        public float UiScale = 1f;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(r, 8f * UiScale))
            {
                using (SolidBrush b = new SolidBrush(p.Card)) g.FillPath(b, path);
                using (Pen pen = new Pen(p.CardBorder, 1f)) g.DrawPath(pen, path);
            }
            if (!string.IsNullOrEmpty(Title))
            {
                using (Font f = Theme.UiFont(14.5f * UiScale, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(p.Text))
                    g.DrawString(Title, f, b, 18f * UiScale, 14f * UiScale);
            }
        }
    }

    /// <summary>Fußzeile wie in allen Projekten: links Copyright, rechts GS-Logo mit Name und Website (anklickbar).</summary>
    internal sealed class SignatureFooter : Control
    {
        public const string Url = "https://www.westmc002.de";
        private const string Name1 = "Günther Schoder";
        private const string Name2 = "www.westmc002.de";
        private static Image _logo;
        private static bool _logoLoaded;
        private readonly float k;
        private Rectangle _link;
        private bool _hover;

        public SignatureFooter(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>GS-Logo: eingebettet in der EXE, sonst Resources\GS-Logo.png (fehlt beides, steht nur der Text da).</summary>
        public static Image Logo
        {
            get
            {
                if (_logoLoaded) return _logo;
                _logoLoaded = true;
                try
                {
                    using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("GS-Logo.png"))
                    {
                        if (s != null)
                        {
                            using (Image img = Image.FromStream(s)) _logo = new Bitmap(img);
                            return _logo;
                        }
                    }
                    string p = Path.Combine(Application.StartupPath, "Resources", "GS-Logo.png");
                    if (File.Exists(p))
                    {
                        using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (Image img = Image.FromStream(fs))
                            _logo = new Bitmap(img);
                    }
                }
                catch { _logo = null; }
                return _logo;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Flache Zeile in Fensterfarbe: kein eigener Hintergrund, kein Rahmen, keine runden Ecken

            Color muted = p.SubText;
            int pad = (int)(28 * k);
            int right = Width - pad;
            using (Font f1 = new Font("Segoe UI Semibold", 9.5f))
            using (Font f2 = new Font("Segoe UI", 9f, _hover ? FontStyle.Underline : FontStyle.Regular))
            using (Font f3 = new Font("Segoe UI", 9f))
            {
                TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                Size s1 = TextRenderer.MeasureText(g, Name1, f1, Size.Empty, flags);
                Size s2 = TextRenderer.MeasureText(g, Name2, f2, Size.Empty, flags);
                int tw = Math.Max(s1.Width, s2.Width);
                int gap = (int)(2 * k);
                int th = s1.Height + gap + s2.Height;
                int top = (Height - th) / 2;
                int tx = right - tw;
                TextRenderer.DrawText(g, Name1, f1, new Point(tx, top), p.Text, flags);
                TextRenderer.DrawText(g, Name2, f2, new Point(tx, top + s1.Height + gap), _hover ? Theme.Accent : muted, flags);
                _link = new Rectangle(tx, top, tw, th);

                Image logo = Logo;
                if (logo != null)
                {
                    int lh = (int)(26 * k);
                    int lw = logo.Width * lh / Math.Max(1, logo.Height);
                    Rectangle r = new Rectangle(tx - (int)(10 * k) - lw, (Height - lh) / 2, lw, lh);
                    g.DrawImage(logo, r);
                    _link = Rectangle.Union(_link, r);
                }

                string left = "© " + DateTime.Now.Year + " · Dock Pilot";
                Size s3 = TextRenderer.MeasureText(g, left, f3, Size.Empty, flags);
                TextRenderer.DrawText(g, left, f3, new Point(pad, (Height - s3.Height) / 2), muted, flags);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _link.Contains(e.Location);
            if (h != _hover) { _hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _link.Contains(e.Location))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }); }
                catch { }
            }
            base.OnMouseClick(e);
        }
    }
}
