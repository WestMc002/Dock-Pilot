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
        /// <summary>Größe des ZIP laut GitHub (Bytes, 0 = unbekannt); der Download wird darauf begrenzt.</summary>
        public long ZipSize;
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
                // Antwort auf 1 MB begrenzen (übliche Antworten haben wenige KB)
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                    json = Encoding.UTF8.GetString(ReadLimited(stream, 1024 * 1024));
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
            long zipSize = 0;
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
                    if (string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        zipUrl = url;
                        object size;
                        if (asset.TryGetValue("size", out size) && size is double) zipSize = (long)(double)size;
                    }
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
            info.ZipSize = zipSize;
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
                if (string.IsNullOrEmpty(Available.ZipUrl))
                    return new UpdateStatus(UpdateKind.Available, "Version " + Available.Version + " verfügbar", "Keine passende ZIP-Datei."); // Knopf: Release-Seite
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

        /// <summary>Obergrenze für das Update-ZIP (Dock Pilot selbst ist unter 1 MB).</summary>
        private const long MaxZip = 64L * 1024 * 1024;

        /// <summary>Stream lesen, höchstens <paramref name="max"/> Bytes – sonst Abbruch.</summary>
        private static byte[] ReadLimited(Stream stream, int max)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Antwort zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Datei laden und beim Lesen auf <paramref name="max"/> Bytes begrenzen (danach Abbruch, Datei weg).</summary>
        private static void DownloadLimited(string url, string file, long max)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            bool ok = false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ContentLength > max) throw new Exception("Das Update ist größer als erwartet.");
                    using (Stream stream = response.GetResponseStream())
                    using (FileStream fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += n;
                            if (total > max) throw new Exception("Das Update ist größer als erwartet.");
                            fs.Write(buffer, 0, n);
                        }
                    }
                }
                ok = true;
            }
            finally
            {
                if (!ok) try { File.Delete(file); } catch { }
            }
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
            if (Available == null || string.IsNullOrEmpty(Available.ZipUrl) || string.IsNullOrEmpty(Available.Sha256)) return false;
            QuietCleanup();
            return InstallBlocker() == null;
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
                    if (error != NoRelease)
                        Log.Write(manual ? "Update-Prüfung" : "Update-Prüfung (automatisch)", error + (more != null ? " – " + more : ""));
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
        /// Warum nicht installiert wird (null = kann installiert werden):
        /// - im Projektordner (mit Erstellen.bat): dort entsteht die EXE beim Bauen
        /// - Dock Pilot läuft selbst mit Administratorrechten: Skript und Neustart liefen dann erhöht mit Arbeitsordner
        ///   im Temp-Ordner, sobald die Schreibprobe gelingt (normal gestartet nimmt das Update den geschützten Weg)
        /// - ein früheres Update wurde nicht abgeschlossen (PendingText): erst wiederherstellen (OfferRepair)
        /// Liest nur und löscht nichts.
        /// </summary>
        public static string InstallBlocker()
        {
            if (InProjectFolder)
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            if (Autostart.IsElevated)
                return "Dock Pilot läuft gerade mit Administratorrechten. Bitte Dock Pilot normal starten und dann aktualisieren.";
            if (PendingRestore() != null) return PendingText;
            return null;
        }

        /// <summary>Läuft die EXE im Projektordner (Erstellen.bat daneben)? Dort entsteht sie beim Bauen.</summary>
        private static bool InProjectFolder
        {
            get { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Erstellen.bat")); }
        }

        /// <summary>Schreibprobe mit Zufallsnamen, CreateNew und DeleteOnClose: berührt nie eine vorhandene Datei.</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".schreibprobe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "Dock Pilot.exe", ManualName = "Anleitung.txt";

        // Ergebnis bis 1.70: eine gemeinsame Datei (nur noch, wenn ein altes Skript die neue EXE startet oder eine
        // Wiederherstellung eine ältere EXE zurückbringt). Ab 1.71 je Installationsziel und Versuch eine eigene Datei;
        // die Versuchskennung (32 Hex-Zeichen) gibt der Neustart mit --nach-update weiter.
        private static string LogFor(string attemptId) { return Path.Combine(Folder, "Update-" + TargetId + "-" + attemptId + ".log"); }
        // Startbestätigung für den Neustarthelfer (Prozess-ID des erhöhten Skripts oder „ABBRUCH“), Bereitschaft des
        // Helfers, Empfangsbestätigung und Freigabe für das erhöhte Skript – je Versuch
        private static string StartFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".start"); }
        private static string ReadyFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".bereit"); }
        private static string ReleaseFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".freigabe"); }
        private static string AckFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".empfangen"); }

        /// <summary>Startzeit dieser Instanz (UTC-Ticks): Die Skripte erkennen die alte Instanz an Prozess-ID und Startzeit.</summary>
        private static long ProcessStartTicks
        {
            get { try { return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; } catch { return 0; } }
        }

        /// <summary>Gültige Versuchskennung (32 Hex-Zeichen, klein)?</summary>
        public static bool IsAttemptId(string id)
        {
            return id != null && Regex.IsMatch(id, "^[0-9a-f]{32}$");
        }

        /// <summary>
        /// ZIP herunterladen, Prüfsumme (aus dem signierten Manifest) kontrollieren und das Update-Skript starten (blockierend – im Hintergrund
        /// aufrufen). Das Skript wartet, bis Dock Pilot beendet ist, prüft Signatur und Prüfsumme erneut, ersetzt EXE und
        /// Anleitung atomar (mit Journal und Rücksicherung) und startet Dock Pilot neu. Die Einstellungen bleiben unberührt.
        /// Danach muss sich Dock Pilot beenden. Ohne Schreibrechte im Programmordner läuft das Skript mit Administratorrechten
        /// in einem geschützten Arbeitsordner; den Neustart übernimmt dann ein Helfer mit normalen Rechten.
        /// Vorlage: RDP Pilot 1.57 (Journalformat 2, Ergebnis je Versuch, Ablauf mit Rechteerhöhung).
        /// </summary>
        public static void Install(ReleaseInfo release) { Install(release, false); }

        /// <param name="tray">Einstellungsfenster war zu: nach dem Update nur im Infobereich starten (--nach-update … /tray).</param>
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
        /// Name der Sperre, die Update- und Reparatur-Skript bis zum Ende des Dateiaustauschs bzw. der Rücksicherung
        /// halten: sitzungsübergreifend (Global\) und je Installationsziel. Dock Pilot hält sie selbst nur kurz zum Aufräumen.
        /// </summary>
        private static string LockName
        {
            get { return @"Global\DockPilot-Update-" + TargetId; }
        }

        /// <summary>Kennung des Installationsziels (24 Zeichen der SHA-256 des Programmordners) für Sperre und Arbeitsordner.</summary>
        private static string TargetId
        {
            get
            {
                string dir = Path.GetFullPath(AppInfo.ExeDirectory).TrimEnd('\\').ToUpperInvariant();
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 24);
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
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256) || release.Manifest == null) throw new Exception(NoZip);
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            if (ScriptRunning()) throw new Exception(Running);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            // Aufräumen nur unter der Sperre des Installationsziels; ist sie belegt, läuft gerade ein Update
            if (!WithTargetLock(CleanupLocked)) throw new Exception(Running);
            if (PendingRestore() != null) throw new Exception(PendingText);
            // eigener Arbeitsordner je Versuch; der Name beginnt mit der Zielkennung, damit die Sperrprüfung nur Ordner
            // dieses Installationsziels betrachtet
            string attemptId = Guid.NewGuid().ToString("N");
            string attempt = Path.Combine(Folder, TargetId + "-" + attemptId);
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            // Download begrenzen: höchstens die von GitHub genannte Größe, in jedem Fall höchstens 64 MB
            long limit = MaxZip;
            if (release.ZipSize > 0)
            {
                if (release.ZipSize > MaxZip) throw new Exception("Das Update ist ungewöhnlich groß und wird nicht geladen.");
                limit = release.ZipSize;
            }
            DownloadLimited(release.ZipUrl, zip, limit);
            if (release.ZipSize > 0 && new FileInfo(zip).Length != release.ZipSize)
            {
                try { File.Delete(zip); } catch { }
                throw new Exception("Download unvollständig.");
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
            string logFile = LogFor(attemptId);
            string exe = Application.ExecutablePath;
            bool elevated = !CanWrite(Path.GetDirectoryName(exe));
            // Erhöht: eigener, nicht vorhersagbarer Arbeitsordner direkt unter ProgramData, den das Skript nur für
            // Administratoren beschreibbar anlegt. Sonst reicht der Arbeitsordner im Temp-Ordner des Benutzers.
            string work = elevated
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DockPilot-Update-" + TargetId + "-" + Guid.NewGuid().ToString("N"))
                : attempt;
            int pid = Process.GetCurrentProcess().Id;
            // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst und nimmt die Prüfsumme nur daraus
            string manifestFile = Path.Combine(attempt, ManifestName(release.Version));
            File.WriteAllBytes(manifestFile, release.Manifest);
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            Var(vars, "Zip", zip);
            vars.Append("$ZipSize = ").Append(new FileInfo(zip).Length).Append("\n");
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "TempRoot", Folder);
            Var(vars, "Log", logFile);
            Var(vars, "SharedLog", "");
            vars.Append("$LegacyFallback = $false\n");
            Var(vars, "AttemptId", attemptId);
            Var(vars, "ReleaseId", attemptId);
            Var(vars, "ReleaseFile", elevated ? ReleaseFileFor(attemptId) : "");
            vars.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Skript als Datei im Arbeitsordner, auf der Befehlszeile nur Lader, Werte und SHA-256 des Skripts:
            // Der Lader liest die Datei einmal in den Speicher, prüft die Prüfsumme und führt genau diese Bytes aus.
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(InstallScript));
            string scriptFile = Path.Combine(attempt, "Update-Skript.txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (SHA256 sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            ProcessStartInfo start = PowerShell(vars + InstallLoader);
            if (!elevated)
            {
                Process.Start(start);
                return;
            }
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten.
            // Zuerst den Neustarthelfer (normale Rechte) – scheitert sein Start, wird nichts Erhöhtes gestartet.
            StartElevated(start, pid, attempt, exe, work, attemptId, "Update", logFile, tray);
        }

        /// <summary>
        /// Ablauf mit Administratorrechten: 1. Neustarthelfer (normale Rechte) starten und auf seine Bereitschaft warten,
        /// 2. erhöhtes Skript starten, 3. dessen Prozess-ID als Startbestätigung für den Helfer schreiben, 4. auf die
        /// Empfangsbestätigung des Helfers warten (bis dahin bleibt der eigene Handle auf den Prozess offen), 5. Freigabe
        /// für genau diesen Versuch schreiben. Das erhöhte Skript ändert erst nach dem Programmende und nur mit Freigabe
        /// etwas; scheitert ein Schritt, fehlt die Freigabe und es bricht ohne Änderung ab.
        /// </summary>
        private static void StartElevated(ProcessStartInfo start, int pid, string attempt, string exe, string work, string attemptId,
                                          string what, string helperLog, bool tray)
        {
            string startFile = StartFileFor(attemptId), readyFile = ReadyFileFor(attemptId);
            string releaseFile = ReleaseFileFor(attemptId), ackFile = AckFileFor(attemptId);
            foreach (string f in new string[] { startFile, readyFile, releaseFile, ackFile }) try { File.Delete(f); } catch { }
            string failed = what + " nicht gestartet: Der Neustart konnte nicht vorbereitet werden. Es wurde nichts verändert.";
            Process helper = StartRestartHelper(pid, attempt, exe, work, attemptId, startFile, readyFile, ackFile, helperLog, tray);
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(readyFile))
            {
                bool exited = true;
                try { exited = helper == null || helper.HasExited; } catch { }
                if (exited || DateTime.UtcNow > until)
                {
                    try { if (!exited) helper.Kill(); } catch { }
                    throw new Exception(failed);
                }
                Thread.Sleep(100);
            }
            start.Verb = "runas";
            Process elevatedProcess;
            try { elevatedProcess = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                WriteStartFile(startFile, "ABBRUCH");
                if (ex.NativeErrorCode == 1223) throw new Exception(what + " abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            catch { WriteStartFile(startFile, "ABBRUCH"); throw; }
            int id = 0;
            try { if (elevatedProcess != null) id = elevatedProcess.Id; } catch { }
            if (id <= 0 || !WriteStartFile(startFile, id.ToString(CultureInfo.InvariantCulture)))
            {
                WriteStartFile(startFile, "ABBRUCH");
                throw new Exception(failed);
            }
            // Zweite Bestätigung: Der Helfer hat die Prozess-ID empfangen und hält einen Handle darauf. Bis dahin bleibt
            // der Handle in elevatedProcess offen, die Prozess-ID kann also nicht neu vergeben werden.
            until = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                string ack = null;
                try { if (File.Exists(ackFile)) ack = File.ReadAllText(ackFile).Trim(); } catch { }
                if (ack == id.ToString(CultureInfo.InvariantCulture)) break;
                bool exited = true;
                try { exited = helper.HasExited; } catch { }
                if (exited || DateTime.UtcNow > until) throw new Exception(failed); // ohne Freigabe: das Skript bricht ohne Änderung ab
                Thread.Sleep(100);
            }
            bool helperGone = true;
            try { helperGone = helper.HasExited; } catch { }
            if (helperGone) throw new Exception(failed);
            // Freigabe zuletzt: fehlt sie, bricht das erhöhte Skript nach dem Programmende ohne Änderung ab
            if (!WriteStartFile(releaseFile, attemptId)) throw new Exception(failed);
            GC.KeepAlive(elevatedProcess);
        }

        private static bool WriteStartFile(string file, string text)
        {
            try
            {
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                return true;
            }
            catch { return false; }
        }

        private static Process StartRestartHelper(int pid, string attempt, string exe, string work, string attemptId,
                                                  string startFile, string readyFile, string ackFile, string log, bool tray)
        {
            StringBuilder r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(r, "Attempt", attempt);
            Var(r, "Exe", exe);
            Var(r, "Log", log);
            Var(r, "Work", work);
            // Wiederherstellung (attempt leer): die wiederhergestellte EXE kann älter sein; Start-Program prüft ihre Version
            Var(r, "AttemptId", attemptId);
            r.Append("$LegacyFallback = $").Append(string.IsNullOrEmpty(attempt) ? "true" : "false").Append("\n");
            Var(r, "StartFile", startFile);
            Var(r, "ReadyFile", readyFile);
            Var(r, "AckFile", ackFile);
            Var(r, "SharedLog", string.IsNullOrEmpty(attempt) ? LogFile : "");
            r.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            return Process.Start(PowerShell(r + RestartScript));
        }

        // --- Nicht abgeschlossenes Update (Globale Richtlinien, Selbst-Update › Offene Wiederherstellung) ----------

        /// <summary>Merker im Arbeitsordner: Rücksicherung unvollständig (Format 2: „Journalformat 2“ und der Alt-Ordner).</summary>
        private const string PendingMarker = "Wiederherstellung-offen";

        /// <summary>Kurzmeldung für Anwender: Ein Update wurde nicht abgeschlossen (Technisches steht im Fehlerprotokoll).</summary>
        private const string PendingText = "Das letzte Update wurde nicht abgeschlossen.";

        /// <summary>
        /// Arbeitsordner eines nicht abgeschlossenen Updates für dieses Installationsziel (null = keiner). Nur lesend:
        /// Aufgeräumt wird ausschließlich in CleanupLocked unter der Sperre des Installationsziels.
        /// </summary>
        private static string PendingRestore()
        {
            try { foreach (string dir in PendingDirs()) return dir; }
            catch { }
            return null;
        }

        /// <summary>Arbeitsordner mit Merker „Wiederherstellung-offen“ für dieses Ziel (Temp-Ordner und ProgramData).</summary>
        private static List<string> PendingDirs()
        {
            List<string> dirs = new List<string>();
            if (Directory.Exists(Folder)) dirs.AddRange(Directory.GetDirectories(Folder, TargetId + "-*"));
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            dirs.AddRange(Directory.GetDirectories(data, "DockPilot-Update-" + TargetId + "-*"));
            return dirs.FindAll(d => File.Exists(Path.Combine(d, PendingMarker)));
        }

        /// <summary>
        /// Sperre des Installationsziels kurz selbst halten (dieselbe Mutex wie Update- und Reparatur-Skript), damit
        /// Aufräumen und Prüfen nie gleichzeitig mit einem laufenden Update oder einer Wiederherstellung geschehen.
        /// false = gerade gesperrt (ein Skript läuft) – dann nichts tun.
        /// </summary>
        private static bool WithTargetLock(Action action)
        {
            Mutex m;
            try { m = new Mutex(false, LockName); }
            catch (UnauthorizedAccessException) { return false; } // von einem Skript mit Administratorrechten gehalten
            using (m)
            {
                bool have;
                try { have = m.WaitOne(0); }
                catch (AbandonedMutexException) { have = true; }
                if (!have) return false;
                try { action(); }
                finally { m.ReleaseMutex(); }
                return true;
            }
        }

        /// <summary>
        /// Unter der Sperre: Temp-Arbeitsordner, bei denen laut Journal nichts zurückzusetzen ist, still entfernen, und
        /// abgebrochene Versuche (älter als ein Tag) aufräumen. Unter ProgramData wird nie still aufgeräumt: Dock Pilot
        /// aktualisiert nie mit Adminrechten (InstallBlocker greift vorher), offene Ordner dort werden per Rückfrage
        /// wiederhergestellt.
        /// </summary>
        public static void QuietCleanup()
        {
            try { WithTargetLock(CleanupLocked); } catch { }
        }

        private static void CleanupLocked()
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string dir in PendingDirs())
            {
                if (!dir.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!NothingToRestore(dir)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    Log.Write("Update", "Nicht abgeschlossenes Update aufgeräumt, es war nichts zurückzusetzen: " + dir);
                }
                catch { }
            }
            CleanOldAttempts();
        }

        /// <summary>„Vorher“-Eintrag im Wiederherstellungsjournal.</summary>
        private sealed class JournalEntry { public string N, Kind, Dst, Hash, Tmp; }

        // Formate des Wiederherstellungsjournals – alle werden ausdrücklich unterschieden, alles andere gilt als
        // unbekannt (Ordner und Sicherungen bleiben erhalten):
        // - Format 2 (ab 1.71): Merker „Journalformat 2“ + Alt-Ordner; Journal mit Formatzeile, „Geplant 1..n“, „Bereit“,
        //   Sicherungen mit SHA-256, „Ersetzt k“ direkt nach „Vorher k“ (Pflicht). Journal entsteht vor dem Merker.
        // - Vorstufe (Stand WindowGrid 1.67, in Dock Pilot nie veröffentlicht): Merker nur Alt-Ordner; Journal mit „Bereit“
        //   (ohne Formatzeile und Plan), Sicherungen mit SHA-256, „Ersetzt“ kann fehlen.
        // - Format 1.69/1.70: Merker nur Alt-Ordner; Journal ohne „Bereit“, Sicherungen ohne Prüfsumme, „Ersetzt“ kann
        //   fehlen. Der Merker entstand vor dem Journal.
        // Fehlt bei einem Merker im alten Aufbau das Journal, ist der Stand ungeklärt.
        private const string JournalFormat = "Journalformat 2";
        private enum MarkerKind { Unknown, Legacy, Format2 }

        /// <summary>Merker lesen: Format 2 = „Journalformat 2“ + Alt-Ordner; alter Aufbau = nur der Alt-Ordner; sonst Unknown.</summary>
        private static MarkerKind ReadMarker(string dir)
        {
            try
            {
                string marker = Path.Combine(dir, PendingMarker);
                if (new FileInfo(marker).Length > 4096) return MarkerKind.Unknown;
                List<string> lines = new List<string>();
                foreach (string l in File.ReadAllLines(marker, Encoding.UTF8))
                {
                    string t = l.Trim().TrimStart('\uFEFF');
                    if (t.Length > 0) lines.Add(t);
                }
                string alt = Path.Combine(dir, "Alt");
                if (lines.Count == 2 && lines[0] == JournalFormat && string.Equals(lines[1], alt, StringComparison.OrdinalIgnoreCase)) return MarkerKind.Format2;
                if (lines.Count == 1 && string.Equals(lines[0], alt, StringComparison.OrdinalIgnoreCase)) return MarkerKind.Legacy;
            }
            catch { }
            return MarkerKind.Unknown;
        }

        /// <summary>
        /// Journal streng prüfen – dieselben Regeln wie im Reparatur-Skript. false = Stand ungeklärt (nichts aufräumen,
        /// Wiederherstellung endet mit OFFEN). Bei true enthält entries alle „Vorher“-Einträge in Reihenfolge.
        /// </summary>
        private static bool ReadJournal(string dir, out List<JournalEntry> entries)
        {
            entries = null;
            string exe = Application.ExecutablePath;
            string manual = Path.Combine(Path.GetDirectoryName(exe), ManualName);
            Func<string, bool> allowed = d => string.Equals(d, exe, StringComparison.OrdinalIgnoreCase) || string.Equals(d, manual, StringComparison.OrdinalIgnoreCase);
            MarkerKind kind = ReadMarker(dir);
            if (kind == MarkerKind.Unknown) return false;
            string journal = Path.Combine(dir, "Wiederherstellung.txt");
            if (!File.Exists(journal) || new FileInfo(journal).Length > 64 * 1024) return false;
            string[] lines = File.ReadAllLines(journal, Encoding.UTF8);
            if (lines.Length < 2 || !lines[0].StartsWith("Dock Pilot ", StringComparison.Ordinal) ||
                !lines[0].EndsWith(" - Wiederherstellungsjournal", StringComparison.Ordinal)) return false;
            bool format2 = kind == MarkerKind.Format2;
            bool formatReady = !format2 && Array.IndexOf(lines, "Bereit") >= 0;
            bool withHash = format2 || formatReady;
            List<string> planned = new List<string>();
            List<JournalEntry> list = new List<JournalEntry>();
            int i = 1;
            if (format2)
            {
                if (lines[1] != JournalFormat) return false;
                i = 2;
            }
            // Erklärtext bis zur ersten Zeile mit Schlüsselwort
            string first = format2 ? "Geplant " : formatReady ? "Bereit" : "Vorher ";
            while (i < lines.Length && !lines[i].StartsWith(first, StringComparison.Ordinal))
            {
                if (IsJournalKeyword(lines[i])) return false;
                i++;
            }
            if (format2)
            {
                for (; i < lines.Length && lines[i].StartsWith("Geplant ", StringComparison.Ordinal); i++)
                {
                    Match g = Regex.Match(lines[i], @"^Geplant (\d+): Ziel: (.+)$");
                    if (!g.Success || g.Groups[1].Value != (planned.Count + 1).ToString(CultureInfo.InvariantCulture) || !allowed(g.Groups[2].Value)) return false;
                    string p = g.Groups[2].Value;
                    if (planned.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) return false;
                    planned.Add(p);
                }
                if (planned.Count == 0) return false;
            }
            if (withHash)
            {
                if (i >= lines.Length || lines[i] != "Bereit") return false;
                i++;
            }
            bool replacedLast = true;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                Match e = Regex.Match(line, @"^Ersetzt (\d+)$");
                if (e.Success)
                {
                    if (list.Count == 0 || replacedLast || e.Groups[1].Value != list[list.Count - 1].N) return false;
                    replacedLast = true;
                    continue;
                }
                Match m = withHash
                    ? Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: ([0-9A-F]{64}))? \| Hilfsdatei: (.+)$")
                    : Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)()(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$");
                if (!m.Success) return false;
                JournalEntry entry = new JournalEntry();
                entry.N = m.Groups[1].Value; entry.Kind = m.Groups[2].Value; entry.Dst = m.Groups[3].Value;
                entry.Hash = m.Groups[4].Value; entry.Tmp = m.Groups[5].Value;
                // fortlaufend ab 1; nur im Format 2 muss die vorige Datei bestätigt sein („Ersetzt“ wird dort mit Abbruch
                // bei Fehler geschrieben, eine fehlende Zeile bedeutet also ein beschädigtes Journal)
                if (entry.N != (list.Count + 1).ToString(CultureInfo.InvariantCulture) || (format2 && list.Count > 0 && !replacedLast) || !allowed(entry.Dst)) return false;
                if (withHash)
                {
                    if (entry.Kind == "Sicherung" && entry.Hash.Length == 0) return false;
                    if (entry.Kind == "Neu" && line.Contains(" | Sicherungsdatei: ")) return false;
                }
                if (format2 && (list.Count >= planned.Count || !string.Equals(planned[list.Count], entry.Dst, StringComparison.OrdinalIgnoreCase))) return false;
                // je Ziel höchstens eine „Vorher“-Zeile; ein doppeltes Ziel macht das Journal widersprüchlich
                if (list.Exists(x => string.Equals(x.Dst, entry.Dst, StringComparison.OrdinalIgnoreCase))) return false;
                list.Add(entry);
                replacedLast = false;
            }
            entries = list;
            return true;
        }

        private static bool IsJournalKeyword(string line)
        {
            return line == "Bereit" || line == JournalFormat || line.StartsWith("Geplant ", StringComparison.Ordinal) ||
                   line.StartsWith("Vorher ", StringComparison.Ordinal) || line.StartsWith("Ersetzt ", StringComparison.Ordinal);
        }

        /// <summary>
        /// true, wenn das Journal die strenge Prüfung besteht und jede eingetragene Datei noch der Sicherung entspricht
        /// bzw. bei „Neu“ nicht vorhanden ist (das Update hat dann nichts verändert). Übrig gebliebene Hilfsdateien werden
        /// erst danach entfernt. Im Zweifel bleibt der Arbeitsordner erhalten.
        /// </summary>
        private static bool NothingToRestore(string dir)
        {
            try
            {
                string target = Path.GetDirectoryName(Application.ExecutablePath);
                List<JournalEntry> entries;
                if (!ReadJournal(dir, out entries)) return false;
                foreach (JournalEntry e in entries)
                {
                    if (e.Kind == "Sicherung")
                    {
                        string bak = Path.Combine(Path.Combine(dir, "Alt"), e.N);
                        // Stromausfall zwischen Kopie und Umbenennen: .teil nur mit Prüfsumme aus dem Journal (unten verglichen)
                        if (!File.Exists(bak) && e.Hash.Length > 0 && File.Exists(bak + ".teil")) bak += ".teil";
                        if (!File.Exists(bak) || !File.Exists(e.Dst)) return false;
                        string hash = Sha256Of(bak);
                        if ((e.Hash.Length > 0 && hash != e.Hash) || hash != Sha256Of(e.Dst)) return false;
                    }
                    else if (File.Exists(e.Dst)) return false;
                }
                foreach (JournalEntry e in entries)
                    // Hilfsdatei nur im Programmordner und mit dem erwarteten Namen entfernen
                    if (string.Equals(Path.GetDirectoryName(e.Tmp), target, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(e.Tmp).StartsWith("." + Path.GetFileName(e.Dst) + ".update-", StringComparison.OrdinalIgnoreCase))
                        try { if (File.Exists(e.Tmp)) File.Delete(e.Tmp); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Nicht abgeschlossenes Update: nachfragen und die vorherige Version wiederherstellen (Dock Pilot wird dazu
        /// beendet und danach neu gestartet). true = Wiederherstellung läuft, Dock Pilot soll sich beenden.
        /// </summary>
        public static bool OfferRepair(IWin32Window owner, bool tray)
        {
            // im Projektordner (Erstellen.bat neben der EXE) wird nie etwas zurückgesetzt, wie beim Update
            if (InProjectFolder) return false;
            // nie aus einer Instanz mit Administratorrechten (sonst liefe die Reparatur mit geerbten Rechten)
            if (Autostart.IsElevated)
            {
                MessageBox.Show(owner, "Dock Pilot läuft gerade mit Administratorrechten. Bitte Dock Pilot normal starten und dann aktualisieren.",
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            // erst unter der Sperre aufräumen, was nichts verändert hat; ist sie belegt, läuft gerade ein Update
            if (!WithTargetLock(CleanupLocked))
            {
                MessageBox.Show(owner, Running, "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            string dir = PendingRestore();
            if (dir == null) return false;
            Log.Write("Update", "Nicht abgeschlossenes Update gefunden: " + dir);
            if (MessageBox.Show(owner,
                PendingText + "\n\nDock Pilot stellt jetzt die vorherige Version wieder her und startet danach neu.\n\nJetzt wiederherstellen?",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return false;
            try { Repair(dir, tray); return true; }
            catch (Exception ex)
            {
                Log.Write("Update", "Wiederherstellung nicht gestartet: " + ex.Message);
                MessageBox.Show(owner, ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private static void Repair(string dir, bool tray)
        {
            if (InProjectFolder) throw new Exception("Im Projektordner wird nichts zurückgesetzt.");
            if (Autostart.IsElevated) throw new Exception("Dock Pilot läuft gerade mit Administratorrechten.");
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool protectedDir = string.Equals(Path.GetDirectoryName(dir), data, StringComparison.OrdinalIgnoreCase);
            string exe = Application.ExecutablePath;
            bool writable = CanWrite(Path.GetDirectoryName(exe));
            // Sicherungen aus dem Temp-Ordner nie mit Administratorrechten zurückkopieren (jedes Programm des Benutzers
            // könnte sie verändert haben); dort hat das Update ohne Adminrechte gearbeitet, also reichen sie auch jetzt.
            if (!protectedDir && !writable)
                throw new Exception("Die vorherige Version lässt sich nicht automatisch wiederherstellen. Bitte Dock Pilot neu herunterladen.");
            bool elevated = protectedDir;
            string attemptId = Guid.NewGuid().ToString("N");
            int pid = Process.GetCurrentProcess().Id;
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            // Die wiederhergestellte EXE kann älter als 1.71 sein: Start-Program prüft ihre Version. Ab 1.71 mit Kennung
            // und eigener Ergebnisdatei, sonst ohne Kennung und mit Kopie in die gemeinsame Update.log
            Var(vars, "AttemptId", attemptId);
            vars.Append("$LegacyFallback = $true\n");
            Var(vars, "ReleaseId", attemptId);
            Var(vars, "ReleaseFile", elevated ? ReleaseFileFor(attemptId) : "");
            vars.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(vars, "Work", dir);
            Var(vars, "Exe", exe);
            Var(vars, "ManualName", ManualName);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFor(attemptId));
            Var(vars, "SharedLog", LogFile);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Reparatur-Skript als Datei im Temp-Ordner, auf der Befehlszeile nur Lader, Werte und SHA-256 (wie beim
            // Update): Ein Austausch der Datei bleibt wirkungslos, und die Befehlszeile bleibt auch bei langen Pfaden kurz.
            Directory.CreateDirectory(Folder);
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(RepairScript));
            string scriptFile = Path.Combine(Folder, "Reparatur-Skript-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (SHA256 sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            ProcessStartInfo start = PowerShell(vars + RepairLoader);
            if (!elevated)
            {
                Process.Start(start);
                return;
            }
            // Neustart mit normalen Rechten über den Neustarthelfer (liest das Ergebnis aus dem geschützten Ordner)
            StartElevated(start, pid, "", exe, dir, attemptId, "Wiederherstellung", LogFor(attemptId), tray);
        }

        /// <summary>
        /// Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen; Ordner mit offener
        /// Wiederherstellung nie. Dazu Reparatur-Skripte, Start- und Freigabedateien und nicht abgeholte Ergebnisse
        /// dieses Ziels nach einem Tag.
        /// </summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(dir, PendingMarker))) continue; // Wiederherstellung offen: nie automatisch löschen
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
                foreach (string pattern in new string[] { "Reparatur-Skript-*.txt", TargetId + "-*.start", TargetId + "-*.bereit",
                    TargetId + "-*.freigabe", TargetId + "-*.empfangen", TargetId + "-*.tmp", "Update-" + TargetId + "-*.log" })
                    foreach (string f in Directory.GetFiles(Folder, pattern))
                        try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { }
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
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(Compact(script)));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            ProcessStartInfo start = new ProcessStartInfo(ps, arguments);
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = system;
            return start;
        }

        /// <summary>Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein.</summary>
        private static string Compact(string script)
        {
            StringBuilder compact = new StringBuilder();
            foreach (string raw in script.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            return compact.ToString();
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
            QuietCleanup();
            string blocker = InstallBlocker();
            if (blocker == PendingText)
            {
                if (OfferRepair(owner, owner == null) && ExitRequested != null) ExitRequested(null, EventArgs.Empty);
                return;
            }
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
                catch (Exception ex) { error = ex; Log.Write("Update automatisch installieren", ex); }
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

        /// <summary>
        /// Ergebnis des letzten Updates („OK x“, „FEHLER …“, „ZURUECK …“, „OFFEN …“) nach dem Neustart mit --nach-update.
        /// attemptId: Versuchskennung aus --nach-update (ab 1.71); ohne Kennung die gemeinsame Datei bis 1.70. Nur das
        /// Ergebnis genau dieses Versuchs und dieses Installationsziels wird gelesen (höchstens 16 KB) und danach entfernt.
        /// </summary>
        public static string LastResult(string attemptId)
        {
            string file = IsAttemptId(attemptId) ? LogFor(attemptId) : LogFile;
            try
            {
                if (!File.Exists(file)) return null;
                string text;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length > 16 * 1024) text = "FEHLER Ergebnis des Updates unlesbar (Update.log zu groß).";
                    else text = Encoding.UTF8.GetString(ReadLimited(fs, 16 * 1024)).Trim().TrimStart('\uFEFF');
                }
                try { File.Delete(file); } catch { }
                return text;
            }
            catch { return null; }
        }

        // Skripte nach RDP Pilot 1.57 (Journalformat 2, Ergebnis je Versuch, Ablauf mit Rechteerhöhung), nur Namen
        // angepasst. Nur ASCII-Anführungszeichen. InstallLoader/RepairLoader stehen mit den Werten auf der Befehlszeile,
        // lesen $ScriptFile einmal in den Speicher, prüfen die SHA-256 gegen $ScriptHash und führen genau diese Bytes aus.
        // RestartScript: Neustarthelfer mit normalen Rechten für Update und Wiederherstellung mit Administratorrechten.
        private const string InstallLoader = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.71 sein und kennt keine Kennung: Dann nur --nach-update, und das Ergebnis wird erst unmittelbar vor dem Start
# in die gemeinsame Datei kopiert, die aeltere Fassungen lesen ($SharedLog).
function Start-Program([bool]$tray) {
  $a = @('--nach-update')
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.71' } catch { $modern = $false } }
  if ($modern -and $AttemptId) { $a += $AttemptId }
  elseif ($SharedLog -and (Test-Path -LiteralPath $Log)) { try { Copy-Item -LiteralPath $Log -Destination $SharedLog -Force } catch {} }
  if ($tray) { $a += '/tray' }
  Start-Process -FilePath $Exe -ArgumentList $a
}
$ErrorActionPreference = 'Stop'
# geschuetzten Arbeitsordner anlegen: Besitzer Administratoren, schreiben nur SYSTEM und Administratoren, lesen angemeldete Benutzer
function New-WorkFolder {
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
  $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
  $root = [Environment]::GetFolderPath('CommonApplicationData')
  if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('DockPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
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
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Update-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Update-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Update-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # eindeutiger Status: Skript nicht geladen, nichts veraendert (FEHLER = bisherige Version darf wieder starten)
  $msg = 'FEHLER Update-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
  if ($Elevated) {
    # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner; den Neustart macht der nicht erhoehte Helfer
    try { New-WorkFolder; $msg | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
  } else {
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # nach der Frist erneut pruefen: laeuft die alte Instanz noch, nicht starten (der Start wuerde ohnehin abgefangen)
    if (Test-OldInstance) {
      try { ($msg + ' Dock Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    }
    elseif ($Tray) { Start-Program $true }
    else { Start-Program $false }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
# Dateien begrenzt lesen bzw. kopieren; waehrenddessen darf niemand schreiben (FileShare.Read). Bei Ueberschreitung Abbruch, Teilkopie weg.
function Read-Limited([string]$path, [long]$max) {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $path) }
    $buf = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $buf.Length) { $k = $fs.Read($buf, $n, $buf.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $buf.Length) { throw ('Die Datei ist unvollstaendig: ' + $path) }
    return ,$buf
  } finally { $fs.Dispose() }
}
function Copy-Limited([string]$src, [string]$dst, [long]$max) {
  $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  $out = $null
  try {
    if ($in.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
    $out = [System.IO.File]::Open($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $buf = New-Object byte[] 65536
    $total = 0
    while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
      $total += $k
      if ($total -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
      $out.Write($buf, 0, $k)
    }
    $out.Dispose()
    $out = $null
  } catch {
    if ($out) { $out.Dispose(); $out = $null; Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue }
    throw
  } finally {
    if ($out) { $out.Dispose() }
    $in.Dispose()
  }
}
# ZIP hoechstens so gross wie heruntergeladen und nie ueber 64 MB
$zipMax = [Math]::Min([long]$ZipSize, [long]67108864)
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
$noRestart = $false
$archive = $null
$zipStream = $null
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # tatsaechliches Token pruefen: erhoeht nur, wenn das Programm es so gestartet hat (geschuetzter Arbeitsordner)
  $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin -and -not $Elevated) { throw 'Das Update laeuft unerwartet mit Administratorrechten.' }
  # erst nach bestaetigtem Ende der alten Instanz Dateien aendern
  $deadline = (Get-Date).AddSeconds(60)
  while (Test-OldInstance) {
    if ((Get-Date) -gt $deadline) { throw 'Dock Pilot wurde nicht beendet.' }
    Start-Sleep -Milliseconds 250
  }
  Start-Sleep -Milliseconds 500
  # erhoeht: nur mit ausdruecklicher Freigabe fuer genau diesen Versuch. Dock Pilot erteilt sie erst, wenn der
  # Neustarthelfer bereit ist und die Startbestaetigung geschrieben wurde; ohne Freigabe nichts veraendern.
  if ($Elevated) {
    $released = $false
    try { if ((Get-Item -LiteralPath $ReleaseFile).Length -le 256) { $released = ([System.IO.File]::ReadAllText($ReleaseFile).Trim() -ceq $ReleaseId) } } catch {}
    if (-not $released) { throw 'Das Update wurde nicht freigegeben.' }
  }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('DockPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen,
    # und nur nachweislich inaktive: gleiches Ziel (diese Sperre halten wir) oder anderes Ziel ohne gehaltene Sperre
    # und mindestens eine Stunde unveraendert; Ordner ohne Zielkennung (aeltere Versionen) nur nach einer Stunde
    Get-ChildItem -LiteralPath $root -Directory -Filter 'DockPilot-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
          $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
          if ($o -eq $admins -or $o -eq $system) {
            $inactive = $false
            if ($_.Name -match '^DockPilot-Update-([0-9A-F]{24})-[0-9a-f]{32}$') {
              if ($Matches[1] -ceq $TargetId) { $inactive = $true }
              else {
                $held = $true
                try { $other = $null; $held = [System.Threading.Mutex]::TryOpenExisting('Global\DockPilot-Update-' + $Matches[1], [ref]$other); if ($other) { $other.Dispose() } } catch { $held = $true }
                $inactive = (-not $held) -and ($_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1))
              }
            } else {
              $inactive = $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1)
            }
            if (Test-Path -LiteralPath (Join-Path $_.FullName 'Wiederherstellung-offen')) { $inactive = $false }
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen (New-WorkFolder steht im Lader)
    New-WorkFolder
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Limited $Zip $zipWork $zipMax
    Copy-Limited $Manifest $manifestWork 8192
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (eigene Herausgeberpruefung, an der Kopie, die verwendet wird)
  $bytes = Read-Limited $manifestWork 8192
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
  # ZIP einmal oeffnen (waehrenddessen darf niemand schreiben oder loeschen), Pruefsumme ueber genau diesen Stream
  # berechnen und denselben Stream entpacken; er bleibt bis nach dem Austausch offen
  $zipStream = [System.IO.File]::Open($zipWork, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  if ($zipStream.Length -gt $zipMax) { throw 'Das Update ist zu gross.' }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [System.BitConverter]::ToString($sha.ComputeHash($zipStream)).Replace('-', '')
  $sha.Dispose()
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $zipStream.Position = 0
  Add-Type -AssemblyName System.IO.Compression
  $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
  # nur bekannte Eintraege (feste Namen im Stammordner), je Datei begrenzt; geschrieben wird direkt aus dem Archiv ins Ziel
  function Find-Entry([string]$name, [long]$max) {
    $hits = @($archive.Entries | Where-Object { $_.FullName -eq $name })
    if ($hits.Count -gt 1) { throw ('Im ZIP steht ' + $name + ' mehrfach.') }
    if ($hits.Count -eq 0) { return $null }
    if ($hits[0].Length -gt $max) { throw ('Im ZIP ist ' + $name + ' zu gross.') }
    return $hits[0]
  }
  # erst vollstaendig in eine Hilfsdatei im Zielordner schreiben, auf den Datentraeger bringen und pruefen,
  # dann das Ziel in einem Schritt ersetzen (File.Replace bzw. File.Move): Das Ziel ist nie halb geschrieben
  function Write-Entry($entry, [string]$dst, [string]$tmp, [long]$max) {
    try {
      $in = $entry.Open()
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
          $buf = New-Object byte[] 65536
          $total = 0
          while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $total += $k
            if ($total -gt $max) { throw ('Im ZIP ist ' + $entry.FullName + ' zu gross.') }
            $out.Write($buf, 0, $k)
          }
          $out.Flush($true)
        } finally { $out.Dispose() }
      } finally { $in.Dispose() }
      if ($total -ne $entry.Length) { throw ('Im ZIP ist ' + $entry.FullName + ' unvollstaendig.') }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  # Ruecksicherung ebenso: Sicherung erst vollstaendig in eine Hilfsdatei im Zielordner, dann das Ziel in einem Schritt ersetzen
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      # [NullString]::Value statt $null: PowerShell macht aus $null sonst einen leeren Text (ungueltiger Pfad)
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $exeEntry = Find-Entry $ExeName 33554432
  if (-not $exeEntry) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($exeEntry, $Exe, 33554432))
  $manualEntry = Find-Entry $ManualName 1048576
  if ($manualEntry) { $pairs += ,@($manualEntry, (Join-Path $target $ManualName), 1048576) }
  # unter der Sperre erneut nach offenen Wiederherstellungen fuer dieses Ziel suchen (ein anderer Versuch kann seit
  # der Vorpruefung im Programm abgebrochen sein); Treffer oder fehlgeschlagene Pruefung = nichts austauschen
  $open = $null
  try {
    $roots = @(@($TempRoot, ($TargetId + '-*')), @([Environment]::GetFolderPath('CommonApplicationData'), ('DockPilot-Update-' + $TargetId + '-*')))
    foreach ($r in $roots) {
      if (Test-Path -LiteralPath $r[0]) {
        foreach ($d in @(Get-ChildItem -LiteralPath $r[0] -Directory -Filter $r[1] -Force -ErrorAction Stop)) {
          if ($d.FullName -ne $Work -and (Test-Path -LiteralPath (Join-Path $d.FullName 'Wiederherstellung-offen'))) { $open = $d.FullName }
        }
      }
    }
  } catch { throw ('Offene Wiederherstellungen konnten nicht geprueft werden: ' + $_.Exception.Message) }
  if ($open) { throw ('Eine fruehere Wiederherstellung ist noch offen (' + $open + ').') }
  # Wiederherstellungsdaten dauerhaft schreiben (WriteThrough + Flush auf den Datentraeger), bevor die zugehoerige
  # Programmdatei ausgetauscht wird: Nach einem Stromausfall darf das Journal nie hinter dem Dateistand zurueckliegen.
  function Write-Durable([string]$path, [string[]]$lines, [bool]$append) {
    if ($append) { $mode = [System.IO.FileMode]::Append } else { $mode = [System.IO.FileMode]::CreateNew }
    $fs = New-Object System.IO.FileStream($path, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 4096, [System.IO.FileOptions]::WriteThrough)
    try {
      $nl = [string][char]13 + [char]10
      $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($lines -join $nl) + $nl)
      $fs.Write($bytes, 0, $bytes.Length)
      $fs.Flush($true)
    } finally { $fs.Dispose() }
  }
  function Copy-Durable([string]$src, [string]$dst) {
    $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $out = New-Object System.IO.FileStream($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 81920, [System.IO.FileOptions]::WriteThrough)
      try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
      if ((Get-Item -LiteralPath $dst).Length -ne $in.Length) { throw ('Sicherung von ' + $src + ' unvollstaendig.') }
    } finally { $in.Dispose() }
  }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  # Journal zuerst (Format 2: geplante Dateien, dann ""Bereit""), dann der Status: Ein Status ohne vollstaendiges Journal gilt als beschaedigt.
  # Wiederherstellungsjournal: vor jedem Austausch Zielpfad, Sicherungsdatei und ob das Ziel vorher existierte
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  $plan = @()
  $k = 0
  foreach ($p in $pairs) { $k++; $plan += ('Geplant ' + $k + ': Ziel: ' + $p[1]) }
  $journalHead = @((('Dock Pilot ' + $Version + ' - Wiederherstellungsjournal'), 'Journalformat 2',
    'Jede Zeile ""Vorher"" gilt als moeglicherweise ueberschrieben, auch ohne folgende Zeile ""Ersetzt"" (Abbruch waehrend des Kopierens).',
    'Wiederherstellen fuer jede Zeile ""Vorher"": bei ""Sicherung"" die Sicherungsdatei auf das Ziel zurueckkopieren (sie ist vollstaendig), bei ""Neu"" die Zieldatei loeschen, falls vorhanden.',
    '""Ersetzt n"" bestaetigt nur, dass der Austausch dieser Datei abgeschlossen wurde. Eine uebrig gebliebene ""Hilfsdatei"" kann geloescht werden.',
    '') + $plan + @('Bereit'))
  Write-Durable $journal $journalHead $false
  # Status vor dem ersten Ueberschreiben dauerhaft anlegen: bricht der Austausch ab (auch hart beendet), bleibt der
  # Arbeitsordner geschuetzt und neue Updates gesperrt. Laesst er sich nicht schreiben, wird nichts ausgetauscht.
  $pending = Join-Path $Work 'Wiederherstellung-offen'
  Write-Durable $pending @('Journalformat 2', $alt) $false
  if (-not (Test-Path -LiteralPath $pending)) { throw 'Statusdatei konnte nicht angelegt werden.' }
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $tmp = Join-Path (Split-Path $p[1] -Parent) ('.' + (Split-Path $p[1] -Leaf) + '.update-' + [Guid]::NewGuid().ToString('N'))
      $had = Test-Path -LiteralPath $p[1]
      if ($had) {
        # erst vollstaendig unter Hilfsnamen kopieren, dann umbenennen: vorhandene Sicherungsdatei = vollstaendig
        Remove-Item -LiteralPath ($bak + '.teil') -Force -ErrorAction SilentlyContinue
        Copy-Durable $p[1] ($bak + '.teil')
        Move-Item -LiteralPath ($bak + '.teil') -Destination $bak -Force
        $bakHash = (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash
        $line = 'Vorher ' + $i + ': Sicherung | Ziel: ' + $p[1] + ' | Sicherungsdatei: ' + $bak + ' | SHA256: ' + $bakHash + ' | Hilfsdatei: ' + $tmp
      } else {
        $line = 'Vorher ' + $i + ': Neu | Ziel: ' + $p[1] + ' | Hilfsdatei: ' + $tmp
      }
      Write-Durable $journal @($line) $true
      $done += ,@($p[1], $bak, $had)
      Write-Entry $p[0] $p[1] $tmp $p[2]
      Write-Durable $journal @('Ersetzt ' + $i) $true
    }
  } catch {
    # jede Datei einzeln zuruecksetzen, Fehler sammeln; bei unvollstaendiger Ruecksicherung Sicherung behalten und nicht neu starten
    $err = $_.Exception.Message
    $failed = @()
    foreach ($d in $done) {
      try {
        if ($d[2]) { Restore-File $d[1] $d[0] }
        else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction Stop }
      } catch { $failed += $d[0] }
    }
    if ($failed.Count -gt 0) {
      # Status bleibt stehen: Arbeitsordner wird nie automatisch bereinigt, neue Updates sind gesperrt
      $noRestart = $true
      throw ('Ruecksicherung unvollstaendig (' + ($failed -join ', ') + '). Die alten Dateien liegen in ' + $alt + ', die Zuordnung steht in ' + $journal + '. Fehler: ' + $err)
    }
    # Ruecksicherung vollstaendig bestaetigt: Status entfernen; klappt das nicht, sicherheitshalber nicht neu starten
    try { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop } } catch { $noRestart = $true }
    throw
  }
  # Installation vollstaendig: erst Status entfernen, dann die Rueckfallkopien
  try { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop }
  catch { $noRestart = $true; throw ('Update installiert, aber der Status in ' + $Work + ' konnte nicht entfernt werden.') }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  $archive.Dispose()
  $zipStream.Dispose()
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  if ($archive) { try { $archive.Dispose() } catch {} }
  if ($zipStream) { try { $zipStream.Dispose() } catch {} }
  # Abschlussstatus: FEHLER = nichts veraendert bzw. vollstaendig zurueckgesetzt (Neustart erlaubt),
  # OFFEN = Ruecksicherung unvollstaendig (kein Neustart, Sicherung bleibt)
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der nicht erhoehte Prozess
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if (-not $noRestart) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated -and -not $noRestart) {
  # nur starten, wenn die alte Instanz beendet ist (z. B. nicht nach Ablauf der Wartefrist)
  if (Test-OldInstance) {
    try { ($result + ' Dock Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  }
  elseif ($Tray) { Start-Program $true }
  else { Start-Program $false }
}
";

        private const string RepairLoader = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.71 sein und kennt keine Kennung: Dann nur --nach-update, und das Ergebnis wird erst unmittelbar vor dem Start
# in die gemeinsame Datei kopiert, die aeltere Fassungen lesen ($SharedLog).
function Start-Program([bool]$tray) {
  $a = @('--nach-update')
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.71' } catch { $modern = $false } }
  if ($modern -and $AttemptId) { $a += $AttemptId }
  elseif ($SharedLog -and (Test-Path -LiteralPath $Log)) { try { Copy-Item -LiteralPath $Log -Destination $SharedLog -Force } catch {} }
  if ($tray) { $a += '/tray' }
  Start-Process -FilePath $Exe -ArgumentList $a
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Reparatur-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Reparatur-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Reparatur-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # nichts veraendert. Erhoeht: ohne Update.log beenden (Neustarthelfer: Ergebnis unbekannt, kein Neustart).
  # Ohne Adminrechte: FEHLER ins Protokoll und die bisherige Version wieder starten.
  if (-not $Elevated) {
    $msg = 'FEHLER Reparatur-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-OldInstance)) { Start-Program ([bool]$Tray) }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        private const string RepairScript = @"
$ErrorActionPreference = 'Stop'
$result = $null
$noRestart = $false
$haveLock = $false
$lock = $null
$workTrusted = $false
$target = Split-Path $Exe -Parent
try {
  $deadline = (Get-Date).AddSeconds(60)
  while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Test-OldInstance) { throw 'Dock Pilot wurde nicht beendet.' }
  # im Projektordner (Erstellen.bat neben der EXE) nie etwas zuruecksetzen
  if (Test-Path -LiteralPath (Join-Path $target 'Erstellen.bat')) { throw 'Im Projektordner wird nichts zurueckgesetzt.' }
  # tatsaechliches Token pruefen: erhoeht nur, wenn das Programm es so gestartet hat
  $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin -and -not $Elevated) { throw 'Die Wiederherstellung laeuft unerwartet mit Administratorrechten.' }
  # erhoeht: nur mit ausdruecklicher Freigabe fuer genau diesen Versuch. Dock Pilot erteilt sie erst, wenn der
  # Neustarthelfer bereit ist und die Startbestaetigung geschrieben wurde; ohne Freigabe nichts veraendern.
  if ($Elevated) {
    $released = $false
    try { if ((Get-Item -LiteralPath $ReleaseFile).Length -le 256) { $released = ([System.IO.File]::ReadAllText($ReleaseFile).Trim() -ceq $ReleaseId) } } catch {}
    if (-not $released) { throw 'Die Wiederherstellung wurde nicht freigegeben.' }
  }
  # dieselbe Sperre wie das Update: nie zwei Wiederherstellungen oder Update und Wiederherstellung gleichzeitig
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # unter der Sperre erneut pruefen: Eine andere Wiederherstellung kann inzwischen fertig sein
  if (-not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))) { throw 'Es ist keine Wiederherstellung mehr offen.' }
  $item = Get-Item -LiteralPath $Work -Force
  if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Unerwarteter Arbeitsordner.' }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('DockPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner ist nicht geschuetzt.' }
  }
  # Erhoeht: Arbeitsordner, Journal, Ordner Alt und jede Sicherung muessen den Administratoren oder SYSTEM gehoeren,
  # duerfen keine Umleitung sein und duerfen fuer niemand anderen beschreibbar sein (wirksame ACL, ohne reine
  # Vererbungseintraege). Sonst koennte ein Prozess ohne Adminrechte untergeschobene Dateien zuruecksetzen lassen.
  $trusted = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
  $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
  function Assert-Protected([string]$p) {
    if (-not $Elevated) { return }
    $it = Get-Item -LiteralPath $p -Force
    if ($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw ('Unerwartete Umleitung: ' + $p) }
    $acl = Get-Acl -LiteralPath $p
    if ($trusted -notcontains $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value) { throw ('Nicht geschuetzt (Besitzer): ' + $p) }
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
      if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
      if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
      if ($trusted -contains $rule.IdentityReference.Value) { continue }
      if (([int64]$rule.FileSystemRights -band $writeMask) -ne 0) { throw ('Nicht geschuetzt (Schreibrecht fuer ' + $rule.IdentityReference.Value + '): ' + $p) }
    }
  }
  Assert-Protected $Work
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $allowed = @($Exe, (Join-Path $target $ManualName))
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  # Journal vollstaendig und streng pruefen, bevor irgendetwas zurueckgesetzt wird (dieselben Regeln wie im Programm,
  # ReadMarker/ReadJournal). Passt etwas nicht: Stand ungeklaert, Merker bleibt, kein Neustart (OFFEN).
  # Drei Formate: Format 2 (Merker ""Journalformat 2"" + Alt-Ordner; Formatzeile, ""Geplant 1..n"", ""Bereit"", Hash,
  # ""Ersetzt"" Pflicht), Format Vorstufe 1.71 (Merker nur Alt-Ordner; ""Bereit"", Hash, ""Ersetzt"" optional), Format 1.69/1.70
  # (Merker nur Alt-Ordner; ohne ""Bereit"" und Hash). Unbekannter Merker oder fehlendes Journal: ungeklaert.
  $noRestart = $true
  $markerFile = Join-Path $Work 'Wiederherstellung-offen'
  Assert-Protected $markerFile
  if ((Get-Item -LiteralPath $markerFile).Length -gt 4096) { throw 'Der Status ist zu gross.' }
  $altDir = Join-Path $Work 'Alt'
  $mlines = @([System.IO.File]::ReadAllLines($markerFile) | ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } | Where-Object { $_.Length -gt 0 })
  if ($mlines.Count -eq 2 -and $mlines[0] -ceq 'Journalformat 2' -and $mlines[1] -eq $altDir) { $format2 = $true }
  elseif ($mlines.Count -eq 1 -and $mlines[0] -eq $altDir) { $format2 = $false }
  else { throw 'Der Status ist nicht lesbar.' }
  $entries = @()
  $keywords = '^(Bereit$|Journalformat 2$|Geplant |Vorher |Ersetzt )'
  if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { throw 'Das Wiederherstellungsjournal fehlt.' }
  Assert-Protected $journal
  if ((Get-Item -LiteralPath $journal).Length -gt 65536) { throw 'Das Wiederherstellungsjournal ist zu gross.' }
  if (Test-Path -LiteralPath $altDir) { Assert-Protected $altDir }
  $workTrusted = $true
  $lines = [System.IO.File]::ReadAllLines($journal)
  if ($lines.Count -lt 2 -or -not $lines[0].StartsWith('Dock Pilot ') -or -not $lines[0].EndsWith(' - Wiederherstellungsjournal')) { throw 'Das Journal ist nicht lesbar.' }
  $format155 = (-not $format2) -and ($lines -ccontains 'Bereit')
  $withHash = $format2 -or $format155
  $planned = @()
  $i = 1
  if ($format2) {
    if ($lines[1] -cne 'Journalformat 2') { throw 'Das Journal ist nicht lesbar.' }
    $i = 2
  }
  if ($format2) { $first = 'Geplant ' } elseif ($format155) { $first = 'Bereit' } else { $first = 'Vorher ' }
  while ($i -lt $lines.Count -and -not $lines[$i].StartsWith($first)) { if ($lines[$i] -cmatch $keywords) { throw 'Das Journal ist nicht lesbar.' }; $i++ }
  if ($format2) {
    while ($i -lt $lines.Count -and $lines[$i].StartsWith('Geplant ')) {
      if ($lines[$i] -cnotmatch '^Geplant (\d+): Ziel: (.+)$') { throw 'Das Journal ist nicht lesbar.' }
      if ($Matches[1] -ne [string]($planned.Count + 1) -or $allowed -notcontains $Matches[2] -or $planned -contains $Matches[2]) { throw 'Unerwarteter Eintrag im Journal.' }
      $planned += $Matches[2]
      $i++
    }
    if ($planned.Count -eq 0) { throw 'Das Journal ist unvollstaendig.' }
  }
  if ($withHash) {
    if ($i -ge $lines.Count -or $lines[$i] -cne 'Bereit') { throw 'Das Journal ist unvollstaendig.' }
    $i++
  }
  $replacedLast = $true
    for (; $i -lt $lines.Count; $i++) {
      $line = $lines[$i]
      if ($line -cmatch '^Ersetzt (\d+)$') {
        if ($entries.Count -eq 0 -or $replacedLast -or $Matches[1] -ne $entries[$entries.Count - 1].N) { throw 'Das Journal ist nicht lesbar.' }
        $replacedLast = $true
        continue
      }
      if ($withHash) { $ok = $line -cmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: ([0-9A-F]{64}))? \| Hilfsdatei: (.+)$' }
      else { $ok = $line -cmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)()(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$' }
      if (-not $ok) { throw 'Das Journal ist nicht lesbar.' }
      $e = @{ N = $Matches[1]; Kind = $Matches[2]; Dst = $Matches[3]; Hash = [string]$Matches[4]; Tmp = $Matches[5] }
      if ($e.N -ne [string]($entries.Count + 1) -or ($format2 -and $entries.Count -gt 0 -and -not $replacedLast) -or $allowed -notcontains $e.Dst) { throw 'Unerwarteter Eintrag im Journal.' }
      if ($format2 -and ($entries.Count -ge $planned.Count -or $planned[$entries.Count] -ne $e.Dst)) { throw 'Unerwarteter Eintrag im Journal.' }
      if ($withHash) {
        if ($e.Kind -eq 'Sicherung' -and $e.Hash.Length -eq 0) { throw 'Das Journal ist nicht lesbar.' }
        if ($e.Kind -eq 'Neu' -and $line.Contains(' | Sicherungsdatei: ')) { throw 'Das Journal ist nicht lesbar.' }
      }
      if ($e.Kind -eq 'Sicherung') {
        $alt = Join-Path $Work 'Alt'
        $e.Bak = Join-Path $alt $e.N
        # Stromausfall zwischen Kopie und Umbenennen: die vollstaendige .teil-Datei nur mit passender Pruefsumme nehmen
        if (-not (Test-Path -LiteralPath $e.Bak -PathType Leaf) -and $e.Hash.Length -gt 0 -and (Test-Path -LiteralPath ($e.Bak + '.teil') -PathType Leaf)) { $e.Bak = $e.Bak + '.teil' }
        if (-not (Test-Path -LiteralPath $e.Bak -PathType Leaf)) { throw ('Sicherung fehlt: ' + $e.Bak) }
        Assert-Protected $alt
        Assert-Protected $e.Bak
        $bakHash = (Get-FileHash -LiteralPath $e.Bak -Algorithm SHA256).Hash
        if ($e.Hash.Length -gt 0 -and $bakHash -ne $e.Hash) { throw ('Sicherung veraendert: ' + $e.Bak) }
        $e.Hash = $bakHash
      }
      foreach ($x in $entries) { if ($x.Dst -eq $e.Dst) { throw 'Das Journal nennt ein Ziel doppelt.' } }
      $entries += ,$e
      $replacedLast = $false
  }
  # ab hier kann schon eine Datei zurueckgesetzt sein: jeder Abbruch ist OFFEN (Merker bleibt, kein Neustart)
  $failed = @()
  foreach ($e in $entries) {
    $kind = $e.Kind; $dst = $e.Dst; $tmp = $e.Tmp
    try {
      if ($kind -eq 'Sicherung') {
        $bak = $e.Bak
        $same = (Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq $e.Hash)
        if (-not $same) { Restore-File $bak $dst }
      } elseif (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Force
      }
      if ((Split-Path $tmp -Parent) -eq $target -and (Split-Path $tmp -Leaf) -like ('.' + (Split-Path $dst -Leaf) + '.update-*')) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      }
    } catch { $failed += ($dst + ': ' + $_.Exception.Message) }
  }
  if ($failed.Count -gt 0) { $noRestart = $true; throw ('Wiederherstellung unvollstaendig (' + ($failed -join '; ') + ').') }
  Remove-Item -LiteralPath (Join-Path $Work 'Wiederherstellung-offen') -Force
  # vollstaendig zurueckgesetzt und Merker entfernt: Neustart erlaubt
  $noRestart = $false
  $result = 'ZURUECK Die vorherige Version wurde wiederhergestellt.'
} catch {
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if ($Elevated) {
  # Ergebnis nur in einen gepruefen Arbeitsordner; sonst ohne Update.log beenden (Neustarthelfer: Ergebnis unbekannt,
  # kein Neustart)
  if (-not $workTrusted) { if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }; exit 1 }
  try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if ($result.StartsWith('ZURUECK')) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
  if (-not $noRestart -and -not (Test-OldInstance)) {
    if ($Tray) { Start-Program $true }
    else { Start-Program $false }
  }
}
";

        private const string RestartScript = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.71 sein und kennt keine Kennung: Dann nur --nach-update, und das Ergebnis wird erst unmittelbar vor dem Start
# in die gemeinsame Datei kopiert, die aeltere Fassungen lesen ($SharedLog).
function Start-Program([bool]$tray) {
  $a = @('--nach-update')
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.71' } catch { $modern = $false } }
  if ($modern -and $AttemptId) { $a += $AttemptId }
  elseif ($SharedLog -and (Test-Path -LiteralPath $Log)) { try { Copy-Item -LiteralPath $Log -Destination $SharedLog -Force } catch {} }
  if ($tray) { $a += '/tray' }
  Start-Process -FilePath $Exe -ArgumentList $a
}
$ErrorActionPreference = 'SilentlyContinue'
# Startbestaetigung abwarten: Dock Pilot schreibt nach dem Start des erhoehten Skripts dessen Prozess-ID in $StartFile
# (oder ABBRUCH). Endet Dock Pilot ohne Bestaetigung bzw. kommt ABBRUCH, wurde nichts Erhoehtes gestartet: beenden.
$Installer = 0
# Bereitschaft melden: erst danach startet Dock Pilot das erhoehte Skript
try { 'bereit' | Out-File -LiteralPath $ReadyFile -Encoding ascii } catch {}
$end = (Get-Date).AddMinutes(15)
while ((Get-Date) -lt $end) {
  if (Test-Path -LiteralPath $StartFile) {
    $s = [string](Get-Content -LiteralPath $StartFile -TotalCount 1)
    Remove-Item -LiteralPath $StartFile -Force
    if ($s.Trim() -match '^[0-9]{1,10}$') { $Installer = [int]$s.Trim() }
    break
  }
  if (-not (Test-OldInstance)) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path -LiteralPath $StartFile)) { break }
  } else { Start-Sleep -Milliseconds 250 }
}
if ($Installer -le 0) { exit }
# Handle auf genau diesen Prozess oeffnen (SYNCHRONIZE | QUERY_LIMITED_INFORMATION, auch fuer erhoehte Prozesse
# erlaubt). Dock Pilot haelt seinen eigenen Handle offen, bis diese Bestaetigung da ist: Die Prozess-ID kann bis
# dahin nicht neu vergeben werden, danach haelt dieser Handle den Prozess fest.
$hInst = [IntPtr]::Zero
$waitNote = $null
try {
  $sig = '[DllImport(""kernel32.dll"", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid); [DllImport(""kernel32.dll"", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr handle, int ms); [DllImport(""kernel32.dll"")] public static extern bool CloseHandle(IntPtr handle);'
  $k32 = Add-Type -MemberDefinition $sig -Name 'Warten' -Namespace 'DockPilotNeustart' -PassThru
  $hInst = $k32::OpenProcess(0x00101000, $false, $Installer)
  if ($hInst -eq [IntPtr]::Zero) { $waitNote = 'Kein Zugriff auf das Update (Windows-Fehler ' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() + '), Rueckfall auf die Prozess-ID.' }
} catch { $waitNote = 'Warten ueber Handle nicht moeglich, Rueckfall auf die Prozess-ID: ' + $_.Exception.Message }
# Kein Handle (z. B. UAC mit einem anderen Administratorkonto): Rueckfall auf die Prozess-ID. Bis zur Bestaetigung
# haelt Dock Pilot seinen eigenen Handle offen; die Prozess-ID kann bis dahin nicht neu vergeben werden.
# Empfang bestaetigen: erst danach gibt Dock Pilot das erhoehte Skript frei
try { [string]$Installer | Out-File -LiteralPath $AckFile -Encoding ascii } catch { exit }
# $true = laeuft noch. WAIT_OBJECT_0 (0) = beendet, WAIT_TIMEOUT (258) = laeuft; WAIT_FAILED: Fehlercode merken und
# ab dann ueber die Prozess-ID pruefen
function Test-Installer {
  if ($hInst -ne [IntPtr]::Zero) {
    $r = $k32::WaitForSingleObject($hInst, 0)
    if ($r -eq 0) { return $false }
    if ($r -eq 258) { return $true }
    $script:waitNote = 'Warten auf das Update fehlgeschlagen (Windows-Fehler ' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() + '), Rueckfall auf die Prozess-ID.'
    try { [void]$k32::CloseHandle($hInst) } catch {}
    $script:hInst = [IntPtr]::Zero
  }
  return [bool](Get-Process -Id $Installer -ErrorAction SilentlyContinue)
}
# Neustart erst nach bestaetigtem Ende des Installers; bei Fristablauf Fehler melden und nicht starten
$end = (Get-Date).AddMinutes(15)
$workLog = Join-Path $Work 'Update.log'
$finished = $false
while ((Get-Date) -lt $end) {
  if (-not (Test-Installer)) { $finished = $true; break }
  Start-Sleep -Milliseconds 500
}
if ($hInst -ne [IntPtr]::Zero) { try { [void]$k32::CloseHandle($hInst) } catch {} }
if (-not $finished) {
  $msg = 'FEHLER Das Update ist nach 15 Minuten nicht fertig geworden. Dock Pilot wurde nicht neu gestartet.'
  if ($waitNote) { $msg += ' ' + $waitNote }
  $msg | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
Start-Sleep -Milliseconds 300
$text = $null
if ((Test-Path -LiteralPath $workLog) -and (Get-Item -LiteralPath $workLog).Length -le 16384) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
if ($Attempt) { Remove-Item -LiteralPath $Attempt -Recurse -Force }
# Neustart nur bei eindeutigem Abschluss: installiert (OK) oder nichts veraendert bzw. vollstaendig zurueckgesetzt (FEHLER)
$clear = $text -and ($text -match '^(OK|FEHLER|ZURUECK) ') -and -not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))
if (-not $clear) {
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  ($text + ' Dock Pilot wurde nicht neu gestartet.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
$text | Out-File -LiteralPath $Log -Encoding utf8
# alte Instanz muss beendet sein (Ladefehler kommen sehr frueh)
$deadline = (Get-Date).AddSeconds(60)
while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (Test-OldInstance) {
  ($text + ' Dock Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
if ($Tray) { Start-Program $true }
else { Start-Program $false }
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