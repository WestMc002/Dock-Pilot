// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace DockPilot
{
    /// <summary>Lädt die Symbole der Einträge (EXE/DLL mit Index, ICO, PNG oder das Symbol des Ziels).</summary>
    internal static class IconLoader
    {
        /// <summary>Symbol eines Eintrags in möglichst großer Auflösung (bis <paramref name="size"/> Pixel).</summary>
        public static Bitmap Load(DockItem item, int size)
        {
            Bitmap b = null;
            try
            {
                if (!string.IsNullOrEmpty(item.Icon)) b = FromSpec(item.Icon, size);
                if (b == null) b = FromPack(item, size);
                if (b == null)
                {
                    string target = Expand(item.Kind == ItemKind.Ordnermenue ? item.Folder : item.Command);
                    if (!string.IsNullOrEmpty(target)) b = ForTarget(target, size);
                }
            }
            catch { b = null; }
            return b ?? Placeholder(size);
        }

        /// <summary>
        /// Eigene Symbole: Liegt im Ordner „Symbole“ (Datenordner) ein Bild, das wie der Eintrag oder wie sein Programm
        /// heißt (z. B. „Editor.png“ oder „notepad.ico“), wird es statt des Programmsymbols verwendet.
        /// Ein in den Eigenschaften gewähltes Symbol hat Vorrang.
        /// </summary>
        private static Bitmap FromPack(DockItem item, int size)
        {
            try
            {
                string dir = Settings.IconDirectory;
                if (item.IsSeparator || !Directory.Exists(dir)) return null;
                List<string> names = new List<string>();
                names.Add(item.Title);
                string target = Expand(item.Kind == ItemKind.Ordnermenue ? item.Folder : item.Command).TrimEnd('\\');
                try { names.Add(Path.GetFileNameWithoutExtension(target)); } catch { }
                foreach (string name in names)
                {
                    if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    foreach (string ext in new[] { ".png", ".ico" })
                    {
                        string file = Path.Combine(dir, name + ext);
                        if (!File.Exists(file)) continue;
                        Bitmap b = FromSpec(file, size);
                        if (b != null) return b;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Kleines Symbol für Menüeinträge (Datei, Verknüpfung oder Ordner).</summary>
        public static Bitmap Small(string path, int size)
        {
            Bitmap b = null;
            try { b = DockNative.ShellIcon(path, false, size); } catch { }
            if (b == null) return Placeholder(size);
            Bitmap r = Fit(b, size);
            b.Dispose();
            return r;
        }

        public static string Expand(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
        }

        /// <summary>Teilt "Datei?Index" auf. Unsinnige Nummern aus RocketDock (z. B. "KeePass.exe?75367680") werden zu 0.</summary>
        public static string SplitSpec(string spec, out int index)
        {
            index = 0;
            string file = spec;
            int q = spec.LastIndexOf('?');
            if (q > 0)
            {
                int i;
                if (int.TryParse(spec.Substring(q + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i > -100000 && i < 100000)
                    index = i;
                file = spec.Substring(0, q);
            }
            file = Expand(file);
            if (file.Length > 0 && !Path.IsPathRooted(file)) file = Path.Combine(Settings.IconDirectory, file);
            return file;
        }

        private static Bitmap FromSpec(string spec, int size)
        {
            int index;
            string file = SplitSpec(spec, out index);
            if (!File.Exists(file)) return null;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff")
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs))
                    return Fit(img, Math.Max(size, Math.Min(256, Math.Max(img.Width, img.Height))));
            }
            if (ext == ".ico" || ext == ".exe" || ext == ".dll" || ext == ".icl" || ext == ".cpl" || ext == ".mun" || ext == ".scr" || ext == ".ocx")
            {
                Bitmap b = DockNative.ExtractIcon(file, index, size);
                if (b == null && index != 0) b = DockNative.ExtractIcon(file, 0, size);
                if (b != null) return b;
            }
            return DockNative.ShellIcon(file, true);
        }

        private static Bitmap ForTarget(string target, int size)
        {
            string ext = "";
            try { if (target.IndexOfAny(Path.GetInvalidPathChars()) < 0) ext = Path.GetExtension(target).ToLowerInvariant(); } catch { }
            if ((ext == ".exe" || ext == ".ico") && File.Exists(target))
            {
                Bitmap b = DockNative.ExtractIcon(target, 0, size);
                if (b != null) return b;
            }
            // "shell:::{CLSID}" versteht SHParseDisplayName als "::{CLSID}"; "shell:Downloads" usw. direkt
            if (target.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)) target = target.Substring(6);
            return DockNative.ShellIcon(target, true);
        }

        /// <summary>Bild in ein quadratisches Bitmap einpassen (Seitenverhältnis bleibt, hohe Qualität).</summary>
        public static Bitmap Fit(Image src, int size)
        {
            size = Math.Max(1, size);
            Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float k = Math.Min(size / (float)src.Width, size / (float)src.Height);
                float w = src.Width * k, h = src.Height * k;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY); // keine hellen Ränder beim Verkleinern
                    g.DrawImage(src, new Rectangle((int)Math.Round((size - w) / 2), (int)Math.Round((size - h) / 2), (int)Math.Round(w), (int)Math.Round(h)),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        /// <summary>Ersatzsymbol, wenn das Ziel kein Symbol liefert: abgerundetes Feld mit Fragezeichen.</summary>
        public static Bitmap Placeholder(int size)
        {
            size = Math.Max(16, size);
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                RectangleF r = new RectangleF(size * 0.08f, size * 0.08f, size * 0.84f, size * 0.84f);
                using (GraphicsPath p = Theme.RoundRect(r, size * 0.18f))
                using (LinearGradientBrush br = new LinearGradientBrush(r, Color.FromArgb(120, 128, 140), Color.FromArgb(70, 76, 88), 90f))
                    g.FillPath(br, p);
                using (Font f = new Font("Segoe UI Semibold", size * 0.45f, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("?", f, Brushes.White, r, sf);
            }
            return b;
        }
    }
}
