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
    /// <summary>
    /// Einstellungsfenster (Aufbau wie WindowGrid): Karten Position, Symbole, Verhalten und Allgemein.
    /// Änderungen werden erst mit "Speichern" übernommen, "Abbrechen" verwirft sie.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly Settings _original;
        private readonly Settings _edit;
        private readonly float _sc;
        public event EventHandler Saved;

        /// <summary>Prüft, ob ein Tastenkürzel frei ist (vom Dock gesetzt).</summary>
        public Func<int, int, bool> TestHotkey;
        /// <summary>RocketDock importieren (vom Tray gesetzt). Rückgabe true = importiert.</summary>
        public Func<Form, bool> ImportRocketDock;

        private ChoiceBox _monitor, _edge;
        private Stepper _edgeDist, _offset, _iconSize, _zoomSize, _zoomWidth, _spacing;
        private ToggleSwitch _labels, _running, _autoHide, _fullscreen, _activate, _locked, _onTop, _raise, _autostart;
        private ChoiceBox _anim, _zoomMode, _labelColor;
        private ToggleSwitch _reflection, _keepWidth, _edgeGrow, _shelfVisible, _preview;
        private Stepper _minShrink, _reserve, _shelfOpacity, _shelfRadius;
        private ChoiceBox _keepMode, _labelFont;
        private List<string> _fontNames;          // installierte Schriften (Index + 1 in _labelFont, 0 = Windows-Standard)
        /// <summary>Sicherung einspielen (vom Tray gesetzt). Rückgabe true = eingespielt.</summary>
        public Func<Form, bool> ImportBackup;
        /// <summary>Reihenfolge der Zoom-Effekte in der Auswahl (Werte wie Settings.ZoomMode).</summary>
        private static readonly int[] ZoomModes = { 0, 1, 5, 2, 3, 6, 7, 8, 4 };
        private NavList _nav;
        private bool _navOpen;                    // nicht angepinnt, aber ausgeklappt (liegt über dem Inhalt)
        private int _navSpace;                    // Platz, den die Navigation links belegt (230 bzw. 68 × Skalierung)
        private readonly Timer _navTimer = new Timer { Interval = 400 };
        private readonly Timer _navHover = new Timer { Interval = 400 }; // nicht angepinnt: Maus kurz auf der Navigation → ausklappen
        private Control[] _content;               // rechts der Navigation, wird beim Anpinnen verschoben
        private Card[] _pages;
        private Stepper _labelSize;
        private ToggleSwitch _labelBold;
        private Slider _labelShadow;
        private Label _labelShadowValue;
        private Stepper _labelShadowSize;
        private LabelSwatch _labelSwatch;
        private Color _labelCustom = Color.Empty;
        private bool _autostartLoaded;            // Stand des Autostart-Schalters beim Laden bzw. Speichern
        private string _baseline;                 // Stand der Felder nach dem Laden bzw. Speichern
        private bool _discard;                    // „Abbrechen“: ohne Rückfrage schließen
        private Slider _showDelay, _hideDelay;
        private Label _showValue, _hideValue;
        private ComboHotkeyBox _hotkey;
        private ModernButton _btnSave, _btnCancel;
        private ContextMenuStrip _extrasMenu;
        private Label _error;
        private PictureBox _logo;
        private UpdateBar _bar;
        private bool _barShown;
        private readonly EventHandler _onUpdateChanged;

        public SettingsForm(Settings settings, Icon icon)
        {
            _original = settings;
            _edit = settings.Clone();
            using (Graphics g = CreateGraphics()) _sc = g.DpiX / 96f;

            AutoScaleMode = AutoScaleMode.None;
            Text = "Dock Pilot – Einstellungen";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            _navSpace = settings.NavPinned ? S(NavFull) : S(NavCompact);
            ClientSize = new Size(_navSpace + S(28) + S(ContentW) + S(28), S(660));
            Font = Theme.UiFont(13.5f * _sc, FontStyle.Regular);
            KeyPreview = true;
            DoubleBuffered = true;

            BuildUi(icon);
            LoadValues(true);
            _baseline = Snapshot();
            ApplyTheme();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _onUpdateChanged = delegate { if (!IsDisposed) ShowUpdateBar(Updater.Available != null); };
            Updater.AvailableChanged += _onUpdateChanged;
            ShowUpdateBar(Updater.Available != null);
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        private const int NavFull = 230, NavCompact = 68, ContentW = 580;

        private void ShowUpdateBar(bool show)
        {
            ReleaseInfo r = Updater.Available;
            show = show && r != null;
            if (show)
            {
                _bar.Text = "Neue Version " + r.Version + " verfügbar – installiert ist " + AppInfo.Version + ".";
                _bar.Invalidate();
            }
            if (show == _barShown) return;
            int d = S(64) * (show ? 1 : -1);
            foreach (Control c in Controls)
                if (c != _bar && c.Dock == DockStyle.None && c.Top >= S(80)) c.Top += d;
            bool fixedSize = MinimumSize != Size.Empty;
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + d);
            if (fixedSize) { MinimumSize = Size; MaximumSize = Size; }
            if (IsHandleCreated)
            {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            }
            _bar.Visible = show;
            _barShown = show;
        }

        // ------------------------------------------------------------------ Aufbau

        private void BuildUi(Icon icon)
        {
            // Kopf: Icon, Programmname, Versions-Plakette, darunter kurze Erklärung
            _logo = new PictureBox();
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.Bounds = new Rectangle(S(24), S(20), S(44), S(44));
            try { _logo.Image = Theme.IconToBitmap(icon, S(48)); } catch { }
            Controls.Add(_logo);

            Label title = new NoPaddingLabel();
            title.Text = "Dock Pilot";
            title.AutoSize = true;
            title.Font = Theme.UiFont(22f * _sc, FontStyle.Bold);
            title.UseMnemonic = false;
            TextFormatFlags np = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            Size ts = TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, np);
            int indent = (TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width - ts.Width) / 2;
            title.Location = new Point(S(80) + indent, S(18));
            Controls.Add(title);

            Label subtitle = MakeLabel("Programmleiste am Bildschirmrand – Programme, Ordner und Menüs mit einem Klick", 13.5f, true);
            subtitle.Location = new Point(S(82), S(48));
            Controls.Add(subtitle);

            VersionPill version = new VersionPill();
            version.Text = "Version " + AppInfo.Version;
            int vw;
            using (Font vf = Theme.UiFont(10.5f * _sc, FontStyle.Regular))
                vw = TextRenderer.MeasureText(version.Text, vf, Size.Empty, np).Width + S(16);
            int inkRight = NoPaddingLabel.InkRight(title);
            int textEnd = inkRight > 0 ? inkRight : ts.Width;
            version.Bounds = new Rectangle(title.Left + textEnd + S(13), title.Top + (ts.Height - S(21)) / 2, vw, S(21));
            version.Click += delegate { AboutDialog.ShowFor(this); };
            Controls.Add(version);

            _bar = new UpdateBar(_sc);
            _bar.Bounds = new Rectangle(S(24), S(84), ClientSize.Width - S(48), S(52));
            _bar.Visible = false;
            _bar.Install.Click += delegate
            {
                Updater.InstallInteractive(this, this, delegate (bool busy)
                {
                    _bar.Install.Enabled = _bar.Page.Enabled = _bar.Later.Enabled = !busy;
                    if (busy) _bar.Text = "Lade Version " + Updater.Available.Version + " …";
                    else ShowUpdateBar(true);
                    _bar.Invalidate();
                });
            };
            _bar.Page.Click += delegate { Updater.OpenPage(); };
            _bar.Later.Click += delegate { ShowUpdateBar(false); };
            Controls.Add(_bar);

            int top = S(88);
            // Navigation links (eine Seite je Bereich, Vorlage: Wake on LAN / Monitor Dimmer), Inhalt rechts daneben.
            // Angepinnt schiebt sie den Inhalt, sonst liegt sie ausgeklappt über dem Inhalt.
            int pageH = S(426);
            int contentX = _navSpace + S(28);
            int colW = S(ContentW);
            _nav = new NavList(_sc);
            _nav.Pinned = _original.NavPinned;
            _nav.Bounds = new Rectangle(0, top, _navSpace, ClientSize.Height - S(64) - top);
            Controls.Add(_nav);

            // --- Seite: Position
            Card pos = MakeCard("Position", new Rectangle(contentX, top, colW, S(300)));
            _monitor = MakeChoice(S(176));
            _monitor.Choices = MonitorNames();
            AddRow(pos, "Bildschirm", _monitor, S(54));
            _edge = MakeChoice(S(132));
            _edge.Choices = new[] { "Oben", "Unten", "Links", "Rechts" };
            AddRow(pos, "Bildschirmrand", _edge, S(96));
            _edgeDist = MakeStepper(0, 400, "px");
            AddRow(pos, "Abstand zum Rand", _edgeDist, S(138));
            _offset = MakeStepper(-100, 100, "%");
            Label offLabel = AddRow(pos, "Zentrierung", _offset, S(180));
            ToolTip tip = new ModernToolTip();
            tip.SetToolTip(offLabel, "Lage des Docks am Bildschirmrand: -100 % ganz links bzw. oben,\n0 % Mitte, 100 % ganz rechts bzw. unten.");
            _onTop = MakeToggle();
            Label topLabel = AddRow(pos, "Immer im Vordergrund", _onTop, S(226));
            tip.SetToolTip(topLabel, "Aus: Fenster dürfen das Dock überdecken.");
            _raise = MakeToggle();
            Label raiseLabel = AddRow(pos, "Maus holt Dock nach vorne", _raise, S(262));
            tip.SetToolTip(raiseLabel, "Nur wenn „Immer im Vordergrund“ aus ist: Bleibt die Maus über dem Dock\nbzw. am Bildschirmrand davor, kommt es nach „Einblenden nach“ nach vorne.");

            // --- Seite: Symbole (Symbole und Aussehen der Leiste)
            Card icons = MakeCard("Symbole", new Rectangle(contentX, top, colW, S(388)));
            _iconSize = MakeStepper(16, 128, "px");
            AddRow(icons, "Symbolgröße", _iconSize, S(54));
            _spacing = MakeStepper(0, 30, "px");
            Label spLabel = AddRow(icons, "Abstand zwischen Symbolen", _spacing, S(96));
            tip.SetToolTip(spLabel, "Passen nicht alle Einträge auf den Bildschirm, verkleinert Dock Pilot die Symbole.\nEin kleiner Abstand lässt mehr Platz für die Symbole.");
            _running = MakeToggle();
            AddRow(icons, "Laufende Programme markieren", _running, S(138));
            _preview = MakeToggle();
            Label pvLabel = AddRow(icons, "Fenstervorschau", _preview, S(180));
            tip.SetToolTip(pvLabel, "Wie in der Taskleiste: Bleibt die Maus kurz auf einem laufenden Programm,\nerscheinen seine Fenster als Vorschaubilder. Klick holt ein Fenster nach vorne, × schließt es.");
            _reflection = MakeToggle();
            Label rfLabel = AddRow(icons, "Spiegelung", _reflection, S(222));
            tip.SetToolTip(rfLabel, "Dezente Spiegelung der Symbole zum Bildschirmrand hin (wie bei RocketDock).");
            _shelfVisible = MakeToggle();
            _shelfVisible.CheckedChanged += delegate { if (_raise != null) UpdateEnabled(); };
            Label svLabel = AddRow(icons, "Leiste anzeigen", _shelfVisible, S(264));
            tip.SetToolTip(svLabel, "Aus: kein Hintergrund, nur die Symbole.");
            _shelfOpacity = MakeStepper(10, 100, "%");
            _shelfOpacity.Step = 5;
            AddRow(icons, "Deckkraft der Leiste", _shelfOpacity, S(306));
            _shelfRadius = MakeStepper(0, 40, "px");
            Label srLabel = AddRow(icons, "Eckenradius der Leiste", _shelfRadius, S(348));
            tip.SetToolTip(srLabel, "0 = eckig. Standard: 8 px wie Fenster und Menüs in Windows 11.");

            // --- Seite: Zoom
            Card zoomCard = MakeCard("Zoom", new Rectangle(contentX, top, colW, S(388)));
            _zoomMode = MakeChoice(S(150));
            _zoomMode.SelectionChanged += delegate { if (_raise != null) UpdateEnabled(); };
            _zoomMode.Choices = new[] { "Automatisch", "Blase", "Weiche Blase", "Fest", "Anheben", "Leuchten", "Fokus", "Federn", "Aus" };
            Label zmLabel = AddRow(zoomCard, "Zoom-Effekt", _zoomMode, S(54));
            tip.SetToolTip(zmLabel, "Blase: die Nachbarn rücken zur Seite.\nWeiche Blase: wie Blase, aber die Nachbarn wachsen kaum mit.\nFest: die Leiste bleibt stehen, die Symbole wachsen an ihrem Platz.\nAutomatisch: fest, sobald das Dock mehr als zwei Drittel des Rands einnimmt.\nAnheben: nur das Symbol unter der Maus wird etwas größer.\nLeuchten: weicher Schein in Akzentfarbe hinter dem Symbol.\nFokus: die übrigen Symbole werden blasser.\nFedern: das Symbol hüpft beim Überfahren kurz hoch.\nAus: kein Effekt, nur die Beschriftung.");
            _zoomSize = MakeStepper(16, 256, "px");
            AddRow(zoomCard, "Größe unter der Maus", _zoomSize, S(96));
            _zoomWidth = MakeStepper(1, 6, null);
            Label zwLabel = AddRow(zoomCard, "Vergrößerte Nachbarn", _zoomWidth, S(138));
            tip.SetToolTip(zwLabel, "Wie viele Symbole links und rechts der Maus mitwachsen.");
            _keepWidth = MakeToggle();
            _keepWidth.CheckedChanged += delegate { if (_raise != null) UpdateEnabled(); };
            Label kwLabel = AddRow(zoomCard, "Leiste bleibt gleich breit", _keepWidth, S(180));
            tip.SetToolTip(kwLabel, "Nur bei Blase und Weiche Blase: Die Leiste wird nicht breiter.\nDie übrigen Symbole machen dafür Platz (wie unter einer Lupe).");
            _keepMode = MakeChoice(S(150));
            _keepMode.Choices = new[] { "Schrumpfen", "Überlappen" };
            Label kmLabel = AddRow(zoomCard, "Platz schaffen durch", _keepMode, S(222));
            tip.SetToolTip(kmLabel, "Schrumpfen: die übrigen Symbole werden kleiner.\nÜberlappen: sie bleiben gleich groß und schieben sich übereinander.");
            _minShrink = MakeStepper(50, 100, "%");
            _minShrink.Step = 5;
            Label msLabel = AddRow(zoomCard, "Übrige Symbole mindestens", _minShrink, S(264));
            tip.SetToolTip(msLabel, "So groß (Schrumpfen) bzw. so weit sichtbar (Überlappen) bleiben die übrigen Symbole.\nBei 100 % wächst die Blase nur so weit, wie Platz ist (Reserve).");
            _reserve = MakeStepper(0, 200, "px");
            _reserve.Step = 4;
            Label rsLabel = AddRow(zoomCard, "Reserve an den Enden", _reserve, S(306));
            tip.SetToolTip(rsLabel, "Die Leiste ist an beiden Enden um diesen Wert länger.\nDie Blase nutzt zuerst diesen Platz, erst danach geben die übrigen Symbole Platz her.");
            _edgeGrow = MakeToggle();
            Label egLabel = AddRow(zoomCard, "Am Ende hinauswachsen", _edgeGrow, S(348));
            tip.SetToolTip(egLabel, "Am ersten und letzten Symbol wächst die Leiste ein Stück über ihr Ende hinaus,\nstatt die Blase nach innen zu schieben.");

            // --- Seite: Beschriftung
            Card label = MakeCard("Beschriftung", new Rectangle(contentX, top, colW, S(380)));
            _labels = MakeToggle();
            AddRow(label, "Beschriftung anzeigen", _labels, S(54));
            _labelFont = MakeChoice(S(240));
            _fontNames = new List<string>();
            try
            {
                foreach (FontFamily fam in FontFamily.Families)
                    if (!fam.Name.StartsWith("@") && (fam.IsStyleAvailable(FontStyle.Regular) || fam.IsStyleAvailable(FontStyle.Bold)))
                        _fontNames.Add(fam.Name);
            }
            catch { }
            _fontNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            List<string> fontChoices = new List<string>();
            fontChoices.Add("Windows-Standard");
            fontChoices.AddRange(_fontNames);
            _labelFont.Choices = fontChoices.ToArray();
            Label lfLabel = AddRow(label, "Schriftart", _labelFont, S(92), S(40));
            tip.SetToolTip(lfLabel, "Windows-Standard: " + Theme.FontFamilyName + " – die Schrift von Windows 11 (unter Windows 10 Segoe UI).");
            _labelSize = MakeStepper(9, 32, "px");
            AddRow(label, "Schriftgröße", _labelSize, S(134));
            _labelBold = MakeToggle();
            AddRow(label, "Fett", _labelBold, S(178));
            _labelColor = MakeChoice(S(150));
            _labelColor.Choices = new[] { "Weiß", "Schwarz", "Wie Windows", "Akzentfarbe", "Eigene Farbe" };
            Label lcLabel = AddRow(label, "Schriftfarbe", _labelColor, S(216), S(40));
            tip.SetToolTip(lcLabel, "Wie Windows: im hellen Modus dunkel, im dunklen Modus hell.");
            _labelSwatch = new LabelSwatch(_sc);
            _labelSwatch.Size = new Size(S(32), S(32));
            _labelSwatch.Location = new Point(label.Width - S(18) - _labelSwatch.Width, _labelColor.Top);
            label.Controls.Add(_labelSwatch);
            _labelSwatch.Click += delegate { PickLabelColor(); };
            _labelColor.SelectionChanged += delegate
            {
                if (_labelColor.SelectedIndex == 4 && _labelCustom == Color.Empty && Visible) PickLabelColor();
                UpdateSwatch();
            };
            _labelShadow = MakeSlider();
            _labelShadow.Maximum = 10; // 0 – 100 % in Schritten von 10 %
            _labelShadowValue = MakeValueLabel();
            Label shLabel = AddRow(label, "Schattenstärke", _labelShadow, S(266), S(64));
            PlaceValue(label, _labelShadowValue, S(266));
            tip.SetToolTip(shLabel, "Wie kräftig der weiche Schatten hinter der Schrift ist –\ner hält sie auf hellen und dunklen Hintergründen lesbar. 0 % = kein Schatten.");
            _labelShadowSize = MakeStepper(1, 8, "px");
            Label ssLabel = AddRow(label, "Schattengröße", _labelShadowSize, S(306));
            tip.SetToolTip(ssLabel, "Wie weit der Schatten um die Schrift herum reicht. Standard: 3 px.");
            _labelShadow.ValueChanged += delegate { _labelShadowValue.Text = _labelShadow.Value * 10 + " %"; if (_raise != null && _labelBold != null) _labelShadowSize.Enabled = _labels.Checked && _labelShadow.Value > 0; };
            _labels.CheckedChanged += delegate { UpdateEnabled(); };

            // --- Seite: Verhalten
            Card behave = MakeCard("Verhalten", new Rectangle(contentX, top, colW, pageH));
            _autoHide = MakeToggle();
            AddRow(behave, "Automatisch ausblenden", _autoHide, S(54));
            _showDelay = MakeSlider();
            _showValue = MakeValueLabel();
            AddRow(behave, "Einblenden nach", _showDelay, S(98), S(64));
            PlaceValue(behave, _showValue, S(98));
            _hideDelay = MakeSlider();
            _hideValue = MakeValueLabel();
            AddRow(behave, "Ausblenden nach", _hideDelay, S(140), S(64));
            PlaceValue(behave, _hideValue, S(140));
            _fullscreen = MakeToggle();
            Label fsLabel = AddRow(behave, "Bei Vollbild ausblenden", _fullscreen, S(184));
            tip.SetToolTip(fsLabel, "Läuft auf diesem Bildschirm ein Spiel oder Video im Vollbild,\nbleibt das Dock verborgen (Tastenkürzel holt es trotzdem).");
            _activate = MakeToggle();
            Label actLabel = AddRow(behave, "Laufende nach vorne holen", _activate, S(226));
            tip.SetToolTip(actLabel, "Läuft ein Programm schon, holt ein Klick sein Fenster nach vorne\n(wie in der Taskleiste). Umschalt + Klick startet es trotzdem neu.");
            _locked = MakeToggle();
            AddRow(behave, "Einträge sperren", _locked, S(268));
            _anim = MakeChoice(S(132));
            _anim.Choices = new[] { "Aus", "Schnell", "Normal", "Langsam" };
            Label animLabel = AddRow(behave, "Animationen", _anim, S(312));
            tip.SetToolTip(animLabel, "Tempo von Vergrößerung, Ein- und Ausblenden und Hüpfen.");
            _hotkey = new ComboHotkeyBox();
            _hotkey.Size = new Size(S(176), S(32));
            Label hkLabel = AddRow(behave, "Tastenkürzel", _hotkey, S(362));
            tip.SetToolTip(hkLabel, "Blendet das Dock ein und aus. Feld anklicken und Tasten drücken,\nEntf löscht das Tastenkürzel.");

            // --- Seite: Allgemein
            Card general = MakeCard("Allgemein", new Rectangle(contentX, top, colW, S(206)));
            _autostart = MakeToggle();
            AddRow(general, "Mit Windows starten", _autostart, S(54));
            ModernButton import = new ModernButton();
            import.Text = "Importieren …";
            import.Size = new Size(S(130), S(30));
            Label impLabel = AddRow(general, "RocketDock übernehmen", import, S(96));
            ModernButton data = new ModernButton();
            data.Text = "Öffnen";
            data.Size = new Size(S(130), S(30));
            Label dataLabel = AddRow(general, "Datenordner", data, S(146));
            tip.SetToolTip(dataLabel, "Einstellungen, Einträge (Dock.ini) und übernommene Symbole.");
            data.Click += delegate { Shell.Open(Settings.DataDirectory, "", ""); };
            tip.SetToolTip(impLabel, "Einträge, Ordnermenüs und Einstellungen aus der Settings.ini\nvon RocketDock übernehmen.");
            import.Click += delegate
            {
                if (ImportRocketDock != null && ImportRocketDock(this))
                {
                    // Import übernimmt auch Größen und Verhalten – Anzeige auf den neuen Stand bringen
                    _edit.CopyFrom(_original);
                    LoadValues(false);
                    _baseline = Snapshot();
                }
            };

            _pages = new Card[] { pos, icons, zoomCard, label, behave, general };
            _nav.Add('\uE7F4', "Position");
            _nav.Add('\uE8A9', "Symbole");
            _nav.Add('\uE8A3', "Zoom");
            _nav.Add('\uE8D2', "Beschriftung");
            _nav.Add('\uE713', "Verhalten");
            _nav.Add('\uE80F', "Allgemein");
            _nav.SelectedChanged += delegate { ShowPage(_nav.SelectedIndex); CloseNav(); };
            _nav.MenuClicked += delegate
            {
                if (_original.NavPinned) SetNavPinned(false);
                else { _navOpen = !_navOpen; ApplyNav(); }
            };
            _nav.PinClicked += delegate { SetNavPinned(!_original.NavPinned); };
            _nav.ExtrasClicked += delegate { ShowExtrasMenu(); };
            _navTimer.Tick += delegate
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt
                if (!_navOpen || _original.NavPinned || !Visible) return;
                if (_extrasMenu != null && !_extrasMenu.IsDisposed && _extrasMenu.Visible) return; // Extras-Menü offen
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) { _navOpen = false; ApplyNav(); }
            };
            _navTimer.Start();
            // nicht angepinnt: verweilt die Maus kurz auf der eingeklappten Navigation, klappt sie aus (Namen sichtbar)
            // und beim Verlassen wieder ein – ☰ funktioniert weiterhin
            _nav.MouseEnter += delegate { if (!_original.NavPinned && !_navOpen) _navHover.Start(); };
            _nav.MouseLeave += delegate { _navHover.Stop(); };
            _navHover.Tick += delegate
            {
                _navHover.Stop();
                if (_original.NavPinned || _navOpen || !Visible) return;
                if (_extrasMenu != null && !_extrasMenu.IsDisposed && _extrasMenu.Visible) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) return;
                _navOpen = true;
                ApplyNav();
            };
            ShowPage(0);

            // --- Schaltflächen
            int by = top + pageH + S(24);
            int right = contentX + colW;
            _btnCancel = MakeButton("Abbrechen", false, new Rectangle(right - S(120) - S(10) - S(120), by, S(120), S(34)));
            _btnSave = MakeButton("Speichern", true, new Rectangle(right - S(120), by, S(120), S(34)));
            _error = MakeLabel("", 12.5f, false);
            _error.AutoSize = false;
            _error.AutoEllipsis = true;
            _error.TextAlign = ContentAlignment.MiddleLeft;
            _error.Tag = "error";
            _error.Bounds = new Rectangle(contentX, by, _btnCancel.Left - S(12) - contentX, S(34));
            Controls.Add(_error);
            _content = new Control[] { pos, icons, zoomCard, label, behave, general, _btnCancel, _btnSave, _error };
            _btnCancel.Click += delegate { _discard = true; Close(); }; // Abbrechen verwirft ohne Rückfrage
            _btnSave.Click += delegate { Save(); };

            // --- Fußzeile (einheitlich in allen Projekten)
            SignatureFooter footer = new SignatureFooter(_sc);
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(64);
            Controls.Add(footer);
            _nav.BringToFront();

            _showDelay.ValueChanged += delegate { _showValue.Text = _showDelay.Value * 50 + " ms"; };
            _hideDelay.ValueChanged += delegate { _hideValue.Text = _hideDelay.Value * 50 + " ms"; };
            _autoHide.CheckedChanged += delegate { UpdateEnabled(); };
            _onTop.CheckedChanged += delegate { UpdateEnabled(); };
            _raise.CheckedChanged += delegate { UpdateEnabled(); };
            _iconSize.ValueChanged += delegate { if (_zoomSize.Value < _iconSize.Value) _zoomSize.Value = _iconSize.Value; };
            _zoomSize.ValueChanged += delegate { if (_zoomSize.Value < _iconSize.Value) _iconSize.Value = _zoomSize.Value; };
        }

        private static string[] MonitorNames()
        {
            Screen[] all = Screen.AllScreens;
            string[] names = new string[all.Length + 1];
            names[0] = "Hauptbildschirm";
            for (int i = 0; i < all.Length; i++)
                names[i + 1] = "Bildschirm " + (i + 1) + " · " + all[i].Bounds.Width + " × " + all[i].Bounds.Height + (all[i].Primary ? " (Haupt)" : "");
            return names;
        }

        private Card MakeCard(string title, Rectangle bounds)
        {
            Card c = new Card();
            c.Title = title;
            c.UiScale = _sc;
            c.Bounds = bounds;
            Controls.Add(c);
            return c;
        }

        private Label MakeLabel(string text, float size, bool sub)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = Theme.UiFont(size * _sc, FontStyle.Regular);
            l.Tag = sub ? "sub" : null;
            l.UseMnemonic = false;
            return l;
        }

        private Label MakeValueLabel()
        {
            Label l = MakeLabel("0 ms", 13f, true);
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.Size = new Size(S(64), S(24));
            return l;
        }

        private void PlaceValue(Card card, Label value, int y)
        {
            value.Location = new Point(card.Width - S(18) - value.Width, y);
            card.Controls.Add(value);
        }

        private ChoiceBox MakeChoice(int width)
        {
            ChoiceBox c = new ChoiceBox();
            c.Size = new Size(width, S(32));
            return c;
        }

        private Stepper MakeStepper(int min, int max, string suffix)
        {
            Stepper s = new Stepper();
            s.Minimum = min;
            s.Maximum = max;
            s.Suffix = suffix;
            s.Size = new Size(S(132), S(32));
            return s;
        }

        private Slider MakeSlider()
        {
            Slider s = new Slider();
            s.Minimum = 0;
            s.Maximum = 40; // in Schritten von 50 ms: 0 – 2000 ms
            s.Size = new Size(S(120), S(24));
            return s;
        }

        private ToggleSwitch MakeToggle()
        {
            ToggleSwitch t = new ToggleSwitch();
            t.Size = new Size(S(44), S(22));
            return t;
        }

        private ModernButton MakeButton(string text, bool primary, Rectangle bounds)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.Primary = primary;
            b.Bounds = bounds;
            Controls.Add(b);
            return b;
        }

        private Label AddRow(Card card, string text, Control control, int y)
        {
            return AddRow(card, text, control, y, 0);
        }

        private Label AddRow(Card card, string text, Control control, int y, int rightReserve)
        {
            int rowH = Math.Max(control.Height, S(24));
            Label l = MakeLabel(text, 13.5f, false);
            card.Controls.Add(l);
            l.Location = new Point(S(18), y + (rowH - l.PreferredHeight) / 2);
            control.Location = new Point(card.Width - S(18) - rightReserve - control.Width, y + (rowH - control.Height) / 2);
            card.Controls.Add(control);
            l.Click += delegate
            {
                ToggleSwitch t = control as ToggleSwitch;
                if (t != null && t.Enabled) t.Checked = !t.Checked;
                else control.Focus();
            };
            return l;
        }

        // ------------------------------------------------------------------ Werte

        private void LoadValues(bool includeAutostart)
        {
            int mon = _edit.Monitor >= 0 && _edit.Monitor < Screen.AllScreens.Length ? _edit.Monitor + 1 : 0;
            _monitor.SelectedIndex = mon;
            _edge.SelectedIndex = (int)_edit.Edge;
            _edgeDist.Value = _edit.EdgeDistance;
            _offset.Value = _edit.CenterPercent;
            _iconSize.Value = _edit.IconSize;
            _zoomSize.Value = _edit.ZoomSize;
            _zoomWidth.Value = _edit.ZoomWidth;
            _zoomMode.SelectedIndex = Math.Max(0, Array.IndexOf(ZoomModes, _edit.ZoomMode));
            _reflection.Checked = _edit.Reflection;
            _preview.Checked = _edit.Preview;
            _keepWidth.Checked = _edit.KeepWidth;
            _keepMode.SelectedIndex = _edit.KeepMode;
            _minShrink.Value = _edit.MinShrink;
            _reserve.Value = _edit.Reserve;
            _edgeGrow.Checked = _edit.EdgeGrow;
            _shelfVisible.Checked = _edit.ShelfVisible;
            _shelfOpacity.Value = _edit.ShelfOpacity;
            _shelfRadius.Value = _edit.ShelfRadius;
            _spacing.Value = _edit.Spacing;
            _labelSize.Value = _edit.LabelSize;
            _labelBold.Checked = _edit.LabelBold;
            _labelCustom = _edit.LabelCustomColor == 0 ? Color.Empty : Color.FromArgb(_edit.LabelCustomColor);
            _labelColor.SelectedIndex = _edit.LabelColorMode;
            _labelShadow.Value = _edit.LabelShadow;
            int fontIndex = string.IsNullOrEmpty(_edit.LabelFont) ? -1 : _fontNames.FindIndex(n => string.Equals(n, _edit.LabelFont, StringComparison.OrdinalIgnoreCase));
            _labelFont.SelectedIndex = fontIndex + 1; // nicht installiert: Windows-Standard
            _labelShadowSize.Value = _edit.LabelShadowSize;
            _labelShadowValue.Text = _labelShadow.Value * 10 + " %";
            UpdateSwatch();
            _labels.Checked = _edit.Labels;
            _running.Checked = _edit.ShowRunning;
            _autoHide.Checked = _edit.AutoHide;
            _showDelay.Value = (int)Math.Round(_edit.ShowDelay / 50.0);
            _hideDelay.Value = (int)Math.Round(_edit.HideDelay / 50.0);
            _showValue.Text = _showDelay.Value * 50 + " ms";
            _hideValue.Text = _hideDelay.Value * 50 + " ms";
            _onTop.Checked = _edit.AlwaysOnTop;
            _raise.Checked = _edit.RaiseOnHover;
            UpdateEnabled();
            _fullscreen.Checked = _edit.HideOnFullscreen;
            _activate.Checked = _edit.ActivateRunning;
            _locked.Checked = _edit.Locked;
            _anim.SelectedIndex = _edit.Animate ? 1 + Math.Max(0, Math.Min(2, _edit.AnimSpeed)) : 0;
            _hotkey.SetHotkey(_edit.HotkeyModifiers, _edit.HotkeyKey);
            if (includeAutostart)
            {
                _autostart.Checked = !Demo.Active && Autostart.IsEnabled;
                _autostartLoaded = _autostart.Checked;
            }
        }

        /// <summary>Abhängige Regler: Verzögerungen nur bei automatischem Ausblenden bzw. „Maus holt Dock nach vorne“.</summary>
        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
        }

        /// <summary>Ausgeklappte (nicht angepinnte) Navigation nach einer Auswahl wieder einklappen.</summary>
        private void CloseNav()
        {
            if (!_original.NavPinned && _navOpen) { _navOpen = false; ApplyNav(); }
        }

        /// <summary>Anpinnen bzw. lösen; der Zustand wird sofort gespeichert (nicht erst mit „Speichern“).</summary>
        private void SetNavPinned(bool pinned)
        {
            _original.NavPinned = pinned;
            _edit.NavPinned = pinned;
            try { _original.Save(); } catch { }
            _navOpen = false;
            ApplyNav();
        }

        /// <summary>
        /// Breite der Navigation: angepinnt 230 und schiebt den Inhalt (das Fenster wird breiter), sonst 68 (nur Symbole);
        /// ausgeklappt, aber nicht angepinnt, liegt sie mit 230 über dem Inhalt.
        /// </summary>
        private void ApplyNav()
        {
            bool pinned = _original.NavPinned;
            bool open = pinned || _navOpen;
            int space = pinned ? S(NavFull) : S(NavCompact);
            if (space != _navSpace)
            {
                int d = space - _navSpace;
                _navSpace = space;
                SuspendLayout();
                foreach (Control c in _content) c.Left += d;
                bool fixedSize = MinimumSize != Size.Empty;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width + d, ClientSize.Height);
                if (fixedSize) { MinimumSize = Size; MaximumSize = Size; }
                _bar.Width = ClientSize.Width - S(48);
                ResumeLayout();
                if (IsHandleCreated)
                {
                    Rectangle work = Screen.FromControl(this).WorkingArea;
                    if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
                }
            }
            _nav.SetBounds(_nav.Left, _nav.Top, open ? S(NavFull) : S(NavCompact), ClientSize.Height - S(64) - _nav.Top);
            _nav.Pinned = pinned;
            _nav.Overlay = open && !pinned;
            _nav.BringToFront();
        }

        /// <summary>Eigene Schriftfarbe mit dem Windows-Farbdialog wählen.</summary>
        private void PickLabelColor()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = _labelCustom == Color.Empty ? Color.White : _labelCustom;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _labelCustom = Color.FromArgb(255, dlg.Color);
                    _labelColor.SelectedIndex = 4;
                }
                else if (_labelCustom == Color.Empty) _labelColor.SelectedIndex = 0;
            }
            UpdateSwatch();
        }

        private void UpdateSwatch()
        {
            _labelSwatch.Color = Settings.LabelColorFor(_labelColor.SelectedIndex, _labelCustom.ToArgb(), Theme.IsDark, Theme.SystemAccent);
            _labelSwatch.Invalidate();
        }

        private void UpdateEnabled()
        {
            bool lab = _labels.Checked;
            _labelFont.Enabled = _labelSize.Enabled = _labelBold.Enabled = _labelColor.Enabled = _labelSwatch.Enabled = _labelShadow.Enabled = lab;
            _labelShadowSize.Enabled = lab && _labelShadow.Value > 0;
            // Größe unter der Maus und Nachbarn gelten nur für die Zoom-Effekte Automatisch, Blase und Fest
            int zm = ZoomModes[Math.Max(0, _zoomMode.SelectedIndex)];
            bool zoom = zm <= 2 || zm == 5; // nur Effekte mit Vergrößerung
            _zoomSize.Enabled = zoom;
            _zoomWidth.Enabled = zoom;
            bool bubble = zm == 0 || zm == 1 || zm == 5; // Blase (auch Automatisch, wenn es Blase wählt)
            _keepWidth.Enabled = bubble;
            _keepMode.Enabled = _minShrink.Enabled = _reserve.Enabled = _edgeGrow.Enabled = bubble && _keepWidth.Checked;
            _shelfOpacity.Enabled = _shelfRadius.Enabled = _shelfVisible.Checked;
            _raise.Enabled = !_onTop.Checked;
            _showDelay.Enabled = _autoHide.Checked || (!_onTop.Checked && _raise.Checked);
            _hideDelay.Enabled = _autoHide.Checked;
        }

        private void ReadValues()
        {
            _edit.Monitor = _monitor.SelectedIndex - 1;
            _edit.Edge = (DockEdge)Math.Max(0, _edge.SelectedIndex);
            _edit.EdgeDistance = _edgeDist.Value;
            _edit.CenterPercent = _offset.Value;
            _edit.AlwaysOnTop = _onTop.Checked;
            _edit.RaiseOnHover = _raise.Checked;
            _edit.IconSize = _iconSize.Value;
            _edit.ZoomSize = Math.Max(_iconSize.Value, _zoomSize.Value);
            _edit.ZoomWidth = _zoomWidth.Value;
            _edit.ZoomMode = ZoomModes[Math.Max(0, _zoomMode.SelectedIndex)];
            _edit.Reflection = _reflection.Checked;
            _edit.Preview = _preview.Checked;
            _edit.KeepWidth = _keepWidth.Checked;
            _edit.KeepMode = Math.Max(0, _keepMode.SelectedIndex);
            _edit.MinShrink = _minShrink.Value;
            _edit.Reserve = _reserve.Value;
            _edit.EdgeGrow = _edgeGrow.Checked;
            _edit.ShelfVisible = _shelfVisible.Checked;
            _edit.ShelfOpacity = _shelfOpacity.Value;
            _edit.ShelfRadius = _shelfRadius.Value;
            _edit.Spacing = _spacing.Value;
            _edit.LabelSize = _labelSize.Value;
            _edit.LabelBold = _labelBold.Checked;
            _edit.LabelColorMode = Math.Max(0, _labelColor.SelectedIndex);
            _edit.LabelCustomColor = _labelCustom == Color.Empty ? 0 : _labelCustom.ToArgb();
            _edit.LabelShadow = _labelShadow.Value;
            _edit.LabelFont = _labelFont.SelectedIndex > 0 ? _fontNames[_labelFont.SelectedIndex - 1] : "";
            _edit.LabelShadowSize = _labelShadowSize.Value;
            _edit.Labels = _labels.Checked;
            _edit.ShowRunning = _running.Checked;
            _edit.AutoHide = _autoHide.Checked;
            _edit.ShowDelay = _showDelay.Value * 50;
            _edit.HideDelay = _hideDelay.Value * 50;
            _edit.HideOnFullscreen = _fullscreen.Checked;
            _edit.ActivateRunning = _activate.Checked;
            _edit.Locked = _locked.Checked;
            _edit.Animate = _anim.SelectedIndex > 0;
            if (_anim.SelectedIndex > 0) _edit.AnimSpeed = _anim.SelectedIndex - 1;
            _edit.HotkeyModifiers = _hotkey.Modifiers;
            _edit.HotkeyKey = _hotkey.Key;
        }

        /// <summary>Meldung links neben den Knöpfen: rot bei Fehlern, grün nach dem Speichern.</summary>
        private void ShowStatus(string text, bool ok)
        {
            _error.Tag = ok ? "ok" : "error";
            _error.ForeColor = StatusColor(ok, Theme.Current.Dark);
            _error.Text = text;
        }

        private static Color StatusColor(bool ok, bool dark)
        {
            if (ok) return dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15);
            return dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
        }

        private void Save()
        {
            _error.Text = "";
            // erst prüfen, dann speichern – bei einem Fehler bleibt das Fenster offen
            if (_hotkey.Key > 0 && TestHotkey != null && !TestHotkey(_hotkey.Modifiers, _hotkey.Key))
            {
                ShowStatus("Das Tastenkürzel nutzt schon ein anderes Programm.", false);
                return;
            }
            ReadValues();
            _edit.FirstRun = _original.FirstRun;
            _edit.LastUpdateCheck = _original.LastUpdateCheck;
            _edit.UpdateCheck = _original.UpdateCheck; // wird im Info-Fenster eingestellt
            bool autoWanted = _autostart.Checked;
            _original.CopyFrom(_edit);
            _original.Save();
            if (autoWanted != Autostart.IsEnabled && !Autostart.Set(autoWanted, false))
            {
                if (Saved != null) Saved(this, EventArgs.Empty);
                ShowStatus("Der Autostart konnte nicht geändert werden.", false);
                return;
            }
            _autostartLoaded = autoWanted;
            _baseline = Snapshot();
            if (Saved != null) Saved(this, EventArgs.Empty);
            // Fenster bleibt offen (wie Monitor Dimmer), kurze Bestätigung in Grün
            ShowStatus("Einstellungen gespeichert.", true);
        }

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
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Label, das seinen Text ohne Innenabstand zeichnet und misst (TextRenderer mit NoPadding).</summary>
    internal sealed class NoPaddingLabel : Label
    {
        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        public NoPaddingLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, Point.Empty, ForeColor, BackColor, Flags);
        }

        /// <summary>Rechter Rand der tatsächlich gezeichneten Schrift (Pixel ab links, 0 = nicht ermittelbar).</summary>
        public static int InkRight(Label label)
        {
            Size size = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, Flags);
            int w = size.Width + size.Height, h = size.Height; // mit Reserve, falls die Messung zu knapp ist
            if (w <= 0 || h <= 0) return 0;
            try
            {
                using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        TextRenderer.DrawText(g, label.Text, label.Font, Point.Empty, Color.Black, Color.White, Flags);
                    }
                    for (int x = w - 1; x >= 0; x--)
                        for (int y = 0; y < h; y++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.R < 160 || c.G < 160 || c.B < 160) return x + 1;
                        }
                }
            }
            catch { }
            return 0;
        }
    }

    /// <summary>
    /// Hinweisleiste "Neue Version verfügbar" mit "Jetzt aktualisieren", "Release-Seite" und "Später"
    /// (Vorlage: Fensterpositionen wiederherstellen).
    /// </summary>
    internal sealed class UpdateBar : Control
    {
        public readonly ModernButton Install = new ModernButton();
        public readonly ModernButton Page = new ModernButton();
        public readonly ModernButton Later = new ModernButton();
        private readonly float _sc;

        public UpdateBar(float scale)
        {
            _sc = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Install.Text = "Jetzt aktualisieren";
            Install.Primary = true;
            Page.Text = "Release-Seite";
            Later.Text = "Später";
            Install.Size = new Size(S(150), S(32));
            Page.Size = new Size(S(116), S(32));
            Later.Size = new Size(S(80), S(32));
            Controls.Add(Install);
            Controls.Add(Page);
            Controls.Add(Later);
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Width - S(10);
            foreach (ModernButton b in new[] { Later, Page, Install })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= S(8);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = Theme.Current;
            g.Clear(p.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(r, 8f * _sc))
            using (SolidBrush brush = new SolidBrush(Theme.Blend(p.Back, Theme.Accent, 0.14f)))
            using (Pen pen = new Pen(Theme.Blend(p.Back, Theme.Accent, 0.45f)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            int x = S(16);
            if (Theme.IconFontName != null)
            {
                using (Font icon = new Font(Theme.IconFontName, 14f * _sc, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, MenuHelper.Download.ToString(), icon, new Rectangle(x, 0, S(20), Height), Theme.Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                x += S(34);
            }
            int right = Install.Left - S(12);
            using (Font f = Theme.UiFont(13.5f * _sc, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, f, new Rectangle(x, 0, Math.Max(10, right - x), Height), p.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
