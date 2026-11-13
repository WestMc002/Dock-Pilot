// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>
    /// Das Dock: ein randloses Fenster mit echter Transparenz (UpdateLayeredWindow), das selbst gezeichnet wird.
    /// Durchsichtige Bereiche lassen die Maus durch. Die Maus wird per Timer abgefragt – so funktionieren
    /// Vergrößerung, automatisches Ausblenden und das Einblenden am Bildschirmrand ohne globale Hooks.
    /// Aufgeteilt: DockWindow.cs (Zustand, Einstellungen, Timer, Systemereignisse), DockLayout.cs (Anordnung),
    /// DockPaint.cs (Zeichnen), DockMouse.cs (Maus, Ziehen), DockLaunch.cs (Starten, Vorschau, Kontextmenü),
    /// DockItems.cs (Einträge ändern), DockDemo.cs (Screenshots).
    /// </summary>
    internal sealed partial class DockWindow : Form
    {
        private sealed class Slot
        {
            public DockItem Item;
            public Bitmap Master, Base, Zoom;
            public HashSet<string> Exes = new HashSet<string>();
            public List<IntPtr> Windows = new List<IntPtr>();
            public DateTime BounceStart = DateTime.MinValue;
            public DateTime HopStart = DateTime.MinValue;   // Zoom-Effekt Federn
            public float BaseStart, Pos, Len, Size;
            public Bitmap Reflect;                          // Spiegelung (aus Base bzw. Zoom, mit Verlauf)
            public Bitmap ReflectSrc;
            public DockEdge ReflectEdge;
            public int BinState = -1;                       // Papierkorb: -1 unbekannt, 0 leer, 1 voll
            public float Rest, Slide;                       // Ruheplatz; Versatz dazu, solange das Symbol an seinen Platz gleitet
            public float Appear = 1f;                       // neu eingefügt: wächst von 0 auf 1

            public void DisposeReflect()
            {
                if (Reflect != null) Reflect.Dispose();
                Reflect = null;
                ReflectSrc = null;
            }

            public void DisposeImages()
            {
                if (Master != null) Master.Dispose();
                if (Base != null) Base.Dispose();
                if (Zoom != null) Zoom.Dispose();
                Master = Base = Zoom = null;
                DisposeReflect();
            }
        }

        private const int HotkeyId = 1;

        private Settings _s;                 // gerade wirksame Einstellungen: die gespeicherten oder die Vorschau aus dem Einstellungsfenster
        private readonly Settings _saved;    // die gespeicherten Einstellungen
        private bool _animDemo;              // Beispiel-Dock im Einstellungsfenster: ohne Fenster, aber mit Animationen
        private List<DockItem> _items;
        private readonly List<Slot> _slots = new List<Slot>();

        // Aufrufe nach außen (TrayContext)
        public MethodInvoker OpenSettings, ShowAbout, ExitApp;

        // Maße in Bildschirmpixeln
        private float _sc = 1f;
        private Rectangle _winRect, _monitor;
        private int _T, _Lwin;
        private float _basePx, _zoomPx, _padA, _padL, _spacing, _sepLen, _edgeDist, _shelfThick, _labelGap, _bounceH, _axisCenter;
        private float _shelfStart, _shelfEnd;
        private float _edge0, _edge1;   // Blase am Rand: so weit wächst die Leiste über ihr Ende hinaus
        private bool _overlap;          // Blase mit fester Breite, Art „überlappen“: Symbole liegen übereinander

        // Zustand
        private readonly System.Windows.Forms.Timer _timer;
        private DateTime _last = DateTime.Now;
        private float _show, _zoom, _mouseAlong;
        private bool _hot, _menuOpen, _userHidden, _hotkeyShown, _fullscreen;
        private DateTime _lastInside = DateTime.MinValue, _edgeSince = DateTime.MinValue, _hotkeyTime = DateTime.MinValue;
        private DateTime _nextRunning = DateTime.MinValue, _nextFullscreen = DateTime.MinValue;
        private int _hover = -1, _pressIndex = -1;
        private Point _pressPoint;
        private bool _dragging, _extDrag;
        private DockItem _undoItem;      // zuletzt entfernter Eintrag (für „Entfernen rückgängig“, bis zum Beenden)
        private int _undoIndex;
        private List<KeyValuePair<string, byte[]>> _undoFiles; // zuletzt aus einem Ordnermenü entfernte Dateien (Pfad, Inhalt)
        private DateTime _nextBin = DateTime.MinValue;
        private bool _binBusy;
        private bool _dragOut;           // Symbol aus dem Dock herausgezogen: Loslassen entfernt es (wie RocketDock)
        private DragGhost _ghost;        // schwebendes Symbol außerhalb des Docks
        private int _dragIndex = -1, _dropIndex = -1, _dropOnto = -1;
        private bool _dirty = true;
        private bool _hotkeyOk = true;
        private bool _dark;
        private ContextMenuStrip _lastMenu;
        private ContextMenuStrip _openMenu;            // offenes Menü des Docks (klappt zu, wenn die Maus weg ist)
        private ContextMenuStrip _folderMenu;          // offenes Ordnermenü (für Wechseln auf ein anderes)
        private int _folderIndex = -1, _switchTo = -1;
        private DateTime _menuAwaySince = DateTime.MinValue, _switchSince = DateTime.MinValue;
        private bool _demo; // Screenshot-Modus: kein Bildschirm, kein Timer, kein Tastenkürzel
        private bool _raised;
        private bool _fixedZoom;
        private float _focus;                              // Fokus: wie weit die entfernten Symbole abgeblendet sind (0..1)
        private DateTime _focusSince = DateTime.MinValue;  // seit wann die Maus über dem Dock ist (Abblenden beginnt verzögert)
        private int _effectIndex = -1; // Symbol unter der Maus bei den ruhigen Effekten (Anheben, Leuchten, Fokus, Federn)
        private PreviewPopup _preview;            // Fenstervorschau über laufenden Programmen
        private int _previewIndex = -1, _previewWant = -1, _previewBlock = -1; // Block: nach einem Klick erst wieder zeigen, wenn die Maus das Symbol verlassen hat
        private DateTime _previewSince = DateTime.MinValue, _previewAway = DateTime.MinValue;
        private int _middleIndex = -1; // Symbol, auf dem die mittlere Maustaste gedrückt wurde
        private int _hopIndex = -1;    // zuletzt angehüpftes Symbol (Federn)
        private DateTime _raiseSince = DateTime.MinValue;
        private Color _accent = Color.FromArgb(0, 120, 212);
        private Bitmap _mica;
        private string _micaKey;

        // Zeichenfläche (DIB, vormultipliziertes Alpha wie von UpdateLayeredWindow verlangt)
        private IntPtr _memDc, _dib, _oldBmp, _bits;
        private Bitmap _surface;
        private Graphics _g;
        private int _bufW, _bufH;

        public DockWindow(Settings settings, List<DockItem> items)
        {
            _s = settings;
            _saved = settings;
            _items = items;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = AppInfo.Name;
            AllowDrop = true;
            TopMost = settings.AlwaysOnTop;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15;
            _timer.Tick += delegate { Tick(); };

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= DockNative.WS_EX_LAYERED | DockNative.WS_EX_TOOLWINDOW | DockNative.WS_EX_NOACTIVATE;
                if (_s == null || _s.AlwaysOnTop) cp.ExStyle |= DockNative.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
            if (!_demo) _shellMsg = DockNative.RegisterShellHook(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _show = _s.AutoHide ? 0f : 1f;
            ReloadSlots();
            DoLayout();
            Render();
            _timer.Start();
        }

        /// <summary>Konnte das Tastenkürzel angemeldet werden (sonst nutzt es schon ein anderes Programm)?</summary>
        public bool HotkeyOk { get { return _hotkeyOk; } }

        /// <summary>Die gespeicherten Einträge (ohne die nur vorübergehend gezeigten laufenden Programme).</summary>
        public List<DockItem> Items { get { return _items.FindAll(delegate (DockItem i) { return !i.Temp; }); } }

        // ------------------------------------------------------------------ Einstellungen und Einträge

        /// <summary>
        /// Live-Vorschau aus dem Einstellungsfenster: Das Dock zeigt sofort die noch nicht gespeicherten Einstellungen.
        /// null = wieder die gespeicherten.
        /// </summary>
        public void UsePreview(Settings preview)
        {
            Settings next = preview ?? _saved;
            if (next == _s && preview == null) return;
            _s = next;
            ApplySettings();
        }

        /// <summary>Maße, Position und Tastenkürzel neu berechnen (nach dem Speichern der Einstellungen).</summary>
        public void ApplySettings()
        {
            if (_demo) return;
            Screen scr = Screen.PrimaryScreen;
            Screen[] all = Screen.AllScreens;
            if (_s.Monitor >= 0 && _s.Monitor < all.Length) scr = all[_s.Monitor];
            ApplyMetrics(scr.WorkingArea, scr.Bounds, MonitorScale(scr.WorkingArea));
            Bounds = _winRect;
            // Größer gestellt (oder höhere Skalierung)? Dann Symbole in passender Auflösung neu laden, sonst wären sie unscharf
            List<Slot> sharper = new List<Slot>();
            float need = Math.Min(256f, _zoomPx);
            foreach (Slot s in _slots)
                if (s.Master != null && Math.Max(s.Master.Width, s.Master.Height) < need - 0.5f) sharper.Add(s);
            if (sharper.Count > 0 && IsHandleCreated) LoadIconsAsync(sharper);
            if (TopMost != _s.AlwaysOnTop) TopMost = _s.AlwaysOnTop;
            _raised = false;
            _dark = MenuTheme.Current().Dark;
            _accent = Theme.SystemAccent;
            LoadMica();
            RegisterHotkey();
            if (_s.Locked && _dragging) EndDrag();
            if (!_s.AutoHide) _hotkeyShown = false;
            _dirty = true;
        }

        /// <summary>Maße und Lage aus Arbeitsbereich, Bildschirm und Skalierung (auch für die Screenshots ohne echten Bildschirm).</summary>
        private void ApplyMetrics(Rectangle work, Rectangle monitor, float scale)
        {
            _monitor = monitor;
            float oldScale = _sc, oldBase = _basePx, oldZoom = _zoomPx;
            _sc = scale;
            bool vertical = _s.Vertical;
            _Lwin = vertical ? work.Height : work.Width;

            _padA = R(8); _padL = R(10);
            // Abstände wie bei RocketDock knapp halten, sonst passen viele Einträge nicht auf den Bildschirm und alles wird verkleinert
            _spacing = R(_s.Spacing); _sepLen = R(8);
            _edgeDist = R(_s.EdgeDistance);
            _basePx = R(_s.IconSize);
            _zoomPx = Math.Max(_basePx, R(_s.ZoomSize));

            // Passt das Dock nicht auf den Bildschirm, werden Symbole und Abstände verkleinert
            int n = _items.Count, seps = 0;
            foreach (DockItem it in _items) if (it.IsSeparator) seps++;
            float need = (n - seps) * _basePx + seps * _sepLen + Math.Max(0, n - 1) * _spacing + 2 * _padL + R(16);
            if (need > _Lwin && need > 0)
            {
                float f = Math.Max(0.35f, (_Lwin - 2 * _padL - R(16)) / (need - 2 * _padL - R(16)));
                _basePx = (float)Math.Floor(_basePx * f);
                _spacing = Math.Max(2f, (float)Math.Floor(_spacing * f));
                _sepLen = Math.Max(6f, (float)Math.Floor(_sepLen * f));
                _zoomPx = Math.Max(_basePx, _zoomPx);
            }

            _shelfThick = _basePx + 2 * _padA;
            _labelGap = R(8);
            _bounceH = R(14);
            float labelH = R(32);
            _T = (int)Math.Ceiling(_edgeDist + _padA + Math.Max(_zoomPx, _basePx + _bounceH) + _labelGap + labelH + R(10));
            _axisCenter = _Lwin / 2f; // genaue Lage nach der Zentrierung in DoLayout

            switch (_s.Edge)
            {
                case DockEdge.Oben: _winRect = new Rectangle(work.Left, work.Top, _Lwin, _T); break;
                case DockEdge.Unten: _winRect = new Rectangle(work.Left, work.Bottom - _T, _Lwin, _T); break;
                case DockEdge.Links: _winRect = new Rectangle(work.Left, work.Top, _T, _Lwin); break;
                default: _winRect = new Rectangle(work.Right - _T, work.Top, _T, _Lwin); break;
            }

            if (oldScale != _sc || oldBase != _basePx || oldZoom != _zoomPx)
            {
                foreach (Slot s in _slots) Rescale(s);
            }
        }

        private float R(float v) { return (float)Math.Round(v * _sc); }

        private static float MonitorScale(Rectangle area)
        {
            try
            {
                IntPtr mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(area.Left + area.Width / 2, area.Top + area.Height / 2), NativeMethods.MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (NativeMethods.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        /// <summary>Einträge neu übernehmen (nach Import, Eigenschaften, Hinzufügen) und Symbole laden.</summary>
        public void SetItems(List<DockItem> items)
        {
            _items = items;
            ReloadSlots();
        }

        public void ReloadSlots()
        {
            foreach (Slot s in _slots) s.DisposeImages();
            _slots.Clear();
            foreach (DockItem it in _items) _slots.Add(new Slot { Item = it });
            _hover = -1;
            ApplySettings();
            LoadIconsAsync(new List<Slot>(_slots));
        }

        /// <summary>Einen Eintrag neu laden (z. B. nach "Eigenschaften").</summary>
        private void ReloadSlot(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            _slots[index].DisposeImages();
            _slots[index].Item = _items[index];
            LoadIconsAsync(new List<Slot> { _slots[index] });
        }

        private void Save()
        {
            DockStore.Save(_items, Number);
        }

        /// <summary>Nummer des Docks: 1 = erstes Dock, 2 und 3 = weitere Docks (eigene Einträge, eigene Position).</summary>
        public int Number = 1;

        /// <summary>Die gemeinsamen, gespeicherten Einstellungen aller Docks (bei einem weiteren Dock ist _saved nur eine Kopie).</summary>
        public Settings Shared;

        /// <summary>Gemeinsame Einstellungen wurden vom Dock aus geändert (Kontextmenü): alle Docks anpassen. Vom Tray gesetzt.</summary>
        public Action SharedChanged;

        /// <summary>Das Tastenkürzel zum Ein-/Ausblenden wurde gedrückt (nur Dock 1 meldet es an). Vom Tray gesetzt.</summary>
        public Action HotkeyPressed;

        /// <summary>Weiteres Dock: neue Einstellungen übernehmen (gespeicherte oder Vorschau, vom Tray zusammengesetzt).</summary>
        public void UpdateSettings(Settings composed)
        {
            _saved.CopyFrom(composed);
            _s = _saved;
            ApplySettings();
        }

        /// <summary>Weiteres Dock: ein-/ausblenden wie Dock 1 beim Tastenkürzel, aber ohne die Tastatur zu übernehmen.</summary>
        public void ToggleQuiet()
        {
            _quietToggle = true;
            try { ToggleByHotkey(); } finally { _quietToggle = false; }
        }

        private bool _quietToggle;

        /// <summary>Symbole im Hintergrund laden (Netzwerkpfade können dauern), danach einsetzen.</summary>
        private void LoadIconsAsync(List<Slot> slots)
        {
            int size = (int)Math.Max(48, Math.Min(256, Math.Ceiling(_zoomPx)));
            Thread t = new Thread(delegate ()
            {
                foreach (Slot s in slots)
                {
                    if (s.Item.IsSeparator) continue;
                    Bitmap master = IconLoader.Load(s.Item, size);
                    HashSet<string> exes = ExeKeys(s.Item);
                    Slot slot = s;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (!_slots.Contains(slot)) { master.Dispose(); return; }
                            if (slot.Master != null) slot.Master.Dispose();
                            slot.Master = master;
                            slot.Exes = exes;
                            Rescale(slot);
                            _nextRunning = DateTime.MinValue;
                            _dirty = true;
                        });
                    }
                    catch { master.Dispose(); return; } // Fenster bereits geschlossen
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void Rescale(Slot s)
        {
            if (s.Master == null) return;
            if (s.Base != null) s.Base.Dispose();
            if (s.Zoom != null) s.Zoom.Dispose();
            s.DisposeReflect();
            s.Base = IconLoader.Fit(s.Master, Math.Max(1, (int)_basePx));
            s.Zoom = IconLoader.Fit(s.Master, Math.Max(1, (int)_zoomPx));
        }

        /// <summary>EXE-Dateien, an denen ein laufendes Programm erkannt wird (Ziel, Ziel der Verknüpfung, Symboldatei).</summary>
        private static HashSet<string> ExeKeys(DockItem it)
        {
            HashSet<string> set = new HashSet<string>();
            if (it.Kind != ItemKind.Programm) return set;
            try
            {
                string cmd = IconLoader.Expand(it.Command);
                if (cmd.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    ShortcutInfo lnk = ShortcutInfo.Read(cmd);
                    if (lnk != null) cmd = lnk.Target;
                }
                if (cmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) set.Add(Path.GetFullPath(cmd).ToLowerInvariant());
                if (!string.IsNullOrEmpty(it.Icon))
                {
                    int idx;
                    string f = IconLoader.SplitSpec(it.Icon, out idx);
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) set.Add(Path.GetFullPath(f).ToLowerInvariant());
                }
            }
            catch { }
            return set;
        }

        // ------------------------------------------------------------------ Ablauf (Timer)

        private void Tick()
        {
            DateTime now = DateTime.Now;
            float dt = (float)Math.Max(0, Math.Min(0.1, (now - _last).TotalSeconds));
            _last = now;

            NativeMethods.POINT cur;
            NativeMethods.GetCursorPos(out cur);
            float along, across;
            ToDock(cur.X, cur.Y, out along, out across);

            if (now >= _nextFullscreen)
            {
                _nextFullscreen = now.AddMilliseconds(500);
                bool fs = _s.HideOnFullscreen && DockNative.FullscreenOn(_monitor);
                if (fs != _fullscreen) { _fullscreen = fs; _dirty = true; }
            }

            if (_openMenu != null && MenuTick(now, cur.X, cur.Y, along, across)) return;
            if (_keyMode) KeyTick(now, cur.X, cur.Y, ref along, ref across); // Tastatur: das gewählte Symbol zählt als Mausposition

            bool busy = _menuOpen || _dragging || _extDrag || _keyMode;
            // Maus in der Fenstervorschau: das Dock bleibt sichtbar
            bool overPreview = PreviewVisible && _preview.Bounds.Contains(new Point(cur.X, cur.Y));
            if (overPreview) busy = true;
            bool inside = !_menuOpen && InHot(along, across);
            if (inside || busy) _lastInside = now;
            if (inside && _hotkeyShown) _hotkeyShown = false; // ab jetzt gelten die normalen Regeln
            if (_hotkeyShown && (now - _hotkeyTime).TotalSeconds > 4 && !inside) _hotkeyShown = false;

            // --- sichtbar oder ausgeblendet?
            float showTarget;
            if (_userHidden && !_hotkeyShown) showTarget = 0f;
            else if (_hotkeyShown || busy) showTarget = 1f;
            else if (_fullscreen) showTarget = 0f;
            else if (!_s.AutoHide) showTarget = 1f;
            else if (_show > 0.01f && (inside || (now - _lastInside).TotalMilliseconds < Math.Max(_s.HideDelay, 250))) showTarget = 1f;
            else if (_show > 0.01f) showTarget = 0f;
            else
            {
                // ausgeblendet: Maus am Bildschirmrand im Bereich des Docks einblenden
                // ein Dock mit wenigen Symbolen wäre am Rand kaum zu treffen: mindestens 240 px breit auslösen
                float wider = Math.Max(0f, (R(240) - (_shelfEnd - _shelfStart)) / 2f);
                bool atEdge = across >= 0 && across <= 1 && along >= _shelfStart - wider && along <= _shelfEnd + wider;
                if (atEdge)
                {
                    if (_edgeSince == DateTime.MinValue) _edgeSince = now;
                    showTarget = (now - _edgeSince).TotalMilliseconds >= _s.ShowDelay ? 1f : 0f;
                    if (showTarget > 0) _lastInside = now;
                }
                else
                {
                    _edgeSince = DateTime.MinValue;
                    showTarget = 0f;
                }
            }
            if (_show > 0.01f) _edgeSince = DateTime.MinValue;

            UpdateRaise(now, inside, across >= 0 && across <= 1 && along >= _shelfStart && along <= _shelfEnd, busy);

            // --- Vergrößerung
            float zoomTarget = inside && _show > 0.95f && !_dragging ? 1f : 0f;
            if (_menuOpen) zoomTarget = _zoom; // bei offenem Menü einfrieren
            else if (Math.Abs(_mouseAlong - along) > 0.01f && (inside || _zoom > 0)) { _mouseAlong = along; _dirty = true; }

            bool animating = false;
            float ns = Approach(_show, showTarget, dt / (0.18f * AnimFactor));
            float nz = Approach(_zoom, zoomTarget, dt / (0.12f * AnimFactor));
            if (ns != _show || nz != _zoom) { _show = ns; _zoom = nz; _dirty = true; animating = true; }

            // --- Fokus: erst nach kurzem Verweilen und langsam abblenden – wer nur über die Leiste fährt, löst nichts aus
            if (_s.Highlight == 7 || _s.Highlight == 14)
            {
                bool over = zoomTarget > 0f;
                if (!over) _focusSince = DateTime.MinValue;
                else if (_focusSince == DateTime.MinValue) _focusSince = now;
                bool waiting = over && (now - _focusSince).TotalMilliseconds < 150;
                float focusTarget = _menuOpen ? _focus : over && !waiting ? 1f : 0f;
                float nf = _s.Animate ? Approach(_focus, focusTarget, dt / ((focusTarget > _focus ? 0.22f : 0.22f) * AnimFactor)) : focusTarget;
                if (nf != _focus) { _focus = nf; _dirty = true; animating = true; }
                else if (waiting) animating = true; // während der Wartezeit weiter schnell takten
            }
            else _focus = 0f;
            // Pulsieren: läuft, solange die Maus auf einem Symbol steht
            if (_s.ZoomMode == 10 && _effectIndex >= 0 && _zoom > 0f && _s.Animate) { animating = true; _dirty = true; }

            int hover = inside && !_dragging ? HitIndex(along, across) : -1;
            if (hover != _hover) { _hover = hover; _dirty = true; }
            PreviewTick(now, hover, overPreview);
            if (inside != _hot) { _hot = inside; _dirty = true; }

            // --- laufende Programme
            if ((_s.ShowRunning || _s.ActivateRunning || _s.ShowUnpinned || FirstTemp() < _items.Count) && now >= _nextRunning)
            {
                _nextRunning = now.AddMilliseconds(1500);
                if (RefreshRunning()) _dirty = true;
            }

            // --- Papierkorb leer/voll (Abfrage im Hintergrund, Netzlaufwerke können dauern)
            if (now >= _nextBin)
            {
                _nextBin = now.AddSeconds(4);
                CheckRecycleBin();
            }

            // --- Hüpfen nach dem Start
            foreach (Slot s in _slots)
                if (s.BounceStart != DateTime.MinValue)
                {
                    animating = true;
                    _dirty = true;
                    if ((now - s.BounceStart).TotalMilliseconds > 900 * AnimFactor) s.BounceStart = DateTime.MinValue;
                }
            foreach (Slot s in _slots)
                if (s.HopStart != DateTime.MinValue)
                {
                    animating = true;
                    _dirty = true;
                    if ((now - s.HopStart).TotalMilliseconds > HopMs * AnimFactor) s.HopStart = DateTime.MinValue;
                }

            // --- Gleiten an den neuen Platz, Erscheinen neuer Symbole, Leuchten bei blinkenden Fenstern
            float glideK = (float)Math.Exp(-dt / (0.07f * AnimFactor));
            foreach (Slot s in _slots)
            {
                if (s.Slide != 0f)
                {
                    s.Slide = _s.Animate && Math.Abs(s.Slide * glideK) >= 0.5f ? s.Slide * glideK : 0f;
                    animating = true;
                    _dirty = true;
                }
                if (s.Appear < 1f)
                {
                    s.Appear = _s.Animate ? Math.Min(1f, s.Appear + dt / (0.2f * AnimFactor)) : 1f;
                    animating = true;
                    _dirty = true;
                }
                if (_flashing.Count > 0 && Attention(s)) { animating = true; _dirty = true; }
            }

            if (_dirty)
            {
                _dirty = false;
                DoLayout();
                Render();
            }
            _timer.Interval = animating || inside || busy || _show > 0 && _s.AutoHide && !inside && showTarget > 0 ? 15 : 100;
        }

        /// <summary>Tempo der Animationen: schnell 0,6 ×, normal 1 ×, langsam 1,6 × die Grunddauer.</summary>
        private float AnimFactor
        {
            get { return _s.AnimSpeed == 0 ? 0.6f : _s.AnimSpeed == 2 ? 1.6f : 1f; }
        }

        /// <summary>
        /// Bildschirmebene "Normal": Steht die Maus über dem Dock (bzw. am Rand davor), wird es nach der Einblend-Verzögerung
        /// nach vorne geholt und danach wieder in die normale Ebene gelegt.
        /// </summary>
        private void UpdateRaise(DateTime now, bool inside, bool atEdge, bool busy)
        {
            if (_demo || !IsHandleCreated) return;
            if (_s.AlwaysOnTop)
            {
                _raiseSince = DateTime.MinValue;
                _raised = false;
                return;
            }
            if (!_s.RaiseOnHover) return;
            if ((inside || atEdge) && !_raised)
            {
                if (_raiseSince == DateTime.MinValue) _raiseSince = now;
                if ((now - _raiseSince).TotalMilliseconds >= _s.ShowDelay)
                {
                    DockNative.SetWindowPos(Handle, DockNative.HWND_TOPMOST, 0, 0, 0, 0, DockNative.SWP_NOMOVE_NOSIZE_NOACTIVATE);
                    _raised = true;
                }
            }
            else if (!inside && !atEdge)
            {
                _raiseSince = DateTime.MinValue;
                if (_raised && !busy && (now - _lastInside).TotalMilliseconds > Math.Max(_s.HideDelay, 250))
                {
                    // bleibt vor den übrigen Fenstern, bis ein anderes Fenster aktiviert wird
                    DockNative.SetWindowPos(Handle, DockNative.HWND_NOTOPMOST, 0, 0, 0, 0, DockNative.SWP_NOMOVE_NOSIZE_NOACTIVATE);
                    _raised = false;
                }
            }
        }

        private float Approach(float v, float target, float step)
        {
            if (!_s.Animate) return target;
            if (v < target) return Math.Min(target, v + step);
            if (v > target) return Math.Max(target, v - step);
            return v;
        }

        /// <summary>Bildschirmpunkt in Dock-Koordinaten: entlang der Leiste und Abstand vom Rand.</summary>
        private void ToDock(int x, int y, out float along, out float across)
        {
            switch (_s.Edge)
            {
                case DockEdge.Oben: along = x - _winRect.Left; across = y - _winRect.Top; break;
                case DockEdge.Unten: along = x - _winRect.Left; across = _winRect.Bottom - 1 - y; break;
                case DockEdge.Links: along = y - _winRect.Top; across = x - _winRect.Left; break;
                default: along = y - _winRect.Top; across = _winRect.Right - 1 - x; break;
            }
        }

        private float MaxSize()
        {
            float m = _basePx;
            foreach (Slot s in _slots) if (!s.Item.IsSeparator && s.Size > m) m = s.Size;
            return m;
        }

        private bool InHot(float along, float across)
        {
            if (_show < 0.5f) return false;
            // Hysterese: Hinein zählt nur über der Leiste, drinnen bleibt man bis zur vollen Zoomhöhe.
            // Sonst schrumpft der Zoom, die Maus ist plötzlich "draußen", der Zoom wächst wieder – Flackern.
            if (_hot)
            {
                float depth = _edgeDist + Math.Max(_shelfThick, _padA + _zoomPx + _padA);
                return across >= 0 && across <= depth && along >= _shelfStart - _spacing && along <= _shelfEnd + _spacing;
            }
            return across >= 0 && across <= _edgeDist + _shelfThick && along >= _shelfStart && along <= _shelfEnd;
        }

        private int HitIndex(float along, float across)
        {
            return HitIndex(along, across, false);
        }

        private int HitIndex(float along, float across, bool separators)
        {
            float a0 = _edgeDist;
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                if (s.Item.IsSeparator && !separators) continue;
                // bei festem Zoom zählt der eigene Platz in der Leiste (die Symbole überdecken sich)
                // beim Überlappen ist das Symbol breiter als sein Platz: es zählt die Mitte
                float from = _fixedZoom ? s.BaseStart : s.Item.IsSeparator ? s.Pos : s.Pos + s.Size / 2f - s.Len / 2f;
                float len = _fixedZoom ? BaseLen(s) : s.Len;
                if (along >= from - _spacing / 2 && along <= from + len + _spacing / 2 &&
                    across >= 0 && across <= a0 + _padA + Math.Max(s.Size, _basePx) + _padA)
                    return i;
            }
            return -1;
        }

        private const string RecycleBinId = "645FF040-5081-101B-9F08-00AA002F954E";

        private static bool IsRecycleBin(DockItem it)
        {
            return it != null && it.Kind == ItemKind.Programm && it.Command.IndexOf(RecycleBinId, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Papierkorb im Dock: wechselt er zwischen leer und voll, wird sein Symbol neu geladen
        /// (Windows liefert je nach Zustand das passende). Nicht bei einem eigenen Symbol.
        /// </summary>
        private void CheckRecycleBin()
        {
            if (_binBusy || _demo) return;
            bool any = false;
            foreach (Slot s in _slots) if (IsRecycleBin(s.Item)) { any = true; break; }
            if (!any) return;
            _binBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long count = DockNative.RecycleBinItems();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _binBusy = false;
                        if (count < 0) return;
                        int state = count > 0 ? 1 : 0;
                        List<Slot> reload = new List<Slot>();
                        foreach (Slot s in _slots)
                        {
                            if (!IsRecycleBin(s.Item) || s.BinState == state) continue;
                            bool first = s.BinState < 0;
                            s.BinState = state;
                            if (!first && string.IsNullOrEmpty(s.Item.Icon)) reload.Add(s);
                        }
                        if (reload.Count > 0) LoadIconsAsync(reload);
                    });
                }
                catch { _binBusy = false; }
            });
        }

        private bool RefreshRunning()
        {
            Dictionary<string, List<IntPtr>> windows;
            try { windows = DockNative.TaskWindows(); }
            catch { return false; }
            bool changed = false;
            foreach (Slot s in _slots)
            {
                List<IntPtr> list = new List<IntPtr>();
                foreach (string exe in s.Exes)
                {
                    List<IntPtr> w;
                    if (windows.TryGetValue(exe, out w)) list.AddRange(w);
                }
                if ((list.Count > 0) != (s.Windows.Count > 0)) changed = true;
                s.Windows = list;
            }
            if (_flashing.Count > 0)
            {
                // geschlossene Fenster blinken nicht mehr
                HashSet<IntPtr> open = new HashSet<IntPtr>();
                foreach (List<IntPtr> w in windows.Values) foreach (IntPtr h in w) open.Add(h);
                _flashing.IntersectWith(open);
            }
            if (SyncUnpinned(windows)) changed = true;
            return changed;
        }

        // ------------------------------------------------------------------ Tastenkürzel, Systemereignisse

        private void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            DockNative.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyOk = true;
            if (_s.HotkeyKey > 0 && Number == 1) // weitere Docks folgen Dock 1 (HotkeyPressed)
                _hotkeyOk = DockNative.RegisterHotKey(Handle, HotkeyId, _s.HotkeyModifiers | DockNative.MOD_NOREPEAT, _s.HotkeyKey);
            RegisterItemHotkeys();
        }

        /// <summary>Prüft, ob ein Tastenkürzel frei ist (für die Einstellungen). Das bisherige bleibt angemeldet.</summary>
        public bool TestHotkey(int modifiers, int key)
        {
            if (key <= 0) return true;
            if (modifiers == _s.HotkeyModifiers && key == _s.HotkeyKey && _hotkeyOk) return true;
            if (!DockNative.RegisterHotKey(Handle, 99, modifiers | DockNative.MOD_NOREPEAT, key)) return false;
            DockNative.UnregisterHotKey(Handle, 99);
            return true;
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate { ApplySettings(); _dirty = true; });
        }

        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                ApplySettings(); // Farben, Hell/Dunkel, Akzentfarbe, Arbeitsbereich (Taskleiste verschoben)
                _dirty = true;
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                if (_ghost != null) _ghost.Dispose();
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
                if (IsHandleCreated) DockNative.UnregisterHotKey(Handle, HotkeyId);
                if (IsHandleCreated && _shellMsg != 0) DockNative.UnregisterShellHook(Handle);
                if (IsHandleCreated) foreach (int id in _itemHotkeys.Keys) DockNative.UnregisterHotKey(Handle, id);
                foreach (Slot s in _slots) s.DisposeImages();
                if (_mica != null) _mica.Dispose();
                FreeBuffer();
            }
            base.Dispose(disposing);
        }
    }
}
