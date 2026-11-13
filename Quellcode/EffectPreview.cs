// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Effekt-Vorschau im Einstellungsfenster: ein kleines Beispiel-Dock mit den ersten Einträgen des echten Docks,
    /// über das eine gedachte Maus hin- und herfährt. Zeigt Größen-Effekt und Hervorhebung sofort, ohne zu speichern.
    /// </summary>
    internal sealed class EffectPreview : Control
    {
        private const int MaxItems = 7;
        private readonly Timer _timer = new Timer { Interval = 33 };
        private DockWindow _dock;
        private Bitmap _frame;
        private DateTime _start = DateTime.Now;
        private float _zoom;
        private float _scale = 1f;
        private List<DockItem> _items;
        private Settings _settings;
        private Color _builtBack;
        private bool _builtDark;

        public EffectPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            _timer.Tick += delegate { Step(); };
        }

        /// <summary>Einträge und Einstellungen setzen (die ersten Einträge ohne Trennlinien).</summary>
        public void Setup(Settings settings, List<DockItem> items, float scale)
        {
            _scale = scale;
            _settings = settings.Clone();
            _items = new List<DockItem>();
            if (items != null)
                foreach (DockItem it in items)
                {
                    if (it.IsSeparator || it.Temp) continue;
                    _items.Add(it.Clone());
                    if (_items.Count >= MaxItems) break;
                }
            Rebuild();
            _timer.Start();
        }

        /// <summary>Geänderte Einstellungen zeigen.</summary>
        public void Apply(Settings settings)
        {
            _settings = settings.Clone();
            try { if (_dock != null) _dock.PreviewApply(_settings); } catch { }
        }

        private void Rebuild()
        {
            DropDock();
            if (_items == null || _items.Count == 0 || Width < 50 || _settings == null) return;
            Palette p = Theme.Current;
            _builtBack = p.Card;
            _builtDark = p.Dark;
            try { _dock = DockWindow.CreatePreview(_settings, _items, Width, _scale, p.Dark, Theme.Accent, p.Card); }
            catch { _dock = null; }
        }

        private void DropDock()
        {
            if (_dock != null) { try { _dock.Dispose(); } catch { } _dock = null; }
        }

        private void Step()
        {
            if (!Visible || !IsHandleCreated || IsDisposed) return;
            Palette p = Theme.Current;
            if (_dock != null && (p.Dark != _builtDark || p.Card != _builtBack)) Rebuild(); // hell/dunkel umgestellt
            if (_dock == null) return;
            // 8 Sekunden je Runde: hin (3,2 s), kurz weg, zurück (3,2 s), kurz weg
            double u = (DateTime.Now - _start).TotalSeconds % 8.0;
            float t;
            bool over;
            if (u < 3.2) { t = Ease((float)(u / 3.2)); over = true; }
            else if (u < 4.0) { t = 1f; over = false; }
            else if (u < 7.2) { t = 1f - Ease((float)((u - 4.0) / 3.2)); over = true; }
            else { t = 0f; over = false; }
            float target = over ? 1f : 0f;
            _zoom += Math.Sign(target - _zoom) * Math.Min(Math.Abs(target - _zoom), 0.2f);
            Bitmap next = null;
            try { next = _dock.PreviewFrame(t, _zoom); } catch { }
            if (next == null) return;
            Bitmap old = _frame;
            _frame = next;
            if (old != null) old.Dispose();
            Invalidate();
        }

        private static float Ease(float x)
        {
            x = Math.Max(0f, Math.Min(1f, x));
            return x * x * (3f - 2f * x);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            e.Graphics.Clear(p.Card);
            if (_frame == null)
            {
                TextRenderer.DrawText(e.Graphics, "Keine Einträge im Dock", Font, ClientRectangle, p.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // unten bündig, waagrecht mittig
            e.Graphics.DrawImageUnscaled(_frame, (Width - _frame.Width) / 2, Height - _frame.Height);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_settings != null && IsHandleCreated) Rebuild();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                DropDock();
                if (_frame != null) { _frame.Dispose(); _frame = null; }
            }
            base.Dispose(disposing);
        }
    }
}
