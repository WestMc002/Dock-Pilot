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
    /// <summary>Anordnung: Maße, Plätze und Vergrößerung der Symbole. Teil von DockWindow (siehe DockWindow.cs).</summary>
    internal sealed partial class DockWindow
    {
        // ------------------------------------------------------------------ Anordnung

        private float BaseLen(Slot s) { return s.Item.IsSeparator ? _sepLen : _basePx; }

        /// <summary>Ruhige Effekte ohne Vergrößerung der Leiste: Anheben, Aus, Leuchten, Fokus, Federn.</summary>
        private bool SimpleEffect { get { int m = _s.ZoomMode; return m == 3 || m == 4 || m >= 6; } }

        /// <summary>Stärke der Hervorhebung als Faktor (Einstellung in Prozent, 1 = Standard).</summary>
        private float Fx { get { return Math.Max(0.2f, Math.Min(2f, _s.EffectStrength / 100f)); } }

        /// <summary>
        /// Anordnen, danach das Symbol unter der Maus für die Hervorhebung bestimmen: Bei den ruhigen Größen-Effekten
        /// steht es schon fest, bei Blase und Lupe erst, wenn die Symbole ihren Platz haben.
        /// </summary>
        private void DoLayout()
        {
            LayoutSlots();
            if (!SimpleEffect)
            {
                _effectIndex = -1;
                if (_zoom > 0.001f && !_dragging)
                {
                    float m = _mouseAlong, half = _spacing / 2f;
                    for (int i = 0; i < _slots.Count; i++)
                    {
                        Slot s = _slots[i];
                        if (!s.Item.IsSeparator && m >= s.Pos - half && m <= s.Pos + s.Len + half) { _effectIndex = i; break; }
                    }
                }
            }
            // Federn und Wackeln: das Symbol bewegt sich einmal, sobald die Maus darauf kommt
            if ((_s.Highlight == 8 || _s.Highlight == 11) && _effectIndex != _hopIndex)
            {
                _hopIndex = _effectIndex;
                if (_effectIndex >= 0 && (!_demo || _animDemo) && _s.Animate) _slots[_effectIndex].HopStart = DateTime.Now;
            }
        }

        private float Falloff(float d)
        {
            float w = _s.ZoomWidth + 1f;
            if (Math.Abs(d) >= w) return 0f;
            if (_s.ZoomMode == 5)
            {
                // weiche Blase: Glockenkurve, nur das Symbol unter der Maus wächst deutlich (Nachbar ca. 14 %)
                float gauss = (float)Math.Exp(-d * d * 2f);
                float edgeK = 1f - Math.Abs(d) / w; // zum Rand hin sanft auf 0
                return gauss * Math.Min(1f, edgeK * 3f);
            }
            float c = (float)Math.Cos(d / w * Math.PI / 2);
            return c * c;
        }

        private void LayoutSlots()
        {
            int n = _slots.Count;
            float sp = _spacing;
            float total = 0;
            foreach (Slot s in _slots) total += BaseLen(s);
            if (n > 1) total += sp * (n - 1);
            // Zentrierung: -100 % = am Anfang, 0 % = Mitte, 100 % = am Ende des freien Platzes
            // Zoom-Effekt: Blase (Nachbarn rücken zur Seite) oder fest (Leiste bleibt stehen, Symbole wachsen an ihrem Platz).
            // Automatisch: fest, sobald das Dock mehr als zwei Drittel des Bildschirmrands einnimmt – sonst wandert die lange Leiste mit der Maus.
            // ruhige Effekte (Anheben, Aus, Leuchten, Fokus, Federn): kein Zoom, Leiste bleibt stehen
            bool simple = SimpleEffect;
            _fixedZoom = simple || _s.ZoomMode == 2 || (_s.ZoomMode == 0 && total > _Lwin * 2f / 3f);
            // Blase mit fester Leistenbreite: auf Wunsch ist die Leiste an beiden Enden etwas länger (Reserve für die Blase)
            bool keep = !_fixedZoom && _s.KeepWidth && _zoomPx > _basePx && n > 0;
            float res = keep ? Math.Max(0f, Math.Min(R(_s.Reserve), (_Lwin - 2 * (_padL + R(4)) - total) / 2f)) : 0f;
            _edge0 = _edge1 = 0f;
            _overlap = false;
            float edge = _padL + R(4) + res;
            float free = _Lwin - 2 * edge - total;
            float x0 = free > 0 ? edge + free * (_s.CenterPercent + 100) / 200f : (_Lwin - total) / 2f;
            _axisCenter = x0 + total / 2f;
            x0 = Math.Max(edge, Math.Min(x0, _Lwin - edge - total)); // nicht über den Bildschirmrand hinaus
            float x = x0;
            foreach (Slot s in _slots) { s.Rest = x; x += BaseLen(s) + sp; }
            if (_glideFrom != null)
            {
                // Reihenfolge geändert: jedes Symbol beginnt an seiner alten Stelle und gleitet an die neue (Tick)
                foreach (Slot s in _slots)
                {
                    float old;
                    if (_glideFrom.TryGetValue(s, out old)) s.Slide = old - s.Rest;
                }
                _glideFrom = null;
            }
            foreach (Slot s in _slots) s.BaseStart = s.Rest + s.Slide;

            bool zoomOn = _zoom > 0.001f && n > 0 && !_dragging && _zoomPx > _basePx;
            float m = _mouseAlong;
            _effectIndex = -1;
            if (simple && _zoom > 0.001f && !_dragging)
                for (int i = 0; i < n; i++)
                {
                    Slot s = _slots[i];
                    if (!s.Item.IsSeparator && m >= s.BaseStart - sp / 2f && m <= s.BaseStart + BaseLen(s) + sp / 2f) { _effectIndex = i; break; }
                }
            for (int i = 0; i < n; i++)
            {
                Slot s = _slots[i];
                s.Size = _basePx;
                if (simple)
                {
                    if (_s.ZoomMode == 3 && i == _effectIndex) s.Size = _basePx * (1f + 0.15f * _zoom); // Anheben: nur leicht größer
                    if (_s.ZoomMode == 10 && i == _effectIndex)
                    {
                        // Pulsieren: atmet zwischen 100 und 108 % (eine Runde in gut anderthalb Sekunden)
                        double breath = _s.Animate && (!_demo || _animDemo) ? 0.5 - 0.5 * Math.Cos(Environment.TickCount / 255.0) : 0.6;
                        s.Size = _basePx * (1f + 0.08f * Fx * _zoom * (float)breath);
                    }
                }
                else if (zoomOn && !s.Item.IsSeparator)
                {
                    float d = (m - (s.BaseStart + _basePx / 2f)) / (_basePx + sp);
                    s.Size = _basePx + (_zoomPx - _basePx) * _zoom * Falloff(d);
                }
                if (s.Appear < 1f && !s.Item.IsSeparator)
                {
                    // neu eingefügt: wächst an seinem Platz heran
                    float a = s.Appear * s.Appear * (3f - 2f * s.Appear);
                    s.Size *= 0.2f + 0.8f * a;
                }
                s.Len = s.Item.IsSeparator ? _sepLen : s.Size;
            }

            // Passt die vergrößerte Leiste nicht auf den Bildschirm, nur so viel wachsen, wie Platz ist.
            // Sonst verschiebt sich bei jeder Mausbewegung die ganze Leiste.
            if (zoomOn && !_fixedZoom && !_s.KeepWidth)
            {
                float growth = 0;
                foreach (Slot s in _slots) growth += s.Len - BaseLen(s);
                float avail = _Lwin - 2 * _padL - R(4);
                if (growth > 0.5f && total + growth > avail)
                {
                    float k = Math.Max(0f, (avail - total) / growth);
                    foreach (Slot s in _slots)
                    {
                        if (s.Item.IsSeparator) continue;
                        s.Size = _basePx + (s.Size - _basePx) * k;
                        s.Len = s.Size;
                    }
                }
            }

            if (simple)
            {
                foreach (Slot s in _slots) s.Pos = s.BaseStart + BaseLen(s) / 2f - s.Len / 2f;
            }
            else if (!zoomOn)
            {
                foreach (Slot s in _slots) s.Pos = s.BaseStart + BaseLen(s) / 2f - s.Len / 2f; // gleich BaseStart, außer beim Heranwachsen
            }
            else if (_fixedZoom)
            {
                // an Ort und Stelle: Mitte jedes Symbols bleibt, größere überdecken die Nachbarn
                foreach (Slot s in _slots)
                {
                    s.Pos = s.BaseStart + BaseLen(s) / 2f - s.Len / 2f;
                }
            }
            else
            {
                // Die Stelle unter der Maus bleibt stehen, die Leiste wächst nach beiden Seiten
                float mm = Math.Max(x0, Math.Min(x0 + total, m));
                int k = n - 1;
                for (int i = 0; i < n; i++)
                    if (mm < _slots[i].BaseStart + BaseLen(_slots[i]) + sp) { k = i; break; }
                float unit = BaseLen(_slots[k]) + sp;
                float t = Math.Max(0f, Math.Min(1f, (mm - _slots[k].BaseStart) / unit));
                float zk = 0;
                for (int j = 0; j < k; j++) zk += _slots[j].Len + sp;
                float start = mm - zk - t * (_slots[k].Len + sp);
                float zTotal = 0;
                foreach (Slot s in _slots) zTotal += s.Len;
                zTotal += sp * (n - 1);
                if (zTotal < _Lwin - 2 * _padL)
                    start = Math.Max(_padL + R(2), Math.Min(start, _Lwin - _padL - R(2) - zTotal));
                float p = start;
                foreach (Slot s in _slots) { s.Pos = p; p += s.Len + sp; }
                if (_s.KeepWidth) KeepShelfWidth(x0, x0 + total, sp, res);
            }

            if (keep)
            {
                // feste Breite: Ruhelänge plus Reserve; nur am ersten/letzten Symbol wächst die Leiste dort etwas hinaus
                _shelfStart = x0 - res - _padL - _edge0;
                _shelfEnd = x0 + total + res + _padL + _edge1;
            }
            else if (n > 0 && _fixedZoom)
            {
                _shelfStart = _slots[0].BaseStart - _padL;
                _shelfEnd = _slots[n - 1].BaseStart + BaseLen(_slots[n - 1]) + _padL;
            }
            else if (n > 0)
            {
                _shelfStart = _slots[0].Pos - _padL;
                _shelfEnd = _slots[n - 1].Pos + _slots[n - 1].Len + _padL;
            }
            else
            {
                _shelfStart = _axisCenter - _basePx / 2 - _padL;
                _shelfEnd = _axisCenter + _basePx / 2 + _padL;
            }
        }

        /// <summary>
        /// Blase, ohne dass die Leiste breiter wird: Die Blase bleibt (möglichst) unter der Maus, die Symbole links und rechts
        /// davon geben Platz her – sie werden kleiner (schrumpfen) oder schieben sich übereinander (überlappen).
        /// Hergegeben wird nach Gewicht (1 − Anteil der Vergrößerung): Symbole in der Blase kaum, weiter entfernte stärker.
        /// Alles ändert sich stetig mit der Maus, damit nichts springt.
        /// res: Reserve an beiden Enden der Leiste, die die Blase zuerst nutzt. Am ersten/letzten Symbol darf die Leiste
        /// auf Wunsch ein Stück über ihr Ende hinauswachsen (_edge0/_edge1). Reicht der Platz nicht, wächst die Blase weniger.
        /// </summary>
        private void KeepShelfWidth(float b0, float b1, float sp, float res)
        {
            int n = _slots.Count;
            if (n == 0) return;
            float minK = Math.Max(0.5f, Math.Min(1f, _s.MinShrink / 100f)); // kleinste Größe bzw. kleinster sichtbarer Anteil
            bool overlap = _s.KeepMode == 1;
            float A0 = b0 - res, A1 = b1 + res;
            float range = Math.Max(1f, _zoomPx - _basePx);
            float m = _mouseAlong;
            // Nähe der Maus zum Ende der Leiste (1 = am ersten bzw. letzten Symbol, 0 = weiter innen als die Blase reicht)
            float reach = (_s.ZoomWidth + 1f) * (_basePx + sp);
            float f0 = _s.EdgeGrow ? Math.Max(0f, Math.Min(1f, 1f - (m - b0) / reach)) : 0f;
            float f1 = _s.EdgeGrow ? Math.Max(0f, Math.Min(1f, 1f - (b1 - m) / reach)) : 0f;
            float room0 = Math.Max(0f, A0 - _padL - R(2)), room1 = Math.Max(0f, _Lwin - R(2) - _padL - A1);
            float start = _slots[0].Pos, e0 = 0f, e1 = 0f;

            // Gewichte: wie viel jedes Symbol hergeben kann – aus der ungekürzten Blase, damit sich beim Überfahren nichts sprunghaft ändert
            float wl = 0, wr = 0;
            float[] w = new float[n];
            bool[] left = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Slot s = _slots[i];
                float g = s.Item.IsSeparator ? 0f : Math.Max(0f, Math.Min(1f, (s.Size - _basePx) / range));
                w[i] = (1f - g) * BaseLen(s);
                left[i] = s.BaseStart + BaseLen(s) / 2f < m;
                if (left[i]) wl += w[i]; else wr += w[i];
            }
            float capL = (1 - minK) * wl, capR = (1 - minK) * wr;
            // Stelle unter der Maus (wie in DoLayout), um die Blase nach dem Verkleinern wieder dort anzulegen
            float mm = Math.Max(b0, Math.Min(b1, m));
            int anchor = n - 1;
            for (int i = 0; i < n; i++)
                if (mm < _slots[i].BaseStart + BaseLen(_slots[i]) + sp) { anchor = i; break; }
            float at = Math.Max(0f, Math.Min(1f, (mm - _slots[anchor].BaseStart) / (BaseLen(_slots[anchor]) + sp)));

            const int Passes = 8;
            for (int pass = 0; pass < Passes; pass++)
            {
                // Überstand links und rechts der normalen Blase (Stelle unter der Maus bleibt stehen)
                float first = _slots[0].Pos;
                float over0 = A0 - first;
                float over1 = _slots[n - 1].Pos + _slots[n - 1].Len - A1;
                // freier Platz auf einer Seite gleicht den Überstand der anderen aus (die Blase rückt dorthin)
                if (over0 < 0) { over1 += over0; over0 = 0; }
                if (over1 < 0) { over0 += over1; over1 = 0; }
                if (over0 < 0) over0 = 0;
                // am Ende der Leiste: statt die Blase nach innen zu schieben, wächst die Leiste dort hinaus
                e0 = Math.Min(Math.Max(0f, over0 - capL) * f0, room0);
                e1 = Math.Min(Math.Max(0f, over1 - capR) * f1, room1);
                over0 -= e0;
                over1 -= e1;
                if (over0 > capL) { over1 += over0 - capL; over0 = capL; }
                if (over1 > capR) { over0 += over1 - capR; over1 = capR; }
                if (over0 > capL + 0.01f && pass < Passes - 1)
                {
                    // insgesamt zu wenig Platz: Blase kleiner machen und noch einmal rechnen
                    float growth = 0;
                    foreach (Slot s in _slots) if (!s.Item.IsSeparator) growth += s.Size - _basePx;
                    float k = growth > 0.01f ? Math.Max(0f, 1f - (over0 - capL) / growth) : 0f;
                    foreach (Slot s in _slots)
                        if (!s.Item.IsSeparator) { s.Size = _basePx + (s.Size - _basePx) * k; s.Len = s.Size; }
                    // wieder um die Stelle unter der Maus legen (wie die normale Blase), dann nächster Durchgang
                    float zk = 0;
                    for (int j = 0; j < anchor; j++) zk += _slots[j].Len + sp;
                    float q = mm - zk - at * (_slots[anchor].Len + sp);
                    foreach (Slot s in _slots) { s.Pos = q; q += s.Len + sp; }
                    continue;
                }
                over0 = Math.Min(over0, capL);
                float newLen = sp * (n - 1);
                for (int i = 0; i < n; i++)
                {
                    Slot s = _slots[i];
                    float side = left[i] ? (wl > 0.01f ? over0 / wl : 0f) : (wr > 0.01f ? over1 / wr : 0f);
                    s.Len = Math.Max(0.5f, s.Len - w[i] * side);
                    if (!overlap && !s.Item.IsSeparator) s.Size = s.Len; // schrumpfen; beim Überlappen bleibt die Größe
                    newLen += s.Len;
                }
                float lo = A0 - e0, hi = A1 + e1 - newLen;
                start = hi < lo ? (lo + hi) / 2f : Math.Max(lo, Math.Min(hi, first + over0));
                break;
            }
            _edge0 = e0;
            _edge1 = e1;
            _overlap = overlap;
            float p = start;
            foreach (Slot s in _slots)
            {
                // Pos = linke Kante des Symbols; beim Überlappen ist das Symbol breiter als sein Platz und liegt mittig darauf
                s.Pos = s.Item.IsSeparator ? p : p + s.Len / 2f - s.Size / 2f;
                p += s.Len + sp;
            }
        }
    }
}
