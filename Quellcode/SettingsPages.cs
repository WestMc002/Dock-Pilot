// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Einstellungsfenster: Aufbau der Oberfläche, je Seite eine Methode. Teil von SettingsForm (siehe SettingsForm.cs).</summary>
    internal sealed partial class SettingsForm
    {
        // ------------------------------------------------------------------ Aufbau

        private void BuildUi(Icon icon)
        {
            // Kopf: Icon, Programmname, Versions-Plakette, darunter kurze Erklärung
            _logo = new PictureBox();
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.Bounds = new Rectangle(S(24), S(20), S(44), S(44));
            try { _logo.Image = Theme.IconToBitmap(icon, S(48)); } catch { }
            Controls.Add(_logo);

            Label title = new NoPaddingLabel();
            title.Text = "Dock Pilot";
            title.AutoSize = true;
            title.Font = Theme.UiFont(22f * _sc, FontStyle.Bold);
            title.UseMnemonic = false;
            TextFormatFlags np = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            Size ts = TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, np);
            int indent = (TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width - ts.Width) / 2;
            title.Location = new Point(S(80) + indent, S(18));
            Controls.Add(title);

            Label subtitle = MakeLabel("Programmleiste am Bildschirmrand – Programme, Ordner und Menüs mit einem Klick", 13.5f, true);
            subtitle.Location = new Point(S(82), S(48));
            Controls.Add(subtitle);

            VersionPill version = new VersionPill();
            version.Text = "Version " + AppInfo.Version;
            int vw;
            using (Font vf = Theme.UiFont(10.5f * _sc, FontStyle.Regular))
                vw = TextRenderer.MeasureText(version.Text, vf, Size.Empty, np).Width + S(16);
            int inkRight = NoPaddingLabel.InkRight(title);
            int textEnd = inkRight > 0 ? inkRight : ts.Width;
            version.Bounds = new Rectangle(title.Left + textEnd + S(13), title.Top + (ts.Height - S(21)) / 2, vw, S(21));
            version.Click += delegate { AboutDialog.ShowFor(this); };
            Controls.Add(version);

            _bar = new UpdateBar(_sc);
            _bar.Bounds = new Rectangle(S(24), S(84), ClientSize.Width - S(48), S(52));
            _bar.Visible = false;
            _bar.Install.Click += delegate
            {
                Updater.InstallInteractive(this, this, delegate (bool busy)
                {
                    _bar.Install.Enabled = _bar.Page.Enabled = _bar.Later.Enabled = !busy;
                    if (busy) _bar.Text = "Lade Version " + Updater.Available.Version + " …";
                    else ShowUpdateBar(true);
                    _bar.Invalidate();
                });
            };
            _bar.Page.Click += delegate { Updater.OpenPage(); };
            _bar.Later.Click += delegate { ShowUpdateBar(false); };
            Controls.Add(_bar);

            int top = S(88);
            // Navigation links (eine Seite je Bereich, Vorlage: Wake on LAN / Monitor Dimmer), Inhalt rechts daneben.
            // Angepinnt schiebt sie den Inhalt, sonst liegt sie ausgeklappt über dem Inhalt.
            int pageH = S(426);
            int contentX = _navSpace + S(28);
            int colW = S(ContentW);
            _nav = new NavList(_sc);
            _nav.Pinned = _original.NavPinned;
            _nav.Bounds = new Rectangle(0, top, _navSpace, ClientSize.Height - S(64) - top);
            Controls.Add(_nav);

            // --- Seiten (je eine Methode weiter unten)
            ToolTip tip = new ModernToolTip();
            Card pos = PagePosition(tip, contentX, top, colW, pageH);
            Card docks = PageDocks(tip, contentX, top, colW, pageH);
            Card icons = PageSymbole(tip, contentX, top, colW, pageH);
            Card zoomCard = PageZoom(tip, contentX, top, colW, pageH);
            Card effects = PageEffekte(tip, contentX, top, colW, pageH);
            Card label = PageBeschriftung(tip, contentX, top, colW, pageH);
            Card behave = PageVerhalten(tip, contentX, top, colW, pageH);
            Card general = PageAllgemein(tip, contentX, top, colW, pageH);

            _pages = new Card[] { pos, docks, icons, zoomCard, effects, label, behave, general };
            _nav.Add('\uE7F4', "Position");
            _nav.Add('\uE71D', "Weitere Docks");
            _nav.Add('\uE8A9', "Symbole");
            _nav.Add('\uE8A3', "Zoom");
            _nav.Add('\uE771', "Effekte");
            _nav.Add('\uE8D2', "Beschriftung");
            _nav.Add('\uE713', "Verhalten");
            _nav.Add('\uE80F', "Allgemein");
            _nav.SelectedChanged += delegate { ShowPage(_nav.SelectedIndex); CloseNav(); };
            _nav.MenuClicked += delegate
            {
                if (_original.NavPinned) SetNavPinned(false);
                else { _navOpen = !_navOpen; ApplyNav(); }
            };
            _nav.PinClicked += delegate { SetNavPinned(!_original.NavPinned); };
            _nav.ExtrasClicked += delegate { ShowExtrasMenu(); };
            _navTimer.Tick += delegate
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt
                if (!_navOpen || _original.NavPinned || !Visible) return;
                if (_extrasMenu != null && !_extrasMenu.IsDisposed && _extrasMenu.Visible) return; // Extras-Menü offen
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) { _navOpen = false; ApplyNav(); }
            };
            _navTimer.Start();
            // nicht angepinnt: verweilt die Maus kurz auf der eingeklappten Navigation, klappt sie aus (Namen sichtbar)
            // und beim Verlassen wieder ein – ☰ funktioniert weiterhin
            _nav.MouseEnter += delegate { if (!_original.NavPinned && !_navOpen) _navHover.Start(); };
            _nav.MouseLeave += delegate { _navHover.Stop(); };
            _navHover.Tick += delegate
            {
                _navHover.Stop();
                if (_original.NavPinned || _navOpen || !Visible) return;
                if (_extrasMenu != null && !_extrasMenu.IsDisposed && _extrasMenu.Visible) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) return;
                _navOpen = true;
                ApplyNav();
            };
            ShowPage(0);

            // --- Schaltflächen
            int by = top + pageH + S(24);
            int right = contentX + colW;
            _btnCancel = MakeButton("Abbrechen", false, new Rectangle(right - S(120) - S(10) - S(120), by, S(120), S(34)));
            _btnSave = MakeButton("Speichern", true, new Rectangle(right - S(120), by, S(120), S(34)));
            _error = MakeLabel("", 12.5f, false);
            _error.AutoSize = false;
            _error.AutoEllipsis = true;
            _error.TextAlign = ContentAlignment.MiddleLeft;
            _error.Tag = "error";
            _error.Bounds = new Rectangle(contentX, by, _btnCancel.Left - S(12) - contentX, S(34));
            Controls.Add(_error);
            _content = new Control[] { pos, docks, icons, zoomCard, effects, label, behave, general, _btnCancel, _btnSave, _error };
            _btnCancel.Click += delegate { _discard = true; Close(); }; // Abbrechen verwirft ohne Rückfrage
            _btnSave.Click += delegate { Save(); };

            // --- Fußzeile (einheitlich in allen Projekten)
            SignatureFooter footer = new SignatureFooter(_sc);
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(64);
            Controls.Add(footer);
            _nav.BringToFront();

            _showDelay.ValueChanged += delegate { _showValue.Text = _showDelay.Value * 50 + " ms"; };
            _hideDelay.ValueChanged += delegate { _hideValue.Text = _hideDelay.Value * 50 + " ms"; };
            _autoHide.CheckedChanged += delegate { UpdateEnabled(); };
            _onTop.CheckedChanged += delegate { UpdateEnabled(); };
            _raise.CheckedChanged += delegate { UpdateEnabled(); };
            _iconSize.ValueChanged += delegate { if (_zoomSize.Value < _iconSize.Value) _zoomSize.Value = _iconSize.Value; };
            _zoomSize.ValueChanged += delegate { if (_zoomSize.Value < _iconSize.Value) _iconSize.Value = _zoomSize.Value; };
        }

        /// <summary>Seite: Position</summary>
        private Card PagePosition(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card pos = MakeCard("Position", new Rectangle(contentX, top, colW, S(300)));
            _monitor = MakeChoice(S(176));
            _monitor.Choices = MonitorNames();
            AddRow(pos, "Bildschirm", _monitor, S(54));
            _edge = MakeChoice(S(132));
            _edge.Choices = new[] { "Oben", "Unten", "Links", "Rechts" };
            AddRow(pos, "Bildschirmrand", _edge, S(96));
            _edgeDist = MakeStepper(0, 400, "px");
            AddRow(pos, "Abstand zum Rand", _edgeDist, S(138));
            _offset = MakeStepper(-100, 100, "%");
            Label offLabel = AddRow(pos, "Zentrierung", _offset, S(180));
            tip.SetToolTip(offLabel, "Lage des Docks am Bildschirmrand: -100 % ganz links bzw. oben,\n0 % Mitte, 100 % ganz rechts bzw. unten.");
            _onTop = MakeToggle();
            Label topLabel = AddRow(pos, "Immer im Vordergrund", _onTop, S(226));
            tip.SetToolTip(topLabel, "Aus: Fenster dürfen das Dock überdecken.");
            _raise = MakeToggle();
            Label raiseLabel = AddRow(pos, "Maus holt Dock nach vorne", _raise, S(262));
            tip.SetToolTip(raiseLabel, "Nur wenn „Immer im Vordergrund“ aus ist: Bleibt die Maus über dem Dock\nbzw. am Bildschirmrand davor, kommt es nach „Einblenden nach“ nach vorne.");
            return pos;
        }

        /// <summary>Seite: Weitere Docks (Dock 2 und 3 mit eigenen Einträgen, eigener Position und Symbolgröße)</summary>
        private Card PageDocks(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card docks = MakeCard("Weitere Docks", new Rectangle(contentX, top, colW, S(388)));
            _dockPick = MakeChoice(S(132));
            _dockPick.Choices = new[] { "Dock 2", "Dock 3" };
            _dockPick.SelectedIndex = 0;
            Label pickLabel = AddRow(docks, "Dock", _dockPick, S(54));
            tip.SetToolTip(pickLabel, "Neben dem ersten Dock gibt es bis zu zwei weitere – z. B. für einen anderen Bildschirm.\nJedes hat eigene Einträge, eine eigene Position und Symbolgröße.\nEffekte, Beschriftung und Verhalten gelten für alle Docks gemeinsam.");
            _dockPick.SelectionChanged += delegate
            {
                if (_extraLoading || _dockPick.SelectedIndex == _extraShown) return;
                StoreExtra();
                ShowExtra(_dockPick.SelectedIndex);
            };
            _xEnabled = MakeToggle();
            Label onLabel = AddRow(docks, "Dieses Dock anzeigen", _xEnabled, S(96));
            tip.SetToolTip(onLabel, "Aus: Das Dock ist ausgeblendet, seine Einträge bleiben erhalten.");
            _xEnabled.CheckedChanged += delegate
            {
                // Läge das neue Dock genau auf einem anderen (gleicher Bildschirm, gleicher Rand), sähe man es nicht:
                // dann einen freien Rand vorschlagen
                if (!_extraLoading && _xEnabled.Checked) FreeEdgeForExtra();
                UpdateExtraEnabled();
            };
            _xMonitor = MakeChoice(S(176));
            _xMonitor.Choices = MonitorNames();
            AddRow(docks, "Bildschirm", _xMonitor, S(138));
            _xEdge = MakeChoice(S(132));
            _xEdge.Choices = new[] { "Oben", "Unten", "Links", "Rechts" };
            AddRow(docks, "Bildschirmrand", _xEdge, S(180));
            _xDist = MakeStepper(0, 400, "px");
            AddRow(docks, "Abstand zum Rand", _xDist, S(222));
            _xOffset = MakeStepper(-100, 100, "%");
            Label offLabel = AddRow(docks, "Zentrierung", _xOffset, S(264));
            tip.SetToolTip(offLabel, "Lage des Docks am Bildschirmrand: -100 % ganz links bzw. oben,\n0 % Mitte, 100 % ganz rechts bzw. unten.");
            _xIcon = MakeStepper(16, 128, "px");
            AddRow(docks, "Symbolgröße", _xIcon, S(306));
            _xZoom = MakeStepper(16, 256, "px");
            Label zoomLabel = AddRow(docks, "Größe unter der Maus", _xZoom, S(348));
            tip.SetToolTip(zoomLabel, "Gilt bei den Zoom-Effekten Automatisch, Blase und Lupe.");
            _xIcon.ValueChanged += delegate { if (!_extraLoading && _xZoom.Value < _xIcon.Value) _xZoom.Value = _xIcon.Value; };
            _xZoom.ValueChanged += delegate { if (!_extraLoading && _xZoom.Value < _xIcon.Value) _xIcon.Value = _xZoom.Value; };
            return docks;
        }

        /// <summary>Seite: Symbole (Symbole und Aussehen der Leiste)</summary>
        private Card PageSymbole(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card icons = MakeCard("Symbole", new Rectangle(contentX, top, colW, S(430)));
            _iconSize = MakeStepper(16, 128, "px");
            AddRow(icons, "Symbolgröße", _iconSize, S(54));
            _spacing = MakeStepper(0, 30, "px");
            Label spLabel = AddRow(icons, "Abstand zwischen Symbolen", _spacing, S(96));
            tip.SetToolTip(spLabel, "Passen nicht alle Einträge auf den Bildschirm, verkleinert Dock Pilot die Symbole.\nEin kleiner Abstand lässt mehr Platz für die Symbole.");
            _running = MakeToggle();
            AddRow(icons, "Laufende Programme markieren", _running, S(138));
            _unpinned = MakeToggle();
            Label upLabel = AddRow(icons, "Laufende ohne Eintrag anzeigen", _unpinned, S(180));
            tip.SetToolTip(upLabel, "Wie in der Taskleiste: Laufende Programme, die keinen Eintrag im Dock haben,\nerscheinen vorübergehend am Ende. Rechtsklick > „Im Dock behalten“ macht daraus einen festen Eintrag.");
            _preview = MakeToggle();
            Label pvLabel = AddRow(icons, "Fenstervorschau", _preview, S(222));
            tip.SetToolTip(pvLabel, "Wie in der Taskleiste: Bleibt die Maus kurz auf einem laufenden Programm,\nerscheinen seine Fenster als Vorschaubilder. Klick holt ein Fenster nach vorne, × schließt es.");
            _reflection = MakeToggle();
            Label rfLabel = AddRow(icons, "Spiegelung", _reflection, S(264));
            tip.SetToolTip(rfLabel, "Dezente Spiegelung der Symbole zum Bildschirmrand hin (wie bei RocketDock).");
            _shelfVisible = MakeToggle();
            _shelfVisible.CheckedChanged += delegate { if (_raise != null) UpdateEnabled(); };
            Label svLabel = AddRow(icons, "Leiste anzeigen", _shelfVisible, S(306));
            tip.SetToolTip(svLabel, "Aus: kein Hintergrund, nur die Symbole.");
            _shelfOpacity = MakeStepper(10, 100, "%");
            _shelfOpacity.Step = 5;
            AddRow(icons, "Deckkraft der Leiste", _shelfOpacity, S(348));
            _shelfRadius = MakeStepper(0, 40, "px");
            Label srLabel = AddRow(icons, "Eckenradius der Leiste", _shelfRadius, S(390));
            tip.SetToolTip(srLabel, "0 = eckig. Standard: 8 px wie Fenster und Menüs in Windows 11.");
            return icons;
        }

        /// <summary>Seite: Zoom</summary>
        private Card PageZoom(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card zoomCard = MakeCard("Zoom", new Rectangle(contentX, top, colW, S(388)));
            _zoomMode = MakeChoice(S(150));
            _zoomMode.SelectionChanged += delegate { if (_raise != null) UpdateEnabled(); };
            _zoomMode.Choices = new[] { "Automatisch", "Blase", "Weiche Blase", "Lupe", "Anheben", "Pulsieren", "Aus" };
            Label zmLabel = AddRow(zoomCard, "Zoom-Effekt", _zoomMode, S(54));
            tip.SetToolTip(zmLabel, "Wie die Symbole unter der Maus größer werden.\nBlase: die Nachbarn rücken zur Seite.\nWeiche Blase: wie Blase, aber die Nachbarn wachsen kaum mit.\nLupe: vergrößert die Symbole unter der Maus, ohne die anderen zu verschieben.\nAutomatisch: Blase – oder Lupe, sobald das Dock mehr als zwei Drittel des Rands einnimmt.\nAnheben: nur das Symbol unter der Maus wird etwas größer.\nPulsieren: das Symbol wird sanft größer und kleiner.\nAus: keine Vergrößerung.\nDazu lässt sich auf der Seite „Effekte“ eine Hervorhebung wählen.");
            _zoomSize = MakeStepper(16, 256, "px");
            AddRow(zoomCard, "Größe unter der Maus", _zoomSize, S(96));
            _zoomWidth = MakeStepper(1, 6, null);
            Label zwLabel = AddRow(zoomCard, "Vergrößerte Nachbarn", _zoomWidth, S(138));
            tip.SetToolTip(zwLabel, "Wie viele Symbole links und rechts der Maus mitwachsen.");
            _keepWidth = MakeToggle();
            _keepWidth.CheckedChanged += delegate { if (_raise != null) UpdateEnabled(); };
            Label kwLabel = AddRow(zoomCard, "Leiste bleibt gleich breit", _keepWidth, S(180));
            tip.SetToolTip(kwLabel, "Nur bei Blase und Weiche Blase: Die Leiste wird nicht breiter.\nDie übrigen Symbole machen dafür Platz (wie unter einer Lupe).");
            _keepMode = MakeChoice(S(150));
            _keepMode.Choices = new[] { "Schrumpfen", "Überlappen" };
            Label kmLabel = AddRow(zoomCard, "Platz schaffen durch", _keepMode, S(222));
            tip.SetToolTip(kmLabel, "Schrumpfen: die übrigen Symbole werden kleiner.\nÜberlappen: sie bleiben gleich groß und schieben sich übereinander.");
            _minShrink = MakeStepper(50, 100, "%");
            _minShrink.Step = 5;
            Label msLabel = AddRow(zoomCard, "Übrige Symbole mindestens", _minShrink, S(264));
            tip.SetToolTip(msLabel, "So groß (Schrumpfen) bzw. so weit sichtbar (Überlappen) bleiben die übrigen Symbole.\nBei 100 % wächst die Blase nur so weit, wie Platz ist (Reserve).");
            _reserve = MakeStepper(0, 200, "px");
            _reserve.Step = 4;
            Label rsLabel = AddRow(zoomCard, "Reserve an den Enden", _reserve, S(306));
            tip.SetToolTip(rsLabel, "Die Leiste ist an beiden Enden um diesen Wert länger.\nDie Blase nutzt zuerst diesen Platz, erst danach geben die übrigen Symbole Platz her.");
            _edgeGrow = MakeToggle();
            Label egLabel = AddRow(zoomCard, "Am Ende hinauswachsen", _edgeGrow, S(348));
            tip.SetToolTip(egLabel, "Am ersten und letzten Symbol wächst die Leiste ein Stück über ihr Ende hinaus,\nstatt die Blase nach innen zu schieben.");
            return zoomCard;
        }

        /// <summary>Seite: Effekte (Hervorhebung zusätzlich zum Zoom-Effekt, mit Vorschau)</summary>
        private Card PageEffekte(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card effects = MakeCard("Effekte", new Rectangle(contentX, top, colW, S(388)));
            _highlight = MakeChoice(S(150));
            _highlight.SelectionChanged += delegate { if (_raise != null) UpdateEnabled(); };
            _highlight.Choices = new[] { "Keine", "Leuchten", "Sockel", "Unterstrich", "Einfärben", "Kippen", "Wackeln", "Federn", "Fokus", "Entsättigen" };
            Label hlLabel = AddRow(effects, "Hervorhebung", _highlight, S(54));
            tip.SetToolTip(hlLabel, "Hebt das Symbol unter der Maus hervor – zusätzlich zum Zoom-Effekt.\nLeuchten: weicher Schein in Akzentfarbe hinter dem Symbol.\nSockel: helle Fläche hinter dem Symbol, wie in der Taskleiste.\nUnterstrich: ein Strich in Akzentfarbe wächst unter dem Symbol.\nEinfärben: ein Hauch Akzentfarbe auf dem Symbol.\nKippen: das Symbol neigt sich zur Maus.\nWackeln: kurzes Schütteln beim Überfahren.\nFedern: das Symbol hüpft beim Überfahren kurz hoch.\nFokus: weiter entfernte Symbole werden nach kurzem Verweilen blasser.\nEntsättigen: wie Fokus, die entfernten Symbole werden grau statt blass.");
            _strength = MakeStepper(20, 200, "%");
            _strength.Step = 10;
            Label stLabel = AddRow(effects, "Stärke", _strength, S(96));
            tip.SetToolTip(stLabel, "Wie kräftig die Hervorhebung und das Pulsieren ausfallen. Standard: 100 %.");
            Label pvTitle = MakeLabel("Vorschau", 13.5f, false);
            effects.Controls.Add(pvTitle);
            pvTitle.Location = new Point(S(18), S(142));
            tip.SetToolTip(pvTitle, "Zeigt Zoom-Effekt und Hervorhebung mit den ersten Einträgen des Docks –\nsofort, ohne zu speichern. Auch das echte Dock zeigt Änderungen sofort.");
            _effectPreview = new EffectPreview();
            _effectPreview.Font = Font;
            _effectPreview.Bounds = new Rectangle(S(12), S(170), colW - S(24), S(206));
            effects.Controls.Add(_effectPreview);
            return effects;
        }

        /// <summary>Seite: Beschriftung</summary>
        private Card PageBeschriftung(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card label = MakeCard("Beschriftung", new Rectangle(contentX, top, colW, S(380)));
            _labels = MakeToggle();
            AddRow(label, "Beschriftung anzeigen", _labels, S(54));
            _labelFont = MakeChoice(S(240));
            _fontNames = new List<string>();
            try
            {
                foreach (FontFamily fam in FontFamily.Families)
                    if (!fam.Name.StartsWith("@") && (fam.IsStyleAvailable(FontStyle.Regular) || fam.IsStyleAvailable(FontStyle.Bold)))
                        _fontNames.Add(fam.Name);
            }
            catch { }
            _fontNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            List<string> fontChoices = new List<string>();
            fontChoices.Add("Windows-Standard");
            fontChoices.AddRange(_fontNames);
            _labelFont.Choices = fontChoices.ToArray();
            Label lfLabel = AddRow(label, "Schriftart", _labelFont, S(92), S(40));
            tip.SetToolTip(lfLabel, "Windows-Standard: " + Theme.FontFamilyName + " – die Schrift von Windows 11 (unter Windows 10 Segoe UI).");
            _labelSize = MakeStepper(9, 32, "px");
            AddRow(label, "Schriftgröße", _labelSize, S(134));
            _labelBold = MakeToggle();
            AddRow(label, "Fett", _labelBold, S(178));
            _labelColor = MakeChoice(S(150));
            _labelColor.Choices = new[] { "Weiß", "Schwarz", "Wie Windows", "Akzentfarbe", "Eigene Farbe" };
            Label lcLabel = AddRow(label, "Schriftfarbe", _labelColor, S(216), S(40));
            tip.SetToolTip(lcLabel, "Wie Windows: im hellen Modus dunkel, im dunklen Modus hell.");
            _labelSwatch = new LabelSwatch(_sc);
            _labelSwatch.Size = new Size(S(32), S(32));
            _labelSwatch.Location = new Point(label.Width - S(18) - _labelSwatch.Width, _labelColor.Top);
            label.Controls.Add(_labelSwatch);
            _labelSwatch.Click += delegate { PickLabelColor(); };
            _labelColor.SelectionChanged += delegate
            {
                if (_labelColor.SelectedIndex == 4 && _labelCustom == Color.Empty && Visible) PickLabelColor();
                UpdateSwatch();
            };
            _labelShadow = MakeSlider();
            _labelShadow.Maximum = 10; // 0 – 100 % in Schritten von 10 %
            _labelShadowValue = MakeValueLabel();
            Label shLabel = AddRow(label, "Schattenstärke", _labelShadow, S(266), S(64));
            PlaceValue(label, _labelShadowValue, S(266));
            tip.SetToolTip(shLabel, "Wie kräftig der weiche Schatten hinter der Schrift ist –\ner hält sie auf hellen und dunklen Hintergründen lesbar. 0 % = kein Schatten.");
            _labelShadowSize = MakeStepper(1, 8, "px");
            Label ssLabel = AddRow(label, "Schattengröße", _labelShadowSize, S(306));
            tip.SetToolTip(ssLabel, "Wie weit der Schatten um die Schrift herum reicht. Standard: 3 px.");
            _labelShadow.ValueChanged += delegate { _labelShadowValue.Text = _labelShadow.Value * 10 + " %"; if (_raise != null && _labelBold != null) _labelShadowSize.Enabled = _labels.Checked && _labelShadow.Value > 0; };
            _labels.CheckedChanged += delegate { UpdateEnabled(); };
            return label;
        }

        /// <summary>Seite: Verhalten</summary>
        private Card PageVerhalten(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card behave = MakeCard("Verhalten", new Rectangle(contentX, top, colW, pageH));
            _autoHide = MakeToggle();
            AddRow(behave, "Automatisch ausblenden", _autoHide, S(54));
            _showDelay = MakeSlider();
            _showValue = MakeValueLabel();
            AddRow(behave, "Einblenden nach", _showDelay, S(98), S(64));
            PlaceValue(behave, _showValue, S(98));
            _hideDelay = MakeSlider();
            _hideValue = MakeValueLabel();
            AddRow(behave, "Ausblenden nach", _hideDelay, S(140), S(64));
            PlaceValue(behave, _hideValue, S(140));
            _fullscreen = MakeToggle();
            Label fsLabel = AddRow(behave, "Bei Vollbild ausblenden", _fullscreen, S(184));
            tip.SetToolTip(fsLabel, "Läuft auf diesem Bildschirm ein Spiel oder Video im Vollbild,\nbleibt das Dock verborgen (Tastenkürzel holt es trotzdem).");
            _activate = MakeToggle();
            Label actLabel = AddRow(behave, "Laufende nach vorne holen", _activate, S(226));
            tip.SetToolTip(actLabel, "Läuft ein Programm schon, holt ein Klick sein Fenster nach vorne\n(wie in der Taskleiste). Umschalt + Klick startet es trotzdem neu.");
            _locked = MakeToggle();
            AddRow(behave, "Einträge sperren", _locked, S(268));
            _anim = MakeChoice(S(132));
            _anim.Choices = new[] { "Aus", "Schnell", "Normal", "Langsam" };
            Label animLabel = AddRow(behave, "Animationen", _anim, S(312));
            tip.SetToolTip(animLabel, "Tempo von Vergrößerung, Ein- und Ausblenden und Hüpfen.");
            _hotkey = new ComboHotkeyBox();
            _hotkey.Size = new Size(S(176), S(32));
            Label hkLabel = AddRow(behave, "Tastenkürzel", _hotkey, S(362));
            tip.SetToolTip(hkLabel, "Blendet das Dock ein und aus. Feld anklicken und Tasten drücken,\nEntf löscht das Tastenkürzel.");
            return behave;
        }

        /// <summary>Seite: Allgemein</summary>
        private Card PageAllgemein(ToolTip tip, int contentX, int top, int colW, int pageH)
        {
            Card general = MakeCard("Allgemein", new Rectangle(contentX, top, colW, S(256)));
            _autostart = MakeToggle();
            AddRow(general, "Mit Windows starten", _autostart, S(54));
            ModernButton import = new ModernButton();
            import.Text = "Importieren …";
            import.Size = new Size(S(130), S(30));
            Label impLabel = AddRow(general, "RocketDock übernehmen", import, S(96));
            ModernButton data = new ModernButton();
            data.Text = "Öffnen";
            data.Size = new Size(S(130), S(30));
            Label dataLabel = AddRow(general, "Datenordner", data, S(146));
            tip.SetToolTip(dataLabel, "Einstellungen, Einträge (Dock.ini) und eigene Symbole.");
            data.Click += delegate { Shell.Open(Settings.DataDirectory, "", ""); };
            ModernButton pack = new ModernButton();
            pack.Text = "Öffnen";
            pack.Size = new Size(S(130), S(30));
            Label packLabel = AddRow(general, "Eigene Symbole", pack, S(196));
            tip.SetToolTip(packLabel, "Ordner „Symbole“: Ein Bild (PNG oder ICO), das wie der Eintrag oder wie sein Programm heißt\n(z. B. „Editor.png“ oder „notepad.ico“), ersetzt das Programmsymbol. Gilt nach einem Neustart von Dock Pilot.\nHier liegen auch die aus RocketDock übernommenen Symbole.");
            pack.Click += delegate
            {
                try { Directory.CreateDirectory(Settings.IconDirectory); } catch { }
                Shell.Open(Settings.IconDirectory, "", "");
            };
            tip.SetToolTip(impLabel, "Einträge, Ordnermenüs und Einstellungen aus der Settings.ini\nvon RocketDock übernehmen.");
            import.Click += delegate
            {
                if (ImportRocketDock != null && ImportRocketDock(this))
                {
                    // Import übernimmt auch Größen und Verhalten – Anzeige auf den neuen Stand bringen
                    _edit.CopyFrom(_original);
                    LoadValues(false);
                    _baseline = Snapshot();
                }
            };
            return general;
        }

        private static string[] MonitorNames()
        {
            Screen[] all = Screen.AllScreens;
            string[] names = new string[all.Length + 1];
            names[0] = "Hauptbildschirm";
            for (int i = 0; i < all.Length; i++)
                names[i + 1] = "Bildschirm " + (i + 1) + " · " + all[i].Bounds.Width + " × " + all[i].Bounds.Height + (all[i].Primary ? " (Haupt)" : "");
            return names;
        }

        private Card MakeCard(string title, Rectangle bounds)
        {
            Card c = new Card();
            c.Title = title;
            c.UiScale = _sc;
            c.Bounds = bounds;
            Controls.Add(c);
            return c;
        }

        private Label MakeLabel(string text, float size, bool sub)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = Theme.UiFont(size * _sc, FontStyle.Regular);
            l.Tag = sub ? "sub" : null;
            l.UseMnemonic = false;
            return l;
        }

        private Label MakeValueLabel()
        {
            Label l = MakeLabel("0 ms", 13f, true);
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.Size = new Size(S(64), S(24));
            return l;
        }

        private void PlaceValue(Card card, Label value, int y)
        {
            value.Location = new Point(card.Width - S(18) - value.Width, y);
            card.Controls.Add(value);
        }

        private ChoiceBox MakeChoice(int width)
        {
            ChoiceBox c = new ChoiceBox();
            c.Size = new Size(width, S(32));
            return c;
        }

        private Stepper MakeStepper(int min, int max, string suffix)
        {
            Stepper s = new Stepper();
            s.Minimum = min;
            s.Maximum = max;
            s.Suffix = suffix;
            s.Size = new Size(S(132), S(32));
            return s;
        }

        private Slider MakeSlider()
        {
            Slider s = new Slider();
            s.Minimum = 0;
            s.Maximum = 40; // in Schritten von 50 ms: 0 – 2000 ms
            s.Size = new Size(S(120), S(24));
            return s;
        }

        private ToggleSwitch MakeToggle()
        {
            ToggleSwitch t = new ToggleSwitch();
            t.Size = new Size(S(44), S(22));
            return t;
        }

        private ModernButton MakeButton(string text, bool primary, Rectangle bounds)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.Primary = primary;
            b.Bounds = bounds;
            Controls.Add(b);
            return b;
        }

        private Label AddRow(Card card, string text, Control control, int y)
        {
            return AddRow(card, text, control, y, 0);
        }

        private Label AddRow(Card card, string text, Control control, int y, int rightReserve)
        {
            int rowH = Math.Max(control.Height, S(24));
            Label l = MakeLabel(text, 13.5f, false);
            card.Controls.Add(l);
            l.Location = new Point(S(18), y + (rowH - l.PreferredHeight) / 2);
            control.Location = new Point(card.Width - S(18) - rightReserve - control.Width, y + (rowH - control.Height) / 2);
            card.Controls.Add(control);
            l.Click += delegate
            {
                ToggleSwitch t = control as ToggleSwitch;
                if (t != null && t.Enabled) t.Checked = !t.Checked;
                else control.Focus();
            };
            return l;
        }
    }
}
