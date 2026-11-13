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
    /// <summary>Zeichnen: Leiste, Symbole, Beschriftung und Ausgabe ins Fenster. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
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
            return (float)(_bounceH * 0.45 * Fx * Math.Sin(t * Math.PI));
        }

        /// <summary>Wackeln: Drehwinkel in Grad, schwingt nach dem Draufgehen kurz aus.</summary>
        private float Wobble(Slot s)
        {
            if (s.HopStart == DateTime.MinValue) return 0f;
            double t = (DateTime.Now - s.HopStart).TotalMilliseconds / (HopMs * AnimFactor);
            if (t >= 1) return 0f;
            return (float)(9.0 * Fx * Math.Sin(t * 5 * Math.PI) * (1 - t));
        }

        /// <summary>Sockel: helle, abgerundete Fläche hinter dem Symbol, wie beim Überfahren in der Taskleiste.</summary>
        private void DrawPlate(Graphics g, Slot s, float a0, bool dark)
        {
            float z = Math.Max(0f, Math.Min(1f, _zoom));
            if (z <= 0.01f) return;
            float pad = Math.Max(1f, Math.Min(R(5), _padA - 1f));
            RectangleF r = Map(s.Pos - pad, a0 + _padA - pad, s.Size + 2 * pad, s.Size + 2 * pad);
            using (GraphicsPath path = Theme.RoundRect(r, R(6)))
            {
                float k = Fx;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, (int)((dark ? 40 : 150) * z * k)), 255, 255, 255))) g.FillPath(b, path);
                using (Pen pen = new Pen(Color.FromArgb(Math.Min(255, (int)((dark ? 34 : 46) * z * k)), dark ? Color.White : Color.Black))) g.DrawPath(pen, path);
            }
        }

        /// <summary>Unterstrich: ein Strich in Akzentfarbe wächst zum Bildschirmrand hin unter dem Symbol von der Mitte nach außen.</summary>
        private void DrawUnderline(Graphics g, Slot s, float a0)
        {
            float z = Math.Max(0f, Math.Min(1f, _zoom));
            float len = s.Size * Math.Min(1f, 0.7f * Fx) * z, thick = Math.Max(2f, R(3));
            if (len < 1f) return;
            RectangleF r = Map(s.Pos + s.Size / 2f - len / 2f, a0 + Math.Max(1f, R(2)), len, thick);
            using (GraphicsPath path = Theme.RoundRect(r, Math.Min(r.Width, r.Height) / 2f))
            using (SolidBrush b = new SolidBrush(_accent))
                g.FillPath(b, path);
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
                    int hl = _s.Highlight;
                    if (hl == 8) lift += Hop(s);                                    // Federn
                    float sat = 1f, tint = 0f, angle = 0f;
                    if (i == _effectIndex && hl == 15) tint = Math.Min(0.8f, 0.3f * Fx) * Math.Max(0f, Math.Min(1f, _zoom)); // Einfärben
                    if (i == _effectIndex && hl == 9)
                    {
                        // Kippen: neigt sich zu der Seite, auf der die Maus steht (höchstens 10 Grad)
                        float lean = (_mouseAlong - (s.Pos + s.Size / 2f)) / Math.Max(1f, s.Size / 2f);
                        angle = 10f * Fx * Math.Max(-1f, Math.Min(1f, lean)) * Math.Max(0f, Math.Min(1f, _zoom));
                    }
                    if (hl == 11) angle = Wobble(s);                                // Wackeln
                    if (i == _effectIndex && hl == 12) DrawPlate(g, s, a0, dark);   // Sockel
                    if (hl == 7 || hl == 14)
                    {
                        // Fokus als Lichtkegel: Das Symbol unter der Maus und je ein Nachbar bleiben voll sichtbar,
                        // danach nimmt die Deckkraft über zwei Symbole bis auf 50 % ab. Der helle Bereich wandert mit der Maus.
                        float focus = _demo ? Math.Max(0f, Math.Min(1f, _zoom)) : _focus;
                        if (focus > 0f)
                        {
                            float dist = Math.Abs(s.Pos + s.Size / 2f - _mouseAlong) / Math.Max(1f, _basePx + _spacing);
                            float t = Math.Max(0f, Math.Min(1f, (dist - 1.2f) / 2f));
                            t = t * t * (3f - 2f * t) * focus;
                            if (hl == 7) alpha = 1f - Math.Min(0.9f, 0.5f * Fx) * t;
                            else { sat = 1f - Math.Min(1f, t * Fx); alpha = 1f - 0.2f * t; } // Entsättigen: grau und nur leicht blasser
                        }
                    }
                    if (i == _effectIndex && hl == 6) DrawGlow(g, s, a0, dark);    // Leuchten
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
                    if (angle != 0f)
                    {
                        // um die Mitte des Symbols gedreht zeichnen
                        RectangleF ir = Map(pos, a0 + _padA + Bounce(s) + lift, size, size);
                        float cx = ir.X + ir.Width / 2f, cy = ir.Y + ir.Height / 2f;
                        GraphicsState state = g.Save();
                        g.TranslateTransform(cx, cy);
                        g.RotateTransform(angle);
                        g.TranslateTransform(-cx, -cy);
                        DrawIcon(g, s, pos, a0 + _padA + Bounce(s) + lift, size, Math.Min(alpha, 0.999f), pressed, sat, tint); // nie die 1:1-Abkürzung
                        g.Restore(state);
                    }
                    else DrawIcon(g, s, pos, a0 + _padA + Bounce(s) + lift, size, alpha, pressed, sat, tint);
                    bool attention = Attention(s); // ein Fenster blinkt: Strich breiter, orange und pulsierend
                    if ((_s.ShowRunning || attention) && s.Windows.Count > 0)
                    {
                        float pl = R(attention ? 20 : s.Windows.Count > 1 ? 14 : 7), pt = Math.Max(2f, R(3));
                        RectangleF pr = Map(s.Pos + s.Size / 2f - pl / 2f, a0 + Math.Max(1f, R(2)), pl, pt);
                        Color mark = _accent;
                        if (attention)
                        {
                            double pulse = _s.Animate ? 0.5 + 0.5 * Math.Sin(Environment.TickCount / 220.0) : 1.0;
                            mark = Color.FromArgb(130 + (int)(125 * pulse), 255, 140, 0);
                        }
                        using (GraphicsPath path = Theme.RoundRect(pr, Math.Min(pr.Width, pr.Height) / 2f))
                        using (SolidBrush b = new SolidBrush(mark))
                            g.FillPath(b, path);
                    }
                    if (i == _effectIndex && hl == 13) DrawUnderline(g, s, a0);     // Unterstrich
                }

                // gezogenes Symbol folgt der Maus
                if (_dragging && !_dragOut && _dragIndex >= 0 && _dragIndex < _slots.Count)
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
                    string text = !(_extDrag && _dropOnto >= 0) ? s.Item.Title
                        : s.Item.Kind == ItemKind.Ordnermenue ? "In " + s.Item.Title + " ablegen" : "Öffnen mit " + s.Item.Title;
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
                    b.CenterColor = Color.FromArgb(Math.Min(255, (int)((dark ? 150 : 130) * z * Fx)), _accent);
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
            DrawIcon(g, s, along, across, size, alpha, pressed, 1f, 0f);
        }

        /// <summary>sat: Farbsättigung (1 = normal, 0 = grau); tint: Anteil Akzentfarbe (0 = keiner).</summary>
        private void DrawIcon(Graphics g, Slot s, float along, float across, float size, float alpha, bool pressed, float sat, float tint)
        {
            RectangleF rf = Map(along, across, size, size);
            int px = Math.Max(1, (int)Math.Round(size));
            Rectangle r = new Rectangle((int)Math.Round(rf.X + (rf.Width - px) / 2f), (int)Math.Round(rf.Y + (rf.Height - px) / 2f), px, px);
            // fast unsichtbare Fläche: ganze Symbolfläche nimmt Mausklicks an (auch durchsichtige Ecken)
            using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(hit, r);
            if (s.Base == null) return;
            bool plain = alpha >= 1f && !pressed && sat >= 1f && tint <= 0f;
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
                    // Helligkeit (gedrückt), Sättigung, Akzentfarbe und Deckkraft in einer Farbmatrix
                    float k = (pressed ? 0.72f : 1f) * (1f - tint);
                    float u = 1f - Math.Max(0f, Math.Min(1f, sat));
                    float lr = 0.299f * u * k, lg = 0.587f * u * k, lb = 0.114f * u * k, d = (1f - u) * k;
                    ia.SetColorMatrix(new ColorMatrix(new float[][]
                    {
                        new float[] { lr + d, lr, lr, 0, 0 },
                        new float[] { lg, lg + d, lg, 0, 0 },
                        new float[] { lb, lb, lb + d, 0, 0 },
                        new float[] { 0, 0, 0, alpha, 0 },
                        new float[] { tint * _accent.R / 255f, tint * _accent.G / 255f, tint * _accent.B / 255f, 0, 1 }
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
    }
}
