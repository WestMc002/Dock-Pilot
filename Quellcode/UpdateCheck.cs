// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DockPilot
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    internal sealed class ReleaseInfo
    {
        public string Version, PageUrl, ZipUrl;
    }

    /// <summary>
    /// Update-Prüfung über die GitHub-Releases und Selbst-Update (Vorlage: Fensterpositionen wiederherstellen).
    /// Abgefragt wird nur die öffentliche GitHub-Schnittstelle, ohne Anmeldung; übertragen wird nur die
    /// Programmversion im User-Agent.
    /// </summary>
    internal static class Updater
    {
        public const string Repository = "WestMc002/Dock-Pilot";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

        /// <summary>Arbeitsordner für Download und Hilfsskript.</summary>
        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "DockPilot-Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }

        /// <summary>Gefundene neuere Version (null = keine bekannt).</summary>
        public static ReleaseInfo Available { get; private set; }

        /// <summary>Fehler der letzten Prüfung von Hand (null = kein Fehler).</summary>
        private static string _error;
        private static string _errorMore; // technischer Originaltext zum Fehler (für „Details“), kann fehlen

        /// <summary>Wird ausgelöst, wenn sich <see cref="Available"/> geändert hat (im UI-Thread).</summary>
        public static event EventHandler AvailableChanged;

        /// <summary>Das Update-Skript läuft – Dock Pilot muss sich jetzt beenden.</summary>
        public static event EventHandler ExitRequested;

        private static string UserAgent { get { return "DockPilot/" + AppInfo.Version; } }

        private static void PrepareTls()
        {
            // .NET 4: TLS 1.2 (3072) für github.com einschalten
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        // ------------------------------------------------------------------ Abfrage

        /// <summary>Kein Fehler: Es gibt noch keine fertige Version (Vorabversionen zählen bei „latest“ nicht).</summary>
        private const string NoRelease = "Auf GitHub ist noch keine fertige Version veröffentlicht.";

        public static ReleaseInfo GetLatest()
        {
            PrepareTls();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            string json;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                HttpWebResponse http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound) throw new Exception(NoRelease);
                // kurze Meldung für das Info-Fenster: Vor dem Knopf ist Platz für etwa 29 Zeichen, mit „Details“
                // dahinter für etwa 20. Der Originaltext steht hinter „Details“.
                string brief = "Nicht erreichbar.";
                if (http != null && (int)http.StatusCode == 403) brief = "Zu viele Anfragen.";
                else if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure) brief = "Keine Verbindung.";
                else if (ex.Status == WebExceptionStatus.Timeout) brief = "Keine Antwort.";
                Exception shown = new Exception(brief);
                shown.Data["Detail"] = ex.Message;
                throw shown;
            }

            ReleaseInfo info = new ReleaseInfo();
            info.PageUrl = ReleasesPage;
            Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"[vV]?([^\"]+)\"");
            if (m.Success) info.Version = m.Groups[1].Value.Trim();
            m = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
            if (m.Success) info.PageUrl = m.Groups[1].Value;
            m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+\\.zip)\"", RegexOptions.IgnoreCase);
            if (m.Success) info.ZipUrl = m.Groups[1].Value;
            if (string.IsNullOrEmpty(info.Version)) throw new Exception("Antwort unlesbar.");
            return info;
        }

        /// <summary>true, wenn <paramref name="remote"/> neuer ist als die laufende Version.</summary>
        public static bool IsNewer(string remote)
        {
            Version a, b;
            if (!Version.TryParse(remote, out a) || !Version.TryParse(AppInfo.Version, out b)) return false;
            return a > b;
        }

        // ------------------------------------------------------------------ Prüfung mit Einstellungen

        private static DateTime LastCheck(Settings s)
        {
            DateTime last;
            if (!string.IsNullOrEmpty(s.LastUpdateCheck) &&
                DateTime.TryParse(s.LastUpdateCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last))
                return last;
            return DateTime.MinValue;
        }

        /// <summary>Automatische Prüfung fällig? Höchstens alle 20 Stunden.</summary>
        public static bool Due(Settings s)
        {
            if (Demo.Active || !s.UpdateCheck) return false;
            DateTime last = LastCheck(s);
            return last == DateTime.MinValue || (DateTime.UtcNow - last.ToUniversalTime()).TotalHours >= 20;
        }

        /// <summary>Stand für das Info-Fenster: Symbol, Überschrift und zweite Zeile (Vorlage: Wake on LAN).</summary>
        public static UpdateStatus Status(Settings s)
        {
            if (Demo.Active) return new UpdateStatus(UpdateKind.Current, "Aktuell", "Zuletzt geprüft " + DateTime.Today.ToString("dd.MM.yyyy"));
            DateTime last = LastCheck(s);
            string lastText = last == DateTime.MinValue ? "" : "Zuletzt geprüft " + last.ToLocalTime().ToString("dd.MM.yyyy");
            if (Available != null) return new UpdateStatus(UpdateKind.Available, "Version " + Available.Version + " verfügbar", lastText);
            // noch nichts Fertiges veröffentlicht: neutral melden, das ist kein Fehler
            if (_error == NoRelease) return new UpdateStatus(UpdateKind.Unknown, "Nur Vorabversionen", "Noch keine fertige Version."); // beide Zeilen kurz: wenig Platz
            if (_error != null)
            {
                UpdateStatus failed = new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", _error);
                failed.More = _errorMore;
                return failed;
            }
            if (last == DateTime.MinValue) return new UpdateStatus(UpdateKind.Unknown, "Noch nicht geprüft", "");
            return new UpdateStatus(UpdateKind.Current, "Aktuell", lastText);
        }

        /// <summary>Kann das Update direkt installiert werden (ZIP vorhanden, nicht im Projektordner)?</summary>
        public static bool CanInstall()
        {
            return Available != null && !string.IsNullOrEmpty(Available.ZipUrl) && InstallBlocker() == null;
        }

        /// <summary>
        /// Neueste Version im Hintergrund abfragen. Automatisch (manual = false) still: Fehler werden nicht gemeldet.
        /// <paramref name="done"/> bekommt den neuen Stand für das Info-Fenster (im Thread von <paramref name="invoker"/>).
        /// </summary>
        public static void CheckAsync(Settings s, Control invoker, bool manual, Action<UpdateStatus> done)
        {
            if (Demo.Active) { if (done != null) done(Status(s)); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo release = null;
                string error = null, more = null;
                try { release = GetLatest(); }
                catch (Exception ex)
                {
                    error = ex.Message;
                    more = ex.Data["Detail"] as string;
                    // unerwarteter Fehler mit langem Text: Die Überschrift „Prüfung nicht möglich“ sagt schon alles,
                    // darunter steht nur „Details“ mit dem Text
                    if (more == null && error != null && error != NoRelease && error.Length > 36) { more = error; error = ""; }
                }
                try
                {
                    invoker.BeginInvoke((MethodInvoker)delegate
                    {
                        if (release != null)
                        {
                            _error = null;
                            _errorMore = null;
                            s.LastUpdateCheck = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                            s.Save();
                            SetAvailable(IsNewer(release.Version) ? release : null);
                        }
                        else if (manual)
                        {
                            _error = error;
                            _errorMore = more;
                        }
                        if (done != null) done(Status(s));
                    });
                }
                catch { }
            });
        }

        private static void SetAvailable(ReleaseInfo release)
        {
            bool changed = (Available == null) != (release == null) ||
                           (Available != null && release != null && Available.Version != release.Version);
            Available = release;
            if (changed && AvailableChanged != null) AvailableChanged(null, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ Installation

        /// <summary>
        /// Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen.
        /// </summary>
        public static string InstallBlocker()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            return null;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "~update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// ZIP herunterladen, Hilfsskript schreiben und starten (blockierend – im Hintergrund aufrufen).
        /// Das Skript wartet, bis Dock Pilot beendet ist, ersetzt EXE und Anleitung und startet Dock Pilot
        /// neu. Die Einstellungen (Dock Pilot.ini) bleiben unberührt. Danach muss sich Dock Pilot beenden.
        /// </summary>
        public static void Install(ReleaseInfo release)
        {
            if (string.IsNullOrEmpty(release.ZipUrl)) throw new Exception("Im Release ist keine ZIP-Datei angehängt.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            string zip = Path.Combine(Folder, "Dock-Pilot-" + release.Version + ".zip");
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.DownloadFile(new Uri(release.ZipUrl), zip);
            }
            if (new FileInfo(zip).Length < 1024) throw new Exception("Die heruntergeladene Datei ist unvollständig.");
            string script = Path.Combine(Folder, "Update.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            string target = Path.GetDirectoryName(exe);
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -ProcessId " + Process.GetCurrentProcess().Id +
                " -Zip \"" + zip + "\" -Exe \"" + exe + "\" -Version \"" + release.Version + "\"");
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
            if (!CanWrite(target)) start.Verb = "runas";
            try { Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
        }

        /// <summary>
        /// Nachfragen, herunterladen und installieren. Im Projektordner oder ohne ZIP öffnet sich stattdessen
        /// die Release-Seite. <paramref name="busy"/> wird während des Downloads mit true/false aufgerufen.
        /// </summary>
        public static void InstallInteractive(IWin32Window owner, Control invoker, Action<bool> busy)
        {
            ReleaseInfo release = Available;
            if (release == null) return;
            string blocker = InstallBlocker();
            if (blocker != null || string.IsNullOrEmpty(release.ZipUrl))
            {
                if (MessageBox.Show(owner,
                    (blocker ?? "Im Release ist keine ZIP-Datei angehängt.") + "\n\nRelease-Seite auf GitHub öffnen?",
                    "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenPage();
                return;
            }
            if (MessageBox.Show(owner,
                "Dock Pilot wird beendet, auf Version " + release.Version + " aktualisiert und neu gestartet.\n" +
                "Die Einstellungen bleiben erhalten.\n\nJetzt aktualisieren?",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            if (busy != null) busy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { Install(release); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    invoker.BeginInvoke((MethodInvoker)delegate
                    {
                        if (busy != null) busy(false);
                        if (error != null)
                        {
                            MessageBox.Show(owner, error, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (ExitRequested != null) ExitRequested(null, EventArgs.Empty);
                    });
                }
                catch { }
            });
        }

        public static void OpenPage()
        {
            ReleaseInfo r = Available;
            try { Process.Start(r != null && !string.IsNullOrEmpty(r.PageUrl) ? r.PageUrl : ReleasesPage); } catch { }
        }

        /// <summary>Ergebnis des letzten Updates (nach dem Neustart mit --nach-update).</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        private const string Script = @"# Dock Pilot – Update (wird vom Programm erzeugt)
param([int]$ProcessId,[string]$Zip,[string]$Exe,[string]$Version)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'Update.log'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  $dir = Join-Path $PSScriptRoot 'Neu'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $dir -Force
  $new = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $old = $Exe + '.alt'
  Copy-Item $Exe $old -Force
  try {
    Copy-Item $new.FullName $Exe -Force
    Get-ChildItem $new.DirectoryName -File | Where-Object { $_.Name -ne $name } | Copy-Item -Destination $target -Force
  } catch {
    Copy-Item $old $Exe -Force
    throw
  }
  Remove-Item $old -Force
  Remove-Item $dir -Recurse -Force
  Remove-Item $Zip -Force
  ('OK ' + $Version) | Out-File $log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File $log -Encoding utf8
}
Start-Process -FilePath $Exe -ArgumentList '--nach-update'
";
    }

    internal enum UpdateKind { Unknown, Current, Available, Failed, Checking }

    /// <summary>Stand der Update-Prüfung für das Info-Fenster (Vorlage: Wake on LAN).</summary>
    internal sealed class UpdateStatus
    {
        public readonly UpdateKind Kind;
        public readonly string Title, Detail;
        /// <summary>Ausführlicher Text zur zweiten Zeile (hinter „Details“), null = keiner.</summary>
        public string More;
        public UpdateStatus(UpdateKind kind, string title, string detail) { Kind = kind; Title = title; Detail = detail ?? ""; }
    }
}
