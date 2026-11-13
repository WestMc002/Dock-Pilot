// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later
// Schwebendes Symbol beim Herausziehen aus dem Dock

using System;
using System.Drawing;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>
    /// Durchsichtiges, nicht anklickbares Fenster, das beim Ziehen eines Symbols aus dem Dock heraus der Maus folgt
    /// (wie bei RocketDock). Echte Transparenz über UpdateLayeredWindow; das Bild kommt fertig gezeichnet von außen.
    /// </summary>
    internal sealed class DragGhost : Form
    {
        public DragGhost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (Maus geht durch) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
                return cp;
            }
        }

        /// <summary>Bild (mit Alpha) zeigen, mittig an der Stelle center (Bildschirm).</summary>
        public void Show(Bitmap image, Point center)
        {
            IntPtr handle = Handle; // Fenster anlegen
            Point topLeft = new Point(center.X - image.Width / 2, center.Y - image.Height / 2);
            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
                hbmp = image.GetHbitmap(Color.FromArgb(0)); // 32 Bit mit vormultipliziertem Alpha
                old = NativeMethods.SelectObject(memDc, hbmp);
                NativeMethods.POINT dst = new NativeMethods.POINT(topLeft.X, topLeft.Y);
                NativeMethods.SIZE size = new NativeMethods.SIZE(image.Width, image.Height);
                NativeMethods.POINT src = new NativeMethods.POINT(0, 0);
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION();
                blend.BlendOp = NativeMethods.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = NativeMethods.AC_SRC_ALPHA;
                NativeMethods.UpdateLayeredWindow(handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                if (memDc != IntPtr.Zero)
                {
                    if (old != IntPtr.Zero) NativeMethods.SelectObject(memDc, old);
                    NativeMethods.DeleteDC(memDc);
                }
                if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
            }
            if (!Visible) base.Show();
        }
    }
}