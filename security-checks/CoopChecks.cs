using System;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.2.2 (schema 30): explosions of the host's world for the guest, sleeping together, and the
    // rainwater tank of the host's train (work kinds 6-8).
    internal static partial class Program
    {
        private static void CoopChecks()
        {
            Packet decoded = null;
            Func<Packet, Packet> world = p => { p.GameVersion = "checks"; p.Session = 42; p.WorldEpoch = 3; p.Sequence = 9; p.Scene = "Location8"; return p; };
            var blast = world(new Packet { Kind = PacketKind.Explosion, Action = LanProtocol.ExplosionBlast, Text = "GrenadeMK2Thrown", X = 1, Y = 2, Z = -3 });
            var sleep = world(new Packet { Kind = PacketKind.Sleep, Action = LanProtocol.SleepWait, Revision = LanProtocol.MaxSleepHours });
            foreach (var p in new[] { blast, sleep })
            {
                var bytes = LanProtocol.Encode(p);
                Require(LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == p.Kind && decoded.Sequence == 9, "Valid packet rejected: " + p.Kind);
                for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated packet accepted: " + p.Kind); }
            }
            LanProtocol.TryDecode(LanProtocol.Encode(blast), out decoded);
            Require(decoded.Text == "GrenadeMK2Thrown" && decoded.Z == -3 && decoded.Action == LanProtocol.ExplosionBlast, "Explosion fields");
            LanProtocol.TryDecode(LanProtocol.Encode(sleep), out decoded);
            Require(decoded.Revision == LanProtocol.MaxSleepHours && decoded.Action == LanProtocol.SleepWait, "Sleep fields");
            Func<Action<Packet>, Packet, bool> rejected = (change, source) =>
            {
                Packet copy; LanProtocol.TryDecode(LanProtocol.Encode(source), out copy);
                copy.GameVersion = "checks"; change(copy);
                try { return !LanProtocol.TryDecode(LanProtocol.Encode(copy), out decoded); }
                catch (System.IO.InvalidDataException) { return true; }
            };
            var bad = new[]
            {
                rejected(p => p.Action = 0, blast), rejected(p => p.Action = LanProtocol.ExplosionBlast + 1, blast), rejected(p => p.Text = "", blast),
                rejected(p => p.Text = new string('a', LanProtocol.MaxExplosiveName + 1), blast), rejected(p => p.Text = "Barrel02(Clone)", blast),
                rejected(p => p.Text = "Bomb/..", blast), rejected(p => p.Text = "Бочка", blast), rejected(p => p.X = float.NaN, blast),
                rejected(p => p.WorldEpoch = 0, blast), rejected(p => p.Scene = "", blast), rejected(p => p.Sequence = 0, blast),
                rejected(p => p.Revision = 0, sleep), rejected(p => p.Revision = LanProtocol.MaxSleepHours + 1, sleep), rejected(p => p.Action = 0, sleep),
                rejected(p => p.Action = LanProtocol.SleepStart + 1, sleep), rejected(p => { p.Action = LanProtocol.SleepCancel; p.Revision = 3; }, sleep),
                rejected(p => p.WorldEpoch = 0, sleep),
            };
            Require(bad.All(ok => ok), "Malformed explosion or sleep packet accepted: #" + Array.IndexOf(bad, false));
            sleep.Action = LanProtocol.SleepCancel; sleep.Revision = 0;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(sleep), out decoded) && decoded.Action == LanProtocol.SleepCancel, "Getting up rejected");
            sleep.Action = LanProtocol.SleepStart; sleep.Revision = 1;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(sleep), out decoded) && decoded.Revision == 1, "Sleep start rejected");
            Require(!LanNetworkPolicy.Allowed(true, PacketKind.Explosion, true) && LanNetworkPolicy.Allowed(false, PacketKind.Explosion, true) && LanProtocol.WorldBound(PacketKind.Explosion), "Explosion direction");
            Require(LanNetworkPolicy.Allowed(true, PacketKind.Sleep, true) && LanNetworkPolicy.Allowed(false, PacketKind.Sleep, true) && !LanNetworkPolicy.Allowed(true, PacketKind.Sleep, false) &&
                LanProtocol.WorldBound(PacketKind.Sleep), "Sleep directions");
            var limits = new LanMessageLimits(); int blasts = 0, sleeps = 0;
            for (int i = 0; i < 1000; i++) { if (limits.Take(PacketKind.Explosion, 0)) blasts++; if (limits.Take(PacketKind.Sleep, 0)) sleeps++; }
            Require(blasts == 20 && sleeps == 8, "Explosion or sleep bursts not bounded");

            // Rainwater: car-local, no tool, bottles in escrow (none for a reading), at most 50.
            float x, y, z; int tool, extra; object[] escrow;
            Require(LanStorage.DecodeWork(LanStorage.WorkPayload(LanStorage.WorkWater, 1, 2, 3, -1, 2, Item(10, 12)), out x, out y, out z, out tool, out extra, out escrow) == LanStorage.WorkWater &&
                extra == 2 && (int)escrow[1] == 12, "Fill bottles roundtrip");
            Require(LanStorage.DecodeWork(LanStorage.WorkPayload(LanStorage.WorkPourWater, 1, 2, 3, -1, 0, Item(61, 1)), out x, out y, out z, out tool, out extra, out escrow) == LanStorage.WorkPourWater, "Pour roundtrip");
            Require(LanStorage.DecodeWork(LanStorage.WorkPayload(LanStorage.WorkWaterLevel, 1, 2, 3, -1, 7, null), out x, out y, out z, out tool, out extra, out escrow) == LanStorage.WorkWaterLevel && escrow == null, "Tank reading roundtrip");
            foreach (var payload in new[] {
                new object[] { LanStorage.WorkWater, 1f, 2f, 3f, -1, 2, null }, new object[] { LanStorage.WorkWaterLevel, 1f, 2f, 3f, -1, 2, Item(10, 1) },
                new object[] { LanStorage.WorkWater, 1f, 2f, 3f, 5, 2, Item(10, 1) }, new object[] { LanStorage.WorkWater, 1f, 2f, 3f, -1, LanProtocol.MaxCars, Item(10, 1) },
                new object[] { LanStorage.WorkWater, 300f, 2f, 3f, -1, 0, Item(10, 1) }, new object[] { LanStorage.WorkWater, 1f, 2f, 3f, -1, 0, Item(10, LanStorage.MaxWaterBottles + 1) },
                new object[] { LanStorage.WorkWaterLevel + 1, 1f, 2f, 3f, -1, 0, null } })
                ExpectRejected(() => LanStorage.DecodeWork(LanValueCodec.Encode(payload), out x, out y, out z, out tool, out extra, out escrow), "Invalid water work accepted");
            Console.WriteLine("PASS: explosions, sleeping together and rainwater (packet shapes and bounds, prefab names, hours, directions, rate limits, tank work kinds and escrow)");
        }
    }
}
