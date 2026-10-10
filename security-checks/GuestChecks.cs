using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ZompiercerLAN
{
    // 0.5.0: guest profile persistence. Pure parts only (no Unity): value codec,
    // profile schema, chunked transfer, host sidecar, host/guest exchange.
    internal static partial class Program
    {
        private static object[] SampleProfile(int items = 3, string scene = "Terrain1", int car = -1)
        {
            var backpack = new object[items];
            for (int i = 0; i < items; i++) backpack[i] = new object[] { i + 1, 2 + i, new object[] { 50f, 40f, null, null } };
            var stats = new object[LanGuestSchema.StatSlots];
            for (int i = 0; i < stats.Length; i++)
                stats[i] = i == 20 || i == 21 ? null : i == 10 || (i >= 13 && i <= 17) || i >= 22 ? (object)1 : 10f;
            var c = new object[LanGuestSchema.CharacterSlots];
            c[0] = new object[] { backpack, new object[0], new object[] { new object[] { 7, 1, new object[] { 3f, 2f, 1, new object[] { new object[0], null } } } } };
            c[1] = stats; c[5] = new object[] { new object[] { 3 }, new object[] { 0 } };
            c[6] = new object[] { 2, 10f, 100f, 1.5f, 1, 1, 1, 1, 1 }; c[7] = 90f; c[8] = 0;
            c[9] = false; c[10] = 0f; c[11] = false; c[12] = 0f; c[13] = false; c[14] = 0f; c[15] = false; c[16] = true;
            return new object[] { LanGuestSchema.Format, scene, 2, car, 1f, 2f, 3f, 90f, car >= 0 ? 0.5f : 0f, 0f, car >= 0 ? -2f : 0f, 0f, c };
        }
        private static byte[] SampleBlob(int items = 3) { return LanValueCodec.Encode(SampleProfile(items)); }

        private static void GuestChecks()
        {
            CodecAndSchemaChecks();
            BlobChecks();
            GuestProtocolChecks();
            GuestExchangeChecks();
            WelcomeGiftChecks();
            StoreChecks();
        }

        private static void CodecAndSchemaChecks()
        {
            var blob = new byte[] { 1, 2, 3 };
            var round = LanValueCodec.Decode(LanValueCodec.Encode(new object[] { blob, "x", 5, 1.5f, true, null }));
            Require(((byte[])round[0]).SequenceEqual(blob) && (string)round[1] == "x" && (int)round[2] == 5, "Codec roundtrip");
            ExpectRejected(() => LanValueCodec.Encode(new object[] { new byte[LanValueCodec.MaxBlobBytes + 1] }), "Oversized blob encoded");
            ExpectRejected(() => LanValueCodec.Encode(new object[] { 1.0 }), "Unsupported double encoded");
            ExpectRejected(() => LanValueCodec.Encode(new object[] { float.NaN }), "NaN encoded");
            // Inventory.Save() in the game is List<object[]>.ToArray(): an object[][], also when empty.
            object[] saved = new object[][] { new object[] { 7, 1, new object[] { 3f, null, null, new object[] { new object[0][] } } } };
            var native = LanValueCodec.Decode(LanValueCodec.Encode(new object[] { saved, new object[0][] }));
            Require(native[0].GetType() == typeof(object[]) && (int)((object[])((object[])native[0])[0])[0] == 7 && ((object[])native[1]).Length == 0, "Native Inventory.Save() shape not encoded");
            ExpectRejected(() => LanValueCodec.Encode(new object[] { new int[] { 1 } }), "Value-type array encoded");

            LanGuestSchema.Decode(SampleBlob());
            LanGuestSchema.Validate(SampleProfile(3, "Terrain1", 2));
            Func<Action<object[]>, object[]> mutated = change => { var p = SampleProfile(); change(p); return p; };
            var bad = new List<object[]>
            {
                mutated(p => p[0] = 2), mutated(p => p[1] = ""), mutated(p => p[1] = new string('s', 97)), mutated(p => p[3] = 8),
                mutated(p => p[7] = 400f), mutated(p => p[3] = 1), // car index with a 1 m local x is fine; next makes it wrong
                mutated(p => { p[3] = 1; p[8] = 150f; }), mutated(p => ((object[])p[12])[3] = 1),
                mutated(p => ((object[])p[12])[17] = new object[0]), mutated(p => ((object[])((object[])p[12])[1])[20] = 1f),
                mutated(p => ((object[])((object[])p[12])[1])[10] = 1f), mutated(p => ((object[])p[12])[8] = 99),
                mutated(p => ((object[])p[12])[6] = new object[] { 1, 1, 1f, 1f, 1, 1, 1, 1, 1 }),
                mutated(p => ((object[])((object[])p[12])[5])[0] = new object[] { -1 }),
                mutated(p => ((object[])((object[])((object[])p[12])[0])[0])[0] = new object[] { 70000, 1, new object[4] }),
                mutated(p => ((object[])((object[])((object[])p[12])[0])[0])[0] = new object[] { 1, 0, new object[4] }),
                mutated(p => ((object[])((object[])((object[])p[12])[0])[0])[0] = new object[] { 1, 1, new object[3] }),
                mutated(p => ((object[])((object[])((object[])p[12])[0])[0])[0] = new object[] { 1, 1, new object[] { "x", null, null, null } }),
                mutated(p => ((object[])p[12])[0] = new object[33]),
            };
            bad.RemoveAt(5); // the fine case above
            foreach (var p in bad) ExpectRejected(() => LanGuestSchema.Validate(p), "Invalid guest profile accepted");
            // Mod nesting deeper than two levels.
            var deep = SampleProfile();
            object[] nested = new object[0];
            for (int i = 0; i < 4; i++) nested = new object[] { new object[] { 1, 1, new object[] { null, null, null, new object[] { nested } } } };
            ((object[])((object[])deep[12])[0])[0] = nested;
            ExpectRejected(() => LanGuestSchema.Validate(deep), "Deep mod nesting accepted");
            // Mutated encodings never escape as unexpected exception types.
            var random = new Random(55301); var seed = SampleBlob(20);
            for (int i = 0; i < 20000; i++)
            {
                var bytes = (byte[])seed.Clone();
                for (int j = 0; j < 1 + random.Next(4); j++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                if (random.Next(4) == 0) Array.Resize(ref bytes, random.Next(1, bytes.Length));
                try { LanGuestSchema.Decode(bytes); } catch (InvalidDataException) { }
            }
            Console.WriteLine("PASS: guest value codec / profile schema (types, slots, ranges, buffs and positions never stored) / 20000 mutated profiles");
        }

        private static void BlobChecks()
        {
            var data = new byte[LanProtocol.MaxGuestBytes]; new Random(3).NextBytes(data);
            var sender = new LanBlobSender(); var receiver = new LanBlobReceiver();
            int revision = sender.Start(data); byte[] got = null; int sent = 0, dropped = 0; double finished = 0;
            var queue = new List<Packet>();
            for (double now = 0; now < 30 && got == null; now += 0.02)
            {
                sender.Tick(now, 40, 2, p => { sent++; if (sent % 3 == 0) { dropped++; return; } queue.Add(p); queue.Add(p); }, PacketKind.GuestStateChunk);
                foreach (var p in queue.OrderBy(_ => Guid.NewGuid()).ToList())
                {
                    bool ack; var blob = receiver.Accept(p, out ack);
                    if (blob != null) { got = blob; finished = now; }
                    if (ack) sender.Acknowledge(p.Revision);
                }
                queue.Clear();
            }
            Require(got != null && got.SequenceEqual(data) && sender.Done && dropped > 0, "64 KiB blob over a lossy, duplicating, reordering link");
            Require(sent <= 40 * finished + 9, "Blob sender exceeded its pace: " + sent + " chunks in " + finished + " s");
            // A newer revision supersedes; an old completed one only needs an ack.
            int next = sender.Start(new byte[] { 9 });
            bool again; Require(receiver.Accept(new Packet { Revision = revision, ChunkCount = 73, ChunkIndex = 0, Chunk = new byte[900] }, out again) == null && again, "Completed revision not re-acknowledged");
            Packet small = null; sender.Tick(100, 40, 2, p => small = p, PacketKind.GuestStateChunk);
            Require(small.Revision == next && receiver.Accept(small, out again).SequenceEqual(new byte[] { 9 }), "New revision not delivered");
            Require(receiver.Accept(new Packet { Revision = 99, ChunkCount = 2, ChunkIndex = 0, Chunk = new byte[10] }, out again) == null, "Short intermediate chunk accepted");
            ExpectArgument(() => sender.Start(new byte[LanProtocol.MaxGuestBytes + 1]), "Oversized blob started");
            Console.WriteLine("PASS: chunked guest transfer (loss, duplicates, reordering, revisions, pace, limits)");
        }

        private static void GuestProtocolChecks()
        {
            Packet decoded;
            var packets = new[]
            {
                new Packet { Kind = PacketKind.IdentityRequest, Chunk = Guid.NewGuid().ToByteArray() },
                new Packet { Kind = PacketKind.IdentityProof, Chunk = new byte[32] },
                new Packet { Kind = PacketKind.GuestStatus, Action = 1, Revision = 4 },
                new Packet { Kind = PacketKind.GuestStatus, Action = 0, Revision = 0 },
                new Packet { Kind = PacketKind.GuestStateChunk, Revision = 1, ChunkCount = 1, ChunkIndex = 0, Chunk = new byte[10] },
                new Packet { Kind = PacketKind.GuestRestoreChunk, Revision = 2, ChunkCount = 2, ChunkIndex = 0, Chunk = new byte[900] },
                new Packet { Kind = PacketKind.GuestStateAck, Revision = 1 }, new Packet { Kind = PacketKind.GuestRestoreAck, Revision = 2 },
            };
            foreach (var p in packets)
            {
                p.GameVersion = "checks"; p.Session = 42;
                var bytes = LanProtocol.Encode(p);
                Require(LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == p.Kind, "Valid guest packet rejected: " + p.Kind);
                for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated guest packet accepted"); }
                var tail = new byte[bytes.Length + 1]; Array.Copy(bytes, tail, bytes.Length); Require(!LanProtocol.TryDecode(tail, out decoded), "Guest packet trailing bytes accepted");
            }
            var empty = new Packet { Kind = PacketKind.IdentityRequest, GameVersion = "checks", Session = 42, Chunk = new byte[16] };
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(empty), out decoded), "Empty world id accepted");
            var status = new Packet { Kind = PacketKind.GuestStatus, GameVersion = "checks", Session = 42, Action = 1, Revision = 0 };
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(status), out decoded), "Restore status without a revision accepted");
            var huge = new Packet { Kind = PacketKind.GuestStateChunk, GameVersion = "checks", Session = 42, Revision = 1, ChunkCount = LanProtocol.MaxGuestChunks + 1, ChunkIndex = 0, Chunk = new byte[900] };
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(huge), out decoded), "Oversized guest blob accepted");
            foreach (var kind in new[] { PacketKind.IdentityProof, PacketKind.GuestStateChunk, PacketKind.GuestRestoreAck })
                Require(LanNetworkPolicy.Allowed(true, kind, true) && !LanNetworkPolicy.Allowed(false, kind, true) && !LanNetworkPolicy.Allowed(true, kind, false), "Guest-to-host direction: " + kind);
            foreach (var kind in new[] { PacketKind.IdentityRequest, PacketKind.GuestStatus, PacketKind.GuestStateAck, PacketKind.GuestRestoreChunk })
                Require(!LanNetworkPolicy.Allowed(true, kind, true) && LanNetworkPolicy.Allowed(false, kind, true) && !LanNetworkPolicy.Allowed(false, kind, false), "Host-to-guest direction: " + kind);
            var limits = new LanMessageLimits(); int allowed = 0;
            for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.GuestStateChunk, 0)) allowed++;
            Require(allowed == 80, "Guest chunk burst not bounded");
            Console.WriteLine("PASS: guest packets (schema 10+) / truncation / directions / rate limits");
        }

        private static string NewSlot(string root, string name)
        {
            string slot = Path.Combine(root, name); Directory.CreateDirectory(slot);
            foreach (var file in LanGuestStore.NativeFiles) File.WriteAllText(Path.Combine(slot, file), name + file);
            return slot;
        }

        private static void StoreChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "zlan-guests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var store = new LanGuestStore();
                string a = new string('A', 64), b = new string('B', 64), slot = NewSlot(root, "AutoSave 1");
                string result;
                Require(!store.Write(slot, out result) && !File.Exists(Path.Combine(slot, LanGuestStore.FileName)), "Sidecar written without guests");
                store.Put(a, SampleBlob(), DateTime.UtcNow); store.Put(b, SampleBlob(5), DateTime.UtcNow);
                Guid world = store.WorldId;
                Require(store.Write(slot, out result), "Sidecar not written: " + result);
                Require(!Directory.GetFiles(slot, "*.tmp").Any(), "Temporary sidecar left behind");
                var loaded = new LanGuestStore();
                Require(loaded.Load(slot, out result) && loaded.WorldId == world && loaded.Count == 2 && loaded.Get(a).SequenceEqual(SampleBlob()), "Sidecar not restored: " + result);
                loaded.Put(a, SampleBlob(7), DateTime.UtcNow);
                Require(loaded.Write(slot, out result) && new LanGuestStore().Load(slot, out result), "Sidecar overwrite failed");
                // 1.4.14: the slot was saved again without a sidecar write (no guest known in that run): its guests
                // keep their profiles, world id and receipts, and the next save writes the sidecar anew.
                loaded.RecordWelcome(a);
                Require(loaded.Write(slot, out result), "Sidecar with a receipt not written");
                File.AppendAllText(Path.Combine(slot, "SaveData.sdt"), "changed");
                var stale = new LanGuestStore();
                Require(stale.Load(slot, out result) && stale.Count == 2 && stale.WorldId == world && stale.Persistent && stale.WasWelcomed(a) &&
                    stale.Get(a).SequenceEqual(SampleBlob(7)) && result.Contains("changed"), "Guests reset by a re-saved slot: " + result);
                Require(stale.Write(slot, out result) && new LanGuestStore().Load(slot, out result) && result.StartsWith("guest sidecar verified"), "Re-saved slot sidecar not refreshed: " + result);
                // A rotated autosave keeps its own sidecar with its own files.
                string older = NewSlot(root, "AutoSave 2");
                Require(store.Write(older, out result), "Second slot sidecar");
                var rotated = new LanGuestStore();
                Require(rotated.Load(older, out result) && rotated.Get(a).SequenceEqual(SampleBlob()), "Checkpoint-specific profile not restored");
                // Damaged or hostile sidecars are refused without throwing.
                var path = Path.Combine(older, LanGuestStore.FileName); var good = File.ReadAllBytes(path);
                var random = new Random(8123);
                for (int i = 0; i < 2000; i++)
                {
                    var bytes = (byte[])good.Clone(); bytes[random.Next(bytes.Length)] ^= (byte)(1 + random.Next(255));
                    File.WriteAllBytes(path, bytes);
                    var s = new LanGuestStore(); s.Load(older, out result);
                    Require(s.WorldId != Guid.Empty, "Damaged sidecar left no world");
                }
                File.WriteAllBytes(path, good);
                // Limits: at most 16 profiles, the oldest go first.
                var many = new LanGuestStore();
                for (int i = 0; i < 20; i++) many.Put(i.ToString("X64"), SampleBlob(), DateTime.UtcNow);
                Require(many.Count == LanGuestStore.MaxProfiles && many.Get(0.ToString("X64")) == null && many.Get(19.ToString("X64")) != null, "Profile limit/eviction");
                ExpectArgument(() => many.Put("not-a-profile", SampleBlob(), DateTime.UtcNow), "Malformed profile id accepted");
                Require(LanGuestStore.ProfileIdFor(new byte[32]).Length == 64 && LanGuestStore.ValidProfileId(LanGuestStore.ProfileIdFor(new byte[32])), "Profile id format");
                // New game: a new lineage without guests.
                var fresh = new LanGuestStore(); fresh.Put(a, SampleBlob(), DateTime.UtcNow); var before = fresh.WorldId; fresh.NewWorld();
                Require(fresh.Count == 0 && fresh.WorldId != before && !fresh.Persistent, "New game kept old guests");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
            Console.WriteLine("PASS: host guest sidecar (bound to its slot, kept when the slot was saved again without it, rotation, overwrite, 2000 damaged files, limits, new game)");
        }

        // Host and guest wired back to back through the real packet encoder.
        private sealed class GuestLink
        {
            internal readonly List<Packet> ToHost = new List<Packet>(), ToGuest = new List<Packet>();
            internal int Dropped;
            internal Func<Packet, bool> Drop = p => false;
            internal Action<Packet> Sender(List<Packet> queue) => p =>
            {
                if (Drop(p)) { Dropped++; return; }
                p.GameVersion = "checks"; p.Session = 42; Packet copy;
                Require(LanProtocol.TryDecode(LanProtocol.Encode(p), out copy), "Guest packet failed the real decoder: " + p.Kind);
                queue.Add(copy);
            };
        }
        private static void Pump(GuestLink link, LanGuestHost host, LanGuestClient guest, ref double now, double seconds, bool placed, Func<byte[]> capture)
        {
            for (double end = now + seconds; now < end; now += 0.05)
            {
                host.Tick(now); guest.Tick(now, placed, capture);
                foreach (var p in link.ToHost.ToList()) { link.ToHost.Remove(p); host.Receive(p, now); }
                foreach (var p in link.ToGuest.ToList()) { link.ToGuest.Remove(p); guest.Receive(p, now); }
            }
        }

        // Every item id known, no attribute checks: the exchange tests count items only.
        private sealed class CountingRules : ILedgerRules { public LedgerItemRule Item(int id) { return new LedgerItemRule(); } }
        private static long ItemTotal(byte[] blob)
        {
            long total = 0;
            if (blob == null) return -1;
            var root = LanGuestSchema.Decode(blob);
            foreach (object[] inventory in (object[])((object[])root[12])[0]) foreach (object[] item in inventory) total += (int)item[1];
            return total;
        }

        private static void GuestExchangeChecks()
        {
            var store = new LanGuestStore(); var log = new List<string>(); var rules = new CountingRules();
            var tokenA = new byte[32]; new Random(1).NextBytes(tokenA); var tokenB = new byte[32]; new Random(2).NextBytes(tokenB);
            var tokenC = new byte[32]; new Random(3).NextBytes(tokenC);
            string idA = LanGuestStore.ProfileIdFor(tokenA);
            double now = 0;
            Func<GuestLink, LanGuestHost> newHost = l => new LanGuestHost(store, l.Sender(l.ToGuest), log.Add, rules, () => 0);
            // 1. First visit: no stored profile. The first belongings wait for the host's decision.
            var link = new GuestLink();
            var host = newHost(link);
            var guest = new LanGuestClient(w => tokenA, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            var state1 = SampleBlob(4); long total1 = ItemTotal(state1);
            Pump(link, host, guest, ref now, 2, false, () => state1);
            Require(!guest.Waiting(now) && guest.Pending == null && host.ProfileId == idA && store.Get(idA) == null, "First visit: status/identity");
            Pump(link, host, guest, ref now, 12, true, () => state1);
            Require(store.Get(idA) == null && host.PendingKit != null && !host.Confirmed, "First belongings stored without the host's decision");
            host.AcceptKit(true);
            Require(ItemTotal(store.Get(idA)) == total1 && host.Confirmed && host.PendingKit == null, "Accepted belongings not on the ledger");
            // 2. Reconnect later (new pairing): the stored profile comes back before any upload.
            var state2 = SampleBlob(6);
            link = new GuestLink();
            host = newHost(link);
            guest = new LanGuestClient(w => tokenA, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 1, false, () => state2);
            Require(guest.Waiting(now) || guest.Pending != null, "Guest placed before its profile arrived");
            Pump(link, host, guest, ref now, 5, false, () => state2);
            Require(guest.Pending != null && ItemTotal(LanValueCodec.Encode(guest.Pending)) == total1 && !guest.Waiting(now), "Stored profile not delivered");
            // Even if placed, nothing is uploaded until the profile is applied.
            Pump(link, host, guest, ref now, 12, true, () => state2);
            Require(ItemTotal(store.Get(idA)) == total1, "Fresh state overwrote the stored profile before it was applied");
            guest.Applied(true, "restored"); guest.TakePending();
            // More than the ledger is cut back; less is accepted.
            Pump(link, host, guest, ref now, 12, true, () => state2);
            Require(ItemTotal(store.Get(idA)) <= total1 && !guest.CorrectionPending, "Spawned items stored, or corrected after a single state");
            var less = SampleBlob(2); long totalLess = ItemTotal(less);
            Pump(link, host, guest, ref now, 12, true, () => less);
            Require(ItemTotal(store.Get(idA)) == totalLess, "Smaller state not accepted");
            // The same kind of excess twice in a row: the host sends its ledger as a correction.
            int flip = 0;
            Pump(link, host, guest, ref now, 25, true, () => ++flip % 2 == 0 ? state2 : SampleBlob(5));
            Require(guest.CorrectionPending && ItemTotal(LanValueCodec.Encode(guest.Pending)) == totalLess && ItemTotal(store.Get(idA)) == totalLess, "No correction after repeated excess");
            int sent = link.ToHost.Count;
            Pump(link, host, guest, ref now, 12, true, () => state2);
            Require(guest.CorrectionPending && !log.Skip(log.Count - 3).Any(l => l.Contains("rejected")), "States uploaded while a correction is pending");
            guest.Applied(true, "corrected"); guest.TakePending();
            // 3. Another player has a separate profile; a forged state chunk before identity is ignored.
            link = new GuestLink();
            host = newHost(link);
            host.Begin();
            host.Receive(new Packet { Kind = PacketKind.GuestStateChunk, Revision = 1, ChunkCount = 1, ChunkIndex = 0, Chunk = SampleBlob() }, now);
            guest = new LanGuestClient(w => tokenB, link.Sender(link.ToHost), log.Add); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => state1);
            host.AcceptKit(true);
            Require(store.Count == 2 && ItemTotal(store.Get(LanGuestStore.ProfileIdFor(tokenB))) == total1 && ItemTotal(store.Get(idA)) == totalLess, "Profiles mixed between players");
            // 4. A hostile guest state is refused and does not replace the stored profile.
            link = new GuestLink();
            host = newHost(link);
            guest = new LanGuestClient(w => tokenA, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 5, false, () => null);
            guest.Applied(true, "restored"); guest.TakePending();
            var hostile = SampleProfile(); ((object[])hostile[12])[17] = new object[] { 1 };
            Pump(link, host, guest, ref now, 12, true, () => LanValueCodec.Encode(hostile));
            Require(ItemTotal(store.Get(idA)) == totalLess && log.Any(l => l.StartsWith("Guest state rejected")), "Hostile guest state stored");
            // 5. No answer from the host: the guest stops holding the player, uploads nothing,
            // and applies its stored profile in play once the host answers (0.8.5).
            link = new GuestLink { Drop = p => true };
            host = newHost(link);
            guest = new LanGuestClient(w => tokenA, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 31, true, () => state1);
            Require(!guest.Waiting(now) && !guest.Blocked && !guest.Saving && ItemTotal(store.Get(idA)) == totalLess, "Timed-out guest uploaded, kept waiting or gave up");
            link.Drop = p => false;
            Pump(link, host, guest, ref now, 6, true, () => state1);
            Require(guest.CorrectionPending && ItemTotal(LanValueCodec.Encode(guest.Pending)) == totalLess && ItemTotal(store.Get(idA)) == totalLess, "Late stored profile not delivered or overwritten");
            guest.TakePending(); guest.Applied(true, "restored late");
            Require(guest.Saving, "Late profile applied but not saving");
            // 6. Lossy link still converges.
            int counter = 0;
            link = new GuestLink { Drop = p => ++counter % 4 == 0 };
            host = newHost(link);
            guest = new LanGuestClient(w => tokenA, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 8, false, () => less);
            Require(guest.Pending != null && link.Dropped > 0, "Lossy restore failed");
            guest.Applied(true, "restored"); guest.TakePending();
            var state3 = SampleBlob(1);
            Pump(link, host, guest, ref now, 25, true, () => state3);
            Require(ItemTotal(store.Get(idA)) == ItemTotal(state3), "Lossy upload failed");
            // 7. Identity unavailable: no proof, no upload, placement not held.
            link = new GuestLink();
            host = newHost(link);
            guest = new LanGuestClient(w => null, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 3, true, () => state1);
            Require(!guest.Waiting(now) && host.ProfileId == null && ItemTotal(store.Get(idA)) == ItemTotal(state3), "Missing identity handled wrongly");
            // 8. The host refuses a new guest's belongings: the guest is corrected to nothing.
            link = new GuestLink();
            host = newHost(link);
            guest = new LanGuestClient(w => tokenC, link.Sender(link.ToHost), log.Add);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => state2);
            host.AcceptKit(false);
            Pump(link, host, guest, ref now, 3, true, () => state2);
            Require(ItemTotal(store.Get(LanGuestStore.ProfileIdFor(tokenC))) == 0 && guest.CorrectionPending && ItemTotal(LanValueCodec.Encode(guest.Pending)) == 0, "Refused belongings kept");
            Console.WriteLine("PASS: guest exchange on the host's ledger (first belongings by decision, restore before upload, excess cut, correction, separate players, hostile state, timeout, loss, no identity, refusal)");
        }

        private static void ExpectArgument(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Require(rejected, message);
        }
    }
}
