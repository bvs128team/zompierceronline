using System;

namespace ZompiercerLAN
{
    // 1.4.6: the player's State carries the sender's clock and its dash (packet schema 36), and
    // goes 20 times a second. 1.4.7: how it moves and its throws (schema 37). 1.4.8: its presses of the
    // use key (schema 38).
    internal static partial class Program
    {
        private static void MotionChecks()
        {
            Packet decoded;
            var state = State(77);
            state.SentAt = 0xFEDCBA98u; state.Dash = (byte)(LanProtocol.MaxDashCount << 3 | 7);
            state.Motion = (byte)(LanProtocol.MotionRun | LanProtocol.MotionAir | LanProtocol.MotionSwim | LanProtocol.MotionCock | LanProtocol.MaxUse << LanProtocol.UseShift);
            state.Throws = 255; state.Interacts = 254;
            var bytes = LanProtocol.Encode(state);
            Require(LanProtocol.TryDecode(bytes, out decoded) && decoded.SentAt == 0xFEDCBA98u && decoded.Dash == state.Dash && decoded.Sequence == 77 &&
                decoded.Motion == state.Motion && decoded.Throws == 255 && decoded.Interacts == 254, "State clock, dash, motion, throws or presses lost");
            for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated state accepted"); }
            var tail = new byte[bytes.Length + 1]; Array.Copy(bytes, tail, bytes.Length);
            Require(!LanProtocol.TryDecode(tail, out decoded), "State trailing bytes accepted");
            // The dash, the motion, the throws and the presses are the last four bytes: a direction without
            // any dash yet is malformed, every other value is a dash.
            for (int dash = 0; dash < 256; dash++)
            {
                var t = (byte[])bytes.Clone(); t[t.Length - 4] = (byte)dash;
                bool valid = dash >> 3 != 0 || (dash & 7) == 0;
                Require(LanProtocol.TryDecode(t, out decoded) == valid && LanProtocol.ValidDash((byte)dash) == valid && (!valid || decoded.Dash == dash), "Dash " + dash + " decoded wrongly");
            }
            // Motion: the top bit unused, the use kind at most MaxUse; every count of throws is one.
            for (int motion = 0; motion < 256; motion++)
            {
                var t = (byte[])bytes.Clone(); t[t.Length - 3] = (byte)motion;
                bool valid = (motion & 0x80) == 0 && motion >> LanProtocol.UseShift <= LanProtocol.MaxUse;
                Require(LanProtocol.TryDecode(t, out decoded) == valid && LanProtocol.ValidMotion((byte)motion) == valid && (!valid || decoded.Motion == motion), "Motion " + motion + " decoded wrongly");
                t = (byte[])bytes.Clone(); t[t.Length - 2] = (byte)motion;
                Require(LanProtocol.TryDecode(t, out decoded) && decoded.Throws == motion, "Throws " + motion + " rejected");
                t = (byte[])bytes.Clone(); t[t.Length - 1] = (byte)motion;
                Require(LanProtocol.TryDecode(t, out decoded) && decoded.Interacts == motion, "Presses " + motion + " rejected");
            }
            state.Dash = 3;
            try { LanProtocol.Encode(state); Require(false, "A direction without a dash was encoded"); }
            catch (System.IO.InvalidDataException) { }
            state.Dash = 0; state.Motion = (byte)((LanProtocol.MaxUse + 1) << LanProtocol.UseShift);
            try { LanProtocol.Encode(state); Require(false, "An unknown use was encoded"); }
            catch (System.IO.InvalidDataException) { }
            // Twenty states a second, with room for a delivery burst; a flood stays bounded.
            var limits = new LanMessageLimits(); int states = 0;
            for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.State, 0)) states++;
            Require(states == 48, "State bursts not bounded");
            for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.State, 1)) states++;
            Require(states == 88, "State rate not 40 a second");
            Console.WriteLine("PASS: player state (sender clock, dash, motion, throws and use-key presses roundtrip, truncation and trailing bytes, every dash, motion, throws and presses byte, 20 a second within a bounded budget)");
        }
    }
}
