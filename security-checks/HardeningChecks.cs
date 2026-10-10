using System;
using System.Diagnostics;
using System.IO;

namespace ZompiercerLAN
{
    // 0.4.2: room passwords, room-name binding in J-PAKE and folder retention.
    internal static partial class Program
    {
        private static void SecretChecks()
        {
            foreach (var good in new[] { "004271", "длинный пароль 42", "k7#Pq2!xZr9", new string('x', 64) })
                Require(LanRelayText.ValidSecret(good), "Valid room secret rejected: " + good);
            foreach (var bad in new[] { null, "", "12345", new string('x', 65), "abc\u0001def", "ab\ud800cdef", " 123456", "123456 ", "éabcde" })
                Require(!LanRelayText.ValidSecret(bad), "Invalid room secret accepted: " + bad);
            Require(LanRelayText.NormalizeSecret("  длинный пароль 42 ") == "длинный пароль 42", "Secret not trimmed");
            Require(LanRelayText.NormalizeSecret("éabcde") == "éabcde", "Secret not NFC-normalized");
            Require(LanRelayText.NormalizeSecret("ab\ud800cdef") == "", "Invalid UTF-16 secret survived normalization");
            foreach (var weak in new[] { "123456", "aaaaaaaa", "abcdefgh", "987654321", "qwerty12", "1234567890" })
                Require(LanRelayText.SecretWarning(weak) != null, "Weak room password not warned: " + weak);
            foreach (var strong in new[] { "k7#Pq2!xZr9", "длинный пароль 42", "" })
                Require(LanRelayText.SecretWarning(strong) == null, "Strong or empty room password warned: " + strong);
            Require(LanRelayText.SecretWarning("12345") != null, "Too-short password not reported");
            Console.WriteLine("PASS: room secret validation / normalization / weak-password warnings");
        }

        private static void RoomBindingPakeChecks()
        {
            var room = LanPake.Nonce(); var nonce = LanPake.Nonce();
            // A host password works over the relay context; LAN (v1) accepts only six digits.
            using (var host = new LanPake(true, room, nonce, "длинный пароль 42", LanPake.RoomNameBinding("Поезд")))
            using (var client = new LanPake(false, room, nonce, "длинный пароль 42", LanPake.RoomNameBinding("Поезд")))
            {
                var h1 = host.Round1(); var c1 = client.Round1(); host.AcceptRound1(c1); client.AcceptRound1(h1);
                var h2 = host.Round2(); var c2 = client.Round2(); host.AcceptRound2(c2); client.AcceptRound2(h2);
                var h3 = host.Round3(); var c3 = client.Round3(); host.AcceptRound3(c3); client.AcceptRound3(h3);
                using (var hk = host.DeriveKeys()) using (var ck = client.DeriveKeys())
                    Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(hk.Secret, ck.Secret), "Password J-PAKE keys differ");
            }
            bool refused = false;
            try { new LanPake(true, room, nonce, "длинный пароль 42").Dispose(); } catch (ArgumentException) { refused = true; }
            Require(refused, "LAN J-PAKE accepted a non-PIN secret");
            // A relay that rewrites the room name breaks the exchange before keys exist.
            using (var host = new LanPake(true, room, nonce, "123456", LanPake.RoomNameBinding("Поезд")))
            using (var client = new LanPake(false, room, nonce, "123456", LanPake.RoomNameBinding("Поезд хакера")))
            {
                host.Round1(); var forged = client.Round1();
                ExpectRejected(() => host.AcceptRound1(forged), "Substituted room name passed J-PAKE");
            }
            using (var host = new LanPake(true, room, nonce, "123456", LanPake.RoomNameBinding("Поезд")))
            using (var client = new LanPake(false, room, nonce, "123456"))
            {
                host.Round1(); var lan = client.Round1();
                ExpectRejected(() => host.AcceptRound1(lan), "LAN and relay contexts mixed");
            }
            Console.WriteLine("PASS: J-PAKE with a host password / room name bound into the context / LAN keeps six-digit PINs");
        }

        private static void RetentionChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "zlan-retention-" + Guid.NewGuid().ToString("N"));
            string outside = Path.Combine(Path.GetTempPath(), "zlan-retention-target-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "must survive");
                string backups = Path.Combine(root, "LAN-backups"), checkpoints = Path.Combine(root, "LAN-inventory-checkpoints"), sessions = Path.Combine(root, "LAN-sessions");
                Func<int, string> stamp = i => "202610" + i.ToString("D2") + "-120000-0000000-" + Guid.NewGuid().ToString("N");
                // Backups: 8 complete (days 1..8), an old incomplete (day 0) and a newest incomplete (day 9).
                for (int i = 1; i <= 8; i++) { var d = Directory.CreateDirectory(Path.Combine(backups, stamp(i))).FullName; File.WriteAllText(Path.Combine(d, LanRetention.BackupMarker), "ok"); Directory.CreateDirectory(Path.Combine(d, "AutoSave 1")); File.WriteAllText(Path.Combine(d, "AutoSave 1", "SaveData.sdt"), "x"); }
                string oldIncomplete = Directory.CreateDirectory(Path.Combine(backups, stamp(0))).FullName;
                string newIncomplete = Directory.CreateDirectory(Path.Combine(backups, stamp(9))).FullName;
                string foreign = Directory.CreateDirectory(Path.Combine(backups, "my own folder")).FullName;
                for (int i = 1; i <= 7; i++) File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(checkpoints, stamp(i))).FullName, "latest.txt"), "x");
                // Sessions: 9 folders; the oldest contains a junction to an outside folder; one is protected.
                string linked = null, guarded = null;
                for (int i = 1; i <= 9; i++)
                {
                    string d = Directory.CreateDirectory(Path.Combine(sessions, "202610" + i.ToString("D2") + "-120000-000" + (i % 2 == 0 ? "-" + Guid.NewGuid().ToString("N") : ""))).FullName;
                    if (i == 1) linked = d;
                    if (i == 2) guarded = d;
                }
                var mklink = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(linked, "link") + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                mklink.WaitForExit();
                Require(mklink.ExitCode == 0 && (File.GetAttributes(Path.Combine(linked, "link")) & FileAttributes.ReparsePoint) != 0, "Test junction not created");

                var warnings = new System.Collections.Generic.List<string>();
                int removed = LanRetention.Run(root, guarded, s => { }, warnings.Add);
                Require(Directory.GetDirectories(backups).Length == LanRetention.Keep + 2, "Backups not pruned to the newest complete copies (+ newest incomplete + foreign)");
                Require(!Directory.Exists(oldIncomplete) && Directory.Exists(newIncomplete) && Directory.Exists(foreign), "Wrong backup folders removed");
                foreach (var d in Directory.GetDirectories(backups))
                    if (d != newIncomplete && d != foreign) Require(File.Exists(Path.Combine(d, LanRetention.BackupMarker)), "Incomplete backup kept instead of a complete one");
                Require(Directory.GetDirectories(checkpoints).Length == LanRetention.Keep, "Checkpoint runs not pruned");
                Require(Directory.Exists(guarded), "Active client session folder deleted");
                Require(Directory.Exists(linked) && File.Exists(Path.Combine(outside, "sentinel.txt")) && warnings.Count == 1, "Link-containing folder or its target was deleted");
                Require(Directory.GetDirectories(sessions).Length == LanRetention.Keep + 2, "Session folders not pruned around the protected and linked ones");
                Require(removed == 4 + 2 + 2, "Unexpected removal count " + removed);
                Require(LanRetention.Run(root, guarded, s => { }, s => { }) == 0, "Second run removed more folders");
            }
            finally
            {
                try { foreach (var link in Directory.GetDirectories(root, "link", SearchOption.AllDirectories)) Directory.Delete(link); } catch { }
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
            Console.WriteLine("PASS: retention keeps the newest " + LanRetention.Keep + " backups/checkpoints/sessions, never follows links or deletes the active session");
        }
    }
}
