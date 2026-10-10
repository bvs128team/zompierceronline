using System;

namespace ZompiercerLAN
{
    // 1.1.7: boss arena gates close only when both players are inside.
    internal static partial class Program
    {
        private static void ArenaChecks()
        {
            // The mall gate trigger in the arena frame: 7.3 m across, from 0.15 to 2.8 m behind the gates.
            var zone = new LanArenaZone(-4.2f, 4.2f, -3f, 3f, -.35f, 3.3f);
            var t = new LanArenaZone.Track();
            zone.Move(t, 0f, 0f, -20f);
            Require(t.Have && !t.Inside, "Outside start counted as inside");
            zone.Move(t, 0f, 0f, -2f);
            Require(!t.Inside, "Approach to the gates counted as inside");
            // A sprint straight through the narrow zone between two positions still counts.
            zone.Move(t, .5f, 0f, 7f);
            Require(t.Inside, "Run through the gates missed");
            zone.Move(t, 2f, 0f, 15f); zone.Move(t, -3f, 0f, 22f); zone.Move(t, -6f, 0f, 34f);
            Require(t.Inside, "Walk inside the arena lost");
            // Back out through the gates.
            zone.Move(t, -3f, 0f, 22f); zone.Move(t, 0f, 0f, 12f); zone.Move(t, 0f, 0f, 4f); zone.Move(t, 0f, 0f, -.5f);
            Require(t.Inside, "Standing at the gates counted as out");
            zone.Move(t, 0f, 0f, -3f);
            Require(!t.Inside, "Leaving through the gates missed");
            // Inside the arena's half-plane but never through the gates (around the walls).
            var around = new LanArenaZone.Track();
            zone.Move(around, 30f, 0f, -5f); zone.Move(around, 30f, 0f, 2f); zone.Move(around, 25f, 0f, 9f); zone.Move(around, 20f, 0f, 15f);
            Require(!around.Inside, "Arena entered around the gates");
            // Another floor above the gates does not count.
            var above = new LanArenaZone.Track();
            zone.Move(above, 0f, 8f, -4f); zone.Move(above, 0f, 8f, 6f);
            Require(!above.Inside, "Arena entered on another floor");
            // A jump (death, respawn, travel) counts only where it lands.
            var jump = new LanArenaZone.Track();
            zone.Move(jump, 0f, 0f, -3f); zone.Move(jump, 0f, 0f, 3f); zone.Move(jump, 0f, 0f, 12f); zone.Move(jump, 0f, 0f, 20f);
            Require(jump.Inside, "Entry before the jump lost");
            zone.Move(jump, 40f, 0f, 30f);
            Require(!jump.Inside, "Respawn far away kept inside");
            zone.Move(jump, 0f, 0f, 1f);
            Require(jump.Inside, "Landing in the gates zone not counted");
            zone.Move(jump, float.NaN, 0f, 0f);
            Require(jump.Inside && jump.Z == 1f, "Invalid position accepted");
            var reset = new LanArenaZone.Track { Inside = true, Have = true };
            reset.Reset();
            Require(!reset.Inside && !reset.Have, "Track reset");
            bool empty = false;
            try { new LanArenaZone(1f, 0f, 0f, 1f, 0f, 1f); } catch (ArgumentException) { empty = true; }
            Require(empty, "Empty arena zone accepted");
            // The gates: pending (the host reached the trap), not closed yet, everyone inside.
            Require(!LanArenaZone.ShouldClose(true, false, true, true, false), "Gates closed without the guest");
            Require(LanArenaZone.ShouldClose(true, false, true, true, true), "Gates kept open with both inside");
            Require(!LanArenaZone.ShouldClose(true, false, false, true, true), "Gates closed without the host");
            Require(LanArenaZone.ShouldClose(true, false, true, false, false), "Gates kept open without a guest in this world");
            Require(!LanArenaZone.ShouldClose(false, false, true, true, true) && !LanArenaZone.ShouldClose(true, true, true, true, true), "Gates closed twice or before the trap");
            Console.WriteLine("PASS: boss arena entry through the gates / around the walls / other floor / jumps / gate rule");
        }
    }
}
