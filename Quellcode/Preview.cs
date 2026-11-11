// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later
// Fenstervorschau wie in der Windows-Taskleiste

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Fenstervorschau wie in der Windows-Taskleiste: kleines Fenster über dem Symbol eines laufenden Programms,
    /// je Fenster eine Kachel mit Titel und Live-Vorschaubild (DWM-Thumbnail). Klick holt das Fenster nach vorne,
    /// das × oben rechts schließt es. Das Fenster wird nie aktiv (nimmt keinem Programm den Fokus).
    /// </summary>
    internal sealed class PreviewPopup : Form
    {
        private sealed class Tile
        {
            public IntPtr Hwnd, Thumb;
            public string Title;
            public bool Minimized;
            public Rectangle Bounds, Image, Close;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThumbProps
        {
            public int Flags;
            public RECT Destination, Source;
            public byte Opacity;
            public int Visible, ClientAreaOnly; // BOOL
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_CLOSE = 0x0010, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly Timer _watch = new Timer { Interval = 500 };
        private float _sc = 1f;
        private bool _vertical;
        private int _hover = -1;
        private bool _hoverClose;
        private Rectangle _anchor, _area;
        private DockEdge _edge;
        private int _gap;

        /// <summary>Wird ausgelöst, wenn die Vorschau sich selbst schließt (Klick oder alle Fenster zu).</summary>
        public event EventHandler Dismissed;

        public PreviewPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _watch.Tick += delegate { Watch(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseRoundCorners(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        private int S(float v) { return (int)Math.Round(v * _sc); }

        /// <summary>
        /// Vorschau für die Fenster eines Programms zeigen. anchor: Symbol im Dock (Bildschirm), gap: Abstand zum Dock,
        /// area: Bildschirm, in dem die Vorschau bleiben muss.
        /// </summary>
        public void ShowFor(IList<IntPtr> windows, string fallbackTitle, Rectangle anchor, DockEdge edge, int gap, Rectangle area, float scale)
        {
            Release();
            _sc = scale;
            _edge = edge;
            _anchor = anchor;
            _gap = gap;
            _area = area;
            _vertical = edge == DockEdge.Links || edge == DockEdge.Rechts;
            _hover = -1;
            _hoverClose = false;
            foreach (IntPtr h in windows)
            {
                if (!IsWindow(h)) continue;
                Tile t = new Tile();
                t.Hwnd = h;
                t.Title = DockNative.WindowTitle(h);
                if (string.IsNullOrEmpty(t.Title)) t.Title = fallbackTitle;
                t.Minimized = DockNative.IsMinimized(h);
                _tiles.Add(t);
                if (_tiles.Count >= 10) break;
            }
            if (_tiles.Count == 0) { Hide(); return; }
            IntPtr handle = Handle; // Fenster anlegen
            foreach (Tile t in _tiles)
                if (!t.Minimized && DwmRegisterThumbnail(handle, t.Hwnd, out t.Thumb) != 0) t.Thumb = IntPtr.Zero;
            DoLayout();
            if (!Visible) Show();
            UpdateThumbs();
            Invalidate();
            _watch.Start();
        }

        public new void Hide()
        {
            _watch.Stop();
            Release();
            if (Visible) base.Hide();
        }

        private void Release()
        {
            foreach (Tile t in _tiles)
                if (t.Thumb != IntPtr.Zero) { DwmUnregisterThumbnail(t.Thumb); t.Thumb = IntPtr.Zero; }
            _tiles.Clear();
        }

        /// <summary>Kacheln anordnen (nebeneinander, bei Dock links/rechts untereinander) und das Fenster am Symbol ausrichten.</summary>
        private void DoLayout()
        {
            int pad = S(8), titleH = S(30), inner = S(8);
            int n = _tiles.Count;
            // Kachelgröße: Vorschaubild höchstens 200 × 120, bei vielen Fenstern kleiner, damit alles auf den Bildschirm passt
            float imgW = 200 * _sc, imgH = 120 * _sc;
            float room = (_vertical ? _area.Height : _area.Width) - 2 * pad - S(16);
            float need = _vertical ? n * (imgH + titleH + 2 * inner) : n * (imgW + 2 * inner);
            float k = need > room && need > 0 ? Math.Max(0.4f, room / need) : 1f;
            imgW *= k;
            imgH *= k;
            int tileW = (int)imgW + 2 * inner, tileH = titleH + (int)imgH + inner;
            int x = pad, y = pad;
            foreach (Tile t in _tiles)
            {
                t.Bounds = new Rectangle(x, y, tileW, tileH);
                Rectangle box = new Rectangle(x + inner, y + titleH, (int)imgW, (int)imgH);
                t.Image = box;
                if (t.Thumb != IntPtr.Zero)
                {
                    SIZE src;
                    if (DwmQueryThumbnailSourceSize(t.Thumb, out src) == 0 && src.Width > 0 && src.Height > 0)
                    {
                        float f = Math.Min(box.Width / (float)src.Width, box.Height / (float)src.Height);
                        f = Math.Min(f, 1f);
                        int w = Math.Max(1, (int)(src.Width * f)), h = Math.Max(1, (int)(src.Height * f));
                        t.Image = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
                    }
                }
                int cs = S(22);
                t.Close = new Rectangle(t.Bounds.Right - cs - S(4), t.Bounds.Y + (titleH - cs) / 2, cs, cs);
                if (_vertical) y += tileH + S(4); else x += tileW + S(4);
            }
            int width = _vertical ? tileW + 2 * pad : x - S(4) + pad;
            int height = _vertical ? y - S(4) + pad : tileH + 2 * pad;

            // über (bzw. neben) dem Symbol, mittig dazu, innerhalb des Bildschirms
            int left, top;
            switch (_edge)
            {
                case DockEdge.Oben: left = _anchor.Left + _anchor.Width / 2 - width / 2; top = _anchor.Bottom + _gap; break;
                case DockEdge.Links: left = _anchor.Right + _gap; top = _anchor.Top + _anchor.Height / 2 - height / 2; break;
                case DockEdge.Rechts: left = _anchor.Left - _gap - width; top = _anchor.Top + _anchor.Height / 2 - height / 2; break;
                default: left = _anchor.Left + _anchor.Width / 2 - width / 2; top = _anchor.Top - _gap - height; break;
            }
            left = Math.Max(_area.Left + S(8), Math.Min(left, _area.Right - S(8) - width));
            top = Math.Max(_area.Top + S(8), Math.Min(top, _area.Bottom - S(8) - height));
            Bounds = new Rectangle(left, top, width, height);
        }

        private void UpdateThumbs()
        {
            foreach (Tile t in _tiles)
            {
                if (t.Thumb == IntPtr.Zero) continue;
                ThumbProps p = new ThumbProps();
                p.Flags = 0x1 | 0x4 | 0x8 | 0x10; // Ziel, Deckkraft, sichtbar, nur Fensterinhalt
                p.Destination = new RECT { Left = t.Image.Left, Top = t.Image.Top, Right = t.Image.Right, Bottom = t.Image.Bottom };
                p.Opacity = 255;
                p.Visible = 1;
                p.ClientAreaOnly = 0;
                DwmUpdateThumbnailProperties(t.Thumb, ref p);
            }
        }

        /// <summary>Geschlossene Fenster entfernen, Titel aktuell halten.</summary>
        private void Watch()
        {
            if (!Visible) return;
            bool changed = false;
            for (int i = _tiles.Count - 1; i >= 0; i--)
            {
                Tile t = _tiles[i];
                if (!IsWindow(t.Hwnd))
                {
                    if (t.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(t.Thumb);
                    _tiles.RemoveAt(i);
                    changed = true;
                    continue;
                }
                string title = DockNative.WindowTitle(t.Hwnd);
                if (!string.IsNullOrEmpty(title) && title != t.Title) { t.Title = title; Invalidate(); }
            }
            if (!changed) return;
            if (_tiles.Count == 0) { Dismiss(); return; }
            _hover = -1;
            DoLayout();
            UpdateThumbs();
            Invalidate();
        }

        private void Dismiss()
        {
            Hide();
            if (Dismissed != null) Dismissed(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Bounds.Contains(e.Location)) { h = i; break; }
            bool close = h >= 0 && _tiles[h].Close.Contains(e.Location);
            if (h == _hover && close == _hoverClose) return;
            _hover = h;
            _hoverClose = close;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            _hover = -1;
            _hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_hover < 0 || _hover >= _tiles.Count) return;
            Tile t = _tiles[_hover];
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && t.Close.Contains(e.Location)))
            {
                // Fenster schließen (wie × bzw. Mittelklick in der Taskleiste); die Vorschau bleibt für die übrigen offen
                PostMessage(t.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            IntPtr hwnd = t.Hwnd;
            Dismiss();
            DockNative.Activate(hwnd);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = Theme.Current;
            Color back = p.Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font f = Theme.UiFont(12f * _sc, FontStyle.Regular))
            using (Font gf = new Font(Theme.IconFontName, 10f * _sc, GraphicsUnit.Pixel))
                for (int i = 0; i < _tiles.Count; i++)
                {
                    Tile t = _tiles[i];
                    bool hot = i == _hover;
                    if (hot)
                        using (GraphicsPath path = Theme.RoundRect(new RectangleF(t.Bounds.X + 0.5f, t.Bounds.Y + 0.5f, t.Bounds.Width - 1, t.Bounds.Height - 1), S(5)))
                        using (SolidBrush b = new SolidBrush(p.Dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(232, 232, 232)))
                            g.FillPath(b, path);
                    int titleRight = hot ? t.Close.Left - S(4) : t.Bounds.Right - S(8);
                    Rectangle tr = new Rectangle(t.Bounds.X + S(8), t.Bounds.Y, titleRight - (t.Bounds.X + S(8)), S(30));
                    TextRenderer.DrawText(g, t.Title, f, tr, p.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    if (hot)
                    {
                        if (_hoverClose)
                            using (GraphicsPath path = Theme.RoundRect(t.Close, S(4)))
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(196, 43, 28)))
                                g.FillPath(b, path);
                        TextRenderer.DrawText(g, "", gf, t.Close, _hoverClose ? Color.White : p.Text,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    }
                    if (t.Thumb == IntPtr.Zero)
                    {
                        // minimiert oder keine Vorschau möglich: Fläche mit Hinweis
                        Rectangle box = new Rectangle(t.Bounds.X + S(8), t.Bounds.Y + S(30), t.Bounds.Width - S(16), t.Bounds.Height - S(30) - S(8));
                        using (GraphicsPath path = Theme.RoundRect(box, S(4)))
                        using (SolidBrush b = new SolidBrush(p.Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238)))
                            g.FillPath(b, path);
                        TextRenderer.DrawText(g, t.Minimized ? "Minimiert" : "Keine Vorschau", f, box, p.SubText,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    }
                }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watch.Dispose();
                Release();
            }
            base.Dispose(disposing);
        }
    }
}