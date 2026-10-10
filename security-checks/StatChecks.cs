using System;
using System.Collections.Generic;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.3.0: the host holds the guest's level, experience, health and needs to what it can explain.
    internal static partial class Program
    {
        // Item 30 is food (hunger 40), 31 a medkit (heal 60), 32 a buffed drink; skills go up to 10.
        private sealed class StatRules : ILedgerRules, IStatRules
        {
            public LedgerItemRule Item(int id) { return new LedgerItemRule(); }
            public float[] Restores(int id) { return id == 30 ? new[] { 0f, 40f, 0f, 0f } : id == 31 ? new[] { 60f, 0f, 0f, 0f } : null; }
            public bool HasBuffs(int id) { return id == 32; }
            public int SkillMax(int skill) { return 10; }
        }

        // A consistent character: level 2 (factor 1.5: 300 to level 3), 10 xp, one free point.
        private static object[] StatProfile(object[] backpack = null)
        {
            var root = LedgerProfile(backpack ?? new object[] { Item(30, 3), Item(31, 1), Item(32, 1) }, new object[0], new int[0]);
            var c = (object[])((object[])root[12]).Clone();
            var s = (object[])((object[])c[1]).Clone();
            s[0] = 100f; s[1] = 80f; s[2] = 100f; s[3] = 50f; s[4] = 100f; s[5] = 50f; s[6] = 100f; s[7] = 50f; s[8] = 100f; s[9] = 50f;
            s[10] = 2; s[11] = 300f; s[12] = 10f; s[13] = 1; s[14] = 1; s[15] = 1; s[16] = 1; s[17] = 1; s[18] = 1f; s[19] = 1f;
            c[1] = s; c[6] = new object[] { 2, 10f, 300f, 1.5f, 1, 1, 1, 1, 1 }; c[7] = 80f;
            root[12] = c;
            return root;
        }
        private static object[] Stats(object[] root) { return (object[])((object[])root[12])[1]; }
        private static object[] Level(object[] root) { return (object[])((object[])root[12])[6]; }
        private static object[] WithStats(object[] root, Action<object[], object[]> change)
        {
            var copy = LanValueCodec.Decode(LanValueCodec.Encode(root));
            change(Stats(copy), Level(copy));
            return copy;
        }
        private static void SetXp(object[] s, object[] l, int level, float xp, float max, int perks, int health = 1)
        {
            s[10] = level; s[12] = xp; s[11] = max; s[13] = perks; s[14] = health;
            l[0] = level; l[1] = xp; l[2] = max; l[4] = perks; l[5] = health;
        }

        private static void StatChecks()
        {
            double clock = 0;
            Func<LanGuestStats> fresh = () => new LanGuestStats(new StatRules()) { Clock = () => clock };
            var start = StatProfile();
            var reasons = new List<string>();
            Func<LanGuestStats, object[], object[], bool> check = (stats, before, after) => { reasons.Clear(); return stats.Check(before, after, reasons); };

            // 1. Nothing new, needs falling: accepted.
            var g = fresh(); check(g, start, start);
            var falling = WithStats(start, (s, l) => { s[5] = 40f; s[7] = 30f; s[1] = 60f; });
            Require(check(g, start, falling), "Falling needs refused");
            // 2. Experience the host never credited: back to the last accepted.
            var cheat = WithStats(start, (s, l) => SetXp(s, l, 2, 60f, 300f, 1));
            Require(!check(fresh(), start, cheat) && (float)Stats(cheat)[12] == 10f && (float)Level(cheat)[1] == 10f && reasons.Contains("level/experience"), "Uncredited experience accepted");
            // 3. Credited (with the game's buff): accepted and used up.
            g = fresh(); g.CreditXp(50f);
            Require(check(g, start, WithStats(start, (s, l) => SetXp(s, l, 2, 60f, 300f, 1))) && g.XpCredit == 0f, "Credited experience refused");
            g = fresh(); g.CreditXp(50f);
            Require(check(g, start, WithStats(start, (s, l) => SetXp(s, l, 2, 110f, 300f, 1))), "Buffed experience refused");
            // 4. A level up as the game does it, with credit; a free point spent on health (max 110, filled).
            g = fresh(); g.CreditXp(340f);
            var levelled = WithStats(start, (s, l) => SetXp(s, l, 3, 50f, 450f, 2));
            Require(check(g, start, levelled), "Credited level up refused");
            var spent = WithStats(levelled, (s, l) => { SetXp(s, l, 3, 50f, 450f, 1, 2); s[0] = 110f; s[1] = 110f; });
            ((object[])spent[12])[7] = 110f;
            Require(check(g, levelled, spent), "Skill point on health refused");
            // 5. Levels without credit, wrong maxXP, points from nowhere, skill beyond its maximum, changed factor.
            foreach (var bad in new Action<object[], object[]>[] {
                (s, l) => SetXp(s, l, 3, 50f, 450f, 2),
                (s, l) => SetXp(s, l, 2, 10f, 250f, 1),
                (s, l) => SetXp(s, l, 2, 10f, 300f, 5),
                (s, l) => { SetXp(s, l, 2, 10f, 300f, 0, 2); s[15] = 2; l[6] = 2; },
                (s, l) => { SetXp(s, l, 2, 10f, 300f, 0); s[16] = 11; l[7] = 11; s[13] = -9; },
                (s, l) => { l[3] = 3f; },
                (s, l) => { s[12] = 20f; } })
            {
                var c = WithStats(start, bad);
                Require(!check(fresh(), start, c) && (int)Stats(c)[10] == 2 && (int)Stats(c)[13] == 1 && (int)Stats(c)[14] == 1 && (float)Stats(c)[11] == 300f &&
                    (float)Level(c)[3] == 1.5f, "Inconsistent level accepted");
            }
            // An uncredited level up with some credit: the last accepted level advanced by the credit.
            g = fresh(); g.CreditXp(300f);
            var over = WithStats(start, (s, l) => SetXp(s, l, 5, 0f, 750f, 4));
            Require(!check(g, start, over) && (int)Stats(over)[10] == 3 && (float)Stats(over)[12] == 10f && (int)Stats(over)[13] == 2 && (float)Stats(over)[11] == 450f, "Level not advanced by the credit");
            // 6. Maximums.
            var maxes = WithStats(start, (s, l) => { s[0] = 200f; s[2] = 150f; s[4] = 150f; s[8] = 120f; });
            Require(!check(fresh(), start, maxes) && (float)Stats(maxes)[0] == 100f && (float)Stats(maxes)[2] == 100f && (float)Stats(maxes)[4] == 100f && (float)Stats(maxes)[8] == 100f, "Raised maximums accepted");
            // 7. Needs and health: only what consumption, events and time explain.
            g = fresh(); check(g, start, start);
            var fed = WithStats(start, (s, l) => s[5] = 90f);
            Require(!check(g, start, fed) && (float)Stats(fed)[5] <= 53.01f, "Hunger from nowhere accepted");
            g = fresh(); check(g, start, start); g.Consumed(new Dictionary<int, long> { { 30, 1 } });
            Require(check(g, start, WithStats(start, (s, l) => s[5] = 90f)), "Hunger from eaten food refused");
            g = fresh(); check(g, start, start); g.Consumed(new Dictionary<int, long> { { 31, 1 } });
            Require(check(g, start, WithStats(start, (s, l) => s[1] = 100f)), "Health from a medkit refused");
            g = fresh(); check(g, start, start);
            var healed = WithStats(start, (s, l) => s[1] = 100f); ((object[])healed[12])[7] = 100f;
            Require(!check(g, start, healed) && (float)Stats(healed)[1] <= 83.01f && (float)((object[])healed[12])[7] <= 83.01f, "Health from nowhere accepted");
            clock += 10; // regeneration over time
            Require(check(g, start, WithStats(start, (s, l) => s[1] = 100f)), "Health regeneration over 10 s refused");
            g = fresh(); check(g, start, start); g.CreditRespawn();
            Require(check(g, start, WithStats(start, (s, l) => { s[1] = 100f; s[5] = 100f; s[7] = 100f; s[9] = 100f; })), "Respawn refused");
            g = fresh(); check(g, start, start); g.CreditSleep(8);
            Require(check(g, start, WithStats(start, (s, l) => s[9] = 100f)), "Shared sleep refused");
            clock += LanGuestStats.EventWindow + 20; // the window closes
            Require(!check(g, start, WithStats(start, (s, l) => s[9] = 100f)), "Sleep credit kept for later");
            g = fresh(); check(g, start, start); g.Consumed(new Dictionary<int, long> { { 32, 1 } });
            Require(check(g, start, WithStats(start, (s, l) => { s[7] = 75f; s[9] = 75f; })), "Buffed drink refused");
            Require(!check(fresh(), start, WithStats(start, (s, l) => s[5] = 150f)), "Hunger above its maximum accepted");

            // 8. Through the ledger: consumption seen in the upload; a correction is a fix.
            var book = new LanGuestLedger(new StatRules());
            Require(book.Stats != null, "Stat rules not picked up");
            book.Reset(start, 0);
            var fixes = new List<LedgerFix>();
            var ate = WithStats(StatProfile(new object[] { Item(30, 2), Item(31, 1), Item(32, 1) }), (s, l) => s[5] = 85f);
            var kept = book.Reconcile(ate, new uint[] { 0, 0 }, fixes);
            Require(fixes.Count == 0 && (float)Stats(kept)[5] == 85f && book.LastStatReasons == null, "Eating through the ledger refused");
            fixes.Clear();
            var greedy = WithStats(StatProfile(new object[] { Item(30, 2), Item(31, 1), Item(32, 1) }), (s, l) => { s[5] = 85f; s[9] = 100f; });
            kept = book.Reconcile(greedy, new uint[] { 0, 0 }, fixes);
            Require(fixes.Count == 1 && fixes[0].Path.Length == 0 && (float)Stats(kept)[9] < 60f && book.LastStatReasons != null && book.LastStatReasons.Contains("sleep"), "Uncredited sleep accepted by the ledger");
            Require((float)Stats(book.Profile)[9] < 60f, "The ledger kept the claimed sleep");
            // A ledger without stat rules (the older checks) leaves stats alone.
            var plain = new LanGuestLedger(new CountingRules());
            Require(plain.Stats == null, "Stats checked without rules");
            Console.WriteLine("PASS: guest stats on the host (experience only as credited, the game's level up, points and skills, maximums from skills, needs and health from consumed items, events and time, corrections through the ledger)");
        }
    }
}
