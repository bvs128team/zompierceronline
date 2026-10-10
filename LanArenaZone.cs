using System;

namespace ZompiercerLAN
{
    // 1.1.7: who is inside a boss arena. Positions come in the arena's own frame (metres:
    // x across the gates, y up, z from the gate line into the arena). The zone is the game's
    // trigger right behind the gates: a player who passed through it is inside, one who went
    // back more than OutsideBy beyond the gate line is out again, and a jump (death, respawn,
    // travel) counts only where it lands. Pure logic, tested by security-checks.
    internal sealed class LanArenaZone
    {
        internal const float OutsideBy = 1f, Jump = 15f, Step = .25f;
        private readonly float _minX, _maxX, _minY, _maxY, _minZ, _maxZ;

        internal sealed class Track
        {
            internal bool Inside, Have;
            internal float X, Y, Z;
            internal void Reset() { Inside = false; Have = false; }
        }

        internal LanArenaZone(float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
        {
            if (!(minX <= maxX && minY <= maxY && minZ <= maxZ)) throw new ArgumentException("Empty arena zone");
            _minX = minX; _maxX = maxX; _minY = minY; _maxY = maxY; _minZ = minZ; _maxZ = maxZ;
        }

        internal bool InZone(float x, float y, float z)
        { return x >= _minX && x <= _maxX && y >= _minY && y <= _maxY && z >= _minZ && z <= _maxZ; }

        internal static bool Outside(float z) { return z < -OutsideBy; }

        // One new position of a player; the path from the last one is checked in short steps,
        // so a fast run through the narrow zone is not missed between two positions.
        internal void Move(Track track, float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return;
            float dx = x - track.X, dy = y - track.Y, dz = z - track.Z;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!track.Have || distance > Jump) { track.Have = true; track.Inside = InZone(x, y, z); }
            else
            {
                int steps = Math.Max(1, (int)Math.Ceiling(distance / Step));
                for (int i = 1; i <= steps; i++)
                {
                    float k = (float)i / steps;
                    float px = track.X + dx * k, py = track.Y + dy * k, pz = track.Z + dz * k;
                    if (InZone(px, py, pz)) track.Inside = true;
                    else if (Outside(pz)) track.Inside = false;
                }
            }
            track.X = x; track.Y = y; track.Z = z;
        }

        // The gates close once the trap was reached and every player of the session is inside.
        internal static bool ShouldClose(bool pending, bool activated, bool hostInside, bool guestRequired, bool guestInside)
        { return pending && !activated && hostInside && (!guestRequired || guestInside); }
    }
}
