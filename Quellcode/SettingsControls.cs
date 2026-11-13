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
