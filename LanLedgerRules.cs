using System;
using System.Collections.Generic;
using System.Text;
using Zompiercer.Inventory;

namespace ZompiercerLAN
{
    // Game side of the ledger rules: what an item's saved slots mean, from the item's
    // own prefab components (the same mapping InventoryItem.Save/Load use).
    internal sealed class LanLedgerRules : ILedgerRules, ILedgerConversions, IStatRules
    {
        // Drinking a bottle of water leaves the empty bottle (InventoryController.Update).
        private const int WaterBottle = 10, EmptyBottle = 61;
        private readonly Dictionary<int, LedgerItemRule> _cache = new Dictionary<int, LedgerItemRule>();
        private Dictionary<int, List<LedgerConversion>> _producers;
        private static readonly List<LedgerConversion> None = new List<LedgerConversion>();

        // Game recipes (ingredients rounded as Inventory.ExtractForRecipe does) and
        // resourcesReceived (disassembly and leftovers), indexed by product.
        public IList<LedgerConversion> ProducersOf(int id)
        {
            if (_producers == null) Build();
            List<LedgerConversion> list;
            return _producers != null && _producers.TryGetValue(id, out list) ? list : None;
        }
        private void Build()
        {
            var db = ItemsDataBase.Global;
            var prefabs = db == null ? null : db.itemPrefab;
            if (prefabs == null) return;
            var producers = new Dictionary<int, List<LedgerConversion>>();
            for (int i = 0; i < prefabs.Length; i++)
            {
                var p = prefabs[i];
                if (p == null) continue;
                if (p.recipe != null && p.recipe.Length > 0)
                {
                    var ins = new List<int>(); var amounts = new List<int>(); bool ok = true;
                    foreach (var r in p.recipe)
                    {
                        if (r == null || r.ID < 0 || r.ID >= prefabs.Length) { ok = false; break; }
                        int n = UnityEngine.Mathf.RoundToInt(r.Amount);
                        if (n > 0) { ins.Add(r.ID); amounts.Add(n); }
                    }
                    if (ok && ins.Count > 0) Add(producers, new LedgerConversion { In = ins.ToArray(), InAmount = amounts.ToArray(), Out = new[] { i }, OutAmount = new[] { Math.Max(1, p.AmountCrafted) } });
                }
                GetResource[] received = null;
                try { received = p.ResourcesReceived; } catch (Exception) { }
                if (received != null && received.Length > 0)
                {
                    var outs = new List<int>(); var amounts = new List<int>();
                    foreach (var r in received)
                        if (r.resource != null && r.resource.itemID >= 0 && r.resource.itemID < prefabs.Length && r.amount > 0) { outs.Add(r.resource.itemID); amounts.Add(r.amount); }
                    if (outs.Count > 0) Add(producers, new LedgerConversion { In = new[] { i }, InAmount = new[] { 1 }, Out = outs.ToArray(), OutAmount = amounts.ToArray() });
                }
            }
            if (WaterBottle < prefabs.Length && EmptyBottle < prefabs.Length)
                Add(producers, new LedgerConversion { In = new[] { WaterBottle }, InAmount = new[] { 1 }, Out = new[] { EmptyBottle }, OutAmount = new[] { 1 } });
            _producers = producers;
        }
        private static void Add(Dictionary<int, List<LedgerConversion>> producers, LedgerConversion c)
        {
            foreach (int id in c.Out)
            {
                List<LedgerConversion> list;
                if (!producers.TryGetValue(id, out list)) producers.Add(id, list = new List<LedgerConversion>());
                list.Add(c);
            }
        }

        public int RepairKit(int id)
        {
            var p = Prefab(id);
            return p != null && (p.GetComponent<WeaponData>() != null || p.GetComponent<Eqipment>() != null) ? p.GetRepairKitID() : -1;
        }
        public float MaxWear(int id, int slot)
        {
            var p = Prefab(id);
            if (p == null) return 0f;
            var weapon = p.GetComponent<WeaponData>();
            if (weapon != null) return slot == 0 ? weapon.weaponDurabilityMax : 0f;
            var equipment = p.GetComponent<Eqipment>();
            if (equipment != null) return Field(equipment, slot == 0 ? ArmorMax : ShoesMax);
            return 0f;
        }
        private static readonly System.Reflection.FieldInfo ArmorMax = HarmonyLib.AccessTools.Field(typeof(Eqipment), "armorDurabilityMax");
        private static readonly System.Reflection.FieldInfo ShoesMax = HarmonyLib.AccessTools.Field(typeof(Eqipment), "shoesDurabilityMax");
        private static float Field(Eqipment equipment, System.Reflection.FieldInfo field)
        {
            try { return field == null ? 0f : (float)field.GetValue(equipment); } catch (Exception) { return 0f; }
        }
        // 1.3.0: what an item restores when the game consumes it (LootObjectValue), whether it gives
        // buffs (BuffParameters), and how far a skill goes (the host player's own ZFLevelController).
        public float[] Restores(int id)
        {
            var value = Prefab(id) == null ? null : Prefab(id).GetComponent<LootObjectValue>();
            return value == null ? null : new[] { value.HealValue, value.HungerValue, value.ThirstValue, value.SleepValue };
        }
        public bool HasBuffs(int id) { return Prefab(id) != null && Prefab(id).GetComponent<Zompiercer.UI.BuffParameters>() != null; }
        public int SkillMax(int skill)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var level = player == null ? null : player.zFLevelController;
            if (level == null) return 100;
            int max = skill == 0 ? level.healthMax : skill == 1 ? level.staminaMax : skill == 2 ? level.strenghtMax : level.stealthMax;
            return max > 0 ? max : 100;
        }

        private static InventoryItem Prefab(int id)
        {
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            return prefabs != null && id >= 0 && id < prefabs.Length ? prefabs[id] : null;
        }

        public LedgerItemRule Item(int id)
        {
            LedgerItemRule rule;
            if (_cache.TryGetValue(id, out rule)) return rule;
            var db = ItemsDataBase.Global;
            var prefabs = db == null ? null : db.itemPrefab;
            if (prefabs == null || db.items == null) return null; // database not loaded yet: not cached
            if (id < 0 || id >= prefabs.Length || prefabs[id] == null || prefabs[id].itemID != id || id >= db.items.Count) { _cache[id] = null; return null; }
            var prefab = prefabs[id];
            rule = new LedgerItemRule();
            if (prefab.GetComponent<WeaponData>() != null) rule.Slot0 = LedgerSlot.Wear;
            else if (prefab.GetComponent<Eqipment>() != null) { rule.Slot0 = LedgerSlot.Wear; rule.Slot1 = LedgerSlot.Wear; }
            else if (prefab.GetComponent<NoteNumber>() != null || prefab.GetComponent<KeyNumber>() != null || prefab.GetComponent<GramophoneRecord>() != null)
                rule.Slot0 = LedgerSlot.Exact;
            _cache[id] = rule;
            return rule;
        }

        // The backpack's place in the character's inventory list (the same prefab on both PCs).
        internal static int HostBackpackIndex()
        {
            var c = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            return c == null || c.AllInventory == null ? 0 : Math.Max(0, c.AllInventory.IndexOf(c.inventory));
        }

        // A short list of a profile's belongings for the host's decision.
        internal static string Describe(object[] profile)
        {
            var counts = new Dictionary<int, long>();
            foreach (object[] inventory in (object[])((object[])profile[12])[0]) Count(counts, inventory);
            if (counts.Count == 0) return "ничего";
            var text = new StringBuilder();
            int shown = 0;
            foreach (var pair in counts)
            {
                if (shown++ == 12) { text.Append(", … ещё ").Append(counts.Count - 12); break; }
                if (text.Length > 0) text.Append(", ");
                text.Append(LanStorageGame.ItemName(pair.Key)).Append(pair.Value > 1 ? " ×" + pair.Value : "");
            }
            return text.ToString();
        }
        private static void Count(Dictionary<int, long> counts, object[] items)
        {
            if (items == null) return;
            foreach (object[] item in items)
            {
                long v; counts.TryGetValue((int)item[0], out v); counts[(int)item[0]] = v + (int)item[1];
                var mods = ((object[])item[2])[3] as object[];
                if (mods != null) foreach (var mod in mods) Count(counts, mod as object[]);
            }
        }
    }
}
