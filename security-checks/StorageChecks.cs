using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZompiercerLAN
{
    // 0.6.0: shared storages. Pure parts only (no Unity): packet schema, storage
    // schema, host/guest request exchange against a fake world. The central check
    // is conservation: whatever the link does, no item appears or disappears.
    internal static partial class Program
    {
        private static object[] Item(int id, int amount, object extra0 = null, object extra1 = null)
        { return new object[] { id, amount, new object[] { extra0, extra1, null, null } }; }

        private static void StorageChecks()
        {
            StorageProtocolChecks();
            StorageSchemaChecks();
            StorageExchangeChecks();
            RapidItemPlacementChecks();
            StorageHostileChecks();
            ManualCraftViewChecks();
            CraftWindowChecks();
        }

        // 1.1.5: the guest's craft window. The host's queue and progress travel in the view;
        // crafts made before the host answers go as one request; the queue button is a request.
        private static void CraftWindowChecks()
        {
            // Views with a craft state: round trip, and malformed states refused.
            var craftView = new LanStorage.View { Handle = 3, Name = "Workbench", Weight = 1f, MaxWeight = 50f, Items = new object[] { Item(41, 1) },
                Craft = new LanStorage.CraftState { WorkingOn = 41, Elapsed = 7, Working = true, Queue = new[] { new[] { 41, 2 }, new[] { 40, 1 } } } };
            var back = LanStorage.DecodeView(LanStorage.EncodeView(craftView));
            Require(back.Craft != null && back.Craft.WorkingOn == 41 && back.Craft.Elapsed == 7 && back.Craft.Working &&
                back.Craft.Queue.Length == 2 && back.Craft.Queue[0][0] == 41 && back.Craft.Queue[0][1] == 2 && back.Craft.Queue[1][0] == 40, "Craft state round trip");
            Require(LanStorage.DecodeView(LanStorage.EncodeView(new LanStorage.View { Handle = 1, Name = "Box" })).Craft == null, "Plain view grew a craft state");
            foreach (var bad in new[]
            {
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, 0, 1 } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 70000, 0, 1, new object[0] } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, -1, 1, new object[0] } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, 0, 2, new object[0] } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, 0, 1, new object[] { new object[] { 41, 0 } } } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, 0, 1, new object[] { new object[] { 41 } } } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { 41, 0, 1, Enumerable.Repeat((object)new object[] { 41, 1 }, LanStorage.MaxCraftQueue + 1).ToArray() } },
                new object[] { LanStorage.ViewFormat, 1, (int)LanStorage.Closed, "", 0f, 0f, new object[0], new object[] { -1, 0, 0, new object[0] } },
                new object[] { LanStorage.ViewFormat, 1, 0, "W", 0f, 1f, new object[0], new object[] { -1, 0, 0, new object[0] }, 5 },
                new object[] { 1, 1, 0, "W", 0f, 1f, new object[0] },
            })
            {
                bool refused = false;
                try { LanStorage.DecodeView(LanValueCodec.Encode(bad)); }
                catch (InvalidDataException) { refused = true; }
                Require(refused, "Malformed craft view accepted");
            }
            int ci, cr;
            LanStorage.DecodeCraftCancel(LanStorage.CraftCancelPayload(3, 41), out ci, out cr);
            Require(ci == 3 && cr == 41, "Craft cancel round trip");
            foreach (var bad in new[] { new object[] { -1, 41 }, new object[] { LanStorage.MaxCraftQueue, 41 }, new object[] { 0, 70000 }, new object[] { 0 }, new object[] { 0, 41, 1 }, new object[] { 0f, 41 } })
            {
                bool refused = false;
                try { LanStorage.DecodeCraftCancel(LanValueCodec.Encode(bad), out ci, out cr); }
                catch (InvalidDataException) { refused = true; }
                Require(refused, "Malformed craft cancel accepted");
            }
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageCraftCancel, 1, 5) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageCraftCancel, 0, 5) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageCraftCancel, 1, 0) && !LanProtocol.ValidStorageRequest(14, 1, 5), "Craft cancel request shape");

            // Three crafts before the host answers: one request for three, paid three times, nothing left over.
            var rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(1, 7), Item(6, 3) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.HostWorld.BenchQueue = new List<int[]>();
            var bench = LanStorage.TrainPayload(0, LanStorage.TrainFurniture, 5);
            for (int i = 0; i < 3; i++) Require(rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 2), Item(6, 1) }), "Craft " + i + " not queued");
            Require(rig.Client.PendingRequests == 1, "Crafts of one recipe were not merged");
            rig.Pump(1);
            Require(rig.HostWorld.Queued.Count == 1 && rig.HostWorld.Queued[0][0] == 40 && rig.HostWorld.Queued[0][1] == 3 &&
                book.Count(1) == 1 && book.Count(6) == 0 && rig.GuestWorld.Backpack.Count == 0 && rig.Client.Message == "", "Merged craft not paid once per craft");
            // A different recipe, or a different workbench, is a request of its own.
            rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 1), Item(6, 0 + 1) });
            rig.Client.Craft(LanStorage.TrainPayload(0, LanStorage.TrainFurniture, 6), 40, 1, new object[] { Item(1, 1) });
            Require(rig.Client.PendingRequests == 2, "Crafts for another workbench were merged");
            rig.Pump(2);
            Require(rig.HostWorld.Queued.Count == 1 && rig.GuestWorld.Backpack.Sum(x => (int)x[1]) == 3, "Refused crafts not returned");
            rig.GuestWorld.Backpack.Clear();
            // No more than MaxCraft per request, and a bounded number of requests while the host is silent.
            rig.Link.Drop = p => p.Kind == PacketKind.StorageResult;
            int queued = 0;
            for (int i = 0; i < 60; i++) if (rig.Client.Craft(bench, 40 + i % 2, 1, new object[] { Item(1, 2), Item(6, 1) })) queued++;
            Require(queued < 60 && rig.Client.PendingRequests <= 6, "Unbounded craft requests while the host is silent");
            rig.Client.End();
            Require(rig.GuestWorld.Backpack.Sum(x => (int)x[0] == 1 ? (int)x[1] : 0) == 2 * queued, "Unsent crafts not returned at the end");
            rig.Link.Drop = p => false;

            // The open workbench's queue in the view; the queue button cancels one craft there.
            rig = new StorageRig();
            rig.HostWorld.BenchQueue = new List<int[]> { new[] { 41, 2 }, new[] { 40, 1 } };
            rig.HostWorld.BenchWorking = 41; rig.HostWorld.BenchElapsed = 4; rig.HostWorld.BenchRuns = true;
            Require(!rig.Client.CancelCraft(0, 41), "Cancel without an open workbench");
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            var shown = rig.Client.Current;
            Require(shown != null && shown.Craft != null && shown.Craft.WorkingOn == 41 && shown.Craft.Elapsed == 4 && shown.Craft.Working &&
                shown.Craft.Queue.Length == 2 && shown.Craft.Queue[0][1] == 2, "Workbench queue not shown to the guest");
            Require(rig.Client.CancelCraft(1, 41), "Cancel not queued"); rig.Pump(1);
            Require(rig.HostWorld.Cancels == 0 && rig.Client.Message == LanStorage.Describe(LanStorage.NotFound) && rig.HostWorld.BenchQueue.Count == 2, "Cancel of a changed queue entry done");
            Require(rig.Client.CancelCraft(0, 41), "Cancel not queued"); rig.Pump(1);
            Require(rig.HostWorld.Cancels == 1 && rig.HostWorld.BenchQueue[0][1] == 1 && rig.Client.Current.Craft.Queue[0][1] == 1 && rig.Client.Message == "", "Queue button not done by the host");
            rig.Client.CancelCraft(0, 41); rig.Client.Close(); rig.Pump(1);
            Require(rig.HostWorld.Cancels == 1, "Cancel sent after the workbench closed");
            Console.WriteLine("PASS: craft window views / merged crafts / bounded requests / queue cancel");
        }

        private static void ManualCraftViewChecks()
        {
            var rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(1, 4), Item(6, 2) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            Require(rig.HostWorld.GuestView != null, "Guest work view was not opened");
            rig.Link.Duplicate = true; rig.Link.Shuffle = true;
            int lost = 0;
            rig.Link.Drop = p => p.Kind == PacketKind.StorageResult && lost++ < 2;
            Require(rig.Client.Craft(BoxTarget(), 41, 1, new object[] { Item(1, 2), Item(6, 1) }), "Manual craft not sent");
            rig.Pump(5);
            Require(rig.HostWorld.Queued.Count == 1 && book.Count(1) == 2 && book.Count(6) == 1,
                "Manual craft retransmit duplicated queue or debit");
            rig.Link.Drop = p => false;
            rig.Client.Close(); rig.Pump(1);
            Require(rig.HostWorld.GuestView == null, "Closed guest view still supplies manual work");
            rig.Client.Craft(BoxTarget(), 41, 1, new object[] { Item(1, 2), Item(6, 1) }); rig.Pump(1);
            Require(rig.HostWorld.Queued.Count == 1 && book.Count(1) == 2 && book.Count(6) == 1 && rig.GuestWorld.Backpack.Count == 2,
                "Closed manual bench took ingredients");
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.GuestReady = false; rig.Host.Tick(rig.Now);
            Require(rig.HostWorld.GuestView == null, "Unavailable guest still supplies manual work");
            rig.GuestReady = true; rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.HostWorld.Box.Near = false; rig.Pump(1);
            Require(rig.HostWorld.GuestView == null, "Distant guest still supplies manual work");
            rig.HostWorld.Box.Near = true; rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.Host.End();
            Require(rig.HostWorld.GuestView == null, "Ended session retains manual work");
            Console.WriteLine("PASS: manual craft view lifecycle / refused escrow / lost replies / duplicate requests");
        }

        private static void StorageProtocolChecks()
        {
            Packet decoded;
            var take = LanStorage.TakePayload(0, 1, new byte[32]);
            var packets = new[]
            {
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageOpen, Revision = 0, Chunk = LanStorage.TrainPayload(1, 1, 77) },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 2, Action = LanProtocol.StorageTake, Revision = 3, Chunk = take },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 3, Action = LanProtocol.StoragePut, Revision = 3, Chunk = LanStorage.EncodeItem(Item(5, 2)) },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 4, Action = LanProtocol.StorageClose, Revision = 3 },
                new Packet { Kind = PacketKind.StorageResult, Sequence = 4, Action = LanStorage.TooFar },
                new Packet { Kind = PacketKind.StorageResult, Sequence = 5, Action = LanStorage.Ok, Revision = 2, Amount = 7, Chunk = LanStorage.EncodeItem(Item(5, 7)) },
                new Packet { Kind = PacketKind.StorageViewChunk, Revision = 1, ChunkCount = 2, ChunkIndex = 1, Chunk = new byte[17] },
                new Packet { Kind = PacketKind.StorageViewAck, Revision = 1 },
            };
            foreach (var p in packets)
            {
                p.GameVersion = "checks"; p.Session = 42;
                var bytes = LanProtocol.Encode(p);
                Require(LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == p.Kind && decoded.Sequence == p.Sequence && decoded.Amount == p.Amount &&
                    (p.Chunk == null ? decoded.Chunk == null || decoded.Chunk.Length == 0 : decoded.Chunk.SequenceEqual(p.Chunk)), "Valid storage packet rejected: " + p.Kind);
                for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated storage packet accepted"); }
                var tail = new byte[bytes.Length + 1]; Array.Copy(bytes, tail, bytes.Length); Require(!LanProtocol.TryDecode(tail, out decoded), "Storage packet trailing bytes accepted");
            }
            // Request shapes: open has no handle, take/put need one and a payload, close has no payload.
            var badShapes = new[]
            {
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageOpen, Revision = 2, Chunk = new byte[5] },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageOpen, Revision = 0 },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageTake, Revision = 0, Chunk = take },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StoragePut, Revision = 1 },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageClose, Revision = 1, Chunk = new byte[1] },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = 0, Revision = 1, Chunk = new byte[1] },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = 5, Revision = 1, Chunk = new byte[1] },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 0, Action = LanProtocol.StorageClose, Revision = 1 },
                new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageClose, Revision = -1 },
                new Packet { Kind = PacketKind.StorageResult, Sequence = 1, Action = LanProtocol.MaxStorageStatus + 1 },
                new Packet { Kind = PacketKind.StorageResult, Sequence = 1, Action = 0, Amount = -1 },
                new Packet { Kind = PacketKind.StorageResult, Sequence = 1, Action = 0, Amount = 1000001 },
                new Packet { Kind = PacketKind.StorageViewChunk, Revision = 1, ChunkCount = LanProtocol.MaxGuestChunks + 1, ChunkIndex = 0, Chunk = new byte[900] },
                new Packet { Kind = PacketKind.StorageViewAck, Revision = 0 },
            };
            foreach (var p in badShapes)
            {
                p.GameVersion = "checks"; p.Session = 42;
                Require(!LanProtocol.TryDecode(LanProtocol.Encode(p), out decoded), "Malformed storage packet accepted: " + p.Kind + "/" + p.Action);
            }
            ExpectRejected(() => LanProtocol.Encode(new Packet { Kind = PacketKind.StorageResult, GameVersion = "checks", Sequence = 1, Chunk = new byte[LanProtocol.MaxStoragePayload + 1] }), "Oversized storage payload encoded");
            foreach (var kind in new[] { PacketKind.StorageRequest, PacketKind.StorageViewAck })
                Require(LanNetworkPolicy.Allowed(true, kind, true) && !LanNetworkPolicy.Allowed(false, kind, true) && !LanNetworkPolicy.Allowed(true, kind, false), "Guest-to-host direction: " + kind);
            foreach (var kind in new[] { PacketKind.StorageResult, PacketKind.StorageViewChunk })
                Require(!LanNetworkPolicy.Allowed(true, kind, true) && LanNetworkPolicy.Allowed(false, kind, true) && !LanNetworkPolicy.Allowed(false, kind, false), "Host-to-guest direction: " + kind);
            var limits = new LanMessageLimits(); int allowed = 0;
            for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.StorageRequest, 0)) allowed++;
            Require(allowed == 20, "Storage request burst not bounded");
            Console.WriteLine("PASS: storage packets (schema 11+) / request shapes / truncation / directions / rate limits");
        }

        private static void StorageSchemaChecks()
        {
            var scene = LanStorage.DecodeTarget(LanStorage.ScenePayload("Terrain1", 1f, 2f, 3f, "Storage Box"));
            Require((int)scene[0] == LanStorage.SceneTarget && (string)scene[5] == "Storage Box", "Scene target roundtrip");
            var train = LanStorage.DecodeTarget(LanStorage.TrainPayload(7, LanStorage.TrainPart, -5));
            Require((int)train[1] == 7 && (int)train[3] == -5, "Train target roundtrip");
            var badTargets = new List<byte[]>
            {
                LanValueCodec.Encode(new object[] { 1, "", 1f, 2f, 3f, "Box" }), LanValueCodec.Encode(new object[] { 1, "T", 1f, 2f, 3f, "" }),
                LanValueCodec.Encode(new object[] { 1, "T", 1f, 2f, 300000f, "Box" }), LanValueCodec.Encode(new object[] { 1, "T", 1, 2f, 3f, "Box" }),
                LanValueCodec.Encode(new object[] { 1, "T", 1f, 2f, 3f, new string('n', 65) }), LanValueCodec.Encode(new object[] { 2, 8, 1, 5 }),
                LanValueCodec.Encode(new object[] { 2, 0, 3, 5 }), LanValueCodec.Encode(new object[] { 2, 0, 1, 0 }), LanValueCodec.Encode(new object[] { 3, 0, 1, 5 }),
                LanValueCodec.Encode(new object[] { 2, 0, 1 }), new byte[0], new byte[LanProtocol.MaxStoragePayload + 1], new byte[] { 5, 1 },
            };
            foreach (var target in badTargets) ExpectRejected(() => LanStorage.DecodeTarget(target), "Invalid storage target accepted");
            // 0.8.0: build hits and refuels.
            int bc, bk, bi, bt;
            LanStorage.DecodeBuild(LanStorage.BuildPayload(3, LanStorage.TrainPart, -77, 12), out bc, out bk, out bi, out bt);
            Require(bc == 3 && bk == LanStorage.TrainPart && bi == -77 && bt == 12, "Build roundtrip");
            foreach (var bad in new[] { new object[] { 8, 2, 1, 1 }, new object[] { -1, 2, 1, 1 }, new object[] { 0, 3, 1, 1 }, new object[] { 0, 1, 1, -1 },
                new object[] { 0, 1, 1, 70000 }, new object[] { 0, 1, 1 }, new object[] { 0, 1, 1, 1f } })
                ExpectRejected(() => LanStorage.DecodeBuild(LanValueCodec.Encode(bad), out bc, out bk, out bi, out bt), "Invalid build accepted");
            float wx, wy, wz; int wt, we; object[] wesc;
            Require(LanStorage.DecodeWork(LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, -2f, 250f, -1, 7, Item(8, 1)), out wx, out wy, out wz, out wt, out we, out wesc) == LanStorage.WorkRefuel && we == 7 && (int)wesc[0] == 8, "Refuel roundtrip");
            foreach (var bad in new[] {
                LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, 8, Item(8, 1)), LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, -1, Item(8, 1)),
                LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, 2f, 300f, -1, 0, Item(8, 1)), LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, 2f, 3f, 5, 0, Item(8, 1)),
                LanStorage.WorkPayload(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, 0, null), LanStorage.WorkPayload(LanStorage.WorkHit, 1f, 2f, 3f, 2, 1, null),
                LanStorage.WorkPayload(6, 1f, 2f, 3f, 2, 0, null) })
                ExpectRejected(() => LanStorage.DecodeWork(bad, out wx, out wy, out wz, out wt, out we, out wesc), "Invalid work accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageBuild, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageBuild, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageBuild, 0, 0) && !LanProtocol.ValidStorageRequest(13, 0, 10), "Build request shape");
            // 0.8.1: placements.
            int pp, pc, pt; float[] ppoint, ppose;
            LanStorage.DecodePlace(LanStorage.PlacePayload(42, 2, 7, new[] { 1f, 2f, 3f }, new[] { -4f, 5f, 6f, 0f, .7071068f, 0f, .7071068f }), out pp, out pc, out pt, out ppoint, out ppose);
            Require(pp == 42 && pc == 2 && pt == 7 && ppoint[2] == 3f && ppose[0] == -4f && Math.Abs(ppose[4] - .7071068f) < 1e-6, "Placement roundtrip");
            var goodPose = new object[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f };
            Func<object[], object[]> place = tail => new object[] { 42, 0, 3, 0f, 0f, 0f }.Concat(tail).ToArray();
            foreach (var bad in new[] {
                new object[] { 70000, 0, 3, 0f, 0f, 0f }.Concat(goodPose).ToArray(), new object[] { 42, 8, 3, 0f, 0f, 0f }.Concat(goodPose).ToArray(),
                new object[] { 42, 0, -2, 0f, 0f, 0f }.Concat(goodPose).ToArray(), new object[] { 42, 0, 3, 300f, 0f, 0f }.Concat(goodPose).ToArray(),
                place(new object[] { 1f, 2f, 300f, 0f, 0f, 0f, 1f }), place(new object[] { 1f, 2f, 3f, 0f, 0f, 0f, 0f }), place(new object[] { 1f, 2f, 3f, 1f, 1f, 0f, 0f }),
                place(new object[] { 1f, 2f, 3f, 0f, 0f, 0f, 1 }), place(new object[] { 1f, 2f, 3f, 0f, 0f, 0f }) })
                ExpectRejected(() => LanStorage.DecodePlace(LanValueCodec.Encode(bad), out pp, out pc, out pt, out ppoint, out ppose), "Invalid placement accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StoragePlace, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StoragePlace, 2, 10), "Placement request shape");
            // 0.10.0: train controls.
            int ck, ci, cc, cv; object[] ce;
            LanStorage.DecodeControl(LanStorage.ControlPayload(LanStorage.ControlLever, 1, -5, 750, null), out ck, out ci, out cc, out cv, out ce);
            Require(ck == LanStorage.ControlLever && ci == 1 && cc == -5 && cv == 750 && ce == null, "Control roundtrip");
            LanStorage.DecodeControl(LanStorage.ControlPayload(LanStorage.ControlRepair, 3, 9, 0, Item(66, 1)), out ck, out ci, out cc, out cv, out ce);
            Require(ck == LanStorage.ControlRepair && (int)ce[0] == 66, "Repair roundtrip");
            LanStorage.DecodeControl(LanStorage.ControlPayload(LanStorage.ControlPartLight, 7, 123, LanStorage.TrainPart, null), out ck, out ci, out cc, out cv, out ce);
            // 1.1.6: train doors (owner, open, 23 bits of the path hash).
            LanStorage.DecodeControl(LanStorage.ControlPayload(LanStorage.ControlDoor, 2, 77, LanStorage.DoorValue(LanStorage.DoorPart, true, 0x7FFFFF), null), out ck, out ci, out cc, out cv, out ce);
            int dOwner, dHash; bool dOpen; LanStorage.DoorFields(cv, out dOwner, out dOpen, out dHash);
            Require(ck == LanStorage.ControlDoor && ci == 2 && cc == 77 && dOwner == LanStorage.DoorPart && dOpen && dHash == 0x7FFFFF && ce == null, "Door control roundtrip");
            LanStorage.DecodeControl(LanStorage.ControlPayload(LanStorage.ControlDoor, 0, 0, LanStorage.DoorValue(LanStorage.DoorTrain, false, 5), null), out ck, out ci, out cc, out cv, out ce);
            LanStorage.DoorFields(cv, out dOwner, out dOpen, out dHash);
            Require(dOwner == LanStorage.DoorTrain && !dOpen && dHash == 5, "Train door control roundtrip");
            foreach (var bad in new[] {
                new object[] { LanStorage.ControlDoor, 8, 1, LanStorage.DoorValue(LanStorage.DoorPart, true, 1), null }, new object[] { LanStorage.ControlDoor, -1, 1, LanStorage.DoorValue(LanStorage.DoorPart, true, 1), null },
                new object[] { LanStorage.ControlDoor, 0, 0, LanStorage.DoorValue(LanStorage.DoorFurniture, true, 1), null }, new object[] { LanStorage.ControlDoor, 0, 5, LanStorage.DoorValue(LanStorage.DoorCar, true, 1), null },
                new object[] { LanStorage.ControlDoor, 1, 0, LanStorage.DoorValue(LanStorage.DoorTrain, true, 1), null }, new object[] { LanStorage.ControlDoor, 0, 0, 0, null },
                new object[] { LanStorage.ControlDoor, 0, 0, 5, null }, new object[] { LanStorage.ControlDoor, 0, 0, LanStorage.DoorCar | 16, null },
                new object[] { LanStorage.ControlDoor, 0, 0, -1, null }, new object[] { LanStorage.ControlDoor, 0, 0, LanStorage.DoorValue(LanStorage.DoorCar, true, 1), Item(66, 1) } })
                ExpectRejected(() => LanStorage.DecodeControl(LanValueCodec.Encode(bad), out ck, out ci, out cc, out cv, out ce), "Invalid door control accepted");
            Require(LanStorage.DescribeDoor(LanStorage.TooFar) != LanStorage.Describe(LanStorage.TooFar) && LanStorage.DescribeDoor(LanStorage.Invalid) == LanStorage.Describe(LanStorage.Invalid), "Door answers");
            foreach (var bad in new[] {
                new object[] { 7, 0, 0, 0, null }, new object[] { 0, 0, 0, 0, null }, new object[] { LanStorage.ControlButton, 32, 0, 0, null },
                new object[] { LanStorage.ControlButton, -1, 0, 0, null }, new object[] { LanStorage.ControlButton, 0, 0, 1, null },
                new object[] { LanStorage.ControlButton, 0, 0, 0, Item(66, 1) }, new object[] { LanStorage.ControlRepair, 0, 0, 0, null },
                new object[] { LanStorage.ControlRepair, 0, 0, 0, Item(67, 1) }, new object[] { LanStorage.ControlRepair, 0, 0, 0, Item(66, 2) },
                new object[] { LanStorage.ControlLever, LanProtocol.MaxLevers, 0, 0, null }, new object[] { LanStorage.ControlLever, 0, 0, 1000001, null },
                new object[] { LanStorage.ControlCabLight, 0, 0, 0, 5 }, new object[] { LanStorage.ControlPartLight, 8, 1, 2, null },
                new object[] { LanStorage.ControlPartLight, 0, 0, 2, null }, new object[] { LanStorage.ControlPartLight, 0, 1, 3, null },
                new object[] { LanStorage.ControlButton, 0, 0, 0 }, new object[] { LanStorage.ControlButton, 0, 0f, 0, null } })
                ExpectRejected(() => LanStorage.DecodeControl(LanValueCodec.Encode(bad), out ck, out ci, out cc, out cv, out ce), "Invalid control accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageControl, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageControl, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageControl, 0, 0), "Control request shape");
            // Fingerprints ignore stack size and the train-car slot, not the item's state.
            var gun = Item(9, 1, 55f, null); var moved = Item(9, 3, 55f, null); ((object[])moved[2])[2] = 4;
            Require(LanStorage.SameFingerprint(LanStorage.Fingerprint(gun), LanStorage.Fingerprint(moved)), "Fingerprint depends on amount or car slot");
            Require(!LanStorage.SameFingerprint(LanStorage.Fingerprint(gun), LanStorage.Fingerprint(Item(9, 1, 54f, null))), "Fingerprint ignores durability");
            Require(!LanStorage.SameFingerprint(LanStorage.Fingerprint(gun), LanStorage.Fingerprint(Item(10, 1, 55f, null))), "Fingerprint ignores item id");
            Require(((object[])LanStorage.DecodeItem(LanStorage.EncodeItem(moved))[2])[2] == null, "Train-car slot crossed the wire");
            foreach (var bad in new[] { Item(70000, 1), Item(1, 0), Item(1, 1000001), new object[] { 1, 1, new object[3] }, new object[] { 1, 1, new object[] { "x", null, null, null } } })
                ExpectRejected(() => LanStorage.EncodeItem(bad), "Invalid storage item accepted");
            // A huge mod tree does not fit one packet.
            var heavy = Item(1, 1); var mods = new object[8];
            for (int i = 0; i < mods.Length; i++) mods[i] = Enumerable.Range(0, 12).Select(j => (object)Item(j, 1, 1f, 2f)).ToArray();
            ((object[])heavy[2])[3] = mods;
            Require(!LanStorage.ItemFits(heavy), "Oversized item fits a request");
            // Views.
            var view = new LanStorage.View { Handle = 3, Name = "Metal Storage", Weight = 4.5f, MaxWeight = 80f, Items = new object[] { Item(1, 5), gun } };
            var back = LanStorage.DecodeView(LanStorage.EncodeView(view));
            Require(back.Handle == 3 && back.Name == "Metal Storage" && back.Items.Length == 2 && (int)((object[])back.Items[0])[1] == 5, "View roundtrip");
            var closed = LanValueCodec.Encode(new object[] { 1, 3, (int)LanStorage.TooFar, "x", 0f, 0f, new object[] { Item(1, 1) } });
            ExpectRejected(() => LanStorage.DecodeView(closed), "Closed view with items accepted");
            ExpectRejected(() => LanStorage.DecodeView(LanValueCodec.Encode(new object[] { 1, 0, 0, "x", 0f, 0f, new object[0] })), "View without handle accepted");
            ExpectRejected(() => LanStorage.DecodeView(LanValueCodec.Encode(new object[] { 1, 1, 0, "x", 0f, -1f, new object[0] })), "Negative capacity accepted");
            ExpectRejected(() => LanStorage.EncodeView(new LanStorage.View { Handle = 1, Items = new object[LanGuestSchema.MaxItems + 1] }), "Oversized view encoded");
            var random = new Random(77113); var seed = LanStorage.EncodeView(new LanStorage.View { Handle = 9, Name = "Box", Items = Enumerable.Range(1, 30).Select(i => (object)Item(i, i, (float)i, null)).ToArray() });
            for (int i = 0; i < 20000; i++)
            {
                var bytes = (byte[])seed.Clone();
                for (int j = 0; j < 1 + random.Next(4); j++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                if (random.Next(4) == 0) Array.Resize(ref bytes, random.Next(1, bytes.Length));
                try { LanStorage.DecodeView(bytes); } catch (InvalidDataException) { }
                try { LanStorage.DecodeTarget(bytes.Length <= LanProtocol.MaxStoragePayload ? bytes : bytes.Take(LanProtocol.MaxStoragePayload).ToArray()); } catch (InvalidDataException) { }
                try { LanStorage.DecodeItem(bytes.Length <= LanProtocol.MaxStoragePayload ? bytes : bytes.Take(LanProtocol.MaxStoragePayload).ToArray()); } catch (InvalidDataException) { }
            }
            Console.WriteLine("PASS: storage schema (targets, items, fingerprints, views, 20000 mutated views/targets/items)");
        }

        // ---- Fake worlds ----
        private sealed class FakeBox
        {
            internal readonly List<object[]> Items = new List<object[]>();
            internal int Capacity = 1000, Forbidden = -1;
            internal bool Near = true, Exists = true;
            internal int Total { get { return Items.Sum(i => (int)i[1]); } }
        }
        private static void Merge(List<object[]> items, object[] item)
        {
            var fingerprint = LanStorage.Fingerprint(item);
            var same = items.FirstOrDefault(i => LanStorage.SameFingerprint(LanStorage.Fingerprint(i), fingerprint) && ((object[])i[2]).All(x => x == null));
            if (same != null && ((object[])item[2]).All(x => x == null)) same[1] = (int)same[1] + (int)item[1];
            else items.Add(LanStorage.Normalize(item));
        }
        private sealed class FakeHostWorld : ILanStorageHostWorld, ILanStoragePlacementWorld, ILanTrainControlWorld, ILanDeathWorld, ILanStorageViewWorld, ILanThrowWorld, ILanDecorWorld, ILanBuildWorld
        {
            // 1.4.2: furniture kits placed (kit, car) and dismantling requests (mode, car, owner, id, tool).
            internal readonly List<object[]> Furnitures = new List<object[]>();
            internal byte FurnitureStatus = LanStorage.Ok;
            public byte PlaceFurniture(int kit, int car, float[] pose)
            {
                if (FurnitureStatus != LanStorage.Ok) return FurnitureStatus;
                Furnitures.Add(new object[] { kit, car, pose });
                return LanStorage.Ok;
            }
            internal readonly List<object[]> Dismantles = new List<object[]>();
            internal byte DismantleStatus = LanStorage.Ok;
            internal object[] DismantleGiven;
            internal bool DismantleDone;
            public byte Dismantle(int mode, int car, int owner, int identity, int tool, out object[] given, out bool done)
            {
                given = null; done = false;
                if (DismantleStatus != LanStorage.Ok) return DismantleStatus;
                Dismantles.Add(new object[] { mode, car, owner, identity, tool });
                given = DismantleGiven == null ? null : (object[])DismantleGiven.Clone(); done = DismantleDone;
                return LanStorage.Ok;
            }
            // 1.4.0: the guest's throws (item, car) and train decor requests, or a refusal.
            internal readonly List<object[]> Throws = new List<object[]>();
            internal byte ThrowStatus = LanStorage.Ok;
            public byte Throw(int item, int car, float[] at, float[] turn, int type)
            {
                if (ThrowStatus != LanStorage.Ok) return ThrowStatus;
                Throws.Add(new object[] { item, car, at, turn, type });
                return LanStorage.Ok;
            }
            internal readonly List<LanStorage.DecorRequest> Decors = new List<LanStorage.DecorRequest>();
            internal byte DecorStatus = LanStorage.Ok;
            public byte Decor(LanStorage.DecorRequest request)
            {
                if (DecorStatus != LanStorage.Ok) return DecorStatus;
                Decors.Add(request);
                return LanStorage.Ok;
            }
            internal object GuestView;
            public void SetGuestView(object container) { GuestView = container; }
            // Guest death (0.12.0): what went into the bags, how often, or a refusal.
            internal readonly List<object[]> DeathItems = new List<object[]>();
            internal int Deaths;
            internal byte DeathStatus = LanStorage.Ok;
            public byte Death(object[] items, float x, float y, float z, int car)
            {
                if (DeathStatus != LanStorage.Ok) return DeathStatus;
                Deaths++; foreach (object[] item in items) DeathItems.Add(item);
                return LanStorage.Ok;
            }
            // Train controls (0.10.0): button 0 is broken until repaired; levers by index.
            internal bool Allows = true, Broken = true;
            internal int Presses;
            internal readonly float[] Levers = new float[2];
            // 1.1.6: train doors need no driving permission.
            internal int DoorRequests; internal byte DoorAnswer = LanStorage.Ok;
            public byte Control(int kind, int index, int check, int value, object[] escrow)
            {
                if (kind == LanStorage.ControlDoor) { DoorRequests++; return DoorAnswer; }
                if (!Allows) return LanStorage.Locked;
                if (!GroundNear) return LanStorage.TooFar;
                switch (kind)
                {
                    case LanStorage.ControlButton: if (index == 0 && Broken) return LanStorage.Broken; Presses++; return LanStorage.Ok;
                    case LanStorage.ControlRepair: if (index != 0 || !Broken) return LanStorage.NotAllowed; Broken = false; return LanStorage.Ok;
                    case LanStorage.ControlLever: if (index >= Levers.Length) return LanStorage.NotFound; Levers[index] = value / 1000f; return LanStorage.Ok;
                    default: return LanStorage.Ok;
                }
            }
            internal readonly FakeBox Box = new FakeBox();
            internal int Resolves, Takes, Puts;
            public object Resolve(object[] target, out byte status)
            {
                Resolves++;
                if ((int)target[0] != LanStorage.SceneTarget || (string)target[5] != "Storage Box") { status = LanStorage.NotFound; return null; }
                status = Check(Box);
                return status == LanStorage.Ok ? Box : null;
            }
            public byte Check(object c) { return !Box.Exists ? LanStorage.Gone : !Box.Near ? LanStorage.TooFar : LanStorage.Ok; }
            public LanStorage.View Snapshot(object c)
            {
                var view = new LanStorage.View { Name = "Storage Box", Weight = Box.Total, MaxWeight = Box.Capacity, Items = Box.Items.Select(i => (object)LanStorage.Normalize(i)).ToArray() };
                if (BenchQueue != null)
                    view.Craft = new LanStorage.CraftState { WorkingOn = BenchWorking, Elapsed = BenchElapsed, Working = BenchRuns, Queue = BenchQueue.Select(q => (int[])q.Clone()).ToArray() };
                return view;
            }
            // 1.1.5: the box as a workbench (queue of [item id, amount]) when BenchQueue is set.
            internal List<int[]> BenchQueue;
            internal int BenchWorking = -1, BenchElapsed, Cancels;
            internal bool BenchRuns;
            public byte CancelCraft(object c, int index, int recipe)
            {
                if (BenchQueue == null) return LanStorage.NotAllowed;
                if (index >= BenchQueue.Count || BenchQueue[index][0] != recipe) return LanStorage.NotFound;
                if (--BenchQueue[index][1] == 0) BenchQueue.RemoveAt(index);
                Cancels++;
                return LanStorage.Ok;
            }
            public byte Take(object c, int index, byte[] fingerprint, int amount, out object[] taken)
            {
                taken = null;
                object[] item = index < Box.Items.Count && LanStorage.SameFingerprint(LanStorage.Fingerprint(Box.Items[index]), fingerprint) ? Box.Items[index]
                    : Box.Items.FirstOrDefault(i => LanStorage.SameFingerprint(LanStorage.Fingerprint(i), fingerprint));
                if (item == null) return LanStorage.NotFound;
                amount = Math.Min(amount, (int)item[1]);
                taken = LanStorage.WithAmount(item, amount);
                if (amount == (int)item[1]) Box.Items.Remove(item); else item[1] = (int)item[1] - amount;
                Takes++;
                return LanStorage.Ok;
            }
            public byte Put(object c, object[] item, out int accepted)
            {
                accepted = 0;
                if ((int)item[0] == Box.Forbidden) return LanStorage.NotAllowed;
                int space = Box.Capacity - Box.Total;
                if (space < 1) return LanStorage.NoSpace;
                accepted = Math.Min(space, (int)item[1]);
                Merge(Box.Items, LanStorage.WithAmount(item, accepted));
                Puts++;
                return LanStorage.Ok;
            }
            // The host's world: loose items by host id; drops land here too.
            internal readonly Dictionary<int, object[]> Ground = new Dictionary<int, object[]>();
            internal bool GroundNear = true;
            internal int Pickups, Drops, NextGroundId = 1000;
            public byte Pickup(int hostId, int itemId, int amount, out object[] taken)
            {
                taken = null;
                object[] item;
                if (!Ground.TryGetValue(hostId, out item) || (int)item[0] != itemId) return LanStorage.NotFound;
                if (!GroundNear) return LanStorage.TooFar;
                amount = Math.Min(amount, (int)item[1]);
                taken = LanStorage.WithAmount(item, amount);
                if (amount == (int)item[1]) Ground.Remove(hostId); else item[1] = (int)item[1] - amount;
                Pickups++;
                return LanStorage.Ok;
            }
            public byte Drop(object[] item, float x, float y, float z)
            {
                if (!GroundNear) return LanStorage.TooFar;
                Ground.Add(NextGroundId++, LanStorage.Normalize(item));
                Drops++;
                return LanStorage.Ok;
            }
            internal int Placements, LastCar;
            internal float[] LastPose;
            public byte PlaceItem(object[] item, int car, float[] pose)
            {
                if (!GroundNear) return LanStorage.TooFar;
                LastCar = car; LastPose = (float[])pose.Clone(); Placements++;
                Ground.Add(NextGroundId++, LanStorage.Normalize(item));
                return LanStorage.Ok;
            }
            internal int Hits, StationFuel = 20;
            internal readonly List<int[]> Queued = new List<int[]>();
            internal byte QueueStatus = LanStorage.Ok;
            // Workbench: 40 <- 2x1 + 1x6; 41 is offered by a manual workbench only.
            public int[][] Recipe(object[] target, int recipe, out byte status)
            {
                status = LanStorage.Ok;
                if (recipe == 40 || recipe == 41 && GuestView != null) return new[] { new[] { 1, 2 }, new[] { 6, 1 } };
                status = recipe == 41 ? LanStorage.Manual : LanStorage.NotAllowed;
                return null;
            }
            public byte Queue(object[] target, int recipe, int count)
            {
                if (QueueStatus != LanStorage.Ok) return QueueStatus;
                Queued.Add(new[] { recipe, count }); return LanStorage.Ok;
            }
            internal float TrainFuel = 80f, TrainFuelMax = 100f;
            public byte Work(int kind, float x, float y, float z, int tool, int extra, object[] escrow, out object[] given, out int detail)
            {
                given = null; detail = 0;
                if (!GroundNear) return LanStorage.TooFar;
                switch (kind)
                {
                    case LanStorage.WorkHit: Hits++; Ground.Add(NextGroundId++, Item(30, 2)); return LanStorage.Ok; // wood falls out
                    case LanStorage.WorkGather: given = Item(31, 2); return LanStorage.Ok;
                    case LanStorage.WorkDig: given = Item(extra == 1 ? 89 : 296, 3); return LanStorage.Ok;
                    case LanStorage.WorkFuel:
                        if ((int)escrow[0] != 92) return LanStorage.NotAllowed;
                        if (StationFuel < 10) return LanStorage.Empty;
                        StationFuel -= 10; given = Item(8, 1); return LanStorage.Ok;
                    case LanStorage.WorkRefuel:
                        if ((int)escrow[0] != 8) return LanStorage.NotAllowed;
                        if (TrainFuel >= TrainFuelMax) return LanStorage.Full;
                        TrainFuel = Math.Min(TrainFuelMax, TrainFuel + 10f); given = Item(92, 1); detail = (int)(TrainFuel * 100f); return LanStorage.Ok;
                }
                return LanStorage.Invalid;
            }
            // Host blueprints by host id: [kind, points, maximum]; part 70 needs 1x3 + 6x1, tool 2 builds with power 4.
            internal readonly Dictionary<int, float[]> Blueprints = new Dictionary<int, float[]>();
            internal int Completed;
            // Host mount points: (car, type) -> taken; part 200 is a wall (type 3), 201 lies on surfaces.
            internal readonly HashSet<string> Taken = new HashSet<string>();
            internal int Placed;
            public byte Place(int part, int car, int pointType, float[] point, float[] pose)
            {
                if (!GroundNear) return LanStorage.TooFar;
                if (part == 200 ? pointType != 3 : part == 201 ? pointType != -1 : true) return LanStorage.NotAllowed;
                if (pointType >= 0)
                {
                    string key = car + ":" + point[0] + ":" + point[1] + ":" + point[2];
                    if (!Taken.Add(key)) return LanStorage.Occupied;
                }
                Placed++;
                return LanStorage.Ok;
            }
            public byte Build(int car, int kind, int identity, int tool, Func<int[][], bool> pay, out bool completed)
            {
                completed = false;
                float[] b;
                if (!Blueprints.TryGetValue(identity, out b) || (int)b[0] != kind) return LanStorage.NotFound;
                if (!GroundNear) return LanStorage.TooFar;
                if (tool != 2) return LanStorage.NotAllowed;
                float next = Math.Min(b[2], b[1] + 4f);
                if (next >= b[2] && kind == LanStorage.TrainPart && !pay(new[] { new[] { 1, 3 }, new[] { 6, 1 } })) return LanStorage.Missing;
                b[1] = next;
                if (next >= b[2]) { completed = true; Completed++; Blueprints.Remove(identity); }
                return LanStorage.Ok;
            }
        }
        private sealed class FakeGuestWorld : ILanStorageGuestWorld
        {
            internal readonly List<object[]> Backpack = new List<object[]>();
            internal int Capacity = 1000, Gives;
            internal int Total { get { return Backpack.Sum(i => (int)i[1]); } }
            public int Room(object[] item) { return Math.Max(0, Capacity - Total); }
            public void Give(object[] item) { Gives++; Merge(Backpack, item); }
            public void Spend(int[][] needs)
            {
                foreach (var need in needs)
                {
                    int left = need[1];
                    foreach (var item in Backpack.Where(i => (int)i[0] == need[0]).ToList())
                    {
                        int take = Math.Min(left, (int)item[1]); left -= take;
                        if (take == (int)item[1]) Backpack.Remove(item); else item[1] = (int)item[1] - take;
                    }
                }
            }
            // What the window does: escrow, then a put (or an immediate return).
            internal bool PutFromBackpack(LanStorageClient client, int index, int amount)
            {
                var item = Backpack[index];
                amount = Math.Min(amount, (int)item[1]);
                var escrow = LanStorage.WithAmount(item, amount);
                if (amount == (int)item[1]) Backpack.RemoveAt(index); else item[1] = (int)item[1] - amount;
                if (client.Put(escrow)) return true;
                Give(escrow); return false;
            }
        }

        private sealed class StorageLink
        {
            internal readonly List<Packet> ToHost = new List<Packet>(), ToGuest = new List<Packet>();
            internal Func<Packet, bool> Drop = p => false;
            internal bool Duplicate, Shuffle;
            internal int Dropped, Sent;
            private readonly Random _random = new Random(4242);
            internal Action<Packet> Sender(List<Packet> queue) => p =>
            {
                Sent++;
                if (Drop(p)) { Dropped++; return; }
                p.GameVersion = "checks"; p.Session = 42; Packet copy;
                Require(LanProtocol.TryDecode(LanProtocol.Encode(p), out copy), "Storage packet failed the real decoder: " + p.Kind);
                queue.Add(copy);
                if (Duplicate && _random.Next(3) == 0) { Packet again; LanProtocol.TryDecode(LanProtocol.Encode(p), out again); queue.Add(again); }
            };
            internal List<Packet> Take(List<Packet> queue)
            {
                var all = queue.ToList(); queue.Clear();
                return Shuffle ? all.OrderBy(_ => _random.Next()).ToList() : all;
            }
        }
        private sealed class StorageRig
        {
            internal readonly StorageLink Link = new StorageLink();
            internal readonly FakeHostWorld HostWorld = new FakeHostWorld();
            internal readonly FakeGuestWorld GuestWorld = new FakeGuestWorld();
            internal readonly List<string> Log = new List<string>();
            internal bool GuestReady = true;
            internal int Changes;
            internal LanStorageHost Host;
            internal LanStorageClient Client;
            internal double Now;
            internal StorageRig()
            {
                Host = new LanStorageHost(HostWorld, Link.Sender(Link.ToGuest), Log.Add, () => GuestReady);
                Client = new LanStorageClient(GuestWorld, Link.Sender(Link.ToHost), Log.Add, () => Changes++);
                Host.Begin(); Client.Begin();
            }
            internal void Pump(double seconds)
            {
                for (double end = Now + seconds; Now < end; Now += 0.05)
                {
                    Host.Tick(Now); Client.Tick(Now);
                    foreach (var p in Link.Take(Link.ToHost)) Host.Receive(p, Now);
                    foreach (var p in Link.Take(Link.ToGuest)) Client.Receive(p);
                }
            }
            internal Dictionary<int, int> Totals()
            {
                var totals = new Dictionary<int, int>();
                foreach (var item in HostWorld.Box.Items.Concat(GuestWorld.Backpack).Concat(HostWorld.Ground.Values))
                { int id = (int)item[0]; int v; totals.TryGetValue(id, out v); totals[id] = v + (int)item[1]; }
                return totals;
            }
            internal static bool Same(Dictionary<int, int> a, Dictionary<int, int> b)
            { return a.Count == b.Count && a.All(pair => { int v; return b.TryGetValue(pair.Key, out v) && v == pair.Value; }); }
        }
        private static byte[] BoxTarget() { return LanStorage.ScenePayload("Terrain1", 1f, 2f, 3f, "Storage Box"); }

        private static void StorageExchangeChecks()
        {
            // 1. Open, see, take part of a stack, take a whole item, put one back.
            var rig = new StorageRig();
            rig.HostWorld.Box.Items.AddRange(new[] { Item(1, 10), Item(2, 1, 80f, null), Item(3, 4) });
            rig.GuestWorld.Backpack.Add(Item(7, 3));
            var start = rig.Totals();
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            Require(rig.Client.Current != null && rig.Client.Current.Items.Length == 3 && rig.Host.Open, "Opened storage not shown");
            rig.Client.Take(0, 4); rig.Pump(1);
            Require((int)rig.HostWorld.Box.Items[0][1] == 6 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 1 && (int)i[1] == 4), "Partial take");
            rig.Client.Take(1, 1); rig.Pump(1);
            Require(rig.GuestWorld.Backpack.Any(i => (int)i[0] == 2 && (float)((object[])i[2])[0] == 80f) && rig.HostWorld.Box.Items.All(i => (int)i[0] != 2), "Whole item with its durability");
            Require(rig.GuestWorld.PutFromBackpack(rig.Client, 0, 2), "Put not started"); rig.Pump(1);
            Require(rig.HostWorld.Box.Items.Any(i => (int)i[0] == 7 && (int)i[1] == 2) && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 7 && (int)i[1] == 1), "Put");
            Require(StorageRig.Same(start, rig.Totals()) && rig.Changes >= 3, "Items not conserved in a clean exchange");
            Require(rig.Client.Current.Items.Length == rig.HostWorld.Box.Items.Count, "View not refreshed after changes");
            // The host changes the box by itself: the guest sees it.
            rig.HostWorld.Box.Items.Add(Item(4, 2)); rig.Pump(1);
            Require(rig.Client.Current.Items.Any(i => (int)((object[])i)[0] == 4), "Host-side change not shown");
            // 2. Take all over a lossy, duplicating, reordering link; then the reverse.
            int counter = 0;
            rig = new StorageRig();
            rig.Link.Drop = p => ++counter % 3 == 0; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
            for (int i = 1; i <= 15; i++) rig.HostWorld.Box.Items.Add(Item(i, i, i % 2 == 0 ? (object)(float)i : null, null));
            start = rig.Totals();
            rig.Client.Open(BoxTarget()); rig.Pump(4);
            Require(rig.Client.Current != null, "Lossy open");
            rig.Client.TakeAll(); rig.Pump(60);
            Require(rig.HostWorld.Box.Items.Count == 0 && !rig.Client.Busy && StorageRig.Same(start, rig.Totals()), "Lossy take-all lost or duplicated items");
            Require(rig.HostWorld.Takes == 15, "A duplicated request was applied twice: " + rig.HostWorld.Takes);
            while (rig.GuestWorld.Backpack.Count > 0) { rig.GuestWorld.PutFromBackpack(rig.Client, 0, 1000); rig.Pump(8); Require(!rig.Client.Busy, "Lossy put did not finish"); }
            Require(StorageRig.Same(start, rig.Totals()) && rig.HostWorld.Box.Total == start.Values.Sum() && rig.Link.Dropped > 0, "Lossy put-back lost or duplicated items");
            Require(rig.HostWorld.Puts == 15, "A duplicated put was applied twice: " + rig.HostWorld.Puts);
            // 3. Space and permissions: partial put, rejected put, full backpack.
            rig = new StorageRig();
            rig.HostWorld.Box.Capacity = 5; rig.HostWorld.Box.Forbidden = 9; rig.HostWorld.Box.Items.Add(Item(1, 3));
            rig.GuestWorld.Backpack.AddRange(new[] { Item(2, 6), Item(9, 1) });
            start = rig.Totals();
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.GuestWorld.PutFromBackpack(rig.Client, 0, 6); rig.Pump(1);
            Require(rig.HostWorld.Box.Total == 5 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 2 && (int)i[1] == 4) && rig.Client.Message.StartsWith("Поместилось"), "Partial put not returned");
            int forbidden = rig.GuestWorld.Backpack.FindIndex(i => (int)i[0] == 9);
            rig.GuestWorld.PutFromBackpack(rig.Client, forbidden, 1); rig.Pump(1);
            Require(rig.GuestWorld.Backpack.Any(i => (int)i[0] == 9) && rig.Client.Message == LanStorage.Describe(LanStorage.NotAllowed), "Rejected put not returned");
            rig.GuestWorld.Capacity = rig.GuestWorld.Total; rig.Client.Take(0, 1); rig.Pump(1);
            Require(rig.Client.Message == "Рюкзак полон" && rig.HostWorld.Takes == 0, "Take into a full backpack");
            Require(StorageRig.Same(start, rig.Totals()), "Space checks broke conservation");
            // 4. The host does not keep this guest's profile: nothing moves, escrow returns.
            rig = new StorageRig { GuestReady = false };
            rig.HostWorld.Box.Items.Add(Item(1, 1)); rig.GuestWorld.Backpack.Add(Item(2, 2));
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            Require(!rig.Client.IsOpen && rig.Client.Message == LanStorage.Describe(LanStorage.Unavailable) && rig.HostWorld.Resolves == 0, "Unconfirmed guest opened a storage");
            Require(!rig.GuestWorld.PutFromBackpack(rig.Client, 0, 2) && rig.GuestWorld.Total == 2, "Put without an open storage kept the item");
            // 5. Walking away closes the view and drops queued takes.
            rig = new StorageRig();
            for (int i = 1; i <= 5; i++) rig.HostWorld.Box.Items.Add(Item(i, 1));
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.HostWorld.Box.Near = false; rig.Pump(1);
            Require(!rig.Client.IsOpen && rig.Client.Current == null && rig.Client.Message == LanStorage.Describe(LanStorage.TooFar) && !rig.Host.Open, "Storage stayed open far away");
            rig.HostWorld.Box.Near = true; rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.Client.TakeAll(); rig.HostWorld.Box.Near = false; rig.Pump(3);
            Require(rig.HostWorld.Takes <= 1 && !rig.Client.Busy, "Queued takes continued after the storage closed");
            // 6. The connection resets with a put in flight that never reached the host: escrow returns.
            rig = new StorageRig();
            rig.HostWorld.Box.Items.Add(Item(1, 1)); rig.GuestWorld.Backpack.Add(Item(5, 3));
            start = rig.Totals();
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.Link.Drop = p => p.Kind == PacketKind.StorageRequest;
            rig.GuestWorld.PutFromBackpack(rig.Client, 0, 3); rig.Pump(2);
            Require(rig.GuestWorld.Total == 0 && rig.Client.Busy, "Escrow not held while in flight");
            rig.Client.Begin(); rig.Host.Begin();
            Require(StorageRig.Same(start, rig.Totals()) && !rig.Client.Busy, "Escrow not returned on a new session");
            // 7. Switching storages quickly keeps only the last one open on both sides.
            rig = new StorageRig();
            rig.HostWorld.Box.Items.Add(Item(1, 1));
            rig.Client.Open(LanStorage.ScenePayload("Terrain1", 9f, 9f, 9f, "Nowhere")); rig.Client.Open(BoxTarget()); rig.Pump(1.5);
            Require(rig.Client.IsOpen && rig.Client.Current != null && rig.Client.Current.Name == "Storage Box", "Last opened storage not shown");
            rig.Client.Close(); rig.Pump(1);
            Require(!rig.Host.Open && !rig.Client.IsOpen, "Closed storage still open on the host");
            // 8. A view that overtakes the answer to its own open is still shown.
            rig = new StorageRig();
            rig.HostWorld.Box.Items.Add(Item(1, 1));
            rig.Client.Open(BoxTarget()); rig.Client.Tick(0);
            foreach (var p in rig.Link.Take(rig.Link.ToHost)) rig.Host.Receive(p, 0);
            var answers = rig.Link.Take(rig.Link.ToGuest);
            rig.Host.Tick(0.01);
            foreach (var p in rig.Link.Take(rig.Link.ToGuest)) rig.Client.Receive(p);
            Require(rig.Client.Current == null, "View shown before its open was answered");
            foreach (var p in answers) rig.Client.Receive(p);
            Require(rig.Client.Current != null && rig.Client.Current.Items.Length == 1, "Overtaking view lost");
            // 9. On the guest's ledger (0.7.0): takes are handed out on it, puts must come from it.
            rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(7, 3) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.HostWorld.Box.Items.Add(Item(1, 4)); rig.HostWorld.Box.Capacity = 6;
            rig.GuestWorld.Backpack.AddRange(new[] { Item(7, 3), Item(8, 1) }); // 8: not on the ledger
            rig.Client.Open(BoxTarget()); rig.Pump(1);
            rig.Client.Take(0, 4); rig.Pump(1);
            Require(book.Count(1) == 4 && book.PendingCount == 1 && rig.Client.AppliedTx > 0, "Take not handed out on the ledger");
            int forged = rig.GuestWorld.Backpack.FindIndex(i => (int)i[0] == 8);
            rig.GuestWorld.PutFromBackpack(rig.Client, forged, 1); rig.Pump(1);
            Require(rig.HostWorld.Box.Items.All(i => (int)i[0] != 8) && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 8) && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Item off the ledger put into a host storage");
            rig.GuestWorld.PutFromBackpack(rig.Client, rig.GuestWorld.Backpack.FindIndex(i => (int)i[0] == 7), 3); rig.Pump(1);
            Require(book.Count(7) == 0 && rig.HostWorld.Box.Total == 3, "Owned put not taken off the ledger");
            rig.HostWorld.Box.Capacity = 4;
            rig.GuestWorld.PutFromBackpack(rig.Client, rig.GuestWorld.Backpack.FindIndex(i => (int)i[0] == 1), 4); rig.Pump(1);
            Require(rig.HostWorld.Box.Total == 4 && book.Count(1) == 3 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 1 && (int)i[1] == 3), "Partial put not refunded on the ledger");
            WorldItemExchangeChecks();
            Console.WriteLine("PASS: storage exchange (take/put/partial, host changes, lossy+duplicating+reordering link conserves items, space, permissions, unsaved guest, distance, reset escrow, switching, overtaking view, ledger hand-outs and owned puts)");
        }

        // Loose items of the host's world (0.7.0): pick-ups and drops through the host.
        private static void WorldItemExchangeChecks()
        {
            // 1. Clean pick-up, partial pick-up by room, drop; all on the ledger.
            var rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(7, 2) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.GuestWorld.Backpack.Add(Item(7, 2));
            rig.HostWorld.Ground.Add(11, Item(3, 5)); rig.HostWorld.Ground.Add(12, Item(4, 1, 50f, null));
            var start = rig.Totals();
            rig.Client.Pickup(11, 3, 5); rig.Pump(1);
            Require(!rig.HostWorld.Ground.ContainsKey(11) && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 3 && (int)i[1] == 5) && book.Count(3) == 5, "Pick-up not handed out on the ledger");
            rig.GuestWorld.Capacity = rig.GuestWorld.Total; rig.Client.Pickup(12, 4, 1); rig.Pump(1);
            Require(rig.HostWorld.Ground.ContainsKey(12) && rig.Client.Message == "Рюкзак полон" && rig.HostWorld.Pickups == 1, "Pick-up into a full backpack");
            rig.GuestWorld.Capacity = 1000;
            Require(rig.Client.Drop(LanStorage.WithAmount(Item(7, 2), 2), 1f, 2f, 3f), "Drop not started");
            rig.GuestWorld.Backpack.RemoveAll(i => (int)i[0] == 7); rig.Pump(1);
            Require(rig.HostWorld.Ground.Values.Any(i => (int)i[0] == 7 && (int)i[1] == 2) && book.Count(7) == 0, "Drop not taken off the ledger");
            Require(StorageRig.Same(start, rig.Totals()), "Pick-ups and drops broke conservation");
            // 2. Dropping what the ledger does not list: refused, escrow returned.
            Require(rig.Client.Drop(Item(9, 1), 0f, 0f, 0f), "Drop not started");
            rig.Pump(1);
            Require(rig.GuestWorld.Backpack.Any(i => (int)i[0] == 9) && rig.HostWorld.Ground.Values.All(i => (int)i[0] != 9) && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Forged item dropped into the host's world");
            rig.GuestWorld.Backpack.RemoveAll(i => (int)i[0] == 9);
            // 3. Too far: nothing moves, the ledger and the backpack keep the item.
            rig.HostWorld.GroundNear = false;
            rig.GuestWorld.Backpack.RemoveAll(i => (int)i[0] == 3);
            Require(rig.Client.Drop(Item(3, 5), 0f, 0f, 0f), "Drop not started");
            rig.Pump(1);
            Require(book.Count(3) == 5 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 3 && (int)i[1] == 5) && rig.HostWorld.Drops == 1, "Refused drop lost the item");
            rig.Client.Pickup(12, 4, 1); rig.Pump(1);
            Require(rig.HostWorld.Ground.ContainsKey(12) && rig.Client.Message == LanStorage.Describe(LanStorage.TooFar), "Far pick-up");
            rig.HostWorld.GroundNear = true;
            // 4. A wrong item id for a host id, and a vanished item.
            rig.Client.Pickup(12, 99, 1); rig.Client.Pickup(77, 4, 1); rig.Pump(2);
            Require(rig.HostWorld.Ground.ContainsKey(12) && rig.Client.Message == LanStorage.Describe(LanStorage.NotFound), "Pick-up by forged id");
            // 5. Lossy, duplicating, reordering link: alternating pick-ups and drops conserve items.
            int counter = 0;
            rig = new StorageRig();
            rig.Link.Drop = p => ++counter % 3 == 0; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[0], new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            for (int i = 1; i <= 10; i++) rig.HostWorld.Ground.Add(100 + i, Item(i, i));
            start = rig.Totals();
            for (int i = 1; i <= 10; i++) { rig.Client.Pickup(100 + i, i, i); rig.Pump(3); }
            Require(rig.HostWorld.Ground.Count == 0 && rig.HostWorld.Pickups == 10 && StorageRig.Same(start, rig.Totals()), "Lossy pick-ups lost or doubled items");
            while (rig.GuestWorld.Backpack.Count > 0)
            {
                var item = rig.GuestWorld.Backpack[0]; rig.GuestWorld.Backpack.RemoveAt(0);
                Require(rig.Client.Drop(item, 0f, 0f, 0f), "Lossy drop not started"); rig.Pump(6);
                Require(!rig.Client.Busy, "Lossy drop did not finish");
            }
            Require(rig.HostWorld.Drops == 10 && StorageRig.Same(start, rig.Totals()) && book.TopLevelIds().Count == 0, "Lossy drops lost or doubled items");
            // 6. Work on the world: the tool must be on the ledger and wears there; results are handed out.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(2, 1, 50f), Item(92, 2) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.GuestWorld.Backpack.AddRange(new[] { Item(2, 1, 50f), Item(92, 2) });
            Require(rig.Client.Work(LanStorage.WorkHit, 0f, 0f, 0f, 9, 0, null), "Work not started");
            rig.Pump(1);
            Require(rig.HostWorld.Hits == 0 && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Work with a tool off the ledger");
            rig.Now += 1; rig.Client.Work(LanStorage.WorkHit, 0f, 0f, 0f, 2, 0, null); rig.Pump(1);
            Require(rig.HostWorld.Hits == 1 && Math.Abs(book.BestWear(2) - 49f) < .01f && rig.HostWorld.Ground.Values.Any(i => (int)i[0] == 30), "Hit not done or tool not worn");
            rig.Client.Work(LanStorage.WorkHit, 0f, 0f, 0f, 2, 0, null); rig.Client.Work(LanStorage.WorkHit, 0f, 0f, 0f, 2, 0, null); rig.Pump(.2);
            Require(rig.HostWorld.Hits == 2, "Swing flood not throttled: " + rig.HostWorld.Hits);
            rig.Pump(1); rig.Client.Work(LanStorage.WorkGather, 0f, 0f, 0f, 2, 0, null); rig.Pump(1);
            rig.Client.Work(LanStorage.WorkDig, 0f, 0f, 0f, 2, 1, null); rig.Pump(1);
            Require(book.Count(31) == 2 && book.Count(89) == 3 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 31) && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 89), "Gathered or dug items not handed out");
            // Fuel: the empty canister is escrowed; the full one comes back; an empty station returns it.
            var can = rig.GuestWorld.Backpack.First(i => (int)i[0] == 92); can[1] = 1;
            Require(rig.Client.Work(LanStorage.WorkFuel, 0f, 0f, 0f, -1, 0, Item(92, 1)), "Fuel not started"); rig.Pump(1);
            Require(book.Count(92) == 1 && book.Count(8) == 1 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 8) && rig.HostWorld.StationFuel == 10, "Fuel not filled on the ledger");
            rig.HostWorld.StationFuel = 5; rig.GuestWorld.Backpack.RemoveAll(i => (int)i[0] == 92);
            rig.Client.Work(LanStorage.WorkFuel, 0f, 0f, 0f, -1, 0, Item(92, 1)); rig.Pump(1);
            Require(book.Count(92) == 1 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 92) && rig.Client.Message == LanStorage.Describe(LanStorage.Empty), "Empty station kept the canister");
            rig.GuestWorld.Backpack.RemoveAll(i => (int)i[0] == 92);
            rig.Client.Work(LanStorage.WorkFuel, 0f, 0f, 0f, -1, 0, Item(92, 1)); rig.Client.Work(LanStorage.WorkFuel, 0f, 0f, 0f, -1, 0, Item(92, 1));
            rig.Pump(2);
            Require(book.Count(92) >= 0 && book.Count(8) == 1, "Second escrow while busy");
            // 7. Crafting at a host workbench: exact ingredients, paid from the ledger, queued there.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(1, 5), Item(6, 2) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            var bench = LanStorage.TrainPayload(0, LanStorage.TrainFurniture, 5);
            Require(rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 2), Item(6, 1) }), "Craft not started"); rig.Pump(1);
            Require(rig.HostWorld.Queued.Count == 1 && book.Count(1) == 3 && book.Count(6) == 1 && rig.Client.Message == "", "Craft not paid or queued");
            rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 1), Item(6, 1) }); rig.Pump(1);
            Require(rig.HostWorld.Queued.Count == 1 && book.Count(1) == 3 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 1 && (int)i[1] == 1) && rig.Client.Message == LanStorage.Describe(LanStorage.Invalid), "Short escrow accepted");
            rig.GuestWorld.Backpack.Clear();
            rig.Client.Craft(bench, 41, 1, new object[] { Item(1, 2), Item(6, 1) }); rig.Pump(1);
            Require(rig.Client.Message == LanStorage.Describe(LanStorage.Manual) && rig.GuestWorld.Backpack.Count == 2 && book.Count(1) == 3, "Manual workbench craft");
            rig.GuestWorld.Backpack.Clear();
            rig.HostWorld.QueueStatus = LanStorage.Gone;
            rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 2), Item(6, 1) }); rig.Pump(1);
            Require(book.Count(1) == 3 && book.Count(6) == 1 && rig.GuestWorld.Backpack.Count == 2 && rig.HostWorld.Queued.Count == 1, "Failed queue not refunded");
            rig.GuestWorld.Backpack.Clear(); rig.HostWorld.QueueStatus = LanStorage.Ok;
            book.Remove(6, 1);
            rig.Client.Craft(bench, 40, 1, new object[] { Item(1, 2), Item(6, 1) }); rig.Pump(1);
            Require(rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned) && book.Count(1) == 3 && rig.HostWorld.Queued.Count == 1, "Craft with ingredients off the ledger");
            // 8. Refuelling the host's train (0.8.0): the full canister is escrowed, the empty one comes back.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(8, 2) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            int shownLevel = -1; rig.Client.WorkDone = (kind, detail) => { if (kind == LanStorage.WorkRefuel) shownLevel = detail; };
            Require(rig.Client.Work(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, 0, Item(8, 1)), "Refuel not started"); rig.Pump(1);
            Require(rig.HostWorld.TrainFuel == 90f && book.Count(8) == 1 && book.Count(92) == 1 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 92) && shownLevel == 9000, "Refuel not done on the ledger");
            rig.HostWorld.TrainFuel = 100f; rig.Now += 1;
            rig.Client.Work(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, 0, Item(8, 1)); rig.Pump(1);
            Require(book.Count(8) == 1 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 8) && rig.Client.Message == LanStorage.Describe(LanStorage.Full), "Full tank kept the canister");
            rig.GuestWorld.Backpack.Clear(); book.Remove(8, 1); rig.Now += 1; rig.HostWorld.TrainFuel = 0f;
            rig.Client.Work(LanStorage.WorkRefuel, 1f, 2f, 3f, -1, 0, Item(8, 1)); rig.Pump(1);
            Require(rig.HostWorld.TrainFuel == 0f && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Refuel with a canister off the ledger");
            // 9. Building on the host's train (0.8.0): hits with a tool on the ledger; the completing hit pays the recipe.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(2, 1, 50f), Item(1, 5), Item(6, 1) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.GuestWorld.Backpack.AddRange(new[] { Item(1, 5), Item(6, 1) });
            rig.HostWorld.Blueprints[70] = new[] { (float)LanStorage.TrainPart, 0f, 10f };
            rig.HostWorld.Blueprints[71] = new[] { (float)LanStorage.TrainFurniture, 0f, 4f };
            var needs = new[] { new[] { 1, 3 }, new[] { 6, 1 } };
            Require(rig.Client.Build(0, LanStorage.TrainPart, 70, 9, needs), "Build not started"); rig.Pump(1);
            Require(rig.HostWorld.Blueprints[70][1] == 0f && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Build with a tool off the ledger");
            for (int hit = 0; hit < 2; hit++) { rig.Now += 1; rig.Client.Build(0, LanStorage.TrainPart, 70, 2, needs); rig.Pump(1); }
            Require(rig.HostWorld.Blueprints[70][1] == 8f && book.Count(1) == 5 && rig.GuestWorld.Backpack.Sum(i => (int)i[1]) == 6, "Unfinished hits paid something");
            rig.Now += 1; rig.Client.Build(0, LanStorage.TrainPart, 70, 2, needs); rig.Pump(1);
            Require(rig.HostWorld.Completed == 1 && book.Count(1) == 2 && book.Count(6) == 0 && rig.GuestWorld.Backpack.Sum(i => (int)i[1]) == 2 && Math.Abs(book.BestWear(2) - 47f) < .01f,
                "Completing hit did not pay the recipe once (ledger and backpack)");
            rig.HostWorld.Blueprints[72] = new[] { (float)LanStorage.TrainPart, 8f, 10f };
            rig.Now += 1; rig.Client.Build(0, LanStorage.TrainPart, 72, 2, needs); rig.Pump(1);
            Require(rig.HostWorld.Blueprints.ContainsKey(72) && rig.HostWorld.Blueprints[72][1] == 8f && book.Count(1) == 2 && rig.Client.Message == LanStorage.Describe(LanStorage.Missing), "Built without the recipe on the ledger");
            rig.Now += 1; rig.Client.Build(0, LanStorage.TrainFurniture, 71, 2, null); rig.Pump(1);
            Require(rig.HostWorld.Completed == 2 && book.Count(1) == 2, "Furniture assembly not done or not free");
            rig.Now += 1; rig.Client.Build(0, LanStorage.TrainPart, 99, 2, needs); rig.Pump(1);
            Require(rig.Client.Message == LanStorage.Describe(LanStorage.NotFound), "Unknown blueprint");
            // 10. Placing blueprints (0.8.1): free, one per place, throttled, refused without the guest's profile.
            rig = new StorageRig();
            var spot = new[] { 1f, 0f, 2f }; var pose = new[] { 1f, 0f, 2f, 0f, 0f, 0f, 1f };
            Require(rig.Client.Place(200, 0, 3, spot, pose), "Placement not started"); rig.Pump(1);
            Require(rig.HostWorld.Placed == 1 && rig.Client.Message == "Чертёж поставлен у хоста", "Blueprint not placed");
            rig.Client.Place(200, 0, 3, spot, pose); rig.Pump(1);
            Require(rig.HostWorld.Placed == 1 && rig.Client.Message == LanStorage.Describe(LanStorage.Occupied), "Taken point placed twice");
            rig.Client.Place(201, 1, -1, new[] { 0f, 0f, 0f }, pose); rig.Pump(1);
            Require(rig.HostWorld.Placed == 2, "Surface placement");
            rig.Client.Place(200, 0, -1, new[] { 0f, 0f, 0f }, pose); rig.Pump(1);
            Require(rig.HostWorld.Placed == 2 && rig.Client.Message == LanStorage.Describe(LanStorage.NotAllowed), "Wall placed on a surface");
            for (int i = 0; i < 6; i++) rig.Client.Place(200, 0, 3, new[] { 5f + i, 0f, 0f }, pose);
            rig.Pump(.3);
            Require(rig.HostWorld.Placed <= 3, "Placement flood not throttled: " + rig.HostWorld.Placed);
            rig = new StorageRig { GuestReady = false };
            rig.Client.Place(200, 0, 3, spot, pose); rig.Pump(1);
            Require(rig.HostWorld.Placed == 0 && rig.Client.Message == LanStorage.Describe(LanStorage.Unavailable), "Placement without the guest's profile");
            // 11. Train controls (0.10.0): presses, levers, a repair paid with a spare lever from the ledger.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(66, 1) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            Require(rig.Client.Control(LanStorage.ControlButton, 0, 1, 0, null), "Control not started"); rig.Pump(1);
            Require(rig.HostWorld.Presses == 0 && rig.Client.Message == LanStorage.Describe(LanStorage.Broken), "Broken button pressed");
            rig.Client.Control(LanStorage.ControlRepair, 0, 1, 0, Item(66, 1)); rig.Pump(1);
            Require(!rig.HostWorld.Broken && book.Count(66) == 0 && rig.GuestWorld.Backpack.Count == 0 && rig.Client.Message == "Починено", "Repair not paid with the spare lever");
            rig.Client.Control(LanStorage.ControlRepair, 0, 1, 0, Item(66, 1)); rig.Pump(1);
            Require(rig.GuestWorld.Backpack.Any(i => (int)i[0] == 66) && rig.Client.Message == LanStorage.Describe(LanStorage.NotOwned), "Repair with a lever off the ledger");
            rig.GuestWorld.Backpack.Clear(); book.Reset(LedgerProfile(new object[] { Item(66, 1) }, new object[0], new int[0]), 0);
            rig.Client.Control(LanStorage.ControlRepair, 0, 1, 0, Item(66, 1)); rig.Pump(1);
            Require(book.Count(66) == 1 && rig.GuestWorld.Backpack.Any(i => (int)i[0] == 66) && rig.Client.Message == LanStorage.Describe(LanStorage.NotAllowed), "Repair of a working button kept the lever");
            rig.Client.Control(LanStorage.ControlButton, 0, 1, 0, null); rig.Client.Control(LanStorage.ControlLever, 1, 1, 650, null); rig.Pump(1);
            Require(rig.HostWorld.Presses == 1 && Math.Abs(rig.HostWorld.Levers[1] - .65f) < 1e-6, "Press and lever");
            for (int i = 0; i < 6; i++) rig.Client.Control(LanStorage.ControlButton, 1, 1, 0, null);
            Require(rig.Client.PendingRequests <= 3, "Control requests not bounded");
            rig.Pump(2);
            rig.HostWorld.Allows = false;
            rig.Client.Control(LanStorage.ControlButton, 1, 1, 0, null); rig.Pump(1);
            Require(rig.Client.Message == LanStorage.Describe(LanStorage.Locked), "Control without the host's permission");
            // 1.1.6: a train door needs no driving permission; the answer moves the guest's copy, a refusal says why.
            byte[] doorDone = null; rig.Client.DoorDone = payload => doorDone = payload;
            int doorValue = LanStorage.DoorValue(LanStorage.DoorPart, true, 1234);
            rig.Client.Control(LanStorage.ControlDoor, 2, 77, doorValue, null); rig.Pump(1);
            Require(doorDone != null && rig.HostWorld.DoorRequests == 1, "Door request without the driving permission refused");
            int dk, di, dc, dv; object[] de;
            LanStorage.DecodeControl(doorDone, out dk, out di, out dc, out dv, out de);
            Require(dk == LanStorage.ControlDoor && di == 2 && dc == 77 && dv == doorValue && rig.Client.Message == "", "Door answer payload");
            doorDone = null; rig.HostWorld.DoorAnswer = LanStorage.TooFar;
            rig.Client.Control(LanStorage.ControlDoor, 2, 77, doorValue, null); rig.Pump(1);
            Require(doorDone == null && rig.Client.Message == LanStorage.DescribeDoor(LanStorage.TooFar), "Refused door moved, or a storage message for a door");
            Console.WriteLine("PASS: world items through the host (pick-up, room, drop, forged drop, distance, forged ids, lossy link conserves items and the ledger; work: tool on the ledger and worn, throttle, gathering, digging, fuel escrow; crafting at host workbenches; refuelling the host's train; building host blueprints paid from the ledger; placing blueprints; train controls, levers and repairs with a spare lever; train doors)");
        }

        private static void StorageHostileChecks()
        {
            var rig = new StorageRig();
            rig.HostWorld.Box.Items.AddRange(new[] { Item(1, 5), Item(2, 1, 30f, null) });
            var send = rig.Link.Sender(rig.Link.ToHost);
            double now = 100; uint tx = 1;
            Func<byte, int, byte[], Packet> request = (op, h, payload) => new Packet { Kind = PacketKind.StorageRequest, Sequence = tx++, Action = op, Revision = h, Chunk = payload };
            // Requests before an open, with forged handles, and garbage payloads.
            rig.Host.Receive(request(LanProtocol.StorageTake, 1, LanStorage.TakePayload(0, 5, LanStorage.Fingerprint(Item(1, 5)))), now);
            rig.Host.Receive(request(LanProtocol.StoragePut, 1, LanStorage.EncodeItem(Item(3, 1))), now);
            Require(rig.HostWorld.Takes == 0 && rig.HostWorld.Puts == 0, "Request without an open storage applied");
            rig.Host.Receive(request(LanProtocol.StorageOpen, 0, LanStorage.ScenePayload("Terrain1", 0f, 0f, 0f, "Other")), now);
            now += 1;
            rig.Host.Receive(request(LanProtocol.StorageOpen, 0, BoxTarget()), now);
            Require(rig.Host.Open, "Valid open refused");
            // Two opens within half a second: the second is refused before any search.
            int resolves = rig.HostWorld.Resolves;
            rig.Host.Receive(request(LanProtocol.StorageOpen, 0, BoxTarget()), now + 0.1);
            Require(rig.HostWorld.Resolves == resolves, "Open requests not throttled");
            int handle = 1;
            rig.Host.Receive(request(LanProtocol.StorageTake, handle + 7, LanStorage.TakePayload(0, 5, LanStorage.Fingerprint(Item(1, 5)))), now);
            rig.Host.Receive(request(LanProtocol.StorageTake, handle, LanStorage.TakePayload(0, 5, LanStorage.Fingerprint(Item(9, 5)))), now);
            Require(rig.HostWorld.Takes == 0, "Forged handle or fingerprint took an item");
            // Replayed and older sequence numbers are answered from memory, never reapplied.
            var good = request(LanProtocol.StorageTake, handle, LanStorage.TakePayload(0, 2, LanStorage.Fingerprint(Item(1, 5))));
            rig.Host.Receive(good, now); rig.Host.Receive(good, now); rig.Host.Receive(good, now);
            var old = new Packet { Kind = PacketKind.StorageRequest, Sequence = 1, Action = LanProtocol.StorageTake, Revision = handle, Chunk = good.Chunk };
            rig.Host.Receive(old, now);
            Require(rig.HostWorld.Takes == 1 && (int)rig.HostWorld.Box.Items[0][1] == 3, "Replayed take applied again");
            // Random payloads for every operation never throw out of the host.
            var random = new Random(9001);
            var seeds = new[] { BoxTarget(), good.Chunk, LanStorage.EncodeItem(Item(4, 1, 2f, 3f)) };
            for (int i = 0; i < 5000; i++)
            {
                var bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
                for (int j = 0; j < 1 + random.Next(4); j++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                if (random.Next(4) == 0) Array.Resize(ref bytes, random.Next(1, bytes.Length));
                byte op = (byte)(2 + random.Next(2));
                rig.Host.Receive(request(op, handle, bytes), now);
            }
            Require(rig.Log.Any(l => l.StartsWith("Storage")), "Hostile payloads not reported");
            Console.WriteLine("PASS: storage host against hostile requests (no open, forged handle/fingerprint, throttled opens, replays, 5000 random payloads)");
        }
    }
}
