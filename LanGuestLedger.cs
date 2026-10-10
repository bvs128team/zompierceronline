using System;
using System.Collections.Generic;
using System.IO;

namespace ZompiercerLAN
{
    // How an item's saved extras (InventoryItem.Save slots 0 and 1) may change on
    // the guest: Free (not checked), Wear (durability: only down, unless the host
    // raises it), Exact (an identity such as a key or note number: never changes).
    internal enum LedgerSlot : byte { Free = 0, Wear = 1, Exact = 2 }
    internal sealed class LedgerItemRule { internal LedgerSlot Slot0, Slot1; }
    internal interface ILedgerRules
    {
        // Null for an id the game does not know: such items are never accepted.
        LedgerItemRule Item(int id);
    }

    // What the game itself turns items into (0.7.0): crafting recipes, disassembly
    // and leftovers (resourcesReceived), repairs. A state may show these products
    // only when the inputs left the guest's belongings in the same proportion.
    internal sealed class LedgerConversion { internal int[] In, InAmount, Out, OutAmount; }
    internal interface ILedgerConversions
    {
        IList<LedgerConversion> ProducersOf(int id);
        // The repair kit that restores this item, or -1; and its full durability per slot.
        int RepairKit(int id);
        float MaxWear(int id, int slot);
    }

    // Host-side accounting of what the guest may own. Everything is an upper bound:
    // the guest can always have less (it ate, shot, dropped, wore out), never more
    // than the host knows about. Items the host has handed out but the guest has
    // not yet confirmed are "pending" and counted separately, so a state captured
    // before a hand-out neither loses nor doubles it. No Unity APIs here.
    internal sealed class LedgerCaps
    {
        internal readonly Dictionary<long, long> Counts = new Dictionary<long, long>();
        internal readonly Dictionary<int, List<float>> Wear0 = new Dictionary<int, List<float>>(), Wear1 = new Dictionary<int, List<float>>();
        internal int[] Magazines = new int[0];
        internal long Count(long key) { long v; return Counts.TryGetValue(key, out v) ? v : 0; }
    }

    // One change the guest must make so it stays within the caps: an item at
    // `Path` (inventory, item, then mod slot and item pairs) gets `Amount`
    // (0 = removed) and, when set, the lower wear values.
    internal sealed class LedgerFix
    {
        internal int[] Path;
        internal int Amount;
        internal object Wear0, Wear1;
    }

    internal static class LedgerMath
    {
        internal const float Epsilon = 0.01f;
        private const long ExactFlag = 1L << 62;

        internal static long Key(int id, object slot0, LedgerItemRule rule)
        {
            if (rule != null && rule.Slot0 == LedgerSlot.Exact && slot0 is int) return ExactFlag | ((long)id << 32) | (uint)(int)slot0;
            return id;
        }

        // Flattened summary of item trees (mods counted as items) and magazines.
        internal static LedgerCaps Summarize(object[] inventories, int[] magazines, ILedgerRules rules)
        {
            var caps = new LedgerCaps { Magazines = magazines == null ? new int[0] : (int[])magazines.Clone() };
            foreach (var inventory in inventories) AddItems(caps, (object[])inventory, rules, 1);
            return caps;
        }
        internal static void AddItems(LedgerCaps caps, object[] items, ILedgerRules rules, int sign)
        {
            if (items == null) return;
            foreach (object[] item in items) AddItem(caps, item, rules, sign);
        }
        internal static void AddItem(LedgerCaps caps, object[] item, ILedgerRules rules, int sign)
        {
            int id = (int)item[0], amount = (int)item[1];
            var extra = (object[])item[2];
            var rule = rules.Item(id);
            long key = Key(id, extra[0], rule);
            caps.Counts[key] = Math.Max(0, caps.Count(key) + sign * (long)amount);
            if (rule != null && amount == 1)
            {
                if (rule.Slot0 == LedgerSlot.Wear && extra[0] is float) Adjust(caps.Wear0, id, (float)extra[0], sign);
                if (rule.Slot1 == LedgerSlot.Wear && extra[1] is float) Adjust(caps.Wear1, id, (float)extra[1], sign);
            }
            if (extra[3] is object[])
                foreach (var mod in (object[])extra[3]) AddItems(caps, mod as object[], rules, sign);
        }
        private static void Adjust(Dictionary<int, List<float>> wear, int id, float value, int sign)
        {
            List<float> list;
            if (!wear.TryGetValue(id, out list)) wear.Add(id, list = new List<float>());
            if (sign > 0) { list.Add(value); return; }
            // Remove the closest value (the exact one when present).
            int best = -1; float distance = float.MaxValue;
            for (int i = 0; i < list.Count; i++) { float d = Math.Abs(list[i] - value); if (d < distance) { distance = d; best = i; } }
            if (best >= 0) list.RemoveAt(best);
        }

        internal static LedgerCaps Copy(LedgerCaps source)
        {
            var copy = new LedgerCaps { Magazines = (int[])source.Magazines.Clone() };
            foreach (var pair in source.Counts) copy.Counts.Add(pair.Key, pair.Value);
            foreach (var pair in source.Wear0) copy.Wear0.Add(pair.Key, new List<float>(pair.Value));
            foreach (var pair in source.Wear1) copy.Wear1.Add(pair.Key, new List<float>(pair.Value));
            return copy;
        }

        // Lowers the guest's item trees and magazines to the caps. Returns the
        // clamped copies; `fixes` lists what changed (empty when nothing did).
        // Unknown item ids are removed.
        internal static object[] Clamp(object[] inventories, int[] magazines, LedgerCaps caps, ILedgerRules rules,
            out int[] clampedMagazines, List<LedgerFix> fixes)
        {
            return Clamp(inventories, magazines, caps, rules, out clampedMagazines, fixes, null);
        }
        internal static object[] Clamp(object[] inventories, int[] magazines, LedgerCaps caps, ILedgerRules rules,
            out int[] clampedMagazines, List<LedgerFix> fixes, Dictionary<int, int> repairs)
        {
            var budget = Copy(caps);
            _repairs = repairs;
            var result = new object[inventories.Length];
            for (int i = 0; i < inventories.Length; i++)
                result[i] = ClampItems((object[])inventories[i], budget, rules, new List<int> { i }, fixes);
            clampedMagazines = magazines == null ? new int[0] : (int[])magazines.Clone();
            for (int i = 0; i < clampedMagazines.Length; i++)
            {
                int cap = i < budget.Magazines.Length ? budget.Magazines[i] : 0;
                if (clampedMagazines[i] > cap)
                {
                    clampedMagazines[i] = Math.Max(0, cap);
                    fixes.Add(new LedgerFix { Path = new[] { -1, i }, Amount = clampedMagazines[i] });
                }
            }
            return result;
        }

        private static object[] ClampItems(object[] items, LedgerCaps budget, ILedgerRules rules, List<int> path, List<LedgerFix> fixes)
        {
            if (items == null) return null;
            var kept = new List<object>();
            for (int index = 0; index < items.Length; index++)
            {
                var item = (object[])items[index];
                int id = (int)item[0], amount = (int)item[1];
                var extra = (object[])((object[])item[2]).Clone();
                var rule = rules.Item(id);
                long key = Key(id, extra[0], rule);
                long allowed = rule == null ? 0 : budget.Count(key);
                int take = (int)Math.Min(amount, allowed);
                var here = new List<int>(path) { index };
                if (take <= 0) { fixes.Add(new LedgerFix { Path = here.ToArray(), Amount = 0 }); continue; }
                budget.Counts[key] = allowed - take;
                var fix = new LedgerFix { Path = here.ToArray(), Amount = take };
                bool changed = take != amount;
                if (take == 1)
                {
                    float v0 = rule.Slot0 == LedgerSlot.Wear && extra[0] is float ? Fit(budget.Wear0, id, (float)extra[0]) : float.MaxValue;
                    float v1 = rule.Slot1 == LedgerSlot.Wear && extra[1] is float ? Fit(budget.Wear1, id, (float)extra[1]) : float.MaxValue;
                    bool high0 = extra[0] is float && v0 < (float)extra[0] - Epsilon, high1 = extra[1] is float && v1 < (float)extra[1] - Epsilon;
                    // One consumed repair kit explains one unit restored up to full durability.
                    if ((high0 || high1) && Repaired(rules, id, extra)) high0 = high1 = false;
                    if (high0) { extra[0] = v0; fix.Wear0 = v0; changed = true; }
                    if (high1) { extra[1] = v1; fix.Wear1 = v1; changed = true; }
                }
                if (changed) fixes.Add(fix);
                if (extra[3] is object[])
                {
                    var mods = (object[])((object[])extra[3]).Clone();
                    for (int slot = 0; slot < mods.Length; slot++)
                    {
                        var modPath = new List<int>(here) { slot };
                        mods[slot] = ClampItems(mods[slot] as object[], budget, rules, modPath, fixes);
                    }
                    extra[3] = mods;
                }
                kept.Add(new object[] { id, take, extra });
            }
            return kept.ToArray();
        }

        [ThreadStatic] private static Dictionary<int, int> _repairs;
        // A consumed repair kit of this item explains a durability above the cap.
        private static bool Repaired(ILedgerRules rules, int id, object[] extra)
        {
            var conversions = rules as ILedgerConversions;
            if (conversions == null || _repairs == null) return false;
            int kit = conversions.RepairKit(id);
            int left;
            if (kit < 0 || !_repairs.TryGetValue(kit, out left) || left <= 0) return false;
            if (extra[0] is float && (float)extra[0] > conversions.MaxWear(id, 0) + Epsilon) return false;
            if (extra[1] is float && (float)extra[1] > conversions.MaxWear(id, 1) + Epsilon) return false;
            _repairs[kit] = left - 1;
            return true;
        }

        // Adds to `caps` what game conversions explain: products the state shows
        // beyond the caps whose inputs are missing from the state. Returns repair
        // kits consumed (kit id -> count) for Clamp.
        internal static Dictionary<int, int> Explain(LedgerCaps caps, LedgerCaps state, ILedgerRules rules)
        {
            var repairs = new Dictionary<int, int>();
            var conversions = rules as ILedgerConversions;
            if (conversions == null) return repairs;
            for (int round = 0; round < 64; round++)
            {
                bool progress = false;
                foreach (var pair in new List<KeyValuePair<long, long>>(state.Counts))
                {
                    long key = pair.Key;
                    if ((key & ExactFlag) != 0 || key > int.MaxValue) continue;
                    long excess = pair.Value - caps.Count(key);
                    if (excess <= 0) continue;
                    foreach (var c in conversions.ProducersOf((int)key))
                    {
                        int outAt = Array.IndexOf(c.Out, (int)key);
                        if (outAt < 0 || c.OutAmount[outAt] <= 0) continue;
                        long n = (excess + c.OutAmount[outAt] - 1) / c.OutAmount[outAt];
                        for (int i = 0; i < c.In.Length; i++)
                        {
                            long missing = caps.Count(c.In[i]) - state.Count(c.In[i]);
                            n = Math.Min(n, c.InAmount[i] <= 0 ? n : missing / c.InAmount[i]);
                        }
                        if (n < 1) continue;
                        for (int i = 0; i < c.In.Length; i++) caps.Counts[c.In[i]] = caps.Count(c.In[i]) - n * c.InAmount[i];
                        for (int i = 0; i < c.Out.Length; i++)
                        {
                            caps.Counts[c.Out[i]] = caps.Count(c.Out[i]) + n * c.OutAmount[i];
                            // A fresh product is at full durability.
                            var rule = rules.Item(c.Out[i]);
                            for (int slot = 0; slot < 2 && rule != null; slot++)
                            {
                                if ((slot == 0 ? rule.Slot0 : rule.Slot1) != LedgerSlot.Wear) continue;
                                var wear = slot == 0 ? caps.Wear0 : caps.Wear1;
                                List<float> list;
                                if (!wear.TryGetValue(c.Out[i], out list)) wear.Add(c.Out[i], list = new List<float>());
                                for (long k = 0; k < n * c.OutAmount[i] && k < 64; k++) list.Add(conversions.MaxWear(c.Out[i], slot));
                            }
                        }
                        progress = true;
                        excess = pair.Value - caps.Count(key);
                        if (excess <= 0) break;
                    }
                }
                if (!progress) break;
            }
            // Repair kits the state no longer has beyond what conversions used.
            foreach (var pair in caps.Counts)
            {
                if ((pair.Key & ExactFlag) != 0) continue;
                long missing = pair.Value - state.Count(pair.Key);
                if (missing > 0) repairs[(int)pair.Key] = (int)Math.Min(missing, 1000);
            }
            return repairs;
        }

        // The cap paired with `value`: the smallest remaining cap at or above it,
        // otherwise the largest remaining one (the item is then worn down to it).
        private static float Fit(Dictionary<int, List<float>> wear, int id, float value)
        {
            List<float> list;
            if (!wear.TryGetValue(id, out list) || list.Count == 0) return 0f;
            int best = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i] >= value - Epsilon && (best < 0 || list[i] < list[best])) best = i;
            if (best < 0) { best = 0; for (int i = 1; i < list.Count; i++) if (list[i] > list[best]) best = i; }
            float cap = list[best]; list.RemoveAt(best);
            return Math.Min(value, cap);
        }
    }

    // The ledger proper, owned by the host for one guest profile.
    internal sealed class LanGuestLedger
    {
        internal const int RequestChannel = 0, CombatChannel = 1;
        private sealed class Pending { internal int Channel; internal uint Seq; internal object[] Items; internal int[] Magazines; }
        private readonly ILedgerRules _rules;
        private readonly List<Pending> _pending = new List<Pending>();
        private object[] _root;          // the profile as the host keeps it (LanGuestSchema root)
        private object[] _inventories;   // _root[12][0]
        private int[] _magazines;        // from _root[12][5]
        private int _backpack;
        // Every committed change, so the host can store the profile (1.0.0). The flag is
        // false for per-shot rounds and wear: those may be stored in batches.
        internal Action<bool> Changed;
        private bool _transaction;
        private object[] _before;
        private List<Pending> _beforePending;
        private void Notify(bool now = true) { if (!_transaction && Changed != null) Changed(now); }
        internal void BeginChange()
        {
            _before = Profile;
            _beforePending = new List<Pending>();
            foreach (var p in _pending) _beforePending.Add(new Pending { Channel = p.Channel, Seq = p.Seq,
                Items = (object[])p.Items.Clone(), Magazines = (int[])p.Magazines.Clone() });
            _transaction = true;
        }
        internal void EndChange(bool commit)
        {
            if (!_transaction) return;
            if (!commit) { Reset(_before, _backpack); _pending.AddRange(_beforePending); }
            _transaction = false; _before = null; _beforePending = null;
            if (commit) Notify();
        }
        // Reserve room for one maximum-size wire item before the world gives it up.
        // Each encoded node costs at least a byte, so this also bounds codec nodes.
        internal bool CanReceive()
        {
            if (!Ready || ((object[])_inventories[_backpack]).Length >= LanGuestSchema.MaxItems) return false;
            var profile = WithInventories(_root, _inventories, _magazines);
            return LanValueCodec.Encode(profile).Length <= LanProtocol.MaxGuestBytes - LanProtocol.MaxStoragePayload
                && Nodes(profile) <= LanValueCodec.MaxNodes - LanProtocol.MaxStoragePayload;
        }
        private static int Nodes(object value)
        {
            int n = 1; var a = value as object[];
            if (a != null) foreach (var v in a) n += Nodes(v);
            return n;
        }
        private static void ValidateProfile(object[] profile)
        {
            LanGuestSchema.Validate(profile);
            if (LanValueCodec.Encode(profile).Length > LanProtocol.MaxGuestBytes)
                throw new InvalidDataException("Guest profile size");
        }
        internal bool Ready { get { return _root != null; } }

        internal LanGuestLedger(ILedgerRules rules) { _rules = rules; var stats = rules as IStatRules; if (stats != null) Stats = new LanGuestStats(stats); }
        // 1.3.0: the guest's level, experience, health and needs, held to what the host can explain
        // (null: not checked); what the last correction was about.
        internal LanGuestStats Stats;
        internal List<string> LastStatReasons { get; private set; }

        // Starts from a profile the host accepts as the truth (stored, or approved).
        internal void Reset(object[] root, int backpack)
        {
            ValidateProfile(root);
            _root = Clone(root); _pending.Clear();
            if (Stats != null) Stats.Reset();
            var character = (object[])_root[12];
            _inventories = (object[])character[0];
            _backpack = backpack >= 0 && backpack < _inventories.Length ? backpack : 0;
            _magazines = ReadMagazines((object[])character[5]);
        }
        internal void Clear() { _root = null; _inventories = null; _magazines = null; _pending.Clear(); }
        // The numbering of a channel restarted (new session or world): whatever is still
        // pending may or may not have reached the guest. It stays owned, uncounted as pending.
        internal void AckAll(int channel) { _pending.RemoveAll(p => p.Channel == channel); }

        internal object[] Profile { get { return _root == null ? null : Clone(WithInventories(_root, _inventories, _magazines)); } }
        internal LedgerCaps Caps { get { return LedgerMath.Summarize(_inventories, _magazines, _rules); } }
        internal int PendingCount { get { return _pending.Count; } }

        // A guest state arrived. `acks` = [last request result applied, last op applied].
        // Returns the profile to store; `fixes` is non-empty when the guest had more.
        internal object[] Reconcile(object[] upload, uint[] acks, List<LedgerFix> fixes)
        {
            if (!Ready) throw new InvalidOperationException("Ledger not ready");
            LanGuestSchema.Validate(upload);
            var uploaded = (object[])((object[])upload[12])[0];
            if (uploaded.Length != _inventories.Length) throw new InvalidDataException("Guest inventory layout changed");
            var remaining = _pending.FindAll(p => p.Seq > (p.Channel < acks.Length ? acks[p.Channel] : 0));
            // What the guest could have had when it captured this state.
            var caps = LedgerMath.Summarize(_inventories, _magazines, _rules);
            foreach (var p in remaining)
            {
                LedgerMath.AddItems(caps, p.Items, _rules, -1);
                for (int i = 0; i < p.Magazines.Length && i < caps.Magazines.Length; i++) caps.Magazines[i] = Math.Max(0, caps.Magazines[i] - p.Magazines[i]);
            }
            int[] magazines;
            var uploadedMagazines = ReadMagazines((object[])((object[])upload[12])[5]);
            var repairs = LedgerMath.Explain(caps, LedgerMath.Summarize(uploaded, uploadedMagazines, _rules), _rules);
            var accepted = LedgerMath.Clamp(uploaded, uploadedMagazines, caps, _rules, out magazines, fixes, repairs);
            // 1.3.0: what the guest consumed (less than the ledger, by id) restores its needs; its
            // level and needs are held to the last accepted profile plus what the host credited.
            var stored = upload;
            LastStatReasons = null;
            if (Stats != null && _root != null)
            {
                var have = CountById(_inventories);
                foreach (var p in remaining) foreach (object[] item in p.Items) { long v; have.TryGetValue((int)item[0], out v); have[(int)item[0]] = v - (int)item[1]; }
                var now = CountById(uploaded);
                var consumed = new Dictionary<int, long>();
                foreach (var pair in have) { long v; now.TryGetValue(pair.Key, out v); if (pair.Value > v) consumed[pair.Key] = pair.Value - v; }
                Stats.Consumed(consumed);
                var checkedRoot = Clone(upload);
                var reasons = new List<string>();
                if (!Stats.Check(_root, checkedRoot, reasons))
                {
                    stored = checkedRoot; LastStatReasons = reasons;
                    fixes.Add(new LedgerFix { Path = new int[0], Amount = 0 });
                }
            }
            // Then everything still on its way to the guest.
            foreach (var p in remaining)
            {
                foreach (object[] item in p.Items) accepted[_backpack] = Append((object[])accepted[_backpack], item);
                for (int i = 0; i < p.Magazines.Length && i < magazines.Length; i++) magazines[i] += p.Magazines[i];
            }
            ValidateProfile(WithInventories(stored, accepted, magazines));
            _root = Clone(stored); _inventories = accepted; _magazines = magazines;
            _pending.Clear(); _pending.AddRange(remaining); Notify();
            return Profile;
        }

        // Host hands an item to the guest (request result or op): owned at once, pending until acknowledged.
        internal void Give(int channel, uint seq, object[] item)
        {
            item = LanStorage.Normalize(item);
            _inventories[_backpack] = Append((object[])_inventories[_backpack], item);
            AddPending(channel, seq, new object[] { item }, null); Notify();
        }
        internal void GiveMagazine(int channel, uint seq, int gun, int rounds)
        {
            if (gun < 0 || gun >= _magazines.Length || rounds <= 0) return;
            _magazines[gun] += rounds;
            var delta = new int[_magazines.Length]; delta[gun] = rounds;
            AddPending(channel, seq, new object[0], delta); Notify();
        }
        private void AddPending(int channel, uint seq, object[] items, int[] magazines)
        {
            foreach (var p in _pending)
                if (p.Channel == channel && p.Seq == seq)
                {
                    var all = new List<object>(p.Items); all.AddRange(items); p.Items = all.ToArray();
                    if (magazines != null) for (int i = 0; i < magazines.Length && i < p.Magazines.Length; i++) p.Magazines[i] += magazines[i];
                    return;
                }
            _pending.Add(new Pending { Channel = channel, Seq = seq, Items = items, Magazines = magazines ?? new int[_magazines.Length] });
        }

        // Does the guest own this item (same id, identity, at least this good)?
        internal bool Has(object[] item)
        {
            int path0, path1; return Find(item, out path0, out path1) != null;
        }
        // Removes an item the guest hands over (a put): false when it does not own it.
        internal bool Take(object[] item)
        {
            int inventory, index;
            var found = Find(item, out inventory, out index);
            if (found == null) return false;
            int amount = (int)item[1], have = (int)found[1];
            var list = new List<object>((object[])_inventories[inventory]);
            if (have > amount) list[index] = new object[] { found[0], have - amount, found[2] };
            else list.RemoveAt(index);
            _inventories[inventory] = list.ToArray(); Notify();
            return true;
        }
        // The guest died (0.12.0): every top-level item (with its mods) leaves the ledger, and
        // nothing handed out is counted as still on its way (it would come back otherwise).
        // Magazines stay: the game keeps loaded rounds with the character, not with the gun item.
        internal object[] TakeAll()
        {
            var all = new List<object>();
            for (int i = 0; i < _inventories.Length; i++)
            {
                foreach (object[] item in (object[])_inventories[i]) all.Add(LanStorage.Normalize(item));
                _inventories[i] = new object[0];
            }
            _pending.Clear(); Notify(); return all.ToArray();
        }
        // Removes `amount` of an id from top-level stacks (ammo for a reload); false if short.
        internal bool Remove(int id, int amount)
        {
            long have = 0;
            foreach (object[] inventory in _inventories) foreach (object[] item in inventory) if ((int)item[0] == id) have += (int)item[1];
            if (have < amount) return false;
            for (int i = 0; i < _inventories.Length && amount > 0; i++)
            {
                var list = new List<object>((object[])_inventories[i]);
                for (int j = list.Count - 1; j >= 0 && amount > 0; j--)
                {
                    var item = (object[])list[j];
                    if ((int)item[0] != id) continue;
                    int take = Math.Min(amount, (int)item[1]); amount -= take;
                    if (take == (int)item[1]) list.RemoveAt(j); else list[j] = new object[] { item[0], (int)item[1] - take, item[2] };
                }
                _inventories[i] = list.ToArray();
            }
            Notify(); return true;
        }
        // Distinct ids of top-level items (not mods), in ledger order.
        internal List<int> TopLevelIds()
        {
            var ids = new List<int>();
            foreach (object[] inventory in _inventories) foreach (object[] item in inventory) if (!ids.Contains((int)item[0])) ids.Add((int)item[0]);
            return ids;
        }
        // Puts back what a put did not store (the guest keeps that part).
        internal void Refund(object[] item)
        {
            _inventories[_backpack] = Append((object[])_inventories[_backpack], LanStorage.Normalize(item)); Notify();
        }
        internal long Count(int id)
        {
            long have = 0;
            foreach (object[] inventory in _inventories) foreach (object[] item in inventory) if ((int)item[0] == id) have += (int)item[1];
            return have;
        }
        // Best durability of a top-level unit of this weapon or armour id; -1 when none.
        internal float BestWear(int id)
        {
            float best = -1f;
            foreach (object[] inventory in _inventories)
                foreach (object[] item in inventory)
                    if ((int)item[0] == id && ((object[])item[2])[0] is float) best = Math.Max(best, (float)((object[])item[2])[0]);
            return best;
        }
        // Wears the best unit of an id down by `amount` (never below zero).
        internal void Wear(int id, float amount)
        {
            int bestInventory = -1, bestIndex = -1; float best = -1f;
            for (int i = 0; i < _inventories.Length; i++)
            {
                var items = (object[])_inventories[i];
                for (int j = 0; j < items.Length; j++)
                {
                    var item = (object[])items[j];
                    if ((int)item[0] == id && ((object[])item[2])[0] is float && (float)((object[])item[2])[0] > best) { best = (float)((object[])item[2])[0]; bestInventory = i; bestIndex = j; }
                }
            }
            if (bestInventory < 0) return;
            var list = (object[])((object[])_inventories[bestInventory]).Clone();
            var unit = (object[])list[bestIndex];
            var extra = (object[])((object[])unit[2]).Clone(); extra[0] = Math.Max(0f, best - amount);
            list[bestIndex] = new object[] { unit[0], unit[1], extra };
            _inventories[bestInventory] = list; Notify(false);
        }
        internal int Magazine(int gun) { return gun >= 0 && gun < _magazines.Length ? _magazines[gun] : 0; }
        // 1.4.1: the guest's maximum stamina in the profile the host keeps (100 when not known).
        internal float MaxStamina
        {
            get
            {
                var character = _root == null ? null : _root[12] as object[];
                var stats = character == null || character.Length < 2 ? null : character[1] as object[];
                return stats != null && stats.Length > 2 && stats[2] is float ? (float)stats[2] : 100f;
            }
        }
        // 1.4.20: the guest's strength level (the accepted profile's, held by LanGuestStats), 1 when not known: its
        // swing hits as hard as the game's swing of that level (Gun.GetMeleeDamage: 5% more a level).
        internal int StrengthLevel
        {
            get
            {
                var character = _root == null ? null : _root[12] as object[];
                var stats = character == null || character.Length < 2 ? null : character[1] as object[];
                int level = stats != null && stats.Length > 16 && stats[16] is int ? (int)stats[16] : 1;
                return Math.Max(1, Math.Min(100, level));
            }
        }
        // 1.4.13: the guest's stealth for the host's zombies, as ZFLevelController sets stealthFactor from the
        // stealth level (the accepted profile's, held by LanGuestStats): 1 at level 1, a tenth less a level.
        internal float StealthFactor
        {
            get
            {
                var character = _root == null ? null : _root[12] as object[];
                var stats = character == null || character.Length < 2 ? null : character[1] as object[];
                int level = stats != null && stats.Length > 17 && stats[17] is int ? (int)stats[17] : 1;
                return Math.Max(.1f, Math.Min(1f, 1f - (level - 1) / 10f));
            }
        }
        internal void SpendRound(int gun) { if (gun >= 0 && gun < _magazines.Length && _magazines[gun] > 0) { _magazines[gun]--; Notify(false); } }

        private object[] Find(object[] wanted, out int inventory, out int index)
        {
            inventory = index = -1;
            int id = (int)wanted[0], amount = (int)wanted[1];
            var extra = (object[])wanted[2];
            var rule = _rules.Item(id);
            if (rule == null) return null;
            long key = LedgerMath.Key(id, extra[0], rule);
            object[] best = null;
            for (int i = 0; i < _inventories.Length; i++)
            {
                var items = (object[])_inventories[i];
                for (int j = 0; j < items.Length; j++)
                {
                    var item = (object[])items[j];
                    var mine = (object[])item[2];
                    if ((int)item[0] != id || LedgerMath.Key(id, mine[0], rule) != key || (int)item[1] < amount) continue;
                    if (!AtLeast(rule.Slot0, mine[0], extra[0]) || !AtLeast(rule.Slot1, mine[1], extra[1])) continue;
                    if (extra[3] is object[] && !SameMods(mine[3], extra[3])) continue;
                    if (best == null || WearOf(mine) < WearOf((object[])best[2])) { best = item; inventory = i; index = j; }
                }
            }
            return best;
        }
        private static bool AtLeast(LedgerSlot slot, object mine, object wanted)
        {
            if (slot != LedgerSlot.Wear) return true;
            if (!(wanted is float)) return true;
            return mine is float && (float)mine >= (float)wanted - LedgerMath.Epsilon;
        }
        private static float WearOf(object[] extra) { return extra[0] is float ? (float)extra[0] : 0f; }
        // Mods travel with their item; the host must already know them inside it.
        private static bool SameMods(object mine, object wanted)
        {
            var a = mine as object[]; var b = wanted as object[];
            if (b == null) return true;
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                var x = a[i] as object[]; var y = b[i] as object[];
                if ((x == null || x.Length == 0) != (y == null || y.Length == 0)) return false;
                if (x == null || y == null) continue;
                if (x.Length != y.Length) return false;
                for (int j = 0; j < x.Length; j++)
                    if ((int)((object[])x[j])[0] != (int)((object[])y[j])[0] || (int)((object[])x[j])[1] < (int)((object[])y[j])[1]) return false;
            }
            return true;
        }

        private static object[] Append(object[] items, object[] item)
        {
            var list = new List<object>(items ?? new object[0]) { item };
            return list.ToArray();
        }
        private static int[] ReadMagazines(object[] guns)
        {
            var result = new int[guns == null ? 0 : guns.Length];
            for (int i = 0; i < result.Length; i++) result[i] = (int)((object[])guns[i])[0];
            return result;
        }
        private static object[] WithInventories(object[] root, object[] inventories, int[] magazines)
        {
            var copy = (object[])root.Clone();
            var character = (object[])((object[])root[12]).Clone();
            character[0] = inventories;
            var guns = new object[magazines.Length];
            for (int i = 0; i < guns.Length; i++) guns[i] = new object[] { magazines[i] };
            character[5] = guns;
            copy[12] = character;
            return copy;
        }
        // Deep copy through the codec: the ledger never shares arrays with callers.
        private static object[] Clone(object[] root) { return LanValueCodec.Decode(LanValueCodec.Encode(root)); }
        // Top-level items by id.
        private static Dictionary<int, long> CountById(object[] inventories)
        {
            var counts = new Dictionary<int, long>();
            foreach (object[] inventory in inventories) foreach (object[] item in inventory) { long v; counts.TryGetValue((int)item[0], out v); counts[(int)item[0]] = v + (int)item[1]; }
            return counts;
        }
    }
}
