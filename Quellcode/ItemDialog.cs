// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Eigenschaften eines Eintrags: Name, Ziel, Parameter, Symbol bzw. Ordner des Ordnermenüs.</summary>
    internal sealed class ItemDialog : Form
    {
        private readonly DockItem _item;
        private readonly float _sc;
        public DockItem Result { get { return _item; } }

        private ChoiceBox _kind, _window, _sort;
        private ToggleSwitch _foldersFirst;
        private Label _sortHint;
        private TextField _name, _target, _args, _dir, _folder;
        private ToggleSwitch _admin, _filesOnly;
        private PictureBox _iconBox;
        private Card _card;
        private ModernButton _btnSave, _btnCancel;
        private Label _error;
        private Control[] _programRows, _menuRows;
        private string _icon;

        public ItemDialog(DockItem item)
        {
            _item = item;
            _icon = item.Icon ?? "";
            using (Graphics g = CreateGraphics()) _sc = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Text = AppInfo.Name + " – Eigenschaften";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true; // das Dock selbst hat keinen Eintrag in der Taskleiste
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            KeyPreview = true;
            Font = Theme.UiFont(13.5f * _sc, FontStyle.Regular);
            ClientSize = new Size(S(560), S(526));
            BuildUi();
            LoadValues();
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        private void BuildUi()
        {
            _card = new Card();
            _card.UiScale = _sc;
            _card.Bounds = new Rectangle(S(24), S(24), S(512), S(420));
            Controls.Add(_card);

            int x = S(150), w = S(512) - S(150) - S(18);
            _kind = Choice(new[] { "Programm, Datei oder Ordner", "Ordnermenü" });
            Row("Art", _kind, x, S(18), w);
            _name = Field();
            Row("Name", _name, x, S(60), w);

            // Programm
            _target = Field();
            ModernButton browse = SmallButton("…");
            browse.Click += delegate { BrowseTarget(); };
            Label lt = Row("Ziel", _target, x, S(102), w - S(40));
            Place(browse, x + w - S(32), S(102));
            _args = Field();
            Label la = Row("Parameter", _args, x, S(144), w);
            _dir = Field();
            ModernButton browseDir = SmallButton("…");
            browseDir.Click += delegate
            {
                string f = FolderPicker.Pick(this, "Arbeitsordner wählen", IconLoader.Expand(_dir.Text));
                if (f != null) _dir.Text = f;
            };
            Label ld = Row("Ausführen in", _dir, x, S(186), w - S(40));
            Place(browseDir, x + w - S(32), S(186));
            _window = Choice(new[] { "Normal", "Minimiert", "Maximiert" });
            Label lw = Row("Fenster", _window, x, S(228), S(180));
            _admin = new ToggleSwitch();
            _admin.Size = new Size(S(44), S(22));
            Label lad = Row("Als Administrator", _admin, x + w - S(44), S(270), S(44));
            _programRows = new Control[] { lt, _target, browse, la, _args, ld, _dir, browseDir, lw, _window, lad, _admin };

            // Ordnermenü
            _folder = Field();
            ModernButton browseFolder = SmallButton("…");
            browseFolder.Click += delegate
            {
                string f = FolderPicker.Pick(this, "Ordner für das Ordnermenü wählen", IconLoader.Expand(_folder.Text));
                if (f != null) { _folder.Text = f; if (_name.Text.Trim().Length == 0) _name.Text = Path.GetFileName(f.TrimEnd('\\')); }
            };
            Label lf = Row("Ordner", _folder, x, S(102), w - S(40));
            Place(browseFolder, x + w - S(32), S(102));
            _sort = Choice(new[] { "Name (A–Z)", "Name (Z–A)", "Neueste zuerst", "Älteste zuerst", "Eigene Reihenfolge" });
            Label ls = Row("Sortierung", _sort, x, S(144), S(220));
            _foldersFirst = new ToggleSwitch();
            _foldersFirst.Size = new Size(S(44), S(22));
            Label lff = Row("Unterordner zuerst", _foldersFirst, x + w - S(44), S(186), S(44));
            _filesOnly = new ToggleSwitch();
            _filesOnly.Size = new Size(S(44), S(22));
            Label lfo = Row("Nur Dateien (ohne Unterordner)", _filesOnly, x + w - S(44), S(228), S(44));
            // Hinweis: Reihenfolge im offenen Menü per Ziehen ändern (stellt die Sortierung auf „Eigene Reihenfolge“)
            _sortHint = MakeLabel("Tipp: Einträge lassen sich im offenen Ordnermenü mit der Maus an eine andere Stelle ziehen – die Sortierung wechselt dann auf „Eigene Reihenfolge“ (nicht bei „Einträge sperren“).", true);
            _sortHint.AutoSize = false;
            _sortHint.Bounds = new Rectangle(S(18), S(268), x + w - S(18), S(44));
            _card.Controls.Add(_sortHint);
            _menuRows = new Control[] { lf, _folder, browseFolder, ls, _sort, _sortHint, lff, _foldersFirst, lfo, _filesOnly };

            // Symbol
            _iconBox = new PictureBox();
            _iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            _iconBox.Bounds = new Rectangle(x, S(320), S(64), S(64));
            _card.Controls.Add(_iconBox);
            Label li = MakeLabel("Symbol", false);
            li.Location = new Point(S(18), S(320) + (S(64) - li.PreferredHeight) / 2);
            _card.Controls.Add(li);
            ModernButton change = new ModernButton();
            change.Text = "Ändern …";
            change.Bounds = new Rectangle(x + S(80), S(320), S(124), S(32));
            change.Click += delegate { ChangeIcon(change); };
            _card.Controls.Add(change);
            ModernButton reset = new ModernButton();
            reset.Text = "Standard";
            reset.Bounds = new Rectangle(x + S(80) + S(132), S(320), S(110), S(32));
            reset.Click += delegate { _icon = ""; UpdatePreview(); };
            _card.Controls.Add(reset);
            Label hint = MakeLabel("Standard = Symbol des Ziels", true);
            hint.Location = new Point(x + S(80), S(360));
            _card.Controls.Add(hint);

            _kind.SelectionChanged += delegate { UpdateKind(); UpdatePreview(); };
            _target.Leave += delegate { UpdatePreview(); };
            _folder.Leave += delegate { UpdatePreview(); };

            // Knöpfe: "Abbrechen" links, "Speichern" rechts, links davon die Fehlerzeile
            int by = S(468);
            _btnSave = new ModernButton();
            _btnSave.Text = "Speichern";
            _btnSave.Primary = true;
            _btnSave.Bounds = new Rectangle(S(536) - S(120), by, S(120), S(34));
            _btnSave.Click += delegate { SaveAndClose(); };
            Controls.Add(_btnSave);
            _btnCancel = new ModernButton();
            _btnCancel.Text = "Abbrechen";
            _btnCancel.Bounds = new Rectangle(_btnSave.Left - S(10) - S(120), by, S(120), S(34));
            _btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);
            _error = MakeLabel("", false);
            _error.AutoSize = false;
            _error.AutoEllipsis = true;
            _error.TextAlign = ContentAlignment.MiddleLeft;
            _error.Tag = "error";
            _error.Bounds = new Rectangle(S(24), by, _btnCancel.Left - S(12) - S(24), S(34));
            Controls.Add(_error);
        }

        private TextField Field()
        {
            TextField t = new TextField();
            t.Font = Theme.UiFont(13.5f * _sc, FontStyle.Regular);
            t.Size = new Size(S(200), S(32));
            return t;
        }

        private ChoiceBox Choice(string[] choices)
        {
            ChoiceBox c = new ChoiceBox();
            c.Choices = choices;
            c.SelectedIndex = 0;
            c.Size = new Size(S(200), S(32));
            return c;
        }

        private ModernButton SmallButton(string text)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.Size = new Size(S(32), S(32));
            return b;
        }

        private void Place(Control c, int x, int y)
        {
            c.Location = new Point(x, y);
            _card.Controls.Add(c);
        }

        private Label MakeLabel(string text, bool sub)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.UseMnemonic = false;
            l.Font = Theme.UiFont((sub ? 12.5f : 13.5f) * _sc, FontStyle.Regular);
            l.Tag = sub ? "sub" : null;
            return l;
        }

        private Label Row(string text, Control c, int x, int y, int width)
        {
            Label l = MakeLabel(text, false);
            int rowH = Math.Max(c.Height, S(32));
            l.Location = new Point(S(18), y + (rowH - l.PreferredHeight) / 2);
            _card.Controls.Add(l);
            if (!(c is ToggleSwitch)) c.Width = width;
            c.Location = new Point(x, y + (rowH - c.Height) / 2);
            _card.Controls.Add(c);
            ToggleSwitch t = c as ToggleSwitch;
            if (t != null) l.Click += delegate { t.Checked = !t.Checked; };
            return l;
        }

        private void LoadValues()
        {
            _kind.SelectedIndex = _item.Kind == ItemKind.Ordnermenue ? 1 : 0;
            _name.Text = _item.Title;
            _target.Text = _item.Command;
            _args.Text = _item.Arguments;
            _dir.Text = _item.WorkingDirectory;
            _window.SelectedIndex = _item.ShowCmd;
            _admin.Checked = _item.RunAsAdmin;
            _folder.Text = _item.Folder;
            _sort.SelectedIndex = _item.Sort;
            _foldersFirst.Checked = _item.FoldersFirst;
            _filesOnly.Checked = _item.FilesOnly;
            UpdateKind();
            UpdatePreview();
        }

        private void UpdateKind()
        {
            bool menu = _kind.SelectedIndex == 1;
            foreach (Control c in _programRows) c.Visible = !menu;
            foreach (Control c in _menuRows) c.Visible = menu;
        }

        private DockItem Current()
        {
            DockItem it = _item.Clone();
            it.Kind = _kind.SelectedIndex == 1 ? ItemKind.Ordnermenue : ItemKind.Programm;
            it.Title = _name.Text.Trim();
            it.Command = _target.Text.Trim();
            it.Arguments = _args.Text.Trim();
            it.WorkingDirectory = _dir.Text.Trim();
            it.ShowCmd = Math.Max(0, _window.SelectedIndex);
            it.RunAsAdmin = _admin.Checked;
            it.Folder = _folder.Text.Trim();
            it.FoldersFirst = _foldersFirst.Checked;
            it.Sort = Math.Max(0, _sort.SelectedIndex);
            it.FilesOnly = _filesOnly.Checked;
            it.Icon = _icon;
            return it;
        }

        private void UpdatePreview()
        {
            Image old = _iconBox.Image;
            _iconBox.Image = IconLoader.Load(Current(), S(64));
            if (old != null) old.Dispose();
        }

        private void BrowseTarget()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Ziel auswählen";
                dlg.Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc|Alle Dateien|*.*";
                dlg.DereferenceLinks = false;
                string cur = IconLoader.Expand(_target.Text);
                try { if (File.Exists(cur)) { dlg.InitialDirectory = Path.GetDirectoryName(cur); dlg.FileName = Path.GetFileName(cur); } } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                DockItem n = DockWindow.ItemFromPath(dlg.FileName);
                _target.Text = n.Command;
                if (n.Arguments.Length > 0) _args.Text = n.Arguments;
                if (n.WorkingDirectory.Length > 0) _dir.Text = n.WorkingDirectory;
                if (_name.Text.Trim().Length == 0) _name.Text = n.Title;
                if (n.Icon.Length > 0 && _icon.Length == 0) _icon = n.Icon;
                UpdatePreview();
            }
        }

        private void ChangeIcon(Control anchor)
        {
            MenuBuilder mb = new MenuBuilder();
            mb.Menu.Items.Add(mb.Item("Bilddatei (PNG, ICO) …", '', delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Symbol auswählen";
                    dlg.Filter = "Bilder und Symbole|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif|Alle Dateien|*.*";
                    if (Directory.Exists(Settings.IconDirectory)) dlg.InitialDirectory = Settings.IconDirectory;
                    if (dlg.ShowDialog(this) == DialogResult.OK) { _icon = dlg.FileName; UpdatePreview(); }
                }
            }));
            mb.Menu.Items.Add(mb.Item("Aus Programm oder DLL …", '', delegate
            {
                int idx = 0;
                string file = _icon.Length > 0 ? IconLoader.SplitSpec(_icon, out idx) : IconLoader.Expand(_target.Text);
                if (_icon.Length == 0) idx = 0;
                string ext = "";
                try { ext = Path.GetExtension(file).ToLowerInvariant(); } catch { }
                if (ext != ".exe" && ext != ".dll" && ext != ".ico" && ext != ".icl") { file = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\imageres.dll"); idx = 0; }
                string pick = DockNative.PickIcon(Handle, file, idx);
                if (pick != null) { _icon = pick; UpdatePreview(); }
            }));
            mb.End();
            mb.Menu.Show(anchor, new Point(0, anchor.Height + 2));
        }

        private void SaveAndClose()
        {
            _error.Text = "";
            DockItem it = Current();
            if (it.Title.Length == 0) { _error.Text = "Bitte einen Namen eingeben."; _name.Focus(); return; }
            if (it.Kind == ItemKind.Programm && it.Command.Length == 0) { _error.Text = "Bitte ein Ziel eingeben."; _target.Focus(); return; }
            if (it.Kind == ItemKind.Ordnermenue && it.Folder.Length == 0) { _error.Text = "Bitte einen Ordner wählen."; return; }
            _item.Kind = it.Kind; _item.Title = it.Title; _item.Command = it.Command; _item.Arguments = it.Arguments;
            _item.WorkingDirectory = it.WorkingDirectory; _item.ShowCmd = it.ShowCmd; _item.RunAsAdmin = it.RunAsAdmin;
            _item.Folder = it.Folder; _item.FoldersFirst = it.FoldersFirst; _item.FilesOnly = it.FilesOnly; _item.Sort = it.Sort; _item.Icon = it.Icon;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ------------------------------------------------------------------ Design und Fenster

        private void ApplyTheme()
        {
            Palette p = Theme.Current;
            BackColor = p.Back;
            ForeColor = p.Text;
            ApplyTheme(this, p);
            if (IsHandleCreated) NativeMethods.UseDarkTitleBar(Handle, p.Dark);
            Invalidate(true);
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
                    c.ForeColor = tag == "error" ? (p.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28)) : tag == "sub" ? p.SubText : p.Text;
                }
                if (c.Controls.Count > 0) ApplyTheme(c, p);
            }
        }

        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)ApplyTheme);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseDarkTitleBar(Handle, Theme.IsDark);
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _name.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None && !(ActiveControl is ModernButton))
            {
                SaveAndClose();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
                if (_iconBox != null && _iconBox.Image != null) _iconBox.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
