using System;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // A boss's reward for two (1.4.5). The railway worker boss drops one bag (its key and loot,
    // ZombieAIController.SetDead) in the host's world. Experience already goes to both players
    // (LanGuestCredits); the bag would go to whoever is first. While the guest plays in the host's
    // world, the boss drops a second bag the same way (its own roll, its own key) next to the first.
    // Host only. Main thread only.
    internal static class LanBossReward
    {
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.boss-reward");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "SetDead"),
                    prefix: new HarmonyMethod(typeof(LanBossReward), nameof(SetDeadPrefix)), postfix: new HarmonyMethod(typeof(LanBossReward), nameof(SetDeadPostfix)));
            }
            catch (Exception ex)
            {
                // Without it a boss drops one bag, as before 1.4.5.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Boss reward hook unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; }

        private static void SetDeadPrefix(ZombieAIController __instance, out bool __state) { __state = __instance != null && __instance.dead; }
        private static void SetDeadPostfix(ZombieAIController __instance, bool value, bool __state)
        {
            if (!value || __state || __instance == null || LanSaveIsolation.Active || !LanBossArena.GuestPlays || __instance.ID != LanBossArena.BossId) return;
            try { SecondBag(__instance); }
            catch (Exception ex) { if (!_logged) { _logged = true; _log?.Invoke("Second boss bag not dropped: " + ex.GetType().Name + ": " + ex.Message); } }
        }

        // As SetDead drops its bag: the bag prefab above the boss, filled by the boss's loot spawner,
        // its key first, then closed for putting things in.
        private static void SecondBag(ZombieAIController boss)
        {
            var global = GlobalObjectLootController.global;
            if (UnityEngine.Random.value >= boss.LootDropChance || boss.PrefabLootSpawner == null || global == null || global.PrefabZombieLoot == null) return;
            var at = boss.transform.position + Vector3.up * global.LootVerticalPosOffset + boss.transform.right * .8f;
            var bag = Object.Instantiate(global.PrefabZombieLoot, at, Quaternion.identity);
            var body = bag.GetComponent<Rigidbody>();
            if (body != null) { body.velocity = Vector3.up * global.LootVerticalSpeed; body.angularVelocity = Vector3.one * global.LootAngularSpeed; }
            var inventory = bag.GetComponentInChildren<Inventory>();
            if (inventory == null) { Object.Destroy(bag.gameObject); return; }
            var spawner = Object.Instantiate(boss.PrefabLootSpawner);
            try
            {
                spawner.inventory = inventory;
                if (boss.AddItemInLoot != null)
                {
                    inventory.Add(boss.AddItemInLoot.itemID, 1, false, true);
                    var key = inventory.content.Count > 0 && inventory.content[0] != null ? inventory.content[0].GetComponent<KeyNumber>() : null;
                    if (key != null) key.keyNumber = boss.AddItemInLootKeyID;
                }
                spawner.GuaranteedFilling();
            }
            finally { Object.Destroy(spawner.gameObject); }
            inventory.maxWeight = 0f;
            _log?.Invoke("The boss dropped a second bag for the guest");
        }
    }
}
