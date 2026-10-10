using System;
using System.Collections.Generic;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.2.0 (schema 28): the host's quests reach the guest; the guest accepts and hands in quests
    // through the host (storage request 14); door frames carry the lock, door toggles may unlock.
    internal static partial class Program
    {
        // The host's quests as LanQuests answers for them.
        private sealed class FakeQuestWorld : ILanQuestWorld
        {
            internal sealed class Q { internal bool Accepted, Ready, Completed, Shared; internal object[] Gifts = new object[0]; internal int[] Need; internal object[] Rewards = new object[0]; internal int Completions, Accepts, Shares; }
            internal readonly Dictionary<uint, Q> Quests = new Dictionary<uint, Q>();
            internal string Scene = "Location8";
            public byte Accept(uint key, string scene, bool room, out object[] gifts)
            {
                gifts = new object[0];
                Q q;
                if (scene != Scene) return LanStorage.Gone;
                if (!Quests.TryGetValue(key, out q)) return LanStorage.NotFound;
                if (q.Accepted || q.Completed) return LanStorage.Ok;
                if (q.Gifts.Length > 0 && !room) return LanStorage.TooLarge;
                q.Accepted = true; q.Accepts++; gifts = q.Gifts; q.Gifts = new object[0];
                return LanStorage.Ok;
            }
            public byte Prepare(uint key, string scene, out int[] need, out object[] rewards)
            {
                need = null; rewards = new object[0];
                Q q;
                if (scene != Scene) return LanStorage.Gone;
                if (!Quests.TryGetValue(key, out q)) return LanStorage.NotFound;
                if (!q.Accepted || !q.Ready || q.Completed) return LanStorage.NotReady;
                need = q.Need; rewards = q.Rewards;
                return LanStorage.Ok;
            }
            public void Complete(uint key, string scene) { var q = Quests[key]; q.Completed = true; q.Completions++; }
            // 1.4.5: the reward of a quest the host handed in, kept for the guest, once.
            public byte Share(uint key, string scene, bool room, out object[] rewards)
            {
                rewards = new object[0];
                Q q;
                if (scene != Scene) return LanStorage.Gone;
                if (!Quests.TryGetValue(key, out q)) return LanStorage.NotFound;
                if (!q.Completed || !q.Shared) return LanStorage.NotReady;
                if (q.Rewards.Length > 0 && !room) return LanStorage.TooLarge;
                q.Shared = false; q.Shares++; rewards = q.Rewards;
                return LanStorage.Ok;
            }
        }

        private static void QuestChecks()
        {
            QuestProtocolChecks();
            QuestRequestChecks();
            Console.WriteLine("PASS: quests (state frames and limits, door locks and key unlocks, request shape, accept with gifts once, hand-in only when ready, cost all-or-nothing from the ledger, rewards once over a lossy link, malformed requests, 1.4.5 shared rewards once and only when kept for the guest)");
        }

        private static void QuestProtocolChecks()
        {
            Packet decoded = null;
            Func<Packet, Packet> world = p => { p.GameVersion = "checks"; p.Session = 42; p.WorldEpoch = 3; p.Sequence = 9; p.Scene = new string('S', 96); return p; };
            var quests = world(new Packet { Kind = PacketKind.QuestStates, Quests = Enumerable.Range(0, LanProtocol.MaxQuestBatch).Select(i =>
                new QuestFrame { Key = (uint)(1000 + i * 7919), Flags = (byte)(i % 3 == 0 ? 0 : LanProtocol.QuestAccepted | (i % 2 == 0 ? LanProtocol.QuestReady : 0) | (i % 5 == 0 ? LanProtocol.QuestCompleted : 0)),
                    Stage = (sbyte)(i % 3 == 0 ? -1 : i % LanProtocol.MaxQuestStage) }).ToArray() });
            var bytes = LanProtocol.Encode(quests);
            Require(bytes.Length <= 1200 && LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == PacketKind.QuestStates && decoded.Quests.Length == LanProtocol.MaxQuestBatch, "Quest states rejected");
            Require(decoded.Quests[4].Key == quests.Quests[4].Key && decoded.Quests[4].Flags == quests.Quests[4].Flags && decoded.Quests[4].Stage == quests.Quests[4].Stage && decoded.Quests[0].Stage == -1, "Quest frame fields");
            for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated quest states accepted"); }
            var empty = world(new Packet { Kind = PacketKind.QuestStates, Quests = new QuestFrame[0] });
            Require(LanProtocol.TryDecode(LanProtocol.Encode(empty), out decoded) && decoded.Quests.Length == 0, "Empty quest states rejected");
            Func<Action<Packet>, Packet, bool> rejected = (change, source) =>
            {
                Packet copy; LanProtocol.TryDecode(LanProtocol.Encode(source), out copy);
                copy.GameVersion = "checks"; change(copy);
                try { return !LanProtocol.TryDecode(LanProtocol.Encode(copy), out decoded); }
                catch (System.IO.InvalidDataException) { return true; }
            };
            var toggle = world(new Packet { Kind = PacketKind.DoorToggle, X = 10, Y = 0.5f, Z = -3, Action = LanProtocol.DoorUnlock, Revision = 37 });
            var doors = world(new Packet { Kind = PacketKind.DoorStates, Doors = new[] { new DoorFrame { X = 1, Y = 2, Z = 3, Flags = LanProtocol.MaxDoorFlags } } });
            var bad = new[]
            {
                rejected(p => p.Quests[1].Key = 0, quests), rejected(p => p.Quests[1].Flags = LanProtocol.MaxQuestFlags + 1, quests),
                rejected(p => p.Quests[1].Stage = -2, quests), rejected(p => p.Quests[1].Stage = LanProtocol.MaxQuestStage + 1, quests),
                rejected(p => p.Quests[0].Flags = LanProtocol.QuestCompleted, quests), rejected(p => p.Quests[0].Flags = LanProtocol.QuestReady, quests),
                rejected(p => p.Quests[0].Stage = 0, quests), rejected(p => p.Quests[2].Key = p.Quests[1].Key, quests),
                rejected(p => p.Quests[1].Flags = LanProtocol.QuestAccepted | LanProtocol.QuestShared, quests), rejected(p => p.Quests[0].Flags = LanProtocol.QuestShared, quests),
                rejected(p => p.Quests = new QuestFrame[LanProtocol.MaxQuestBatch + 1].Select((_, i) => new QuestFrame { Key = (uint)i + 1, Stage = -1 }).ToArray(), quests),
                rejected(p => p.WorldEpoch = 0, quests), rejected(p => p.Sequence = 0, quests), rejected(p => p.Scene = "", quests),
                rejected(p => p.Revision = -1, toggle), rejected(p => p.Revision = LanProtocol.MaxKeyItem + 1, toggle), rejected(p => p.Action = LanProtocol.DoorUnlock + 1, toggle),
                rejected(p => { p.Action = 1; p.Revision = 37; }, toggle), rejected(p => { p.Action = 0; p.Revision = 1; }, toggle),
                rejected(p => p.Doors[0].Flags = LanProtocol.MaxDoorFlags + 1, doors),
            };
            Require(bad.All(x => x), "Malformed quest or door packet accepted: #" + Array.IndexOf(bad, false));
            Require(LanProtocol.TryDecode(LanProtocol.Encode(toggle), out decoded) && decoded.Action == LanProtocol.DoorUnlock && decoded.Revision == 37, "Door unlock rejected");
            // 1.4.5: a reward kept for the guest only on a quest handed in.
            var shared = world(new Packet { Kind = PacketKind.QuestStates, Quests = new[] { new QuestFrame { Key = 5, Flags = LanProtocol.QuestAccepted | LanProtocol.QuestCompleted | LanProtocol.QuestShared, Stage = 2 } } });
            Require(LanProtocol.TryDecode(LanProtocol.Encode(shared), out decoded) && decoded.Quests[0].Flags == (LanProtocol.QuestAccepted | LanProtocol.QuestCompleted | LanProtocol.QuestShared), "Shared quest reward frame rejected");
            Require(LanProtocol.TryDecode(LanProtocol.Encode(doors), out decoded) && decoded.Doors[0].Flags == LanProtocol.MaxDoorFlags, "Locked door frame rejected");
            toggle.Action = 1; toggle.Revision = 0;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(toggle), out decoded) && decoded.Action == 1 && decoded.Revision == 0, "Door toggle rejected");
            Require(!LanNetworkPolicy.Allowed(true, PacketKind.QuestStates, true) && LanNetworkPolicy.Allowed(false, PacketKind.QuestStates, true) &&
                !LanNetworkPolicy.Allowed(false, PacketKind.QuestStates, false) && LanProtocol.WorldBound(PacketKind.QuestStates), "Quest states direction");
            var limits = new LanMessageLimits(); int sent = 0;
            for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.QuestStates, 0)) sent++;
            Require(sent == 8, "Quest state bursts not bounded");

            // Request shape.
            uint key; string scene;
            Require(LanStorage.DecodeQuest(LanStorage.QuestPayload(LanStorage.QuestComplete, 0xFEDCBA98u, "Location8"), out key, out scene) == LanStorage.QuestComplete &&
                key == 0xFEDCBA98u && scene == "Location8", "Quest request roundtrip");
            Require(LanStorage.DecodeQuest(LanStorage.QuestPayload(LanStorage.QuestShare, 7u, "Location8"), out key, out scene) == LanStorage.QuestShare && key == 7u, "Shared reward request roundtrip");
            foreach (var payload in new[] {
                new object[] { 4, 5, "Location8" }, new object[] { 0, 5, "Location8" }, new object[] { 1, 0, "Location8" }, new object[] { 1, 5, "" },
                new object[] { 1, 5, new string('s', 97) }, new object[] { 1, 5 }, new object[] { 1, 5, "a", 1 }, new object[] { 1f, 5, "a" }, new object[] { 1, "5", "a" } })
                ExpectRejected(() => LanStorage.DecodeQuest(LanValueCodec.Encode(payload), out key, out scene), "Invalid quest request accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageQuest, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageQuest, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageQuest, 0, 0), "Quest request shape");
            var items = LanStorage.DecodeQuestItems(LanStorage.QuestItemsPayload(new object[] { Item(219, 1), Item(16, 6) }));
            Require(items.Length == 2 && (int)((object[])items[1])[0] == 16 && (int)((object[])items[1])[1] == 6, "Quest items roundtrip");
            ExpectRejected(() => LanStorage.QuestItemsPayload(Enumerable.Range(0, LanStorage.MaxQuestItems + 1).Select(i => (object)Item(i, 1)).ToArray()), "Too many quest items accepted");
            ExpectRejected(() => LanStorage.DecodeQuestItems(LanValueCodec.Encode(new object[0])), "Empty quest items accepted");
            ExpectRejected(() => LanStorage.DecodeQuestItems(LanValueCodec.Encode(new object[] { 5 })), "Malformed quest item accepted");
        }

        private static void QuestRequestChecks()
        {
            const uint Supplies = 0x1234u, Church = 0x5678u, Gate = 0x9ABCu;
            Func<StorageRig, FakeQuestWorld, LanGuestLedger, List<object[]>> attach = (r, w, b) =>
            {
                var list = new List<object[]>();
                r.Host.Quests = w; r.Host.Ledger = () => b;
                r.Client.QuestDone = (kind, k, status, received) => list.Add(new object[] { kind, k, status, received });
                return list;
            };
            // 1. Accept with a gift: the gift reaches the backpack and the ledger once; a second accept gives nothing.
            var rig = new StorageRig();
            var quests = new FakeQuestWorld();
            quests.Quests[Supplies] = new FakeQuestWorld.Q { Need = new[] { 247, 3 }, Rewards = new object[] { Item(230, 1), Item(16, 6) }, Gifts = new object[] { Item(37, 1, 5, null) } };
            quests.Quests[Church] = new FakeQuestWorld.Q();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(247, 2) }, new object[] { Item(247, 2) }, new int[0]), 0);
            var answers = attach(rig, quests, book);
            int changes = 0; rig.Host.QuestChanged = (kind, k) => changes++;
            Require(rig.Client.Quest(LanStorage.QuestAccept, Supplies, "Location8"), "Quest accept not queued");
            rig.Pump(1);
            Require(answers.Count == 1 && (byte)answers[0][2] == LanStorage.Ok && (int)answers[0][3] == 1 && quests.Quests[Supplies].Accepted, "Quest accept not answered with its gift");
            Require(book.Count(37) == 1 && rig.GuestWorld.Backpack.Count(i => (int)i[0] == 37) == 1 && changes == 1, "Quest gift not on the ledger and in the backpack once");
            rig.Client.Quest(LanStorage.QuestAccept, Supplies, "Location8"); rig.Pump(1);
            Require(answers.Count == 2 && (byte)answers[1][2] == LanStorage.Ok && (int)answers[1][3] == 0 && book.Count(37) == 1 && quests.Quests[Supplies].Accepts == 1, "Second accept gave the gift again");
            // 2. Hand in before it is ready: refused, nothing taken.
            rig.Client.Quest(LanStorage.QuestComplete, Supplies, "Location8"); rig.Pump(1);
            Require(answers.Count == 3 && (byte)answers[2][2] == LanStorage.NotReady && book.Count(247) == 4 && !quests.Quests[Supplies].Completed, "Hand-in before ready accepted");
            // 3. Ready: the cost comes off the ledger (across inventories), rewards go on and into the backpack, once.
            quests.Quests[Supplies].Ready = true;
            rig.Client.Quest(LanStorage.QuestComplete, Supplies, "Location8"); rig.Pump(1);
            Require(answers.Count == 4 && (byte)answers[3][2] == LanStorage.Ok && (int)answers[3][3] == 2 && quests.Quests[Supplies].Completions == 1, "Ready hand-in not done");
            Require(book.Count(247) == 1 && book.Count(230) == 1 && book.Count(16) == 6 && rig.GuestWorld.Backpack.Count(i => (int)i[0] == 230) == 1, "Hand-in cost or rewards wrong");
            rig.Client.Quest(LanStorage.QuestComplete, Supplies, "Location8"); rig.Pump(1);
            Require(answers.Count == 5 && (byte)answers[4][2] == LanStorage.NotReady && quests.Quests[Supplies].Completions == 1 && book.Count(230) == 1, "Quest handed in twice");
            // 4. Not enough by the ledger: refused, nothing taken, not completed.
            quests.Quests[Gate] = new FakeQuestWorld.Q { Accepted = true, Ready = true, Need = new[] { 247, 2 }, Rewards = new object[] { Item(252, 1) } };
            rig.Client.Quest(LanStorage.QuestComplete, Gate, "Location8"); rig.Pump(1);
            Require(answers.Count == 6 && (byte)answers[5][2] == LanStorage.NotOwned && book.Count(247) == 1 && book.Count(252) == 0 && !quests.Quests[Gate].Completed, "Hand-in without the items accepted");
            // 5. Another scene, an unknown quest, no ledger, no quests: refused.
            rig.Client.Quest(LanStorage.QuestAccept, Church, "Location9"); rig.Pump(1);
            Require((byte)answers[6][2] == LanStorage.Gone && !quests.Quests[Church].Accepted, "Quest of another scene accepted");
            rig.Client.Quest(LanStorage.QuestAccept, 0x4444u, "Location8"); rig.Pump(1);
            Require((byte)answers[7][2] == LanStorage.NotFound, "Unknown quest accepted");
            rig.Host.Ledger = () => null;
            rig.Client.Quest(LanStorage.QuestAccept, Church, "Location8"); rig.Pump(1);
            Require((byte)answers[8][2] == LanStorage.Unavailable && !quests.Quests[Church].Accepted, "Quest accepted without a ledger");
            rig.Host.Ledger = () => book; rig.Host.Quests = null;
            rig.Client.Quest(LanStorage.QuestAccept, Church, "Location8"); rig.Pump(1);
            Require((byte)answers[9][2] == LanStorage.NotAllowed && !quests.Quests[Church].Accepted, "Quest accepted without quest support");
            // 5b. 1.4.5: the reward of a quest the host handed in: only when kept for this guest, once,
            // and kept while the guest's ledger has no room.
            rig = new StorageRig();
            quests = new FakeQuestWorld();
            quests.Quests[Gate] = new FakeQuestWorld.Q { Accepted = true, Completed = true, Shared = true, Rewards = new object[] { Item(252, 1), Item(16, 2) } };
            quests.Quests[Church] = new FakeQuestWorld.Q { Accepted = true, Completed = true };
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[0], new object[0], new int[0]), 0);
            answers = attach(rig, quests, book);
            rig.Client.Quest(LanStorage.QuestShare, Church, "Location8"); rig.Pump(1);
            Require(answers.Count == 1 && (byte)answers[0][2] == LanStorage.NotReady && book.Count(252) == 0, "A reward nobody kept for the guest was given");
            rig.Client.Quest(LanStorage.QuestShare, Gate, "Location8"); rig.Pump(1);
            Require(answers.Count == 2 && (byte)answers[1][2] == LanStorage.Ok && (int)answers[1][3] == 2 && book.Count(252) == 1 && book.Count(16) == 2 &&
                rig.GuestWorld.Backpack.Count(i => (int)i[0] == 252) == 1, "Shared quest reward not given");
            rig.Client.Quest(LanStorage.QuestShare, Gate, "Location8"); rig.Pump(1);
            Require(answers.Count == 3 && (byte)answers[2][2] == LanStorage.NotReady && book.Count(252) == 1 && quests.Quests[Gate].Shares == 1, "Shared quest reward given twice");
            rig.Client.Quest(LanStorage.QuestShare, Gate, "Location9"); rig.Pump(1);
            Require((byte)answers[3][2] == LanStorage.Gone, "Shared reward of another scene given");
            // 6. Lossy, duplicating, reordering link: one accept, one hand-in, every item once.
            for (int round = 0; round < 20; round++)
            {
                rig = new StorageRig();
                var lossy = new Random(round); rig.Link.Drop = pk => lossy.Next(10) < 3; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
                quests = new FakeQuestWorld();
                quests.Quests[Supplies] = new FakeQuestWorld.Q { Ready = true, Need = new[] { 247, 3 }, Rewards = new object[] { Item(230, 1), Item(13, 10) }, Gifts = new object[] { Item(37, 1, 5, null) } };
                book = new LanGuestLedger(new CountingRules());
                book.Reset(LedgerProfile(new object[] { Item(247, 3) }, new object[0], new int[0]), 0);
                rig.GuestWorld.Backpack.Add(Item(247, 3));
                answers = attach(rig, quests, book);
                rig.Client.Quest(LanStorage.QuestAccept, Supplies, "Location8");
                rig.Client.Quest(LanStorage.QuestComplete, Supplies, "Location8");
                rig.Pump(30);
                Require(answers.Count == 2 && answers.All(a => (byte)a[2] == LanStorage.Ok) && quests.Quests[Supplies].Accepts == 1 && quests.Quests[Supplies].Completions == 1,
                    "Lossy link: quest answers " + answers.Count + ", accepts " + quests.Quests[Supplies].Accepts + ", completions " + quests.Quests[Supplies].Completions);
                Require(book.Count(37) == 1 && book.Count(230) == 1 && book.Count(13) == 10 && book.Count(247) == 0, "Lossy link: ledger after the quest");
                Require(rig.GuestWorld.Backpack.Where(i => (int)i[0] == 13).Sum(i => (int)i[1]) == 10 && rig.GuestWorld.Backpack.Count(i => (int)i[0] == 37) == 1, "Lossy link: quest items received more than once");
            }
            // 7. Hostile requests: bad payloads change nothing.
            rig = new StorageRig();
            quests = new FakeQuestWorld();
            quests.Quests[Supplies] = new FakeQuestWorld.Q { Accepted = true, Ready = true, Need = new[] { 247, 1 }, Rewards = new object[] { Item(230, 1) } };
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(247, 3) }, new object[0], new int[0]), 0);
            attach(rig, quests, book);
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { 2, 0x1234, "Location8", 1 }), new byte[] { 1, 2, 3 }, LanValueCodec.Encode(new object[] { 2, 0, "Location8" }),
                LanValueCodec.Encode(new object[] { 9, 0x1234, "Location8" }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageQuest, Chunk = payload }, rig.Now += 1);
            Require(book.Count(247) == 3 && book.Count(230) == 0 && !quests.Quests[Supplies].Completed, "A malformed quest request changed something");
        }
    }
}
