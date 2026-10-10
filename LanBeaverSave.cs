using System;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;

namespace ZompiercerLAN
{
    internal static class LanBeaverSave
    {
        private static Harmony _harmony;
        private static ManualLogSource _log;
        private static bool _installed;
        internal static bool Ready { get { return _installed && Failure == null; } }
        internal static string Failure { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
            if (Ready || Failure != null) return;
            try
            {
                _harmony = new Harmony("local.zompiercer.lan.beaver-save");
                var save = AccessTools.Method(typeof(BinarySaver), "Save", new[] { typeof(object), typeof(string) });
                var load = AccessTools.Method(typeof(BinarySaver), "Load", new[] { typeof(string) });
                if (save == null || load == null || load.ReturnType != typeof(object))
                    throw new MissingMethodException("Native BinarySaver signatures changed.");
                _harmony.Patch(save, prefix: new HarmonyMethod(typeof(LanBeaverSave), nameof(SavePrefix)));
                _harmony.Patch(load, postfix: new HarmonyMethod(typeof(LanBeaverSave), nameof(LoadPostfix)));
                _installed = true;
            }
            catch (Exception ex)
            {
                Failure = ex.Message;
                if (_harmony != null) _harmony.UnpatchSelf();
                if (_log != null) _log.LogError("Beaver native-save protection unavailable: " + ex);
            }
        }

        internal static void Shutdown()
        {
            // Keep protection until process exit: live custom objects may still be saved
            // after plugin destruction. Restoration is gated on item registration Ready.
        }

        private static bool IsGameSave(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".tac", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".scene", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".sdt", StringComparison.OrdinalIgnoreCase);
        }

        private static void SavePrefix(ref object __0, string __1)
        {
            if (!IsGameSave(__1)) return;
            try
            {
                int count;
                __0 = LanBeaverSaveCodec.Transform(__0, LanBeaverItem.ItemId, LanBeaverItem.FallbackId, true, false, out count);
            }
            catch (Exception ex)
            {
                Failure = ex.Message;
                if (_log != null) _log.LogError("Native save aborted before opening file: figurine conversion failed. " + ex);
                throw;
            }
        }

        private static void LoadPostfix(string __0, ref object __result)
        {
            if (!IsGameSave(__0) || __result == null) return;
            try
            {
                int count;
                object restored = LanBeaverSaveCodec.Transform(__result, LanBeaverItem.ItemId, LanBeaverItem.FallbackId,
                    false, LanBeaverItem.Ready, out count);
                __result = restored;
                if (count > 0 && !LanBeaverItem.Ready && _log != null)
                    _log.LogWarning("Loaded " + count + " beaver figurines as native toys because custom item registration is unavailable.");
            }
            catch (Exception ex)
            {
                // Original deserialized graph is still vanilla-loadable and untouched.
                if (_log != null) _log.LogError("Figurine restoration failed; keeping native toys. " + ex);
            }
        }
    }
}
