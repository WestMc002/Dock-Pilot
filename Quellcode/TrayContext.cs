// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DockPilot
{
    /// <summary>Startet das Dock und das Tray-Symbol (Einstellungen, Updates, Beenden).</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        public const string ShowSettingsEventName = "DockPilot_ShowSettings";

        private readonly Settings _settings;
        private readonly DockWindow _dock;
        private readonly NotifyIcon _tray;
        private readonly Icon _appIcon;
        private readonly Icon _trayIcon;
        private readonly EventWaitHandle _showEvent;
        private readonly RegisteredWaitHandle _showWait;
        private readonly Control _invoker;
        private readonly System.Windows.Forms.Timer _updateTimer;
        private SettingsForm _form;
        private readonly ContextMenuStrip _trayMenu;
        private bool _balloonIsUpdate;
        private string _notifiedVersion;

        public TrayContext(bool afterUpdate)
        {
            _invoker = new Control();
            IntPtr h = _invoker.Handle;
            GC.KeepAlive(h);

            Theme.Accent = Theme.SystemAccent;
            _settings = Settings.Load();
            _appIcon = LoadIcon(32);
            _trayIcon = LoadIcon(SystemInformation.SmallIconSize.Width);
            Autostart.UpdateMovedPath();

            // Erster Start: RocketDock gefunden? Dann anbieten, alles zu übernehmen
            List<DockItem> items = DockStore.Load();
            bool firstStart = !DockStore.Exists;
            if (firstStart)
            {
                string rd = RocketDockImport.Find();
                if (rd != null && MessageBox.Show(
                    "RocketDock ist auf diesem PC installiert.\n\nAlle Einträge, Ordnermenüs und Einstellungen aus RocketDock übernehmen?",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    List<DockItem> imported = RunImport(rd, null);
                    if (imported != null) items = imported;
                }
                if (items.Count == 0) items = DefaultItems();
                DockStore.Save(items);
            }

            _dock = new DockWindow(_settings, items);
            _dock.OpenSettings = ShowSettings;
            _dock.ShowAbout = delegate { AboutDialog.ShowFor(VisibleForm); };
            _dock.ExitApp = ExitApp;
            _dock.Show();

            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = AppInfo.Name;
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Opening += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                BuildTrayMenu(); // jedes Mal frisch: Design, Akzentfarbe und Zustände aktuell
                e.Cancel = false;
            };
            _trayMenu.Opened += delegate { MenuHelper.RoundCorners(_trayMenu); };
            _tray.ContextMenuStrip = _trayMenu;
            _tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowSettings();
            };
            _tray.Visible = true;

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, delegate
            {
                try { _invoker.BeginInvoke((MethodInvoker)ShowSettings); } catch { }
            }, null, -1, false);

            if (_settings.FirstRun)
            {
                _settings.FirstRun = false;
                _settings.Save();
                _tray.ShowBalloonTip(7000, "Dock Pilot läuft",
                    "Rechtsklick ins Dock: Einträge hinzufügen, bearbeiten und Einstellungen. Programme und Ordner lassen sich aus dem Explorer ins Dock ziehen.",
                    ToolTipIcon.Info);
            }
            if (!_dock.HotkeyOk && _settings.HotkeyKey > 0)
                _tray.ShowBalloonTip(6000, "Tastenkürzel belegt",
                    ComboHotkeyBox.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey) + " nutzt schon ein anderes Programm (z. B. noch RocketDock?).",
                    ToolTipIcon.Warning);
            WarnIfRocketDockRunning();

            // ---------- Updates (Vorlage: WindowGrid / Fensterpositionen wiederherstellen) ----------
            if (afterUpdate)
            {
                string result = Updater.LastResult();
                if (result != null && result.StartsWith("OK"))
                    _tray.ShowBalloonTip(6000, "Dock Pilot aktualisiert", "Version " + result.Substring(2).Trim() + " ist installiert.", ToolTipIcon.Info);
                else if (result != null)
                    _tray.ShowBalloonTip(8000, "Update fehlgeschlagen",
                        result.Replace("FEHLER", "").Trim() + " – die bisherige Version läuft weiter.", ToolTipIcon.Warning);
            }
            AboutDialog.LiveSettings = _settings;
            AboutDialog.StartInstall = delegate
            {
                _invoker.BeginInvoke((MethodInvoker)delegate { Updater.InstallInteractive(VisibleForm, _invoker, null); });
            };
            Updater.AvailableChanged += delegate { NotifyUpdate(); };
            Updater.ExitRequested += delegate { ExitApp(); };
            _tray.BalloonTipClicked += delegate
            {
                if (_balloonIsUpdate) Updater.InstallInteractive(VisibleForm, _invoker, null);
            };
            _tray.BalloonTipClosed += delegate { _balloonIsUpdate = false; };
            // 20 Sekunden nach dem Start, danach alle 6 Stunden nachsehen, ob die Prüfung fällig ist (höchstens alle 20 Stunden)
            _updateTimer = new System.Windows.Forms.Timer();
            _updateTimer.Interval = 20000;
            _updateTimer.Tick += delegate
            {
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                if (Updater.Due(_settings)) Updater.CheckAsync(_settings, _invoker, false, null);
            };
            _updateTimer.Start();
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        }

        private Form VisibleForm
        {
            get { return _form != null && !_form.IsDisposed && _form.Visible ? _form : null; }
        }

        private static List<DockItem> DefaultItems()
        {
            List<DockItem> list = new List<DockItem>();
            list.Add(new DockItem { Title = "Explorer", Command = "explorer.exe", Icon = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\explorer.exe") });
            list.Add(new DockItem { Title = "Dokumente", Command = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) });
            list.Add(new DockItem { Kind = ItemKind.Trennlinie });
            list.Add(new DockItem { Title = "Papierkorb", Command = "::{645FF040-5081-101B-9F08-00AA002F954E}" });
            return list;
        }

        internal static Icon LoadIcon(int size)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dock Pilot.ico"))
                {
                    if (s != null) return new Icon(s, size, size);
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            return SystemIcons.Application;
        }

        // ------------------------------------------------------------------ RocketDock

        /// <summary>Import ausführen und Ergebnis melden. Rückgabe: neue Einträge oder null (abgebrochen/Fehler).</summary>
        private List<DockItem> RunImport(string path, IWin32Window owner)
        {
            try
            {
                RocketDockImport.Result r = RocketDockImport.Import(path, _settings);
                _settings.Save();
                string msg = "Übernommen: " + r.Programs + " Programme, " + r.Menus + " Ordnermenüs und " + r.Separators + " Trennlinien" +
                             (r.IconsCopied > 0 ? ", " + r.IconsCopied + " eigene Symbole" : "") + ".\n\n" +
                             "Größen, Position, automatisches Ausblenden und das Tastenkürzel wurden ebenfalls übernommen. " +
                             "RocketDock bitte beenden und aus dem Autostart nehmen, sonst laufen beide Docks.";
                if (r.Notes.Count > 0) msg += "\n\nHinweise:\n• " + string.Join("\n• ", r.Notes.ToArray());
                MessageBox.Show(owner, msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return r.Items;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "RocketDock konnte nicht übernommen werden.\n\n" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        /// <summary>Import von Hand (Einstellungen oder Tray-Menü): Datei wählen, bei vorhandenen Einträgen nachfragen.</summary>
        private bool ImportInteractive(Form owner)
        {
            string path = RocketDockImport.Ask(owner);
            if (path == null) return false;
            List<DockItem> current = _dock.Items;
            bool append = false;
            if (current.Count > 0)
            {
                DialogResult d = MessageBox.Show(owner,
                    "Das Dock enthält schon " + current.Count + " Einträge.\n\n„Ja“ ersetzt sie durch die Einträge aus RocketDock, „Nein“ hängt die RocketDock-Einträge hinten an.\n\nVorhandene Einträge ersetzen?",
                    AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (d == DialogResult.Cancel) return false;
                append = d == DialogResult.No;
            }
            List<DockItem> items = RunImport(path, owner);
            if (items == null) return false;
            if (append)
            {
                List<DockItem> all = new List<DockItem>(current);
                all.Add(new DockItem { Kind = ItemKind.Trennlinie });
                all.AddRange(items);
                items = all;
            }
            DockStore.Save(items);
            _dock.SetItems(items);
            return true;
        }

        /// <summary>Sicherung auswählen und einspielen: ersetzt Einträge und Einstellungen und übernimmt sie sofort.</summary>
        private bool ImportBackup(Form owner)
        {
            string file;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Sicherung wiederherstellen";
                dlg.Filter = Backup.Filter;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                file = dlg.FileName;
            }
            if (MessageBox.Show(owner,
                "Die Sicherung ersetzt alle Einträge und Einstellungen von Dock Pilot.\n\nSicherung jetzt wiederherstellen?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            string err = Backup.Import(file);
            if (err != null)
            {
                MessageBox.Show(owner, "Die Sicherung konnte nicht wiederhergestellt werden.\n\n" + err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            // Zustände dieses PCs behalten (nicht Teil der Einstellungen eines anderen PCs)
            bool navPinned = _settings.NavPinned;
            string lastCheck = _settings.LastUpdateCheck;
            Settings loaded = Settings.Load();
            loaded.FirstRun = false;
            loaded.NavPinned = navPinned;
            loaded.LastUpdateCheck = lastCheck;
            _settings.CopyFrom(loaded);
            _settings.Save();
            _dock.SetItems(DockStore.Load());
            _dock.ApplySettings();
            return true;
        }

        private void WarnIfRocketDockRunning()
        {
            try
            {
                if (Process.GetProcessesByName("RocketDock").Length > 0)
                    _tray.ShowBalloonTip(8000, "RocketDock läuft noch",
                        "Bitte RocketDock beenden und aus dem Autostart nehmen, sonst laufen zwei Docks gleichzeitig.", ToolTipIcon.Warning);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Tray-Menü

        private void BuildTrayMenu()
        {
            MenuBuilder.DisposeDeep(_trayMenu.Items);
            _trayMenu.Items.Clear();
            MenuBuilder mb = new MenuBuilder(_trayMenu);
            ToolStripItemCollection items = mb.Menu.Items;

            Image icon = null;
            try { icon = Theme.IconToBitmap(_appIcon, (int)(64 * mb.Scale)); } catch { }
            MenuHeader header = new MenuHeader(mb.Colors, mb.Scale, icon);
            header.Title = AppInfo.Name;
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            header.Version = "v" + v.Major + "." + v.Minor;
            int count = 0;
            foreach (DockItem it in _dock.Items) if (!it.IsSeparator) count++;
            header.Status = (_settings.AutoHide ? "Blendet sich aus" : "Immer sichtbar") + " · " + count + " Einträge";
            header.StatusColor = Color.FromArgb(34, 160, 80);
            header.Size = header.GetPreferredSize(Size.Empty);
            items.Add(header);
            mb.Separator();

            ReleaseInfo update = Updater.Available;
            if (update != null)
            {
                GlyphMenuItem upd = mb.Item("Update auf " + update.Version + " installieren …", MenuHelper.Download,
                    delegate { Updater.InstallInteractive(VisibleForm, _invoker, null); });
                upd.Font = new Font(mb.Menu.Font, FontStyle.Bold);
                items.Add(upd);
                mb.Separator();
            }

            GlyphMenuItem settings = mb.Item("Einstellungen …", MenuHelper.Settings, ShowSettings);
            settings.Font = new Font(mb.Menu.Font, FontStyle.Bold);
            items.Add(settings);

            GlyphMenuItem hide = mb.Item("Automatisch ausblenden", '', delegate
            {
                _settings.AutoHide = !_settings.AutoHide;
                _settings.Save();
                _dock.ApplySettings();
            });
            MenuBuilder.MakeToggle(hide, _settings.AutoHide);
            items.Add(hide);

            GlyphMenuItem lockItem = mb.Item("Einträge sperren", '', delegate
            {
                _settings.Locked = !_settings.Locked;
                _settings.Save();
            });
            MenuBuilder.MakeToggle(lockItem, _settings.Locked);
            items.Add(lockItem);

            bool autoOn = Autostart.IsEnabled;
            GlyphMenuItem auto = mb.Item("Mit Windows starten", MenuHelper.Startup, delegate
            {
                if (!Autostart.Set(!autoOn, false))
                    MessageBox.Show("Der Autostart konnte nicht geändert werden.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
            MenuBuilder.MakeToggle(auto, autoOn);
            items.Add(auto);
            mb.Separator();

            items.Add(mb.Item("RocketDock übernehmen …", '', delegate { ImportInteractive(VisibleForm); }));
            items.Add(mb.Item("Datenordner öffnen", MenuHelper.Folder, delegate { Shell.Open(Settings.DataDirectory, "", ""); }));
            mb.Separator();
            items.Add(mb.Item("Info über Dock Pilot", MenuHelper.Info, delegate { AboutDialog.ShowFor(VisibleForm); }));
            items.Add(mb.Item("Beenden", MenuHelper.Power, ExitApp));
            mb.End();
        }

        private void NotifyUpdate()
        {
            ReleaseInfo r = Updater.Available;
            if (r == null || r.Version == _notifiedVersion) return;
            _notifiedVersion = r.Version;
            _balloonIsUpdate = true;
            _tray.ShowBalloonTip(10000, "Dock Pilot " + r.Version + " ist verfügbar",
                "Installiert ist " + AppInfo.Version + ". Klicken, um jetzt zu aktualisieren.", ToolTipIcon.Info);
        }

        private void ShowSettings()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm(_settings, _appIcon);
                _form.TestHotkey = _dock.TestHotkey;
                _form.ImportRocketDock = ImportInteractive;
                _form.ImportBackup = ImportBackup;
                _form.Saved += delegate { _dock.ApplySettings(); };
                _form.FormClosed += delegate { _form = null; };
                _form.Show();
            }
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }

        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Theme.Accent = Theme.SystemAccent;
        }

        private void ExitApp()
        {
            if (_form != null && !_form.IsDisposed) _form.Close();
            if (_updateTimer != null) _updateTimer.Stop();
            _tray.Visible = false;
            _showWait.Unregister(null);
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            _dock.Close();
            _dock.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _trayMenu.Dispose();
                _showEvent.Dispose();
                _invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
