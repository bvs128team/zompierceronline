using System;
using System.Collections.Generic;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.4.2 (schema 32): storage request 18 places the guest's furniture kit on the host's train (escrow,
    // ledger), storage request 19 takes a host part or furniture apart (tool from the ledger and worn,
    // what it gives onto the ledger and into the backpack once) or takes a blueprint away.
    internal static partial class Program
    {
        private static readonly float[] CarPose = { 1f, .5f, -2f, 0f, 0f, 0f, 1f };

        private static void BuildChecks()
        {
            BuildProtocolChecks();
            FurnitureRequestChecks();
            DismantleRequestChecks();
            Console.WriteLine("PASS: guest furniture kits and taking the host's train apart (request shapes and bounds, kit escrow and refusal, host pace, tool on the ledger and worn, recipe or kit onto the ledger and into the backpack once over a lossy link, blueprints, malformed requests)");
        }

        private static void BuildProtocolChecks()
        {
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageFurniture, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageFurniture, 1, 10) &&
                LanProtocol.ValidStorageRequest(LanProtocol.StorageDismantle, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageDismantle, 0, 0), "Furniture and dismantle request shapes");
            Require(LanStorage.NotEmpty <= LanProtocol.MaxStorageStatus && LanStorage.Describe(LanStorage.NotEmpty) != LanStorage.Describe(LanStorage.DeviceInUse), "Not-empty status");
            int car; float[] pose;
            var kit = LanStorage.DecodeFurniture(LanStorage.FurniturePayload(Item(80, 1), 2, CarPose), out car, out pose);
            Require((int)kit[0] == 80 && car == 2 && pose[2] == -2f && pose[6] == 1f, "Furniture roundtrip");
            Func<int, object, object[]> furniture = (index, value) =>
            {
                var r = new object[] { Item(80, 1), 1, 1f, 2f, 3f, 0f, 0f, 0f, 1f };
                if (index >= 0) r[index] = value;
                return r;
            };
            foreach (var bad in new[] { furniture(0, Item(80, 2)), furniture(0, 4), furniture(1, -1), furniture(1, LanProtocol.MaxCars), furniture(2, 300f), furniture(4, 1),
                furniture(5, 1f), furniture(8, 0f), furniture(-1, null).Take(8).ToArray(), furniture(-1, null).Concat(new object[] { 0f }).ToArray() })
                ExpectRejected(() => LanStorage.DecodeFurniture(LanValueCodec.Encode(bad), out car, out pose), "Invalid furniture request accepted");

            int owner, identity, tool;
            Require(LanStorage.DecodeDismantle(LanStorage.DismantlePayload(LanStorage.DismantleHit, 3, LanStorage.TrainFurniture, -77, 40), out car, out owner, out identity, out tool) == LanStorage.DismantleHit &&
                car == 3 && owner == LanStorage.TrainFurniture && identity == -77 && tool == 40, "Dismantle roundtrip");
            Require(LanStorage.DecodeDismantle(LanStorage.DismantlePayload(LanStorage.DismantleBlueprint, 0, LanStorage.TrainPart, 5, -1), out car, out owner, out identity, out tool) == LanStorage.DismantleBlueprint, "Blueprint removal roundtrip");
            foreach (var bad in new[] { new object[] { 0, 1, 2, 5, 40 }, new object[] { 3, 1, 2, 5, 40 }, new object[] { 1, -1, 2, 5, 40 }, new object[] { 1, LanProtocol.MaxCars, 2, 5, 40 },
                new object[] { 1, 1, 3, 5, 40 }, new object[] { 1, 1, 2, 0, 40 }, new object[] { 1, 1, 2, 5, -1 }, new object[] { 1, 1, 2, 5, 70000 }, new object[] { 2, 1, 2, 5, 40 },
                new object[] { 2, 1, 1, 5, -1 }, new object[] { 1, 1, 2, 5 }, new object[] { 1, 1, 2, 5, 40, 0 }, new object[] { 1, 1, 2, 5, 40f } })
                ExpectRejected(() => LanStorage.DecodeDismantle(LanValueCodec.Encode(bad), out car, out owner, out identity, out tool), "Invalid dismantle request accepted: " + string.Join(",", bad));

            var refund = LanStorage.DecodeRefund(LanStorage.RefundPayload(new object[] { Item(10, 4), Item(11, 2) }));
            Require(refund.Length == 2 && (int)((object[])refund[1])[1] == 2, "Refund roundtrip");
            ExpectRejected(() => LanStorage.RefundPayload(new object[0]), "Empty refund encoded");
            ExpectRejected(() => LanStorage.DecodeRefund(LanValueCodec.Encode(Enumerable.Range(0, LanStorage.MaxRefundItems + 1).Select(i => (object)Item(10, 1)).ToArray())), "Oversized refund accepted");
            ExpectRejected(() => LanStorage.DecodeRefund(LanValueCodec.Encode(new object[] { 5 })), "Malformed refund accepted");
        }

        private static LanGuestLedger BuildLedger()
        {
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(80, 2), Item(40, 1, 50f) }, new object[0], new int[0]), 0);
            return book;
        }

        private static void FurnitureRequestChecks()
        {
            var rig = new StorageRig();
            var book = BuildLedger();
            rig.Host.Ledger = () => book;
            var answers = new List<byte>();
            var returned = new List<object[]>();
            Action<byte> keep = status => answers.Add(status);
            Action<object[]> back = item => returned.Add(item);
            Require(rig.Client.Furniture(Item(80, 1), 1, CarPose, keep, back) && rig.Client.Busy, "Furniture not queued or uploads not held");
            rig.Pump(1);
            Require(answers.SequenceEqual(new[] { LanStorage.Ok }) && rig.HostWorld.Furnitures.Count == 1 && (int)rig.HostWorld.Furnitures[0][0] == 80 && (int)rig.HostWorld.Furnitures[0][1] == 1 &&
                book.Count(80) == 1 && returned.Count == 0, "Furniture kit not placed once");
            // The host refuses: the ledger keeps the kit and it goes back where it was.
            rig.HostWorld.FurnitureStatus = LanStorage.TooFar;
            rig.Client.Furniture(Item(80, 1), 1, CarPose, keep, back); rig.Pump(1);
            Require(answers.Last() == LanStorage.TooFar && book.Count(80) == 1 && returned.Count == 1 && rig.HostWorld.Furnitures.Count == 1, "Refused kit not given back");
            rig.HostWorld.FurnitureStatus = LanStorage.Ok;
            // Two at once: the host's pace.
            book = BuildLedger(); rig.Host.Ledger = () => book; answers.Clear();
            rig.Client.Furniture(Item(80, 1), 1, CarPose, keep, back); rig.Client.Furniture(Item(80, 1), 1, CarPose, keep, back); rig.Pump(1);
            Require(answers.SequenceEqual(new[] { LanStorage.Ok, LanStorage.Busy }) && book.Count(80) == 1 && returned.Count == 2, "Furniture pace");
            // A kit the guest does not own never reaches the world.
            int placed = rig.HostWorld.Furnitures.Count;
            rig.Client.Furniture(Item(81, 1), 1, CarPose, keep, back); rig.Pump(1);
            Require(answers.Last() == LanStorage.NotOwned && rig.HostWorld.Furnitures.Count == placed, "Unowned kit placed");
            Require(!rig.Client.Furniture(Item(80, 2), 1, CarPose, keep, back) && !rig.Client.Furniture(null, 1, CarPose, keep, back) && !rig.Client.Furniture(Item(80, 1), 1, new[] { 0f }, keep, back), "Malformed furniture queued");
            // Hostile requests reach no world and leave the ledger alone.
            rig = new StorageRig(); book = BuildLedger(); rig.Host.Ledger = () => book;
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { Item(80, 2), 1, 1f, 2f, 3f, 0f, 0f, 0f, 1f }), new byte[] { 3 },
                LanValueCodec.Encode(new object[] { Item(80, 1), -1, 1f, 2f, 3f, 0f, 0f, 0f, 1f }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageFurniture, Chunk = payload }, rig.Now += 1);
            Require(rig.HostWorld.Furnitures.Count == 0 && book.Count(80) == 2, "A malformed furniture request reached the world or the ledger");
        }

        private static void DismantleRequestChecks()
        {
            var rig = new StorageRig();
            var book = BuildLedger();
            rig.Host.Ledger = () => book;
            var answers = new List<object[]>();
            Action<byte, bool> keep = (status, done) => answers.Add(new object[] { status, done });
            // A hit that does not take it apart yet: the tool wears, nothing is given.
            Require(rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 99, 40, keep), "Dismantle not queued");
            rig.Pump(1);
            Require(answers.Count == 1 && (byte)answers[0][0] == LanStorage.Ok && !(bool)answers[0][1] && rig.HostWorld.Dismantles.Count == 1 && book.BestWear(40) == 49f &&
                rig.GuestWorld.Backpack.Count == 0, "Dismantle hit");
            // The hit that takes it apart: its recipe onto the ledger and into the backpack.
            rig.HostWorld.DismantleGiven = new object[] { Item(10, 4), Item(11, 2) }; rig.HostWorld.DismantleDone = true;
            rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 99, 40, keep); rig.Pump(1);
            Require((bool)answers.Last()[1] && book.Count(10) == 4 && book.Count(11) == 2 && rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 10 ? (int)i[1] : 0) == 4 &&
                book.BestWear(40) == 48f, "Taken apart: recipe not given once");
            // A blueprint taken away needs no tool and gives nothing.
            rig.HostWorld.DismantleGiven = null; rig.HostWorld.DismantleDone = true;
            rig.Client.Dismantle(LanStorage.DismantleBlueprint, 1, LanStorage.TrainPart, 98, -1, keep); rig.Pump(1);
            Require((byte)answers.Last()[0] == LanStorage.Ok && (int)rig.HostWorld.Dismantles.Last()[0] == LanStorage.DismantleBlueprint && book.BestWear(40) == 48f, "Blueprint removal");
            // The host refuses (still holds something): nothing changes.
            rig.HostWorld.DismantleStatus = LanStorage.NotEmpty;
            rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainFurniture, 97, 40, keep); rig.Pump(1);
            Require((byte)answers.Last()[0] == LanStorage.NotEmpty && book.BestWear(40) == 48f, "Refused dismantling changed the ledger");
            rig.HostWorld.DismantleStatus = LanStorage.Ok;
            // A tool the guest does not own never reaches the world.
            int calls = rig.HostWorld.Dismantles.Count;
            rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 99, 41, keep); rig.Pump(1);
            Require((byte)answers.Last()[0] == LanStorage.NotOwned && rig.HostWorld.Dismantles.Count == calls, "Dismantling with an unowned tool");
            Require(!rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 0, 40, keep) && !rig.Client.Dismantle(LanStorage.DismantleBlueprint, 1, LanStorage.TrainFurniture, 5, -1, keep), "Malformed dismantle queued");
            // Lossy, duplicating, reordering link: taken apart once, its recipe given once.
            for (int round = 0; round < 20; round++)
            {
                rig = new StorageRig();
                var lossy = new Random(round); rig.Link.Drop = pk => lossy.Next(10) < 3; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
                book = BuildLedger(); rig.Host.Ledger = () => book; answers.Clear();
                rig.HostWorld.DismantleGiven = new object[] { Item(10, 4) }; rig.HostWorld.DismantleDone = true;
                rig.Client.Dismantle(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 99, 40, keep);
                rig.Pump(30);
                Require(rig.HostWorld.Dismantles.Count == 1 && answers.Count == 1 && book.Count(10) == 4 && rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 10 ? (int)i[1] : 0) == 4,
                    "Lossy link: dismantles " + rig.HostWorld.Dismantles.Count + ", answers " + answers.Count + ", ledger " + book.Count(10));
            }
            // Hostile requests reach no world.
            rig = new StorageRig(); book = BuildLedger(); rig.Host.Ledger = () => book;
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { 1, 1, 2, 0, 40 }), new byte[] { 7, 7 }, LanValueCodec.Encode(new object[] { 2, 1, 2, 5, 40 }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageDismantle, Chunk = payload }, rig.Now += 1);
            Require(rig.HostWorld.Dismantles.Count == 0 && book.BestWear(40) == 50f, "A malformed dismantle request reached the world or the ledger");
        }
    }
}
