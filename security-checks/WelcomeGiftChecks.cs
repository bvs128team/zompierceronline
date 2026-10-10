using System;
using System.IO;
using System.Linq;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static object[] WelcomeItem() { return new object[] { 4095, 1, new object[4] }; }
        private static long GiftCount(byte[] blob)
        {
            return LedgerMath.Summarize((object[])((object[])LanGuestSchema.Decode(blob)[12])[0], null, new CountingRules()).Count(4095);
        }
        private static void WelcomeGiftChecks()
        {
            var store = new LanGuestStore(); var link = new GuestLink(); double now = 0;
            var token = new byte[32]; token[0] = 23; string id = LanGuestStore.ProfileIdFor(token);
            var host = new LanGuestHost(store, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, WelcomeItem);
            var guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { });
            var forged = SampleProfile(); var inventories = (object[])((object[])forged[12])[0];
            inventories[1] = new object[] { new object[] { 4095, 99, new object[4] } };
            ((object[])((object[])((object[])inventories[2])[0])[2])[3] = new object[] { new object[] { WelcomeItem() } };
            byte[] initial = LanValueCodec.Encode(forged);
            host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => initial);
            host.AcceptKit(true);
            Require(store.WasWelcomed(id) && GiftCount(store.Get(id)) == 1 && host.Ledger.Count(4095) == 1, "Gift missing or forged initial gift survived");
            Require(!host.Confirmed, "Interactions enabled before welcome correction applied");
            host.AcceptKit(true);
            Require(GiftCount(store.Get(id)) == 1, "Repeated acceptance doubled welcome gift");
            // Delayed restore/ack plus a fresh stale revision must not erase the new gift.
            host.Receive(new Packet { Kind = PacketKind.GuestStateChunk, Revision = 777, ChunkCount = 1, ChunkIndex = 0, Chunk = SampleBlob() }, now);
            Pump(link, host, guest, ref now, 4, true, () => SampleBlob());
            Require(GiftCount(store.Get(id)) == 1 && guest.CorrectionPending && !host.Confirmed && !guest.Saving, "Stale upload erased gift or client operations enabled before application");
            byte[] restored = LanValueCodec.Encode(guest.TakePending()); guest.Applied(true, "gift applied");
            Pump(link, host, guest, ref now, 4, true, () => restored);
            Require(host.Confirmed && guest.Saving && GiftCount(store.Get(id)) == 1, "Welcome correction did not finish");
            // Reconnect restores the same copy.
            link = new GuestLink();
            host = new LanGuestHost(store, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, WelcomeItem);
            guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 4, false, () => null);
            Require(GiftCount(LanValueCodec.Encode(guest.Pending)) == 1 && GiftCount(store.Get(id)) == 1, "Reconnect regranted gift");
            guest.TakePending(); guest.Applied(true, "restored");
            Pump(link, host, guest, ref now, 2, true, () => restored);
            Require(host.Ledger.Take(WelcomeItem()), "Cannot move gift from ledger");
            store.Put(id, LanValueCodec.Encode(host.Ledger.Profile), DateTime.UtcNow);
            Require(GiftCount(store.Get(id)) == 0 && store.WasWelcomed(id), "Moving gift lost receipt");
            link = new GuestLink();
            host = new LanGuestHost(store, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, WelcomeItem);
            guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 4, false, () => null);
            Require(GiftCount(LanValueCodec.Encode(guest.Pending)) == 0 && store.WasWelcomed(id), "Moved/lost gift reappeared on reconnect");
            // New guests get the gift even when their imported items are refused.
            var refusedToken = new byte[32]; refusedToken[0] = 24;
            link = new GuestLink();
            host = new LanGuestHost(store, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, WelcomeItem);
            guest = new LanGuestClient(_ => refusedToken, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => initial); host.AcceptKit(false);
            Require(ItemTotal(store.Get(host.ProfileId)) == 1 && GiftCount(store.Get(host.ProfileId)) == 1, "Refused kit did not receive exactly the gift");
            // Eviction of an ownership profile retains its receipt, including on disk.
            for (int i = 100; i < 120; i++) store.Put(i.ToString("X64"), SampleBlob(), DateTime.UtcNow);
            Require(store.Get(id) == null && store.WasWelcomed(id), "Profile eviction removed gift receipt");
            string root = Path.Combine(Path.GetTempPath(), "zlan-welcome-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root); string slot = NewSlot(root, "slot"); string result;
                Require(store.Write(slot, out result), "Welcome sidecar write failed: " + result);
                var loaded = new LanGuestStore(); Require(loaded.Load(slot, out result) && loaded.WasWelcomed(id), "Receipt not persisted");
                link = new GuestLink();
                host = new LanGuestHost(loaded, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, WelcomeItem);
                guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
                Pump(link, host, guest, ref now, 14, true, () => initial); host.AcceptKit(true);
                Require(GiftCount(loaded.Get(id)) == 0, "Evicted guest got a second gift (or forged one)");
                // Old files load without giving existing guests an upgrade gift.
                var path = Path.Combine(slot, LanGuestStore.FileName);
                var current = LanValueCodec.Decode(File.ReadAllBytes(path)); var old = current.Take(7).ToArray(); old[1] = 1;
                File.WriteAllBytes(path, LanValueCodec.Encode(old));
                var migrated = new LanGuestStore(); Require(migrated.Load(slot, out result), "Version 1 migration failed");
                foreach (object[] entry in (object[])old[6]) Require(migrated.WasWelcomed((string)entry[0]), "Legacy guest eligible for new gift");
                // The slot's own receipt set, not a global file; 1.4.14: kept when the slot was saved again without
                // its sidecar (no second gift for a guest whose profile the slot keeps).
                File.WriteAllBytes(path, LanValueCodec.Encode(current));
                File.AppendAllText(Path.Combine(slot, LanGuestStore.NativeFiles[0]), "changed");
                Require(loaded.Load(slot, out result) && loaded.WasWelcomed(id), "Re-saved checkpoint lost welcome receipts");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
            var full = new LanGuestStore();
            for (int i = 0; i < LanGuestStore.MaxWelcomeReceipts; i++) Require(full.RecordWelcome(i.ToString("X64")), "Receipt cap reached early");
            Require(!full.RecordWelcome(9999.ToString("X64")) && full.WasWelcomed(0.ToString("X64")), "Full receipt store evicts/regrants");
            full.NewWorld(); Require(full.CanWelcome(0.ToString("X64")), "New world retained receipt");
            // Registration can become available between candidate upload and acceptance.
            bool available = false; var late = new LanGuestStore(); link = new GuestLink();
            host = new LanGuestHost(late, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, () => available ? WelcomeItem() : null);
            guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => SampleBlob());
            Require(!late.WasWelcomed(id), "Receipt recorded before grant"); available = true; host.AcceptKit(true);
            Require(late.WasWelcomed(id) && GiftCount(late.Get(id)) == 1, "Late registration gift missing");
            var unavailable = new LanGuestStore(); link = new GuestLink();
            host = new LanGuestHost(unavailable, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 0, () => null);
            guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => initial); host.AcceptKit(true);
            Require(!unavailable.WasWelcomed(id) && GiftCount(unavailable.Get(id)) == 0, "Unavailable gift created receipt or accepted forgery");
            // The injection point supports a different ordinary item in pure tests.
            var alternate = new LanGuestStore(); link = new GuestLink();
            host = new LanGuestHost(alternate, link.Sender(link.ToGuest), _ => { }, new CountingRules(), () => 1,
                () => new object[] { 42, 1, new object[4] });
            guest = new LanGuestClient(_ => token, link.Sender(link.ToHost), _ => { }); host.Begin(); guest.Begin(now);
            Pump(link, host, guest, ref now, 14, true, () => SampleBlob()); host.AcceptKit(false);
            Require(alternate.WasWelcomed(id) && host.Ledger.Count(42) == 1 &&
                ((object[])((object[])((object[])host.Ledger.Profile[12])[0])[1]).Length == 1, "Gift callback/backpack ignored");
            Console.WriteLine("PASS: welcome gift (authoritative first kit, recursive forgery removal, correction application barrier, reconnect, moved gift, refusal, receipts, v1 migration, eviction, cap, checkpoint, late registration)");
        }
    }
}
