using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    // Process-lifetime latch: disconnect and plugin shutdown must NOT restore solo paths.
    // Patches are inert until Activate succeeds. No original save is moved or deleted.
    internal static class LanSaveIsolation
    {
        private static Harmony _harmony;
        private static ManualLogSource _log;
        private static string _path, _originalPath;
        private static bool _installed;
        internal static bool Active { get { return _path != null; } }
        internal static bool Ready { get { return _installed && Failure == null; } }
        internal static string Failure { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
            if (_installed || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.save-isolation");
            try
            {
                Patch(typeof(SaveLoadCore), "Awake", nameof(AwakePrefix));
                Patch(typeof(SaveLoadCore), "Update", nameof(CorePrefix));
                Patch(typeof(SaveLoadCore), "SaveFromMenu", nameof(SavePrefix));
                Patch(typeof(SaveLoadCore), "PerformAutoSave", nameof(AllowNormalSession));
                Patch(typeof(SaveLoadCore), "LoadGame", nameof(AllowNormalSession));
                Patch(typeof(SaveLoadCore), "OnSceneLoaded", nameof(SceneLoadedPrefix));
                Patch(typeof(FileBrowser), "CheckSaveFolder", nameof(RequireIsolation));
                Patch(typeof(FileBrowser), "SaveFileInFolder", nameof(SaveFilePrefix));
                Patch(typeof(FileBrowser), "LoadFile", nameof(LoadFilePrefix));
                Patch(typeof(FileBrowser), "CreateFolder", nameof(FolderPrefix));
                Patch(typeof(FileBrowser), "GetAllFolders", nameof(FolderPrefix));
                // Do not recursively delete even a session folder: it may contain links.
                Patch(typeof(FileBrowser), "DeleteFile", nameof(AllowNormalSession));
                Patch(typeof(FileBrowser), "CopyFilesRecursively", nameof(AllowNormalSession));
                Patch(typeof(BinarySaver), "Save", nameof(BinarySavePrefix));
                Patch(typeof(BinarySaver), "Load", nameof(BinaryLoadPrefix));
                // These exact native settings methods share the save-root fields, but
                // their settings must stay in the original directory. Replace field
                // reads, never temporarily restore shared mutable save paths.
                Settings("Zompiercer.Inputs.InputEditor", "Start", "SaveHotkeys", "LoadHotkeys", "ResetBindingsOnStart");
                Settings("Zompiercer.Inputs.ControllerEditorWindow", "Save", "Load");
                Settings("Zompiercer.GUI.VideoSettings", "SaveNewSettings", "LoadSettings");
                _installed = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                // Activation has not occurred, so partial inert patches can be removed.
                if (!Active) _harmony.UnpatchSelf();
            }
        }

        // The caller must back up saves first and call this BEFORE starting any
        // client scene load or applying host world state. Existing empty folders are
        // accepted for compatibility with the caller's fresh GUID directory creation.
        internal static bool Activate(string path)
        {
            try
            {
                if (!Ready) throw new InvalidOperationException("Save isolation hooks unavailable.");
                string target = Canonical(path);
                if (Active)
                {
                    if (!Same(target, _path)) throw new InvalidOperationException("Client isolation cannot change until restart.");
                    return Enforce();
                }
                var saves = SaveLoadCore.global;
                if (saves == null || saves.LoadingNow || saves.GetLoadingPhase ||
                    (GlobalSceneManager.global != null && GlobalSceneManager.global.SceneCurrentlyLoading))
                    throw new InvalidOperationException("Cannot isolate saves while native loading is active.");
                string root = Canonical(Path.Combine(Paths.BepInExRootPath, "LAN-sessions"));
                if (!Same(Path.GetDirectoryName(target), root))
                    throw new IOException("Client directory must be a fresh direct child of LAN-sessions.");
                string original = Canonical(saves.SavesPath);
                if (Within(target, original) || Within(original, target))
                    throw new IOException("Client and original save directories overlap.");
                LanInventoryGuard.RejectLinks(target);
                LanInventoryGuard.RejectLinks(original);
                if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length != 0)
                    throw new IOException("Client session directory is not empty.");
                Directory.CreateDirectory(target);
                LanInventoryGuard.RejectLinks(target);
                _originalPath = original;
                _path = target; // One-way latch before changing the first native field.
                if (!Enforce()) return false;
                saves.NowLoadingSaveFileName = "";
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        internal static bool Enforce()
        {
            if (!Active) return Ready;
            try
            {
                if (!Ready) return false;
                LanInventoryGuard.RejectLinks(_path);
                if (!Directory.Exists(_path)) throw new IOException("Client save directory disappeared.");
                var saves = SaveLoadCore.global;
                if (saves == null) throw new InvalidOperationException("Native save manager is unavailable.");
                Apply(saves);
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        private static void Apply(SaveLoadCore saves)
        {
            saves.SavesPath = _path;
            saves.SavesPathSlash = _path + Path.DirectorySeparatorChar;
            saves.AutoSaveEnabled = false;
            GlobalSceneManager.SaveLocationStartFileAfterSceneLoaded = false;
        }

        private static void Fail(Exception ex)
        {
            if (Failure != null) return;
            Failure = ex.GetType().Name + ": " + ex.Message;
            _log?.LogError("Client save isolation failed; restart required: " + Failure);
        }

        private static void Patch(Type type, string method, string prefix)
        {
            var target = AccessTools.DeclaredMethod(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(LanSaveIsolation), prefix) { priority = Priority.First });
        }

        private static void Settings(string typeName, params string[] methods)
        {
            var type = typeof(SaveLoadCore).Assembly.GetType(typeName, true);
            foreach (string name in methods)
            {
                var target = AccessTools.DeclaredMethod(type, name);
                if (target == null) throw new MissingMethodException(typeName, name);
                _harmony.Patch(target, transpiler: new HarmonyMethod(typeof(LanSaveIsolation), nameof(SettingsPaths)));
            }
        }

        private static IEnumerable<CodeInstruction> SettingsPaths(IEnumerable<CodeInstruction> instructions)
        {
            int found = 0;
            foreach (var instruction in instructions)
            {
                var field = instruction.operand as FieldInfo;
                if (instruction.opcode == OpCodes.Ldfld && field != null && field.DeclaringType == typeof(SaveLoadCore) &&
                    field.Name == "SavesPathSlash")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(LanSaveIsolation), nameof(SettingsPath));
                    found++;
                }
                yield return instruction;
            }
            if (found == 0) throw new InvalidOperationException("Native settings save-path layout changed.");
        }

        private static string SettingsPath(SaveLoadCore saves)
        {
            if (!Active) return saves.SavesPathSlash;
            try
            {
                LanInventoryGuard.RejectLinks(_originalPath);
                return _originalPath + Path.DirectorySeparatorChar;
            }
            catch (Exception ex) { Fail(ex); throw; }
        }

        private static bool AwakePrefix(SaveLoadCore __instance)
        {
            if (!Active) return true;
            // Native Awake restores normal paths and migrates ./Saves. Replace only
            // this tiny initializer while isolated; never run its legacy move/copy.
            SaveLoadCore.global = __instance;
            __instance.PersistentDataPath = UnityEngine.Application.persistentDataPath;
            Apply(__instance);
            Enforce();
            return false;
        }

        private static bool CorePrefix(SaveLoadCore __instance)
        {
            if (!Active) return true;
            if (!Enforce()) return false;
            Apply(__instance);
            return true;
        }

        private static bool SceneLoadedPrefix(SaveLoadCore __instance)
        {
            if (!Active) return true;
            __instance.NowLoadingSaveFileName = "";
            return CorePrefix(__instance);
        }

        private static bool AllowNormalSession() { return !Active; }
        private static void RequireIsolation()
        {
            if (Active && !Enforce()) throw new IOException("Client save isolation is unavailable; restart required.");
        }

        private static bool SavePrefix(SaveLoadCore __instance, string __0)
        {
            if (!Active) return true;
            if (!CorePrefix(__instance)) return false;
            if (!Leaf(__0)) { Fail(new IOException("Invalid isolated save name.")); return false; }
            return true;
        }

        private static void SaveFilePrefix(string __1, string __2)
        {
            if (!Active) return;
            RequireIsolation();
            if (!Leaf(__1) || !Leaf(__2)) Reject("Invalid isolated save filename or folder.");
            ValidatePath(Path.Combine(_path, __2, __1), false);
        }

        private static void FolderPrefix(string __0)
        {
            if (!Active) return;
            RequireIsolation();
            ValidatePath(__0, true);
        }

        private static void LoadFilePrefix(string __0)
        {
            if (!Active) return;
            RequireIsolation();
            ValidatePath(__0, false);
        }

        private static void BinarySavePrefix(string __1) { BinaryPath(__1); }
        private static void BinaryLoadPrefix(string __0) { BinaryPath(__0); }
        private static void BinaryPath(string path)
        {
            if (!Active) return;
            // Bindings is the only native non-save BinarySaver caller. Other settings
            // use direct text I/O and narrow field-read substitutions above.
            string full;
            try { full = Canonical(path); }
            catch (Exception ex) { Fail(ex); throw; }
            if (Same(full, Path.Combine(_originalPath, "Bindings.hotkeys")))
            {
                try { LanInventoryGuard.RejectLinks(full); return; }
                catch (Exception ex) { Fail(ex); throw; }
            }
            RequireIsolation();
            ValidatePath(full, false);
        }

        private static void ValidatePath(string path, bool allowRoot)
        {
            try
            {
                string full = Canonical(path);
                if (!Within(full, _path) || (!allowRoot && Same(full, _path)))
                    throw new IOException("Native file operation escaped client save directory.");
                // Also reject links at the final filename, not merely root ancestors.
                LanInventoryGuard.RejectLinks(full);
            }
            catch (Exception ex) { Fail(ex); throw; }
        }

        private static void Reject(string message)
        {
            var ex = new IOException(message);
            Fail(ex);
            throw ex;
        }

        private static bool Leaf(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 &&
                !name.EndsWith(".", StringComparison.Ordinal) && !name.EndsWith(" ", StringComparison.Ordinal);
        }

        private static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Empty save path.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool Within(string path, string root)
        { return Same(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }

        internal static void Shutdown()
        {
            // Keep protection after disconnect/plugin destruction: native coroutines
            // can still resume. Only process restart releases an activated guard.
            if (Active) { Enforce(); return; }
            _harmony?.UnpatchSelf();
            _harmony = null;
            _installed = false;
        }
    }
}
