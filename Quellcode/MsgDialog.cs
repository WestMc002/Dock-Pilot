// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Ersatz für System.Windows.Forms.MessageBox im Programmdesign (globale Vorgabe „Dialogfenster“, Referenz Sync Pilot).
    /// Gleiche Show-Überladungen; weil die Klasse im Namensraum DockPilot liegt, nutzt der vorhandene Code sie automatisch.
    /// Fenstertitel ist immer der Programmname.
    /// </summary>
    internal static class MessageBox
    {
        public static DialogResult Show(string text)
        { return Show(null, text, null, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(string text, string caption)
        { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        { return Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        { return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        { return Show(null, text, caption, buttons, icon, def); }

        public static DialogResult Show(IWin32Window owner, string text)
        { return Show(owner, text, null, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption)
        { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons)
        { return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        { return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            try
            {
                if (owner == null)
                {
                    // ohne Owner: das aktive Programmfenster, wenn es im selben Thread läuft
                    Form active = Form.ActiveForm;
                    if (active != null && !active.InvokeRequired && active.Visible) owner = active;
                }
                using (MsgDialog d = new MsgDialog(text, buttons, icon, def, owner == null))
                    return owner != null ? d.ShowDialog(owner) : d.ShowDialog();
            }
            catch
            {
                // Rückfall, falls das eigene Fenster nicht angezeigt werden kann
                return System.Windows.Forms.MessageBox.Show(owner, text, AppInfo.Name, buttons, icon, def);
            }
        }
    }

    /// <summary>Meldungs- und Rückfragefenster im Programmdesign (hell/dunkel, Akzentfarbe, Programmschaltflächen).</summary>
    internal sealed class MsgDialog : Form
    {
        private enum Sign { None, Question, Info, Warning, Error }

        private readonly float k;
        private readonly Sign _sign;
        private readonly DialogResult _escapeResult;
        private readonly List<ModernButton> _buttons = new List<ModernButton>();
        private ModernButton _default;
        private Rectangle _signRect;
        private bool _chosen; // Schaltfläche oder Esc gewählt (sonst wurde mit X geschlossen)

        public MsgDialog(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def, bool standalone)
        {
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            Palette p = Theme.Current;

            Text = AppInfo.Name;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = standalone; // ohne Programmfenster sonst nicht auffindbar
            StartPosition = standalone ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            if (standalone) TopMost = true;
            KeyPreview = true;
            BackColor = p.Back;
            ForeColor = p.Text;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            switch (icon)
            {
                case MessageBoxIcon.Question: _sign = Sign.Question; break;
                case MessageBoxIcon.Information: _sign = Sign.Info; break;   // = Asterisk
                case MessageBoxIcon.Warning: _sign = Sign.Warning; break;    // = Exclamation
                case MessageBoxIcon.Error: _sign = Sign.Error; break;        // = Hand, Stop
                default: _sign = Sign.None; break;
            }

            // Schaltflächen in Windows-Reihenfolge; Esc bzw. X wie bei Windows
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel:
                    AddButton("OK", DialogResult.OK); AddButton("Abbrechen", DialogResult.Cancel);
                    _escapeResult = DialogResult.Cancel; break;
                case MessageBoxButtons.YesNo:
                    AddButton("Ja", DialogResult.Yes); AddButton("Nein", DialogResult.No);
                    _escapeResult = DialogResult.No; break;
                case MessageBoxButtons.YesNoCancel:
                    AddButton("Ja", DialogResult.Yes); AddButton("Nein", DialogResult.No); AddButton("Abbrechen", DialogResult.Cancel);
                    _escapeResult = DialogResult.Cancel; break;
                case MessageBoxButtons.RetryCancel:
                    AddButton("Wiederholen", DialogResult.Retry); AddButton("Abbrechen", DialogResult.Cancel);
                    _escapeResult = DialogResult.Cancel; break;
                case MessageBoxButtons.AbortRetryIgnore:
                    AddButton("Abbrechen", DialogResult.Abort); AddButton("Wiederholen", DialogResult.Retry); AddButton("Ignorieren", DialogResult.Ignore);
                    _escapeResult = DialogResult.Abort; break;
                default:
                    AddButton("OK", DialogResult.OK);
                    _escapeResult = DialogResult.OK; break;
            }
            int index = def == MessageBoxDefaultButton.Button3 ? 2 : def == MessageBoxDefaultButton.Button2 ? 1 : 0;
            _default = _buttons[Math.Min(index, _buttons.Count - 1)];
            _default.Primary = true; // Vorauswahl hervorgehoben, hat den Fokus, Enter löst sie aus

            // --- Maße: Innenabstand 24, Symbol 32, Breite nach Text (420–600), Schaltflächen rechtsbündig
            int pad = S(24), signSize = S(32), gap = S(16);
            int textLeft = pad + (_sign != Sign.None ? signSize + gap : 0);
            Font font = Theme.UiFont(13.5f * k, FontStyle.Regular);
            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            string body = (text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            int natural = TextRenderer.MeasureText(body, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width;
            int clientW = Math.Max(S(420), Math.Min(S(600), textLeft + natural + pad));
            int textW = clientW - textLeft - pad;
            Size ts = TextRenderer.MeasureText(body, font, new Size(textW, int.MaxValue), flags);

            Label label = new Label();
            label.Text = body;
            label.Font = font;
            label.UseMnemonic = false;
            label.ForeColor = p.Text;
            label.BackColor = p.Back;
            label.Bounds = new Rectangle(textLeft, pad + (ts.Height < signSize ? (signSize - ts.Height) / 2 : 0), textW, ts.Height + S(2));
            Controls.Add(label);
            _signRect = new Rectangle(pad, pad, signSize, signSize);

            int contentBottom = Math.Max(label.Bottom, _sign != Sign.None ? _signRect.Bottom : 0);
            int by = contentBottom + S(24), bh = S(34), bgap = S(8);
            int x = clientW - pad;
            for (int i = _buttons.Count - 1; i >= 0; i--)
            {
                ModernButton b = _buttons[i];
                int bw = Math.Max(S(110), TextRenderer.MeasureText(b.Text, Theme.UiFont(13.5f * k, FontStyle.Regular)).Width + S(32));
                b.Bounds = new Rectangle(x - bw, by, bw, bh);
                x -= bw + bgap;
            }
            ClientSize = new Size(clientW, by + bh + pad);
            // Tab-Reihenfolge links nach rechts
            for (int i = 0; i < _buttons.Count; i++) _buttons[i].TabIndex = i;
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void AddButton(string text, DialogResult result)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.BackColor = Theme.Current.Back;
            b.Tag = result;
            b.Click += delegate { Choose(result); };
            _buttons.Add(b);
            Controls.Add(b);
        }

        private void Choose(DialogResult result)
        {
            _chosen = true;
            DialogResult = result;
            Close();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size; // feste Größe trotz normalem Rahmen
            MaximumSize = Size;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _default.Focus();
            if (TopMost) Activate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseDarkTitleBar(Handle, Theme.IsDark); // bei jedem HandleCreated (globale Vorgabe)
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Choose(_escapeResult); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !(ActiveControl is ModernButton)) { Choose((DialogResult)_default.Tag); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_chosen) DialogResult = _escapeResult; // X wie Esc
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_sign == Sign.None) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color fill;
            string mark;
            switch (_sign)
            {
                case Sign.Question: fill = Theme.Accent; mark = "?"; break;
                case Sign.Info: fill = Theme.Accent; mark = "i"; break;
                case Sign.Warning: fill = Color.FromArgb(247, 99, 12); mark = "!"; break;
                default: fill = Color.FromArgb(196, 43, 28); mark = "×"; break;
            }
            using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, _signRect);
            using (Font f = Theme.UiFont(19f * k, FontStyle.Bold))
            using (SolidBrush w = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(mark, f, w, new RectangleF(_signRect.X, _signRect.Y + S(1), _signRect.Width, _signRect.Height), sf);
        }

        // Feste Größe: am Rand keine Pfeile zum Größe-Ändern anzeigen
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
    }

    /// <summary>
    /// Tooltip im Programmdesign (globale Vorgabe „Weitere Fenster“, Referenz Wake on LAN): Kartenfarbe, 1 px Rand,
    /// Text in Textfarbe, Segoe UI 9 pt, Innenabstand 10 × 6, Umbruch bei 400 (je × Skalierung).
    /// </summary>
    internal sealed class ModernToolTip : ToolTip
    {
        private static readonly Font TipFont = new Font("Segoe UI", 9f);
        // NoPadding: sonst rechnet und zeichnet Windows einen Zuschlag, und der Rand rechts wird größer als links
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding;
        private const int PadX = 8, PadY = 5; // Innenabstand waagrecht × senkrecht (× Skalierung), an allen Seiten gleich

        public ModernToolTip()
        {
            OwnerDraw = true;
            AutoPopDelay = 15000; // 15 s sichtbar statt 5 s
            Popup += delegate (object s, PopupEventArgs e)
            {
                string text = GetToolTip(e.AssociatedControl) ?? "";
                // mit Graphics messen – ohne rechnet Windows trotz NoPadding einen Zuschlag ein
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float k = g.DpiX / 96f;
                    Size size = TextRenderer.MeasureText(g, text, TipFont, new Size((int)(400 * k), 0), Flags);
                    e.ToolTipSize = new Size(size.Width + 2 * (int)(PadX * k) + 1, size.Height + 2 * (int)(PadY * k)); // 1 px Breite als Reserve
                }
            };
            Draw += delegate (object s, DrawToolTipEventArgs e)
            {
                float k = e.Graphics.DpiX / 96f;
                Palette p = Theme.Current;
                using (SolidBrush back = new SolidBrush(p.Card)) e.Graphics.FillRectangle(back, e.Bounds);
                using (Pen pen = new Pen(p.CardBorder)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                Rectangle r = new Rectangle((int)(PadX * k), (int)(PadY * k), e.Bounds.Width - (int)(PadX * k), e.Bounds.Height - (int)(PadY * k));
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, r, p.Text, Flags);
            };
        }
    }
}
