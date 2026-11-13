// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>Kleiner Dialog für eine Texteingabe (z. B. der Name eines neuen Profils), im Design der übrigen Fenster.</summary>
    internal sealed class InputDialog : Form
    {
        private readonly TextField _field;
        private readonly Label _error;

        /// <summary>Prüft die Eingabe: Fehlertext oder null, wenn sie in Ordnung ist.</summary>
        public Func<string, string> Check;

        public string Value { get { return _field.Text.Trim(); } }

        private InputDialog(string title, string prompt, string initial)
        {
            float sc;
            using (Graphics g = CreateGraphics()) sc = g.DpiX / 96f;
            Func<float, int> S = delegate (float v) { return (int)Math.Round(v * sc); };
            Palette p = Theme.Current;

            AutoScaleMode = AutoScaleMode.None;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true; // das Dock selbst hat keinen Eintrag in der Taskleiste
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Font = Theme.UiFont(13.5f * sc, FontStyle.Regular);
            BackColor = p.Back;
            ForeColor = p.Text;
            ClientSize = new Size(S(420), S(176));
            try { Icon = TrayContext.LoadIcon(32); } catch { }

            Label label = new Label();
            label.Text = prompt;
            label.AutoSize = true;
            label.UseMnemonic = false;
            label.ForeColor = p.Text;
            label.BackColor = p.Back;
            label.Location = new Point(S(24), S(22));
            Controls.Add(label);

            _field = new TextField();
            _field.Font = Font;
            _field.MaxLength = 40;
            _field.Bounds = new Rectangle(S(24), S(52), S(372), S(32));
            _field.Text = initial ?? "";
            Controls.Add(_field);

            _error = new Label();
            _error.AutoSize = false;
            _error.AutoEllipsis = true;
            _error.UseMnemonic = false;
            _error.BackColor = p.Back;
            _error.ForeColor = p.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
            _error.Bounds = new Rectangle(S(24), S(90), S(372), S(22));
            Controls.Add(_error);

            ModernButton ok = new ModernButton();
            ok.Text = "OK";
            ok.Primary = true;
            ok.Bounds = new Rectangle(S(396) - S(110), S(124), S(110), S(34));
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);
            ModernButton cancel = new ModernButton();
            cancel.Text = "Abbrechen";
            cancel.Bounds = new Rectangle(ok.Left - S(10) - S(110), S(124), S(110), S(34));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            KeyPreview = true;
        }

        private void Accept()
        {
            string err = Value.Length == 0 ? "Bitte einen Namen eingeben." : Check != null ? Check(Value) : null;
            if (err != null) { _error.Text = err; return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Accept(); return; }
            if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); return; }
            base.OnKeyDown(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { NativeMethods.UseDarkTitleBar(Handle, Theme.Current.Dark); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _field.Focus();
        }

        /// <summary>Fragt einen Text ab. Rückgabe: die Eingabe oder null (abgebrochen).</summary>
        public static string Ask(string title, string prompt, string initial, Func<string, string> check)
        {
            using (InputDialog d = new InputDialog(title, prompt, initial))
            {
                d.Check = check;
                return d.ShowDialog() == DialogResult.OK ? d.Value : null;
            }
        }
    }
}
