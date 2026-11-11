// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>Programmdaten. AppInfo.Version erzeugt die Erstellen.bat in Quellcode\VersionInfo.cs.</summary>
    public static partial class AppInfo
    {
        public const string Name = "Dock Pilot";

        public static string ExeDirectory
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }
    }

    internal enum DockEdge { Oben = 0, Unten = 1, Links = 2, Rechts = 3 }

    /// <summary>
    /// Einstellungen, gespeichert als %LOCALAPPDATA%\Dock Pilot\Dock Pilot.ini.
    /// Größen in Pixeln bei 100 % Skalierung (werden mit der Bildschirmskalierung multipliziert).
    /// </summary>
    internal sealed class Settings
    {
        public int Monitor = -1;                  // -1 = Hauptbildschirm, sonst Nummer in Screen.AllScreens
        public DockEdge Edge = DockEdge.Unten;
        public int EdgeDistance = 0;              // Abstand zum Bildschirmrand
        public int CenterPercent = 0;             // Zentrierung: -100 = ganz links bzw. oben, 0 = Mitte, 100 = ganz rechts bzw. unten
        public int IconSize = 44;                 // Symbolgröße in Ruhe
        public int ZoomSize = 72;                 // Symbolgröße unter der Maus
        public int ZoomWidth = 2;                 // wie viele Nachbarn mitwachsen
        public int Spacing = 2;                   // Abstand zwischen den Symbolen
        public int LabelSize = 14;                // Beschriftung: Schriftgröße in Pixeln
        public bool LabelBold = true;
        public string LabelFont = "";             // Schriftart der Beschriftung, leer = Windows-Standard (Segoe UI Variable bzw. Segoe UI)
        public int LabelColorMode = 0;            // 0 = weiß, 1 = schwarz, 2 = wie Windows, 3 = Akzentfarbe, 4 = eigene Farbe
        public int LabelCustomColor = 0;          // ARGB, 0 = keine
        public int LabelShadow = 10;              // Schattenstärke (Deckkraft) 0 – 10 (× 10 %)
        public int LabelShadowSize = 3;           // Schattengröße: wie weit der Schatten um die Schrift reicht (1 – 8 px)
        public int ZoomMode = 0;                  // 0 = automatisch, 1 = Blase, 2 = fest, 3 = anheben, 4 = aus, 5 = weiche Blase, 6 = leuchten, 7 = Fokus, 8 = federn
        public bool Reflection = false;           // Spiegelung unter den Symbolen (wie RocketDock)
        public bool KeepWidth = true;             // Blase: Leiste bleibt gleich breit (die übrigen Symbole rücken zusammen)
        public int KeepMode = 0;                  // Platz schaffen: 0 = übrige Symbole schrumpfen, 1 = überlappen (bleiben gleich groß)
        public int MinShrink = 50;                // übrige Symbole mindestens so groß bzw. so weit sichtbar (50 – 100 %)
        public int Reserve = 0;                   // zusätzliche Länge der Leiste an beiden Enden (px), Platz für die Blase
        public bool EdgeGrow = true;              // am ersten/letzten Symbol darf die Leiste über ihr Ende hinauswachsen
        public bool ShelfVisible = true;          // Leiste (Hintergrund) anzeigen; aus = nur Symbole
        public int ShelfOpacity = 100;            // Deckkraft der Leiste (10 – 100 %)
        public int ShelfRadius = 8;               // Eckenradius der Leiste (0 – 40 px), Windows 11: 8 px wie Fenster und Menüs
        public bool Labels = true;                // Beschriftung beim Überfahren
        public bool ShowRunning = true;           // Punkt unter laufenden Programmen
        public bool Preview = true;               // Fenstervorschau über laufenden Programmen (wie in der Taskleiste)
        public bool ActivateRunning = true;       // läuft das Programm schon: nur nach vorne holen
        public bool AutoHide = false;
        public int ShowDelay = 350;               // ms am Rand, bis das Dock erscheint
        public int HideDelay = 250;               // ms nach dem Verlassen, bis es verschwindet
        public bool HideOnFullscreen = true;      // bei Vollbild-Programmen (Spiele, Videos) nicht einblenden
        public bool Locked = false;               // Einträge sperren (kein Verschieben per Maus)
        public bool Animate = true;
        public int AnimSpeed = 1;                 // 0 = schnell, 1 = normal, 2 = langsam (Zoom, Ein-/Ausblenden, Hüpfen)
        public bool AlwaysOnTop = true;           // Bildschirmebene: immer im Vordergrund
        public bool RaiseOnHover = true;          // sonst: Maus über dem Dock holt es nach vorne (nach "Einblenden nach")
        public int HotkeyModifiers = 0;           // MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8
        public int HotkeyKey = 0;                 // 0 = kein Tastenkürzel
        public bool FirstRun = true;
        public bool UpdateCheck = true;           // beim Start auf GitHub nach neuen Versionen sehen (höchstens alle 20 Stunden)
        public string LastUpdateCheck = "";       // Zeitpunkt der letzten erfolgreichen Prüfung (UTC, ISO 8601)
        public bool NavPinned = true;             // Einstellungsfenster: Navigation angepinnt (wird sofort gespeichert)

        public Settings Clone()
        {
            Settings s = new Settings();
            s.CopyFrom(this);
            return s;
        }

        public void CopyFrom(Settings o)
        {
            Monitor = o.Monitor; Edge = o.Edge; EdgeDistance = o.EdgeDistance; CenterPercent = o.CenterPercent;
            IconSize = o.IconSize; ZoomSize = o.ZoomSize; ZoomWidth = o.ZoomWidth; ZoomMode = o.ZoomMode; Spacing = o.Spacing; Reflection = o.Reflection; KeepWidth = o.KeepWidth;
            KeepMode = o.KeepMode; MinShrink = o.MinShrink; Reserve = o.Reserve; EdgeGrow = o.EdgeGrow;
            ShelfVisible = o.ShelfVisible; ShelfOpacity = o.ShelfOpacity; ShelfRadius = o.ShelfRadius;
            LabelSize = o.LabelSize; LabelBold = o.LabelBold; LabelFont = o.LabelFont; LabelColorMode = o.LabelColorMode; LabelCustomColor = o.LabelCustomColor; LabelShadow = o.LabelShadow; LabelShadowSize = o.LabelShadowSize;
            Labels = o.Labels; ShowRunning = o.ShowRunning; ActivateRunning = o.ActivateRunning; Preview = o.Preview;
            AutoHide = o.AutoHide; ShowDelay = o.ShowDelay; HideDelay = o.HideDelay;
            HideOnFullscreen = o.HideOnFullscreen; Locked = o.Locked; Animate = o.Animate;
            AnimSpeed = o.AnimSpeed; AlwaysOnTop = o.AlwaysOnTop; RaiseOnHover = o.RaiseOnHover;
            HotkeyModifiers = o.HotkeyModifiers; HotkeyKey = o.HotkeyKey;
            FirstRun = o.FirstRun; UpdateCheck = o.UpdateCheck; LastUpdateCheck = o.LastUpdateCheck; NavPinned = o.NavPinned;
        }

        /// <summary>Schriftfarbe der Beschriftung nach Einstellung.</summary>
        public static Color LabelColorFor(int mode, int custom, bool dark, Color accent)
        {
            switch (mode)
            {
                case 1: return Color.Black;
                case 2: return dark ? Color.White : Color.FromArgb(27, 27, 27);
                case 3: return accent;
                case 4: return custom == 0 ? Color.White : Color.FromArgb(255, Color.FromArgb(custom));
                default: return Color.White;
            }
        }

        public bool Vertical
        {
            get { return Edge == DockEdge.Links || Edge == DockEdge.Rechts; }
        }

        /// <summary>Ordner der Laufzeitdaten: %LOCALAPPDATA%\Dock Pilot.</summary>
        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name); }
        }

        public static string DataPath
        {
            get { return Path.Combine(DataDirectory, "Dock Pilot.ini"); }
        }

        /// <summary>Kopien importierter Symbole (PNG/ICO), damit RocketDock später entfernt werden kann.</summary>
        public static string IconDirectory
        {
            get { return Path.Combine(DataDirectory, "Symbole"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            Dictionary<string, string> v = Ini.ReadFlat(DataPath);
            if (v == null) return s;
            s.Monitor = Ini.GetInt(v, "Bildschirm", s.Monitor, -1, 16);
            s.Edge = (DockEdge)Ini.GetInt(v, "Rand", (int)s.Edge, 0, 3);
            s.EdgeDistance = Ini.GetInt(v, "AbstandRand", s.EdgeDistance, 0, 400);
            s.CenterPercent = Ini.GetInt(v, "Zentrierung", s.CenterPercent, -100, 100);
            s.IconSize = Ini.GetInt(v, "Symbolgroesse", s.IconSize, 16, 128);
            s.ZoomSize = Ini.GetInt(v, "Zoomgroesse", s.ZoomSize, 16, 256);
            s.ZoomWidth = Ini.GetInt(v, "Zoombreite", s.ZoomWidth, 1, 6);
            s.ZoomMode = Ini.GetInt(v, "ZoomArt", -1, -1, 8);
            if (s.ZoomMode < 0)
            {
                // ältere Versionen: ZoomEffekt mit 3 = Hervorheben (entfallen), 4 = Anheben, 5 = Aus
                int old = Ini.GetInt(v, "ZoomEffekt", 0, 0, 5);
                s.ZoomMode = old == 3 ? 0 : old > 3 ? old - 1 : old;
            }
            s.Spacing = Ini.GetInt(v, "Symbolabstand", s.Spacing, 0, 30);
            s.LabelSize = Ini.GetInt(v, "SchriftGroesse", s.LabelSize, 9, 32);
            s.LabelBold = Ini.GetBool(v, "SchriftFett", s.LabelBold);
            string font;
            if (v.TryGetValue("Schriftart", out font)) s.LabelFont = (font ?? "").Trim();
            s.LabelColorMode = Ini.GetInt(v, "SchriftFarbe", s.LabelColorMode, 0, 4);
            string lc;
            if (v.TryGetValue("SchriftFarbeEigene", out lc))
            {
                int argb;
                if (int.TryParse(lc.Trim().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb)) s.LabelCustomColor = argb;
            }
            s.LabelShadow = Ini.GetInt(v, "SchriftSchatten", s.LabelShadow, 0, 10);
            s.LabelShadowSize = Ini.GetInt(v, "SchriftSchattenGroesse", s.LabelShadowSize, 1, 8);
            s.Labels = Ini.GetBool(v, "Beschriftung", s.Labels);
            s.ShowRunning = Ini.GetBool(v, "LaufendeMarkieren", s.ShowRunning);
            s.Reflection = Ini.GetBool(v, "Spiegelung", s.Reflection);
            s.Preview = Ini.GetBool(v, "Fenstervorschau", s.Preview);
            s.KeepWidth = Ini.GetBool(v, "LeisteGleichBreit", s.KeepWidth);
            s.KeepMode = Ini.GetInt(v, "PlatzSchaffen", s.KeepMode, 0, 1);
            s.MinShrink = Ini.GetInt(v, "KleinsteGroesse", s.MinShrink, 50, 100);
            s.Reserve = Ini.GetInt(v, "Reserve", s.Reserve, 0, 200);
            s.EdgeGrow = Ini.GetBool(v, "AmRandHinauswachsen", s.EdgeGrow);
            s.ShelfVisible = Ini.GetBool(v, "LeisteAnzeigen", s.ShelfVisible);
            s.ShelfOpacity = Ini.GetInt(v, "LeisteDeckkraft", s.ShelfOpacity, 10, 100);
            s.ShelfRadius = Ini.GetInt(v, "LeisteEckenradius", s.ShelfRadius, 0, 40);
            s.ActivateRunning = Ini.GetBool(v, "LaufendeNachVorne", s.ActivateRunning);
            s.AutoHide = Ini.GetBool(v, "AutomatischAusblenden", s.AutoHide);
            s.ShowDelay = Ini.GetInt(v, "EinblendenNach", s.ShowDelay, 0, 5000);
            s.HideDelay = Ini.GetInt(v, "AusblendenNach", s.HideDelay, 0, 5000);
            s.HideOnFullscreen = Ini.GetBool(v, "VollbildAusblenden", s.HideOnFullscreen);
            s.Locked = Ini.GetBool(v, "Gesperrt", s.Locked);
            s.Animate = Ini.GetBool(v, "Animation", s.Animate);
            s.AnimSpeed = Ini.GetInt(v, "AnimationTempo", s.AnimSpeed, 0, 2);
            s.AlwaysOnTop = Ini.GetBool(v, "ImmerImVordergrund", s.AlwaysOnTop);
            s.RaiseOnHover = Ini.GetBool(v, "MausHoltNachVorne", s.RaiseOnHover);
            s.HotkeyModifiers = Ini.GetInt(v, "TastenkuerzelZusatz", s.HotkeyModifiers, 0, 15);
            s.HotkeyKey = Ini.GetInt(v, "TastenkuerzelTaste", s.HotkeyKey, 0, 254);
            s.FirstRun = Ini.GetBool(v, "ErsterStart", s.FirstRun);
            s.UpdateCheck = Ini.GetBool(v, "UpdatesPruefen", s.UpdateCheck);
            s.NavPinned = Ini.GetBool(v, "NavigationAngepinnt", s.NavPinned);
            string last;
            if (v.TryGetValue("LetzteUpdatePruefung", out last)) s.LastUpdateCheck = last.Trim();
            if (s.ZoomSize < s.IconSize) s.ZoomSize = s.IconSize;
            return s;
        }

        public void Save()
        {
            Ini.Write(DataPath, ToIniText());
        }

        /// <summary>Inhalt der Einstellungsdatei (auch zum Vergleichen, ob sich etwas geändert hat).</summary>
        public string ToIniText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; Dock Pilot " + AppInfo.Version + " - Einstellungen (Groessen in Pixel bei 100 % Skalierung)");
            sb.AppendLine("[Position]");
            sb.AppendLine("; -1 = Hauptbildschirm, sonst Nummer ab 0");
            sb.AppendLine("Bildschirm=" + Monitor);
            sb.AppendLine("; 0 = oben, 1 = unten, 2 = links, 3 = rechts");
            sb.AppendLine("Rand=" + (int)Edge);
            sb.AppendLine("AbstandRand=" + EdgeDistance);
            sb.AppendLine("; Zentrierung in Prozent: -100 = ganz links bzw. oben, 0 = Mitte, 100 = ganz rechts bzw. unten");
            sb.AppendLine("Zentrierung=" + CenterPercent);
            sb.AppendLine("; 1 = immer im Vordergrund; sonst holt die Maus das Dock nach vorne, wenn MausHoltNachVorne = 1");
            sb.AppendLine("ImmerImVordergrund=" + B(AlwaysOnTop));
            sb.AppendLine("MausHoltNachVorne=" + B(RaiseOnHover));
            sb.AppendLine("[Symbole]");
            sb.AppendLine("Symbolgroesse=" + IconSize);
            sb.AppendLine("Zoomgroesse=" + ZoomSize);
            sb.AppendLine("Zoombreite=" + ZoomWidth);
            sb.AppendLine("; Zoom-Effekt: 0 = automatisch, 1 = Blase, 2 = fest, 3 = hervorheben wie die Taskleiste, 4 = anheben, 5 = aus");
            sb.AppendLine("; 0 automatisch, 1 Blase, 2 fest, 3 anheben, 4 aus, 5 weiche Blase, 6 leuchten, 7 Fokus, 8 federn");
            sb.AppendLine("ZoomArt=" + ZoomMode);
            sb.AppendLine("Symbolabstand=" + Spacing);
            sb.AppendLine("[Beschriftung]");
            sb.AppendLine("SchriftGroesse=" + LabelSize);
            sb.AppendLine("SchriftFett=" + B(LabelBold));
            sb.AppendLine("; leer = Windows-Standard");
            sb.AppendLine("Schriftart=" + LabelFont);
            sb.AppendLine("; 0 = weiss, 1 = schwarz, 2 = wie Windows, 3 = Akzentfarbe, 4 = eigene Farbe (SchriftFarbeEigene als ARGB hex)");
            sb.AppendLine("SchriftFarbe=" + LabelColorMode);
            sb.AppendLine("SchriftFarbeEigene=" + (LabelCustomColor == 0 ? "" : LabelCustomColor.ToString("X8")));
            sb.AppendLine("; Schatten 0 - 10 (mal 10 %)");
            sb.AppendLine("SchriftSchatten=" + LabelShadow);
            sb.AppendLine("SchriftSchattenGroesse=" + LabelShadowSize);
            sb.AppendLine("Beschriftung=" + B(Labels));
            sb.AppendLine("LaufendeMarkieren=" + B(ShowRunning));
            sb.AppendLine("Spiegelung=" + B(Reflection));
            sb.AppendLine("Fenstervorschau=" + B(Preview));
            sb.AppendLine("LeisteGleichBreit=" + B(KeepWidth));
            sb.AppendLine("; 0 schrumpfen, 1 überlappen");
            sb.AppendLine("PlatzSchaffen=" + KeepMode);
            sb.AppendLine("KleinsteGroesse=" + MinShrink);
            sb.AppendLine("Reserve=" + Reserve);
            sb.AppendLine("AmRandHinauswachsen=" + B(EdgeGrow));
            sb.AppendLine("LeisteAnzeigen=" + B(ShelfVisible));
            sb.AppendLine("LeisteDeckkraft=" + ShelfOpacity);
            sb.AppendLine("LeisteEckenradius=" + ShelfRadius);
            sb.AppendLine("[Verhalten]");
            sb.AppendLine("LaufendeNachVorne=" + B(ActivateRunning));
            sb.AppendLine("AutomatischAusblenden=" + B(AutoHide));
            sb.AppendLine("EinblendenNach=" + ShowDelay);
            sb.AppendLine("AusblendenNach=" + HideDelay);
            sb.AppendLine("VollbildAusblenden=" + B(HideOnFullscreen));
            sb.AppendLine("Gesperrt=" + B(Locked));
            sb.AppendLine("Animation=" + B(Animate));
            sb.AppendLine("; 0 = schnell, 1 = normal, 2 = langsam");
            sb.AppendLine("AnimationTempo=" + AnimSpeed);
            sb.AppendLine("; Zusatztasten: 1 = Alt, 2 = Strg, 4 = Umschalt, 8 = Windows (addiert); Taste als virtueller Tastencode, 0 = aus");
            sb.AppendLine("TastenkuerzelZusatz=" + HotkeyModifiers);
            sb.AppendLine("TastenkuerzelTaste=" + HotkeyKey);
            sb.AppendLine("[Allgemein]");
            sb.AppendLine("ErsterStart=" + B(FirstRun));
            sb.AppendLine("; Beim Start auf GitHub nach einer neuen Version sehen, hoechstens alle 20 Stunden (1 = ja)");
            sb.AppendLine("UpdatesPruefen=" + B(UpdateCheck));
            sb.AppendLine("LetzteUpdatePruefung=" + LastUpdateCheck);
            sb.AppendLine("NavigationAngepinnt=" + B(NavPinned));
            return sb.ToString();
        }

        private static string B(bool b) { return b ? "1" : "0"; }
    }

    internal enum ItemKind { Programm, Trennlinie, Ordnermenue }

    /// <summary>Ein Eintrag im Dock: Programm/Datei/Ordner/Webseite, Trennlinie oder Ordnermenü.</summary>
    internal sealed class DockItem
    {
        public ItemKind Kind = ItemKind.Programm;
        public string Title = "";
        public string Command = "";       // Programm, Datei, Ordner, Netzwerkpfad, Webadresse oder Shell-Pfad (::{…})
        public string Arguments = "";
        public string WorkingDirectory = "";
        public string Icon = "";          // leer = Symbol des Ziels; sonst "Datei" oder "Datei?Index" (Index < 0 = Ressourcen-Nummer)
        public int ShowCmd = 0;           // 0 = normal, 1 = minimiert, 2 = maximiert
        public bool RunAsAdmin = false;
        public string Folder = "";        // nur Ordnermenü
        public bool FoldersFirst = true;  // nur Ordnermenü: Unterordner zuerst
        public bool FilesOnly = false;    // nur Ordnermenü: keine Unterordner anzeigen
        public int Sort = 0;              // nur Ordnermenü: 0 Name A–Z, 1 Name Z–A, 2 neueste zuerst, 3 älteste zuerst, 4 eigene Reihenfolge
        public string Order = "";         // nur Ordnermenü, eigene Reihenfolge: Pfade relativ zum Ordner, getrennt mit „|“

        public DockItem Clone()
        {
            return (DockItem)MemberwiseClone();
        }

        public bool IsSeparator { get { return Kind == ItemKind.Trennlinie; } }
    }

    /// <summary>Die Einträge des Docks, gespeichert als %LOCALAPPDATA%\Dock Pilot\Dock.ini (ein Abschnitt je Eintrag).</summary>
    internal static class DockStore
    {
        public static string FilePath
        {
            get { return Path.Combine(Settings.DataDirectory, "Dock.ini"); }
        }

        public static bool Exists
        {
            get { return File.Exists(FilePath); }
        }

        public static List<DockItem> Load()
        {
            List<DockItem> list = new List<DockItem>();
            List<KeyValuePair<string, Dictionary<string, string>>> sections = Ini.ReadSections(FilePath);
            if (sections == null) return list;
            foreach (KeyValuePair<string, Dictionary<string, string>> sec in sections)
            {
                if (!sec.Key.StartsWith("Eintrag", StringComparison.OrdinalIgnoreCase)) continue;
                Dictionary<string, string> v = sec.Value;
                DockItem it = new DockItem();
                string art = Ini.Get(v, "Art", "Programm").ToLowerInvariant();
                it.Kind = art.StartsWith("trenn") ? ItemKind.Trennlinie : art.StartsWith("ordner") ? ItemKind.Ordnermenue : ItemKind.Programm;
                it.Title = Ini.Get(v, "Name", "");
                it.Command = Ini.Get(v, "Ziel", "");
                it.Arguments = Ini.Get(v, "Parameter", "");
                it.WorkingDirectory = Ini.Get(v, "AusfuehrenIn", "");
                it.Icon = Ini.Get(v, "Symbol", "");
                it.ShowCmd = Ini.GetInt(v, "Fenster", 0, 0, 2);
                it.RunAsAdmin = Ini.GetBool(v, "AlsAdministrator", false);
                it.Folder = Ini.Get(v, "Ordner", "");
                it.FoldersFirst = Ini.GetBool(v, "OrdnerZuerst", true);
                it.FilesOnly = Ini.GetBool(v, "NurDateien", false);
                it.Sort = Ini.GetInt(v, "Sortierung", 0, 0, 4);
                it.Order = Ini.Get(v, "Reihenfolge", "");
                list.Add(it);
            }
            return list;
        }

        public static void Save(List<DockItem> items)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; Dock Pilot - Eintraege im Dock (Reihenfolge = Reihenfolge der Abschnitte)");
            sb.AppendLine("; Art: Programm, Trennlinie oder Ordnermenue. Symbol: leer = Symbol des Ziels, sonst Datei oder Datei?Index");
            for (int i = 0; i < items.Count; i++)
            {
                DockItem it = items[i];
                sb.AppendLine();
                sb.AppendLine("[Eintrag" + (i + 1).ToString("000") + "]");
                if (it.Kind == ItemKind.Trennlinie)
                {
                    sb.AppendLine("Art=Trennlinie");
                    continue;
                }
                sb.AppendLine("Art=" + (it.Kind == ItemKind.Ordnermenue ? "Ordnermenue" : "Programm"));
                sb.AppendLine("Name=" + Ini.Clean(it.Title));
                if (it.Kind == ItemKind.Ordnermenue)
                {
                    sb.AppendLine("Ordner=" + Ini.Clean(it.Folder));
                    sb.AppendLine("OrdnerZuerst=" + (it.FoldersFirst ? "1" : "0"));
                    sb.AppendLine("NurDateien=" + (it.FilesOnly ? "1" : "0"));
                    sb.AppendLine("Sortierung=" + it.Sort);
                    if (it.Order.Length > 0) sb.AppendLine("Reihenfolge=" + Ini.Clean(it.Order));
                }
                else
                {
                    sb.AppendLine("Ziel=" + Ini.Clean(it.Command));
                    sb.AppendLine("Parameter=" + Ini.Clean(it.Arguments));
                    sb.AppendLine("AusfuehrenIn=" + Ini.Clean(it.WorkingDirectory));
                    sb.AppendLine("Fenster=" + it.ShowCmd);
                    sb.AppendLine("AlsAdministrator=" + (it.RunAsAdmin ? "1" : "0"));
                }
                sb.AppendLine("Symbol=" + Ini.Clean(it.Icon));
            }
            Ini.Write(FilePath, sb.ToString());
        }
    }

    /// <summary>Einfache INI-Dateien (UTF-8).</summary>
    internal static class Ini
    {
        public static Dictionary<string, string> ReadFlat(string path)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return values;
        }

        public static List<KeyValuePair<string, Dictionary<string, string>>> ReadSections(string path)
        {
            return ReadSections(path, Encoding.UTF8);
        }

        public static List<KeyValuePair<string, Dictionary<string, string>>> ReadSections(string path, Encoding enc)
        {
            if (!File.Exists(path)) return null;
            List<KeyValuePair<string, Dictionary<string, string>>> list = new List<KeyValuePair<string, Dictionary<string, string>>>();
            Dictionary<string, string> current = null;
            try
            {
                foreach (string raw in File.ReadAllLines(path, enc))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        list.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2), current));
                        continue;
                    }
                    if (current == null) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return list;
        }

        public static void Write(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // erst in eine Hilfsdatei, dann ersetzen – so geht bei einem Absturz nichts verloren
                string tmp = path + ".neu";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }
            }
        }

        public static string Clean(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public static string Get(Dictionary<string, string> v, string key, string def)
        {
            string s;
            return v.TryGetValue(key, out s) ? s : def;
        }

        public static int GetInt(Dictionary<string, string> v, string key, int def, int min, int max)
        {
            string s;
            int i;
            if (v.TryGetValue(key, out s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return Math.Max(min, Math.Min(max, i));
            return def;
        }

        public static bool GetBool(Dictionary<string, string> v, string key, bool def)
        {
            string s;
            if (!v.TryGetValue(key, out s)) return def;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "ja";
        }
    }
}
