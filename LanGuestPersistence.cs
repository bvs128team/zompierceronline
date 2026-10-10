using System;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    // Host side: ties LanGuestStore to the game's own save lifecycle.
    //  - FileBrowser.SaveFileInFolder("TrainAndChar.tac") completed: the native
    //    checkpoint is fully written, so the sidecar is written next to it.
    //  - SaveLoadCore.LoadGame(slot) + OnSceneLoaded: the sidecar of exactly that
    //    checkpoint is verified and loaded (or the guests start fresh).
    //  - GUIManager.StartNewGameButton: a new world lineage.
    // All hooks are inert in a client session (LanSaveIsolation active).
    internal static class LanGuestPersistence
    {
        internal static readonly LanGuestStore Store = new LanGuestStore();
        private static Harmony _harmony;
        private static ManualLogSource _log;
        private static string _pendingSlot;
        internal static string LastResult { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
            if (_harmony != null) return;
            var harmony = new Harmony("local.zompiercer.lan.guest-persistence");
            try
            {
                harmony.Patch(Method(typeof(FileBrowser), "SaveFileInFolder"), postfix: new HarmonyMethod(typeof(LanGuestPersistence), nameof(AfterSaveFile)));
                harmony.Patch(Method(typeof(SaveLoadCore), "LoadGame"), prefix: new HarmonyMethod(typeof(LanGuestPersistence), nameof(BeforeLoadGame)));
                harmony.Patch(Method(typeof(SaveLoadCore), "OnSceneLoaded"), postfix: new HarmonyMethod(typeof(LanGuestPersistence), nameof(AfterSceneLoaded)));
                harmony.Patch(Method(typeof(Zompiercer.GUI.GUIManager), "StartNewGameButton"), prefix: new HarmonyMethod(typeof(LanGuestPersistence), nameof(BeforeNewGame)));
                _harmony = harmony;
            }
            catch (Exception ex)
            {
                harmony.UnpatchSelf();
                _log.LogError("Guest persistence hooks unavailable; guest profiles are kept only until the game closes: " + ex.Message);
            }
        }

        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; }

        private static System.Reflection.MethodInfo Method(Type type, string name)
        {
            var method = AccessTools.DeclaredMethod(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void AfterSaveFile(string __1, string __2)
        {
            if (LanSaveIsolation.Active || __1 != "TrainAndChar.tac") return;
            try
            {
                var saves = SaveLoadCore.global;
                if (saves == null || !Leaf(__2)) return;
                string result;
                if (Store.Write(Path.Combine(saves.SavesPath, __2), out result))
                { LastResult = "Профили друзей записаны в сохранение «" + __2 + "»"; _log.LogInfo("Guest profiles saved with '" + __2 + "': " + result); }
            }
            catch (Exception ex) { LastResult = "Не удалось записать профили друзей"; _log.LogWarning("Guest profiles not saved with '" + __2 + "': " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void BeforeLoadGame(string __0)
        {
            if (LanSaveIsolation.Active) return;
            _pendingSlot = Leaf(__0) ? __0 : null;
        }

        private static void AfterSceneLoaded()
        {
            if (LanSaveIsolation.Active || _pendingSlot == null) return;
            string slot = _pendingSlot; _pendingSlot = null;
            try
            {
                var saves = SaveLoadCore.global;
                string result;
                Store.Load(saves == null ? null : Path.Combine(saves.SavesPath, slot), out result);
                LastResult = Store.Count != 0 ? "Загружены профили друзей: " + Store.Count : null;
                _log.LogInfo("Loaded '" + slot + "': " + result);
            }
            catch (Exception ex) { Store.NewWorld(); _log.LogWarning("Guest sidecar of '" + slot + "' ignored: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void BeforeNewGame()
        {
            if (LanSaveIsolation.Active) return;
            _pendingSlot = null; Store.NewWorld(); LastResult = null;
        }

        private static bool Leaf(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
        }
    }
}
