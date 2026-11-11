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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Erzeugt die Screenshots für die README aus Beispieldaten (Vorlage: WindowGrid / Monitor Dimmer).
    /// Aufruf: "Dock Pilot.exe" --screenshots [Zielordner]. Die echten Einstellungen (Dock Pilot.ini, Dock.ini),
    /// der Autostart und das laufende Dock werden nicht angefasst; nichts wird gespeichert.
    /// Bilder: hell.png / dunkel.png (Dock auf einem Beispiel-Desktop), menue-/ordnermenue-*.png (Menü über dem Dock)
    /// und &lt;name&gt;-hell.png / &lt;name&gt;-dunkel.png für die Fenster.
    /// Fenster und Menüs werden einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben
    /// sich durchsichtige runde Ecken und der Windows-Schatten. Das Dock selbst wird ohne Bildschirm gezeichnet.
    /// </summary>
    internal static class Demo
    {
        /// <summary>true, solange die Screenshots erstellt werden.</summary>
        public static bool Active;

        /// <summary>Programmordner, der im Info-Fenster angezeigt wird (statt des echten, privaten Pfads).</summary>
        public const string ProgramPath = @"C:\Tools\Dock Pilot";

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // Beispiel-Desktop für das Titelbild
        private const int DeskW = 1920, DeskH = 1080;
        private const float DeskScale = 1.25f;

        public static bool RunScreenshots(string outDir)
        {
            string demoFolder = Path.Combine(Path.GetTempPath(), "Dock Pilot Demo");
            try
            {
                Active = true;
                Directory.CreateDirectory(outDir);
                try { File.Delete(Path.Combine(outDir, "Fehler.txt")); } catch { }

                Settings settings = DemoSettings();
                Theme.Accent = Color.FromArgb(0, 120, 212); // Windows-Standard statt der eigenen Akzentfarbe
                Icon icon = TrayContext.LoadIcon(32);
                string tools = CreateDemoFolder(demoFolder);
                List<DockItem> items = DemoItems(tools);
                int editor = items.FindIndex(delegate (DockItem i) { return i.Title == "Editor"; });
                int folder = items.FindIndex(delegate (DockItem i) { return i.Kind == ItemKind.Ordnermenue; });

                // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm)
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);

                using (Backdrop backdrop = new Backdrop())
                {
                    // Hintergrundfläche einmal vorab anzeigen: das erste Anzeigen schließt sonst ein offenes Menü
                    backdrop.Cover(new Rectangle(area.Right - 2, area.Bottom - 2, 1, 1), Color.Black, IntPtr.Zero);
                    Pump(300);
                    backdrop.Hide();

                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Override = dark;
                        string suffix = dark ? "dunkel" : "hell";

                        // Titelbild: Dock auf dem Beispiel-Desktop, Maus über „Editor“
                        Save(DockShot(settings, items, dark, editor), Path.Combine(outDir, suffix + ".png"));

                        // Kontextmenü eines Eintrags – über dem Dock, wie bei der Benutzung
                        using (DockWindow dock = DemoDock(settings, items))
                        {
                            ContextMenuStrip menu = dock.BuildContextMenu(editor);
                            int margin;
                            Bitmap img = MenuShot(backdrop, menu, area, out margin);
                            Save(MenuScene(settings, items, dark, editor, img, margin), Path.Combine(outDir, "menue-" + suffix + ".png"));
                            MenuBuilder.DisposeDeep(menu.Items);
                            menu.Dispose();
                        }

                        // Ordnermenü (KkMenu-Ersatz) – über seinem Symbol im Dock
                        MenuBuilder mb = new MenuBuilder();
                        mb.FillFolder(mb.Menu.Items, tools, true, false, false);
                        mb.End();
                        {
                            int margin;
                            Bitmap img = MenuShot(backdrop, mb.Menu, area, out margin);
                            Save(MenuScene(settings, items, dark, folder, img, margin), Path.Combine(outDir, "ordnermenue-" + suffix + ".png"));
                        }
                        MenuBuilder.DisposeDeep(mb.Menu.Items);
                        mb.Menu.Dispose();

                        Shot(backdrop, delegate { return new SettingsForm(settings, icon); }, Path.Combine(outDir, "einstellungen-" + suffix + ".png"));
                        Shot(backdrop, delegate
                        {
                            ItemDialog d = new ItemDialog(items[editor].Clone());
                            d.TopMost = false;
                            return d;
                        }, Path.Combine(outDir, "eigenschaften-" + suffix + ".png"));
                        Shot(backdrop, delegate { return new AboutDialog(null); }, Path.Combine(outDir, "info-" + suffix + ".png"));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(outDir, "Fehler.txt"), ex.ToString()); } catch { }
                return false;
            }
            finally
            {
                Theme.Override = null;
                Active = false;
                try { if (Directory.Exists(demoFolder)) Directory.Delete(demoFolder, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ Beispieldaten

        private static Settings DemoSettings()
        {
            Settings s = new Settings(); // Standardwerte, wird nie gespeichert
            s.FirstRun = false;
            s.Edge = DockEdge.Unten;
            s.EdgeDistance = 8;
            s.IconSize = 48;
            s.ZoomSize = 80;
            s.ZoomWidth = 2;
            s.Labels = true;
            s.ShowRunning = true;
            s.AutoHide = true;
            s.HotkeyModifiers = ComboHotkeyBox.ModControl | ComboHotkeyBox.ModAlt;
            s.HotkeyKey = (int)Keys.D;
            return s;
        }

        private static string Sys(string file)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file);
        }

        /// <summary>Beispiel-Einträge mit Programmen, die in jedem Windows enthalten sind.</summary>
        private static List<DockItem> DemoItems(string tools)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            List<DockItem> list = new List<DockItem>();
            list.Add(new DockItem { Title = "Explorer", Command = Path.Combine(win, "explorer.exe") });
            if (File.Exists(edge)) list.Add(new DockItem { Title = "Microsoft Edge", Command = edge });
            list.Add(new DockItem { Title = "Editor", Command = Sys("notepad.exe") });
            list.Add(new DockItem { Title = "Eingabeaufforderung", Command = Sys("cmd.exe") });
            list.Add(new DockItem { Title = "PowerShell", Command = Sys(@"WindowsPowerShell\v1.0\powershell.exe") });
            list.Add(new DockItem { Kind = ItemKind.Trennlinie });
            list.Add(new DockItem { Kind = ItemKind.Ordnermenue, Title = "Werkzeuge", Folder = tools });
            list.Add(new DockItem { Title = "Dokumente", Command = "shell:Personal" });
            list.Add(new DockItem { Kind = ItemKind.Trennlinie });
            list.Add(new DockItem { Title = "Task-Manager", Command = Sys("taskmgr.exe") });
            list.Add(new DockItem { Title = "Systemsteuerung", Command = Sys("control.exe") });
            list.Add(new DockItem { Title = "Remotedesktopverbindung", Command = Sys("mstsc.exe") });
            list.Add(new DockItem { Title = "Papierkorb", Command = "::{645FF040-5081-101B-9F08-00AA002F954E}" });
            return list;
        }

        /// <summary>Beispielordner für das Ordnermenü: Verknüpfungen auf Windows-Werkzeuge (wird danach gelöscht).</summary>
        private static string CreateDemoFolder(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            string tools = Path.Combine(root, "Werkzeuge");
            string admin = Path.Combine(tools, "Verwaltung");
            Directory.CreateDirectory(admin);
            Link(tools, "Editor", Sys("notepad.exe"));
            Link(tools, "Eingabeaufforderung", Sys("cmd.exe"));
            Link(tools, "PowerShell", Sys(@"WindowsPowerShell\v1.0\powershell.exe"));
            Link(tools, "Rechner", Sys("calc.exe"));
            Link(tools, "Registrierungs-Editor", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"));
            Link(tools, "Remotedesktopverbindung", Sys("mstsc.exe"));
            Link(tools, "Task-Manager", Sys("taskmgr.exe"));
            Link(admin, "Ereignisanzeige", Sys("eventvwr.exe"));
            Link(admin, "Dienste", Sys("services.msc"));
            return tools;
        }

        private static void Link(string folder, string name, string target)
        {
            object shell = null, link = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(folder, name + ".lnk") });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        /// <summary>Dock ohne Fenster, nur für Menüs (Kontextmenü eines Eintrags).</summary>
        private static DockWindow DemoDock(Settings settings, List<DockItem> items)
        {
            return new DockWindow(settings, items);
        }

        // ------------------------------------------------------------------ Titelbild

        /// <summary>Dock am unteren Rand des Beispiel-Desktops, Ausschnitt um die Leiste.</summary>
        private static Bitmap DockShot(Settings settings, List<DockItem> items, bool dark, int hover)
        {
            return Scene(settings, items, dark, hover, DeskScale, null, 0);
        }

        /// <summary>Wie DockShot, dazu das aufgenommene Menü über dem Symbol – so wie es beim Anklicken erscheint.</summary>
        private static Bitmap MenuScene(Settings settings, List<DockItem> items, bool dark, int anchor, Bitmap menu, int margin)
        {
            using (menu)
            {
                // Desktop in der Skalierung des Bildschirms zeichnen, auf dem das Menü aufgenommen wurde
                float scale = Math.Max(1f, margin / (float)MenuMargin);
                return Scene(settings, items, dark, anchor, scale, menu, margin);
            }
        }

        private static Bitmap Scene(Settings settings, List<DockItem> items, bool dark, int hover, float scale, Bitmap menu, int margin)
        {
            // gleiche Fläche wie 1920 × 1080 bei 125 %, nur in der passenden Skalierung
            int deskW = (int)Math.Round(DeskW / DeskScale * scale), deskH = (int)Math.Round(DeskH / DeskScale * scale);
            Rectangle desk = new Rectangle(0, 0, deskW, deskH);
            using (Bitmap desktop = new Bitmap(deskW, deskH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(desktop))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.ScaleTransform(deskW / (float)DeskW, deskH / (float)DeskH);
                    DrawDesktop(g, dark);
                }
                Rectangle window, shelf, icon;
                int[] running = { 0, 1, 2 }; // Explorer, Edge bzw. Editor laufen
                Rectangle menuRect = Rectangle.Empty;
                using (Bitmap dock = DockWindow.RenderDemo(settings, items, desk, scale, dark, desktop, hover, running, menu != null,
                    out window, out shelf, out icon))
                {
                    using (Graphics g = Graphics.FromImage(desktop))
                    {
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.DrawImage(dock, window.Location);
                        if (menu != null)
                        {
                            // links bündig mit dem Symbol, knapp über der Leiste bzw. dem vergrößerten Symbol (wie DockWindow.ShowAt)
                            int gap = (int)Math.Round(6 * scale);
                            int bottom = window.Top + Math.Min(shelf.Top, icon.Top) - gap;
                            int menuW = menu.Width - 2 * margin, menuH = menu.Height - 2 * margin;
                            int left = Math.Max(0, Math.Min(window.Left + icon.Left, deskW - menuW));
                            menuRect = new Rectangle(left, bottom - menuH, menuW, menuH);
                            g.DrawImage(menu, menuRect.Left - margin, menuRect.Top - margin, menu.Width, menu.Height);
                        }
                    }
                }
                // Ausschnitt: Leiste (und Menü) mit Rand, unten bis zum Bildschirmrand
                int marginX = (int)(140 * scale), marginTop = (int)(60 * scale);
                int l = window.Left + shelf.Left - marginX, r = window.Left + shelf.Right + marginX, t = window.Top - marginTop;
                if (menu != null)
                {
                    l = Math.Min(l, menuRect.Left - marginX / 2);
                    r = Math.Max(r, menuRect.Right + marginX / 2);
                    t = Math.Min(t, menuRect.Top - marginTop);
                }
                Rectangle crop = Rectangle.FromLTRB(Math.Max(0, l), Math.Max(0, t), Math.Min(deskW, r), deskH);
                Bitmap result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(result))
                    g.DrawImage(desktop, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                return result;
            }
        }

        /// <summary>Neutraler Beispiel-Desktop: ruhiger Farbverlauf mit weichen Lichtflecken (wie WindowGrid).</summary>
        private static void DrawDesktop(Graphics g, bool dark)
        {
            Rectangle r = new Rectangle(0, 0, DeskW, DeskH);
            Color c1 = dark ? Color.FromArgb(18, 26, 48) : Color.FromArgb(170, 200, 236);
            Color c2 = dark ? Color.FromArgb(6, 10, 22) : Color.FromArgb(92, 140, 210);
            using (LinearGradientBrush b = new LinearGradientBrush(r, c1, c2, 35f)) g.FillRectangle(b, r);
            Glow(g, new RectangleF(-300, 380, 1300, 900), dark ? Color.FromArgb(120, 40, 90, 200) : Color.FromArgb(140, 255, 255, 255));
            Glow(g, new RectangleF(1050, -350, 1200, 900), dark ? Color.FromArgb(110, 30, 120, 210) : Color.FromArgb(120, 210, 235, 255));
        }

        private static void Glow(Graphics g, RectangleF r, Color center)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                using (PathGradientBrush b = new PathGradientBrush(p))
                {
                    b.CenterColor = center;
                    b.SurroundColors = new[] { Color.FromArgb(0, center) };
                    g.FillPath(b, p);
                }
            }
        }

        // ------------------------------------------------------------------ Fenster und Menü aufnehmen

        /// <summary>Nimmt das Menü (mit Schatten, durchsichtigem Rand) auf; margin: Rand um das Menü im Bild.</summary>
        private static Bitmap MenuShot(Backdrop backdrop, ContextMenuStrip menu, Rectangle area, out int margin)
        {
            Size size = menu.GetPreferredSize(Size.Empty);
            Point pos = new Point(area.Left + (area.Width - size.Width) / 2, area.Top + Math.Max(0, (area.Height - size.Height) / 2));
            menu.Show(pos);
            Pump(800);
            if (!menu.Visible) { menu.Show(pos); Pump(800); } // falls Windows es wieder geschlossen hat
            margin = Margin(menu, MenuMargin);
            Bitmap img = Capture(backdrop, menu.Handle, menu.Bounds, margin);
            menu.Close();
            Pump(200);
            return img;
        }

        private static void Shot(Backdrop backdrop, Func<Form> create, string file)
        {
            using (Form f = create())
            {
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.Show();
                f.TopMost = true;
                f.Activate();
                Pump(1500); // Aufbau und Zeichnen abwarten
                RECT r;
                Rectangle bounds = DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                    ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                    : f.Bounds;
                Save(Capture(backdrop, f.Handle, bounds, Margin(f)), file);
                f.Close();
            }
            Pump(300);
        }

        /// <summary>Rand um das Fenster für den Schatten (24 Einheiten).</summary>
        private static int Margin(Control c)
        {
            return Margin(c, 24);
        }

        /// <summary>Rand um die Aufnahme (Platz für den Schatten) in Pixeln bei 100 %, mit der Skalierung des Bildschirms.</summary>
        private static int Margin(Control c, int baseMargin)
        {
            try { using (Graphics g = c.CreateGraphics()) return (int)Math.Round(baseMargin * g.DpiX / 96f); }
            catch { return baseMargin; }
        }

        /// <summary>Menüs haben in Windows 11 einen deutlich größeren Schatten als Fenster.</summary>
        private const int MenuMargin = 56;

        /// <summary>Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit.</summary>
        private static Bitmap Capture(Backdrop backdrop, IntPtr target, Rectangle bounds, int margin)
        {
            Rectangle r = Rectangle.Inflate(bounds, margin, margin);
            backdrop.Cover(r, Color.Black, target);
            Pump(300);
            using (Bitmap onBlack = Grab(r))
            {
                backdrop.Cover(r, Color.White, target);
                Pump(300);
                using (Bitmap onWhite = Grab(r))
                {
                    backdrop.Hide();
                    return Combine(onBlack, onWhite, margin);
                }
            }
        }

        private static Bitmap Grab(Rectangle r)
        {
            Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        private static Bitmap Combine(Bitmap onBlack, Bitmap onWhite, int margin)
        {
            // Sicherheitsrand: Schatten zum Rand der Aufnahme hin auslaufen lassen, damit nie eine Kante sichtbar wird
            int feather = Math.Max(1, margin / 3);
            int w = onBlack.Width, h = onBlack.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData wd = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData od = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = bd.Stride * h;
                byte[] b = new byte[n];
                byte[] wh = new byte[n];
                byte[] o = new byte[n];
                Marshal.Copy(bd.Scan0, b, 0, n);
                Marshal.Copy(wd.Scan0, wh, 0, n);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    int diff = ((wh[i] - b[i]) + (wh[i + 1] - b[i + 1]) + (wh[i + 2] - b[i + 2])) / 3;
                    int a = Math.Max(0, Math.Min(255, 255 - diff));
                    if (a == 0) continue; // ganz durchsichtig
                    for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, b[i + c] * 255 / a);
                    int px = (i % bd.Stride) / 4, py = i / bd.Stride;
                    int edge = Math.Min(Math.Min(px, w - 1 - px), Math.Min(py, h - 1 - py));
                    if (edge < feather) a = a * edge / feather; // Farbe bleibt, nur die Deckkraft läuft aus
                    o[i + 3] = (byte)a;
                }
                Marshal.Copy(o, 0, od.Scan0, n);
            }
            finally
            {
                onBlack.UnlockBits(bd);
                onWhite.UnlockBits(wd);
                result.UnlockBits(od);
            }
            return result;
        }

        private static void Save(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }

        /// <summary>Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden).</summary>
        private static void Pump(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Einfarbige Fläche hinter dem aufgenommenen Bereich.</summary>
        private sealed class Backdrop : Form
        {
            public Backdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void Cover(Rectangle r, Color color, IntPtr target)
            {
                BackColor = color;
                Bounds = r;
                if (!Visible) Show();
                // vor alle anderen Fenster legen, danach das aufgenommene Fenster bzw. Menü wieder davor
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                if (target != IntPtr.Zero) SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                Refresh();
            }

            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}
