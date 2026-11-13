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
    /// Aufgeteilt: SettingsForm.cs (Felder, Werte laden/lesen/speichern, Live-Vorschau), SettingsPages.cs (Aufbau, je Seite
    /// eine Methode), SettingsExtras.cs (Design, Navigation, Extras-Menü, Schließen), SettingsControls.cs (eigene Steuerelemente).
    /// </summary>
    internal sealed partial class SettingsForm : Form
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
        private ToggleSwitch _reflection, _keepWidth, _edgeGrow, _shelfVisible, _preview, _unpinned;
        private Stepper _minShrink, _reserve, _shelfOpacity, _shelfRadius;
        private ChoiceBox _keepMode, _labelFont;
        private List<string> _fontNames;          // installierte Schriften (Index + 1 in _labelFont, 0 = Windows-Standard)
        /// <summary>Sicherung einspielen (vom Tray gesetzt). Rückgabe true = eingespielt.</summary>
        public Func<Form, bool> ImportBackup;
        /// <summary>Reihenfolge der Zoom-Effekte in der Auswahl (Werte wie Settings.ZoomMode).</summary>
        private static readonly int[] ZoomModes = { 0, 1, 5, 2, 3, 10, 4 };
        /// <summary>Reihenfolge der Hervorhebungen in der Auswahl (Werte wie Settings.Highlight).</summary>
        private static readonly int[] HighlightModes = { 0, 6, 12, 13, 15, 9, 11, 8, 7, 14 };
        // Seite „Weitere Docks“: Die Felder zeigen das gewählte Dock (2 oder 3), _extraEdit hält beide
        private ChoiceBox _dockPick, _xMonitor, _xEdge;
        private ToggleSwitch _xEnabled;
        private Stepper _xDist, _xOffset, _xIcon, _xZoom;
        private readonly DockPlace[] _extraEdit = new DockPlace[Settings.ExtraDocks];
        private int _extraShown = -1;
        private bool _extraLoading;
        private ChoiceBox _highlight;
        private Stepper _strength;
        private EffectPreview _effectPreview;
        /// <summary>Live-Vorschau: bekommt die noch nicht gespeicherten Einstellungen (null = wieder die gespeicherten). Vom Tray gesetzt.</summary>
        public Action<Settings> Live;
        private readonly Timer _liveTimer = new Timer { Interval = 250 };
        private string _liveSnap;
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
            _liveSnap = _baseline;
            _liveTimer.Tick += delegate { LiveTick(); };
            _liveTimer.Start();
            ApplyTheme();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _onUpdateChanged = delegate { if (!IsDisposed) ShowUpdateBar(Updater.Available != null); };
            Updater.AvailableChanged += _onUpdateChanged;
            ShowUpdateBar(Updater.Available != null);
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        /// <summary>Seite „Weitere Docks“: die Felder mit den Werten von Dock 2 (index 0) oder Dock 3 (index 1) füllen.</summary>
        private void ShowExtra(int index)
        {
            if (index < 0 || index >= Settings.ExtraDocks || _extraEdit[index] == null) return;
            _extraLoading = true;
            try
            {
                DockPlace p = _extraEdit[index];
                _extraShown = index;
                if (_dockPick.SelectedIndex != index) _dockPick.SelectedIndex = index;
                _xEnabled.Checked = p.Enabled;
                _xMonitor.SelectedIndex = p.Monitor >= 0 && p.Monitor < Screen.AllScreens.Length ? p.Monitor + 1 : 0;
                _xEdge.SelectedIndex = (int)p.Edge;
                _xDist.Value = p.EdgeDistance;
                _xOffset.Value = p.CenterPercent;
                _xIcon.Value = p.IconSize;
                _xZoom.Value = Math.Max(p.IconSize, p.ZoomSize);
            }
            finally { _extraLoading = false; }
            UpdateExtraEnabled();
        }

        /// <summary>Die Felder der Seite „Weitere Docks“ in das gerade gezeigte Dock zurückschreiben.</summary>
        private void StoreExtra()
        {
            if (_extraLoading || _extraShown < 0 || _extraEdit[_extraShown] == null) return;
            DockPlace p = _extraEdit[_extraShown];
            p.Enabled = _xEnabled.Checked;
            p.Monitor = _xMonitor.SelectedIndex - 1;
            p.Edge = (DockEdge)Math.Max(0, _xEdge.SelectedIndex);
            p.EdgeDistance = _xDist.Value;
            p.CenterPercent = _xOffset.Value;
            p.IconSize = _xIcon.Value;
            p.ZoomSize = Math.Max(_xIcon.Value, _xZoom.Value);
        }

        /// <summary>
        /// Das gerade gezeigte weitere Dock auf einen Rand legen, an dem auf seinem Bildschirm noch kein anderes
        /// eingeschaltetes Dock sitzt (zuerst gegenüber von Dock 1).
        /// </summary>
        private void FreeEdgeForExtra()
        {
            int monitor = _xMonitor.SelectedIndex;
            List<int> taken = new List<int>();
            if (_monitor.SelectedIndex == monitor) taken.Add(Math.Max(0, _edge.SelectedIndex));
            for (int i = 0; i < Settings.ExtraDocks; i++)
            {
                DockPlace p = _extraEdit[i];
                if (i == _extraShown || p == null || !p.Enabled || p.Monitor + 1 != monitor) continue;
                taken.Add((int)p.Edge);
            }
            if (!taken.Contains(Math.Max(0, _xEdge.SelectedIndex))) return;
            int main = Math.Max(0, _edge.SelectedIndex);
            foreach (int edge in new[] { main ^ 1, 2, 3, 0, 1 }) // gegenüber, dann links, rechts, oben, unten
            {
                if (taken.Contains(edge)) continue;
                _xEdge.SelectedIndex = edge;
                return;
            }
        }

        private void UpdateExtraEnabled()
        {
            bool on = _xEnabled.Checked;
            _xMonitor.Enabled = _xEdge.Enabled = _xDist.Enabled = _xOffset.Enabled = _xIcon.Enabled = _xZoom.Enabled = on;
        }

        /// <summary>Einträge für die Effekt-Vorschau (vom Tray gesetzt, nach dem Erzeugen des Fensters).</summary>
        public void SetPreviewItems(List<DockItem> items)
        {
            if (_effectPreview != null) _effectPreview.Setup(_edit, items, _sc);
        }

        /// <summary>
        /// Live-Vorschau: Sobald sich ein Feld ändert, zeigen das Beispiel-Dock und das echte Dock den neuen Stand.
        /// Gespeichert wird erst mit „Speichern“; Schließen oder Abbrechen stellt den gespeicherten Stand wieder her.
        /// </summary>
        private void LiveTick()
        {
            if (!Visible || IsDisposed || _baseline == null || _raise == null) return;
            string snap = Snapshot();
            if (snap == _liveSnap) return;
            _liveSnap = snap;
            if (_effectPreview != null) _effectPreview.Apply(_edit);
            if (Live == null) return;
            if (snap == _baseline) { Live(null); return; }
            Settings p = _edit.Clone();
            // das Tastenkürzel wird erst mit dem Speichern umgestellt (und dabei geprüft)
            p.HotkeyModifiers = _original.HotkeyModifiers;
            p.HotkeyKey = _original.HotkeyKey;
            Live(p);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _liveTimer.Stop();
            if (Live != null) Live(null); // nicht Gespeichertes verwerfen
            base.OnFormClosed(e);
        }

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
            for (int i = 0; i < Settings.ExtraDocks; i++) _extraEdit[i] = _edit.Extra[i].Clone();
            _extraShown = -1;
            ShowExtra(Math.Max(0, _dockPick.SelectedIndex));
            _zoomMode.SelectedIndex = Math.Max(0, Array.IndexOf(ZoomModes, _edit.ZoomMode));
            _highlight.SelectedIndex = Math.Max(0, Array.IndexOf(HighlightModes, _edit.Highlight));
            _strength.Value = _edit.EffectStrength;
            _reflection.Checked = _edit.Reflection;
            _preview.Checked = _edit.Preview;
            _unpinned.Checked = _edit.ShowUnpinned;
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
            // Größe unter der Maus und Nachbarn gelten nur für die Zoom-Effekte Automatisch, Blase und Lupe
            int zm = ZoomModes[Math.Max(0, _zoomMode.SelectedIndex)];
            bool zoom = zm <= 2 || zm == 5; // nur Effekte mit Vergrößerung
            _strength.Enabled = _highlight.SelectedIndex > 0 || zm == 10;
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
            StoreExtra();
            for (int i = 0; i < Settings.ExtraDocks; i++) _edit.Extra[i] = _extraEdit[i].Clone();
            _edit.ZoomMode = ZoomModes[Math.Max(0, _zoomMode.SelectedIndex)];
            _edit.Highlight = HighlightModes[Math.Max(0, _highlight.SelectedIndex)];
            _edit.EffectStrength = _strength.Value;
            _edit.Reflection = _reflection.Checked;
            _edit.Preview = _preview.Checked;
            _edit.ShowUnpinned = _unpinned.Checked;
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
            _edit.Profile = _original.Profile;         // wird im Tray-Menü umgeschaltet
            bool autoWanted = _autostart.Checked;
            _original.CopyFrom(_edit);
            _original.Save();
            // gespeichert: Das Dock nimmt wieder die gespeicherten Einstellungen (gleicher Stand wie die Vorschau)
            _liveSnap = null;
            if (Live != null) Live(null);
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
    }
}
