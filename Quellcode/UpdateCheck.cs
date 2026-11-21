// Dock Pilot – Programmleiste am Bildschirmrand (Nachfolger von RocketDock)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
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
        /// <summary>SHA-256 des ZIP aus dem signierten Manifest (Großbuchstaben); ohne gültige Signatur bleibt ZipUrl leer.</summary>
        public string Sha256;
        /// <summary>Kurzmeldung, warum das Update nicht installiert wird (z. B. „Nicht signiert.“), sonst null.</summary>
        public string Problem;
        /// <summary>Ausführlicher Text zu <see cref="Problem"/> (für „Details“).</summary>
        public string ProblemDetail;
        /// <summary>Das geprüfte Manifest (Bytes wie auf GitHub); das Update-Skript prüft es selbst noch einmal.</summary>
        public byte[] Manifest;
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

        /// <summary>Fehler beim automatischen Aktualisieren (Originaltext für „Details“); null = keiner.</summary>
        private static string _installError;

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

            // Antwort richtig als JSON lesen (nicht per Suchmuster: Der Release-Text könnte sonst
            // Adressen vortäuschen) und nur das erwartete ZIP mit Prüfsumme übernehmen
            Dictionary<string, object> obj;
            try { obj = MiniJson.Parse(json) as Dictionary<string, object>; }
            catch { obj = null; }
            if (obj == null) throw new Exception("Antwort unlesbar.");
            ReleaseInfo info = new ReleaseInfo();
            info.PageUrl = ReleasesPage;
            string tag = (MiniJson.Text(obj, "tag_name") ?? "").Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
            Version parsed;
            if (tag.Length == 0 || !Version.TryParse(tag, out parsed)) throw new Exception("Antwort unlesbar.");
            info.Version = tag;
            string page = MiniJson.Text(obj, "html_url");
            if (page != null && page.StartsWith("https://github.com/" + Repository + "/releases/", StringComparison.OrdinalIgnoreCase))
                info.PageUrl = page;
            // ZIP und signiertes Manifest suchen: nur mit den erwarteten Namen und von der Download-Adresse
            // dieses Repositorys
            string wanted = AssetName(info.Version);
            string wantedManifest = ManifestName(info.Version);
            string prefix = "https://github.com/" + Repository + "/releases/download/";
            string zipUrl = null, manifestUrl = null;
            object list;
            if (obj.TryGetValue("assets", out list) && list is List<object>)
            {
                foreach (object entry in (List<object>)list)
                {
                    Dictionary<string, object> asset = entry as Dictionary<string, object>;
                    if (asset == null) continue;
                    string name = MiniJson.Text(asset, "name");
                    string url = MiniJson.Text(asset, "browser_download_url");
                    if (url == null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, wanted, StringComparison.Ordinal)) zipUrl = url;
                    else if (string.Equals(name, wantedManifest, StringComparison.Ordinal)) manifestUrl = url;
                }
            }
            if (zipUrl == null) return info; // kein ZIP: nur die Release-Seite
            // Die Prüfsumme kommt nur aus dem signierten Manifest, nicht von GitHub: Wer das Release austauscht,
            // kann ohne den privaten Schlüssel keine passende Signatur erzeugen.
            if (manifestUrl == null)
            {
                info.Problem = "Nicht signiert.";
                info.ProblemDetail = "Im Release fehlt die Signatur (" + wantedManifest + "). Dock Pilot installiert nur signierte Updates.";
                return info;
            }
            byte[] manifest;
            try { manifest = DownloadSmall(manifestUrl, 8192); }
            catch (Exception ex)
            {
                info.Problem = "Signatur nicht lesbar.";
                info.ProblemDetail = "Die Signatur konnte nicht geladen werden: " + ex.Message;
                return info;
            }
            string sha, detail;
            string problem = CheckManifest(manifest, info.Version, out sha, out detail);
            if (problem != null)
            {
                info.Problem = problem;
                info.ProblemDetail = detail;
                return info;
            }
            info.ZipUrl = zipUrl;
            info.Sha256 = sha;
            info.Manifest = manifest;
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

        /// <summary>Nachprüfung im laufenden Betrieb fällig? Höchstens alle 20 Stunden (beim Start gilt die Sperre nicht).</summary>
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
            if (Available != null)
            {
                if (_installError != null)
                {
                    // automatisches Aktualisieren ist gescheitert: Kurzmeldung mit „Details“ statt der letzten Prüfung
                    UpdateStatus broken = new UpdateStatus(UpdateKind.Available, "Version " + Available.Version + " verfügbar", "Update fehlgeschlagen.");
                    broken.More = _installError;
                    return broken;
                }
                if (Available.Problem != null)
                {
                    // neue Version da, aber nicht (gültig) signiert: wird nicht installiert
                    UpdateStatus unsigned = new UpdateStatus(UpdateKind.Available, "Version " + Available.Version + " verfügbar", Available.Problem);
                    unsigned.More = Available.ProblemDetail;
                    return unsigned;
                }
                return new UpdateStatus(UpdateKind.Available, "Version " + Available.Version + " verfügbar", lastText);
            }
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

        /// <summary>Name des ZIP im Release (wie ihn der Workflow anlegt).</summary>
        public static string AssetName(string version)
        {
            return "Dock-Pilot-" + version + ".zip";
        }

        /// <summary>Name des signierten Manifests neben dem ZIP.</summary>
        public static string ManifestName(string version)
        {
            return "Dock-Pilot-" + version + ".manifest";
        }

        /// <summary>Kein passendes ZIP im Release: dann nur die Release-Seite.</summary>
        private const string NoZip = "Im Release ist keine passende ZIP-Datei angehängt.";

        // ------------------------------------------------------------------ Signatur

        // Öffentliche Schlüssel (RSA 3072) des Herausgebers: Haupt- und Reserveschlüssel. Signiert wird mit dem
        // Hauptschlüssel; der Reserveschlüssel liegt nur in KeePass und ersetzt ihn, falls er verloren geht.
        private const string MainKey = "<RSAKeyValue><Modulus>tLtylaHt5SgGCrTrEaM0H3Z/UqIQWSL2c3rCpuhZEYnCyJVDIYFCNuLbZ2OeapsJgCttOnwv5djvmpHTcGV6DeFM059O699xepTE0QWKYYM69sCFG4gunH2xh39bM20q9qWbui1Fyy+nN4cqzy26eR7tzXyClAJSS1zCKBuoF1uAtt2c1f1fcCHXdHsjWXZA6Z8OZeiIOizYSfiVHNIJFr8kSFQfnGRaygwp2ta27Hv0z0oKM6Zrvld/vXkgL2um7X1+GV24IzIVT7J8nxmar//yPoc6ugYhgcLjUMBgjwe/5m4REJQFWwntMF1uj5Jf+ehBFZS87kGtSvTykh7mea71JMdLpvAuhicMaP4T8Fr1uTYCPKR4cuiRI+6uogKVOZN23BuZS5iBj1zZkcmopNOXbQupsOkYp2QwejMHoEV7cEc71ZlRqqJed0vBqO8B5H21mrBGJm3kKHxa2QFzY5BZY7ZcC4EG2TZIkC8eZRNUXXmZOdAWGR+aPKeJg7DZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const string ReserveKey = "<RSAKeyValue><Modulus>zY/NkGbkd4IZHMMuGty2WLmQ0hm6lEZBql2v84BPf3NT+rpenfLK0kk7aKfRXgFS1G8tz+tHHd58GhioFFhnXN2syFTUus7u5csblitORGoNjTf9Rlicxw6Ut3hX7tkn2u+2PRKE5xOOL+epZV1YvBgr/W67WTt5RZEyfA0Vgn8llhrXaSD32iVNEzkeY5yy1bvQAmoxAk3KjNcD8Uuia9XP9qs/orDO0giIj/xsfY2D7qp/rkhSaIOnt/+gRWHbjhC/PGPga3dmcIMC1C3X0Z/meg6XgI+zWeXOOxFe8jCVRlaeGDoRJlJJGvg6uzIJlLCQeNxnSEufZCD10e+ex2xx75Gp3bqCvBvr49VXEC+pLxQteQMj2V5tuhq23MtIeSV0huaKZ1SPm1kxFjG5vj/11bo2VCkcx9dhfEBDdsucJqdUecbLJp58Y6smM2Ah4Qh7nILSNHHcpWey100qMN3WdwXsfzPVuRtD1c16KgC3TQIov2/2fGQGUxn1Awox</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static readonly string[] ManifestKeys = { "Programm", "Version", "Datei", "SHA256", "Schluessel", "Erstellt" };

        /// <summary>
        /// Manifest prüfen (Aufbau wie Signieren.ps1: UTF-8, LF, feste Reihenfolge, letzte Zeile „Signatur=“ mit
        /// RSA-SHA256/PKCS#1 v1.5 über alle Bytes davor). Rückgabe: null = gültig (dann <paramref name="sha"/> gesetzt),
        /// sonst Kurzmeldung; <paramref name="detail"/> erklärt sie.
        /// </summary>
        public static string CheckManifest(byte[] data, string version, out string sha, out string detail)
        {
            sha = null;
            detail = null;
            const string Invalid = "Signatur ungültig.";
            byte[] marker = Encoding.ASCII.GetBytes("\nSignatur=");
            int at = IndexOf(data, marker);
            if (at < 0) { detail = "Im Manifest fehlt die Zeile „Signatur=“."; return Invalid; }
            byte[] signed = new byte[at + 1];
            Array.Copy(data, signed, signed.Length);
            byte[] signature;
            try
            {
                string text = Encoding.ASCII.GetString(data, at + marker.Length, data.Length - at - marker.Length).Trim();
                signature = Convert.FromBase64String(text);
            }
            catch { detail = "Die Signatur im Manifest ist nicht lesbar."; return Invalid; }
            string[] lines;
            try { lines = new UTF8Encoding(false, true).GetString(signed).Split('\n'); }
            catch { detail = "Das Manifest ist kein gültiger UTF-8-Text."; return Invalid; }
            if (lines.Length != ManifestKeys.Length + 1 || lines[ManifestKeys.Length].Length != 0)
            {
                detail = "Das Manifest hat nicht den erwarteten Aufbau.";
                return Invalid;
            }
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ManifestKeys.Length; i++)
            {
                if (!lines[i].StartsWith(ManifestKeys[i] + "=", StringComparison.Ordinal))
                {
                    detail = "Im Manifest fehlt oder steht falsch: " + ManifestKeys[i] + ".";
                    return Invalid;
                }
                values[ManifestKeys[i]] = lines[i].Substring(ManifestKeys[i].Length + 1);
            }
            string keyName = values["Schluessel"];
            string keyXml = keyName == "Haupt" ? MainKey : keyName == "Reserve" ? ReserveKey : null;
            if (keyXml == null) { detail = "Unbekannter Schlüssel im Manifest: " + keyName; return Invalid; }
            bool ok;
            try
            {
                // PROV_RSA_AES (24): RSA mit SHA-256 auch unter .NET 4
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(keyXml);
                    ok = rsa.VerifyData(signed, "SHA256", signature);
                }
            }
            catch (Exception ex) { detail = "Die Signatur konnte nicht geprüft werden: " + ex.Message; return Invalid; }
            if (!ok)
            {
                detail = "Die Signatur passt nicht zum " + (keyName == "Haupt" ? "Haupt" : "Reserve") +
                         "schlüssel von Dock Pilot. Das Release wurde nicht vom Herausgeber signiert oder nachträglich verändert.";
                return Invalid;
            }
            // gültig signiert: Inhalt muss genau zu diesem Programm und dieser Version passen
            if (values["Programm"] != "Dock Pilot") { detail = "Das Manifest gehört zu „" + values["Programm"] + "“, nicht zu Dock Pilot."; return Invalid; }
            if (values["Version"] != version) { detail = "Das Manifest nennt Version " + values["Version"] + ", das Release " + version + "."; return Invalid; }
            if (values["Datei"] != AssetName(version)) { detail = "Das Manifest nennt die Datei „" + values["Datei"] + "“."; return Invalid; }
            if (!Regex.IsMatch(values["SHA256"], "^[0-9a-f]{64}$")) { detail = "Die Prüfsumme im Manifest ist ungültig."; return Invalid; }
            sha = values["SHA256"].ToUpperInvariant();
            return null;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        /// <summary>Kleine Datei laden (höchstens <paramref name="max"/> Bytes).</summary>
        private static byte[] DownloadSmall(string url, int max)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Die Datei ist zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Kann das Update direkt installiert werden (ZIP vorhanden, nicht im Projektordner)?</summary>
        public static bool CanInstall()
        {
            return Available != null && !string.IsNullOrEmpty(Available.ZipUrl) && !string.IsNullOrEmpty(Available.Sha256) && InstallBlocker() == null;
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
            if (changed) _installError = null;
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

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "Dock Pilot.exe", ManualName = "Anleitung.txt";

        /// <summary>
        /// ZIP herunterladen, Prüfsumme (aus dem signierten Manifest) kontrollieren und das Update-Skript starten (blockierend – im Hintergrund
        /// aufrufen). Das Skript wartet, bis Dock Pilot beendet ist, prüft die Prüfsumme erneut, ersetzt EXE und
        /// Anleitung und startet Dock Pilot neu. Die Einstellungen bleiben unberührt. Danach muss sich Dock Pilot beenden.
        /// Ohne Schreibrechte im Programmordner läuft das Skript mit Administratorrechten in einem geschützten
        /// Arbeitsordner; den Neustart übernimmt dann ein zweiter Prozess mit normalen Rechten.
        /// </summary>
        public static void Install(ReleaseInfo release) { Install(release, false); }

        /// <param name="tray">Einstellungsfenster war zu: nach dem Update nur im Infobereich starten (--nach-update /tray).</param>
        public static void Install(ReleaseInfo release, bool tray)
        {
            // nur ein Update zur Zeit: von Hand und automatisch teilen sich diese Sperre; sie bleibt nach dem Start
            // des Skripts gesetzt, weil sich Dock Pilot danach beendet
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new Exception(Running);
            bool started = false;
            try
            {
                InstallLocked(release, tray);
                started = true;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>Läuft gerade ein Update (in diesem Programm oder als Skript nach einem früheren Start)?</summary>
        public static bool Busy
        {
            get { return _busy != 0 || ScriptRunning(); }
        }

        private static int _busy;
        private const string Running = "Ein Update läuft bereits.";
        /// <summary>
        /// Name der Sperre, die das Update-Skript bis zum Ende des Dateiaustauschs bzw. der Rücksicherung hält:
        /// sitzungsübergreifend (Global\) und je Installationsziel (Prüfsumme des Programmordners), damit auch
        /// Updates aus zwei angemeldeten Konten nie gleichzeitig dieselben Dateien ersetzen.
        /// </summary>
        private static string LockName
        {
            get
            {
                string dir = Path.GetFullPath(AppInfo.ExeDirectory).TrimEnd('\\').ToUpperInvariant();
                using (SHA256 sha = SHA256.Create())
                    return @"Global\DockPilot-Update-" +
                           BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 24);
            }
        }

        private static bool ScriptRunning()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(LockName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // vom Skript mit Administratorrechten oder aus einem anderen Konto angelegt
            catch { return false; }
        }

        private static void InstallLocked(ReleaseInfo release, bool tray)
        {
            if (release.Problem != null)
            {
                Exception unsigned = new Exception(release.Problem);
                unsigned.Data["Detail"] = release.ProblemDetail;
                throw unsigned;
            }
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256)) throw new Exception(NoZip);
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            if (ScriptRunning()) throw new Exception(Running);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            CleanOldAttempts();
            // eigener Arbeitsordner je Versuch: Download, Entpacken und Rückfallkopien kommen sich nie in die Quere
            string attempt = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.DownloadFile(new Uri(release.ZipUrl), zip);
            }
            string hash = Sha256Of(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zip); } catch { }
                Exception bad = new Exception("Download beschädigt.");
                bad.Data["Detail"] = "Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\n" +
                                     "Erwartet: " + release.Sha256 + "\nErhalten: " + hash;
                throw bad;
            }
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            bool elevated = !CanWrite(Path.GetDirectoryName(exe));
            // Erhöht: eigener, nicht vorhersagbarer Arbeitsordner unter ProgramData, den das Skript nur für
            // Administratoren beschreibbar anlegt. Sonst reicht der Temp-Ordner des Benutzers.
            string work = elevated
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DockPilot-Update-" + Guid.NewGuid().ToString("N"))
                : attempt;
            int pid = Process.GetCurrentProcess().Id;
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst (bei Administratorrechten an seiner
            // Kopie im geschützten Arbeitsordner) und nimmt die Prüfsumme nur aus dem geprüften Manifest
            if (release.Manifest == null) throw new Exception(NoZip);
            string manifestFile = Path.Combine(attempt, ManifestName(release.Version));
            File.WriteAllBytes(manifestFile, release.Manifest);
            Var(vars, "Zip", zip);
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "Log", LogFile);
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            ProcessStartInfo start = PowerShell(vars + InstallScript);
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten
            if (elevated) start.Verb = "runas";
            Process installer;
            try { installer = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            if (!elevated) return;
            // Neustart mit normalen Rechten: Ein nicht erhöhter Prozess wartet auf das Ende des Updates, übernimmt
            // das Ergebnis in Update.log und startet Dock Pilot – sonst liefe es danach mit Administratorrechten.
            int installerId = 0;
            try { if (installer != null) installerId = installer.Id; } catch { }
            StringBuilder r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$Installer = ").Append(installerId).Append("\n");
            Var(r, "Attempt", attempt);
            Var(r, "Exe", exe);
            Var(r, "Log", LogFile);
            Var(r, "Work", work);
            r.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            Process.Start(PowerShell(r + RestartScript));
        }

        /// <summary>Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen.</summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
            }
            catch { }
        }

        private static string Sha256Of(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        /// <summary>PowerShell mit dem Skript als Kommando (-EncodedCommand): Es liegt nirgends als Datei, die man austauschen könnte.</summary>
        private static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            // nur der feste Systempfad, kein Suchen über PATH oder den aktuellen Ordner
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            // Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein
            StringBuilder compact = new StringBuilder();
            foreach (string raw in script.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(compact.ToString()));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            ProcessStartInfo start = new ProcessStartInfo(ps, arguments);
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = system;
            return start;
        }

        /// <summary>Variable für das Skript als PowerShell-Text in einfachen Anführungszeichen.</summary>
        private static void Var(StringBuilder sb, string name, string value)
        {
            sb.Append('$').Append(name).Append(" = '");
            foreach (char c in value ?? "")
            {
                sb.Append(c);
                // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c);
                if (c == '\n' || c == '\r') throw new Exception("Ungültiger Pfad für das Update.");
            }
            sb.Append("'\n");
        }

        /// <summary>
        /// Nachfragen, herunterladen und installieren. Im Projektordner oder ohne ZIP öffnet sich stattdessen
        /// die Release-Seite. <paramref name="busy"/> wird während des Downloads mit true/false aufgerufen.
        /// </summary>
        public static void InstallInteractive(IWin32Window owner, Control invoker, Action<bool> busy)
        {
            ReleaseInfo release = Available;
            if (release == null) return;
            if (Busy)
            {
                MessageBox.Show(owner, Running, "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string blocker = InstallBlocker();
            if (blocker != null || string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256))
            {
                string why = blocker;
                if (why == null && release.Problem != null)
                    why = "Dock Pilot installiert dieses Update nicht: " + release.Problem +
                          (string.IsNullOrEmpty(release.ProblemDetail) ? "" : "\n\n" + release.ProblemDetail);
                if (MessageBox.Show(owner,
                    (why ?? NoZip) + "\n\nRelease-Seite auf GitHub öffnen?",
                    "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenPage();
                return;
            }
            if (MessageBox.Show(owner,
                "Dock Pilot wird beendet, auf Version " + release.Version + " aktualisiert und neu gestartet.\n" +
                "Die Einstellungen bleiben erhalten.\n\nJetzt aktualisieren?",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            bool tray = owner == null; // ohne sichtbares Einstellungsfenster: nach dem Update nur im Infobereich
            if (busy != null) busy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { Install(release, tray); }
                catch (Exception ex)
                {
                    error = ex.Message;
                    string detail = ex.Data["Detail"] as string;
                    if (detail != null) error += "\n\n" + detail;
                }
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

        /// <summary>
        /// Automatisch aktualisieren: ohne Rückfrage herunterladen und installieren (nur mit ZIP, nicht im Projektordner).
        /// Fehler ohne Meldung: Die Hinweise bleiben, die Kurzmeldung steht mit „Details“ im Info-Fenster.
        /// </summary>
        public static void InstallSilently(Control invoker, bool tray)
        {
            ReleaseInfo release = Available;
            if (release == null || !CanInstall() || Busy) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try { Install(release, tray); }
                catch (Exception ex) { error = ex; Program.Log(ex); }
                try
                {
                    invoker.BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null) { _installError = (error.Data["Detail"] as string) ?? error.Message; return; }
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

        // Update-Skript. Davor setzt das Programm: $ProcessId, $Zip, $Manifest, $Exe, $Version, $Sha256, $AssetName,
        // $MainKey, $ReserveKey, $LockName, $Log, $Work, $ExeName, $ManualName, $Elevated, $Tray. Nur ASCII-Anführungszeichen verwenden.
        private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen
    Get-ChildItem -LiteralPath $root -Directory -Filter 'DockPilot-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
          $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
          if ($o -eq $admins -or $o -eq $system) { [System.IO.Directory]::Delete($_.FullName, $true) }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen: schreiben nur SYSTEM und Administratoren, lesen alle angemeldeten Benutzer
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $sec = New-Object System.Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    $sec.SetOwner($admins)
    foreach ($sid in @($admins, $system)) {
      $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
    }
    $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
    if (Test-Path -LiteralPath $Work) { throw 'Der Arbeitsordner fuer das Update existiert bereits.' }
    [void][System.IO.Directory]::CreateDirectory($Work, $sec)
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner fuer das Update ist nicht geschuetzt.' }
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Item -LiteralPath $Zip -Destination $zipWork -Force
    Copy-Item -LiteralPath $Manifest -Destination $manifestWork -Force
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (eigene Herausgeberpruefung, an der Kopie, die verwendet wird)
  $bytes = [System.IO.File]::ReadAllBytes($manifestWork)
  if ($bytes.Length -gt 8192) { throw 'Das Manifest ist zu gross.' }
  $marker = [System.Text.Encoding]::ASCII.GetBytes([string][char]10 + 'Signatur=')
  $at = -1
  for ($i = 0; $i -le $bytes.Length - $marker.Length -and $at -lt 0; $i++) {
    $j = 0
    while ($j -lt $marker.Length -and $bytes[$i + $j] -eq $marker[$j]) { $j++ }
    if ($j -eq $marker.Length) { $at = $i }
  }
  if ($at -lt 0) { throw 'Im Manifest fehlt die Signatur.' }
  $signed = New-Object byte[] ($at + 1)
  [System.Array]::Copy($bytes, $signed, $at + 1)
  $sig = [System.Convert]::FromBase64String([System.Text.Encoding]::ASCII.GetString($bytes, $at + $marker.Length, $bytes.Length - $at - $marker.Length).Trim())
  $lines = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($signed).Split([char]10)
  $names = @('Programm', 'Version', 'Datei', 'SHA256', 'Schluessel', 'Erstellt')
  if ($lines.Count -ne 7 -or $lines[6] -ne '') { throw 'Das Manifest hat nicht den erwarteten Aufbau.' }
  $mv = @{}
  for ($i = 0; $i -lt 6; $i++) {
    $p = $names[$i] + '='
    if (-not $lines[$i].StartsWith($p, [System.StringComparison]::Ordinal)) { throw ('Im Manifest fehlt ' + $names[$i] + '.') }
    $mv[$names[$i]] = $lines[$i].Substring($p.Length)
  }
  if ($mv['Schluessel'] -ceq 'Haupt') { $keyXml = $MainKey }
  elseif ($mv['Schluessel'] -ceq 'Reserve') { $keyXml = $ReserveKey }
  else { throw 'Unbekannter Schluessel im Manifest.' }
  $csp = New-Object System.Security.Cryptography.CspParameters -ArgumentList 24
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
  $rsa.PersistKeyInCsp = $false
  $rsa.FromXmlString($keyXml)
  $valid = $rsa.VerifyData($signed, 'SHA256', $sig)
  $rsa.Clear()
  if (-not $valid) { throw 'Die Signatur des Updates ist ungueltig.' }
  if ($mv['Programm'] -cne 'Dock Pilot' -or $mv['Version'] -cne $Version -or $mv['Datei'] -cne $AssetName) { throw 'Das Manifest passt nicht zu diesem Update.' }
  if ($mv['SHA256'] -cnotmatch '^[0-9a-f]{64}$') { throw 'Die Pruefsumme im Manifest ist ungueltig.' }
  $expected = $mv['SHA256'].ToUpperInvariant()
  if ($expected -ne $Sha256) { throw 'Die Pruefsumme im Manifest weicht von der Pruefung im Programm ab.' }
  # Pruefsumme aus dem gepruften Manifest an der Datei pruefen, die tatsaechlich entpackt wird
  $hash = (Get-FileHash -LiteralPath $zipWork -Algorithm SHA256).Hash
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $dir = Join-Path $Work 'Neu'
  if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
  Expand-Archive -LiteralPath $zipWork -DestinationPath $dir -Force
  # nur bekannte Dateien uebernehmen
  $new = Join-Path $dir $ExeName
  if (-not (Test-Path -LiteralPath $new -PathType Leaf)) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($new, $Exe))
  $manual = Join-Path $dir $ManualName
  if (Test-Path -LiteralPath $manual -PathType Leaf) { $pairs += ,@($manual, (Join-Path $target $ManualName)) }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $had = Test-Path -LiteralPath $p[1]
      if ($had) { Copy-Item -LiteralPath $p[1] -Destination $bak -Force }
      $done += ,@($p[1], $bak, $had)
      Copy-Item -LiteralPath $p[0] -Destination $p[1] -Force
    }
  } catch {
    foreach ($d in $done) {
      if ($d[2]) { Copy-Item -LiteralPath $d[1] -Destination $d[0] -Force }
      else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction SilentlyContinue }
    }
    throw
  }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  $result = 'FEHLER ' + $_.Exception.Message
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der nicht erhoehte Prozess
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated) {
  if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

        // Neustart nach einem Update mit Administratorrechten, selbst mit normalen Rechten.
        // Davor setzt das Programm: $ProcessId, $Installer, $Attempt, $Exe, $Log, $Work, $Tray.
        private const string RestartScript = @"
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id $ProcessId -Timeout 60 } catch {}
$end = (Get-Date).AddMinutes(15)
$workLog = Join-Path $Work 'Update.log'
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { break } }
  elseif (Test-Path -LiteralPath $workLog) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 300
$text = $null
if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
if (-not $text) { $text = 'FEHLER Das Update wurde nicht ausgefuehrt.' }
$text | Out-File -LiteralPath $Log -Encoding utf8
Remove-Item -LiteralPath $Attempt -Recurse -Force
if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
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

    /// <summary>Kleiner JSON-Leser für die Antwort von GitHub (Objekte, Listen, Texte, Zahlen, true/false/null).</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            int i = 0;
            object value = Value(json, ref i, 0);
            Skip(json, ref i);
            if (i != json.Length) throw new FormatException("JSON: Zeichen nach dem Ende");
            return value;
        }

        /// <summary>Text eines Feldes oder null.</summary>
        public static string Text(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) ? v as string : null;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("JSON: zu tief verschachtelt");
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unerwartetes Ende");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: Name erwartet");
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: Doppelpunkt erwartet");
                    i++;
                    obj[key] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new FormatException("JSON: Komma oder } erwartet");
                }
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException("JSON: Komma oder ] erwartet");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("JSON: unbekannter Wert");
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON: ungültige Zahl");
            return d;
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            i++; // "
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: unvollständiges \\u");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("JSON: ungültiges Escape");
                }
            }
            throw new FormatException("JSON: Text nicht abgeschlossen");
        }
    }
}
