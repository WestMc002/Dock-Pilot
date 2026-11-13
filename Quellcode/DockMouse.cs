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
    /// <summary>Maus: Klicken, Verschieben, Herausziehen und Ablegen aus dem Explorer. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
        // ------------------------------------------------------------------ Maus

        private void CursorDock(out float along, out float across)
        {
            Point p = Cursor.Position;
            ToDock(p.X, p.Y, out along, out across);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == DockNative.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)DockNative.MA_NOACTIVATE; // Klick ins Dock nimmt anderen Fenstern nicht den Fokus
                return;
            }
            if (m.Msg == 0x0320) // WM_DWMCOLORIZATIONCOLORCHANGED: Akzentfarbe in Windows geändert
            {
                _accent = Theme.SystemAccent;
                _dirty = true;
            }
            if (_shellMsg != 0 && m.Msg == _shellMsg)
            {
                OnShellMessage((int)(m.WParam.ToInt64() & 0xFFFF), m.LParam);
                return;
            }
            if (m.Msg == DockNative.WM_HOTKEY)
            {
                // eigenes Tastenkürzel eines Eintrags: wie ein Klick darauf
                DockItem hit;
                if (_itemHotkeys.TryGetValue(m.WParam.ToInt32(), out hit))
                {
                    int at = _items.IndexOf(hit);
                    if (at >= 0) { EndKeyMode(false); Launch(at, false); }
                    return;
                }
            }
            if (m.Msg == DockNative.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleByHotkey();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float along, across;
            CursorDock(out along, out across);
            if (e.Button == MouseButtons.Left)
            {
                _pressIndex = HitIndex(along, across);
                _pressPoint = Cursor.Position;
                _dirty = true;
            }
            else if (e.Button == MouseButtons.Middle) _middleIndex = HitIndex(along, across);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressIndex < 0 || (MouseButtons & MouseButtons.Left) == 0) return;
            float along, across;
            CursorDock(out along, out across);
            if (!_dragging)
            {
                Point p = Cursor.Position;
                if (_s.Locked || Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) < R(8)) return;
                if (_pressIndex >= _items.Count || _items[_pressIndex].Temp) return; // vorübergehende Einträge bleiben am Ende
                _dragging = true;
                _dragIndex = _pressIndex;
                Capture = true;
            }
            // aus dem Dock herausgezogen? Dann folgt ein schwebendes Symbol der Maus, Loslassen entfernt den Eintrag
            float outside = R(32);
            bool outNow = across > _edgeDist + _shelfThick + outside || across < -outside
                || along < _shelfStart - outside * 2 || along > _shelfEnd + outside * 2;
            if (outNow != _dragOut) { _dragOut = outNow; _dirty = true; }
            if (_dragOut)
            {
                ShowGhost();
                return;
            }
            HideGhost();
            _mouseAlong = along;
            // Ziel nach der Grundanordnung (ohne Vergrößerung) bestimmen
            // Position in der Liste ohne das gezogene Symbol = Anzahl der Mitten links der Maus
            int target = 0;
            for (int i = 0; i < _slots.Count; i++)
                if (i != _dragIndex && along > _slots[i].BaseStart + BaseLen(_slots[i]) / 2f) target++;
            target = Math.Min(target, FirstTemp() - 1); // nicht hinter die vorübergehenden Einträge
            if (target != _dragIndex && target >= 0)
            {
                GlideFrom(GlideSnapshot()); // die übrigen Symbole gleiten zur Seite
                Slot s = _slots[_dragIndex];
                DockItem it = _items[_dragIndex];
                _slots.RemoveAt(_dragIndex);
                _items.RemoveAt(_dragIndex);
                _slots.Insert(target, s);
                _items.Insert(target, it);
                _dragIndex = target;
                _pressIndex = target;
            }
            _dirty = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            float along, across;
            CursorDock(out along, out across);
            if (_dragging)
            {
                EndDrag();
                return;
            }
            int hit = HitIndex(along, across);
            if (e.Button == MouseButtons.Left && _pressIndex >= 0 && hit == _pressIndex)
                Launch(hit, (ModifierKeys & Keys.Shift) == Keys.Shift);
            else if (e.Button == MouseButtons.Middle)
            {
                // Mittelklick: immer neu starten, auch wenn das Programm schon läuft (wie in der Taskleiste)
                if (_middleIndex >= 0 && hit == _middleIndex) Launch(hit, true);
                _middleIndex = -1;
            }
            else if (e.Button == MouseButtons.Right)
                ShowContextMenu(HitIndex(along, across, true));
            _pressIndex = -1;
            _dirty = true;
        }

        private void EndDrag()
        {
            int index = _dragIndex;
            bool remove = _dragOut && index >= 0 && index < _items.Count;
            HideGhost();
            _dragOut = false;
            _dragging = false;
            _dragIndex = -1;
            _pressIndex = -1;
            Capture = false;
            if (remove)
            {
                // herausgezogen und losgelassen: entfernen ohne Rückfrage (wie RocketDock); Programm bleibt installiert
                Dictionary<Slot, float> glide = GlideSnapshot();
                _undoItem = _items[index];
                _undoIndex = index;
                _undoFiles = null;
                _items.RemoveAt(index);
                _slots[index].DisposeImages();
                _slots.RemoveAt(index);
                _hover = -1;
                Save();
                ApplySettings();
                GlideFrom(glide);
                return;
            }
            Save();
            _dirty = true;
        }

        /// <summary>Schwebendes Symbol an der Maus: halb durchsichtig, mit rotem Entfernen-Zeichen.</summary>
        private void ShowGhost()
        {
            if (_dragIndex < 0 || _dragIndex >= _slots.Count) return;
            Slot s = _slots[_dragIndex];
            int size = Math.Max(16, (int)Math.Round(_basePx));
            int badge = Math.Max(14, (int)Math.Round(R(18)));
            int w = size + badge / 2 + (int)R(4);
            using (Bitmap img = new Bitmap(w, w, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    Bitmap src = s.Base ?? s.Zoom;
                    if (src != null)
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ColorMatrix cm = new ColorMatrix();
                            cm.Matrix33 = 0.75f;
                            ia.SetColorMatrix(cm);
                            g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                        }
                    else if (s.Item.IsSeparator)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(160, 128, 128, 128)))
                            g.FillRectangle(b, size / 2 - (int)R(1.5f), 0, Math.Max(2, (int)R(3)), size);
                    // rotes Zeichen „wird entfernt“ unten rechts
                    Rectangle br = new Rectangle(w - badge - 1, w - badge - 1, badge, badge);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(196, 43, 28))) g.FillEllipse(b, br);
                    using (Pen pen = new Pen(Color.White, Math.Max(1.5f, R(2))))
                    {
                        float k = badge * 0.28f;
                        float cx = br.X + badge / 2f, cy = br.Y + badge / 2f;
                        g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                        g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
                    }
                }
                if (_ghost == null || _ghost.IsDisposed) _ghost = new DragGhost();
                Point c = Cursor.Position;
                _ghost.Show(img, new Point(c.X + (w - size) / 2, c.Y + (w - size) / 2));
            }
        }

        private void HideGhost()
        {
            if (_ghost != null && !_ghost.IsDisposed && _ghost.Visible) _ghost.Hide();
        }

        /// <summary>Dock kurz einblenden (zweiter Programmstart): wie das Tastenkürzel, aber nie ausblenden.</summary>
        public void ShowNow()
        {
            _userHidden = false;
            _hotkeyShown = true;
            _hotkeyTime = DateTime.Now;
            _dirty = true;
        }

        private void ToggleByHotkey()
        {
            if (_s.AutoHide || _fullscreen)
            {
                if (_show > 0.5f) { _hotkeyShown = false; _userHidden = !_s.AutoHide && !_fullscreen; _lastInside = DateTime.MinValue; }
                else { _hotkeyShown = true; _hotkeyTime = DateTime.Now; _userHidden = false; }
            }
            else
            {
                _userHidden = !_userHidden;
                _hotkeyShown = false;
            }
            _dirty = true;
            if (_quietToggle) return; // weiteres Dock: nur ein-/ausblenden
            // eingeblendet: Tastatur übernehmen (Pfeiltasten, Enter); ausgeblendet: Tastatur zurückgeben
            if (!_userHidden && (_hotkeyShown || !(_s.AutoHide || _fullscreen))) StartKeyMode();
            else EndKeyMode(true);
            if (HotkeyPressed != null) HotkeyPressed();
        }

        // ------------------------------------------------------------------ Ziehen aus dem Explorer

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; _extDrag = true; UpdateDrop(e); }
            else e.Effect = DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            UpdateDrop(e);
        }

        private void UpdateDrop(DragEventArgs e)
        {
            float along, across;
            ToDock(e.X, e.Y, out along, out across);
            int onto = -1, index = _slots.Count;
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                float rel = (along - s.Pos) / Math.Max(1f, s.Len);
                bool target = s.Item.Kind == ItemKind.Ordnermenue || (s.Item.Kind == ItemKind.Programm && IsProgram(s.Item.Command));
                if (!s.Item.IsSeparator && target && rel >= 0.25f && rel <= 0.75f)
                {
                    onto = i;
                    break;
                }
                if (along < s.Pos + s.Len / 2f) { index = i; break; }
            }
            if (onto != _dropOnto || index != _dropIndex) { _dropOnto = onto; _dropIndex = index; _dirty = true; }
        }

        private static bool IsProgram(string cmd)
        {
            string c = IconLoader.Expand(cmd).ToLowerInvariant();
            return c.EndsWith(".exe") || c.EndsWith(".bat") || c.EndsWith(".cmd");
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _extDrag = false;
            _dropIndex = _dropOnto = -1;
            _dirty = true;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            int onto = _dropOnto, index = _dropIndex;
            _extDrag = false;
            _dropIndex = _dropOnto = -1;
            _dirty = true;
            if (files == null || files.Length == 0) return;
            if (onto >= 0 && onto < _items.Count && _items[onto].Kind == ItemKind.Ordnermenue)
            {
                DropIntoFolder(onto, files);
                return;
            }
            if (onto >= 0 && onto < _items.Count)
            {
                // Dateien auf ein Programm gezogen: damit öffnen
                DockItem it = _items[onto];
                string args = it.Arguments;
                foreach (string f in files) args += (args.Length > 0 ? " " : "") + "\"" + f + "\"";
                string err = Shell.Start(it.Command, args, it.WorkingDirectory, it.ShowCmd, it.RunAsAdmin);
                if (err != null) MessageBox.Show(err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (_s.Animate) { _slots[onto].BounceStart = DateTime.Now; }
                return;
            }
            if (index < 0) index = _items.Count;
            foreach (string f in files) InsertItem(index++, ItemFromPath(f));
        }

        /// <summary>
        /// Dateien auf ein Ordnermenü gezogen: Verknüpfungen (.lnk, .url) werden in den Ordner kopiert,
        /// für alles andere wird dort eine Verknüpfung angelegt. Die Originale bleiben, wo sie sind.
        /// </summary>
        private void DropIntoFolder(int index, string[] files)
        {
            string folder = IconLoader.Expand(_items[index].Folder).TrimEnd('\\');
            string error = null;
            int done = 0;
            foreach (string f in files)
            {
                try
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    bool isLink = (ext == ".lnk" || ext == ".url") && File.Exists(f);
                    if (isLink && string.Equals(Path.GetDirectoryName(f), folder, StringComparison.OrdinalIgnoreCase)) continue; // liegt schon dort
                    string name = isLink || ext == ".exe" ? Path.GetFileNameWithoutExtension(f) : Path.GetFileName(f.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(name)) name = f.Replace(":", "").Replace("\\", "");
                    string linkExt = isLink ? ext : ".lnk";
                    string path = Path.Combine(folder, name + linkExt);
                    for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, name + " (" + n + ")" + linkExt);
                    if (isLink) File.Copy(f, path);
                    else if (!ShortcutInfo.Create(path, f)) throw new Exception("Die Verknüpfung auf „" + name + "“ konnte nicht angelegt werden.");
                    done++;
                }
                catch (Exception ex) { error = ex.Message; }
            }
            if (error != null) MessageBox.Show(error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (done > 0 && _s.Animate && index < _slots.Count) _slots[index].BounceStart = DateTime.Now;
        }
    }
}
