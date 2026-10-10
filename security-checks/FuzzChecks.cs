using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZompiercerLAN
{
    // Fuzzing of every decoder that reads bytes from another party: the friend
    // (guest profile, storage requests), the host (storage replies), the network
    // worker (IPC) and the relay (UDP seats, J-PAKE rounds), plus the native-save
    // figurine conversion. Inputs are random bytes, mutations of valid encodings
    // and random value trees that pass the codec and reach the field checks.
    // A decoder may only reject with the exceptions its callers handle; anything
    // else (InvalidCast, NullReference, IndexOutOfRange...) is a failure, and so
    // is one input that takes longer than 250 ms.
    internal static partial class Program
    {
        private static readonly Type[] CodecRejects = { typeof(InvalidDataException), typeof(EndOfStreamException), typeof(ArgumentException) };
        private static readonly Type[] DataRejects = { typeof(InvalidDataException) };

        private static void FuzzChecks()
        {
            // Fixed seed for reproducible release runs; ZLAN_FUZZ_SEED explores other inputs.
            string seedText = Environment.GetEnvironmentVariable("ZLAN_FUZZ_SEED");
            int seed = string.IsNullOrEmpty(seedText) ? 90417 : int.Parse(seedText);
            var random = new Random(seed);
            int inputs = 0;
            inputs += FuzzCodec(random);
            inputs += FuzzStorage(random);
            inputs += FuzzGuestProfile(random);
            inputs += FuzzIpc(random);
            inputs += FuzzRelayAndPake(random);
            inputs += FuzzBeaverSave(random);
            Console.WriteLine("PASS: fuzzing (seed " + seed + ", " + inputs + " inputs: value codec, 32 storage decoders, train decor, guest profile, IPC status/rooms/faults/relay options/frames, relay UDP seats, J-PAKE rounds, native-save figurine conversion)");
        }

        // ------------------------------------------------------------ harness

        private static int Fuzz(string target, IEnumerable<byte[]> inputs, Action<byte[]> decode, Type[] allowed)
        {
            int count = 0;
            var watch = new Stopwatch();
            foreach (var input in inputs)
            {
                count++;
                watch.Restart();
                try { decode(input); }
                catch (Exception ex)
                {
                    bool expected = false;
                    foreach (var type in allowed) if (type.IsInstanceOfType(ex)) expected = true;
                    if (!expected)
                        throw new Exception("Fuzz " + target + ": " + ex.GetType().Name + " (" + ex.Message + ") for input " + Hex(input), ex);
                }
                if (watch.ElapsedMilliseconds > 250) throw new Exception("Fuzz " + target + ": " + watch.ElapsedMilliseconds + " ms for input " + Hex(input));
            }
            return count;
        }

        private static string Hex(byte[] input)
        {
            if (input == null) return "null";
            var text = BitConverter.ToString(input, 0, Math.Min(input.Length, 96)).Replace("-", "");
            return input.Length + " bytes " + text + (input.Length > 96 ? "..." : "");
        }

        // Random bytes, then mutations of each seed.
        private static IEnumerable<byte[]> Inputs(Random random, byte[][] seeds, int randomCount, int mutationsPerSeed, int maxLength)
        {
            for (int i = 0; i < randomCount; i++)
            {
                var bytes = new byte[random.Next(0, maxLength + 1)];
                random.NextBytes(bytes);
                yield return bytes;
            }
            foreach (var seed in seeds)
            {
                yield return (byte[])seed.Clone();
                for (int i = 0; i < mutationsPerSeed; i++) yield return Mutate(random, seed);
            }
        }

        private static readonly int[] Extremes = { 0, 1, -1, 2, 255, 256, 65535, 65536, 0x7FFF, int.MaxValue, int.MinValue, 2048, 2049, 8192, 128, 129 };

        private static byte[] Mutate(Random random, byte[] seed)
        {
            var bytes = new List<byte>(seed);
            int operations = 1 + random.Next(4);
            for (int n = 0; n < operations; n++)
            {
                int at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
                switch (random.Next(8))
                {
                    case 0: if (bytes.Count > 0) bytes[at] ^= (byte)(1 << random.Next(8)); break;
                    case 1: if (bytes.Count > 0) bytes[at] = (byte)new[] { 0, 1, 0x7F, 0x80, 0xFF, 5, 6, 4 }[random.Next(8)]; break;
                    case 2: if (bytes.Count > 0) bytes.RemoveRange(at, bytes.Count - at); break;
                    case 3: for (int i = random.Next(1, 16); i > 0; i--) bytes.Add((byte)random.Next(256)); break;
                    case 4: bytes.Insert(at, (byte)random.Next(256)); break;
                    case 5: if (bytes.Count > 0) bytes.RemoveAt(at); break;
                    case 6:
                        var value = BitConverter.GetBytes(Extremes[random.Next(Extremes.Length)]);
                        for (int i = 0; i < 4 && at + i < bytes.Count; i++) bytes[at + i] = value[i];
                        break;
                    default:
                        if (bytes.Count > 2)
                        {
                            int length = random.Next(1, Math.Min(64, bytes.Count - at) + 1);
                            bytes.InsertRange(random.Next(bytes.Count), bytes.GetRange(at, length));
                        }
                        break;
                }
            }
            return bytes.ToArray();
        }

        // A random value tree the codec accepts (so the field checks behind it run).
        private static object RandomValue(Random random, int depth)
        {
            switch (random.Next(depth >= 4 ? 6 : 8))
            {
                case 0: return null;
                case 1: return Extremes[random.Next(Extremes.Length)];
                case 2: return random.Next(-3, 40);
                case 3: return new[] { 0f, 1f, -1f, .5f, 99999f, -100001f, 1e7f, 256.5f }[random.Next(8)];
                case 4: return random.Next(3) == 0 ? "" : new[] { "Terrain1", "Шкаф", "x", new string('a', 97), "\u0000", "Ящик с инструментами" }[random.Next(6)];
                case 5: return random.Next(2) == 0 ? (object)(random.Next(2) == 1) : RandomBytes(random, random.Next(0, 40));
                default:
                    var array = new object[random.Next(0, 15)];
                    for (int i = 0; i < array.Length; i++) array[i] = RandomValue(random, depth + 1);
                    return array;
            }
        }

        private static byte[] RandomBytes(Random random, int length) { var b = new byte[length]; random.NextBytes(b); return b; }

        private static object[] RandomRoot(Random random, int length)
        {
            var root = new object[length];
            for (int i = 0; i < length; i++) root[i] = RandomValue(random, 1);
            return root;
        }

        // Encodings of random trees shaped like a payload of `length` fields, a few
        // fields replaced by the matching field of a valid sample (deeper checks run).
        private static IEnumerable<byte[]> Trees(Random random, object[] sample, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var root = RandomRoot(random, random.Next(4) == 0 ? random.Next(0, 16) : sample.Length);
                if (root.Length == sample.Length)
                    for (int f = 0; f < root.Length; f++) if (random.Next(3) > 0) root[f] = sample[f];
                byte[] bytes;
                try { bytes = LanValueCodec.Encode(root); } catch (InvalidDataException) { continue; }
                yield return bytes;
            }
        }

        private static IEnumerable<byte[]> Concat(params IEnumerable<byte[]>[] parts) { foreach (var part in parts) foreach (var b in part) yield return b; }

        // ------------------------------------------------------------- codec

        private static int FuzzCodec(Random random)
        {
            int count = 0;
            // Round trip of random valid trees.
            for (int i = 0; i < 3000; i++)
            {
                var root = RandomRoot(random, random.Next(0, 12));
                byte[] bytes;
                try { bytes = LanValueCodec.Encode(root); } catch (InvalidDataException) { continue; }
                Require(SameTree(LanValueCodec.Decode(bytes), root), "Value codec round trip");
                count++;
            }
            var seeds = new List<byte[]>();
            for (int i = 0; i < 40; i++) { try { seeds.Add(LanValueCodec.Encode(RandomRoot(random, random.Next(1, 10)))); } catch (InvalidDataException) { } }
            // Deeply nested and very wide shapes at the limits.
            var deep = new object[1]; var cursor = deep;
            for (int i = 0; i < LanValueCodec.MaxDepth - 1; i++) { var next = new object[1]; cursor[0] = next; cursor = next; }
            seeds.Add(LanValueCodec.Encode(deep));
            seeds.Add(LanValueCodec.Encode(new object[LanValueCodec.MaxArray]));
            count += Fuzz("LanValueCodec.Decode", Inputs(random, seeds.ToArray(), 5000, 300, 600), b => LanValueCodec.Decode(b), CodecRejects);
            // Hostile length fields: huge counts with little data must not allocate or loop.
            var hostile = new List<byte[]>();
            foreach (byte tag in new byte[] { 4, 5, 6 })
                foreach (int n in Extremes)
                {
                    var b = new byte[5]; b[0] = tag; Buffer.BlockCopy(BitConverter.GetBytes(n), 0, b, 1, 4);
                    hostile.Add(new byte[] { 5, 1, 0, 0, 0 }.Join(b));
                }
            count += Fuzz("LanValueCodec.Decode hostile lengths", hostile, b => LanValueCodec.Decode(b), CodecRejects);
            return count;
        }

        private static bool SameTree(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            var aa = a as object[]; var ba = b as object[];
            if (aa != null || ba != null)
            {
                if (aa == null || ba == null || aa.Length != ba.Length) return false;
                for (int i = 0; i < aa.Length; i++) if (!SameTree(aa[i], ba[i])) return false;
                return true;
            }
            var ab = a as byte[]; var bb = b as byte[];
            if (ab != null || bb != null) return ab != null && bb != null && Convert.ToBase64String(ab) == Convert.ToBase64String(bb);
            return a.GetType() == b.GetType() && a.Equals(b);
        }

        // ----------------------------------------------------------- storage

        private static int FuzzStorage(Random random)
        {
            var item = Item(10, 3);
            var fingerprint = LanStorage.Fingerprint(item);
            var pose = new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f };
            var target = LanStorage.ScenePayload("Terrain1", 1, 2, 3, "Шкаф");
            var view = LanStorage.EncodeView(new LanStorage.View { Handle = 7, Status = 0, Name = "Шкаф", Weight = 1, MaxWeight = 50, Items = new object[] { item, Item(11, 1) } });

            int count = 0;
            float x, y, z; int a, b, c, d; float[] r1, r2; object[] o; byte[] f;
            var decoders = new List<KeyValuePair<string, KeyValuePair<object[], Action<byte[]>>>>();
            Action<string, byte[], Action<byte[]>> add = (name, seed, decode) =>
                decoders.Add(new KeyValuePair<string, KeyValuePair<object[], Action<byte[]>>>(name, new KeyValuePair<object[], Action<byte[]>>(LanValueCodec.Decode(seed), decode)));
            add("DecodeTarget scene", target, p => LanStorage.DecodeTarget(p));
            add("DecodeTarget train", LanStorage.TrainPayload(1, LanStorage.TrainPart, 99), p => LanStorage.DecodeTarget(p));
            add("DecodeTarget bag", LanStorage.BagPayload(5), p => LanStorage.DecodeTarget(p));
            add("DecodeItem", LanStorage.EncodeItem(item), p => LanStorage.DecodeItem(p));
            add("DecodeTake", LanStorage.TakePayload(1, 2, fingerprint), p => LanStorage.DecodeTake(p, out a, out b, out f));
            add("DecodePickup", LanStorage.PickupPayload(5, 10, 1), p => LanStorage.DecodePickup(p, out a, out b, out c));
            add("DecodeDrop", LanStorage.DropPayload(item, 1, 2, 3), p => LanStorage.DecodeDrop(p, out x, out y, out z));
            add("DecodeDrop car", LanStorage.DropPayload(item, 1, pose), p => LanStorage.DecodeDrop(p, out x, out y, out z, out a, out r1));
            add("DecodeWork hit", LanStorage.WorkPayload(LanStorage.WorkHit, 1, 2, 3, 40, 0, null), p => LanStorage.DecodeWork(p, out x, out y, out z, out a, out b, out o));
            add("DecodeWork refuel", LanStorage.WorkPayload(LanStorage.WorkRefuel, 1, 2, 3, -1, 1, Item(8, 1)), p => LanStorage.DecodeWork(p, out x, out y, out z, out a, out b, out o));
            add("DecodeBuild", LanStorage.BuildPayload(1, LanStorage.TrainPart, 99, 40), p => LanStorage.DecodeBuild(p, out a, out b, out c, out d));
            add("DecodePlace", LanStorage.PlacePayload(12, 1, 3, new[] { 1f, 2f, 3f }, pose), p => LanStorage.DecodePlace(p, out a, out b, out c, out r1, out r2));
            add("DecodeCraft", LanStorage.CraftPayload(target, 30, 1, new object[] { Item(10, 2), Item(11, 1) }), p => LanStorage.DecodeCraft(p, out a, out b, out o));
            add("DecodeControl lever", LanStorage.ControlPayload(LanStorage.ControlLever, 1, -5, 750, null), p => LanStorage.DecodeControl(p, out a, out b, out c, out d, out o));
            add("DecodeControl repair", LanStorage.ControlPayload(LanStorage.ControlRepair, 3, 9, 0, Item(66, 1)), p => LanStorage.DecodeControl(p, out a, out b, out c, out d, out o));
            add("DecodeControl door", LanStorage.ControlPayload(LanStorage.ControlDoor, 2, 77, LanStorage.DoorValue(LanStorage.DoorPart, true, 1234), null), p => LanStorage.DecodeControl(p, out a, out b, out c, out d, out o));
            add("DecodeDeath", LanStorage.DeathPayload(1, 2, 3, 2), p => LanStorage.DecodeDeath(p, out x, out y, out z, out a));
            add("DecodeView", view, p => LanStorage.DecodeView(p));
            add("DecodeView craft", LanStorage.EncodeView(new LanStorage.View { Handle = 7, Name = "Верстак", Weight = 1, MaxWeight = 50, Items = new object[] { item },
                Craft = new LanStorage.CraftState { WorkingOn = 30, Elapsed = 12, Working = true, Queue = new[] { new[] { 30, 2 }, new[] { 31, 1 } } } }), p => LanStorage.DecodeView(p));
            add("DecodeCraftCancel", LanStorage.CraftCancelPayload(1, 30), p => LanStorage.DecodeCraftCancel(p, out a, out b));
            uint questKey; string questScene;
            add("DecodeQuest", LanStorage.QuestPayload(LanStorage.QuestComplete, 77, "Location8"), p => LanStorage.DecodeQuest(p, out questKey, out questScene));
            add("DecodeQuestItems", LanStorage.QuestItemsPayload(new object[] { Item(10, 2), Item(11, 1) }), p => LanStorage.DecodeQuestItems(p));
            add("DecodeTakable release", LanStorage.ReleasePayload(9, LanStorage.ReleaseThrow, 1, new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f }, new[] { 4f, 5f, 6f }), p => LanStorage.DecodeTakable(p));
            add("DecodeTakable setting", LanStorage.RobotSetPayload(9, LanStorage.RobotAttack, 2), p => LanStorage.DecodeTakable(p));
            add("DecodeTakable station", LanStorage.StationPayload(9, LanStorage.StationBack), p => LanStorage.DecodeTakable(p));
            add("DecodeRobot", LanStorage.EncodeRobot(new LanStorage.RobotState { Active = true, Behavior = 1, Battery = 40, Durability = 90,
                Devices = new[] { 300, -1 }, Storage = new[] { true, false }, Medkit = new[] { 1, -1 }, Upgrades = new[] { 310 } }), p => LanStorage.DecodeRobot(p));
            add("DecodeTarget robot", LanStorage.RobotPayload(9, LanStorage.RobotSlotDevice, 2), p => LanStorage.DecodeTarget(p));
            int throwCar, throwType; float[] throwAt, throwTurn;
            add("DecodeThrow", LanStorage.ThrowPayload(Item(70, 1), 1, new[] { 1f, 2f, 3f }, new[] { 0f, 0f, 0f, 1f }, LanStorage.ThrowStandard), p => LanStorage.DecodeThrow(p, out throwCar, out throwAt, out throwTurn, out throwType));
            add("DecodeDecor paint", LanStorage.DecorPayload(new LanStorage.DecorRequest { Kind = LanStorage.DecorPaint, Car = 1, Owner = LanStorage.TrainPart, Identity = 99, Index = 2,
                Text = "Wood", Color = new[] { 1f, .5f, 0f, 1f }, ApplyColor = true }), p => LanStorage.DecodeDecor(p));
            add("DecodeDecor wire", LanStorage.DecorPayload(new LanStorage.DecorRequest { Kind = LanStorage.DecorWire, Car = 1, Owner = LanStorage.TrainFurniture, Identity = 99, Index = 1,
                OutCar = 2, OutIdentity = -5 }), p => LanStorage.DecodeDecor(p));
            add("DecodeDecor sign", LanStorage.DecorPayload(new LanStorage.DecorRequest { Kind = LanStorage.DecorSign, Car = 0, Owner = LanStorage.TrainFurniture, Identity = 7, Text = "Кухня" }), p => LanStorage.DecodeDecor(p));
            int furnitureCar; float[] furniturePose; int dismantleCar, dismantleOwner, dismantleId, dismantleTool;
            add("DecodeFurniture", LanStorage.FurniturePayload(Item(80, 1), 1, new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f }), p => LanStorage.DecodeFurniture(p, out furnitureCar, out furniturePose));
            add("DecodeDismantle", LanStorage.DismantlePayload(LanStorage.DismantleHit, 1, LanStorage.TrainPart, 99, 40), p => LanStorage.DecodeDismantle(p, out dismantleCar, out dismantleOwner, out dismantleId, out dismantleTool));
            add("DecodeRefund", LanStorage.RefundPayload(new object[] { Item(10, 4), Item(11, 2) }), p => LanStorage.DecodeRefund(p));

            foreach (var entry in decoders)
            {
                var sample = entry.Value.Key;
                var seed = LanValueCodec.Encode(sample);
                var inputs = Concat(Inputs(random, new[] { seed }, 600, 1500, 300), Trees(random, sample, 2500), FieldSwaps(random, sample, 600));
                count += Fuzz("LanStorage." + entry.Key, inputs, entry.Value.Value, DataRejects);
            }
            // 1.4.0: a train object's paint, wires and sign text from the host's layout.
            var decor = new TrainDecor { Sign = "Кухня" };
            decor.Paints.Add(new TrainDecor.Paint { Index = 3, R = 9, G = 8, B = 7, A = 255, Material = "Wood" });
            decor.Wires.Add(new TrainDecor.Wire { Input = 0, Output = 42 });
            count += Fuzz("TrainDecor.Decode", Inputs(random, new[] { decor.Encode(), new TrainDecor { Sign = "" }.Encode() }, 2000, 3000, 200), p => TrainDecor.Decode(p), DataRejects);
            return count;
        }

        // The valid sample with single fields replaced by values of every other type.
        private static IEnumerable<byte[]> FieldSwaps(Random random, object[] sample, int count)
        {
            for (int i = 0; i < count && sample.Length > 0; i++)
            {
                var root = (object[])sample.Clone();
                int field = random.Next(root.Length);
                var inner = root[field] as object[];
                if (inner != null && inner.Length > 0 && random.Next(2) == 0)
                {
                    inner = (object[])inner.Clone();
                    int j = random.Next(inner.Length);
                    var leaf = inner[j] as object[];
                    if (leaf != null && leaf.Length > 0 && random.Next(2) == 0) { leaf = (object[])leaf.Clone(); leaf[random.Next(leaf.Length)] = RandomValue(random, 3); inner[j] = leaf; }
                    else inner[j] = RandomValue(random, 2);
                    root[field] = inner;
                }
                else root[field] = RandomValue(random, 1);
                byte[] bytes;
                try { bytes = LanValueCodec.Encode(root); } catch (InvalidDataException) { continue; }
                yield return bytes;
            }
        }

        // ------------------------------------------------------ guest profile

        private static int FuzzGuestProfile(Random random)
        {
            int count = 0;
            var seeds = new List<byte[]>();
            for (int format = 1; format <= 2; format++)
                for (int i = 0; i < 20; i++)
                {
                    var root = RandomRoot(random, format == 1 ? 13 : 14);
                    root[0] = format;
                    root[1] = "Terrain1";
                    if (format == 2) root[13] = new object[] { random.Next(3), random.Next(3) };
                    try { seeds.Add(LanValueCodec.Encode(root)); } catch (InvalidDataException) { }
                }
            count += Fuzz("LanGuestSchema.Decode", Inputs(random, seeds.ToArray(), 2000, 200, 2000), p => LanGuestSchema.Decode(p), DataRejects);
            for (int format = 1; format <= 2; format++)
            {
                var sample = RandomRoot(random, format == 1 ? 13 : 14); sample[0] = format; sample[1] = "Terrain1";
                count += Fuzz("LanGuestSchema.Decode trees", Concat(Trees(random, sample, 3000), FieldSwaps(random, sample, 2000)), p => LanGuestSchema.Decode(p), DataRejects);
            }
            return count;
        }

        // --------------------------------------------------------------- IPC

        private static int FuzzIpc(Random random)
        {
            int count = 0;
            var ipcRejects = new[] { typeof(InvalidDataException), typeof(EndOfStreamException), typeof(ArgumentException), typeof(LanFaultException) };
            var status = LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.Active, Session = 42, PeerAddress = IPAddress.Parse("192.168.1.20"), PeerPort = 27777 });
            var pending = LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 7, PendingAddress = IPAddress.Parse("10.0.0.5"), RoomName = "Комната", Seconds = 5 });
            var rooms = LanIpc.RoomsBytes(new[] { new LanRoomInfo { Address = IPAddress.Parse("192.168.1.5"), Room = new byte[16], Name = "Поезд", Flags = 1 } });
            count += Fuzz("LanIpc.ReadStatus", Inputs(random, new[] { status, pending }, 3000, 2000, 120), p => LanIpc.ReadStatus(p), ipcRejects);
            count += Fuzz("LanIpc.ReadRooms", Inputs(random, new[] { rooms }, 3000, 2000, 400), p => LanIpc.ReadRooms(p), ipcRejects);
            count += Fuzz("LanFaultException.Read", Inputs(random, new[] { new byte[] { 1 } }, 500, 300, 4), p => LanFaultException.Read(p), ipcRejects);
            var options = LanIpc.Encode(w => new LanRelayOptions { Server = IPAddress.Parse("203.0.113.10"), Fingerprint = new byte[32], AccessKey = "key", Room = "7Q2MXK4D" }.Write(w));
            count += Fuzz("LanRelayOptions.Read", Inputs(random, new[] { options }, 2000, 2000, 200), p => LanIpc.Decode(p, r => LanRelayOptions.Read(r)), ipcRejects);
            byte[] frame;
            using (var stream = new MemoryStream()) { ulong sequence = 0; LanIpc.Write(stream, ref sequence, new LanIpcFrame { Kind = LanIpcKind.Status, Context = 1, Payload = status }); frame = stream.ToArray(); }
            count += Fuzz("LanIpc.Read", Inputs(random, new[] { frame }, 2000, 3000, 2200), p => { ulong seq = 0; using (var s = new MemoryStream(p)) LanIpc.Read(s, ref seq); }, ipcRejects);
            return count;
        }

        // ------------------------------------------------- relay seats, J-PAKE

        private static int FuzzRelayAndPake(Random random)
        {
            int count = 0;
            var secret = RandomBytes(random, 32);
            using (var session = new Relay.RelayUdpSession(77, secret))
            using (var sender = new Relay.RelayUdpSession(77, secret))
            {
                var token = new byte[16];
                var seeds = new[] { sender.SealData(new byte[] { 1, 2, 3 }, 0, 3), sender.SealBind(1) };
                count += Fuzz("Relay.RelayUdpSession.TryOpen", Inputs(random, seeds, 3000, 2000, 1500), p =>
                {
                    byte kind; int offset, length; ulong nonce; byte flags;
                    if (session.TryOpen(p, p.Length, out kind, out offset, out length))
                        Relay.RelayUdpSession.ReadBindAck(p, offset, length, out nonce, out flags, token);
                }, new Type[0]);
            }
            // J-PAKE rounds from an unauthenticated peer: garbage must be refused, never crash.
            var room = RandomBytes(random, 16); var nonceBytes = RandomBytes(random, 16);
            var peer = new LanPake(true, room, nonceBytes, "123456");
            var round1 = peer.Round1();
            var pakeRejects = new[] { typeof(InvalidDataException), typeof(Org.BouncyCastle.Crypto.CryptoException), typeof(EndOfStreamException) };
            count += Fuzz("LanPake.AcceptRound1", Inputs(random, new[] { round1 }, 100, 200, 1200), p => new LanPake(false, room, nonceBytes, "123456").AcceptRound1(p), pakeRejects);
            var host = new LanPake(true, room, nonceBytes, "123456"); var guest = new LanPake(false, room, nonceBytes, "123456");
            var h1 = host.Round1(); var g1 = guest.Round1(); host.AcceptRound1(g1); guest.AcceptRound1(h1);
            var h2 = host.Round2();
            count += Fuzz("LanPake.AcceptRound2", Inputs(random, new[] { h2 }, 60, 150, 800), p =>
            {
                var fresh = new LanPake(false, room, nonceBytes, "123456"); fresh.Round1(); fresh.AcceptRound1(h1); fresh.AcceptRound2(p);
            }, pakeRejects);
            return count;
        }

        // ------------------------------------------- native save conversion

        private static int FuzzBeaverSave(Random random)
        {
            const int custom = 4095, fallback = 149;
            int count = 0;
            for (int i = 0; i < 2000; i++)
            {
                int beavers;
                var graph = SaveGraph(random, out beavers);
                var before = Snapshot(graph);
                int changed;
                var saved = LanBeaverSaveCodec.Transform(graph, custom, fallback, true, false, out changed);
                Require(changed == beavers, "Figurine save conversion count");
                Require(Snapshot(graph) == before, "Figurine save conversion changed the live graph");
                Require(!ContainsId(saved, custom), "Custom figurine id written to a native save");
                int restored;
                var loaded = LanBeaverSaveCodec.Transform(saved, custom, fallback, false, true, out restored);
                Require(restored == beavers, "Figurine load conversion count");
                Require(Snapshot(loaded) == before, "Save and load of figurines is not a round trip");
                int kept;
                var vanilla = LanBeaverSaveCodec.Transform(saved, custom, fallback, false, false, out kept);
                Require(kept == beavers && !ContainsId(vanilla, custom), "Without the mod figurines stay native toys");
                count++;
            }
            return count;
        }

        // Random save-shaped graph: nested arrays, items [id, amount, extra4] and
        // placed items [transform6, id, amount, extra4], shared subtrees and a cycle.
        private static object[] SaveGraph(Random random, out int beavers)
        {
            beavers = 0;
            var nodes = new List<object[]>();
            var root = new object[random.Next(1, 8)];
            nodes.Add(root);
            for (int i = 0; i < root.Length; i++)
            {
                var list = new object[random.Next(0, 10)];
                for (int j = 0; j < list.Length; j++)
                {
                    int kind = random.Next(6);
                    int id = random.Next(5) == 0 ? custom4095(random, ref beavers) : random.Next(1, 300);
                    var extra = new object[] { random.Next(4) == 0 ? (object)random.Next(5) : null, null, random.Next(2) == 0 ? (object)random.Next(9) : null, random.Next(3) == 0 ? new object[] { 1, "x" } : null };
                    if (id == 4095) extra[0] = null;
                    if (kind == 0) list[j] = new object[] { id, random.Next(1, 20), extra };
                    else if (kind == 1) list[j] = new object[] { new object[] { 1f, 2f, 3f, 0f, 90f, 0f }, id, 1, extra };
                    else if (kind == 2) list[j] = random.Next(1000);
                    else if (kind == 3) list[j] = "Terrain" + random.Next(3);
                    else if (kind == 4 && nodes.Count > 1) list[j] = nodes[random.Next(nodes.Count)]; // shared reference or cycle
                    else list[j] = new object[] { random.Next(9), 2.5f, null };
                    var array = list[j] as object[];
                    if (array != null && !nodes.Contains(array)) nodes.Add(array);
                }
                root[i] = list;
                nodes.Add(list);
            }
            // Shared subtrees can hold the same figurine twice: count distinct entries.
            beavers = 0;
            foreach (var node in Reachable(root)) if (IsItem(node, 4095)) beavers++;
            return root;
        }

        private static int custom4095(Random random, ref int beavers) { beavers++; return 4095; }

        private static bool IsItem(object[] node, int id)
        {
            int at = node.Length == 3 ? 0 : node.Length == 4 && node[0] is object[] && ((object[])node[0]).Length == 6 ? 1 : -1;
            return at >= 0 && node[at] is int && (int)node[at] == id && node[at + 1] is int && node[at + 2] is object[] && ((object[])node[at + 2]).Length == 4;
        }

        private static List<object[]> Reachable(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var list = new List<object[]>();
            var stack = new Stack<object>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var array = stack.Pop() as object[];
                if (array == null || !seen.Add(array)) continue;
                list.Add(array);
                foreach (var child in array) stack.Push(child);
            }
            return list;
        }

        private static bool ContainsId(object root, int id)
        {
            foreach (var node in Reachable(root)) if (IsItem(node, id)) return true;
            return false;
        }

        // Canonical text of a graph, cycles and shared nodes as back references.
        private static string Snapshot(object root)
        {
            var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            var text = new StringBuilder();
            Write(root);
            return text.ToString();
            void Write(object value)
            {
                var array = value as object[];
                if (array == null) { text.Append(value == null ? "~" : value.GetType().Name + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return; }
                int id;
                if (ids.TryGetValue(array, out id)) { text.Append('@').Append(id).Append(';'); return; }
                ids.Add(array, ids.Count);
                text.Append('[');
                foreach (var child in array) Write(child);
                text.Append(']');
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }

    internal static class FuzzBytes
    {
        internal static byte[] Join(this byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }
}
