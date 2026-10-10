using System;
using System.Linq;
using System.IO;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static void RapidItemPlacementChecks()
        {
            var rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[0], new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.HostWorld.Ground.Add(44, Item(149, 1));
            rig.Link.Duplicate = true; rig.Link.Shuffle = true;
            int callbacks = 0;
            rig.Client.PickedUp = (id, amount) => { Require(id == 44 && amount == 1, "pickup feedback identity"); callbacks++; };
            for (int i = 0; i < 50; i++) rig.Client.Pickup(44, 149, 1);
            Require(rig.Client.PendingRequests == 1, "rapid pickup queued duplicate requests");
            rig.Pump(4);
            Require(rig.HostWorld.Pickups == 1 && rig.GuestWorld.Total == 1 && book.Count(149) == 1 && callbacks == 1, "rapid/replayed pickup duplicated figurine");
            for (int i = 0; i < 10; i++) { rig.Client.Pickup(44, 149, 1); rig.Pump(1); }
            Require(rig.GuestWorld.Total == 1 && book.Count(149) == 1 && callbacks == 1, "stale visual minted figurine");

            var pose = new[] { 1f, 2f, 3f, 0f, .70710677f, 0f, .70710677f };
            var item = rig.GuestWorld.Backpack.Single();
            rig.GuestWorld.Backpack.Clear();
            Require(rig.Client.Drop(item, 2, pose), "figurine placement refused by client");
            rig.Pump(4);
            Require(rig.HostWorld.Placements == 1 && rig.HostWorld.LastCar == 2 && rig.HostWorld.LastPose.SequenceEqual(pose), "placement pose or replay failed");
            Require(book.Count(149) == 0 && rig.GuestWorld.Total == 0 && rig.HostWorld.Ground.Count == 1, "placed figurine ownership duplicated");
            var placedId = rig.HostWorld.Ground.Keys.Single();
            rig.Client.PickedUp = null;
            rig.Client.Pickup(placedId, 149, 1); rig.Pump(3);
            rig.HostWorld.GroundNear = false;
            rig.GuestWorld.Backpack.Clear();
            Require(rig.Client.Drop(item, 2, pose), "refused placement not enqueued"); rig.Pump(3);
            Require(book.Count(149) == 1 && rig.GuestWorld.Total == 1 && rig.HostWorld.Ground.Count == 0, "rejected placement did not return escrow exactly once");

            foreach (var bad in new[]
            {
                new object[] { item, 1f, 2f, 3f, 999, 0f, 0f, 0f, 1f },
                new object[] { item, 1f, 2f, 3f, 0, 0f, 0f, 0f, 0f },
                new object[] { item, 300f, 2f, 3f, 0, 0f, 0f, 0f, 1f },
                new object[] { item, 1f, 2f, 3f, 0, "bad", 0f, 0f, 1f }
            })
            {
                bool rejected = false;
                try { float x, y, z; int car; float[] q; LanStorage.DecodeDrop(LanValueCodec.Encode(bad), out x, out y, out z, out car, out q); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "invalid placement accepted");
            }
            Console.WriteLine("PASS: rapid figurine pickup / delayed visual / packet replay / rotated train placement / rejection refund / malformed poses");
        }
    }
}
