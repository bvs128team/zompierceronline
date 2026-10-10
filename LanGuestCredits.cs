using System;
using HarmonyLib;

namespace ZompiercerLAN
{
    // Host (1.3.0): what the host's world gives the guest, credited to the guest's checked stats
    // (LanGuestStats): the experience of every zombie dying here while the guest plays here (the game
    // gives each player that experience on its own copy), quest experience the guest hands in,
    // getting up after being downed, respawning, sleeping together. Main thread only.
    internal static class LanGuestCredits
    {
        private static Harmony _harmony;
        internal static string Failure { get; private set; }
        // Set by the plugin every frame: the attached guest's checked stats (null: none), and whether
        // it plays in this world now (zombie experience counts only then).
        internal static Func<LanGuestStats> Stats;
        internal static Func<bool> GuestHere;

        internal static void Initialize(Action<string> log)
        {
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.guest-credits");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(ZombieDamagDetector), "Dead"), prefix: new HarmonyMethod(typeof(LanGuestCredits), nameof(DeadPrefix)), postfix: new HarmonyMethod(typeof(LanGuestCredits), nameof(DeadPostfix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Guest experience hook unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; Stats = null; GuestHere = null; }

        private static LanGuestStats Current() { var stats = Stats; return stats == null ? null : stats(); }

        private static void DeadPrefix(ZombieDamagDetector __instance, out bool __state)
        { __state = __instance != null && __instance.zombieAIController != null && __instance.zombieAIController.dead; }
        private static void DeadPostfix(ZombieDamagDetector __instance, bool __state)
        {
            if (__state || __instance == null || __instance.zombieAIController == null || !__instance.zombieAIController.dead) return;
            if (GuestHere != null && GuestHere()) Current()?.CreditXp(__instance.XPReward);
        }

        internal static void Xp(float experience) { Current()?.CreditXp(experience); }
        internal static void Revive() { Current()?.CreditRevive(); }
        internal static void Health(float amount) { Current()?.CreditHealth(amount); }
        internal static void Respawn() { Current()?.CreditRespawn(); }
        internal static void Sleep(int hours) { Current()?.CreditSleep(hours); }
    }
}
