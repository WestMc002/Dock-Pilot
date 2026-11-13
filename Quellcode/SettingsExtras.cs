// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Einstellungsfenster: Design, Navigation, Extras-Menü, Schließen. Teil von SettingsForm (siehe SettingsForm.cs).</summary>
    internal sealed partial class SettingsForm
    {
        // ------------------------------------------------------------------ Design

        private void ApplyTheme()
        {
            Palette p = Theme.Current;
            if (!Demo.Active) Theme.Accent = Theme.SystemAccent;
            BackColor = p.Back;
            ForeColor = p.Text;
            ApplyTheme(this, p);
            if (IsHandleCreated) NativeMethods.UseDarkTitleBar(Handle, p.Dark);
            InvalidateAll(this);
        }

        private static void ApplyTheme(Control parent, Palette p)
        {
            foreach (Control c in parent.Controls)
            {
                bool onCard = parent is Card;
                if (!(c is Card)) c.BackColor = onCard ? p.Card : p.Back;
                if (c is Label)
                {
                    string tag = c.Tag as string;
                    c.ForeColor = tag == "error" ? StatusColor(false, p.Dark) : tag == "ok" ? StatusColor(true, p.Dark) : tag == "sub" ? p.SubText : p.Text;
                }
                if (c.Controls.Count > 0) ApplyTheme(c, p);
            }
        }

        private static void InvalidateAll(Control c)
        {
            c.Invalidate();
            foreach (Control child in c.Controls) InvalidateAll(child);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)ApplyTheme);
        }

        // ------------------------------------------------------------------ Extras (unten in der Navigation)

        /// <summary>„Extras“: Menü rechts über dem Knopf (Info, Anleitung, Fehlerprotokoll, Sichern/Wiederherstellen, Standardwerte).</summary>
        private void ShowExtrasMenu()
        {
            if (_extrasMenu != null) { MenuBuilder.DisposeDeep(_extrasMenu.Items); _extrasMenu.Dispose(); _extrasMenu = null; }
            MenuBuilder mb = new MenuBuilder();
            mb.Menu.Items.Add(mb.Item("Info über Dock Pilot", MenuHelper.Info, delegate { AboutDialog.ShowFor(this); }));
            mb.Menu.Items.Add(mb.Item("Anleitung öffnen", MenuHelper.Document, delegate
            {
                OpenFile(Path.Combine(AppInfo.ExeDirectory, "Anleitung.txt"), "Die Anleitung wurde nicht gefunden.");
            }));
            mb.Menu.Items.Add(mb.Item("Fehlerprotokoll öffnen", MenuHelper.Log, delegate
            {
                OpenFile(Path.Combine(Settings.DataDirectory, "Fehler.log"), "Es gibt noch kein Fehlerprotokoll.");
            }));
            mb.Separator();
            mb.Menu.Items.Add(mb.Item("Dock und Einstellungen sichern …", '\uE74E', ExportBackup));
            mb.Menu.Items.Add(mb.Item("Sicherung wiederherstellen …", MenuHelper.Download, RestoreBackup));
            mb.Separator();
            mb.Menu.Items.Add(mb.Item("Standardwerte", MenuHelper.Refresh, ResetDefaults));
            mb.End();
            ContextMenuStrip m = mb.Menu;
            _extrasMenu = m;
            _nav.ExtrasActive = true; // ausgewählt, solange das Menü offen ist
            m.Closed += delegate
            {
                if (!_nav.IsDisposed) _nav.ExtrasActive = false;
                CloseNav();
            };
            Rectangle r = _nav.ExtrasBounds;
            m.Show(_nav, new Point(r.Right + S(4), r.Bottom), ToolStripDropDownDirection.AboveRight);
        }

        private void OpenFile(string path, string missing)
        {
            try
            {
                if (!File.Exists(path)) { ShowStatus(missing, false); return; }
                Shell.Open(path, "", "");
            }
            catch (Exception ex) { ShowStatus(ex.Message, false); }
        }

        /// <summary>Einträge, Einstellungen und Symbole in eine Datei sichern (der zuletzt gespeicherte Stand).</summary>
        private void ExportBackup()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Dock und Einstellungen sichern";
                dlg.Filter = Backup.Filter;
                dlg.FileName = "Dock Pilot Sicherung " + DateTime.Now.ToString("yyyy-MM-dd") + Backup.Extension;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string err = Backup.Export(dlg.FileName);
                if (err != null) ShowStatus("Sicherung fehlgeschlagen: " + err, false);
                else ShowStatus("Sicherung gespeichert.", true);
            }
        }

        /// <summary>Sicherung einspielen: ersetzt Einträge und Einstellungen (mit Rückfrage im Tray-Teil).</summary>
        private void RestoreBackup()
        {
            if (ImportBackup == null || !ImportBackup(this)) return;
            _edit.CopyFrom(_original);
            LoadValues(false);
            _baseline = Snapshot();
            ShowStatus("Sicherung wiederhergestellt.", true);
        }

        /// <summary>Alle Seiten auf die Standardwerte setzen; gilt erst nach „Speichern“.</summary>
        private void ResetDefaults()
        {
            Settings d = new Settings();
            d.FirstRun = false;
            d.UpdateCheck = _edit.UpdateCheck;
            d.LastUpdateCheck = _edit.LastUpdateCheck;
            d.NavPinned = _edit.NavPinned;
            _edit.CopyFrom(d);
            LoadValues(false);
            ShowStatus("Standardwerte geladen – mit „Speichern“ übernehmen.", true);
        }

        /// <summary>Gibt es Änderungen, die noch nicht gespeichert sind? (Zustand der Felder mit dem gespeicherten vergleichen)</summary>
        private bool HasChanges()
        {
            return _baseline != null && (Snapshot() != _baseline || _autostart.Checked != _autostartLoaded);
        }

        /// <summary>
        /// Stand der Felder als Text. Verglichen wird mit dem Stand direkt nach dem Laden (nicht mit der Datei),
        /// damit angepasste Werte – z. B. eine nicht mehr installierte Schrift – nicht als Änderung zählen.
        /// </summary>
        private string Snapshot()
        {
            ReadValues();
            _edit.FirstRun = _original.FirstRun;
            _edit.LastUpdateCheck = _original.LastUpdateCheck;
            _edit.UpdateCheck = _original.UpdateCheck;
            _edit.NavPinned = _original.NavPinned;
            _edit.Profile = _original.Profile; // wird im Tray-Menü umgeschaltet
            return _edit.ToIniText();
        }

        /// <summary>
        /// Schließen mit ungespeicherten Änderungen (X, Alt+F4): erst nachfragen. Seitenwechsel verwerfen nichts –
        /// die Änderungen werden über alle Seiten gesammelt (reines Einstellungsfenster, siehe globale Vorgaben).
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_discard && !Demo.Active && e.CloseReason == CloseReason.UserClosing && HasChanges())
            {
                DialogResult r = MessageBox.Show(this, "Die Änderungen sind noch nicht gespeichert.\n\nÄnderungen verwerfen?",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
            _discard = false;
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseDarkTitleBar(Handle, Theme.IsDark);
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _btnSave.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool capturing = _hotkey.Capturing;
            if (e.KeyCode == Keys.Escape && !capturing) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None && !capturing && !(ActiveControl is ModernButton) && !(ActiveControl is ComboHotkeyBox))
            {
                Save();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_extrasMenu != null) { MenuBuilder.DisposeDeep(_extrasMenu.Items); _extrasMenu.Dispose(); }
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                Updater.AvailableChanged -= _onUpdateChanged;
                if (_logo != null && _logo.Image != null) _logo.Image.Dispose();
                _navTimer.Dispose();
                _navHover.Dispose();
                _liveTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
