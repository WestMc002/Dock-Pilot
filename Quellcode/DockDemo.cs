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
    /// <summary>Screenshots: das Dock mit Beispieldaten zeichnen. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
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

        // ------------------------------------------------------------------ Beispiel-Dock im Einstellungsfenster

        private Rectangle _previewDesk;

        /// <summary>Für die Vorschau immer unten, mittig, ohne Beschriftung – es geht nur um die Effekte.</summary>
        private static void PreviewAdjust(Settings p)
        {
            p.Edge = DockEdge.Unten;
            p.Monitor = -1;
            p.EdgeDistance = 0;
            p.CenterPercent = 0;
            p.AutoHide = false;
            p.Labels = false;
            p.ShowRunning = false;
        }

        /// <summary>
        /// Beispiel-Dock ohne Fenster für die Effekt-Vorschau: zeichnet mit PreviewFrame Bild für Bild, mit Animationen.
        /// width: Breite der Fläche in Pixeln, back: Farbe hinter dem Dock.
        /// </summary>
        internal static DockWindow CreatePreview(Settings s, List<DockItem> items, int width, float scale, bool dark, Color accent, Color back)
        {
            Settings p = s.Clone();
            PreviewAdjust(p);
            DockWindow w = new DockWindow(p, items);
            w._demo = true;
            w._animDemo = true;
            w._dark = dark;
            w._accent = accent;
            foreach (DockItem it in items) w._slots.Add(new Slot { Item = it });
            w._previewDesk = new Rectangle(0, 0, Math.Max(100, width), (int)(600 * scale));
            w.ApplyMetrics(w._previewDesk, w._previewDesk, scale);
            using (Bitmap bg = new Bitmap(8, 8))
            {
                using (Graphics g = Graphics.FromImage(bg)) g.Clear(back);
                w.BuildMica(bg);
            }
            w.PreviewIcons();
            w._show = 1f;
            w._zoom = 0f;
            w.DoLayout();
            return w;
        }

        private void PreviewIcons()
        {
            int size = (int)Math.Max(48, Math.Min(256, Math.Ceiling(_zoomPx)));
            foreach (Slot sl in _slots)
            {
                if (sl.Item.IsSeparator) continue;
                if (sl.Master != null && Math.Max(sl.Master.Width, sl.Master.Height) >= Math.Min(256f, _zoomPx) - 0.5f) continue;
                Bitmap old = sl.Master;
                sl.Master = IconLoader.Load(sl.Item, size);
                if (old != null) old.Dispose();
                Rescale(sl);
            }
        }

        /// <summary>Vorschau: geänderte Einstellungen übernehmen.</summary>
        internal void PreviewApply(Settings s)
        {
            _s.CopyFrom(s);
            PreviewAdjust(_s);
            ApplyMetrics(_previewDesk, _previewDesk, _sc);
            PreviewIcons();
            foreach (Slot sl in _slots) if (sl.Base == null || sl.Base.Width != Math.Max(1, (int)_basePx)) Rescale(sl);
            _hopIndex = -1;
        }

        /// <summary>
        /// Vorschau: ein Bild mit der Maus an der Stelle t (0 = erstes, 1 = letztes Symbol) und der Vergrößerung zoom (0..1).
        /// Das Bild ist so breit wie die Fläche und reicht vom unteren Rand nach oben.
        /// </summary>
        internal Bitmap PreviewFrame(float t, float zoom)
        {
            if (_slots.Count == 0) return null;
            Slot first = _slots[0], last = _slots[_slots.Count - 1];
            float a = first.Rest + BaseLen(first) / 2f, b = last.Rest + BaseLen(last) / 2f;
            _mouseAlong = a + (b - a) * Math.Max(0f, Math.Min(1f, t));
            _zoom = Math.Max(0f, Math.Min(1f, zoom));
            _hot = _zoom > 0f;
            _hover = -1;
            DoLayout();
            Rectangle wr = _winRect;
            if (wr.Width <= 0 || wr.Height <= 0) return null;
            Bitmap bmp = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                DrawScene(g);
            }
            return bmp;
        }
    }
}
