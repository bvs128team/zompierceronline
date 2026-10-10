using System;
using System.IO;
using UnityEngine;
using Zompiercer.Inventory;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    // Guest side, main thread only. Captures the local character into a guest
    // profile blob (LanGuestSchema) and restores one through the game's own
    // ZombieFighterController.Load, after validating every value that Load would
    // unbox without checks. Load is given the guest's current position, current
    // world time and no buffs, so it changes neither the clock nor the place; the
    // caller positions the character separately.
    internal static class LanGuestCharacter
    {
        // `acks`: the last storage/world request result and combat state this guest has
        // applied (the host's ledger counts later hand-outs as still on their way).
        internal static byte[] Capture(uint[] acks)
        {
            var manager = GlobalManager.global;
            var c = manager == null ? null : manager.controlledChar;
            var scenes = GlobalSceneManager.global;
            var saves = SaveLoadCore.global;
            if (c == null || scenes == null || saves == null || string.IsNullOrEmpty(scenes.CurrentSceneName)) return null;
            // A dead character is never the progress to keep (0.12.0): it would die again on restore.
            if (LanGuestDeath.Dead || LanGuestDeath.LocalDead() || LanDowned.Down) return null;
            var position = c.transform.position;
            var root = new object[14];
            root[0] = LanGuestSchema.Format2;
            root[13] = new object[] { unchecked((int)acks[0]), unchecked((int)acks[1]) }; root[1] = scenes.CurrentSceneName; root[2] = saves.currentLocation; root[3] = -1;
            root[4] = position.x; root[5] = position.y; root[6] = position.z; root[7] = Yaw(c.transform.eulerAngles.y);
            root[8] = 0f; root[9] = 0f; root[10] = 0f; root[11] = 0f;
            var train = manager.controlledTrain; var car = c.OnTrainCar;
            if (car != null && train != null && car.transform.IsChildOf(train.transform))
            {
                int index = car.GetCarIndex();
                var local = car.transform.InverseTransformPoint(position);
                if (index >= 0 && index < LanProtocol.MaxCars && Mathf.Abs(local.x) <= 100 && Mathf.Abs(local.y) <= 100 && Mathf.Abs(local.z) <= 100)
                {
                    root[3] = index; root[8] = local.x; root[9] = local.y; root[10] = local.z;
                    root[11] = Yaw((Quaternion.Inverse(car.transform.rotation) * c.transform.rotation).eulerAngles.y);
                }
            }
            root[12] = Character(c);
            LanGuestSchema.Validate(root); // never upload what the host would reject
            return LanValueCodec.Encode(root);
        }

        // Mirrors ZombieFighterController.Save() field by field (no Debug.Log spam),
        // without world time (22..26 = 0), positions (3, 4) and global buffs (17).
        private static object[] Character(ZombieFighterController c)
        {
            var bar = c.zombieFighterIndicatorsBar; var level = c.zFLevelController; var weapons = c.zombieFighterFireArmWeapon;
            if (bar == null || level == null || weapons == null || weapons.gunList == null || c.AllInventory == null || InventoryController.global == null)
                throw new InvalidOperationException("Character components unavailable");
            var data = new object[LanGuestSchema.CharacterSlots];
            var inventories = new object[c.AllInventory.Count];
            for (int i = 0; i < inventories.Length; i++) inventories[i] = c.AllInventory[i].Save();
            data[0] = inventories;
            var s = new object[LanGuestSchema.StatSlots];
            s[0] = bar.MaxHealth; s[1] = bar.currentHealth; s[2] = bar.MaxStamina; s[3] = bar.currentStamina;
            s[4] = bar.maxHunger; s[5] = bar.currentHunger; s[6] = bar.maxThirst; s[7] = bar.currentThirst;
            s[8] = bar.maxSleep; s[9] = bar.currentSleep;
            s[10] = level.charLevel; s[11] = level.maxXP; s[12] = level.CurrentXp; s[13] = level.perks;
            s[14] = level.healthLvl; s[15] = level.staminaLvl; s[16] = level.strenghtLvl; s[17] = level.stealthtLvl;
            s[18] = level.strenghtFactor; s[19] = level.stealthFactor;
            for (int i = 22; i <= 26; i++) s[i] = 0;
            data[1] = s;
            var guns = new object[weapons.gunList.Count];
            for (int i = 0; i < guns.Length; i++) guns[i] = new object[] { weapons.gunList[i] == null ? 0 : Math.Max(0, weapons.gunList[i].magazine) };
            data[5] = guns;
            data[6] = level.Save();
            data[7] = bar.currentHealth;
            data[8] = InventoryController.global.selectedBeltIcon;
            data[9] = bar.cure; data[10] = bar.cureTimer; data[11] = bar.regeneration; data[12] = bar.regenerationTimer;
            data[13] = bar.Poisoning; data[14] = bar.poisoningTimer;
            data[15] = weapons.HelmetFlashlightEnabled; data[16] = weapons.FlashlightEnabled;
            return data;
        }

        // Applies a validated profile to the local character (position excluded).
        internal static void Apply(object[] root)
        {
            var c = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (c == null || GlobalWeatherController.global == null || InventoryController.global == null) throw new InvalidOperationException("Character not ready");
            var character = (object[])root[12];
            ValidateForGame(c, character);
            var data = (object[])character.Clone();
            var stats = (object[])((object[])data[1]).Clone();
            data[1] = stats;
            // Native Load writes these five into the world clock: give it the current time.
            var sky = EnviroSky.instance;
            if (sky != null && sky.GameTime != null)
            { stats[22] = sky.GameTime.Seconds; stats[23] = sky.GameTime.Minutes; stats[24] = sky.GameTime.Hours; stats[25] = sky.GameTime.Days; stats[26] = sky.GameTime.Years; }
            else
            {
                var w = GlobalWeatherController.global;
                stats[22] = w.currentSecond; stats[23] = w.currentMinute; stats[24] = w.currentHour; stats[25] = w.currentDay; stats[26] = w.currentYear;
            }
            data[3] = c.transform.position; data[4] = c.transform.eulerAngles; data[17] = null;
            // 1.4.19: poisoning ends with the game's timed buff (Bitter Breath: StopBuff clears it); buffs are not part
            // of the profile, so a profile stored poisoned left the player poisoned for good (a yellow screen, health
            // draining) after a join or a correction. It stays only while that buff still runs here.
            if (data[13] is bool && (bool)data[13] && !BitterBreath()) { data[13] = false; data[14] = 0f; }
            // A profile stored dead (before 0.12.0) would kill the character at once.
            if (stats[0] is float && stats[1] is float && (float)stats[1] <= 0f)
            {
                float health = Math.Max(1f, (float)stats[0] * LanGuestDeath.RespawnHealth);
                stats[1] = health; data[7] = health;
            }
            else if (data[7] is float && (float)data[7] <= 0f && stats[1] is float) data[7] = stats[1];
            // Inventory.Load(empty) keeps old items: empty every inventory first, natively.
            foreach (var inventory in c.AllInventory)
            {
                if (inventory == null || inventory.content == null) continue;
                foreach (var item in inventory.content.ToArray()) if (item != null && item.amount > 0) inventory.Extract(item, item.amount);
            }
            c.Load(data);
        }

        private static HarmonyLib.AccessTools.FieldRef<Zompiercer.GlobalStatsController, bool> _bitterBreath;
        private static bool _bitterBreathFailed;
        private static bool BitterBreath()
        {
            var stats = Zompiercer.GlobalStatsController.Global;
            if (stats == null || _bitterBreathFailed) return false;
            try
            {
                if (_bitterBreath == null) _bitterBreath = HarmonyLib.AccessTools.FieldRefAccess<Zompiercer.GlobalStatsController, bool>("_isBitterBreath");
                return _bitterBreath(stats);
            }
            catch (Exception) { _bitterBreathFailed = true; return false; }
        }

        // Everything native Load would unbox or index without a check.
        private static void ValidateForGame(ZombieFighterController c, object[] character)
        {
            var inventories = (object[])character[0];
            Require(c.AllInventory != null && inventories.Length == c.AllInventory.Count, "inventory layout differs from this character");
            foreach (var inventory in inventories) ValidateItems((object[])inventory, 0);
            var weapons = c.zombieFighterFireArmWeapon;
            Require(weapons != null && weapons.gunList != null && ((object[])character[5]).Length <= weapons.gunList.Count, "weapon layout");
        }

        // Every item value that native InventoryItem.Load would unbox without a check.
        internal static void ValidateItems(object[] items, int depth)
        {
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            Require(prefabs != null && depth <= 2, "item database");
            foreach (object[] item in items)
            {
                int id = (int)item[0];
                Require(id < prefabs.Length && prefabs[id] != null && prefabs[id].itemID == id && ItemsDataBase.Global.items != null && id < ItemsDataBase.Global.items.Count, "unknown item " + id);
                var extra = (object[])item[2];
                var prefab = prefabs[id];
                if (prefab.GetComponent<Eqipment>() != null) Require(extra[0] is float && extra[1] is float, "equipment durability of item " + id);
                var mods = prefab.GetComponent<EquipmentModsItem>();
                if (mods != null && extra[3] != null)
                {
                    var saved = (object[])extra[3];
                    Require(mods.Slots != null && saved.Length >= mods.Slots.Length, "equipment mod slots of item " + id);
                    for (int i = 0; i < mods.Slots.Length; i++) if (saved[i] != null) ValidateItems((object[])saved[i], depth + 1);
                }
            }
        }

        private static float Yaw(float value) { value = Mathf.Repeat(value, 360f); return value >= 360f ? 0f : value; }
        private static void Require(bool condition, string what) { if (!condition) throw new InvalidDataException(what); }
    }
}
