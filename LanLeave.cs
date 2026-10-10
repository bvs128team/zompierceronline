using System;
using HarmonyLib;

namespace ZompiercerLAN
{
    // Leaving the game (1.4.5). The game's own "exit to main menu" ends this player's session (the
    // plugin tells the partner with a Leave packet), and a guest whose host left goes back to the main
    // menu the same way instead of staying in a frozen copy of the host's world. Main thread only.
    internal static class LanLeave
    {
        private static Harmony _harmony;
        private static bool _bypass;
        internal static string Failure { get; private set; }
        // The local player chose "exit to main menu" in the game's menu.
        internal static Action Leaving;

        internal static void Initialize(Action<string> log)
        {
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.leave");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Zompiercer.GUI.GUIManager), "ExitGameToMainMenuYesButton"), prefix: new HarmonyMethod(typeof(LanLeave), nameof(ExitPrefix)));
            }
            catch (Exception ex)
            {
                // Without it the partner notices a player who left by the silence, as before 1.4.5.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Leave hook unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; Leaving = null; }

        private static void ExitPrefix()
        {
            if (_bypass) return;
            try { Leaving?.Invoke(); }
            catch (Exception) { } // leaving the world goes on regardless
        }

        // The game's own way back to the main menu (as its menu's "yes").
        internal static bool ToMainMenu()
        {
            var gui = Zompiercer.GUI.GUIManager.Global;
            if (gui == null) return false;
            _bypass = true;
            try { gui.ExitGameToMainMenuYesButton(); return true; }
            finally { _bypass = false; }
        }
    }
}
