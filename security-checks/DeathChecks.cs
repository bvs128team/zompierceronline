using System;
using System.Collections.Generic;
using System.Linq;

namespace ZompiercerLAN
{
    // Guest death (0.12.0): the request, the host moving the whole ledger into bags,
    // refusals and refunds, repeats over a lossy link, and the next state upload.
    internal static partial class Program
    {
        private static void DeathChecks()
        {
            // Wire shape.
            float x, y, z; int car;
            LanStorage.DecodeDeath(LanStorage.DeathPayload(1.5f, -2f, 3f, -1), out x, out y, out z, out car);
            Require(x == 1.5f && y == -2f && z == 3f && car == -1, "Death roundtrip");
            LanStorage.DecodeDeath(LanStorage.DeathPayload(-255f, 1f, 255f, 7), out x, out y, out z, out car);
            Require(car == 7, "Death on a car rejected");
            foreach (var bad in new[] {
                new object[] { 1f, 2f, 3f, 8 }, new object[] { 1f, 2f, 3f, -2 }, new object[] { 257f, 0f, 0f, 0 },
                new object[] { 100000f, 0f, 0f, -1 }, new object[] { 1f, 2f, 3f }, new object[] { 1f, 2f, 3, -1 },
                new object[] { 1f, 2f, 3f, -1, 0 }, new object[] { float.NaN, 0f, 0f, -1 } })
                ExpectRejected(() => LanStorage.DecodeDeath(LanValueCodec.Encode(bad), out x, out y, out z, out car), "Invalid death accepted");
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageDeath, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageDeath, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageDeath, 0, 0), "Death request shape");

            // 1. Everything on the ledger, including a hand-out still on its way, goes into the bags once.
            var rig = new StorageRig();
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(7, 3), Item(9, 1, 55f, null) }, new object[] { Item(1, 2) }, new[] { 4 }), 0);
            book.Give(LanGuestLedger.RequestChannel, 9, Item(3, 5));
            rig.Host.Ledger = () => book;
            var answers = new List<KeyValuePair<byte, int>>();
            rig.Client.DeathDone = (status, stacks) => answers.Add(new KeyValuePair<byte, int>(status, stacks));
            Require(rig.Client.Death(1f, 0f, 2f, -1), "Death not queued");
            rig.Pump(1);
            Require(answers.Count == 1 && answers[0].Key == LanStorage.Ok && answers[0].Value == 4, "Death not answered with the stacks left");
            Require(rig.HostWorld.DeathItems.Count == 4 && rig.HostWorld.Deaths == 1, "Not every stack went into the bags");
            Require(new[] { 7, 9, 1, 3 }.All(id => book.Count(id) == 0) && book.PendingCount == 0, "The ledger kept something after the death");
            Require(book.Magazine(0) == 4, "Loaded rounds changed by the death");
            // An upload captured before the death cannot bring anything back.
            var fixes = new List<LedgerFix>();
            var kept = book.Reconcile(LedgerProfile(new object[] { Item(7, 3), Item(9, 1, 55f, null), Item(3, 5) }, new object[] { Item(1, 2) }, new[] { 4 }), new uint[] { 0, 0 }, fixes);
            var inventories = (object[])((object[])kept[12])[0];
            Require(fixes.Count > 0 && inventories.All(i => ((object[])i).Length == 0), "A state from before the death restored items");
            // A second death within 5 s: Busy; later: Ok with nothing left.
            rig.Client.Death(1f, 0f, 2f, -1); rig.Pump(1);
            Require(answers.Count == 2 && answers[1].Key == LanStorage.Busy && rig.HostWorld.Deaths == 1, "Second death within 5 s not refused");
            rig.Pump(5); rig.Client.Death(1f, 0f, 2f, -1); rig.Pump(1);
            Require(answers.Count == 3 && answers[2].Key == LanStorage.Ok && answers[2].Value == 0 && rig.HostWorld.Deaths == 1, "Empty death not answered");

            // 2. The world refuses: everything is refunded to the ledger.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(7, 3) }, new object[] { Item(1, 2) }, new int[0]), 0);
            rig.Host.Ledger = () => book;
            rig.HostWorld.DeathStatus = LanStorage.Unavailable;
            answers.Clear(); rig.Client.DeathDone = (status, stacks) => answers.Add(new KeyValuePair<byte, int>(status, stacks));
            rig.Client.Death(0f, 0f, 0f, -1); rig.Pump(1);
            Require(answers.Count == 1 && answers[0].Key == LanStorage.Unavailable && book.Count(7) == 3 && book.Count(1) == 2, "Refused death lost items");

            // 3. No profile on the host: nothing happens.
            rig = new StorageRig();
            rig.Host.Ledger = () => null;
            answers.Clear(); rig.Client.DeathDone = (status, stacks) => answers.Add(new KeyValuePair<byte, int>(status, stacks));
            rig.Client.Death(0f, 0f, 0f, -1); rig.Pump(1);
            Require(answers.Count == 1 && answers[0].Key == LanStorage.Unavailable && rig.HostWorld.Deaths == 0, "Death without a ledger accepted");

            // 4. Lossy, duplicating, reordering link: one death, every stack exactly once, one answer.
            for (int round = 0; round < 20; round++)
            {
                rig = new StorageRig();
                var lossy = new Random(round); rig.Link.Drop = pk => lossy.Next(10) < 3; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
                book = new LanGuestLedger(new CountingRules());
                book.Reset(LedgerProfile(new object[] { Item(7, 3), Item(8, 1) }, new object[] { Item(1, 2) }, new int[0]), 0);
                rig.Host.Ledger = () => book;
                answers.Clear(); rig.Client.DeathDone = (status, stacks) => answers.Add(new KeyValuePair<byte, int>(status, stacks));
                rig.Client.Death(0f, 0f, 0f, -1); rig.Pump(30);
                Require(rig.HostWorld.Deaths == 1 && rig.HostWorld.DeathItems.Count == 3 && answers.Count == 1 && answers[0].Key == LanStorage.Ok,
                    "Lossy link: death applied " + rig.HostWorld.Deaths + " times, " + answers.Count + " answers");
            }

            // 5. Hostile requests: bad payloads change nothing.
            rig = new StorageRig();
            book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(7, 3) }, new object[0], new int[0]), 0);
            rig.Host.Ledger = () => book;
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { 1f, 2f, 3f, 9 }), new byte[] { 1, 2, 3 }, LanValueCodec.Encode(new object[] { "x" }) })
            {
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageDeath, Chunk = payload }, rig.Now += 6);
            }
            Require(book.Count(7) == 3 && rig.HostWorld.Deaths == 0, "A malformed death request changed the ledger");
            Console.WriteLine("PASS: guest death (request shape, whole ledger and hand-outs into bags once, magazines kept, no restore from older states, repeat throttled, refusal refunds, no ledger, lossy link, malformed requests)");
        }
    }
}
