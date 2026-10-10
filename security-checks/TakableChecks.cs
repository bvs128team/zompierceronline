using System;
using System.Collections.Generic;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.2.1 (schema 29): who holds a carryable object and whose robot dog it is; storage request 15
    // (carry a host object, drive the guest's own dog); storage target 4 (a slot of that dog).
    internal static partial class Program
    {
        // The host's world as LanTakables answers for it.
        private sealed class FakeTakableWorld : ILanTakableWorld
        {
            internal readonly List<object[]> Requests = new List<object[]>();
            internal byte Status = LanStorage.Ok;
            internal int Carries;
            public byte Takable(object[] request, out byte[] answer, out int amount)
            {
                Requests.Add(request); answer = null; amount = 0;
                if (Status != LanStorage.Ok) return Status;
                int op = (int)request[0];
                if (op == LanStorage.TakableTake) Carries++;
                if (op == LanStorage.RobotInfo || op == LanStorage.RobotSet)
                    answer = LanStorage.EncodeRobot(new LanStorage.RobotState { Active = true, Behavior = 1, Attack = 2, Doors = 1, Battery = 80, Durability = 55,
                        Devices = new[] { 300, -1 }, Storage = new[] { true, false }, Medkit = new[] { -1, 1 }, Upgrades = new[] { 310 } });
                if (op == LanStorage.RobotMedkit) amount = 2500;
                return LanStorage.Ok;
            }
        }

        private static void TakableChecks()
        {
            TakableProtocolChecks();
            TakableRequestChecks();
            Console.WriteLine("PASS: carryable objects and robot dogs (holder and owner bits, robot slot targets, carry/release/robot request shapes and bounds, robot state, host dispatch once over a lossy link, malformed requests)");
        }

        private static void TakableProtocolChecks()
        {
            Packet decoded;
            var items = new Packet { Kind = PacketKind.WorldItems, GameVersion = "checks", Session = 42, WorldEpoch = 3, Sequence = 9, Scene = "Location8",
                Items = new[] { new WorldItemFrame { HostId = 5, ItemId = LanProtocol.MaxTakablePrefab, Kind = 3, Amount = LanProtocol.MaxTakableState << 8, Pose = new NetPose { QW = 1 } } } };
            Require(LanProtocol.TryDecode(LanProtocol.Encode(items), out decoded) && decoded.Items[0].Amount >> 8 == LanProtocol.MaxTakableState, "Takable holder and owner bits rejected");
            items.Items[0].Amount = (LanProtocol.MaxTakableState + 1) << 8;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(items), out decoded), "Unknown takable state accepted");

            // Robot slot targets.
            var target = LanStorage.DecodeTarget(LanStorage.RobotPayload(77, LanStorage.RobotSlotStorage, LanStorage.MaxRobotSlots - 1));
            Require((int)target[0] == LanStorage.RobotTarget && (int)target[1] == 77 && (int)target[2] == LanStorage.RobotSlotStorage, "Robot target roundtrip");
            foreach (var bad in new[] { new object[] { 4, 0, 1, 0 }, new object[] { 4, 7, 0, 0 }, new object[] { 4, 7, 4, 0 }, new object[] { 4, 7, 1, -1 },
                new object[] { 4, 7, 1, LanStorage.MaxRobotSlots }, new object[] { 4, 7, 1 }, new object[] { 4, 7, 1, 0, 0 }, new object[] { 4, "7", 1, 0 } })
                ExpectRejected(() => LanStorage.DecodeTarget(LanValueCodec.Encode(bad)), "Invalid robot target accepted");

            // Requests.
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageTakable, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageTakable, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageTakable, 0, 0), "Takable request shape");
            var take = LanStorage.DecodeTakable(LanStorage.CarryPayload(-12345));
            Require((int)take[0] == LanStorage.TakableTake && (int)take[1] == -12345, "Carry roundtrip");
            var pose = new[] { 1.5f, -2f, 3f, 0f, 0.7071068f, 0f, 0.7071068f };
            var release = LanStorage.DecodeTakable(LanStorage.ReleasePayload(9, LanStorage.ReleaseThrow, 2, pose, new[] { 5f, 1f, -3f }));
            Require((int)release[2] == LanStorage.ReleaseThrow && (int)release[3] == 2 && ((float[])release[4])[1] == -2f && ((float[])release[5])[2] == -3f, "Release roundtrip");
            Require((int)LanStorage.DecodeTakable(LanStorage.RobotSetPayload(9, LanStorage.RobotAttack, 3))[3] == 3, "Robot setting roundtrip");
            Require((int)LanStorage.DecodeTakable(LanStorage.RobotMedkitPayload(9, 15))[2] == 15, "Robot medkit roundtrip");
            // 1.4.3: a fuel station's lever, pump and gun.
            var station = LanStorage.DecodeTakable(LanStorage.StationPayload(-77, LanStorage.StationTank));
            Require((int)station[0] == LanStorage.StationUse && (int)station[1] == -77 && (int)station[2] == LanStorage.StationTank, "Station use roundtrip");
            for (int action = LanStorage.StationLever; action <= LanStorage.StationDrop; action++)
                Require((int)LanStorage.DecodeTakable(LanStorage.StationPayload(5, action))[2] == action, "Station action roundtrip");
            ExpectRejected(() => LanStorage.StationPayload(0, LanStorage.StationLever), "Station without a key encoded");
            ExpectRejected(() => LanStorage.StationPayload(5, LanStorage.StationDrop + 1), "Unknown station action encoded");
            foreach (var bad in new[] { new object[] { 6, 5, 0 }, new object[] { 6, 5, 7 }, new object[] { 6, 0, 1 }, new object[] { 6, 5, 1, 0 }, new object[] { 6, 5, "1" }, new object[] { 6, 5, 1f } })
                ExpectRejected(() => LanStorage.DecodeTakable(LanValueCodec.Encode(bad)), "Invalid station request accepted");
            Func<object[], object[]> releaseWith = changes => { var r = new object[] { 2, 9, 0, -1, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }; for (int i = 0; i < changes.Length; i += 2) r[(int)changes[i]] = changes[i + 1]; return r; };
            foreach (var bad in new[] {
                new object[] { 1, 0 }, new object[] { 6, 5 }, new object[] { 1, 5, 0 }, new object[] { 3, 5, 1 },
                new object[] { 4, 5, 0, 1 }, new object[] { 4, 5, 5, 1 }, new object[] { 4, 5, LanStorage.RobotAttack, 4 }, new object[] { 4, 5, LanStorage.RobotBehavior, 2 },
                new object[] { 4, 5, LanStorage.RobotActive, -1 }, new object[] { 4, 5, 1 }, new object[] { 5, 5, LanStorage.MaxRobotSlots }, new object[] { 5, 5, -1 },
                releaseWith(new object[] { 2, 4 }), releaseWith(new object[] { 2, -1 }), releaseWith(new object[] { 3, LanProtocol.MaxCars }), releaseWith(new object[] { 3, -2 }),
                releaseWith(new object[] { 4, float.NaN }), releaseWith(new object[] { 4, 100000f }), releaseWith(new object[] { 3, 0, 4, 300f }),
                releaseWith(new object[] { 10, 0f }), releaseWith(new object[] { 7, 1f, 10, 1f }), releaseWith(new object[] { 11, 51f }), releaseWith(new object[] { 12, float.PositiveInfinity }),
                releaseWith(new object[] { 13, float.NaN }), releaseWith(new object[] { 4, 1 }), releaseWith(new object[0]).Take(13).ToArray(), releaseWith(new object[0]).Concat(new object[] { 0f }).ToArray() })
                ExpectRejected(() => LanStorage.DecodeTakable(LanValueCodec.Encode(bad)), "Invalid takable request accepted: " + string.Join(",", bad.Select(x => x == null ? "null" : x.ToString())));

            // Robot state.
            var state = new LanStorage.RobotState { Active = true, OnTrain = true, Behavior = 1, Attack = 3, Doors = 1, Battery = 100, Durability = 0,
                Devices = new[] { 300, -1, 65535 }, Storage = new[] { false, true, false }, Medkit = new[] { 1, -1, 0 }, Upgrades = new[] { -1, 310 } };
            var back = LanStorage.DecodeRobot(LanStorage.EncodeRobot(state));
            Require(back.Active && !back.Broken && back.OnTrain && back.Attack == 3 && back.Battery == 100 && back.Devices[2] == 65535 && back.Storage[1] && back.Medkit[2] == 0 && back.Upgrades[1] == 310, "Robot state roundtrip");
            Func<int, object, object[]> stateWith = (index, value) =>
            {
                var r = new object[] { 1, 0, 0, 1, 2, 0, 50, 50, new object[] { new object[] { 5, 0, -1 } }, new object[] { 6 } };
                if (index >= 0) r[index] = value;
                return r;
            };
            foreach (var bad in new[] { stateWith(0, 2), stateWith(3, 2), stateWith(4, 4), stateWith(5, -1), stateWith(6, 101), stateWith(7, -1),
                stateWith(8, new object[] { new object[] { 5, 2, -1 } }), stateWith(8, new object[] { new object[] { 5, 0, 2 } }), stateWith(8, new object[] { new object[] { 70000, 0, -1 } }),
                stateWith(8, new object[] { new object[] { 5, 0 } }), stateWith(9, new object[] { -2 }), stateWith(9, Enumerable.Range(0, LanStorage.MaxRobotSlots + 1).Select(i => (object)i).ToArray()),
                stateWith(8, 5), stateWith(-1, null).Take(9).ToArray() })
                ExpectRejected(() => LanStorage.DecodeRobot(LanValueCodec.Encode(bad)), "Invalid robot state accepted");
            ExpectRejected(() => LanStorage.EncodeRobot(new LanStorage.RobotState { Devices = new[] { 1 }, Storage = new bool[0], Medkit = new[] { -1 } }), "Inconsistent robot state encoded");
            Require(LanStorage.Describe(LanStorage.NotYours) != LanStorage.Describe(LanStorage.DeviceInUse) && LanStorage.DeviceInUse <= LanProtocol.MaxStorageStatus, "Robot statuses");
        }

        private static void TakableRequestChecks()
        {
            // The host's answer reaches the guest's callback as it is.
            var rig = new StorageRig();
            var world = new FakeTakableWorld();
            rig.Host.Takables = world;
            var answers = new List<object[]>();
            Action<byte, int, byte[]> keep = (status, amount, answer) => answers.Add(new object[] { status, amount, answer });
            Require(rig.Client.Takable(LanStorage.RobotInfoPayload(7), keep), "Robot info not queued");
            rig.Pump(1);
            Require(answers.Count == 1 && (byte)answers[0][0] == LanStorage.Ok && LanStorage.DecodeRobot((byte[])answers[0][2]).Durability == 55, "Robot state not answered");
            rig.Client.Takable(LanStorage.RobotMedkitPayload(7, 1), keep); rig.Pump(1);
            Require(answers.Count == 2 && (int)answers[1][1] == 2500, "Medkit amount not answered");
            world.Status = LanStorage.NotYours;
            rig.Client.Takable(LanStorage.CarryPayload(7), keep); rig.Pump(1);
            Require(answers.Count == 3 && (byte)answers[2][0] == LanStorage.NotYours && world.Carries == 0, "Refusal not answered");
            rig.Host.Takables = null;
            rig.Client.Takable(LanStorage.CarryPayload(7), keep); rig.Pump(1);
            Require(answers.Count == 4 && (byte)answers[3][0] == LanStorage.NotAllowed, "Takable request accepted without a world");
            Require(!rig.Client.Takable(new byte[] { 1, 2, 3 }, keep) && !rig.Client.Takable(null, keep), "Malformed takable request queued");
            // Lossy, duplicating, reordering link: each request carried out once, each answered once.
            for (int round = 0; round < 20; round++)
            {
                rig = new StorageRig();
                var lossy = new Random(round); rig.Link.Drop = pk => lossy.Next(10) < 3; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
                world = new FakeTakableWorld(); rig.Host.Takables = world; answers.Clear();
                rig.Client.Takable(LanStorage.CarryPayload(7), keep);
                rig.Client.Takable(LanStorage.ReleasePayload(7, LanStorage.ReleaseDrop, -1, new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f }, new[] { 0f, 0f, 0f }), keep);
                rig.Pump(30);
                Require(world.Carries == 1 && world.Requests.Count == 2 && answers.Count == 2 && answers.All(a => (byte)a[0] == LanStorage.Ok),
                    "Lossy link: takable requests " + world.Requests.Count + ", answers " + answers.Count);
            }
            // Hostile requests reach no world.
            rig = new StorageRig(); world = new FakeTakableWorld(); rig.Host.Takables = world;
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { 1, 0 }), new byte[] { 9, 9 }, LanValueCodec.Encode(new object[] { 2, 5, 9, -1 }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageTakable, Chunk = payload }, rig.Now += 1);
            Require(world.Requests.Count == 0, "A malformed takable request reached the world");
        }
    }
}
