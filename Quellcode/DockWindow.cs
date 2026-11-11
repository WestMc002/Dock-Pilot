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
    /// </summary>
    internal sealed class DockWindow : Form
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

        private readonly Settings _s;
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

        public List<DockItem> Items { get { return _items; } }

        // ------------------------------------------------------------------ Einstellungen und Einträge

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
            DockStore.Save(_items);
        }

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

            bool busy = _menuOpen || _dragging || _extDrag;
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
                bool atEdge = across >= 0 && across <= 1 && along >= _shelfStart && along <= _shelfEnd;
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

            int hover = inside && !_dragging ? HitIndex(along, across) : -1;
            if (hover != _hover) { _hover = hover; _dirty = true; }
            PreviewTick(now, hover, overPreview);
            if (inside != _hot) { _hot = inside; _dirty = true; }

            // --- laufende Programme
            if ((_s.ShowRunning || _s.ActivateRunning) && now >= _nextRunning)
            {
                _nextRunning = now.AddMilliseconds(1500);
                if (RefreshRunning()) _dirty = true;
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
            return changed;
        }

        // ------------------------------------------------------------------ Anordnung

        private float BaseLen(Slot s) { return s.Item.IsSeparator ? _sepLen : _basePx; }

        /// <summary>Ruhige Effekte ohne Vergrößerung der Leiste: Anheben, Aus, Leuchten, Fokus, Federn.</summary>
        private bool SimpleEffect { get { int m = _s.ZoomMode; return m == 3 || m == 4 || m >= 6; } }

        private float Falloff(float d)
        {
            float w = _s.ZoomWidth + 1f;
            if (Math.Abs(d) >= w) return 0f;
            if (_s.ZoomMode == 5)
            {
                // weiche Blase: Glockenkurve, nur das Symbol unter der Maus wächst deutlich (Nachbar ca. 14 %)
                float gauss = (float)Math.Exp(-d * d * 2f);
                float edgeK = 1f - Math.Abs(d) / w; // zum Rand hin sanft auf 0
                return gauss * Math.Min(1f, edgeK * 3f);
            }
            float c = (float)Math.Cos(d / w * Math.PI / 2);
            return c * c;
        }

        private void DoLayout()
        {
            int n = _slots.Count;
            float sp = _spacing;
            float total = 0;
            foreach (Slot s in _slots) total += BaseLen(s);
            if (n > 1) total += sp * (n - 1);
            // Zentrierung: -100 % = am Anfang, 0 % = Mitte, 100 % = am Ende des freien Platzes
            // Zoom-Effekt: Blase (Nachbarn rücken zur Seite) oder fest (Leiste bleibt stehen, Symbole wachsen an ihrem Platz).
            // Automatisch: fest, sobald das Dock mehr als zwei Drittel des Bildschirmrands einnimmt – sonst wandert die lange Leiste mit der Maus.
            // ruhige Effekte (Anheben, Aus, Leuchten, Fokus, Federn): kein Zoom, Leiste bleibt stehen
            bool simple = SimpleEffect;
            _fixedZoom = simple || _s.ZoomMode == 2 || (_s.ZoomMode == 0 && total > _Lwin * 2f / 3f);
            // Blase mit fester Leistenbreite: auf Wunsch ist die Leiste an beiden Enden etwas länger (Reserve für die Blase)
            bool keep = !_fixedZoom && _s.KeepWidth && _zoomPx > _basePx && n > 0;
            float res = keep ? Math.Max(0f, Math.Min(R(_s.Reserve), (_Lwin - 2 * (_padL + R(4)) - total) / 2f)) : 0f;
            _edge0 = _edge1 = 0f;
            _overlap = false;
            float edge = _padL + R(4) + res;
            float free = _Lwin - 2 * edge - total;
            float x0 = free > 0 ? edge + free * (_s.CenterPercent + 100) / 200f : (_Lwin - total) / 2f;
            _axisCenter = x0 + total / 2f;
            x0 = Math.Max(edge, Math.Min(x0, _Lwin - edge - total)); // nicht über den Bildschirmrand hinaus
            float x = x0;
            foreach (Slot s in _slots) { s.BaseStart = x; x += BaseLen(s) + sp; }

            bool zoomOn = _zoom > 0.001f && n > 0 && !_dragging && _zoomPx > _basePx;
            float m = _mouseAlong;
            _effectIndex = -1;
            if (simple && _zoom > 0.001f && !_dragging)
                for (int i = 0; i < n; i++)
                {
                    Slot s = _slots[i];
                    if (!s.Item.IsSeparator && m >= s.BaseStart - sp / 2f && m <= s.BaseStart + BaseLen(s) + sp / 2f) { _effectIndex = i; break; }
                }
            // Federn: das Symbol hüpft einmal, sobald die Maus darauf kommt
            if (_s.ZoomMode == 8 && _effectIndex != _hopIndex)
            {
                _hopIndex = _effectIndex;
                if (_effectIndex >= 0 && !_demo && _s.Animate) _slots[_effectIndex].HopStart = DateTime.Now;
            }
            for (int i = 0; i < n; i++)
            {
                Slot s = _slots[i];
                s.Size = _basePx;
                if (simple)
                {
                    if (_s.ZoomMode == 3 && i == _effectIndex) s.Size = _basePx * (1f + 0.15f * _zoom); // Anheben: nur leicht größer
                }
                else if (zoomOn && !s.Item.IsSeparator)
                {
                    float d = (m - (s.BaseStart + _basePx / 2f)) / (_basePx + sp);
                    s.Size = _basePx + (_zoomPx - _basePx) * _zoom * Falloff(d);
                }
                s.Len = s.Item.IsSeparator ? _sepLen : s.Size;
            }

            // Passt die vergrößerte Leiste nicht auf den Bildschirm, nur so viel wachsen, wie Platz ist.
            // Sonst verschiebt sich bei jeder Mausbewegung die ganze Leiste.
            if (zoomOn && !_fixedZoom && !_s.KeepWidth)
            {
                float growth = 0;
                foreach (Slot s in _slots) growth += s.Len - BaseLen(s);
                float avail = _Lwin - 2 * _padL - R(4);
                if (growth > 0.5f && total + growth > avail)
                {
                    float k = Math.Max(0f, (avail - total) / growth);
                    foreach (Slot s in _slots)
                    {
                        if (s.Item.IsSeparator) continue;
                        s.Size = _basePx + (s.Size - _basePx) * k;
                        s.Len = s.Size;
                    }
                }
            }

            if (simple)
            {
                foreach (Slot s in _slots) s.Pos = s.BaseStart + BaseLen(s) / 2f - s.Len / 2f;
            }
            else if (!zoomOn)
            {
                foreach (Slot s in _slots) s.Pos = s.BaseStart;
            }
            else if (_fixedZoom)
            {
                // an Ort und Stelle: Mitte jedes Symbols bleibt, größere überdecken die Nachbarn
                foreach (Slot s in _slots)
                {
                    s.Pos = s.BaseStart + BaseLen(s) / 2f - s.Len / 2f;
                }
            }
            else
            {
                // Die Stelle unter der Maus bleibt stehen, die Leiste wächst nach beiden Seiten
                float mm = Math.Max(x0, Math.Min(x0 + total, m));
                int k = n - 1;
                for (int i = 0; i < n; i++)
                    if (mm < _slots[i].BaseStart + BaseLen(_slots[i]) + sp) { k = i; break; }
                float unit = BaseLen(_slots[k]) + sp;
                float t = Math.Max(0f, Math.Min(1f, (mm - _slots[k].BaseStart) / unit));
                float zk = 0;
                for (int j = 0; j < k; j++) zk += _slots[j].Len + sp;
                float start = mm - zk - t * (_slots[k].Len + sp);
                float zTotal = 0;
                foreach (Slot s in _slots) zTotal += s.Len;
                zTotal += sp * (n - 1);
                if (zTotal < _Lwin - 2 * _padL)
                    start = Math.Max(_padL + R(2), Math.Min(start, _Lwin - _padL - R(2) - zTotal));
                float p = start;
                foreach (Slot s in _slots) { s.Pos = p; p += s.Len + sp; }
                if (_s.KeepWidth) KeepShelfWidth(x0, x0 + total, sp, res);
            }

            if (keep)
            {
                // feste Breite: Ruhelänge plus Reserve; nur am ersten/letzten Symbol wächst die Leiste dort etwas hinaus
                _shelfStart = x0 - res - _padL - _edge0;
                _shelfEnd = x0 + total + res + _padL + _edge1;
            }
            else if (n > 0 && _fixedZoom)
            {
                _shelfStart = _slots[0].BaseStart - _padL;
                _shelfEnd = _slots[n - 1].BaseStart + BaseLen(_slots[n - 1]) + _padL;
            }
            else if (n > 0)
            {
                _shelfStart = _slots[0].Pos - _padL;
                _shelfEnd = _slots[n - 1].Pos + _slots[n - 1].Len + _padL;
            }
            else
            {
                _shelfStart = _axisCenter - _basePx / 2 - _padL;
                _shelfEnd = _axisCenter + _basePx / 2 + _padL;
            }
        }

        /// <summary>
        /// Blase, ohne dass die Leiste breiter wird: Die Blase bleibt (möglichst) unter der Maus, die Symbole links und rechts
        /// davon geben Platz her – sie werden kleiner (schrumpfen) oder schieben sich übereinander (überlappen).
        /// Hergegeben wird nach Gewicht (1 − Anteil der Vergrößerung): Symbole in der Blase kaum, weiter entfernte stärker.
        /// Alles ändert sich stetig mit der Maus, damit nichts springt.
        /// res: Reserve an beiden Enden der Leiste, die die Blase zuerst nutzt. Am ersten/letzten Symbol darf die Leiste
        /// auf Wunsch ein Stück über ihr Ende hinauswachsen (_edge0/_edge1). Reicht der Platz nicht, wächst die Blase weniger.
        /// </summary>
        private void KeepShelfWidth(float b0, float b1, float sp, float res)
        {
            int n = _slots.Count;
            if (n == 0) return;
            float minK = Math.Max(0.5f, Math.Min(1f, _s.MinShrink / 100f)); // kleinste Größe bzw. kleinster sichtbarer Anteil
            bool overlap = _s.KeepMode == 1;
            float A0 = b0 - res, A1 = b1 + res;
            float range = Math.Max(1f, _zoomPx - _basePx);
            float m = _mouseAlong;
            // Nähe der Maus zum Ende der Leiste (1 = am ersten bzw. letzten Symbol, 0 = weiter innen als die Blase reicht)
            float reach = (_s.ZoomWidth + 1f) * (_basePx + sp);
            float f0 = _s.EdgeGrow ? Math.Max(0f, Math.Min(1f, 1f - (m - b0) / reach)) : 0f;
            float f1 = _s.EdgeGrow ? Math.Max(0f, Math.Min(1f, 1f - (b1 - m) / reach)) : 0f;
            float room0 = Math.Max(0f, A0 - _padL - R(2)), room1 = Math.Max(0f, _Lwin - R(2) - _padL - A1);
            float start = _slots[0].Pos, e0 = 0f, e1 = 0f;

            // Gewichte: wie viel jedes Symbol hergeben kann – aus der ungekürzten Blase, damit sich beim Überfahren nichts sprunghaft ändert
            float wl = 0, wr = 0;
            float[] w = new float[n];
            bool[] left = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Slot s = _slots[i];
                float g = s.Item.IsSeparator ? 0f : Math.Max(0f, Math.Min(1f, (s.Size - _basePx) / range));
                w[i] = (1f - g) * BaseLen(s);
                left[i] = s.BaseStart + BaseLen(s) / 2f < m;
                if (left[i]) wl += w[i]; else wr += w[i];
            }
            float capL = (1 - minK) * wl, capR = (1 - minK) * wr;
            // Stelle unter der Maus (wie in DoLayout), um die Blase nach dem Verkleinern wieder dort anzulegen
            float mm = Math.Max(b0, Math.Min(b1, m));
            int anchor = n - 1;
            for (int i = 0; i < n; i++)
                if (mm < _slots[i].BaseStart + BaseLen(_slots[i]) + sp) { anchor = i; break; }
            float at = Math.Max(0f, Math.Min(1f, (mm - _slots[anchor].BaseStart) / (BaseLen(_slots[anchor]) + sp)));

            const int Passes = 8;
            for (int pass = 0; pass < Passes; pass++)
            {
                // Überstand links und rechts der normalen Blase (Stelle unter der Maus bleibt stehen)
                float first = _slots[0].Pos;
                float over0 = A0 - first;
                float over1 = _slots[n - 1].Pos + _slots[n - 1].Len - A1;
                // freier Platz auf einer Seite gleicht den Überstand der anderen aus (die Blase rückt dorthin)
                if (over0 < 0) { over1 += over0; over0 = 0; }
                if (over1 < 0) { over0 += over1; over1 = 0; }
                if (over0 < 0) over0 = 0;
                // am Ende der Leiste: statt die Blase nach innen zu schieben, wächst die Leiste dort hinaus
                e0 = Math.Min(Math.Max(0f, over0 - capL) * f0, room0);
                e1 = Math.Min(Math.Max(0f, over1 - capR) * f1, room1);
                over0 -= e0;
                over1 -= e1;
                if (over0 > capL) { over1 += over0 - capL; over0 = capL; }
                if (over1 > capR) { over0 += over1 - capR; over1 = capR; }
                if (over0 > capL + 0.01f && pass < Passes - 1)
                {
                    // insgesamt zu wenig Platz: Blase kleiner machen und noch einmal rechnen
                    float growth = 0;
                    foreach (Slot s in _slots) if (!s.Item.IsSeparator) growth += s.Size - _basePx;
                    float k = growth > 0.01f ? Math.Max(0f, 1f - (over0 - capL) / growth) : 0f;
                    foreach (Slot s in _slots)
                        if (!s.Item.IsSeparator) { s.Size = _basePx + (s.Size - _basePx) * k; s.Len = s.Size; }
                    // wieder um die Stelle unter der Maus legen (wie die normale Blase), dann nächster Durchgang
                    float zk = 0;
                    for (int j = 0; j < anchor; j++) zk += _slots[j].Len + sp;
                    float q = mm - zk - at * (_slots[anchor].Len + sp);
                    foreach (Slot s in _slots) { s.Pos = q; q += s.Len + sp; }
                    continue;
                }
                over0 = Math.Min(over0, capL);
                float newLen = sp * (n - 1);
                for (int i = 0; i < n; i++)
                {
                    Slot s = _slots[i];
                    float side = left[i] ? (wl > 0.01f ? over0 / wl : 0f) : (wr > 0.01f ? over1 / wr : 0f);
                    s.Len = Math.Max(0.5f, s.Len - w[i] * side);
                    if (!overlap && !s.Item.IsSeparator) s.Size = s.Len; // schrumpfen; beim Überlappen bleibt die Größe
                    newLen += s.Len;
                }
                float lo = A0 - e0, hi = A1 + e1 - newLen;
                start = hi < lo ? (lo + hi) / 2f : Math.Max(lo, Math.Min(hi, first + over0));
                break;
            }
            _edge0 = e0;
            _edge1 = e1;
            _overlap = overlap;
            float p = start;
            foreach (Slot s in _slots)
            {
                // Pos = linke Kante des Symbols; beim Überlappen ist das Symbol breiter als sein Platz und liegt mittig darauf
                s.Pos = s.Item.IsSeparator ? p : p + s.Len / 2f - s.Size / 2f;
                p += s.Len + sp;
            }
        }

        // ------------------------------------------------------------------ Zeichnen

        private RectangleF Map(float along, float across, float alongLen, float acrossLen)
        {
            switch (_s.Edge)
            {
                case DockEdge.Oben: return new RectangleF(along, across, alongLen, acrossLen);
                case DockEdge.Unten: return new RectangleF(along, _T - across - acrossLen, alongLen, acrossLen);
                case DockEdge.Links: return new RectangleF(across, along, acrossLen, alongLen);
                default: return new RectangleF(_T - across - acrossLen, along, acrossLen, alongLen);
            }
        }

        private float Bounce(Slot s)
        {
            if (s.BounceStart == DateTime.MinValue) return 0f;
            double t = (DateTime.Now - s.BounceStart).TotalMilliseconds / (900.0 * AnimFactor);
            if (t >= 1) return 0f;
            return (float)(_bounceH * Math.Abs(Math.Sin(t * Math.PI * 2)) * (1 - t));
        }

        private const double HopMs = 420;

        /// <summary>Federn: ein kurzer, weicher Hüpfer (ca. halb so hoch wie beim Starten).</summary>
        private float Hop(Slot s)
        {
            if (s.HopStart == DateTime.MinValue) return 0f;
            double t = (DateTime.Now - s.HopStart).TotalMilliseconds / (HopMs * AnimFactor);
            if (t >= 1) return 0f;
            return (float)(_bounceH * 0.45 * Math.Sin(t * Math.PI));
        }

        private void Render()
        {
            if (!IsHandleCreated) return;
            int w = _winRect.Width, h = _winRect.Height;
            if (w <= 0 || h <= 0) return;
            EnsureBuffer(w, h);
            Graphics g = _g;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            DrawScene(g);
            UpdateLayered();
        }

        /// <summary>Leiste, Symbole, Markierungen und Beschriftung in Fensterkoordinaten zeichnen.</summary>
        private void DrawScene(Graphics g)
        {
            if (_show > 0.001f)
            {
                bool dark = _dark;
                float ease = 1 - (1 - _show) * (1 - _show);
                float off = -(1 - ease) * (_edgeDist + _shelfThick + R(8));
                float a0 = _edgeDist + off;

                DrawShelf(g, Map(_shelfStart, a0, _shelfEnd - _shelfStart, _shelfThick), dark);

                // bei festem Zoom die größten Symbole zuletzt zeichnen, damit sie oben liegen
                List<int> order = new List<int>();
                for (int i = 0; i < _slots.Count; i++) order.Add(i);
                if (_fixedZoom || _overlap) order.Sort(delegate (int a, int b)
                {
                    int c = _slots[a].Size.CompareTo(_slots[b].Size);
                    return c != 0 ? c : a.CompareTo(b);
                });
                foreach (int i in order)
                {
                    Slot s = _slots[i];
                    if (s.Item.IsSeparator)
                    {
                        using (Pen p = new Pen(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(55, 0, 0, 0), Math.Max(1f, R(1))))
                        {
                            RectangleF r = Map(s.Pos + s.Len / 2f, a0 + _padA + _basePx * 0.18f, 0, _basePx * 0.64f);
                            g.DrawLine(p, r.Left, r.Top, r.Right, r.Bottom);
                        }
                        continue;
                    }
                    if (_dragging && i == _dragIndex) continue;
                    float alpha = 1f;
                    float pos = s.Pos, size = s.Size, lift = 0f;
                    bool pressed = i == _pressIndex && !_dragging;
                    if (i == _effectIndex && _s.ZoomMode == 3) lift = R(4) * _zoom; // Anheben: etwas vom Rand weg
                    if (_s.ZoomMode == 8) lift += Hop(s);                           // Federn
                    if (_s.ZoomMode == 7 && _effectIndex >= 0 && i != _effectIndex)
                        alpha = 1f - 0.45f * Math.Max(0f, Math.Min(1f, _zoom));        // Fokus: die übrigen blasser
                    if (i == _effectIndex && _s.ZoomMode == 6) DrawGlow(g, s, a0, dark); // Leuchten
                    if (_s.Reflection) DrawReflection(g, s, s.Pos, a0 + _padA, s.Size, alpha);
                    if (pressed && SimpleEffect)
                    {
                        // wie in der Taskleiste: beim Drücken kurz kleiner statt dunkler
                        float k = 0.86f;
                        pos += size * (1 - k) / 2f;
                        lift += size * (1 - k) / 2f;
                        size *= k;
                        pressed = false;
                    }
                    DrawIcon(g, s, pos, a0 + _padA + Bounce(s) + lift, size, alpha, pressed);
                    if (_s.ShowRunning && s.Windows.Count > 0)
                    {
                        float pl = R(s.Windows.Count > 1 ? 14 : 7), pt = Math.Max(2f, R(3));
                        RectangleF pr = Map(s.Pos + s.Size / 2f - pl / 2f, a0 + Math.Max(1f, R(2)), pl, pt);
                        using (GraphicsPath path = Theme.RoundRect(pr, Math.Min(pr.Width, pr.Height) / 2f))
                        using (SolidBrush b = new SolidBrush(_accent))
                            g.FillPath(b, path);
                    }
                }

                // gezogenes Symbol folgt der Maus
                if (_dragging && _dragIndex >= 0 && _dragIndex < _slots.Count)
                {
                    Slot s = _slots[_dragIndex];
                    DrawIcon(g, s, _mouseAlong - _basePx / 2f, a0 + _padA + R(6), _basePx, 0.85f, false);
                }

                // Einfügemarke beim Ziehen aus dem Explorer
                if (_extDrag && _dropOnto < 0 && _dropIndex >= 0)
                {
                    float at = _dropIndex < _slots.Count ? _slots[_dropIndex].Pos - _spacing / 2f
                        : (_slots.Count > 0 ? _slots[_slots.Count - 1].Pos + _slots[_slots.Count - 1].Len + _spacing / 2f : _axisCenter);
                    RectangleF mr = Map(at - R(1.5f), a0 + _padA / 2, R(3), _shelfThick - _padA);
                    using (GraphicsPath path = Theme.RoundRect(mr, R(1.5f)))
                    using (SolidBrush b = new SolidBrush(_accent))
                        g.FillPath(b, path);
                }

                // Beschriftung
                int labelIndex = _extDrag && _dropOnto >= 0 ? _dropOnto : _hover;
                if (_s.Labels && labelIndex >= 0 && labelIndex < _slots.Count && !_dragging && !_menuOpen && !PreviewVisible && _show > 0.95f)
                {
                    Slot s = _slots[labelIndex];
                    string text = _extDrag && _dropOnto >= 0 ? "Öffnen mit " + s.Item.Title : s.Item.Title;
                    if (!string.IsNullOrEmpty(text))
                        DrawLabel(g, text, s.Pos + s.Size / 2f, a0 + _padA + s.Size + Bounce(s) + Hop(s) + _labelGap, dark);
                }
            }
        }

        /// <summary>Leuchten: weicher, runder Schein in Akzentfarbe hinter dem Symbol.</summary>
        private void DrawGlow(Graphics g, Slot s, float a0, bool dark)
        {
            float z = Math.Max(0f, Math.Min(1f, _zoom));
            if (z <= 0.01f) return;
            float d = s.Size * 1.55f;
            RectangleF icon = Map(s.Pos, a0 + _padA, s.Size, s.Size);
            float cx = icon.X + icon.Width / 2f, cy = icon.Y + icon.Height / 2f;
            RectangleF r = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (PathGradientBrush b = new PathGradientBrush(path))
                {
                    b.CenterColor = Color.FromArgb((int)((dark ? 150 : 130) * z), _accent);
                    b.SurroundColors = new[] { Color.FromArgb(0, _accent) };
                    b.FocusScales = new PointF(0.35f, 0.35f);
                    g.FillPath(b, path);
                }
            }
        }

        /// <summary>
        /// Spiegelung wie bei RocketDock: das Symbol gespiegelt zum Bildschirmrand hin, oben ca. 30 % deckend,
        /// nach 40 % der Höhe ausgeblendet. Das Bild mit Verlauf wird je Symbol einmal erzeugt.
        /// </summary>
        private void DrawReflection(Graphics g, Slot s, float along, float across, float size, float alpha)
        {
            if (s.Base == null) return;
            int px = Math.Max(1, (int)Math.Round(size));
            Bitmap src = px > s.Base.Width && s.Zoom != null ? s.Zoom : s.Base;
            if (s.Reflect == null || s.ReflectSrc != src || s.ReflectEdge != _s.Edge)
            {
                s.DisposeReflect();
                s.Reflect = MakeReflection(src, _s.Edge);
                s.ReflectSrc = src;
                s.ReflectEdge = _s.Edge;
            }
            RectangleF rf = Map(along, across, size, size);
            Rectangle r = new Rectangle((int)Math.Round(rf.X + (rf.Width - px) / 2f), (int)Math.Round(rf.Y + (rf.Height - px) / 2f), px, px);
            Point[] dest;
            switch (_s.Edge)
            {
                case DockEdge.Oben: dest = new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left, r.Top - r.Height) }; break;
                case DockEdge.Links: dest = new[] { new Point(r.Left, r.Top), new Point(r.Left - r.Width, r.Top), new Point(r.Left, r.Bottom) }; break;
                case DockEdge.Rechts: dest = new[] { new Point(r.Right + r.Width, r.Top), new Point(r.Right, r.Top), new Point(r.Right + r.Width, r.Bottom) }; break;
                default: dest = new[] { new Point(r.Left, r.Bottom + r.Height), new Point(r.Right, r.Bottom + r.Height), new Point(r.Left, r.Bottom) }; break;
            }
            InterpolationMode oldIm = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.Bilinear;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                if (alpha < 1f)
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = alpha;
                    ia.SetColorMatrix(cm);
                }
                g.DrawImage(s.Reflect, dest, new Rectangle(0, 0, s.Reflect.Width, s.Reflect.Height), GraphicsUnit.Pixel, ia);
            }
            g.InterpolationMode = oldIm;
        }

        /// <summary>Kopie des Symbols, deren Deckkraft zur Seite des Bildschirmrands hin 30 % beträgt und nach 40 % ausläuft.</summary>
        private static Bitmap MakeReflection(Bitmap src, DockEdge edge)
        {
            int w = src.Width, h = src.Height;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, w, h);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] px = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // Abstand (0 … 1) von der Seite, die zum Bildschirmrand zeigt
                        float t;
                        switch (edge)
                        {
                            case DockEdge.Oben: t = (y + 0.5f) / h; break;
                            case DockEdge.Links: t = (x + 0.5f) / w; break;
                            case DockEdge.Rechts: t = 1f - (x + 0.5f) / w; break;
                            default: t = 1f - (y + 0.5f) / h; break;
                        }
                        float k = t >= 0.4f ? 0f : 0.3f * (1f - t / 0.4f) * (1f - t / 0.4f);
                        int i = y * stride + x * 4 + 3;
                        px[i] = (byte)(px[i] * k);
                    }
                System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        private void DrawShelf(Graphics g, RectangleF r, bool dark)
        {
            if (!_s.ShelfVisible) return; // nur Symbole
            float op = Math.Max(0.1f, Math.Min(1f, _s.ShelfOpacity / 100f));
            float radius = Math.Min(R(_s.ShelfRadius), Math.Min(r.Width, r.Height) / 2f);
            // weicher Schatten
            for (int i = 3; i >= 1; i--)
            {
                RectangleF sr = RectangleF.Inflate(r, R(i), R(i));
                using (GraphicsPath path = Theme.RoundRect(sr, radius + R(i)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)((dark ? 22 / i : 14 / i) * op), 0, 0, 0)))
                    g.FillPath(b, path);
            }
            // Hintergrund wie ein aktives Windows-11-Fenster (Mica): Fensterfarbe, leicht in den Farben des Hintergrundbilds getönt
            Color border = dark ? Color.FromArgb((int)(52 * op), 255, 255, 255) : Color.FromArgb((int)(38 * op), 0, 0, 0);
            RectangleF br = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            using (GraphicsPath path = Theme.RoundRect(br, radius))
            {
                if (_mica != null && _monitor.Width > 0 && _monitor.Height > 0)
                {
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        if (op < 1f)
                        {
                            ColorMatrix cm = new ColorMatrix();
                            cm.Matrix33 = op;
                            ia.SetColorMatrix(cm);
                        }
                        using (TextureBrush tb = new TextureBrush(_mica, new Rectangle(0, 0, _mica.Width, _mica.Height), ia))
                        {
                        // Tönung bleibt am Bildschirm fest (wie bei echtem Mica), auch beim Ein- und Ausblenden
                        tb.TranslateTransform(_monitor.Left - _winRect.Left, _monitor.Top - _winRect.Top);
                        tb.ScaleTransform(_monitor.Width / (float)_mica.Width, _monitor.Height / (float)_mica.Height);
                        InterpolationMode old = g.InterpolationMode;
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.FillPath(tb, path);
                        g.InterpolationMode = old;
                        }
                    }
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * op), dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243)))) g.FillPath(b, path);
                }
                using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, System.Text.StringBuilder pvParam, int fWinIni);

        /// <summary>
        /// Nachbau von Mica (aktive Windows-11-Fenster): Hintergrundbild stark verwischt, davon nur die Farbe (ohne Helligkeit)
        /// über die Fensterfarbe (#F3F3F3 hell, #202020 dunkel) gelegt. Echtes Mica gibt es für das durchsichtige Dock-Fenster nicht,
        /// und es wäre bei einem nie aktiven Fenster ohnehin nur grau.
        /// </summary>
        private void LoadMica()
        {
            string wallpaper = "";
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(520);
                if (SystemParametersInfo(0x0073, sb.Capacity, sb, 0)) wallpaper = sb.ToString(); // SPI_GETDESKWALLPAPER
            }
            catch { }
            DateTime stamp = DateTime.MinValue;
            try { if (File.Exists(wallpaper)) stamp = File.GetLastWriteTimeUtc(wallpaper); } catch { }
            string key = wallpaper + "|" + stamp.Ticks + "|" + _monitor + "|" + _dark;
            if (key == _micaKey && _mica != null) return;
            _micaKey = key;
            Image img = null;
            try
            {
                if (File.Exists(wallpaper))
                    using (FileStream fs = new FileStream(wallpaper, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image tmp = Image.FromStream(fs))
                        img = new Bitmap(tmp);
            }
            catch { img = null; }
            try { BuildMica(img); }
            finally { if (img != null) img.Dispose(); }
        }

        /// <summary>Mica-Tönung aus einem Hintergrundbild (null = nur Fensterfarbe).</summary>
        private void BuildMica(Image img)
        {
            if (_mica != null) { _mica.Dispose(); _mica = null; }
            if (_monitor.Width <= 0 || _monitor.Height <= 0) return;

            int w = 48, h = Math.Max(1, (int)Math.Round(48.0 * _monitor.Height / _monitor.Width));
            Bitmap small = null;
            try
            {
                if (img != null)
                {
                    {
                        // wie "Ausfüllen": Bild auf den Bildschirm skalieren, Überstand mittig abschneiden
                        float k = Math.Max(_monitor.Width / (float)img.Width, _monitor.Height / (float)img.Height);
                        float cw = _monitor.Width / k, ch = _monitor.Height / k;
                        RectangleF src = new RectangleF((img.Width - cw) / 2f, (img.Height - ch) / 2f, cw, ch);
                        small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, new Rectangle(0, 0, w, h), src, GraphicsUnit.Pixel);
                        }
                    }
                }
            }
            catch { if (small != null) { small.Dispose(); small = null; } }

            int baseV = _dark ? 32 : 243;
            float tint = _dark ? 0.45f : 0.35f;
            Bitmap res = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int r = baseV, gg = baseV, b = baseV;
                    if (small != null)
                    {
                        Color c = small.GetPixel(x, y);
                        float l = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                        r = Clamp(baseV + (c.R - l) * tint);
                        gg = Clamp(baseV + (c.G - l) * tint);
                        b = Clamp(baseV + (c.B - l) * tint);
                    }
                    res.SetPixel(x, y, Color.FromArgb(255, r, gg, b));
                }
            if (small != null) small.Dispose();
            _mica = res;
        }

        private static int Clamp(float v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v))); }

        /// <summary>
        /// Symbol zeichnen. Lage und Größe werden auf ganze Pixel gerundet und vergrößerte Symbole immer bikubisch aus dem
        /// großen Bild verkleinert – so wechselt die Schärfe beim Zoomen nicht sprunghaft und nichts flimmert zwischen den Pixeln.
        /// </summary>
        private void DrawIcon(Graphics g, Slot s, float along, float across, float size, float alpha, bool pressed)
        {
            RectangleF rf = Map(along, across, size, size);
            int px = Math.Max(1, (int)Math.Round(size));
            Rectangle r = new Rectangle((int)Math.Round(rf.X + (rf.Width - px) / 2f), (int)Math.Round(rf.Y + (rf.Height - px) / 2f), px, px);
            // fast unsichtbare Fläche: ganze Symbolfläche nimmt Mausklicks an (auch durchsichtige Ecken)
            using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(hit, r);
            if (s.Base == null) return;
            bool plain = alpha >= 1f && !pressed;
            if (px == s.Base.Width && plain)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(s.Base, r.X, r.Y, s.Base.Width, s.Base.Height); // 1:1, gestochen scharf
                return;
            }
            Bitmap img = px > s.Base.Width && s.Zoom != null ? s.Zoom : s.Base;
            PixelOffsetMode oldPo = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY); // keine hellen oder dunklen Ränder beim Skalieren
                if (!plain)
                {
                    float k = pressed ? 0.72f : 1f;
                    ia.SetColorMatrix(new ColorMatrix(new float[][]
                    {
                        new float[] { k, 0, 0, 0, 0 },
                        new float[] { 0, k, 0, 0, 0 },
                        new float[] { 0, 0, k, 0, 0 },
                        new float[] { 0, 0, 0, alpha, 0 },
                        new float[] { 0, 0, 0, 0, 1 }
                    }));
                }
                g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
            g.PixelOffsetMode = oldPo;
        }

        /// <summary>
        /// Beschriftung wie bei RocketDock: nur der Text, weiß mit weichem dunklem Schatten – auf jedem Hintergrund lesbar
        /// (wie die Beschriftung der Desktop-Symbole), ohne Kasten.
        /// </summary>
        /// <summary>
        /// Schrift der Beschriftung: leer = Windows-Standard (wie die Oberfläche, fett als Halbfett).
        /// Ist die gewählte Schrift nicht (mehr) installiert, gilt ebenfalls der Standard; kann sie kein Fett, wird sie normal gezeichnet.
        /// </summary>
        internal static Font LabelFont(string name, float pixelSize, bool bold)
        {
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    using (FontFamily fam = new FontFamily(name))
                    {
                        if (!fam.IsStyleAvailable(style)) style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
                        if (fam.IsStyleAvailable(style)) return new Font(fam, pixelSize, style, GraphicsUnit.Pixel);
                    }
                }
                catch { } // nicht installiert
            }
            return Theme.UiFont(pixelSize, bold ? FontStyle.Bold : FontStyle.Regular);
        }

        private void DrawLabel(Graphics g, string text, float centerAlong, float across, bool dark)
        {
            using (Font f = LabelFont(_s.LabelFont, _s.LabelSize * _sc, _s.LabelBold))
            using (GraphicsPath path = new GraphicsPath())
            {
                SizeF ts = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic);
                float w = (float)Math.Ceiling(ts.Width) + R(8), h = (float)Math.Ceiling(ts.Height) + R(4);
                bool vertical = _s.Vertical;
                float alongLen = vertical ? h : w, acrossLen = vertical ? w : h;
                float start = centerAlong - alongLen / 2f;
                start = Math.Max(R(4), Math.Min(start, _Lwin - R(4) - alongLen));
                RectangleF r = Map(start, across, alongLen, acrossLen);
                float x = (float)Math.Round(r.X + (r.Width - ts.Width) / 2f), y = (float)Math.Round(r.Y + (r.Height - ts.Height) / 2f);
                path.AddString(text, f.FontFamily, (int)f.Style, f.Size, new PointF(x, y), StringFormat.GenericTypographic);

                SmoothingMode oldSm = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color textColor = Settings.LabelColorFor(_s.LabelColorMode, _s.LabelCustomColor, dark, _accent);
                // Schatten in der Gegenfarbe: dunkel hinter heller Schrift, hell hinter dunkler
                bool lightText = 0.299 * textColor.R + 0.587 * textColor.G + 0.114 * textColor.B > 128;
                Color sc = lightText ? Color.Black : Color.White;
                float strength = _s.LabelShadow / 10f;
                // weicher Schatten: Umriss in mehreren Breiten, leicht nach unten versetzt
                using (Matrix down = new Matrix())
                {
                    down.Translate(0, Math.Max(1f, R(1)));
                    using (GraphicsPath shadow = (GraphicsPath)path.Clone())
                    {
                        shadow.Transform(down);
                        // Schattengröße = Reichweite um die Schrift (Stiftbreite ist das Doppelte); Standard 3 px
                        float size = Math.Max(1, Math.Min(8, _s.LabelShadowSize));
                        float[] widths = { R(size * 2f), R(size * 4f / 3f), R(size * 2.5f / 3f) };
                        int[] alphas = { 28, 45, 80 };
                        if (strength > 0)
                        {
                            for (int i = 0; i < widths.Length; i++)
                                using (Pen p = new Pen(Color.FromArgb((int)(alphas[i] * strength), sc), Math.Max(1f, widths[i])) { LineJoin = LineJoin.Round })
                                    g.DrawPath(p, shadow);
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(140 * strength), sc))) g.FillPath(sb, shadow);
                        }
                    }
                }
                using (SolidBrush tb = new SolidBrush(textColor)) g.FillPath(tb, path);
                g.SmoothingMode = oldSm;
            }
        }

        private void EnsureBuffer(int w, int h)
        {
            if (_surface != null && _bufW == w && _bufH == h) return;
            FreeBuffer();
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            _memDc = NativeMethods.CreateCompatibleDC(screen);
            NativeMethods.BITMAPINFOHEADER bi = new NativeMethods.BITMAPINFOHEADER();
            bi.biSize = 40;
            bi.biWidth = w;
            bi.biHeight = -h;
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            _dib = NativeMethods.CreateDIBSection(screen, ref bi, 0, out _bits, IntPtr.Zero, 0);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            _oldBmp = NativeMethods.SelectObject(_memDc, _dib);
            _surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, _bits);
            _g = Graphics.FromImage(_surface);
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.PixelOffsetMode = PixelOffsetMode.Half;
            _bufW = w;
            _bufH = h;
        }

        private void FreeBuffer()
        {
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_surface != null) { _surface.Dispose(); _surface = null; }
            if (_memDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memDc, _oldBmp);
                NativeMethods.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero) { NativeMethods.DeleteObject(_dib); _dib = IntPtr.Zero; }
        }

        private void UpdateLayered()
        {
            NativeMethods.POINT dst = new NativeMethods.POINT(_winRect.Left, _winRect.Top);
            NativeMethods.SIZE size = new NativeMethods.SIZE(_bufW, _bufH);
            NativeMethods.POINT src = new NativeMethods.POINT(0, 0);
            NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION();
            blend.BlendOp = NativeMethods.AC_SRC_OVER;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = NativeMethods.AC_SRC_ALPHA;
            NativeMethods.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        // ------------------------------------------------------------------ Maus

        private void CursorDock(out float along, out float across)
        {
            Point p = Cursor.Position;
            ToDock(p.X, p.Y, out along, out across);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == DockNative.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)DockNative.MA_NOACTIVATE; // Klick ins Dock nimmt anderen Fenstern nicht den Fokus
                return;
            }
            if (m.Msg == 0x0320) // WM_DWMCOLORIZATIONCOLORCHANGED: Akzentfarbe in Windows geändert
            {
                _accent = Theme.SystemAccent;
                _dirty = true;
            }
            if (m.Msg == DockNative.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleByHotkey();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float along, across;
            CursorDock(out along, out across);
            if (e.Button == MouseButtons.Left)
            {
                _pressIndex = HitIndex(along, across);
                _pressPoint = Cursor.Position;
                _dirty = true;
            }
            else if (e.Button == MouseButtons.Middle) _middleIndex = HitIndex(along, across);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressIndex < 0 || (MouseButtons & MouseButtons.Left) == 0) return;
            float along, across;
            CursorDock(out along, out across);
            if (!_dragging)
            {
                Point p = Cursor.Position;
                if (_s.Locked || Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) < R(8)) return;
                _dragging = true;
                _dragIndex = _pressIndex;
                Capture = true;
            }
            _mouseAlong = along;
            // Ziel nach der Grundanordnung (ohne Vergrößerung) bestimmen
            // Position in der Liste ohne das gezogene Symbol = Anzahl der Mitten links der Maus
            int target = 0;
            for (int i = 0; i < _slots.Count; i++)
                if (i != _dragIndex && along > _slots[i].BaseStart + BaseLen(_slots[i]) / 2f) target++;
            if (target != _dragIndex)
            {
                Slot s = _slots[_dragIndex];
                DockItem it = _items[_dragIndex];
                _slots.RemoveAt(_dragIndex);
                _items.RemoveAt(_dragIndex);
                _slots.Insert(target, s);
                _items.Insert(target, it);
                _dragIndex = target;
                _pressIndex = target;
            }
            _dirty = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            float along, across;
            CursorDock(out along, out across);
            if (_dragging)
            {
                EndDrag();
                return;
            }
            int hit = HitIndex(along, across);
            if (e.Button == MouseButtons.Left && _pressIndex >= 0 && hit == _pressIndex)
                Launch(hit, (ModifierKeys & Keys.Shift) == Keys.Shift);
            else if (e.Button == MouseButtons.Middle)
            {
                // Mittelklick: immer neu starten, auch wenn das Programm schon läuft (wie in der Taskleiste)
                if (_middleIndex >= 0 && hit == _middleIndex) Launch(hit, true);
                _middleIndex = -1;
            }
            else if (e.Button == MouseButtons.Right)
                ShowContextMenu(HitIndex(along, across, true));
            _pressIndex = -1;
            _dirty = true;
        }

        private void EndDrag()
        {
            _dragging = false;
            _dragIndex = -1;
            _pressIndex = -1;
            Capture = false;
            Save();
            _dirty = true;
        }

        private void ToggleByHotkey()
        {
            if (_s.AutoHide || _fullscreen)
            {
                if (_show > 0.5f) { _hotkeyShown = false; _userHidden = !_s.AutoHide && !_fullscreen; _lastInside = DateTime.MinValue; }
                else { _hotkeyShown = true; _hotkeyTime = DateTime.Now; _userHidden = false; }
            }
            else
            {
                _userHidden = !_userHidden;
                _hotkeyShown = false;
            }
            _dirty = true;
        }

        // ------------------------------------------------------------------ Starten

        private void Launch(int index, bool forceNew)
        {
            if (index < 0 || index >= _slots.Count) return;
            Slot s = _slots[index];
            DockItem it = s.Item;
            if (it.Kind == ItemKind.Ordnermenue)
            {
                ShowFolderMenu(index);
                return;
            }
            if (!forceNew && _s.ActivateRunning)
            {
                RefreshRunning();
                if (s.Windows.Count > 0)
                {
                    // wie die Taskleiste: nach vorne holen, aktives minimieren; bei mehreren Fenstern Auswahl zeigen
                    if (s.Windows.Count > 1)
                    {
                        if (_s.Preview) ShowPreview(index); else ShowWindowMenu(index);
                        return;
                    }
                    HidePreview();
                    _previewBlock = index;
                    IntPtr fg = DockNative.GetForegroundWindow();
                    if (s.Windows[0] != fg) DockNative.Activate(s.Windows[0]);
                    else NativeMethods.ShowWindow(fg, 6); // SW_MINIMIZE
                    return;
                }
            }
            string err = Shell.Start(it.Command, it.Arguments, it.WorkingDirectory, it.ShowCmd, it.RunAsAdmin);
            if (err != null)
            {
                MessageBox.Show("„" + it.Title + "“ konnte nicht gestartet werden.\n\n" + err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_s.Animate) s.BounceStart = DateTime.Now;
            _nextRunning = DateTime.Now.AddMilliseconds(1200);
            _dirty = true;
        }

        /// <summary>Bildschirmrechteck eines Symbols (für die Position von Menüs).</summary>
        private Rectangle ScreenRect(Slot s)
        {
            RectangleF r = Map(s.Pos, _edgeDist + _padA, s.Size, s.Size);
            return new Rectangle(_winRect.Left + (int)r.X, _winRect.Top + (int)r.Y, (int)r.Width, (int)r.Height);
        }

        private void ShowAt(ContextMenuStrip menu, Slot anchor)
        {
            HidePreview();
            _menuOpen = true;
            menu.Opened += delegate { _menuOpen = true; _dirty = true; };
            menu.Closed += delegate
            {
                _menuOpen = false;
                _lastInside = DateTime.Now;
                _dirty = true;
            };
            // Das vorige Menü erst jetzt freigeben – ein Klick darin kann noch einen Dialog offen haben
            if (_lastMenu != null && _lastMenu != menu && !_lastMenu.Visible)
            {
                MenuBuilder.DisposeDeep(_lastMenu.Items);
                _lastMenu.Dispose();
            }
            _lastMenu = menu;
            _openMenu = menu;
            _menuAwaySince = DateTime.MinValue;
            menu.Closed += delegate { if (_openMenu == menu) _openMenu = null; };
            // Menü nach vorne holen: dann schließt es wie gewohnt beim Klick irgendwo anders (oder mit Esc).
            // Das Dock selbst wird nie aktiv, ohne das bekäme das Menü vom Wegklicken nichts mit.
            menu.Opened += delegate { DockNative.BringToFront(menu.Handle); };
            if (anchor == null)
            {
                menu.Show(Cursor.Position);
                return;
            }
            Rectangle r = ScreenRect(anchor);
            int gap = (int)R(6);
            float shelfOuter = _edgeDist + Math.Max(_shelfThick, _padA + anchor.Size) + gap;
            // Menü innerhalb des Bildschirms halten, auf dem das Dock liegt. Windows prüft nur den gesamten Desktop –
            // beim letzten Symbol am Rand ragte das Menü sonst auf den Nachbarbildschirm.
            Size size = menu.GetPreferredSize(Size.Empty);
            Rectangle area = Screen.FromRectangle(_monitor.Width > 0 ? _monitor : _winRect).WorkingArea;
            int margin = (int)R(4);
            int x = Math.Max(area.Left + margin, Math.Min(r.Left, area.Right - margin - size.Width));
            int y = Math.Max(area.Top + margin, Math.Min(r.Top, area.Bottom - margin - size.Height));
            switch (_s.Edge)
            {
                case DockEdge.Oben: menu.Show(new Point(x, _winRect.Top + (int)shelfOuter), ToolStripDropDownDirection.BelowRight); break;
                case DockEdge.Unten: menu.Show(new Point(x, _winRect.Bottom - (int)shelfOuter), ToolStripDropDownDirection.AboveRight); break;
                case DockEdge.Links: menu.Show(new Point(_winRect.Left + (int)shelfOuter, y), ToolStripDropDownDirection.Right); break;
                default: menu.Show(new Point(_winRect.Right - (int)shelfOuter, y), ToolStripDropDownDirection.Left); break;
            }
        }

        // ------------------------------------------------------------------ Fenstervorschau

        private static int _previewDelay = -1;

        /// <summary>
        /// Verzögerung bis zur ersten Vorschau. Wie die Taskleiste: der Wert „ExtendedUIHoverTime“ aus der Registry,
        /// falls jemand ihn gesetzt hat, sonst 300 ms (minus ein Tick des Timers, damit es nicht später kommt).
        /// </summary>
        private static int PreviewDelay
        {
            get
            {
                if (_previewDelay >= 0) return _previewDelay;
                int ms = 300;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    {
                        object v = key != null ? key.GetValue("ExtendedUIHoverTime") : null;
                        if (v is int && (int)v >= 0 && (int)v <= 5000) ms = (int)v;
                    }
                }
                catch { }
                _previewDelay = Math.Max(0, ms - 15);
                return _previewDelay;
            }
        }

        private bool PreviewVisible { get { return _preview != null && !_preview.IsDisposed && _preview.Visible; } }

        /// <summary>
        /// Fenstervorschau wie in der Taskleiste: Bleibt die Maus kurz auf einem laufenden Programm, erscheint die Vorschau.
        /// Sie verschwindet, wenn die Maus Symbol und Vorschau verlässt.
        /// </summary>
        private void PreviewTick(DateTime now, int hover, bool overPreview)
        {
            bool shown = PreviewVisible;
            if (!_s.Preview || _menuOpen || _dragging || _extDrag || _show < 0.95f)
            {
                _previewWant = -1;
                if (shown) HidePreview();
                return;
            }
            int target = hover >= 0 && hover < _slots.Count && !_slots[hover].Item.IsSeparator && _slots[hover].Windows.Count > 0 ? hover : -1;
            if (target != _previewBlock) _previewBlock = -1;
            else if (!shown) { _previewWant = -1; return; }
            if (target >= 0)
            {
                _previewAway = DateTime.MinValue;
                if (shown && target == _previewIndex) return;
                if (target != _previewWant) { _previewWant = target; _previewSince = now; }
                // erste Vorschau nach kurzem Verweilen, der Wechsel zum Nachbarn geht schneller
                if ((now - _previewSince).TotalMilliseconds >= (shown ? 100 : PreviewDelay) && (MouseButtons & MouseButtons.Left) == 0) ShowPreview(target);
                return;
            }
            _previewWant = -1;
            if (!shown) return;
            if (overPreview) { _previewAway = DateTime.MinValue; return; }
            if (_previewAway == DateTime.MinValue) _previewAway = now;
            else if ((now - _previewAway).TotalMilliseconds >= 350) HidePreview();
        }

        private void ShowPreview(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            Slot s = _slots[index];
            if (s.Windows.Count == 0) return;
            if (_preview == null || _preview.IsDisposed)
            {
                _preview = new PreviewPopup();
                _preview.Dismissed += delegate { _previewIndex = -1; _previewWant = -1; _lastInside = DateTime.Now; _dirty = true; };
            }
            // Abstand zum Symbol: knapp über der Leiste bzw. dem vergrößerten Symbol (wie bei den Menüs)
            Rectangle r = ScreenRect(s);
            Rectangle anchor;
            int outer = (int)(_edgeDist + Math.Max(_shelfThick, _padA + Math.Max(s.Size, _zoomPx)));
            switch (_s.Edge)
            {
                case DockEdge.Oben: anchor = Rectangle.FromLTRB(r.Left, _winRect.Top, r.Right, _winRect.Top + outer); break;
                case DockEdge.Links: anchor = Rectangle.FromLTRB(_winRect.Left, r.Top, _winRect.Left + outer, r.Bottom); break;
                case DockEdge.Rechts: anchor = Rectangle.FromLTRB(_winRect.Right - outer, r.Top, _winRect.Right, r.Bottom); break;
                default: anchor = Rectangle.FromLTRB(r.Left, _winRect.Bottom - outer, r.Right, _winRect.Bottom); break;
            }
            _preview.ShowFor(s.Windows, s.Item.Title, anchor, _s.Edge, (int)R(6), _monitor, _sc);
            _previewIndex = PreviewVisible ? index : -1;
            _previewWant = index;
            _previewAway = DateTime.MinValue;
            _dirty = true;
        }

        private void HidePreview()
        {
            _previewIndex = -1;
            if (_preview == null || _preview.IsDisposed || !_preview.Visible) return;
            _preview.Hide();
            _dirty = true;
        }

        /// <summary>Mehrere Fenster eines laufenden Programms: Menü mit den Fenstertiteln, darunter „Neues Fenster“.</summary>
        private void ShowWindowMenu(int index)
        {
            Slot s = _slots[index];
            MenuBuilder mb = new MenuBuilder();
            IntPtr fg = DockNative.GetForegroundWindow();
            foreach (IntPtr h in s.Windows)
            {
                IntPtr hwnd = h;
                string title = DockNative.WindowTitle(hwnd);
                if (string.IsNullOrEmpty(title)) title = s.Item.Title;
                if (title.Length > 70) title = title.Substring(0, 68) + "…";
                GlyphMenuItem item = mb.Item(title.Replace("&", "&&"), '\uE737', delegate { DockNative.Activate(hwnd); });
                if (hwnd == fg) item.Font = new Font(mb.Menu.Font, FontStyle.Bold);
                mb.Menu.Items.Add(item);
            }
            mb.Separator();
            mb.Menu.Items.Add(mb.Item("Neues Fenster", '\uE710', delegate { Launch(index, true); }));
            mb.End();
            ShowAt(mb.Menu, s);
        }

        private void ShowFolderMenu(int index)
        {
            Slot s = _slots[index];
            string folder = IconLoader.Expand(s.Item.Folder);
            MenuBuilder mb = new MenuBuilder();
            // Sortierung; eigene Reihenfolge per Ziehen im Menü – wird sofort gespeichert
            DockItem item = s.Item;
            mb.Sorting = new FolderSort(item.Sort, folder, item.Order);
            mb.Sorting.Changed = delegate (string order)
            {
                item.Sort = 4;
                item.Order = order;
                Save();
            };
            // „Einträge sperren“ schützt auch die Reihenfolge in den Ordnermenüs
            mb.AllowReorder = !_s.Locked;
            mb.FillFolder(mb.Menu.Items, folder, item.FoldersFirst, item.FilesOnly, false); // "Ordner öffnen" nur im Kontextmenü
            mb.EnableReorder(mb.Menu, folder);
            mb.End();
            ContextMenuStrip menu = mb.Menu;
            menu.Closed += delegate { if (_folderMenu == menu) { _folderMenu = null; _folderIndex = -1; } };
            ShowAt(menu, s);
            _folderMenu = menu;
            _folderIndex = index;
            _switchSince = DateTime.MinValue;
            _switchTo = -1;
        }

        /// <summary>
        /// Bei offenem Menü (Kontextmenü oder Ordnermenü): Verlässt die Maus Menü und Dock eine Sekunde lang, klappt es zu.
        /// Bei einem Ordnermenü: Fährt die Maus im Dock auf ein anderes Ordnermenü, wird direkt dorthin gewechselt
        /// (wie in einer Menüleiste). Gibt true zurück, wenn das Menü gewechselt wurde (der Rest des Ticks entfällt dann).
        /// </summary>
        private bool MenuTick(DateTime now, int x, int y, float along, float across)
        {
            ContextMenuStrip menu = _openMenu;
            if (menu.IsDisposed || !menu.Visible) { _openMenu = null; return false; }
            Point p = new Point(x, y);
            float depth = _edgeDist + Math.Max(_shelfThick, _padA + Math.Max(_zoomPx, _basePx) + _padA);
            bool overDock = across >= 0 && across <= depth && along >= _shelfStart - _spacing && along <= _shelfEnd + _spacing;
            bool overMenu = InMenus(menu, p);

            // zuklappen, wenn die Maus weg ist
            if (overDock || overMenu) _menuAwaySince = DateTime.MinValue;
            else if (_menuAwaySince == DateTime.MinValue) _menuAwaySince = now;
            else if ((now - _menuAwaySince).TotalMilliseconds >= 1000)
            {
                _menuAwaySince = DateTime.MinValue;
                menu.Close(ToolStripDropDownCloseReason.AppClicked);
                return false;
            }

            // auf ein anderes Ordnermenü wechseln
            if (menu != _folderMenu) return false;
            int h = overDock && !overMenu ? HitIndex(along, across) : -1;
            bool other = h >= 0 && h != _folderIndex && h < _slots.Count && !_slots[h].Item.IsSeparator && _slots[h].Item.Kind == ItemKind.Ordnermenue;
            if (!other) { _switchTo = -1; return false; }
            if (h != _switchTo) { _switchTo = h; _switchSince = now; return false; }
            if ((now - _switchSince).TotalMilliseconds < 120) return false; // nur kurzes Verweilen, kein Wechsel im Vorbeiflug
            _switchTo = -1;
            menu.Close(ToolStripDropDownCloseReason.AppClicked);
            _hover = h;
            ShowFolderMenu(h);
            _dirty = true;
            return true;
        }

        /// <summary>Liegt der Punkt im Menü oder in einem seiner offenen Untermenüs?</summary>
        private static bool InMenus(ToolStripDropDown dd, Point p)
        {
            if (dd == null || !dd.Visible) return false;
            if (dd.Bounds.Contains(p)) return true;
            foreach (ToolStripItem item in dd.Items)
            {
                ToolStripDropDownItem di = item as ToolStripDropDownItem;
                if (di != null && di.HasDropDownItems && di.DropDown.Visible && InMenus(di.DropDown, p)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Kontextmenü

        private void ShowContextMenu(int index)
        {
            ShowAt(BuildContextMenu(index), null);
        }

        /// <summary>Kontextmenü eines Eintrags (index &lt; 0: Rechtsklick auf die freie Leiste).</summary>
        internal ContextMenuStrip BuildContextMenu(int index)
        {
            MenuBuilder mb = new MenuBuilder();
            ToolStripItemCollection items = mb.Menu.Items;
            DockItem it = index >= 0 ? _items[index] : null;
            int insertAt = index >= 0 ? index + 1 : _items.Count;

            if (it != null && it.Kind == ItemKind.Programm)
            {
                GlyphMenuItem open = mb.Item("Öffnen", '', delegate { Launch(index, true); });
                open.Font = new Font(mb.Menu.Font, FontStyle.Bold);
                items.Add(open);
                items.Add(mb.Item("Als Administrator ausführen", MenuHelper.Shield, delegate
                {
                    string err = Shell.Start(it.Command, it.Arguments, it.WorkingDirectory, it.ShowCmd, true);
                    if (err != null) MessageBox.Show(err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
                string cmd = IconLoader.Expand(it.Command);
                bool local = false;
                try { local = Path.IsPathRooted(cmd) && (File.Exists(cmd) || Directory.Exists(cmd)); } catch { }
                if (local) items.Add(mb.Item("Speicherort öffnen", MenuHelper.Folder, delegate { Shell.ShowInExplorer(cmd); }));
                mb.Separator();
            }
            else if (it != null && it.Kind == ItemKind.Ordnermenue)
            {
                items.Add(mb.Item("Ordner öffnen", MenuHelper.Folder, delegate { Shell.Open(it.Folder, "", ""); }));
                mb.Separator();
            }
            if (it != null)
            {
                if (!it.IsSeparator) items.Add(mb.Item("Eigenschaften …", MenuHelper.Settings, delegate { EditItem(index); }));
                items.Add(mb.Item(it.IsSeparator ? "Trennlinie entfernen" : "Aus dem Dock entfernen", '', delegate { RemoveItem(index); }));
                mb.Separator();
            }
            items.Add(mb.Item("Programm oder Datei hinzufügen …", MenuHelper.Add, delegate { AddFile(insertAt); }));
            items.Add(mb.Item("Ordnermenü hinzufügen …", '', delegate { AddFolderMenu(insertAt); }));
            items.Add(mb.Item("Trennlinie einfügen", '', delegate { InsertItem(insertAt, new DockItem { Kind = ItemKind.Trennlinie }); }));
            mb.Separator();

            GlyphMenuItem hide = mb.Item("Automatisch ausblenden", '', delegate
            {
                _s.AutoHide = !_s.AutoHide;
                _s.Save();
                ApplySettings();
            });
            MenuBuilder.MakeToggle(hide, _s.AutoHide);
            items.Add(hide);
            GlyphMenuItem lockItem = mb.Item("Einträge sperren", '', delegate
            {
                _s.Locked = !_s.Locked;
                _s.Save();
            });
            MenuBuilder.MakeToggle(lockItem, _s.Locked);
            items.Add(lockItem);
            mb.Separator();
            items.Add(mb.Item("Einstellungen …", MenuHelper.Settings, delegate { if (OpenSettings != null) OpenSettings(); }));
            items.Add(mb.Item("Info über Dock Pilot", MenuHelper.Info, delegate { if (ShowAbout != null) ShowAbout(); }));
            items.Add(mb.Item("Beenden", MenuHelper.Power, delegate { if (ExitApp != null) ExitApp(); }));
            mb.End();
            return mb.Menu;
        }

        // ------------------------------------------------------------------ Screenshots

        /// <summary>
        /// Zeichnet das Dock ohne Fenster in ein Bild (für die README-Screenshots): Beispiel-Desktop als Bildschirm,
        /// Symbole sofort geladen, Maus über <paramref name="hover"/>, Markierung unter <paramref name="running"/>.
        /// Rückgabe in Fensterkoordinaten; <paramref name="window"/> = Lage auf dem Beispiel-Desktop,
        /// <paramref name="shelf"/> = Leiste samt vergrößerter Symbole und Beschriftung.
        /// </summary>
        internal static Bitmap RenderDemo(Settings s, List<DockItem> items, Rectangle desk, float scale, bool dark, Image desktop,
            int hover, int[] running, out Rectangle window, out Rectangle shelf)
        {
            Rectangle hoverRect;
            return RenderDemo(s, items, desk, scale, dark, desktop, hover, running, false, out window, out shelf, out hoverRect);
        }

        /// <summary>Wie oben; menuOpen: wie bei offenem Menü (ohne Beschriftung), hoverRect: Symbol unter der Maus im Fenster.</summary>
        internal static Bitmap RenderDemo(Settings s, List<DockItem> items, Rectangle desk, float scale, bool dark, Image desktop,
            int hover, int[] running, bool menuOpen, out Rectangle window, out Rectangle shelf, out Rectangle hoverRect)
        {
            using (DockWindow w = new DockWindow(s, items))
            {
                w._demo = true;
                w._dark = dark;
                w._accent = Color.FromArgb(0, 120, 212); // Windows-Standard-Akzentfarbe statt der eigenen
                foreach (DockItem it in items) w._slots.Add(new Slot { Item = it });
                w.ApplyMetrics(desk, desk, scale);
                w.BuildMica(desktop);
                int size = (int)Math.Max(48, Math.Min(256, Math.Ceiling(w._zoomPx)));
                foreach (Slot sl in w._slots)
                {
                    if (sl.Item.IsSeparator) continue;
                    sl.Master = IconLoader.Load(sl.Item, size);
                    w.Rescale(sl);
                }
                if (running != null)
                    foreach (int i in running)
                        if (i >= 0 && i < w._slots.Count) w._slots[i].Windows = new List<IntPtr> { IntPtr.Zero };

                w._show = 1f;
                w._zoom = 0f;
                w.DoLayout();
                if (hover >= 0 && hover < w._slots.Count)
                {
                    w._mouseAlong = w._slots[hover].BaseStart + w._basePx * 0.45f;
                    w._zoom = 1f;
                    w._hot = true;
                    w._hover = hover;
                    w.DoLayout();
                }
                w._menuOpen = menuOpen;
                hoverRect = Rectangle.Empty;
                if (hover >= 0 && hover < w._slots.Count)
                {
                    Slot hs = w._slots[hover];
                    hoverRect = Rectangle.Round(w.Map(hs.Pos, w._edgeDist + w._padA, hs.Size, hs.Size));
                }

                Rectangle wr = w._winRect; // Kopie: Felder eines Form-Objekts nicht direkt über die Instanz lesen (CS1690)
                Bitmap bmp = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    w.DrawScene(g);
                }
                window = wr;
                RectangleF r = w.Map(w._shelfStart, 0, w._shelfEnd - w._shelfStart, w._T);
                shelf = Rectangle.Round(r);
                return bmp;
            }
        }

        public void InsertItem(int index, DockItem item)
        {
            index = Math.Max(0, Math.Min(index, _items.Count));
            _items.Insert(index, item);
            Slot s = new Slot { Item = item };
            _slots.Insert(index, s);
            Save();
            ApplySettings();
            LoadIconsAsync(new List<Slot> { s });
        }

        private void RemoveItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            DockItem it = _items[index];
            if (!it.IsSeparator &&
                MessageBox.Show("„" + it.Title + "“ aus dem Dock entfernen?\n\nDas Programm selbst bleibt installiert.", AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _items.RemoveAt(index);
            _slots[index].DisposeImages();
            _slots.RemoveAt(index);
            _hover = -1;
            Save();
            ApplySettings();
        }

        private void EditItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _menuOpen = true; // Dock bleibt sichtbar, solange der Dialog offen ist
            try
            {
                using (ItemDialog dlg = new ItemDialog(_items[index].Clone()))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        _items[index] = dlg.Result;
                        ReloadSlot(index);
                        Save();
                        ApplySettings();
                    }
                }
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        private void AddFile(int index)
        {
            _menuOpen = true;
            try
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Programm oder Datei zum Dock hinzufügen";
                    dlg.Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc|Alle Dateien|*.*";
                    dlg.Multiselect = true;
                    dlg.DereferenceLinks = false;
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    foreach (string f in dlg.FileNames) InsertItem(index++, ItemFromPath(f));
                }
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        private void AddFolderMenu(int index)
        {
            _menuOpen = true;
            try
            {
                string folder = FolderPicker.Pick(null, "Ordner für das Ordnermenü wählen", null);
                if (string.IsNullOrEmpty(folder)) return;
                DockItem it = new DockItem();
                it.Kind = ItemKind.Ordnermenue;
                it.Folder = folder;
                it.Title = Path.GetFileName(folder.TrimEnd('\\'));
                if (string.IsNullOrEmpty(it.Title)) it.Title = folder;
                InsertItem(index, it);
            }
            finally { _menuOpen = false; _lastInside = DateTime.Now; }
        }

        /// <summary>Neuer Eintrag aus einer Datei, einem Ordner oder einer Verknüpfung (Ziel der Verknüpfung wird übernommen).</summary>
        public static DockItem ItemFromPath(string path)
        {
            DockItem it = new DockItem();
            it.Command = path;
            it.Title = MenuBuilder.DisplayName(path);
            if (Directory.Exists(path))
            {
                if (string.IsNullOrEmpty(it.Title)) it.Title = path;
                return it;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lnk")
            {
                ShortcutInfo lnk = ShortcutInfo.Read(path);
                if (lnk != null && !string.IsNullOrEmpty(lnk.Target) && (File.Exists(lnk.Target) || Directory.Exists(lnk.Target)))
                {
                    it.Command = lnk.Target;
                    it.Arguments = lnk.Arguments;
                    it.WorkingDirectory = lnk.WorkingDirectory;
                    if (!string.IsNullOrEmpty(lnk.IconFile)) it.Icon = lnk.IconFile + "?" + lnk.IconIndex;
                }
            }
            else if (ext == ".exe")
            {
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 1) it.Title = desc.Trim();
                }
                catch { }
            }
            return it;
        }

        // ------------------------------------------------------------------ Ziehen aus dem Explorer

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; _extDrag = true; UpdateDrop(e); }
            else e.Effect = DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            UpdateDrop(e);
        }

        private void UpdateDrop(DragEventArgs e)
        {
            float along, across;
            ToDock(e.X, e.Y, out along, out across);
            int onto = -1, index = _slots.Count;
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                float rel = (along - s.Pos) / Math.Max(1f, s.Len);
                if (!s.Item.IsSeparator && s.Item.Kind == ItemKind.Programm && rel >= 0.25f && rel <= 0.75f && IsProgram(s.Item.Command))
                {
                    onto = i;
                    break;
                }
                if (along < s.Pos + s.Len / 2f) { index = i; break; }
            }
            if (onto != _dropOnto || index != _dropIndex) { _dropOnto = onto; _dropIndex = index; _dirty = true; }
        }

        private static bool IsProgram(string cmd)
        {
            string c = IconLoader.Expand(cmd).ToLowerInvariant();
            return c.EndsWith(".exe") || c.EndsWith(".bat") || c.EndsWith(".cmd");
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _extDrag = false;
            _dropIndex = _dropOnto = -1;
            _dirty = true;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            int onto = _dropOnto, index = _dropIndex;
            _extDrag = false;
            _dropIndex = _dropOnto = -1;
            _dirty = true;
            if (files == null || files.Length == 0) return;
            if (onto >= 0 && onto < _items.Count)
            {
                // Dateien auf ein Programm gezogen: damit öffnen
                DockItem it = _items[onto];
                string args = it.Arguments;
                foreach (string f in files) args += (args.Length > 0 ? " " : "") + "\"" + f + "\"";
                string err = Shell.Start(it.Command, args, it.WorkingDirectory, it.ShowCmd, it.RunAsAdmin);
                if (err != null) MessageBox.Show(err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (_s.Animate) { _slots[onto].BounceStart = DateTime.Now; }
                return;
            }
            if (index < 0) index = _items.Count;
            foreach (string f in files) InsertItem(index++, ItemFromPath(f));
        }

        // ------------------------------------------------------------------ Tastenkürzel, Systemereignisse

        private void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            DockNative.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyOk = true;
            if (_s.HotkeyKey > 0)
                _hotkeyOk = DockNative.RegisterHotKey(Handle, HotkeyId, _s.HotkeyModifiers | DockNative.MOD_NOREPEAT, _s.HotkeyKey);
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
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
                if (IsHandleCreated) DockNative.UnregisterHotKey(Handle, HotkeyId);
                foreach (Slot s in _slots) s.DisposeImages();
                if (_mica != null) _mica.Dispose();
                FreeBuffer();
            }
            base.Dispose(disposing);
        }
    }
}
