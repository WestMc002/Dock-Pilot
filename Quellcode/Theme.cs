// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Farben und Schriften passend zum hellen bzw. dunklen Windows-Design.</summary>
    internal sealed class Palette
    {
        public bool Dark;
        public Color Back;
        public Color Card;
        public Color CardBorder;
        public Color Text;
        public Color SubText;
        public Color Input;
        public Color InputBorder;
        public Color Hover;
        public Color ToggleOff;
    }

    internal static class Theme
    {
        private static string _fontFamily;
        public static Color Accent = Color.FromArgb(0, 120, 212);

        /// <summary>Fest vorgegebenes Farbschema (Screenshots); null = wie Windows.</summary>
        public static bool? Override;

        public static bool IsDark
        {
            get
            {
                if (Override.HasValue) return Override.Value;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        if (key != null)
                        {
                            object v = key.GetValue("AppsUseLightTheme");
                            if (v is int) return (int)v == 0;
                        }
                    }
                }
                catch { }
                return false;
            }
        }

        public static Color SystemAccent
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    {
                        if (key != null)
                        {
                            object v = key.GetValue("AccentColor");
                            if (v is int)
                            {
                                // Format ABGR
                                uint c = unchecked((uint)(int)v);
                                return Color.FromArgb(255, (int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
                            }
                            v = key.GetValue("ColorizationColor");
                            if (v is int)
                            {
                                uint c = unchecked((uint)(int)v);
                                return Color.FromArgb(255, (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));
                            }
                        }
                    }
                }
                catch { }
                return Color.FromArgb(0, 120, 212);
            }
        }

        public static Palette Current
        {
            get
            {
                Palette p = new Palette();
                p.Dark = IsDark;
                if (p.Dark)
                {
                    p.Back = Color.FromArgb(32, 32, 32);
                    p.Card = Color.FromArgb(43, 43, 43);
                    p.CardBorder = Color.FromArgb(58, 58, 58);
                    p.Text = Color.FromArgb(242, 242, 242);
                    p.SubText = Color.FromArgb(160, 160, 160);
                    p.Input = Color.FromArgb(55, 55, 55);
                    p.InputBorder = Color.FromArgb(75, 75, 75);
                    p.Hover = Color.FromArgb(66, 66, 66);
                    p.ToggleOff = Color.FromArgb(150, 150, 150);
                }
                else
                {
                    p.Back = Color.FromArgb(243, 243, 243);
                    p.Card = Color.FromArgb(251, 251, 251);
                    p.CardBorder = Color.FromArgb(229, 229, 229);
                    p.Text = Color.FromArgb(27, 27, 27);
                    p.SubText = Color.FromArgb(96, 96, 96);
                    p.Input = Color.FromArgb(255, 255, 255);
                    p.InputBorder = Color.FromArgb(212, 212, 212);
                    p.Hover = Color.FromArgb(234, 234, 234);
                    p.ToggleOff = Color.FromArgb(120, 120, 120);
                }
                return p;
            }
        }

        public static string FontFamilyName
        {
            get
            {
                if (_fontFamily == null)
                {
                    string[] wanted = { "Segoe UI Variable Text", "Segoe UI" };
                    _fontFamily = "Segoe UI";
                    try
                    {
                        string[] installed = FontFamily.Families.Select(f => f.Name).ToArray();
                        foreach (string w in wanted)
                        {
                            if (installed.Contains(w)) { _fontFamily = w; break; }
                        }
                    }
                    catch { }
                }
                return _fontFamily;
            }
        }

        private static string _iconFont;

        /// <summary>Symbolschrift: "Segoe Fluent Icons" (Windows 11), sonst "Segoe MDL2 Assets" (Windows 10).</summary>
        public static string IconFontName
        {
            get
            {
                if (_iconFont == null)
                {
                    _iconFont = "Segoe MDL2 Assets";
                    try
                    {
                        foreach (FontFamily f in FontFamily.Families)
                            if (f.Name == "Segoe Fluent Icons") { _iconFont = f.Name; break; }
                    }
                    catch { }
                }
                return _iconFont;
            }
        }

        /// <summary>Schrift in Pixelgröße (bereits DPI-skaliert übergeben).</summary>
        public static Font UiFont(float pixelSize, FontStyle style)
        {
            string family = FontFamilyName;
            if (style == FontStyle.Bold)
            {
                // Halbfett wirkt moderner als Fett
                string semi = family + " Semibold";
                try
                {
                    Font f = new Font(semi, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (f.Name == semi) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(family, pixelSize, style, GraphicsUnit.Pixel);
        }

        /// <summary>
        /// Icon sauber in ein Bitmap umwandeln. Icon.ToBitmap() liefert bei PNG-komprimierten
        /// Icon-Bildern im .NET Framework nur Pixelmüll – DrawIcon (Win32) zeichnet sie korrekt.
        /// </summary>
        public static Bitmap IconToBitmap(Icon icon, int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Icon sized = new Icon(icon, size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.DrawIcon(sized, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }

        public static Color WithAlpha(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Weiße oder schwarze Schrift, je nachdem was auf der Farbe besser lesbar ist.</summary>
        public static Color ContrastText(Color c)
        {
            double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return l > 0.62 ? Color.Black : Color.White;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
