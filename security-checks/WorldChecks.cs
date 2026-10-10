using System;
using System.Linq;

namespace ZompiercerLAN
{
    // 0.6.1: zombie corpses and world doors (packet schema 12).
    internal static partial class Program
    {
        private static NetPose Bone(float x) { return new NetPose { X = x, Y = 1f, Z = 2f, QX = 0f, QY = 0.7071068f, QZ = 0f, QW = 0.7071068f }; }

        private static void WorldChecks()
        {
            Packet decoded;
            Func<Packet, Packet> world = p => { p.GameVersion = "checks"; p.Session = 42; p.WorldEpoch = 3; p.Sequence = 9; p.Scene = new string('S', 96); return p; };
            var corpse = world(new Packet { Kind = PacketKind.ZombieCorpse, Corpse = new CorpseFrame { Id = LanProtocol.MaxZombieIds, Prefab = new string('Z', 64), X = 1, Y = 2, Z = 3, Yaw = 359.9f,
                Bones = Enumerable.Range(0, LanProtocol.MaxCorpseBones).Select(i => Bone(i)).ToArray() } });
            var doors = world(new Packet { Kind = PacketKind.DoorStates, Doors = Enumerable.Range(0, LanProtocol.MaxDoorBatch).Select(i => new DoorFrame { X = i, Y = -i, Z = 5, Flags = (byte)(i % 4) }).ToArray() });
            var toggle = world(new Packet { Kind = PacketKind.DoorToggle, X = 10, Y = 0.5f, Z = -3, Action = 1 });
            // 1.1.6: a host zombie's spit or burst stomach.
            var effect = world(new Packet { Kind = PacketKind.ZombieEffect, Revision = LanProtocol.MaxZombieIds, Action = LanProtocol.EffectShot, X = 1, Y = 2, Z = 3, AimZ = 1 });
            // 1.1.9: the partner revives a downed player.
            var revive = world(new Packet { Kind = PacketKind.Revive, Action = LanProtocol.ReviveDone });
            foreach (var p in new[] { corpse, doors, toggle, effect, revive })
            {
                var bytes = LanProtocol.Encode(p);
                Require(bytes.Length <= 1200 && LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == p.Kind && decoded.Sequence == 9, "Valid world packet rejected: " + p.Kind);
                for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated world packet accepted"); }
                var tail = new byte[bytes.Length + 1]; Array.Copy(bytes, tail, bytes.Length); Require(!LanProtocol.TryDecode(tail, out decoded), "World packet trailing bytes accepted");
            }
            LanProtocol.TryDecode(LanProtocol.Encode(corpse), out decoded);
            Require(decoded.Corpse.Bones.Length == LanProtocol.MaxCorpseBones && decoded.Corpse.Bones[5].X == 5 && decoded.Corpse.Id == LanProtocol.MaxZombieIds, "Corpse fields");
            LanProtocol.TryDecode(LanProtocol.Encode(doors), out decoded);
            Require(decoded.Doors.Length == LanProtocol.MaxDoorBatch && decoded.Doors[7].Flags == 3 && decoded.Doors[7].Y == -7, "Door fields");
            // Malformed or out-of-range fields.
            Func<Action<Packet>, Packet, bool> rejected = (change, source) =>
            {
                Packet copy; LanProtocol.TryDecode(LanProtocol.Encode(source), out copy);
                copy.GameVersion = "checks"; change(copy);
                try { return !LanProtocol.TryDecode(LanProtocol.Encode(copy), out decoded); }
                catch (System.IO.InvalidDataException) { return true; }
            };
            var bad = new[]
            {
                rejected(p => p.Corpse.Id = 0, corpse), rejected(p => p.Corpse.Id = LanProtocol.MaxZombieIds + 1, corpse), rejected(p => p.Corpse.Prefab = "", corpse),
                rejected(p => p.Corpse.Yaw = 361, corpse), rejected(p => p.Corpse.Bones = new NetPose[LanProtocol.MaxCorpseBones + 1], corpse),
                rejected(p => p.Corpse.Bones[0].QW = 3, corpse), rejected(p => p.Corpse.X = float.NaN, corpse),
                rejected(p => p.Doors[0].Flags = LanProtocol.MaxDoorFlags + 1, doors), rejected(p => p.Doors = new DoorFrame[LanProtocol.MaxDoorBatch + 1], doors), rejected(p => p.Doors[1].Z = 200000, doors),
                rejected(p => p.Action = LanProtocol.DoorUnlock + 1, toggle), rejected(p => p.Revision = 1, toggle), rejected(p => p.X = float.PositiveInfinity, toggle),
                rejected(p => p.WorldEpoch = 0, toggle), rejected(p => p.Sequence = 0, doors), rejected(p => p.Scene = "", corpse),
                rejected(p => p.Revision = 0, effect), rejected(p => p.Revision = LanProtocol.MaxZombieIds + 1, effect), rejected(p => p.Action = 0, effect),
                rejected(p => p.Action = LanProtocol.EffectThrow + 1, effect), rejected(p => p.AimZ = 0, effect), rejected(p => p.AimZ = float.NaN, effect), rejected(p => p.WorldEpoch = 0, effect),
                rejected(p => p.Action = 0, revive), rejected(p => p.Action = LanProtocol.ReviveDone + 1, revive), rejected(p => p.WorldEpoch = 0, revive), rejected(p => p.Scene = "", revive), rejected(p => p.Sequence = 0, revive),
            };
            Require(bad.All(x => x), "Malformed world packet accepted: #" + Array.IndexOf(bad, false));
            Require(LanNetworkPolicy.Allowed(true, PacketKind.DoorToggle, true) && !LanNetworkPolicy.Allowed(false, PacketKind.DoorToggle, true), "Door request direction");
            Require(LanNetworkPolicy.Allowed(true, PacketKind.Revive, true) && LanNetworkPolicy.Allowed(false, PacketKind.Revive, true) && !LanNetworkPolicy.Allowed(true, PacketKind.Revive, false) &&
                LanProtocol.WorldBound(PacketKind.Revive), "Revive directions");
            LanProtocol.TryDecode(LanProtocol.Encode(effect), out decoded);
            Require(decoded.Revision == LanProtocol.MaxZombieIds && decoded.Action == LanProtocol.EffectShot && decoded.Z == 3 && decoded.AimZ == 1 && LanProtocol.WorldBound(PacketKind.ZombieEffect), "Zombie effect fields");
            // 1.1.8: the nurse's thrown healing smoke is effect 3; nothing beyond it.
            effect.Action = LanProtocol.EffectThrow;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(effect), out decoded) && decoded.Action == LanProtocol.EffectThrow, "Nurse throw effect rejected");
            effect.Action = LanProtocol.EffectThrow + 1;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(effect), out decoded), "Unknown zombie effect accepted");
            effect.Action = 0;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(effect), out decoded), "Zombie effect 0 accepted");
            effect.Action = LanProtocol.EffectShot;
            foreach (var kind in new[] { PacketKind.ZombieCorpse, PacketKind.DoorStates, PacketKind.ZombieEffect })
                Require(!LanNetworkPolicy.Allowed(true, kind, true) && LanNetworkPolicy.Allowed(false, kind, true) && !LanNetworkPolicy.Allowed(false, kind, false), "Host-to-guest direction: " + kind);
            var limits = new LanMessageLimits(); int toggles = 0, corpses = 0, effects = 0;
            for (int i = 0; i < 1000; i++) { if (limits.Take(PacketKind.DoorToggle, 0)) toggles++; if (limits.Take(PacketKind.ZombieCorpse, 0)) corpses++; if (limits.Take(PacketKind.ZombieEffect, 0)) effects++; }
            Require(toggles == 4 && corpses == 24 && effects == 20, "World packet bursts not bounded");
            // Loose items and bags (schema 13).
            var items = world(new Packet { Kind = PacketKind.WorldItems, Items = Enumerable.Range(0, LanProtocol.MaxItemBatch).Select(i =>
                new WorldItemFrame { HostId = i + 1, ItemId = i % 3 == 0 ? -1 : 5, Amount = i % 3 == 0 ? 0 : 3, Kind = (byte)(i % 3 == 0 ? 1 : 0), Pose = Bone(i) }).ToArray() });
            var itemBytes = LanProtocol.Encode(items);
            Require(itemBytes.Length <= 1200 && LanProtocol.TryDecode(itemBytes, out decoded) && decoded.Items.Length == LanProtocol.MaxItemBatch && decoded.Items[1].Amount == 3, "Item batch rejected");
            var badItems = new[]
            {
                rejected(p => p.Items[0].HostId = 0, items), rejected(p => p.Items[1].Kind = 4, items), rejected(p => p.Items[1].ItemId = 70000, items),
                rejected(p => p.Items[1].Amount = 0, items), rejected(p => p.Items[0].Amount = 5, items), rejected(p => p.Items[0].ItemId = 4, items),
                rejected(p => p.Items = new WorldItemFrame[LanProtocol.MaxItemBatch + 1], items), rejected(p => p.Items[2].Pose.QW = 5, items),
            };
            Require(badItems.All(x => x), "Malformed item batch accepted: #" + Array.IndexOf(badItems, false));
            Require(!LanNetworkPolicy.Allowed(true, PacketKind.WorldItems, true) && LanNetworkPolicy.Allowed(false, PacketKind.WorldItems, true), "Item batch direction");
            // 1.1.6: carryable objects (kind 3): a prefab (or the robot dog), their car + 1 and a dog's state.
            var takables = world(new Packet { Kind = PacketKind.WorldItems, Items = new[] {
                new WorldItemFrame { HostId = 7, ItemId = LanProtocol.MaxTakablePrefab, Amount = LanProtocol.MaxCars | (LanProtocol.TakableActive | LanProtocol.TakableRun) << 8, Kind = 3, Pose = new NetPose { X = 255, Y = 1, Z = -255, QW = 1 } },
                new WorldItemFrame { HostId = 8, ItemId = 0, Amount = 0, Kind = 3, Pose = new NetPose { X = 5000, Y = 60, Z = -3000, QW = 1 } } } });
            Require(LanProtocol.TryDecode(LanProtocol.Encode(takables), out decoded) && decoded.Items[0].Amount == takables.Items[0].Amount && decoded.Items[1].Pose.X == 5000, "Carryable objects rejected");
            var badTakables = new[]
            {
                rejected(p => p.Items[0].ItemId = LanProtocol.MaxTakablePrefab + 1, takables), rejected(p => p.Items[0].ItemId = -1, takables),
                rejected(p => p.Items[0].Amount = LanProtocol.MaxCars + 1, takables), rejected(p => p.Items[0].Amount = (LanProtocol.MaxTakableState + 1) << 8, takables),
                rejected(p => p.Items[0].Amount = -1, takables), rejected(p => p.Items[0].Pose.X = 257, takables),
            };
            Require(badTakables.All(x => x), "Malformed carryable object accepted: #" + Array.IndexOf(badTakables, false));
            // 1.4.13: a robot dog's frame carries what its device slots hold; another carryable object's carries nothing.
            takables.Items[0].Devices = new byte[] { 3, LanProtocol.NoDevice, 0 }; takables.Items[1].Devices = new byte[] { 9 };
            Require(LanProtocol.TryDecode(LanProtocol.Encode(takables), out decoded) && decoded.Items[0].Devices.Length == 3 &&
                decoded.Items[0].Devices[1] == LanProtocol.NoDevice && decoded.Items[0].Devices[2] == 0 && decoded.Items[1].Devices == null, "Robot dog devices");
            Require(rejected(p => p.Items[0].Devices = new byte[LanProtocol.MaxDogDevices + 1], takables), "Too many robot dog devices accepted");
            var truncated = LanProtocol.Encode(world(new Packet { Kind = PacketKind.WorldItems, Items = new[] {
                new WorldItemFrame { HostId = 7, ItemId = LanProtocol.MaxTakablePrefab, Kind = 3, Pose = new NetPose { QW = 1 }, Devices = new byte[] { 1, 2, 3 } } } }));
            Require(LanProtocol.TryDecode(truncated, out decoded) && !LanProtocol.TryDecode(truncated.Take(truncated.Length - 2).ToArray(), out decoded), "Truncated robot dog devices accepted");
            // The batch's room: frames up to MaxItemBytes fit a packet with the longest version and scene names,
            // and a full batch of plain items fits that room.
            var dogs = new System.Collections.Generic.List<WorldItemFrame>(); int room = 0;
            while (dogs.Count < LanProtocol.MaxItemBatch)
            {
                var dog = new WorldItemFrame { HostId = dogs.Count + 1, ItemId = LanProtocol.MaxTakablePrefab, Kind = 3, Pose = new NetPose { QW = 1 }, Devices = new byte[LanProtocol.MaxDogDevices] };
                if (room + LanProtocol.ItemBytes(dog) > LanProtocol.MaxItemBytes) break;
                room += LanProtocol.ItemBytes(dog); dogs.Add(dog);
            }
            var fullBatch = LanProtocol.Encode(new Packet { Kind = PacketKind.WorldItems, GameVersion = new string('v', 40), Session = 1, WorldEpoch = 1, Sequence = 1, Scene = new string('s', 96), Items = dogs.ToArray() });
            Require(fullBatch.Length <= 1200 && LanProtocol.TryDecode(fullBatch, out decoded) && decoded.Items.Length == dogs.Count && decoded.Items[0].Devices.Length == LanProtocol.MaxDogDevices &&
                LanProtocol.ItemBytes(new WorldItemFrame { Kind = 0 }) * LanProtocol.MaxItemBatch <= LanProtocol.MaxItemBytes, "Item batch byte room");
            // Pick-up and drop requests carry no handle; bag targets name a host id.
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StoragePickup, 0, 5) && !LanProtocol.ValidStorageRequest(LanProtocol.StoragePickup, 1, 5) &&
                LanProtocol.ValidStorageRequest(LanProtocol.StorageDrop, 0, 5) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageDrop, 0, 0), "Pick-up/drop request shapes");
            Require((int)LanStorage.DecodeTarget(LanStorage.BagPayload(42))[1] == 42, "Bag target roundtrip");
            ExpectRejected(() => LanStorage.DecodeTarget(LanValueCodec.Encode(new object[] { 3, 0 })), "Bag target without id accepted");
            int hostId, itemId, amount; float dx, dy, dz;
            LanStorage.DecodePickup(LanStorage.PickupPayload(9, 8, 7), out hostId, out itemId, out amount);
            Require(hostId == 9 && itemId == 8 && amount == 7, "Pick-up payload roundtrip");
            ExpectRejected(() => LanStorage.DecodePickup(LanValueCodec.Encode(new object[] { 0, 8, 7 }), out hostId, out itemId, out amount), "Pick-up without host id accepted");
            ExpectRejected(() => LanStorage.DecodePickup(LanValueCodec.Encode(new object[] { 9, 8, 0 }), out hostId, out itemId, out amount), "Empty pick-up accepted");
            var dropped = LanStorage.DecodeDrop(LanStorage.DropPayload(Item(5, 2), 1f, 2f, 3f), out dx, out dy, out dz);
            Require((int)dropped[0] == 5 && dz == 3f, "Drop payload roundtrip");
            ExpectRejected(() => LanStorage.DecodeDrop(LanValueCodec.Encode(new object[] { Item(5, 2), 1f, 2f, 300000f }), out dx, out dy, out dz), "Far drop position accepted");
            ExpectRejected(() => LanStorage.DecodeDrop(LanValueCodec.Encode(new object[] { Item(70000, 2), 1f, 2f, 3f }), out dx, out dy, out dz), "Unknown dropped item accepted");
            // Work requests.
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageWork, 0, 9) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageWork, 2, 9), "Work request shape");
            int wtool, wextra; object[] wescrow;
            Require(LanStorage.DecodeWork(LanStorage.WorkPayload(LanStorage.WorkDig, 1f, 2f, 3f, 7, 2, null), out dx, out dy, out dz, out wtool, out wextra, out wescrow) == LanStorage.WorkDig && wextra == 2 && wescrow == null, "Work payload roundtrip");
            foreach (var badWork in new[] { new object[] { 9, 1f, 2f, 3f, 7, 0, null }, new object[] { LanStorage.WorkDig, 1f, 2f, 3f, 7, 3, null }, new object[] { LanStorage.WorkHit, 1f, 2f, 3f, -1, 0, null },
                new object[] { LanStorage.WorkFuel, 1f, 2f, 3f, -1, 0, null }, new object[] { LanStorage.WorkHit, 1f, 2f, 3f, 7, 0, Item(92, 1) }, new object[] { LanStorage.WorkFuel, 1f, 2f, 300000f, -1, 0, Item(92, 1) } })
                ExpectRejected(() => LanStorage.DecodeWork(LanValueCodec.Encode(badWork), out dx, out dy, out dz, out wtool, out wextra, out wescrow), "Invalid work request accepted");
            // Craft requests.
            int crecipe, ccount; object[] cingredients;
            var ctarget = LanStorage.DecodeCraft(LanStorage.CraftPayload(LanStorage.TrainPayload(1, 1, 9), 40, 2, new object[] { Item(1, 4) }), out crecipe, out ccount, out cingredients);
            Require((int)ctarget[0] == LanStorage.TrainTarget && crecipe == 40 && ccount == 2 && cingredients.Length == 1, "Craft payload roundtrip");
            foreach (var badCraft in new[] {
                new object[] { new object[] { 2, 9, 1, 5 }, 40, 1, new object[] { Item(1, 1) } }, new object[] { new object[] { 2, 0, 1, 5 }, 40, 0, new object[] { Item(1, 1) } },
                new object[] { new object[] { 2, 0, 1, 5 }, 40, 21, new object[] { Item(1, 1) } }, new object[] { new object[] { 2, 0, 1, 5 }, 40, 1, new object[0] },
                new object[] { new object[] { 2, 0, 1, 5 }, 70000, 1, new object[] { Item(1, 1) } }, new object[] { new object[] { 2, 0, 1, 5 }, 40, 1, new object[] { Item(70000, 1) } } })
                ExpectRejected(() => LanStorage.DecodeCraft(LanValueCodec.Encode(badCraft), out crecipe, out ccount, out cingredients), "Invalid craft request accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageCraft, 0, 9) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageCraft, 3, 9), "Craft request shape");
            Console.WriteLine("PASS: world packets schema 12-13, 25-27 (corpse poses, door batches, door requests, item batches, carryable objects, robot dog devices and the batch's byte room, zombie effects incl. the nurse throw, revives, pick-up/drop shapes) / bounds / truncation / directions / rate limits");
        }
    }
}
