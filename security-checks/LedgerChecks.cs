using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZompiercerLAN
{
    // 0.7.0: the host's ledger of what the guest owns. Pure accounting only.
    internal static partial class Program
    {
        private sealed class FakeLedgerRules : ILedgerRules
        {
            // 1 ammo, 2 weapon (wear), 3 key (exact), 4 armour (wear, wear), 5 mod, 6 food.
            public LedgerItemRule Item(int id)
            {
                switch (id)
                {
                    case 1: case 5: case 6: return new LedgerItemRule();
                    case 2: return new LedgerItemRule { Slot0 = LedgerSlot.Wear };
                    case 3: return new LedgerItemRule { Slot0 = LedgerSlot.Exact };
                    case 4: return new LedgerItemRule { Slot0 = LedgerSlot.Wear, Slot1 = LedgerSlot.Wear };
                    default: return null;
                }
            }
        }
        internal static object[] L(int id, int amount, object a = null, object b = null, object[] mods = null)
        { return new object[] { id, amount, new object[] { a, b, null, mods } }; }
        internal static object[] LedgerProfile(object[] backpack, object[] belt, int[] magazines)
        {
            var root = SampleProfile();
            var c = (object[])((object[])root[12]).Clone();
            c[0] = new object[] { backpack, belt };
            c[5] = magazines.Select(m => (object)new object[] { m }).ToArray();
            root[12] = c;
            return root;
        }
        private static object[] Inventories(object[] root) { return (object[])((object[])root[12])[0]; }
        private static int[] Magazines(object[] root) { return ((object[])((object[])root[12])[5]).Select(g => (int)((object[])g)[0]).ToArray(); }
        private static long Total(object[] root, int id)
        {
            var caps = LedgerMath.Summarize(Inventories(root), Magazines(root), new FakeLedgerRules());
            return caps.Counts.Where(p => p.Key == id || (p.Key & ~(1L << 62)) >> 32 == id && (p.Key & (1L << 62)) != 0).Sum(p => p.Value);
        }
        // What the guest does with fixes: walk the same paths in its own trees.
        private static object[] ApplyFixes(object[] inventories, List<LedgerFix> fixes)
        {
            var copy = (object[])LanValueCodec.Decode(LanValueCodec.Encode(new object[] { inventories }))[0];
            foreach (var fix in fixes.OrderByDescending(f => string.Join(",", f.Path.Select(x => x.ToString("D5")))))
            {
                if (fix.Path[0] < 0) continue;
                object[] list = (object[])copy[fix.Path[0]]; object[] holder = copy; int holderIndex = fix.Path[0];
                for (int i = 1; i < fix.Path.Length - 2; i += 2)
                {
                    var item = (object[])list[fix.Path[i]];
                    var mods = (object[])((object[])item[2])[3];
                    holder = mods; holderIndex = fix.Path[i + 1]; list = (object[])mods[holderIndex];
                }
                int last = fix.Path[fix.Path.Length - 1];
                var target = (object[])list[last];
                if (fix.Amount == 0) { var l = list.ToList(); l.RemoveAt(last); holder[holderIndex] = l.ToArray(); continue; }
                target[1] = fix.Amount;
                if (fix.Wear0 != null) ((object[])target[2])[0] = fix.Wear0;
                if (fix.Wear1 != null) ((object[])target[2])[1] = fix.Wear1;
            }
            return copy;
        }
        private static bool SameTrees(object[] a, object[] b) { return LanValueCodec.Encode(new object[] { a }).SequenceEqual(LanValueCodec.Encode(new object[] { b })); }

        // Game conversions for the ledger tests: 20 <- 2x1 + 1x6, weapon 21 <- 3x5,
        // 22 -> 4x1 + 1x5 (disassembly), 10 -> 61 (water bottle), kit 155 repairs 2.
        private sealed class FakeConversionRules : ILedgerRules, ILedgerConversions
        {
            private readonly FakeLedgerRules _items = new FakeLedgerRules();
            public LedgerItemRule Item(int id)
            {
                if (id == 21) return new LedgerItemRule { Slot0 = LedgerSlot.Wear };
                if (id == 20 || id == 22 || id == 10 || id == 61 || id == 155) return new LedgerItemRule();
                return _items.Item(id);
            }
            public IList<LedgerConversion> ProducersOf(int id)
            {
                var all = new[]
                {
                    new LedgerConversion { In = new[] { 1, 6 }, InAmount = new[] { 2, 1 }, Out = new[] { 20 }, OutAmount = new[] { 1 } },
                    new LedgerConversion { In = new[] { 5 }, InAmount = new[] { 3 }, Out = new[] { 21 }, OutAmount = new[] { 1 } },
                    new LedgerConversion { In = new[] { 22 }, InAmount = new[] { 1 }, Out = new[] { 1, 5 }, OutAmount = new[] { 4, 1 } },
                    new LedgerConversion { In = new[] { 10 }, InAmount = new[] { 1 }, Out = new[] { 61 }, OutAmount = new[] { 1 } },
                };
                return all.Where(c => c.Out.Contains(id)).ToList();
            }
            public int RepairKit(int id) { return id == 2 || id == 21 ? 155 : -1; }
            public float MaxWear(int id, int slot) { return slot == 0 && (id == 2 || id == 21) ? 100f : 0f; }
        }

        private static void ConversionChecks()
        {
            var rules = new FakeConversionRules();
            var start = LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 6), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 60f) }, new object[0], new[] { 0 });
            var ledger = new LanGuestLedger(rules);
            var fixes = new List<LedgerFix>();
            Func<object[], object[]> reconcile = state => { fixes.Clear(); return ledger.Reconcile(state, new uint[] { 0, 0 }, fixes); };
            // Crafting with the inputs gone is accepted; the product needs its inputs.
            ledger.Reset(start, 0);
            var crafted = reconcile(LedgerProfile(new object[] { L(1, 6), L(5, 6), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 60f), L(20, 2) }, new object[0], new[] { 0 }));
            Require(fixes.Count == 0 && Total(crafted, 20) == 2 && Total(crafted, 1) == 6 && Total(crafted, 6) == 0, "Honest crafting refused");
            ledger.Reset(start, 0);
            reconcile(LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 6), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 60f), L(20, 3) }, new object[0], new[] { 0 }));
            Require(fixes.Count > 0 && Total(ledger.Profile, 20) == 0, "Product without spent inputs accepted");
            ledger.Reset(start, 0);
            reconcile(LedgerProfile(new object[] { L(1, 8), L(6, 1), L(5, 6), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 60f), L(20, 3) }, new object[0], new[] { 0 }));
            Require(Total(ledger.Profile, 20) == 1, "More products than spent inputs accepted");
            // A crafted weapon arrives at full durability, no better.
            ledger.Reset(start, 0);
            var weapon = reconcile(LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 3), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 60f), L(21, 1, 100f) }, new object[0], new[] { 0 }));
            Require(fixes.Count == 0 && Total(weapon, 21) == 1, "Crafted weapon refused");
            // Disassembly and leftovers.
            ledger.Reset(start, 0);
            var parsed = reconcile(LedgerProfile(new object[] { L(1, 14), L(6, 2), L(5, 7), L(10, 1), L(61, 1), L(155, 1), L(2, 1, 60f) }, new object[0], new[] { 0 }));
            Require(fixes.Count == 0 && Total(parsed, 1) == 14 && Total(parsed, 61) == 1 && Total(parsed, 22) == 0, "Disassembly or leftover refused");
            ledger.Reset(start, 0);
            reconcile(LedgerProfile(new object[] { L(1, 14), L(6, 2), L(5, 7), L(22, 1), L(10, 2), L(61, 3), L(155, 1), L(2, 1, 60f) }, new object[0], new[] { 0 }));
            Require(Total(ledger.Profile, 1) == 10 && Total(ledger.Profile, 61) == 0, "Disassembly without the item accepted");
            // Repairs: one consumed kit restores one item to full; none without a kit.
            ledger.Reset(start, 0);
            var repaired = reconcile(LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 6), L(22, 1), L(10, 2), L(2, 1, 100f) }, new object[0], new[] { 0 }));
            var wear = Inventories(repaired).Cast<object[]>().SelectMany(i => i).Cast<object[]>().First(i => (int)i[0] == 2);
            Require(fixes.Count == 0 && (float)((object[])wear[2])[0] == 100f, "Repair with a kit refused");
            ledger.Reset(start, 0);
            var unrepaired = reconcile(LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 6), L(22, 1), L(10, 2), L(155, 1), L(2, 1, 100f) }, new object[0], new[] { 0 }));
            wear = Inventories(unrepaired).Cast<object[]>().SelectMany(i => i).Cast<object[]>().First(i => (int)i[0] == 2);
            Require((float)((object[])wear[2])[0] == 60f, "Repair without a kit accepted");
            ledger.Reset(start, 0);
            reconcile(LedgerProfile(new object[] { L(1, 10), L(6, 2), L(5, 6), L(22, 1), L(10, 2), L(2, 1, 120f) }, new object[0], new[] { 0 }));
            Require(Math.Abs(ledger.BestWear(2) - 60f) < .01f, "Repair beyond full durability accepted");
            Console.WriteLine("PASS: ledger conversions (crafting by recipe, products need spent inputs, full-durability products, disassembly and leftovers, repairs by kit)");
        }

        private static void LedgerChecks()
        {
            var rules = new FakeLedgerRules();
            var start = LedgerProfile(new object[] { L(1, 30), L(2, 1, 80f), L(3, 1, 7), L(4, 1, 50f, 20f), L(6, 3) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[] { L(5, 1) } }) }, new[] { 5, 0 });
            var ledger = new LanGuestLedger(rules);
            ledger.Reset(start, 0);
            var fixes = new List<LedgerFix>();
            // 1. The same state is accepted unchanged.
            var stored = ledger.Reconcile(start, new uint[] { 0, 0 }, fixes);
            Require(fixes.Count == 0 && LanValueCodec.Encode(stored).SequenceEqual(LanValueCodec.Encode(start)), "Unchanged state altered");
            // 2. Less is always fine (ate, shot, wore out, moved the mod to the backpack).
            var less = LedgerProfile(new object[] { L(1, 22), L(2, 1, 70f), L(3, 1, 7), L(4, 1, 45f, 20f), L(6, 1), L(5, 1) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[0] }) }, new[] { 2, 0 });
            fixes.Clear(); stored = ledger.Reconcile(less, new uint[] { 0, 0 }, fixes);
            Require(fixes.Count == 0 && Total(stored, 1) == 22 && Magazines(stored)[0] == 2, "Legitimate decreases refused");
            // 3. More than the host knows is cut back, item by item.
            var cheat = LedgerProfile(new object[] { L(1, 99), L(2, 1, 100f), L(3, 1, 7), L(3, 1, 9), L(4, 1, 45f, 90f), L(6, 1), L(5, 1), L(77, 1) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[] { L(5, 1) } }) }, new[] { 9, 4 });
            fixes.Clear(); stored = ledger.Reconcile(cheat, new uint[] { 0, 0 }, fixes);
            Require(Total(stored, 1) == 22 && Total(stored, 5) == 1 && Total(stored, 77) == 0, "Spawned items accepted");
            Require(Total(stored, 3) == 1 && Inventories(stored).Cast<object[]>().SelectMany(i => i).Cast<object[]>().Any(i => (int)i[0] == 3 && (int)((object[])i[2])[0] == 7), "Forged key accepted");
            var wears = Inventories(stored).Cast<object[]>().SelectMany(i => i).Cast<object[]>().Where(i => (int)i[0] == 2).Select(i => (float)((object[])i[2])[0]).OrderBy(x => x).ToArray();
            Require(wears.SequenceEqual(new[] { 60f, 70f }), "Repaired weapon accepted: " + string.Join(",", wears));
            var armour = Inventories(stored).Cast<object[]>().SelectMany(i => i).Cast<object[]>().First(i => (int)i[0] == 4);
            Require((float)((object[])armour[2])[1] == 20f && Magazines(stored).SequenceEqual(new[] { 2, 0 }), "Armour or magazines raised");
            Require(fixes.Count > 0 && SameTrees(ApplyFixes(Inventories(cheat), fixes), Inventories(stored)), "Fixes do not reproduce the accepted state");
            // 4. A hand-out the guest had not received yet is neither lost nor doubled.
            var given = L(6, 4);
            ledger.Give(LanGuestLedger.RequestChannel, 5, given);
            fixes.Clear(); stored = ledger.Reconcile(less, new uint[] { 4, 0 }, fixes);
            Require(Total(stored, 6) == 5 && fixes.Count == 0 && ledger.PendingCount == 1, "Pending hand-out lost by a stale state");
            var received = LedgerProfile(new object[] { L(1, 22), L(2, 1, 70f), L(3, 1, 7), L(4, 1, 45f, 20f), L(6, 1), L(5, 1), L(6, 4) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[0] }) }, new[] { 2, 0 });
            fixes.Clear(); stored = ledger.Reconcile(received, new uint[] { 5, 0 }, fixes);
            Require(Total(stored, 6) == 5 && fixes.Count == 0 && ledger.PendingCount == 0, "Acknowledged hand-out doubled");
            fixes.Clear(); stored = ledger.Reconcile(received, new uint[] { 5, 0 }, fixes);
            Require(Total(stored, 6) == 5, "Repeated state changed the ledger");
            // A second identical state claiming the hand-out again is clamped.
            var twice = LedgerProfile(new object[] { L(1, 22), L(2, 1, 70f), L(3, 1, 7), L(4, 1, 45f, 20f), L(6, 9), L(5, 1) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[0] }) }, new[] { 2, 0 });
            fixes.Clear(); stored = ledger.Reconcile(twice, new uint[] { 5, 0 }, fixes);
            Require(Total(stored, 6) == 5 && fixes.Count > 0, "Doubled hand-out accepted");
            // 5. Puts: only what the guest owns, never better than it is.
            Require(!ledger.Take(L(2, 1, 95f)) && !ledger.Take(L(1, 23)) && !ledger.Take(L(3, 1, 9)), "Put of unowned or improved item accepted");
            Require(ledger.Take(L(2, 1, 65f)) && ledger.Take(L(1, 20)) && Total(ledger.Profile, 1) == 2, "Owned items refused for a put");
            // A state captured before the put still lists the item: it must not come back.
            fixes.Clear(); stored = ledger.Reconcile(received, new uint[] { 5, 0 }, fixes);
            Require(Total(stored, 1) == 2 && Total(stored, 2) == 1, "Item put away came back with a stale state");
            // 6. Combat: rounds spent at the host stay spent; a reload is pending until acknowledged.
            ledger.Reset(start, 0);
            ledger.SpendRound(0); ledger.SpendRound(0);
            Require(ledger.Remove(1, 2) && !ledger.Remove(1, 1000), "Reserve removal");
            ledger.GiveMagazine(LanGuestLedger.CombatChannel, 1, 0, 2);
            fixes.Clear(); stored = ledger.Reconcile(start, new uint[] { 0, 0 }, fixes);
            Require(Magazines(stored)[0] == 5 && Total(stored, 1) == 28, "Stale state undid spent rounds or the reload: " + Magazines(stored)[0] + "/" + Total(stored, 1));
            var reloaded = LedgerProfile(new object[] { L(1, 28), L(2, 1, 80f), L(3, 1, 7), L(4, 1, 50f, 20f), L(6, 3) },
                new object[] { L(2, 1, 60f, null, new object[] { new object[] { L(5, 1) } }) }, new[] { 5, 0 });
            fixes.Clear(); stored = ledger.Reconcile(reloaded, new uint[] { 0, 1 }, fixes);
            Require(Magazines(stored)[0] == 5 && fixes.Count == 0 && ledger.PendingCount == 0, "Acknowledged reload");
            ledger.Wear(2, 5f);
            Require(Math.Abs(ledger.BestWear(2) - 75f) < 0.01f, "Weapon wear");
            // 7. A changed inventory layout is refused.
            var layout = LedgerProfile(new object[] { L(1, 1) }, new object[0], new[] { 5, 0 });
            ((object[])layout[12])[0] = new object[] { new object[0] };
            bool refused = false;
            try { ledger.Reconcile(layout, new uint[] { 0, 0 }, fixes); } catch (InvalidDataException) { refused = true; }
            Require(refused, "Inventory layout change accepted");
            // 8. Random states never exceed the ledger, whatever they claim.
            var random = new Random(31337);
            for (int round = 0; round < 3000; round++)
            {
                ledger.Reset(start, 0);
                var items = new List<object>();
                for (int i = 0; i < random.Next(12); i++)
                {
                    int id = random.Next(1, 8);
                    items.Add(L(id, id == 1 || id == 6 ? random.Next(1, 60) : 1, id == 2 || id == 4 ? (object)(float)random.Next(0, 120) : id == 3 ? (object)random.Next(0, 10) : null,
                        id == 4 ? (object)(float)random.Next(0, 40) : null));
                }
                var claim = LedgerProfile(items.ToArray(), new object[0], new[] { random.Next(0, 20), random.Next(0, 3) });
                fixes.Clear(); stored = ledger.Reconcile(claim, new uint[] { 0, 0 }, fixes);
                var have = LedgerMath.Summarize(Inventories(stored), Magazines(stored), rules);
                var allowed = LedgerMath.Summarize(Inventories(start), Magazines(start), rules);
                foreach (var pair in have.Counts) Require(pair.Value <= allowed.Count(pair.Key), "Random claim exceeded the ledger");
                Require(Magazines(stored)[0] <= 5 && Magazines(stored)[1] == 0, "Random magazines exceeded the ledger");
                Require(SameTrees(ApplyFixes(Inventories(claim), fixes), Inventories(stored)), "Random fixes do not reproduce the accepted state");
            }
            ConversionChecks();
            Console.WriteLine("PASS: guest ledger (decreases, spawned/improved/forged items cut, pending hand-outs, puts, spent rounds and reloads, layout, 3000 random claims)");
        }
    }
}
