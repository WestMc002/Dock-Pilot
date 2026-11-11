// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Farben für das Menü, passend zum hellen oder dunklen Windows-Design.</summary>
    internal sealed class MenuTheme
    {
        public bool Dark;
        public Color Back, Border, Text, Muted, Hover, Separator, Accent, AccentText, Section;

        public static MenuTheme Current()
        {
            bool dark = false;
            if (Theme.Override.HasValue) dark = Theme.Override.Value;
            else try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
            }
            catch { }
            var t = new MenuTheme { Dark = dark };
            if (dark)
            {
                t.Back = Color.FromArgb(44, 44, 44);
                t.Border = Color.FromArgb(70, 70, 70);
                t.Text = Color.FromArgb(240, 240, 240);
                t.Muted = Color.FromArgb(160, 160, 160);
                t.Hover = Color.FromArgb(62, 62, 62);
                t.Separator = Color.FromArgb(68, 68, 68);
                t.Section = Color.FromArgb(130, 130, 130);
            }
            else
            {
                t.Back = Color.FromArgb(249, 249, 249);
                t.Border = Color.FromArgb(220, 220, 220);
                t.Text = Color.FromArgb(28, 28, 28);
                t.Muted = Color.FromArgb(110, 110, 110);
                t.Hover = Color.FromArgb(234, 234, 234);
                t.Separator = Color.FromArgb(226, 226, 226);
                t.Section = Color.FromArgb(150, 150, 150);
            }
            // Windows-Akzentfarbe (bzw. eigene Farbe aus den Einstellungen)
            t.Accent = Theme.Accent;
            t.AccentText = Theme.ContrastText(t.Accent);
            return t;
        }
    }

    /// <summary>Zeichnet Menüs im Stil von Windows 11 (abgerundete Markierung, dezente Trennlinien).</summary>
    internal sealed class ModernRenderer : ToolStripRenderer
    {
        private readonly MenuTheme _t;
        private readonly float _k;

        public ModernRenderer(MenuTheme theme, float scale)
        {
            _t = theme;
            _k = scale;
        }

        private float S(float v) { return v * _k; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(_t.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(_t.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        // Breite der Symbolspalte je Menü (wird beim Zeichnen des Menühintergrunds ermittelt)
        private readonly Dictionary<ToolStrip, int> _marginWidth = new Dictionary<ToolStrip, int>();

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            _marginWidth[e.ToolStrip] = e.AffectedBounds.Right;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected && e.Item.Enabled)
            {
                var r = new RectangleF(S(4), S(1), e.Item.Width - S(8), e.Item.Height - S(2));
                using (var path = Theme.RoundRect(r, S(4)))
                using (var b = new SolidBrush(_t.Hover))
                    g.FillPath(b, path);
            }

            // Symbol, Haken bzw. Punkt zeichnet OnRenderItemText – ausgerichtet am Text, nicht an der Zeile
        }

        /// <summary>Symbol, Haken oder Punkt mittig in der Symbolspalte, senkrecht auf Höhe der Schrift.</summary>
        private void DrawItemMark(Graphics g, GlyphMenuItem item, float cy)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int margin;
            if (item.Owner == null || !_marginWidth.TryGetValue(item.Owner, out margin)) margin = (int)S(36);
            float cx = margin / 2f + S(2);
            if (item.Checked && !item.IsToggle)
            {
                if (item.Radio)
                {
                    float d = S(8);
                    using (var b = new SolidBrush(_t.Accent))
                        g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                }
                else
                {
                    float s = S(5);
                    using (var p = new Pen(_t.Accent, S(1.8f)))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        g.DrawLines(p, new[] { new PointF(cx - s, cy), new PointF(cx - s / 3, cy + s * 0.7f), new PointF(cx + s, cy - s * 0.7f) });
                    }
                }
            }
            else if (item.Glyph != null)
            {
                g.DrawImage(item.Glyph, (float)Math.Round(cx - item.Glyph.Width / 2f), (float)Math.Round(cy - item.Glyph.Height / 2f), item.Glyph.Width, item.Glyph.Height);
            }
        }

        /// <summary>
        /// Senkrechte Mitte der sichtbaren Schrift: Windows zentriert die ganze Zeilenhöhe
        /// (inklusive Unterlängen). Die Buchstaben selbst sitzen dadurch etwas tiefer als die
        /// Mitte des Textbereichs – genau dort sollen auch Symbole und Schalter stehen.
        /// </summary>
        private static float TextVisualCenter(Graphics g, Font f, Rectangle textRect)
        {
            float center = textRect.Y + textRect.Height / 2f;
            try
            {
                FontFamily ff = f.FontFamily;
                FontStyle st = f.Style;
                float em = f.SizeInPoints * g.DpiY / 72f;
                float up = ff.GetEmHeight(st);
                float asc = ff.GetCellAscent(st);
                float desc = ff.GetCellDescent(st);
                float lineCenter = (asc + desc) / 2f / up * em;
                float capCenter = (asc - 0.70f * up / 2f) / up * em; // Großbuchstaben ca. 70 % der Schriftgröße
                center += capCenter - lineCenter;
            }
            catch { }
            return center;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Schalter (z. B. "Mit Windows starten") statt Tastenkürzel-Text zeichnen
            var gi = e.Item as GlyphMenuItem;

            // Text senkrecht genau in die Zeilenmitte setzen (nach der Mitte der Buchstaben).
            // Windows zeichnet ihn sonst bei fester Zeilenhöhe zu weit oben – dann wären die
            // Abstände zu Trennlinien und Menürand oben und unten verschieden groß.
            Font font = e.TextFont ?? e.Item.Font;
            Rectangle tr = e.TextRectangle;
            float shift = TextVisualCenter(e.Graphics, font, new Rectangle(0, 0, tr.Width, tr.Height)) - tr.Height / 2f;
            int newY = (int)Math.Round((e.Item.Height - tr.Height) / 2f - shift);
            // Haupttext näher an die Symbolspalte: Windows lässt 14 × Skalierung Abstand,
            // gewünscht sind MenuHelper.TextGap (8) – Tastenkürzel-Text bleibt rechts stehen.
            int newX = tr.X;
            if (e.Text == e.Item.Text) newX -= (int)Math.Round(S(14 - MenuHelper.TextGap));
            e.TextRectangle = new Rectangle(newX, newY, tr.Width, tr.Height);
            float textCenter = TextVisualCenter(e.Graphics, font, e.TextRectangle);
            if (gi != null && e.Text == gi.Text) DrawItemMark(e.Graphics, gi, textCenter);
            if (gi != null && gi.IsToggle && e.Text == gi.ShortcutKeyDisplayString)
            {
                DrawToggle(e.Graphics, e.TextRectangle, gi.ToggleOn, textCenter);
                return;
            }
            e.TextColor = e.Item.Enabled ? _t.Text : _t.Muted;
            var item = e.Item as ToolStripMenuItem;
            if (item != null && e.TextRectangle.X > 0 && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString)
                e.TextColor = _t.Muted;
            e.TextFormat |= TextFormatFlags.EndEllipsis;
            base.OnRenderItemText(e);
        }

        private void DrawToggle(Graphics g, Rectangle area, bool on, float cy)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float sw = S(26), sh = S(14);
            var sr = new RectangleF(area.Right - sw, cy - sh / 2, sw, sh);
            using (var path = Theme.RoundRect(sr, sh / 2))
            {
                if (on)
                    using (var b = new SolidBrush(_t.Accent)) g.FillPath(b, path);
                else
                    using (var p = new Pen(_t.Muted, S(1.2f))) g.DrawPath(p, path);
            }
            float kd = on ? S(9) : S(8);
            float kx = on ? sr.Right - S(2.5f) - kd : sr.X + S(3);
            using (var b = new SolidBrush(on ? _t.AccentText : _t.Muted))
                g.FillEllipse(b, kx, cy - kd / 2, kd, kd);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(_t.Separator))
                e.Graphics.DrawLine(p, S(12), y, e.Item.Width - S(12), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ArrowRectangle;
            // Pfeil nach innen gerückt (auf Höhe der Tastenkürzel statt am Rand)
            float cx = r.X + r.Width / 2f - S(8), cy = r.Y + r.Height / 2f, s = S(3.5f);
            using (var p = new Pen(e.Item.Enabled ? _t.Text : _t.Muted, S(1.3f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
            }
        }

        // Haken und Symbole zeichnet OnRenderMenuItemBackground selbst (einheitlich ausgerichtet)
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
    }

    /// <summary>Menüeintrag mit selbst gezeichnetem Symbol und Haken bzw. Auswahlpunkt.</summary>
    internal sealed class GlyphMenuItem : ToolStripMenuItem
    {
        public Image Glyph;
#pragma warning disable 0649 // in Dock Pilot nicht genutzt (Vorlage aus Monitor Dimmer)
        public bool Radio;
#pragma warning restore 0649
        public bool IsToggle;
        public bool ToggleOn;
        /// <summary>Feste Zeilenhöhe (0 = Windows entscheidet). Sorgt für gleiche Abstände in Haupt- und Untermenüs.</summary>
        public int FixedHeight;

        public override Size GetPreferredSize(Size constrainingSize)
        {
            var size = base.GetPreferredSize(constrainingSize);
            if (FixedHeight > 0) size.Height = FixedHeight;
            return size;
        }

        public GlyphMenuItem(string text) : base(text) { }
    }

    /// <summary>Kopfbereich: Programm-Icon, Name und farbiger Status.</summary>
    internal sealed class MenuHeader : ToolStripItem
    {
        private readonly MenuTheme _t;
        private readonly float _k;
        private readonly Image _icon;
        public string Title, Status, Version;
        public string Badge = null; // optionale Plakette (in Dock Pilot nicht genutzt)
        public Color StatusColor;

        public MenuHeader(MenuTheme theme, float k, Image icon)
        {
            _t = theme;
            _k = k;
            _icon = icon;
            AutoSize = false;
        }

        public override bool CanSelect { get { return false; } }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size((int)(300 * _k), (int)(58 * _k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float x = 14 * _k, iconSize = 32 * _k;
            if (_icon != null) g.DrawImage(_icon, x, (Height - iconSize) / 2f, iconSize, iconSize);
            float tx = x + iconSize + 12 * _k;
            using (var f = new Font("Segoe UI Semibold", 10.5f))
            using (var b = new SolidBrush(_t.Text))
            {
                g.DrawString(Title, f, b, tx, 9 * _k);
                if (!string.IsNullOrEmpty(Badge))
                {
                    float w = g.MeasureString(Title, f).Width;
                    using (var fb = new Font("Segoe UI Semibold", 7f))
                    {
                        var sz = g.MeasureString(Badge, fb);
                        var r = new RectangleF(tx + w + 2 * _k, 12 * _k, sz.Width + 4 * _k, sz.Height + 1 * _k);
                        using (var p = Theme.RoundRect(r, r.Height / 2))
                        using (var bb = new SolidBrush(Color.FromArgb(_t.Dark ? 60 : 40, _t.Accent)))
                            g.FillPath(bb, p);
                        using (var tb = new SolidBrush(_t.Dark ? Theme.Blend(_t.Accent, Color.White, 0.4f) : Theme.Blend(_t.Accent, Color.Black, 0.25f)))
                            g.DrawString(Badge, fb, tb, r.X + 2 * _k, r.Y + 0.5f * _k);
                    }
                }
            }
            if (!string.IsNullOrEmpty(Version))
            {
                using (var f = new Font("Segoe UI", 7.5f))
                using (var b = new SolidBrush(_t.Section))
                using (var sf = new StringFormat { Alignment = StringAlignment.Far })
                    g.DrawString(Version, f, b, new RectangleF(0, 11 * _k, Width - 14 * _k, 20 * _k), sf);
            }
            float dy = 33 * _k, d = 8 * _k;
            using (var b = new SolidBrush(StatusColor))
                g.FillEllipse(b, tx, dy + 4 * _k, d, d);
            using (var f = new Font("Segoe UI", 9f))
            using (var b = new SolidBrush(_t.Muted))
                g.DrawString(Status, f, b, tx + d + 6 * _k, dy - 1 * _k);
        }
    }

    /// <summary>Unsichtbare Leerzeile mit fester Höhe (Abstand, den Windows nicht verändert).</summary>
    internal sealed class MenuSpacer : ToolStripItem
    {
        private readonly int _height;

        public MenuSpacer(int height)
        {
            _height = height;
            AutoSize = false;
            Size = new Size(1, height);
        }

        public override bool CanSelect { get { return false; } }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size(1, _height);
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    /// <summary>Kleine graue Abschnittsüberschrift ("MODUS").</summary>
    internal sealed class MenuSection : ToolStripItem
    {
        private readonly MenuTheme _t;
        private readonly float _k;

        public MenuSection(string text, MenuTheme theme, float k)
        {
            Text = text;
            _t = theme;
            _k = k;
            AutoSize = false;
        }

        public override bool CanSelect { get { return false; } }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size((int)(100 * _k), (int)(24 * _k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // Normale Menüschrift, etwas kleiner und hellgrau: dezente Überschrift
            using (var f = new Font("Segoe UI", 8.5f))
            using (var b = new SolidBrush(_t.Section))
                e.Graphics.DrawString(Text, f, b, 0, 5 * _k); // bündig mit den Menütexten
        }
    }

    /// <summary>Hilfen für Symbole (Windows-Symbolschrift) und runde Ecken.</summary>
    internal static class MenuHelper
    {
        private static string _iconFont;

        /// <summary>Abstand Symbolspalte → Text in Pixeln bei 100 % (vor Skalierung).</summary>
        public const int TextGap = 8;

        // Zeichen aus "Segoe Fluent Icons" (Windows 11) bzw. "Segoe MDL2 Assets" (Windows 10)
        public const char Monitor = '\uE7F4';
        public const char Game = '\uE7FC';
        public const char Keyboard = '\uE765';
        public const char Settings = '\uE713';
        public const char Refresh = '\uE72C';
        public const char Add = '\uE710';
        public const char Document = '\uE8A5';
        public const char Folder = '\uE838';
        public const char Power = '\uE7E8';
        public const char Sync = '\uE895';
        public const char Log = '\uE81C';
        public const char Startup = '\uE7B5';
        public const char History = '\uE81C';
        public const char Info = '\uE946';
        public const char Download = '\uE896';
        public const char Auto = '\uE945';
        public const char Pause = '\uE769';
        public const char Shield = '\uEA18';
        public const char Completed = '\uE930';
        public const char Warning = '\uE7BA';
        public const char Link = '\uE71B';

        private static string IconFont()
        {
            if (_iconFont != null) return _iconFont;
            _iconFont = "";
            using (var fonts = new InstalledFontCollection())
            {
                foreach (var f in fonts.Families)
                {
                    if (f.Name == "Segoe Fluent Icons") { _iconFont = f.Name; break; }
                    if (f.Name == "Segoe MDL2 Assets") _iconFont = f.Name;
                }
            }
            return _iconFont;
        }

        public static Image Glyph(char c, Color color, int size)
        {
            return Glyph(c, color, size, size);
        }

        /// <summary>Symbol mittig in einem breiteren Bild (= fester Abstand links und rechts).</summary>
        public static Image Glyph(char c, Color color, int size, int width)
        {
            string font = IconFont();
            if (font.Length == 0) return null;

            // Symbol zuerst auf eine größere Fläche zeichnen ...
            int pad = size;
            var raw = new Bitmap(width + 2 * pad, size + 2 * pad);
            using (var g = Graphics.FromImage(raw))
            using (var f = new Font(font, size * 0.78f, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(c.ToString(), f, b, new RectangleF(0, 0, raw.Width, raw.Height), sf);
            }

            // ... dann die tatsächlich gezeichneten Pixel genau mittig setzen.
            // Die Zeichen der Symbolschrift sitzen nicht alle mittig in ihrem Kästchen
            // (z. B. Ein/Aus etwas höher) – so stehen alle Symbole optisch auf einer Linie.
            Rectangle ink = InkBounds(raw);
            var bmp = new Bitmap(width, size);
            if (ink.Width > 0 && ink.Height > 0)
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    float x = (width - ink.Width) / 2f;
                    float y = (size - ink.Height) / 2f;
                    g.DrawImage(raw, new RectangleF((float)Math.Round(x), (float)Math.Round(y), ink.Width, ink.Height),
                        new RectangleF(ink.X, ink.Y, ink.Width, ink.Height), GraphicsUnit.Pixel);
                }
            }
            raw.Dispose();
            return bmp;
        }

        /// <summary>Rechteck um alle sichtbaren (nicht durchsichtigen) Pixel.</summary>
        private static Rectangle InkBounds(Bitmap bmp)
        {
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (bmp.GetPixel(x, y).A > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        private static Bitmap _blank;

        /// <summary>Durchsichtiges Bild als Platzhalter für die Breite der Symbolspalte.</summary>
        public static Image Blank(int width, int height)
        {
            if (_blank == null || _blank.Width != width || _blank.Height != height) _blank = new Bitmap(width, height);
            return _blank;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Windows 11: runde Ecken und Schatten für ein Menüfenster.</summary>
        public static void RoundCorners(ToolStripDropDown dd)
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(dd.Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }
    }
}
