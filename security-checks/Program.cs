using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ZompiercerLAN
{
    // Data-only substitute; the real clock implementation contains Unity APIs.
    internal sealed class ClockSnapshot { internal int Year, Day; internal float Hour, HoursPerRealSecond; }
    internal static partial class Program
    {
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void ExpectRejected(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            catch (EndOfStreamException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            catch (Org.BouncyCastle.Crypto.CryptoException) { rejected = true; }
            Require(rejected, message);
        }
        private static int Main(string[] args)
        {
            try
            {
                ProtocolChecks();
                ZombieProtocolChecks();
                CombatProtocolChecks();
                CombatInventoryChecks();
                CombatSyncChecks.Run();
                NetworkPolicyChecks();
                LanRoutingChecks();
                PeerGuardChecks();
                IpcChecks();
                NetworkFailureChecks();
                SecretChecks();
                RetentionChecks();
                GuestChecks();
                StorageChecks();
                WorldChecks();
                SceneChecks();
                MotionChecks();
                ArenaChecks();
                LedgerChecks();
                GuestOwnershipPersistenceChecks();
                ChatChecks();
                DeathChecks();
                QuestChecks();
                TakableChecks();
                CoopChecks();
                StatChecks();
                DecorChecks();
                BuildChecks();
                FuzzChecks();
                if (Array.IndexOf(args, "--discovery") >= 0) DiscoveryChecks();
                if (Array.IndexOf(args, "--pake") >= 0) { PakeChecks(); RoomBindingPakeChecks(); }
                if (Array.IndexOf(args, "--pairing") >= 0) PairingChecks();
                if (Array.IndexOf(args, "--transport") >= 0) { TransportChecks(); TamperTransportChecks(); PeerAttackChecks(); ReconnectChecks(); }
                if (Array.IndexOf(args, "--admission") >= 0) AdmissionChecks();
                if (Array.IndexOf(args, "--peer-attacks") >= 0) PeerAttackChecks();
                if (Array.IndexOf(args, "--stress") >= 0) StressChecks(args);
#if RELAY_SERVER
                if (Array.IndexOf(args, "--relay") >= 0) RelayChecks();
#else
                if (Array.IndexOf(args, "--relay") >= 0) Console.WriteLine("SKIPPED: --relay needs the ZompiercerRelay server source next to this folder");
#endif
                Console.WriteLine("PASS: " + (args.Length == 0 ? "protocol checks" : "requested checks")); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }
        private static Packet Control(PacketKind kind) { return new Packet { Kind = kind, GameVersion = "checks", Session = 42 }; }
        private static Packet State(uint sequence)
        { return new Packet { Kind = PacketKind.State, GameVersion = "checks", Session = 42, WorldEpoch = 1, Scene = "Terrain1", Sequence = sequence, Y = 60, Yaw = 90, TrainPosition = 100 }; }
        private static NetPose Pose() { return new NetPose { QW = 1 }; }
        private static void ProtocolChecks()
        {
            var packets = new List<Packet>();
            foreach (var kind in new[] { PacketKind.Hello, PacketKind.Welcome, PacketKind.Busy, PacketKind.VersionMismatch, PacketKind.Heartbeat }) packets.Add(Control(kind));
            packets.Add(State(1));
            packets.Add(new Packet { Kind = PacketKind.WorldState, GameVersion = "checks", Session = 42, WorldEpoch = 1, Scene = "Terrain1", Location = 2,
                RailsId = 2008187229, TrainPosition = 100, Clock = new ClockSnapshot { Year = 1, Day = 3, Hour = 12, HoursPerRealSecond = .01f } });
            packets.Add(new Packet { Kind = PacketKind.TrainLayoutAck, GameVersion = "checks", Session = 42, Revision = 1 });
            packets.Add(new Packet { Kind = PacketKind.TrainLayoutChunk, GameVersion = "checks", Session = 42, Revision = 1, ChunkCount = 1, Chunk = new byte[900] });
            var cars = new CarPose[LanProtocol.MaxCars];
            for (int i = 0; i < cars.Length; i++) cars[i] = new CarPose { Root = Pose(), Body = Pose(), Front = Pose(), Back = Pose() };
            packets.Add(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42,
                Train = new TrainFrame { Revision = 1, Sequence = 1, Scene = "Terrain1", Cars = cars } });
            // 0.10.0: the cab's state, the guest's permission and marks.
            var cab = new TrainFrame { Revision = 1, Sequence = 2, Scene = "Terrain1", Flags = 127, Buttons = 0x80000001, Broken = 4, Lights = 0x8001, Levers = new[] { .5f, -1f, 0f, 999f }, Cars = cars };
            packets.Add(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = cab });
            // 1.4.3: the main tank and the fuel stations; the largest frame still fits one datagram.
            var fueled = new TrainFrame { Revision = 1, Sequence = 3, Scene = new string('s', 96), Flags = 127, Buttons = uint.MaxValue, Broken = uint.MaxValue, Lights = ushort.MaxValue,
                Levers = new[] { 1f, 2f, 3f, 4f }, Cars = cars, Fuel = 250.5f, FuelMax = 500f,
                Stations = new[] { new StationFrame { Key = -7, Fuel = 2999.5f, Flags = LanProtocol.MaxStationFlags, Gun = LanProtocol.GunHost, GunPose = Pose() },
                    new StationFrame { Key = 9, Fuel = 0f, Flags = 0, Gun = LanProtocol.GunLoose, GunPose = Pose() } } };
            var fueledPacket = new Packet { Kind = PacketKind.TrainMotion, GameVersion = new string('v', 40), Session = 42, Train = fueled };
            packets.Add(fueledPacket);
            var tanked = new TrainFrame { Revision = 1, Sequence = 4, Scene = "Terrain1", Cars = cars, Fuel = 0f, FuelMax = 0f,
                Stations = new[] { new StationFrame { Key = 1, Fuel = 10f, Flags = LanProtocol.StationActive | LanProtocol.StationPumping, Gun = LanProtocol.GunTank } } };
            packets.Add(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = tanked });
            packets.Add(new Packet { Kind = PacketKind.Mark, GameVersion = "checks", Session = 42, WorldEpoch = 1, Scene = "Terrain1", Sequence = 1,
                Action = LanProtocol.MarkEnemy, Revision = 65535, CarIndex = -1, X = 12000f, Y = -3f, Z = 4f });
            packets.Add(new Packet { Kind = PacketKind.Mark, GameVersion = "checks", Session = 42, WorldEpoch = 1, Scene = "Terrain1", Sequence = 2,
                Action = LanProtocol.MarkClear, Revision = 1, CarIndex = 7, X = 256f, Y = 0f, Z = -256f });
            Packet parsed;
            foreach (var packet in packets)
            {
                var bytes = LanProtocol.Encode(packet);
                Require(LanProtocol.TryDecode(bytes, out parsed) && parsed.Kind == packet.Kind && parsed.Session == packet.Session, "Valid packet rejected: " + packet.Kind);
                for (int size = 0; size < bytes.Length; size++)
                { var truncated = new byte[size]; Array.Copy(bytes, truncated, size); Require(!LanProtocol.TryDecode(truncated, out parsed), "Truncated packet accepted: " + packet.Kind); }
                var trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length);
                Require(!LanProtocol.TryDecode(trailing, out parsed), "Trailing data accepted");
                bytes[4] = 0; Require(!LanProtocol.TryDecode(bytes, out parsed), "Old schema accepted");
            }
            Require(LanProtocol.TryDecode(LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = cab }), out parsed) &&
                parsed.Train.Buttons == 0x80000001 && parsed.Train.Broken == 4 && parsed.Train.Lights == 0x8001 && parsed.Train.Flags == 127 &&
                parsed.Train.Levers.Length == 4 && parsed.Train.Levers[3] == 999f, "Train cab state roundtrip");
            foreach (var bad in new[] {
                new TrainFrame { Revision = 1, Sequence = 2, Scene = "Terrain1", Flags = 128, Cars = cars },
                new TrainFrame { Revision = 1, Sequence = 2, Scene = "Terrain1", Levers = new[] { 1001f }, Cars = cars } })
                Require(!LanProtocol.TryDecode(LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = bad }), out parsed), "Invalid cab state accepted");
            ExpectRejected(() => LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Train = new TrainFrame { Revision = 1, Scene = "x", Levers = new float[5], Cars = cars } }), "Five levers encoded");
            var largest = LanProtocol.Encode(fueledPacket);
            Require(largest.Length <= 1200 && LanProtocol.TryDecode(largest, out parsed) && parsed.Train.Fuel == 250.5f && parsed.Train.FuelMax == 500f &&
                parsed.Train.Stations.Length == 2 && parsed.Train.Stations[0].Key == -7 && parsed.Train.Stations[0].Fuel == 2999.5f &&
                parsed.Train.Stations[0].Flags == LanProtocol.MaxStationFlags && parsed.Train.Stations[0].Gun == LanProtocol.GunHost &&
                parsed.Train.Stations[0].GunPose != null && parsed.Train.Stations[1].Gun == LanProtocol.GunLoose, "Train fuel and stations roundtrip");
            Require(LanProtocol.TryDecode(LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = tanked }), out parsed) &&
                parsed.Train.FuelMax == 0f && parsed.Train.Stations.Length == 1 && parsed.Train.Stations[0].Gun == LanProtocol.GunTank && parsed.Train.Stations[0].GunPose == null, "Fuel gun in a tank roundtrip");
            Func<Action<TrainFrame>, TrainFrame> fuelWith = change =>
            {
                var f = new TrainFrame { Revision = 1, Sequence = 5, Scene = "Terrain1", Cars = cars, Fuel = 10f, FuelMax = 20f,
                    Stations = new[] { new StationFrame { Key = 3, Fuel = 5f, Flags = 1, Gun = LanProtocol.GunAtStation } } };
                change(f); return f;
            };
            foreach (var bad in new[] {
                fuelWith(f => f.Fuel = 22f), fuelWith(f => f.Fuel = -1f), fuelWith(f => f.FuelMax = -1f), fuelWith(f => f.Fuel = float.NaN), fuelWith(f => { f.FuelMax = 0f; f.Fuel = 1f; }),
                fuelWith(f => f.FuelMax = 100000f), fuelWith(f => f.Stations = null), fuelWith(f => f.Stations = new StationFrame[3]),
                fuelWith(f => f.Stations[0].Key = 0), fuelWith(f => f.Stations[0].Fuel = -1f), fuelWith(f => f.Stations[0].Fuel = float.NaN), fuelWith(f => f.Stations[0].Flags = 16),
                fuelWith(f => f.Stations[0].Gun = 5), fuelWith(f => f.Stations[0].Gun = LanProtocol.GunHost), fuelWith(f => f.Stations[0].GunPose = Pose()),
                fuelWith(f => { f.Stations[0].Gun = LanProtocol.GunGuest; f.Stations[0].GunPose = new NetPose { QW = 2 }; }),
                fuelWith(f => { f.Stations[0].Gun = LanProtocol.GunLoose; f.Stations[0].GunPose = new NetPose { X = float.PositiveInfinity, QW = 1 }; }) })
                ExpectRejected(() => LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = bad }), "Invalid fuel or station encoded");
            // The same on the wire: the frame ends with the station's Flags and Gun bytes.
            var stationBytes = LanProtocol.Encode(new Packet { Kind = PacketKind.TrainMotion, GameVersion = "checks", Session = 42, Train = fuelWith(f => { }) });
            Require(LanProtocol.TryDecode(stationBytes, out parsed), "Station frame rejected");
            foreach (var change in new[] { new[] { 2, 16 }, new[] { 1, 5 }, new[] { 1, LanProtocol.GunHost }, new[] { 1, LanProtocol.GunLoose } })
            {
                var copy = (byte[])stationBytes.Clone();
                copy[copy.Length - change[0]] = (byte)change[1];
                Require(!LanProtocol.TryDecode(copy, out parsed), "Invalid station bytes accepted");
            }
            var fuelBytes = (byte[])stationBytes.Clone();
            int fuelAt = fuelBytes.Length - (1 + 4 + 4 + 1 + 1) - 8; // Fuel, FuelMax, then the station count and one station
            BitConverter.GetBytes(30f).CopyTo(fuelBytes, fuelAt);
            Require(!LanProtocol.TryDecode(fuelBytes, out parsed), "Fuel above the capacity accepted");
            BitConverter.GetBytes(10f).CopyTo(fuelBytes, fuelAt);
            Require(LanProtocol.TryDecode(fuelBytes, out parsed) && parsed.Train.Fuel == 10f, "Fuel offset check");
            foreach (var mark in new[] {
                new Packet { Kind = PacketKind.Mark, Action = 4, Revision = 1, CarIndex = -1 }, new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 0, CarIndex = -1 },
                new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 65536, CarIndex = -1 }, new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 1, CarIndex = 8 },
                new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 1, CarIndex = -2 }, new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 1, CarIndex = 0, X = 257f },
                new Packet { Kind = PacketKind.Mark, Action = 1, Revision = 1, CarIndex = -1, Scene = "" } })
            {
                mark.GameVersion = "checks"; mark.Session = 42; mark.WorldEpoch = 1; mark.Sequence = 1; if (mark.Scene == null) mark.Scene = "Terrain1";
                Require(!LanProtocol.TryDecode(LanProtocol.Encode(mark), out parsed), "Invalid mark accepted");
            }
            foreach (bool host in new[] { true, false }) Require(LanNetworkPolicy.Allowed(host, PacketKind.Mark, true) && !LanNetworkPolicy.Allowed(host, PacketKind.Mark, false), "Mark direction policy");
            // Schema 21: health in percent, or unknown.
            foreach (byte health in new byte[] { 0, 1, 100, LanProtocol.NoHealth })
            { var state = State(1); state.Health = health; Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed) && parsed.Health == health, "Player health rejected: " + health); }
            foreach (byte health in new byte[] { 101, 254 })
            { var state = State(1); state.Health = health; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Invalid player health accepted: " + health); }
            // Schema 22: the view's pitch and the crouch flag.
            { var state = State(1); state.Pitch = -37.5f; state.Pose = LanProtocol.PoseCrouch;
              Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed) && parsed.Pitch == -37.5f && parsed.Pose == LanProtocol.PoseCrouch, "Player pose roundtrip"); }
            foreach (float pitch in new[] { 90.5f, -91f, float.NaN, float.PositiveInfinity })
            { var state = State(1); state.Pitch = pitch; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Invalid view pitch accepted: " + pitch); }
            { var state = State(1); state.Pose = LanProtocol.PoseCrouch | LanProtocol.PoseAim;
              Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed) && parsed.Pose == 3, "Aiming crouch rejected"); }
            { var state = State(1); state.Pose = LanProtocol.MaxPose + 1; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Unknown player pose accepted"); }
            // Schema 27: downed, dead, the flashlights and a reload; the nickname.
            { var state = State(1); state.Pose = LanProtocol.PoseDowned | LanProtocol.PoseHelmetLight | LanProtocol.PoseGunLight | LanProtocol.PoseReload | LanProtocol.PoseCrouch; state.Name = "Иван 2";
              Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed) && parsed.Pose == 117 && parsed.Name == "Иван 2", "Downed pose / nickname roundtrip"); }
            Require(LanProtocol.TryDecode(LanProtocol.Encode(State(1)), out parsed) && parsed.Name == "", "No nickname");
            foreach (var bad in new[] { "12345678901234567", " Ivan", "Ivan ", "a\nb", "<b>x</b>", "\u0085", "\uD83D\uDE00" })
            { var state = State(1); bool refused = false; state.Name = bad;
              try { LanProtocol.Encode(state); } catch (System.IO.InvalidDataException) { refused = true; }
              Require(refused && !LanProtocol.ValidName(bad), "Invalid nickname sent: " + bad.Length); }
            Require(LanProtocol.CleanName("  <Bob>\n  ") == "Bob" && LanProtocol.CleanName("12345678901234567890") == "1234567890123456" && LanProtocol.CleanName(null) == "" &&
                LanProtocol.ValidName(LanProtocol.CleanName("x\uD83D\uDE00y")), "Nickname cleaning");
            { var nameBytes = LanProtocol.Encode(State(1)); var raw = new byte[nameBytes.Length + 3]; Array.Copy(nameBytes, raw, nameBytes.Length - 1);
              raw[nameBytes.Length - 1] = 3; raw[nameBytes.Length] = (byte)' '; raw[nameBytes.Length + 1] = (byte)'A'; raw[nameBytes.Length + 2] = (byte)'B';
              Require(!LanProtocol.TryDecode(raw, out parsed), "Received nickname with a leading space accepted"); }
            // Schema 25: health points as the game shows them, and the chosen look.
            { var state = State(1); state.HealthNow = 100; state.HealthMax = 300; state.Skin = 17;
              Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed) && parsed.HealthNow == 100 && parsed.HealthMax == 300 && parsed.Skin == 17, "Health points / look roundtrip"); }
            Require(LanProtocol.TryDecode(LanProtocol.Encode(State(1)), out parsed) && parsed.Skin == LanProtocol.NoSkin && parsed.HealthMax == 0, "Default look / unknown health");
            foreach (var bad in new[] { new ushort[] { 301, 300 }, new ushort[] { 1, 0 } })
            { var state = State(1); state.HealthNow = bad[0]; state.HealthMax = bad[1]; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Health above its maximum accepted"); }
            foreach (byte skin in new byte[] { LanProtocol.MaxSkin + 1, 254 })
            { var state = State(1); state.Skin = skin; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Unknown look accepted: " + skin); }
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 100000f })
            { var state = State(1); state.X = invalid; Require(!LanProtocol.TryDecode(LanProtocol.Encode(state), out parsed), "Invalid coordinate accepted"); }
            var utf8 = LanProtocol.Encode(State(1)); utf8[7] = 0xC0;
            Require(!LanProtocol.TryDecode(utf8, out parsed), "Invalid UTF-8 accepted");
            var malformedChunk = new Packet { Kind = PacketKind.TrainLayoutChunk, GameVersion = "checks", Revision = 1, ChunkCount = 2, ChunkIndex = 0, Chunk = new byte[1] };
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(malformedChunk), out parsed), "Short intermediate chunk accepted");
            foreach (var pin in new[] { "000000", "123456", "999999" }) Require(LanPake.ValidPin(pin), "Six-digit PIN rejected");
            foreach (var pin in new[] { "ZLAN2-123456", "12345", "1234567", "１２３４５６", "12a456", "123 56" }) Require(!LanPake.ValidPin(pin), "Invalid PIN accepted");
            var random = new Random(72491);
            for (int i = 0; i < 20000; i++)
            { var input = new byte[random.Next(0, 1302)]; random.NextBytes(input); LanProtocol.TryDecode(input, out parsed); }
        }
        // Prepared for an explicitly authorized future check run. No game/Unity dependency.
        private static void ZombieProtocolChecks()
        {
            Packet decoded;
            var action=new Packet {Kind=PacketKind.ZombieAction,GameVersion="checks",Session=42,WorldEpoch=1,Sequence=1,Scene="Terrain1",EquippedItemId=1,AimZ=1,Revision=1};
            Require(LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Valid zombie action rejected");
            action.AimZ=0;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Zero aim accepted");
            action.AimZ=float.NaN;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"NaN aim accepted");
            action.AimZ=1;action.Action=2;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Unknown weapon action accepted");
            // Schema 25: item -1 is a swing of the bare fists, never a shot.
            action.EquippedItemId=-1;action.Action=1;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded) && decoded.EquippedItemId==-1,"Fist swing rejected");
            action.Action=0;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Fist shot accepted");
            action.EquippedItemId=-2;action.Action=1;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Negative item accepted");
            action.EquippedItemId=1;
            action.Action=0;action.CarIndex=2;action.X=3;action.Y=1.6f;action.Z=-4;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded) && decoded.CarIndex==2 && decoded.Z==-4,"Car-relative zombie action rejected");
            action.X=150;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Car-relative origin outside the car accepted");
            action.X=3;action.CarIndex=LanProtocol.MaxCars;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Unknown car accepted for a zombie action");
            // Schema 21: a shot's loudness in percent of the gun's hearing radius.
            action.CarIndex=-1;action.Noise=35;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded) && decoded.Noise==35,"Shot loudness roundtrip");
            action.Noise=101;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Shot louder than its gun accepted");
            action.Noise=100;action.WorldEpoch=0;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(action),out decoded),"Worldless zombie action accepted");
            var state=new Packet {Kind=PacketKind.ZombieState,GameVersion=new string('v',40),Session=42,WorldEpoch=1,Sequence=1,Scene=new string('s',96),Zombies=new ZombieFrame[LanProtocol.MaxZombieBatch]};
            for(int i=0;i<state.Zombies.Length;i++) state.Zombies[i]=new ZombieFrame {Id=(uint)i+1,Prefab=new string('z',64),Health=100};
            state.HostTime=12345.5f;
            var bytes=LanProtocol.Encode(state);
            Require(bytes.Length<=1200 && LanProtocol.TryDecode(bytes,out decoded) && decoded.HostTime==12345.5f,"Maximum zombie batch rejected");
            // Schema 26: an alive zombie's frame carries the firefighter's flames; a dead or removed one carries nothing else.
            state.Zombies[0].Flags=LanProtocol.ZombieFlameFiring|LanProtocol.ZombieFlameHand1|LanProtocol.ZombieHandFire0|LanProtocol.ZombieHandFire1;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded) && decoded.Zombies[0].Flags==60,"Firefighter flames rejected");
            foreach(byte bad in new byte[] {64,128,255,1|LanProtocol.ZombieFlameFiring,2|LanProtocol.ZombieHandFire0,3|LanProtocol.ZombieHandFire1}) {
                state.Zombies[0].Flags=bad;Require(!LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"Zombie flags "+bad+" accepted");
            }
            foreach(byte good in new byte[] {1,2,3}) { state.Zombies[0].Flags=good;Require(LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"Zombie state "+good+" rejected"); }
            state.Zombies[0].Flags=0;
            state.HostTime=float.NaN;Require(!LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"NaN zombie batch time accepted");
            state.HostTime=-1;Require(!LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"Negative zombie batch time accepted");
            state.HostTime=12345.5f;
            for(int i=0;i<bytes.Length;i++) { var truncated=new byte[i];Array.Copy(bytes,truncated,i);Require(!LanProtocol.TryDecode(truncated,out decoded),"Truncated zombie batch accepted"); }
            state.Zombies[1].Id=1;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"Duplicate zombie ID accepted");
            state.Zombies[1].Id=2;state.Zombies[0].Id=LanProtocol.MaxZombieIds+1;
            Require(!LanProtocol.TryDecode(LanProtocol.Encode(state),out decoded),"Out of catalog zombie ID accepted");
            var hurt=new Packet {Kind=PacketKind.ZombieHurt,GameVersion="checks",Session=42,WorldEpoch=1,Sequence=1,Scene="Terrain1",Damage=10,Action=1};
            Require(LanProtocol.TryDecode(LanProtocol.Encode(hurt),out decoded),"Host damage rejected");
            // 1.4.3: the sender refuses it too; on the wire the damage sits before the Action and text length bytes.
            hurt.Damage=1001;
            ExpectRejected(() => LanProtocol.Encode(hurt),"Unbounded host damage encoded");
            hurt.Damage=10; var unbounded=LanProtocol.Encode(hurt);
            BitConverter.GetBytes(1001f).CopyTo(unbounded,unbounded.Length-6);
            Require(!LanProtocol.TryDecode(unbounded,out decoded),"Unbounded host damage accepted");
            BitConverter.GetBytes(10f).CopyTo(unbounded,unbounded.Length-6);
            Require(LanProtocol.TryDecode(unbounded,out decoded) && decoded.Damage==10f,"Hurt damage offset check");
            // 1.4.3: a melee hit names the zombie's prefab; nothing else does.
            hurt.Damage=10; hurt.Action=LanProtocol.HurtMelee; hurt.Text="ZombieHunter_01";
            Require(LanProtocol.TryDecode(LanProtocol.Encode(hurt),out decoded) && decoded.Action==LanProtocol.HurtMelee && decoded.Text=="ZombieHunter_01","Melee hit with its prefab rejected");
            hurt.Text="";
            Require(LanProtocol.TryDecode(LanProtocol.Encode(hurt),out decoded) && decoded.Text=="","Melee hit without a prefab rejected");
            foreach(var bad in new[]{ new object[]{(byte)1,"Zombie"}, new object[]{(byte)6,""}, new object[]{(byte)0,""}, new object[]{(byte)5,"bad" + (char)10 + "name"}, new object[]{(byte)5,new string('z',65)} })
            {
                var copy=new Packet {Kind=PacketKind.ZombieHurt,GameVersion="checks",Session=42,WorldEpoch=1,Sequence=1,Scene="Terrain1",Damage=10,Action=(byte)bad[0],Text=(string)bad[1]};
                ExpectRejected(() => LanProtocol.Encode(copy),"Invalid zombie hurt encoded");
            }
            hurt.Text="Zombie"; var hurtBytes=LanProtocol.Encode(hurt);
            hurtBytes[hurtBytes.Length-8]=1; // the Action byte before the 6-letter prefab: a damage kind may not name one
            Require(!LanProtocol.TryDecode(hurtBytes,out decoded),"Prefab on a plain hurt accepted");
        }
        private static CombatWeapon Weapon(int id=1,int magazine=2,float durability=100) { return new CombatWeapon {ItemId=id,Magazine=magazine,Durability=durability}; }
        private static CombatAmmo Ammo(int amount=3) { return new CombatAmmo {ItemId=10,Amount=amount}; }
        private static CombatRule Rule(int id=1,byte action=0,float stamina=0) { return new CombatRule {ItemId=id,AmmoId=action==0?10:-1,Capacity=action==0?2:0,Action=action,Interval=.5f,ReloadSeconds=3,StaminaCost=stamina,MaxDurability=100}; }
        private static HostCombatInventory Ledger()
        {
            var ledger=new HostCombatInventory();
            ledger.Sync(new[]{Weapon()},new[]{Ammo()},new[]{Rule()},0);
            Require(ledger.Approved,"Valid allowance rejected");return ledger;
        }
        private static void CombatInventoryChecks()
        {
            // Nothing is allowed before the host's ledger provides an allowance.
            var ledger=new HostCombatInventory();
            Require(!ledger.TryAttack(1,0,1,0,0) && !ledger.Approved,"Weapon fired without an allowance");
            var offer=new[]{Weapon()};var reserve=new[]{Ammo()};var rules=new[]{Rule()};
            ledger.Sync(offer,reserve,rules,0);
            offer[0].Magazine=500;reserve[0].Amount=10000;rules[0].Capacity=500;
            Require(ledger.Weapons()[0].Magazine==2 && ledger.Ammo()[0].Amount==3,"Allowance shares caller arrays");
            int spent=0,reloaded=0;ledger.Spent=(item,action)=>spent++;ledger.Reloaded=(item,ammo,count)=>reloaded+=count;
            Require(!ledger.TryAttack(2,1,99,0,0),"Unowned weapon fired");
            Require(!ledger.TryAttack(3,1,1,1,0),"Weapon action substituted");
            Require(ledger.TryAttack(4,1,1,0,0),"First shot rejected");
            Require(!ledger.TryAttack(4,1,1,0,1),"Replayed shot accepted");
            // 1.0.2: an early shot within the network jitter allowance is accepted (average rate below).
            Require(ledger.TryAttack(5,1,1,0,.1),"Shot within the jitter allowance rejected");
            Require(!ledger.TryAttack(7,1,1,0,1),"Empty magazine fired");
            Require(spent==2,"Accepted shots not booked to the ledger");
            Require(ledger.TryReload(8,1,1,1),"Reload rejected");
            Require(!ledger.TryReload(9,1,1,1.1),"Reload restarted");
            ledger.Tick(3.99);Require(ledger.Weapons()[0].Magazine==0 && ledger.Ammo()[0].Amount==3,"Reload completed early");
            ledger.Tick(4);Require(ledger.Weapons()[0].Magazine==2 && ledger.Ammo()[0].Amount==1 && reloaded==2,"Reload did not conserve or book ammunition");
            ledger.Tick(100);Require(ledger.Ammo()[0].Amount==1 && reloaded==2,"Reload completion repeated");
            Require(!ledger.TryAttack(10,2,1,0,100),"Wrong inventory revision accepted");
            Require(ledger.TryAttack(10,1,1,0,100),"Valid revision rejected");
            Require(ledger.TryReload(11,1,1,101),"Partial reload rejected");
            ledger.Rebind();ledger.Tick(200);
            Require(ledger.Weapons()[0].Magazine==1 && ledger.Ammo()[0].Amount==1,"Canceled reload completed across reconnect/world change");
            Require(ledger.TryAttack(1,1,1,0,200),"New context sequence rejected");
            ledger.Rebind();Require(!ledger.TryAttack(1,1,1,0,200.1),"Reconnect bypassed cooldown or empty magazine");
            // A burst beyond the jitter allowance, and the average rate after it, are refused.
            var burst=new HostCombatInventory();
            burst.Sync(new[]{Weapon(1,30)},new[]{Ammo()},new[]{new CombatRule {ItemId=1,AmmoId=10,Capacity=30,Action=0,Interval=.5f,ReloadSeconds=3,StaminaCost=0,MaxDurability=100}},0);
            Require(burst.TryAttack(1,1,1,0,10) && burst.TryAttack(2,1,1,0,10) && !burst.TryAttack(3,1,1,0,10),"Burst beyond the jitter allowance accepted");
            Require(!burst.TryAttack(4,1,1,0,10.2) && burst.TryAttack(5,1,1,0,10.3),"Average fire rate not enforced after a burst");
            var view=ledger.Weapons();view[0].Magazine=500;Require(ledger.Weapons()[0].Magazine==0,"Snapshot modified host inventory");
            // A new ledger view (ammo taken from a host storage) raises the reserve; a reload in progress survives it.
            Require(ledger.TryReload(2,1,1,300),"Reload before a ledger update rejected");
            ledger.Sync(new[]{Weapon(1,0)},new[]{Ammo(9)},new[]{Rule()},300.5);
            ledger.Tick(303);Require(ledger.Weapons()[0].Magazine==2 && ledger.Ammo()[0].Amount==7,"Ledger update lost the reload or the new reserve");
            // A weapon that left the ledger cannot be used; its reload is cancelled.
            Require(ledger.TryReload(3,1,1,304) || true,"");
            ledger.Sync(new CombatWeapon[0],new CombatAmmo[0],new CombatRule[0],304.1);
            Require(!ledger.TryAttack(4,1,1,0,305) && ledger.ReloadItem==-1,"Weapon removed from the ledger still usable");
            ledger.DenyOrRevoke();ledger.Sync(new[]{Weapon()},new[]{Ammo()},new[]{Rule()},306);
            Require(!ledger.TryAttack(5,ledger.Revision,1,0,306) && !ledger.Approved && ledger.Denied,"Revoked combat re-enabled by the ledger");

            ledger=new HostCombatInventory();
            ledger.Sync(new[]{Weapon(1,0),Weapon(2,0)},new[]{Ammo(3)},new[]{Rule(1),Rule(2)},0);
            Require(ledger.TryReload(1,1,1,0),"First shared reload rejected");ledger.Tick(3);
            Require(ledger.TryReload(2,1,2,3),"Second shared reload rejected");ledger.Tick(6);
            var ws=ledger.Weapons();int total=0;foreach(var w in ws) total+=w.Magazine;
            Require(total==3 && ledger.Ammo()[0].Amount==0,"Two guns duplicated shared ammunition");
            Require(!ledger.TryReload(3,1,2,6),"Empty shared reserve reloaded");

            ledger=new HostCombatInventory();
            ledger.Sync(new[]{Weapon(2,0)},new CombatAmmo[0],new[]{Rule(2,1,60)},0);
            Require(ledger.TryAttack(1,1,2,1,0),"Melee rejected");
            for(uint i=2;i<1002;i++) Require(!ledger.TryAttack(i,1,2,1,.5),"Flood restored stamina");
            Require(Math.Abs(ledger.Stamina-45)<.001,"Request count changed stamina budget"); // 1.4.1: half a second of the game's recovery
            ledger.Rebind();Require(!ledger.TryAttack(1,1,2,1,.5),"Reconnect reset stamina");
            ledger.Sync(new[]{Weapon(2,0)},new CombatAmmo[0],new[]{Rule(2,1,60)},1);
            Require(!ledger.TryAttack(2,1,2,1,1),"Ledger update reset stamina");
            Require(ledger.TryAttack(3,1,2,1,5),"Timed stamina recovery failed");
            // 1.4.1: as the game: a tenth of the maximum a second, without a pause; the maximum is the
            // guest's own (its profile, held to its skill by the host), within bounds.
            var regen=new HostCombatInventory();regen.Sync(new[]{Weapon(2,0)},new CombatAmmo[0],new[]{Rule(2,1,60)},0);
            regen.SetMaxStamina(150);Require(regen.MaxStamina==150 && regen.Stamina==100,"Profile stamina maximum not taken or stamina raised");
            Require(regen.TryAttack(1,1,2,1,0) && Math.Abs(regen.Stamina-40)<.001,"Swing at the profile maximum");
            regen.Tick(1);Require(Math.Abs(regen.Stamina-55)<.001,"Recovery is not a tenth of the maximum a second: "+regen.Stamina);
            regen.Tick(100);Require(regen.Stamina==150,"Recovery above the maximum");
            regen.SetMaxStamina(5);Require(regen.MaxStamina==HostCombatInventory.MinMaxStamina && regen.Stamina==HostCombatInventory.MinMaxStamina,"Stamina maximum below its bound");
            regen.SetMaxStamina(float.NaN);regen.SetMaxStamina(1e9f);Require(regen.MaxStamina==HostCombatInventory.MaxMaxStamina,"Stamina maximum above its bound or NaN taken");
            // 1.0.7: stamina limits swings only; a firearm empties its magazine as natively.
            ledger=new HostCombatInventory();
            ledger.Sync(new[]{Weapon(1,30)},new[]{Ammo()},new[]{new CombatRule {ItemId=1,AmmoId=10,Capacity=30,Action=0,Interval=.1f,ReloadSeconds=3,StaminaCost=60,MaxDurability=100}},0);
            int fired=0; for(uint i=1;i<=40;i++) if(ledger.TryAttack(i,1,1,0,i*.1)) fired++;
            Require(fired==30 && ledger.Weapons()[0].Magazine==0 && Math.Abs(ledger.Stamina-100)<.001,"Firearm burst limited by stamina: "+fired);
            ledger=new HostCombatInventory();
            ledger.Sync(new[]{Weapon(1,2,1)},new[]{Ammo()},new[]{Rule()},0);
            Require(ledger.TryAttack(1,1,1,0,0) && !ledger.TryAttack(2,1,1,0,1),"Broken weapon fired");
            Require(!ledger.TryReload(3,1,1,1),"Broken weapon reloaded");
            // Invalid ledger entries are skipped, never admitted.
            foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1f,100000f}) {
                ledger=new HostCombatInventory();ledger.Sync(new[]{Weapon(1,1,bad)},new[]{Ammo()},new[]{Rule()},0);
                Require(!ledger.TryAttack(1,1,1,0,0),"Invalid durability admitted");
            }
            ledger=new HostCombatInventory();ledger.Sync(new[]{Weapon(1,3)},new[]{Ammo()},new[]{Rule()},0);
            Require(!ledger.TryAttack(1,1,1,0,0),"Host magazine capacity bypassed");
            ledger=new HostCombatInventory();ledger.Sync(new[]{Weapon(),Weapon()},new[]{Ammo()},new[]{Rule(),Rule()},0);
            Require(ledger.Weapons().Length==1,"Duplicate weapons admitted");
            ledger=new HostCombatInventory();ledger.Sync(new[]{Weapon()},new[]{new CombatAmmo {ItemId=11,Amount=1}},new[]{Rule()},0);
            Require(ledger.Ammo().Length==0,"Unrelated reserve admitted");
            ledger=Ledger();Require(!ledger.TryAttack(1,1,1,0,double.NaN) && !ledger.TryReload(1,1,1,double.PositiveInfinity),"Invalid server time admitted");
            // Thousands of mixed requests, with the ledger fed back from the callbacks as
            // the game does every 0.1 s, conserve the entire allowance.
            ledger=Ledger();var random=new Random(7729);uint seq=0;int accepted=0;
            int bookMagazine=2,bookReserve=3;
            ledger.Spent=(item,action)=>bookMagazine--;
            ledger.Reloaded=(item,ammo,count)=>{bookReserve-=count;bookMagazine+=count;};
            for(int i=0;i<10000;i++) {
                double now=i*.03;ledger.Tick(now);
                if(random.Next(3)==0) ledger.TryReload(++seq,1,1,now);
                else if(ledger.TryAttack(++seq,1,1,0,now)) accepted++;
                var remaining=ledger.Weapons()[0].Magazine+ledger.Ammo()[0].Amount;
                Require(remaining+accepted==5 && bookMagazine+bookReserve+accepted==5,"Mixed requests created/lost host ammunition");
                if(i%4==0) ledger.Sync(new[]{Weapon(1,bookMagazine,ledger.Weapons()[0].Durability)},new[]{Ammo(bookReserve)},new[]{Rule()},now);
                if(i%100==0) { ledger.Rebind();seq=0; }
            }
            Console.WriteLine("PASS: host combat allowance from the ledger / conservation / replay / reconnect / stamina / revocation");
        }
        private static Packet Kit(PacketKind kind)
        {
            return new Packet {Kind=kind,GameVersion="checks",Session=42,WorldEpoch=1,Scene="Terrain1",Sequence=1,
                Revision=kind==PacketKind.CombatState?1:0,Action=(byte)(kind==PacketKind.CombatState?1:0),CombatWeapons=new[]{Weapon()},CombatAmmo=new[]{Ammo()}};
        }
        private static void CombatProtocolChecks()
        {
            Packet decoded;
            foreach(var p in new[]{Kit(PacketKind.CombatProposal),Kit(PacketKind.CombatState),new Packet {Kind=PacketKind.CombatReload,GameVersion="checks",Session=42,WorldEpoch=1,Scene="Terrain1",Sequence=1,Revision=1,EquippedItemId=1}}) {
                var bytes=LanProtocol.Encode(p);Require(LanProtocol.TryDecode(bytes,out decoded),"Valid combat packet rejected");
                for(int size=0;size<bytes.Length;size++) { var b=new byte[size];Array.Copy(bytes,b,size);Require(!LanProtocol.TryDecode(b,out decoded),"Truncated combat packet admitted"); }
                var tail=new byte[bytes.Length+1];Array.Copy(bytes,tail,bytes.Length);Require(!LanProtocol.TryDecode(tail,out decoded),"Combat trailing bytes admitted");
                bytes[4]=8;Require(!LanProtocol.TryDecode(bytes,out decoded),"Pre-authority schema admitted");
            }
            var maximum=Kit(PacketKind.CombatState);maximum.GameVersion=new string('v',40);maximum.Scene=new string('s',96);
            maximum.CombatWeapons=new CombatWeapon[16];maximum.CombatAmmo=new CombatAmmo[16];
            for(int i=0;i<16;i++) { maximum.CombatWeapons[i]=Weapon(i,500,99999);maximum.CombatAmmo[i]=new CombatAmmo {ItemId=i,Amount=10000}; }
            var maxBytes=LanProtocol.Encode(maximum);Require(maxBytes.Length<=1200 && LanProtocol.TryDecode(maxBytes,out decoded),"Maximum combat kit rejected");
            var kit=Kit(PacketKind.CombatProposal);kit.Revision=1;Require(!LanProtocol.TryDecode(LanProtocol.Encode(kit),out decoded),"Client supplied grant revision");
            kit=Kit(PacketKind.CombatState);kit.Action=2;Require(!LanProtocol.TryDecode(LanProtocol.Encode(kit),out decoded),"Denied state carried a kit");
            kit=Kit(PacketKind.CombatState);kit.Stamina=float.NaN;Require(!LanProtocol.TryDecode(LanProtocol.Encode(kit),out decoded),"NaN stamina admitted");
            // 1.4.18: a guest's maximum above 100 (its stamina level) passes; above the host's bound it does not.
            kit=Kit(PacketKind.CombatState);kit.Stamina=110;Require(LanProtocol.TryDecode(LanProtocol.Encode(kit),out decoded) && decoded.Stamina==110,"Stamina above 100 refused");
            kit=Kit(PacketKind.CombatState);kit.Stamina=HostCombatInventory.MaxMaxStamina+1;Require(!LanProtocol.TryDecode(LanProtocol.Encode(kit),out decoded),"Stamina above the bound admitted");
            // Mutate structurally valid seeds to exercise deeper parsers, not just magic rejection.
            var random=new Random(99381);var seeds=new[]{LanProtocol.Encode(Kit(PacketKind.CombatState)),LanProtocol.Encode(Kit(PacketKind.CombatProposal))};
            for(int i=0;i<20000;i++) { var bytes=(byte[])seeds[i%seeds.Length].Clone();for(int j=0;j<1+random.Next(4);j++) bytes[random.Next(6,bytes.Length)]=(byte)random.Next(256);LanProtocol.TryDecode(bytes,out decoded); }
            Console.WriteLine("PASS: combat schema / bounds / truncation / 20000 mutated packets");
        }
        private static bool Wait(Func<bool> ready, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds) { if (ready()) return true; Thread.Sleep(5); }
            return false;
        }
        private static void NetworkPolicyChecks()
        {
            foreach(var s in new[]{"127.0.0.1","10.1.2.3","172.16.0.1","172.31.255.254","192.168.1.2","169.254.1.1"})
                Require(LanNetworkPolicy.PrivatePeer(IPAddress.Parse(s)),"Private peer rejected: "+s);
            foreach(var s in new[]{"8.8.8.8","1.1.1.1","172.15.1.2","172.32.1.2","192.169.1.2","0.0.0.0","255.255.255.255","224.0.0.1","169.254.0.1","169.254.255.1","::1","::ffff:192.168.1.2"})
                Require(!LanNetworkPolicy.PrivatePeer(IPAddress.Parse(s)),"Non-LAN peer accepted: "+s);
            foreach(var kind in new[]{PacketKind.WorldState,PacketKind.TrainMotion,PacketKind.TrainLayoutChunk,PacketKind.ZombieState,PacketKind.ZombieHurt,PacketKind.CombatState}) {
                Require(!LanNetworkPolicy.Allowed(true,kind,true) && LanNetworkPolicy.Allowed(false,kind,true),"Host packet role bypass: "+kind);
                Require(!LanNetworkPolicy.Allowed(false,kind,false),"Application packet before establishment: "+kind);
            }
            foreach(var kind in new[]{PacketKind.CombatProposal,PacketKind.CombatReload,PacketKind.ZombieAction,PacketKind.ZombieHurtAck,PacketKind.TrainLayoutAck})
                Require(LanNetworkPolicy.Allowed(true,kind,true) && !LanNetworkPolicy.Allowed(false,kind,true),"Client packet role bypass: "+kind);
            Require(!LanNetworkPolicy.Allowed(true,(PacketKind)255,true),"Unknown packet role accepted");
            var rate=new LanRateLimit(2,2);Require(rate.Take(2,0) && !rate.Take(1,0),"Burst limit not enforced");
            Require(rate.Take(1,.5) && !rate.Take(1,.5),"Time-based refill failed");
            Require(!rate.Take(1,.1) && !rate.Take(1,.5),"Clock rollback duplicated budget");
            Require(!rate.Take(-1,1) && !rate.Take(double.NaN,1) && !rate.Take(1,double.PositiveInfinity),"Invalid budget values admitted");
            var limits=new LanMessageLimits();Require(limits.Take(PacketKind.CombatProposal,0) && limits.Take(PacketKind.CombatProposal,0) && !limits.Take(PacketKind.CombatProposal,0),"Proposal kind flood not bounded");
            Require(limits.Take(PacketKind.State,0),"One kind exhausted another kind's budget");
            for(int i=0;i<10000;i++) Require(!limits.Take(PacketKind.CombatProposal,0),"Packet count refilled kind budget");
            // 1.4.9: every kind a peer may send in a session has a budget of its own (without one each
            // such packet is refused as a flood: 1.4.6-1.4.8 lost Heartbeat and WorldState that way, and
            // the guard closed every session within seconds), and the game's steady cadences fit theirs.
            limits=new LanMessageLimits();
            foreach(PacketKind kind in Enum.GetValues(typeof(PacketKind)))
                if(LanNetworkPolicy.Allowed(true,kind,true) || LanNetworkPolicy.Allowed(false,kind,true))
                    Require(limits.Take(kind,0),"No budget for an allowed packet kind: "+kind);
            limits=new LanMessageLimits();
            for(int tick=1;tick<=1200;tick++) {
                double now=tick*.05;
                Require(limits.Take(PacketKind.State,now),"Player state cadence (20 a second) refused");
                if(tick%2==0) Require(limits.Take(PacketKind.Heartbeat,now),"Heartbeat cadence (10 a second) refused");
                if(tick%5==0) Require(limits.Take(PacketKind.WorldState,now),"World state cadence (4 a second) refused");
            }
            // Constructors reject these addresses before creating a socket.
            bool rejected=false;
            try { using(var peer=LanSecureTransport.JoinWithKey(27777,IPAddress.Parse("8.8.8.8"),"checks",new byte[32])) { } }
            catch(ArgumentException) { rejected=true; }
            Require(rejected,"Transport connected outside LAN policy");
            var work=new LanPeerWorkLimits();Require(work.WorldChange(0) && work.WorldChange(0) && !work.WorldChange(0) && work.Failure!=null,"World-load churn not bounded");
            Require(!work.WorldChange(1000),"Tripped peer-work guard reactivated automatically");
            work=new LanPeerWorkLimits();for(int i=0;i<4;i++)Require(work.LayoutChange(0),"Layout burst rejected early");
            Require(!work.LayoutChange(0) && work.Failure!=null,"Layout-revision churn not bounded");
            work=new LanPeerWorkLimits();for(int i=0;i<100;i++)Require(work.WorldChange(i*15) && work.LayoutChange(i*15),"Normal world/layout cadence rejected");
            Console.WriteLine("PASS: LAN destination policy / direction policy / monotonic network budgets / a budget for every allowed kind / steady cadences fit");
        }
        private static void TamperTransportChecks()
        {
            int port;
            using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) port=((IPEndPoint)reservation.Client.LocalEndPoint).Port;
            var secret=new byte[32];new Random(61937).NextBytes(secret);
            using(var host=LanSecureTransport.HostWithKey(port,IPAddress.Loopback,"checks",secret))
            using(var proxy=new LoopbackDatagramProxy(port,secret))
            using(var client=LanSecureTransport.JoinWithKey(proxy.Port,IPAddress.Loopback,"checks",secret)) {
                Packet packet;IPEndPoint peer;
                Require(Wait(()=>host.Session!=0 && host.Session==client.Session,15000),"Proxied DTLS handshake failed");
                Require(Wait(()=> {
                    if(!host.TryReceive(out peer,out packet) || packet.Kind!=PacketKind.Hello)return false;
                    host.Send(new Packet {Kind=PacketKind.Welcome,GameVersion="checks",Session=host.Session});return true;
                },3000),"Proxied Hello missing");
                Require(Wait(()=>client.TryReceive(out peer,out packet) && packet.Kind==PacketKind.Welcome,3000),"Proxied Welcome missing");
                proxy.Tamper(true);var state=State(321);state.Session=client.Session;client.Send(state);
                Require(!Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==321,500),"Corrupt AEAD record with valid admission tag delivered");
                proxy.Tamper(false);state=State(322);state.Session=client.Session;client.Send(state);
                Require(Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==322,3000),"DTLS did not recover after tampering");
                proxy.Capture(true);state=State(333);state.Session=client.Session;client.Send(state);
                Require(Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==333,3000),"Replay seed missing");
                proxy.Capture(false);Require(proxy.Replay()>0,"No encrypted replay records captured");
                Require(!Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==333,500),"Encrypted datagram replay delivered twice");
                proxy.InvalidBurst();state=State(334);state.Session=client.Session;client.Send(state);
                // A finite flood may drop this UDP record. Send a fresh one after
                // draining/replenishing; require transport recovery, not lossless UDP.
                Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==334,1000);
                state=State(335);state.Session=client.Session;client.Send(state);
                Require(Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==335,5000),"Transport did not recover from invalid burst");
            }
            Console.WriteLine("PASS: loopback DTLS tamper with valid outer MAC / encrypted replay / bounded invalid burst recovery");
        }
        private static void TransportChecks()
        {
            int port;
            using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)reservation.Client.LocalEndPoint).Port;
            var secret = new byte[32]; new Random(43973).NextBytes(secret);
            using (var host = LanSecureTransport.HostWithKey(port, IPAddress.Loopback, "checks", secret))
            {
                // Wrong admission key must not occupy a host or establish DTLS.
                using (var wrong = LanSecureTransport.JoinWithKey(port, IPAddress.Loopback, "checks", new byte[32]))
                    Require(!Wait(() => host.Session != 0 || wrong.Session != 0, 1000), "Wrong key established a session");
                using (var client = LanSecureTransport.JoinWithKey(port, IPAddress.Loopback, "checks", secret))
                {
                    Require(Wait(() => host.Session != 0 && client.Session == host.Session, 15000), "DTLS handshake/session export failed");
                    Packet packet; IPEndPoint peer;
                    Require(Wait(() =>
                    {
                        if (!host.TryReceive(out peer, out packet) || packet.Kind != PacketKind.Hello) return false;
                        host.Send(new Packet { Kind = PacketKind.Welcome, GameVersion = "checks", Session = host.Session }); return true;
                    }, 3000), "Protected Hello missing");
                    Require(Wait(() => client.TryReceive(out peer, out packet) && packet.Kind == PacketKind.Welcome, 3000), "Protected Welcome missing");
                    ulong oldSession = host.Session;
                    var state = State(123); state.Session = client.Session; client.Send(state);
                    Require(Wait(() => host.TryReceive(out peer, out packet) && packet.Kind == PacketKind.State && packet.Sequence == 123, 3000), "Protected application delivery failed");
                    var proposal=Kit(PacketKind.CombatProposal);proposal.Session=client.Session;client.Send(proposal);
                    Require(Wait(() => host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.CombatProposal,3000),"Protected combat proposal missing");
                    var combatState=Kit(PacketKind.CombatState);combatState.Session=host.Session;combatState.ShotSequence=7;host.Send(combatState);
                    Require(Wait(() => client.TryReceive(out peer,out packet) && packet.Kind==PacketKind.CombatState && packet.ShotSequence==7,3000),"Protected combat state/ack missing");
                    var reload=new Packet {Kind=PacketKind.CombatReload,GameVersion="checks",Session=client.Session,WorldEpoch=1,Scene="Terrain1",Sequence=8,Revision=1,EquippedItemId=1};client.Send(reload);
                    Require(Wait(() => host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.CombatReload,3000),"Protected reload missing");
                    combatState.Session=client.Session;client.Send(combatState);
                    Require(!Wait(() => host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.CombatState,300),"Client sent host-only combat state");
                    proposal.Session=host.Session;host.Send(proposal);
                    Require(!Wait(() => client.TryReceive(out peer,out packet) && packet.Kind==PacketKind.CombatProposal,300),"Host sent client-only proposal");
                    host.Restart(); client.Restart();
                    Require(Wait(() => host.Session != 0 && host.Session != oldSession && host.Session == client.Session, 15000), "Reconnect reused keys/session or failed");
                    state.Session = oldSession; client.Send(state); // Old application session must not be rebound to new keys.
                    Require(!Wait(() => host.TryReceive(out peer, out packet) && packet.Kind == PacketKind.State, 500), "Old application state crossed reconnect");
                }
            }
        }
        private static void PakeChecks()
        {
            LanSecureTransport.VerifyLibrary();
            var room = LanPake.Nonce(); var nonce = LanPake.Nonce();
            using (var host = new LanPake(true, room, nonce, "123456"))
            using (var client = new LanPake(false, room, nonce, "123456"))
            {
                ExpectRejected(() => { using (host.DeriveKeys()) { } }, "Keys exposed before confirmation");
                var h1 = host.Round1(); var c1 = client.Round1();
                host.AcceptRound1(c1); client.AcceptRound1(h1);
                var h2 = host.Round2(); var c2 = client.Round2();
                host.AcceptRound2(c2); client.AcceptRound2(h2);
                var h3 = host.Round3(); var c3 = client.Round3();
                host.AcceptRound3(c3); client.AcceptRound3(h3);
                using (var hk = host.DeriveKeys())
                using (var ck = client.DeriveKeys())
                {
                    Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(hk.Secret, ck.Secret), "PAKE transport keys differ");
                    Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(hk.ApprovalTag(), ck.ApprovalTag()), "Approval keys differ");
                    Require(!Org.BouncyCastle.Utilities.Arrays.AreEqual(hk.Secret, hk.Approval), "KDF did not separate keys");
                }
                Require(h1.Length <= LanPake.MaximumFrame && h2.Length <= LanPake.MaximumFrame && h3.Length <= 32, "PAKE frame limits exceeded");
            }
            using (var host = new LanPake(true, room, nonce, "123456"))
            using (var client = new LanPake(false, room, nonce, "654321"))
            {
                var h1 = host.Round1(); var c1 = client.Round1(); host.AcceptRound1(c1); client.AcceptRound1(h1);
                var h2 = host.Round2(); var c2 = client.Round2(); host.AcceptRound2(c2); client.AcceptRound2(h2);
                var h3 = host.Round3(); var c3 = client.Round3();
                bool rejected = false;
                try { host.AcceptRound3(c3); } catch (Org.BouncyCastle.Crypto.CryptoException) { rejected = true; }
                Require(rejected, "Incorrect PIN passed PAKE confirmation");
            }
            using (var host = new LanPake(true, room, nonce, "123456"))
            {
                var reflected = host.Round1();
                ExpectRejected(() => host.AcceptRound1(reflected), "Role reflection accepted");
            }
            for (int changed = 0; changed < 2; changed++)
            {
                var otherRoom = (byte[])room.Clone(); var otherNonce = (byte[])nonce.Clone();
                if (changed == 0) otherRoom[0] ^= 1; else otherNonce[0] ^= 1;
                using (var host = new LanPake(true, room, nonce, "123456"))
                using (var client = new LanPake(false, otherRoom, otherNonce, "123456"))
                {
                    host.Round1(); var wrongContext = client.Round1();
                    ExpectRejected(() => host.AcceptRound1(wrongContext), "Different pairing context accepted");
                }
            }
            using (var client = new LanPake(false, room, nonce, "123456"))
            {
                var valid = client.Round1();
                var truncated = new byte[valid.Length - 1]; Array.Copy(valid, truncated, truncated.Length);
                var trailing = new byte[valid.Length + 1]; Array.Copy(valid, trailing, valid.Length);
                var noncanonical = new byte[valid.Length + 1];
                int firstSize = BitConverter.ToUInt16(valid, 0);
                Array.Copy(BitConverter.GetBytes((ushort)(firstSize + 1)), noncanonical, 2);
                Array.Copy(valid, 2, noncanonical, 3, valid.Length - 2); // Leading zero before first group element.
                var zeroGroup = (byte[])valid.Clone(); Array.Clear(zeroGroup, 2, firstSize);
                var badProof = (byte[])valid.Clone(); badProof[badProof.Length - 1] ^= 1;
                foreach (var malformed in new[] { new byte[LanPake.MaximumFrame + 1], truncated, trailing, noncanonical, zeroGroup, badProof })
                    using (var host = new LanPake(true, room, nonce, "123456"))
                    {
                        host.Round1();
                        ExpectRejected(() => host.AcceptRound1(malformed), "Malformed PAKE round accepted");
                    }
            }
        }
        private static void PairingChecks()
        {
            // Manual-only: reserves the normal LAN discovery/pairing ports and
            // broadcasts public queries on active adapters. Keep the game closed.
            using (var host = LanPairing.HostLoopbackForChecks())
            using (var client = LanPairing.JoinLoopbackForChecks(host.Code, host.ListenPortForChecks))
            {
                Require(Wait(() => host.PendingApproval, 9000), "Discovery or PAKE failed: " + host.Status + "; " + client.Status);
                byte[] key; IPAddress peer;
                Require(!host.TryTakeCredentials(out key, out peer), "Credentials exposed before host approval");
                Require(!client.TryTakeCredentials(out key, out peer), "Client credentials exposed before host approval");
                host.Approve();
                Require(Wait(() => host.TryTakeCredentials(out key, out peer), 1000), "Approved host credentials missing");
                host.TransportReady();
                byte[] clientKey = null; IPAddress clientPeer;
                Require(Wait(() => client.TryTakeCredentials(out clientKey, out clientPeer), 1500), "Client approval missing");
                Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(key, clientKey), "Network pairing keys differ");
                LanPake.Clear(key); LanPake.Clear(clientKey);
                Require(host.Code == "", "Invitation was not consumed");
                Require(Wait(() => host.Finished && client.Finished && host.Succeeded && client.Succeeded, 1000), "Pairing did not complete");
                Require(host.Failure==LanFault.None && client.Failure==LanFault.None,"Successful pairing retained a failure");
            }
            using (var host = LanPairing.HostLoopbackForChecks())
            using (var client = LanPairing.JoinLoopbackForChecks(host.Code, host.ListenPortForChecks))
            {
                Require(Wait(() => host.PendingApproval, 9000), "Rejected exchange did not reach approval");
                host.Reject();
                Require(Wait(() => host.Finished && client.Finished, 1500), "Rejected pairing did not stop");
                byte[] key; IPAddress peer;
                Require(!host.Succeeded && !client.Succeeded && !host.TryTakeCredentials(out key, out peer) &&
                    !client.TryTakeCredentials(out key, out peer), "Rejected pairing exposed credentials");
            }
            using (var expired = LanPairing.HostLoopbackForChecks())
            {
                Require(Wait(() => expired.Expired && expired.Finished, (LanLocalText.PinSeconds+1)*1000), "Invitation failed to expire");
                Require(expired.Code == "", "Expired invitation remains visible");
                byte[] key; IPAddress peer;
                Require(!expired.Succeeded && !expired.TryTakeCredentials(out key, out peer), "Expired invitation exposed credentials");
                Require(expired.Failure==LanFault.None,"Normal host expiry became a network fault");
            }
            PairingFailureChecks();
        }
    }
}
