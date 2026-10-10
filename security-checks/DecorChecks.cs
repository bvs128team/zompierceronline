using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.4.0 (schema 31): storage request 16 throws the guest's explosive in the host's world (escrow,
    // the guest's ledger, the host's pace), storage request 17 paints, wires and letters the host's
    // train, and the train layout carries paint, wires and sign texts (TrainDecor).
    internal static partial class Program
    {
        private static void DecorChecks()
        {
            ThrowProtocolChecks();
            ThrowRequestChecks();
            DecorProtocolChecks();
            DecorRequestChecks();
            TrainDecorChecks();
            Console.WriteLine("PASS: guest explosives and train decor (throw and decor request shapes and bounds, sign and material cleaning, escrow back on refusal, ledger taken once over a lossy link, host pace, TrainDecor format)");
        }

        private static readonly float[] Upright = { 0f, 0f, 0f, 1f };

        private static void ThrowProtocolChecks()
        {
            Require(LanProtocol.ValidStorageRequest(LanProtocol.StorageThrow, 0, 10) && !LanProtocol.ValidStorageRequest(LanProtocol.StorageThrow, 1, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageThrow, 0, 0) && LanProtocol.ValidStorageRequest(LanProtocol.StorageDecor, 0, 10) &&
                !LanProtocol.ValidStorageRequest(LanProtocol.StorageDecor, 2, 10), "Throw and decor request shapes");
            int car, type; float[] at, turn;
            var escrow = LanStorage.DecodeThrow(LanStorage.ThrowPayload(Item(70, 1), 2, new[] { 1f, 1.5f, -3f }, new[] { 0f, .7071068f, 0f, .7071068f }, LanStorage.ThrowAlternative), out car, out at, out turn, out type);
            Require((int)escrow[0] == 70 && (int)escrow[1] == 1 && car == 2 && at[2] == -3f && turn[1] > .7f && type == LanStorage.ThrowAlternative, "Throw roundtrip");
            LanStorage.DecodeThrow(LanStorage.ThrowPayload(Item(70, 1), -1, new[] { 5000f, 10f, -9000f }, Upright, LanStorage.ThrowStandard), out car, out at, out turn, out type);
            Require(car == -1 && at[0] == 5000f, "Throw in the world refused");
            Func<int, object, object[]> with = (index, value) =>
            {
                var r = new object[] { Item(70, 1), 1, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0 };
                if (index >= 0) r[index] = value;
                return r;
            };
            foreach (var bad in new[] { with(0, Item(70, 2)), with(0, 5), with(1, LanProtocol.MaxCars), with(1, -2), with(2, 300f), with(2, 1),
                with(5, 1f), with(8, 0f), with(9, 2), with(9, -1), with(-1, null).Take(9).ToArray(), with(-1, null).Concat(new object[] { 0 }).ToArray() })
                ExpectRejected(() => LanStorage.DecodeThrow(LanValueCodec.Encode(bad), out car, out at, out turn, out type), "Invalid throw accepted");
            ExpectRejected(() => LanStorage.DecodeThrow(LanValueCodec.Encode(new object[] { Item(70, 1), -1, 100000f, 0f, 0f, 0f, 0f, 0f, 1f, 0 }), out car, out at, out turn, out type), "Throw beyond the world accepted");
        }

        private static LanGuestLedger ThrowLedger(int grenades)
        {
            var book = new LanGuestLedger(new CountingRules());
            book.Reset(LedgerProfile(new object[] { Item(70, grenades), Item(10, 5) }, new object[0], new int[0]), 0);
            return book;
        }

        private static void ThrowRequestChecks()
        {
            var rig = new StorageRig();
            var book = ThrowLedger(3);
            rig.Host.Ledger = () => book;
            var answers = new List<byte>();
            Action<byte> keep = status => answers.Add(status);
            // One throw: off the ledger, into the host's world, nothing back to the backpack.
            Require(rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep), "Throw not queued");
            Require(rig.Client.Busy, "A throw in flight does not hold the guest's uploads");
            rig.Pump(1);
            Require(answers.SequenceEqual(new[] { LanStorage.Ok }) && rig.HostWorld.Throws.Count == 1 && book.Count(70) == 2 && rig.GuestWorld.Backpack.Count == 0 && !rig.Client.Busy,
                "Throw not carried out once");
            Require((int)rig.HostWorld.Throws[0][0] == 70 && (int)rig.HostWorld.Throws[0][1] == 1, "Throw reached the world changed");
            // Faster than the game throws: the second one comes back.
            rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep);
            rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep);
            rig.Pump(1);
            Require(answers.Skip(1).SequenceEqual(new[] { LanStorage.Ok, LanStorage.Busy }) && rig.HostWorld.Throws.Count == 2 && book.Count(70) == 1 &&
                rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 70 ? (int)i[1] : 0) == 1, "Throw pace or escrow return");
            // The world refuses (too far): the ledger keeps it, the backpack gets it back.
            rig.HostWorld.ThrowStatus = LanStorage.TooFar;
            rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep);
            rig.Pump(1);
            Require(answers.Last() == LanStorage.TooFar && book.Count(70) == 1 && rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 70 ? (int)i[1] : 0) == 2, "Refused throw not given back");
            // 1.4.1: a refused throw goes back where it was taken from (the belt), the backpack only when that fails.
            var returned = new List<object[]>();
            rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep, back => returned.Add(back));
            rig.Pump(1);
            Require(returned.Count == 1 && (int)returned[0][0] == 70 && rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 70 ? (int)i[1] : 0) == 2, "Refused throw not put back where it was");
            rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep, back => { throw new InvalidOperationException("belt full"); });
            rig.Pump(1);
            Require(rig.GuestWorld.Backpack.Sum(i => (int)i[0] == 70 ? (int)i[1] : 0) == 3, "Refused throw lost when its place was full");
            rig.HostWorld.ThrowStatus = LanStorage.Ok;
            // What the ledger does not have never flies.
            int thrown = rig.HostWorld.Throws.Count;
            rig.Client.Throw(Item(71, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep);
            rig.Pump(1);
            Require(answers.Last() == LanStorage.NotOwned && rig.HostWorld.Throws.Count == thrown, "Throw of an item the guest does not own");
            Require(!rig.Client.Throw(null, 1, new[] { 0f, 0f, 0f }, Upright, 0, keep) && !rig.Client.Throw(Item(70, 2), 1, new[] { 0f, 0f, 0f }, Upright, 0, keep), "Malformed throw queued");
            // Lossy, duplicating, reordering link: the grenade leaves the ledger once and flies once.
            for (int round = 0; round < 20; round++)
            {
                rig = new StorageRig();
                var lossy = new Random(round); rig.Link.Drop = pk => lossy.Next(10) < 3; rig.Link.Duplicate = true; rig.Link.Shuffle = true;
                book = ThrowLedger(1); rig.Host.Ledger = () => book; answers.Clear();
                rig.Client.Throw(Item(70, 1), 1, new[] { 0f, 1f, 0f }, Upright, LanStorage.ThrowStandard, keep);
                rig.Pump(30);
                Require(rig.HostWorld.Throws.Count == 1 && answers.SequenceEqual(new[] { LanStorage.Ok }) && book.Count(70) == 0 && rig.GuestWorld.Backpack.Count == 0,
                    "Lossy link: throws " + rig.HostWorld.Throws.Count + ", answers " + answers.Count + ", ledger " + book.Count(70));
            }
            // Hostile requests reach no world and leave the ledger alone.
            rig = new StorageRig(); book = ThrowLedger(2); rig.Host.Ledger = () => book;
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { Item(70, 2), 1, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0 }), new byte[] { 9, 9 },
                LanValueCodec.Encode(new object[] { Item(70, 1), 1, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 7 }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageThrow, Chunk = payload }, rig.Now += 1);
            Require(rig.HostWorld.Throws.Count == 0 && book.Count(70) == 2, "A malformed throw reached the world or the ledger");
        }

        private static LanStorage.DecorRequest Decor(int kind, int index = 0, string text = "", int outIdentity = 0)
        {
            var d = new LanStorage.DecorRequest { Kind = kind, Car = 1, Owner = LanStorage.TrainPart, Identity = 4242, Index = index, Text = text };
            if (kind == LanStorage.DecorPaint) { d.Color = new[] { 1f, .5f, 0f, 1f }; d.ApplyColor = true; }
            if (kind == LanStorage.DecorWire) { d.OutCar = 2; d.OutIdentity = outIdentity; }
            return d;
        }

        private static void DecorProtocolChecks()
        {
            var paint = LanStorage.DecodeDecor(LanStorage.DecorPayload(Decor(LanStorage.DecorPaint, 3, "Metal_Painted_Red")));
            Require(paint.Kind == LanStorage.DecorPaint && paint.Index == 3 && paint.Text == "Metal_Painted_Red" && paint.Color[1] == .5f && paint.ApplyColor && paint.Identity == 4242, "Paint roundtrip");
            var wire = LanStorage.DecodeDecor(LanStorage.DecorPayload(Decor(LanStorage.DecorWire, 1, "", -77)));
            Require(wire.OutCar == 2 && wire.OutIdentity == -77 && wire.Index == 1, "Wire roundtrip");
            Require(LanStorage.DecodeDecor(LanStorage.DecorPayload(Decor(LanStorage.DecorSign, 0, "Вагон 2\nкухня"))).Text == "Вагон 2\nкухня", "Sign roundtrip");
            Require(LanStorage.DecodeDecor(LanStorage.DecorPayload(Decor(LanStorage.DecorSign, 0, ""))).Text == "", "Empty sign refused");
            foreach (int kind in new[] { LanStorage.DecorRestore, LanStorage.DecorUnwire, LanStorage.DecorUnwireAll })
                Require(LanStorage.DecodeDecor(LanStorage.DecorPayload(Decor(kind))).Kind == kind, "Decor kind " + kind + " refused");
            // Cleaning: what a sign may say, what a material is called.
            Require(LanStorage.CleanSign("a<size=500>b</size>\u0007‮c") == "asize=500b/sizec", "Sign tags or controls kept");
            Require(LanStorage.CleanSign(new string('x', 500)).Length == LanStorage.MaxSignChars && LanStorage.CleanSign(null) == "", "Sign length");
            Require(LanStorage.CleanMaterial("Wood (Instance) (Instance)") == "Wood" && LanStorage.CleanMaterial("Me\ttal") == "Metal", "Material name cleaning");
            // 1.4.1: cleaning twice gives the same name (a host's name is never refused by the guest).
            var names = new List<string> { "Wood (Instance)\u0001", new string('m', 60) + " (Instance)", new string('m', 63) + "\uD83D\uDE00x", "A (Instance)\u0007 (Instance)" };
            var namesRandom = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                var chars = new char[namesRandom.Next(0, 90)];
                for (int j = 0; j < chars.Length; j++) chars[j] = namesRandom.Next(4) == 0 ? " (Instance)"[namesRandom.Next(11)] : (char)namesRandom.Next(0, 0xD900);
                names.Add(new string(chars));
            }
            foreach (var name in names)
            {
                var once = LanStorage.CleanMaterial(name);
                Require(LanStorage.CleanMaterial(once) == once && once.Length <= LanStorage.MaxMaterialChars && !once.EndsWith(" (Instance)", StringComparison.Ordinal), "Material cleaning not stable for " + name);
            }
            Func<int, object, object[]> with = (index, value) =>
            {
                var r = new object[] { LanStorage.DecorPaint, 1, LanStorage.TrainPart, 4242, 3, "Wood", 1f, 1f, 1f, 1f, false, 0, 0 };
                if (index >= 0) r[index] = value;
                return r;
            };
            Func<object[], int, object, object[]> and = (r, index, value) => { var c = (object[])r.Clone(); c[index] = value; return c; };
            var sign = with(0, LanStorage.DecorSign); sign[5] = "x"; sign[4] = 0; for (int i = 6; i < 10; i++) sign[i] = 0f;
            var cut = and(and(sign, 0, LanStorage.DecorUnwire), 5, "");
            foreach (var bad in new[] {
                with(0, 0), with(0, 7), with(1, -1), with(1, LanProtocol.MaxCars), with(2, 3), with(3, 0), with(4, -1), with(4, LanStorage.MaxDecorIndex + 1),
                with(5, ""), with(5, "Wood (Instance)"), with(5, new string('w', LanStorage.MaxMaterialChars + 1)), with(6, 1.5f), with(9, -.1f), with(6, 1),
                with(11, 1), with(12, 5), with(10, 1),
                and(sign, 5, "<b>"), and(sign, 5, new string('s', LanStorage.MaxSignChars + 1)), and(sign, 5, "a\u0001"), and(sign, 4, 1), and(sign, 6, .5f), and(sign, 10, true),
                and(cut, 5, "x"), and(and(cut, 0, LanStorage.DecorWire), 12, 0), and(and(cut, 0, LanStorage.DecorUnwireAll), 2, LanStorage.TrainFurniture),
                and(and(cut, 0, LanStorage.DecorUnwireAll), 4, 2), with(-1, null).Take(12).ToArray(), with(-1, null).Concat(new object[] { 0 }).ToArray() })
                ExpectRejected(() => LanStorage.DecodeDecor(LanValueCodec.Encode(bad)), "Invalid decor request accepted: " + string.Join(",", bad.Select(x => x == null ? "null" : x.ToString())));
        }

        private static void DecorRequestChecks()
        {
            var rig = new StorageRig();
            var answers = new List<byte>();
            Action<byte> keep = status => answers.Add(status);
            Require(rig.Client.Decor(Decor(LanStorage.DecorPaint, 2, "Wood"), keep), "Decor not queued");
            rig.Pump(1);
            Require(rig.Client.Decor(Decor(LanStorage.DecorSign, 0, "Склад"), keep), "Decor not queued");
            rig.Pump(1);
            Require(answers.SequenceEqual(new[] { LanStorage.Ok, LanStorage.Ok }) && rig.HostWorld.Decors.Count == 2 && rig.HostWorld.Decors[1].Text == "Склад" &&
                rig.HostWorld.Decors[0].Index == 2, "Decor not carried out");
            // 1.4.1: at most ten a second: the second of two at once waits for the host's pace.
            rig.Client.Decor(Decor(LanStorage.DecorRestore, 1), keep); rig.Client.Decor(Decor(LanStorage.DecorRestore, 2), keep); rig.Pump(1);
            Require(answers.Skip(2).SequenceEqual(new[] { LanStorage.Ok, LanStorage.Busy }) && rig.HostWorld.Decors.Count == 3, "Decor pace");
            rig.HostWorld.DecorStatus = LanStorage.TooFar;
            rig.Client.Decor(Decor(LanStorage.DecorUnwire, 1), keep); rig.Pump(1);
            Require(answers.Last() == LanStorage.TooFar, "Decor refusal not answered");
            Require(!rig.Client.Decor(null, keep) && !rig.Client.Decor(Decor(LanStorage.DecorSign, 0, "<i>"), keep) && !rig.Client.Decor(Decor(LanStorage.DecorWire, 1, "", 0), keep), "Malformed decor queued");
            // Hostile requests reach no world.
            rig = new StorageRig();
            uint tx = 100;
            foreach (var payload in new[] { LanValueCodec.Encode(new object[] { 3, 1, 2, 4242, 0, "<size=999>", 0f, 0f, 0f, 0f, false, 0, 0 }), new byte[] { 1 },
                LanValueCodec.Encode(new object[] { 1, 1, 2, 4242, 0, "Wood", 2f, 0f, 0f, 0f, false, 0, 0 }) })
                rig.Host.Receive(new Packet { Kind = PacketKind.StorageRequest, Sequence = ++tx, Action = LanProtocol.StorageDecor, Chunk = payload }, rig.Now += 1);
            Require(rig.HostWorld.Decors.Count == 0, "A malformed decor request reached the world");
        }

        private static void TrainDecorChecks()
        {
            var d = new TrainDecor { Sign = "Кухня" };
            d.Paints.Add(new TrainDecor.Paint { Index = 0, R = 255, G = 10, B = 0, A = 255, Material = "Wood" });
            d.Paints.Add(new TrainDecor.Paint { Index = 5, R = 1, G = 2, B = 3, A = 4, Material = "Metal Painted" });
            d.Wires.Add(new TrainDecor.Wire { Input = 1, Output = -555 });
            var back = TrainDecor.Decode(d.Encode());
            Require(back.Sign == "Кухня" && back.Paints.Count == 2 && back.PaintOf(5).Material == "Metal Painted" && back.PaintOf(0).G == 10 && back.WireOf(1).Output == -555 &&
                back.PaintOf(1) == null && back.WireOf(0) == null, "TrainDecor roundtrip");
            Require(TrainDecor.Decode(new TrainDecor { Sign = "" }.Encode()).Sign == "", "Empty sign lost");
            var onlyWire = new TrainDecor(); onlyWire.Wires.Add(new TrainDecor.Wire { Input = 0, Output = 9 });
            Require(TrainDecor.Decode(onlyWire.Encode()).Sign == null && TrainDecor.Decode(onlyWire.Encode()).Paints.Count == 0, "Absent parts appeared");
            var good = d.Encode();
            Func<Action<BinaryWriter>, byte[]> raw = write => { using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) { write(w); return s.ToArray(); } };
            Action<BinaryWriter, string> text = (w, s) => { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); };
            foreach (var bad in new[] {
                new byte[0], new byte[] { 0 }, new byte[] { 8 }, good.Concat(new byte[] { 0 }).ToArray(), good.Take(good.Length - 1).ToArray(),
                raw(w => { w.Write((byte)1); w.Write((byte)0); }),
                raw(w => { w.Write((byte)1); w.Write((byte)(TrainDecor.MaxPaint + 1)); }),
                raw(w => { w.Write((byte)1); w.Write((byte)2); for (int i = 0; i < 2; i++) { w.Write((byte)3); w.Write(new byte[4]); text(w, "Wood"); } }),
                raw(w => { w.Write((byte)1); w.Write((byte)1); w.Write((byte)(LanStorage.MaxDecorIndex + 1)); w.Write(new byte[4]); text(w, "Wood"); }),
                raw(w => { w.Write((byte)1); w.Write((byte)1); w.Write((byte)0); w.Write(new byte[4]); text(w, "Wood (Instance)"); }),
                raw(w => { w.Write((byte)1); w.Write((byte)1); w.Write((byte)0); w.Write(new byte[4]); text(w, ""); }),
                raw(w => { w.Write((byte)1); w.Write((byte)1); w.Write((byte)0); w.Write(new byte[4]); w.Write((ushort)2); w.Write(new byte[] { 0xC3, 0x28 }); }),
                raw(w => { w.Write((byte)2); text(w, "<color=red>"); }),
                raw(w => { w.Write((byte)2); w.Write((ushort)(TrainDecor.MaxSignBytes + 1)); w.Write(new byte[TrainDecor.MaxSignBytes + 1]); }),
                raw(w => { w.Write((byte)4); w.Write((byte)1); w.Write((byte)0); w.Write(0); }),
                raw(w => { w.Write((byte)4); w.Write((byte)2); w.Write((byte)0); w.Write(5); w.Write((byte)0); w.Write(6); }),
                raw(w => { w.Write((byte)4); w.Write((byte)(TrainDecor.MaxWires + 1)); }) })
                ExpectRejected(() => TrainDecor.Decode(bad), "Invalid TrainDecor accepted: " + BitConverter.ToString(bad));
            var tooMany = new TrainDecor();
            for (int i = 0; i <= TrainDecor.MaxPaint; i++) tooMany.Paints.Add(new TrainDecor.Paint { Index = i, Material = "Wood" });
            ExpectRejected(() => tooMany.Encode(), "Oversized TrainDecor encoded");
        }
    }
}
