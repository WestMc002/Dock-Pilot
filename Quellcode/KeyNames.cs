// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DockPilot
{
    /// <summary>Anzeigenamen für Tasten und Vergleich von Tastencodes (Strg/Umschalt/Alt egal ob links oder rechts).</summary>
    internal static class KeyNames
    {
        public const int Escape = 0x1B;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

        public static string Get(int vk)
        {
            switch (vk)
            {
                case 0x20: return "Leertaste";
                case 0x11: return "Strg";
                case 0x10: return "Umschalt";
                case 0x12: return "Alt";
                case 0xA2: return "Strg links";
                case 0xA3: return "Strg rechts";
                case 0xA0: return "Umschalt links";
                case 0xA1: return "Umschalt rechts";
                case 0xA4: return "Alt links";
                case 0xA5: return "Alt Gr";
                case 0x5B: return "Windows links";
                case 0x5C: return "Windows rechts";
                case 0x5D: return "Menütaste";
                case 0x14: return "Feststelltaste";
                case 0x09: return "Tab";
                case 0x0D: return "Eingabe";
                case 0x08: return "Rücktaste";
                case 0x2E: return "Entf";
                case 0x2D: return "Einfg";
                case 0x24: return "Pos1";
                case 0x23: return "Ende";
                case 0x21: return "Bild auf";
                case 0x22: return "Bild ab";
                case 0x25: return "Pfeil links";
                case 0x26: return "Pfeil oben";
                case 0x27: return "Pfeil rechts";
                case 0x28: return "Pfeil unten";
                case 0x13: return "Pause";
                case 0x91: return "Rollen";
                case 0x2C: return "Druck";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            try
            {
                uint sc = MapVirtualKey((uint)vk, 0);
                if (sc != 0)
                {
                    StringBuilder sb = new StringBuilder(64);
                    if (GetKeyNameText((int)(sc << 16), sb, sb.Capacity) > 0)
                    {
                        string n = sb.ToString();
                        return n.Length == 1 ? n.ToUpperInvariant() : n;
                    }
                }
            }
            catch { }
            return "Taste " + vk;
        }

        /// <summary>Passt der gedrückte Tastencode (aus dem Tastatur-Hook) zur eingestellten Taste?</summary>
        public static bool Matches(int configured, uint vk)
        {
            if (configured == (int)vk) return true;
            switch (configured)
            {
                case 0x11: return vk == 0xA2 || vk == 0xA3;
                case 0x10: return vk == 0xA0 || vk == 0xA1;
                case 0x12: return vk == 0xA4 || vk == 0xA5;
            }
            return false;
        }
    }
}
