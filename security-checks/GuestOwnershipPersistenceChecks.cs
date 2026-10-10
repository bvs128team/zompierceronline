using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static long StoredCount(LanGuestStore store, string id, int item)
        {
            var root = LanGuestSchema.Decode(store.Get(id));
            return LedgerMath.Summarize((object[])((object[])root[12])[0], null, new CountingRules()).Count(item);
        }
        private static int StoredMagazine(LanGuestStore store, string id)
        {
            return (int)((object[])((object[])((object[])LanGuestSchema.Decode(store.Get(id))[12])[5])[0])[0];
        }
        private static LanGuestHost StoredHost(LanGuestStore store, byte[] token)
        {
            var sent = new List<Packet>();
            var host = new LanGuestHost(store, sent.Add, _ => { }, new CountingRules(), () => 0);
            host.Begin();
            host.Receive(new Packet { Kind = PacketKind.IdentityProof, Chunk = token }, 0);
            host.Receive(new Packet { Kind = PacketKind.GuestRestoreAck, Revision = sent.Last().Revision }, 0);
            Require(host.Confirmed, "Stored guest not confirmed");
            return host;
        }
        private static void GuestOwnershipPersistenceChecks()
        {
            var store = new LanGuestStore(); var token = new byte[32]; token[0] = 91;
            string id = LanGuestStore.ProfileIdFor(token);
            store.Put(id, LanValueCodec.Encode(LedgerProfile(new object[] { Item(7, 3) }, new object[0], new[] { 4 })), DateTime.UtcNow);
            var host = StoredHost(store, token);
            var rig = new StorageRig(); rig.Host.Ledger = () => host.Confirmed ? host.Ledger : null;
            rig.HostWorld.Box.Items.Add(Item(1, 10));
            rig.GuestWorld.Backpack.Add(Item(7, 3));
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.Client.Take(0, 4); rig.Pump(1);
            Require(StoredCount(store, id, 1) == 4, "Take not persisted without client upload");
            rig.GuestWorld.PutFromBackpack(rig.Client, 0, 2); rig.Pump(1);
            Require(StoredCount(store, id, 7) == 1, "Put not persisted without client upload");
            rig.HostWorld.Box.Forbidden = 7;
            var before = store.Get(id);
            rig.GuestWorld.PutFromBackpack(rig.Client, 0, 1); rig.Pump(1);
            Require(before.SequenceEqual(store.Get(id)), "Rejected put changed persisted profile");
            // A save while still connected must already include ownership changes.
            string dir = Path.Combine(Path.GetTempPath(), "zlan-ownership-" + Guid.NewGuid().ToString("N"));
            try
            {
                string slot = NewSlot(dir, "checkpoint"); string result;
                Require(store.Write(slot, out result), "Ownership checkpoint failed");
                var loaded = new LanGuestStore();
                Require(loaded.Load(slot, out result) && StoredCount(loaded, id, 1) == 4 && StoredCount(loaded, id, 7) == 1,
                    "Save before client upload lost ownership");
                host.End(); host = StoredHost(store, token);
                Require(host.Ledger.Count(1) == 4 && host.Ledger.Count(7) == 1, "Reconnect lost take or duplicated put");
                // Per-shot rounds are batched: stored by the next save tick, End() or a sidecar write.
                host.Ledger.SpendRound(0);
                host.Tick(0);
                Require(StoredMagazine(store, id) == 3, "Combat round not saved");
                host.Ledger.SpendRound(0); host.End();
                Require(StoredMagazine(store, id) == 2, "Batched round lost at disconnect");
                host = StoredHost(store, token);
                host.Ledger.SpendRound(0);
                string later = NewSlot(dir, "batched"); string written;
                Require(store.Write(later, out written), "Batched checkpoint failed");
                var reloaded = new LanGuestStore();
                Require(reloaded.Load(later, out written) && StoredMagazine(reloaded, id) == 1, "Batched round missing from game save");
                host.Ledger.Remove(1, 2);
                Require(StoredCount(store, id, 1) == 2, "Recipe payment not saved");
                // Same-lineage load invalidates the live binding before its next tick.
                Require(store.Load(slot, out result), "Same-world reload failed");
                host.Ledger.Remove(1, 1);
                Require(!host.Confirmed && StoredCount(store, id, 1) == 4, "Live ledger overwrote loaded checkpoint");
                host = StoredHost(store, token);
                host.Ledger.TakeAll();
                Require(StoredCount(store, id, 1) == 0 && StoredCount(store, id, 7) == 0, "Death ownership not saved");
                store.NewWorld(); host.Ledger.Refund(Item(1, 1)); host.End();
                Require(store.Count == 0, "Old profile leaked into new world");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            // Capacity denial must precede mutation of the host world.
            var full = Enumerable.Range(0, LanGuestSchema.MaxItems).Select(_ => (object)Item(1, 1)).ToArray();
            store.Put(id, LanValueCodec.Encode(LedgerProfile(full, new object[0], new int[0])), DateTime.UtcNow);
            host = StoredHost(store, token);
            rig = new StorageRig(); rig.Host.Ledger = () => host.Ledger;
            rig.HostWorld.Box.Items.Add(Item(2, 1));
            rig.Client.Open(BoxTarget()); rig.Pump(1); rig.Client.Take(0, 1); rig.Pump(1);
            Require(rig.HostWorld.Takes == 0 && rig.HostWorld.Box.Items.Count == 1 && host.Ledger.Count(2) == 0,
                "Full profile consumed an item from the world");
            // Rollback retains layout and pending acknowledgements, not just totals.
            var book = host.Ledger;
            book.Reset(LedgerProfile(new object[0], new object[] { Item(7, 2) }, new int[0]), 0);
            book.Give(LanGuestLedger.RequestChannel, 88, Item(1, 3));
            before = LanValueCodec.Encode(book.Profile); int pending = book.PendingCount;
            book.BeginChange(); book.TakeAll(); book.EndChange(false);
            Require(before.SequenceEqual(LanValueCodec.Encode(book.Profile)) && book.PendingCount == pending, "Rollback lost layout or pending grants");
            Console.WriteLine("PASS: guest ownership persistence (take/put stored before any upload, rejected put unchanged, reconnect, game save, batched rounds at tick/disconnect/save, stale lineage, death, capacity before world, rollback)");
        }
    }
}
