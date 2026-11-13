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
    /// <summary>
    /// Tastatursteuerung, Mausrad, blinkende Fenster, laufende Programme ohne Eintrag und das sanfte Gleiten der
    /// Symbole. Teil von DockWindow (siehe DockWindow.cs).
    /// </summary>
    internal sealed partial class DockWindow
    {
        // ------------------------------------------------------------------ Tastatur

        private bool _keyMode;           // nach dem Tastenkürzel: Pfeiltasten wählen, Enter startet
        private int _keyIndex = -1;
        private IntPtr _keyPrev;         // Fenster, das vorher aktiv war (bekommt den Fokus zurück)
        private Point _keyCursor;
        private DateTime _keyLast;

        /// <summary>
        /// Nach dem Einblenden per Tastenkürzel: Das Dock nimmt die Tastatur, das erste Symbol ist gewählt.
        /// Endet mit Esc, Enter, einer Mausbewegung oder nach 15 Sekunden ohne Taste.
        /// </summary>
        private void StartKeyMode()
        {
            if (_demo || !IsHandleCreated) return;
            int first = NextKeyIndex(-1, 1);
            if (first < 0) return;
            _keyPrev = DockNative.GetForegroundWindow();
            _keyCursor = Cursor.Position;
            _keyLast = DateTime.Now;
            _keyIndex = first;
            _keyMode = true;
            HidePreview();
            DockNative.BringToFront(Handle);
            _dirty = true;
        }

        private void EndKeyMode(bool restoreFocus)
        {
            if (!_keyMode) return;
            _keyMode = false;
            _keyIndex = -1;
            _lastInside = DateTime.Now;
            _dirty = true;
            IntPtr prev = _keyPrev;
            _keyPrev = IntPtr.Zero;
            if (restoreFocus && prev != IntPtr.Zero && IsHandleCreated && DockNative.GetForegroundWindow() == Handle)
                DockNative.BringToFront(prev);
        }

        /// <summary>Nächstes Symbol in Richtung dir (ohne Trennlinien, am Ende geht es vorne weiter); -1 = keins.</summary>
        private int NextKeyIndex(int from, int dir)
        {
            int n = _slots.Count;
            for (int k = 1; k <= n; k++)
            {
                int i = ((from + dir * k) % n + n) % n;
                if (!_slots[i].Item.IsSeparator) return i;
            }
            return -1;
        }

        /// <summary>Im Tastaturmodus steht die „Maus“ für Vergrößerung und Beschriftung auf dem gewählten Symbol.</summary>
        private void KeyTick(DateTime now, int x, int y, ref float along, ref float across)
        {
            bool moved = Math.Abs(x - _keyCursor.X) + Math.Abs(y - _keyCursor.Y) > R(8);
            if (moved || (now - _keyLast).TotalSeconds > 15 || _keyIndex < 0 || _keyIndex >= _slots.Count)
            {
                EndKeyMode(true);
                return;
            }
            Slot s = _slots[_keyIndex];
            along = s.BaseStart + BaseLen(s) / 2f;
            across = _edgeDist + _shelfThick / 2f;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_keyMode) return base.ProcessCmdKey(ref msg, keyData);
            _keyLast = DateTime.Now;
            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0;
            int index = _keyIndex;
            switch (key)
            {
                case Keys.Left:
                case Keys.Up:
                    SelectKey(NextKeyIndex(index, -1));
                    return true;
                case Keys.Right:
                case Keys.Down:
                    SelectKey(NextKeyIndex(index, 1));
                    return true;
                case Keys.Tab:
                    SelectKey(NextKeyIndex(index, shift ? -1 : 1));
                    return true;
                case Keys.Home:
                    SelectKey(NextKeyIndex(-1, 1));
                    return true;
                case Keys.End:
                    SelectKey(NextKeyIndex(_slots.Count, -1));
                    return true;
                case Keys.Escape:
                    EndKeyMode(true);
                    return true;
                case Keys.Enter:
                case Keys.Space:
                    EndKeyMode(false);
                    Launch(index, shift);
                    return true;
                case Keys.Apps:
                    KeyMenu(index);
                    return true;
                case Keys.F10:
                    if (!shift) break;
                    KeyMenu(index);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SelectKey(int index)
        {
            if (index < 0) return;
            _keyIndex = index;
            HidePreview();
            _dirty = true;
        }

        private void KeyMenu(int index)
        {
            EndKeyMode(false);
            if (index < 0 || index >= _slots.Count) return;
            ShowAt(BuildContextMenu(index), _slots[index]);
        }

        /// <summary>Buchstabe oder Ziffer: zum nächsten Eintrag springen, der so beginnt.</summary>
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!_keyMode || !char.IsLetterOrDigit(e.KeyChar)) return;
            _keyLast = DateTime.Now;
            string start = e.KeyChar.ToString();
            int n = _slots.Count;
            for (int k = 1; k <= n; k++)
            {
                int i = (_keyIndex + k) % n;
                DockItem it = _slots[i].Item;
                if (it.IsSeparator || !it.Title.StartsWith(start, StringComparison.CurrentCultureIgnoreCase)) continue;
                SelectKey(i);
                break;
            }
            e.Handled = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_keyMode) EndKeyMode(false); // ein anderes Fenster wurde aktiv
        }

        // ------------------------------------------------------------------ Tastenkürzel je Eintrag

        private const int ItemHotkeyBase = 200; // Nummern ab hier: eigene Tastenkürzel der Einträge
        private readonly Dictionary<int, DockItem> _itemHotkeys = new Dictionary<int, DockItem>();

        /// <summary>Eigene Tastenkürzel der Einträge anmelden (startet den Eintrag wie ein Klick).</summary>
        private void RegisterItemHotkeys()
        {
            if (!IsHandleCreated || _demo) return;
            foreach (int old in _itemHotkeys.Keys) DockNative.UnregisterHotKey(Handle, old);
            _itemHotkeys.Clear();
            int id = ItemHotkeyBase;
            foreach (DockItem it in _items)
            {
                if (it.HotkeyKey <= 0 || it.Temp || it.IsSeparator) continue;
                if (DockNative.RegisterHotKey(Handle, id, it.HotkeyModifiers | DockNative.MOD_NOREPEAT, it.HotkeyKey)) _itemHotkeys[id] = it;
                id++;
            }
        }

        /// <summary>Prüft, ob ein Tastenkürzel für einen Eintrag frei ist (für den Eigenschaften-Dialog).</summary>
        private bool TestItemHotkey(int modifiers, int key)
        {
            if (key <= 0 || !IsHandleCreated) return true;
            if (!DockNative.RegisterHotKey(Handle, 98, modifiers | DockNative.MOD_NOREPEAT, key)) return false;
            DockNative.UnregisterHotKey(Handle, 98);
            return true;
        }

        // ------------------------------------------------------------------ Mausrad

        /// <summary>Mausrad auf einem laufenden Programm: durch seine Fenster blättern.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float along, across;
            CursorDock(out along, out across);
            int index = HitIndex(along, across);
            if (index < 0 || index >= _slots.Count || e.Delta == 0) return;
            Slot s = _slots[index];
            if (s.Windows.Count == 0) return;
            // feste Reihenfolge (die Liste selbst ändert sich mit jedem Wechsel)
            List<IntPtr> list = new List<IntPtr>(s.Windows);
            list.Sort(delegate (IntPtr a, IntPtr b) { return a.ToInt64().CompareTo(b.ToInt64()); });
            int cur = list.IndexOf(DockNative.GetForegroundWindow());
            if (cur >= 0 && list.Count == 1) return;
            int next = cur < 0 ? 0 : ((cur + (e.Delta < 0 ? 1 : -1)) % list.Count + list.Count) % list.Count;
            HidePreview();
            _previewBlock = index;
            DockNative.Activate(list[next]);
        }

        // ------------------------------------------------------------------ Blinkende Fenster

        private int _shellMsg;
        private readonly HashSet<IntPtr> _flashing = new HashSet<IntPtr>();

        /// <summary>
        /// Meldungen, die auch die Taskleiste bekommt: Ein Fenster blinkt (will Aufmerksamkeit) – sein Symbol hüpft
        /// und der Strich darunter leuchtet orange, bis das Fenster aktiviert oder geschlossen wird.
        /// </summary>
        private void OnShellMessage(int code, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (code == DockNative.HSHELL_FLASH)
            {
                if (hwnd == DockNative.GetForegroundWindow())
                {
                    if (_flashing.Remove(hwnd)) _dirty = true;
                    return;
                }
                if (!_flashing.Add(hwnd)) return;
                RefreshRunning();
                if (_s.Animate)
                    foreach (Slot s in _slots)
                        if (s.Windows.Contains(hwnd)) s.BounceStart = DateTime.Now;
                _dirty = true;
            }
            else if (code == DockNative.HSHELL_WINDOWACTIVATED || code == DockNative.HSHELL_RUDEAPPACTIVATED || code == DockNative.HSHELL_WINDOWDESTROYED)
            {
                if (_flashing.Remove(hwnd)) _dirty = true;
            }
        }

        private bool Attention(Slot s)
        {
            if (_flashing.Count == 0) return false;
            foreach (IntPtr h in s.Windows) if (_flashing.Contains(h)) return true;
            return false;
        }

        // ------------------------------------------------------------------ Laufende Programme ohne Eintrag

        // Fenster von Windows selbst, die kein eigenes Programm sind
        private static readonly HashSet<string> UnpinnedIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "applicationframehost.exe", "systemsettings.exe", "textinputhost.exe", "shellexperiencehost.exe",
            "searchhost.exe", "startmenuexperiencehost.exe", "lockapp.exe", "searchapp.exe"
        };

        /// <summary>Index des ersten vorübergehenden Eintrags (sie stehen immer am Ende); ohne solche: Anzahl der Einträge.</summary>
        private int FirstTemp()
        {
            for (int i = 0; i < _items.Count; i++) if (_items[i].Temp) return i;
            return _items.Count;
        }

        /// <summary>
        /// Wie in der Taskleiste: Laufende Programme, die keinen Eintrag haben, erscheinen vorübergehend am Ende des
        /// Docks und verschwinden wieder, wenn sie beendet werden. Gibt true zurück, wenn sich etwas geändert hat.
        /// </summary>
        private bool SyncUnpinned(Dictionary<string, List<IntPtr>> windows)
        {
            if (_demo) return false;
            bool want = _s.ShowUnpinned && Number == 1; // nur im ersten Dock, sonst stünden sie in jedem
            if (!want && FirstTemp() == _items.Count) return false;
            // nicht, während ein Menü, die Vorschau oder das Ziehen einen Eintrag über seine Nummer anspricht
            if (_menuOpen || _openMenu != null || _dragging || _extDrag || _keyMode || _pressIndex >= 0 || PreviewVisible) return false;
            HashSet<string> pinned = new HashSet<string>();
            foreach (Slot s in _slots)
            {
                if (s.Item.Temp) continue;
                // erst wenn alle festen Einträge geladen sind – sonst erschienen ihre Programme kurz doppelt
                if (want && !s.Item.IsSeparator && s.Master == null) return false;
                foreach (string exe in s.Exes) pinned.Add(exe);
            }
            Dictionary<Slot, float> glide = null;
            bool changed = false;
            HashSet<string> shown = new HashSet<string>();
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                DockItem it = _items[i];
                if (!it.Temp) continue;
                string key = it.Command.ToLowerInvariant();
                if (want && windows.ContainsKey(key) && !pinned.Contains(key)) { shown.Add(key); continue; }
                if (!changed) glide = GlideSnapshot();
                changed = true;
                _slots[i].DisposeImages();
                _slots.RemoveAt(i);
                _items.RemoveAt(i);
            }
            List<Slot> added = new List<Slot>();
            if (want)
                foreach (KeyValuePair<string, List<IntPtr>> w in windows)
                {
                    string key = w.Key;
                    if (pinned.Contains(key) || shown.Contains(key)) continue;
                    string name;
                    try { name = Path.GetFileName(key); } catch { continue; }
                    if (UnpinnedIgnore.Contains(name) || !key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!changed) glide = GlideSnapshot();
                    changed = true;
                    DockItem it = ItemFromPath(key);
                    it.Temp = true;
                    Slot s = new Slot { Item = it, Windows = w.Value };
                    s.Exes.Add(key);
                    if (_s.Animate) s.Appear = 0f;
                    _items.Add(it);
                    _slots.Add(s);
                    added.Add(s);
                }
            if (!changed) return false;
            _hover = -1;
            ApplySettings();
            GlideFrom(glide);
            if (added.Count > 0) LoadIconsAsync(added);
            return true;
        }

        /// <summary>„Im Dock behalten“: aus dem vorübergehenden wird ein fester Eintrag (hinter den übrigen festen).</summary>
        private void KeepItem(int index)
        {
            if (index < 0 || index >= _items.Count || !_items[index].Temp) return;
            Dictionary<Slot, float> glide = GlideSnapshot();
            DockItem it = _items[index];
            Slot s = _slots[index];
            int to = FirstTemp();
            it.Temp = false;
            _items.RemoveAt(index);
            _slots.RemoveAt(index);
            _items.Insert(to, it);
            _slots.Insert(to, s);
            Save();
            GlideFrom(glide);
            _dirty = true;
        }

        // ------------------------------------------------------------------ Sanftes Gleiten

        private Dictionary<Slot, float> _glideFrom;

        /// <summary>Vor einer Änderung der Reihenfolge: wo jedes Symbol gerade steht (null = Animationen aus).</summary>
        private Dictionary<Slot, float> GlideSnapshot()
        {
            if (!_s.Animate || _demo) return null;
            Dictionary<Slot, float> at = new Dictionary<Slot, float>();
            foreach (Slot s in _slots) at[s] = s.Rest + s.Slide;
            return at;
        }

        /// <summary>Nach der Änderung: Die Symbole gleiten von ihrer alten Stelle an die neue (siehe DoLayout und Tick).</summary>
        private void GlideFrom(Dictionary<Slot, float> at)
        {
            if (at == null || _glideFrom != null) return; // eine noch nicht angewandte ältere Aufnahme gilt weiter
            _glideFrom = at;
            _dirty = true;
        }
    }
}
