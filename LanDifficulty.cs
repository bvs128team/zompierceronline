using System;
using HarmonyLib;
using UnityEngine;

namespace ZompiercerLAN
{
    // Difficulty for two (1.4.4). The game's difficulty (DifficultyManager) belongs to the world: the
    // host's settings, saved with its world. Two players kill zombies twice as fast and share one
    // world's loot, so while a guest plays the host's game adds to it:
    //  - zombie health x[Coop] ZombieHealth (1.3) while the guest is in the host's world: 1.4.20, every
    //    blow, shot and blast a zombie takes (ZombieHP.CauseDamage) is divided by it. Before, the game's
    //    DifficultyManager.ZombieHP was raised; but the game's own swing multiplies its damage by that
    //    value before CauseDamage divides by it, so the host's melee never felt it (zombies too weak for the
    //    host, while the guest's swing, counted apart, did);
    //  - loot x[Coop] Loot (1.5) while a guest is connected: the amount of each item a container or a
    //    zombie's bag gets when the game fills it (ObjectLootController.ApplyAmountMultiplier), not the
    //    chance that it gets one, so on average there is that much more.
    // The guest plays by the host's difficulty (zombie damage, weapon recoil and wobble, players on the
    // map; WorldState) and gets its own settings back when it leaves. Main thread only.
    internal static class LanDifficulty
    {
        internal const float MinFactor = 1f, MaxFactor = 3f;
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Host: the [Coop] factors, and whether a guest is connected (loot) and in this world (health).
        internal static float ZombieHealth = 1.3f, Loot = 1.5f;
        internal static bool LootActive;
        private static float _guestSeen = -100f;
        // Guest: its own settings while it plays by the host's.
        private static DifficultySnapshot _own;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.difficulty");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(ObjectLootController), "ApplyAmountMultiplier"), prefix: new HarmonyMethod(typeof(LanDifficulty), nameof(AmountPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieHP), "CauseDamage", new[] { typeof(float) }), prefix: new HarmonyMethod(typeof(LanDifficulty), nameof(DamagePrefix)));
            }
            catch (Exception ex)
            {
                // Without these the world plays at the host's own difficulty, as before 1.4.4.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Co-op difficulty hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { HostRestore(); GuestRestore(); _harmony?.UnpatchSelf(); _harmony = null; LootActive = false; }

        internal static float Clamp(float factor) { return float.IsNaN(factor) ? MinFactor : Mathf.Clamp(factor, MinFactor, MaxFactor); }

        // ---- Host ----

        // Every frame: zombie health for two while the guest is in this world (and a few seconds after
        // it was last seen, so a short stall does not flip it).
        internal static void HostTick(bool hosting, bool guestInWorld)
        {
            if (Failure != null) return;
            float now = Time.unscaledTime;
            if (hosting && guestInWorld) _guestSeen = now;
            bool active = hosting && now - _guestSeen < 5f && ZombieHealth > MinFactor;
            float factor = active ? Clamp(ZombieHealth) : MinFactor;
            bool was = _healthFactor > MinFactor;
            _healthFactor = factor;
            if (was != active) _log?.Invoke(active ? "Zombie health x" + factor.ToString("0.##") + " while the guest is in this world" : "Zombie health back to the host's setting");
        }
        internal static void HostRestore() { _guestSeen = -100f; _healthFactor = MinFactor; }
        // 1.4.20: the damage a zombie takes, for two (HostTick sets the factor; 1 otherwise).
        private static float _healthFactor = MinFactor;
        private static void DamagePrefix(ref float damage)
        {
            if (_healthFactor > MinFactor && !float.IsNaN(damage) && !float.IsInfinity(damage)) damage /= _healthFactor;
        }
        private static void AmountPrefix(ref float itemAmount)
        {
            if (LootActive && Loot > MinFactor && !float.IsNaN(itemAmount)) itemAmount *= Clamp(Loot);
        }

        // What the guest plays by (WorldState).
        internal static DifficultySnapshot Capture()
        {
            var settings = DifficultyManager.Global;
            if (settings == null) return null;
            var shot = new DifficultySnapshot { ZombieDamage = Mathf.Clamp(settings.ZombieDamage, 0f, 10f), Recoil = Mathf.Clamp(settings.WeaponRecoil, 0f, 10f),
                Wobble = Mathf.Clamp(settings.WeaponAimWobble, 0f, 10f), ShowOnMap = settings.ShowPlayerOnMap };
            return LanProtocol.ValidDifficulty(shot) ? shot : null;
        }

        // ---- Guest ----

        internal static void GuestApply(DifficultySnapshot host)
        {
            var settings = DifficultyManager.Global;
            if (host == null || settings == null || !LanProtocol.ValidDifficulty(host)) return;
            if (_own == null)
            {
                _own = new DifficultySnapshot { ZombieDamage = settings.ZombieDamage, Recoil = settings.WeaponRecoil, Wobble = settings.WeaponAimWobble, ShowOnMap = settings.ShowPlayerOnMap };
                _log?.Invoke("Playing by the host's difficulty: zombie damage x" + host.ZombieDamage.ToString("0.##"));
            }
            settings.ZombieDamage = host.ZombieDamage; settings.WeaponRecoil = host.Recoil;
            settings.WeaponAimWobble = host.Wobble; settings.ShowPlayerOnMap = host.ShowOnMap;
        }
        internal static void GuestRestore()
        {
            var own = _own; _own = null;
            var settings = DifficultyManager.Global;
            if (own == null || settings == null) return;
            settings.ZombieDamage = own.ZombieDamage; settings.WeaponRecoil = own.Recoil;
            settings.WeaponAimWobble = own.Wobble; settings.ShowPlayerOnMap = own.ShowOnMap;
        }
    }
}
