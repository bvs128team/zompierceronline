using System;
using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    // Retains the client's actual character and inventory across the game's normal scene loader.
    internal sealed class ClientSceneTravel
    {
        private readonly ManualLogSource _log;
        private ZombieFighterController _character;
        private bool _enabled, _controllerEnabled, _controlEnabled;
        private int _carIndex = -1;
        private Vector3 _localPosition;
        private float _localYaw, _readySince = -1f;
        internal bool Active { get { return _character != null; } }
        internal string TargetScene { get; private set; }
        internal string Status { get; private set; }
        internal ClientSceneTravel(ManualLogSource log) { _log = log; }

        internal bool Begin(Packet world)
        {
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            var saves = SaveLoadCore.global;
            if (manager == null || scenes == null || saves == null || scenes.SceneCurrentlyLoading || saves.LoadingNow ||
                manager.controlledChar == null || manager.controlledTrain == null) return false;
            if (!ClientWorldValidation.Valid(world) || !ClientTravelHooks.ClientSession || !LanSaveIsolation.Enforce())
            { Status = "Неподходящие данные мира или отсутствует изоляция сохранений"; return false; }
            if (Time.timeScale <= 0f)
            {
                var gui = manager.canvas;
                if (gui != null && gui.inGameMenu != null && gui.inGameMenu.activeInHierarchy) gui.BackToGameButton();
                if (Time.timeScale <= 0f)
                { Status = "Закройте окно паузы для перехода за хостом"; return false; }
            }
            if (!Active)
            {
                _character = manager.controlledChar;
                _enabled = _character.enabled;
                _controllerEnabled = _character.controller != null && _character.controller.enabled;
                _controlEnabled = _character.controlEnabled;
                var car = _character.OnTrainCar;
                _carIndex = car != null && car.transform.IsChildOf(manager.controlledTrain.transform) ? car.GetCarIndex() : -1;
                if (_carIndex >= 0)
                {
                    _localPosition = car.transform.InverseTransformPoint(_character.transform.position);
                    _localYaw = (Quaternion.Inverse(car.transform.rotation) * _character.transform.rotation).eulerAngles.y;
                    if (_localPosition.sqrMagnitude > 10000f) _carIndex = -1;
                }
                LanInventoryGuard.CaptureCheckpoint("before-client-scene-travel", _log);
            }
            Hold();
            TrainSync.DetachPlayer();
            UnityEngine.Object.DontDestroyOnLoad(_character.gameObject);
            // The train is already persistent in the native loader. Keep that invariant explicitly.
            manager.controlledTrain.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(manager.controlledTrain.gameObject);
            TargetScene = world.Scene;
            _readySince = -1f;
            saves.currentLocation = world.Location;
            saves.TrainPositionAfterLoad = -1f;
            saves.TrainRailsIDAfterLoad = -1;
            saves.NewGamePlusStartingPhase = false;
            saves.AutoSaveEnabled = false;
            GlobalSceneManager.SaveLocationStartFileAfterSceneLoaded = false;
            Status = "Переход за хостом: " + world.Scene;
            _log.LogInfo("Client scene travel to " + world.Scene + " epoch=" + world.WorldEpoch +
                " location=" + world.Location + " rails=" + world.RailsId + " passengerCar=" + _carIndex);
            try
            {
                ClientTravelHooks.AuthorizeLoad(world);
                scenes.LoadLevel(world.Scene, world.TrainPosition, world.RailsId);
            }
            catch (Exception ex)
            {
                // Never resume a partially loaded client world after a loader failure.
                ClientTravelHooks.Fail(ex.GetType().Name);
                throw;
            }
            return true;
        }

        internal void Hold()
        {
            if (!Active) return;
            _character.enabled = false;
            _character.controlEnabled = false;
            if (_character.controller != null) _character.controller.enabled = false;
        }

        internal bool LoadFinished()
        {
            if (!Active) return false;
            var scenes = GlobalSceneManager.global;
            var saves = SaveLoadCore.global;
            if (ClientTravelHooks.LoadFailure != null || !LanSaveIsolation.Enforce() || scenes == null ||
                scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading || scenes.CurrentSceneName != TargetScene || saves == null || saves.LoadingNow)
            { _readySince = -1f; return false; }
            // Wait for the complete native coroutine, not its early SceneCurrentlyLoading flag.
            if (_readySince < 0f) _readySince = Time.unscaledTime;
            return Time.unscaledTime - _readySince >= 0.1f;
        }

        internal bool PassengerPosition(out Vector3 position, out float yaw)
        {
            var car = _carIndex < 0 ? null : TrainSync.GetCar(_carIndex);
            position = Vector3.zero; yaw = 0f;
            if (car == null) return false;
            position = car.transform.TransformPoint(_localPosition);
            yaw = (car.transform.rotation * Quaternion.Euler(0f, _localYaw, 0f)).eulerAngles.y;
            return true;
        }

        internal void Complete()
        {
            if (!Active) return;
            _character.enabled = _enabled;
            _character.controlEnabled = _controlEnabled;
            if (_character.controller != null) _character.controller.enabled = _controllerEnabled;
            LanInventoryGuard.CaptureCheckpoint("after-client-scene-travel", _log);
            _log.LogInfo("Client scene travel completed: " + TargetScene);
            _character = null;
            _carIndex = -1;
            TargetScene = null;
            Status = null;
        }
    }

    internal static class ClientWorldValidation
    {
        // Use the installed game's own playable-scene catalogue, not arbitrary
        // build scenes (menus, loading screens, etc.) supplied by a peer.
        internal static bool Valid(Packet world)
        {
            return LanWorldCatalog.Valid(world);
        }
    }

    internal static class ClientTravelHooks
    {
        private static Harmony _harmony;
        private static int _activeLoads;
        private static Packet _authorized;
        private static double _loadStarted;
        private static bool _awaitingLoad;
        private static readonly System.Diagnostics.Stopwatch LoadTime = System.Diagnostics.Stopwatch.StartNew();
        internal static bool Loading { get { return _activeLoads > 0; } }
        internal static bool Ready { get { return _harmony != null; } }
        internal static bool ClientSession;
        internal static string LoadFailure { get; private set; }
        internal static void Init()
        {
            if (_harmony != null) return;
            var harmony = new Harmony("local.zompiercer.lan.scene-travel");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(LoadTrigger), "OnTriggerEnter"),
                    prefix: new HarmonyMethod(typeof(ClientTravelHooks), nameof(AllowLocalTrigger)));
                harmony.Patch(AccessTools.Method(typeof(GlobalSceneManager), "LoadLevelCoroutine"),
                    postfix: new HarmonyMethod(typeof(ClientTravelHooks), nameof(TrackLoad)));
                harmony.Patch(AccessTools.Method(typeof(GlobalSceneManager), "LoadLevel"),
                    prefix: new HarmonyMethod(typeof(ClientTravelHooks), nameof(CheckLoad)));
                harmony.Patch(AccessTools.Method(typeof(GlobalSceneManager), "SetTrainOnRails"),
                    prefix: new HarmonyMethod(typeof(ClientTravelHooks), nameof(PlaceTrain)));
                _harmony = harmony;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        private static bool AllowLocalTrigger() { return !ClientSession; }
        internal static void Fail(string reason) { if (LoadFailure == null) LoadFailure = reason; }
        internal static void AuthorizeLoad(Packet world)
        {
            if (!ClientSession || !LanSaveIsolation.Active || !LanSaveIsolation.Enforce() ||
                LoadFailure != null || !LanWorldCatalog.Valid(world) || Loading || _awaitingLoad)
                throw new InvalidOperationException("Client load authorization failed");
            // Immutable copy: newer host updates cannot change an in-flight load.
            _authorized = new Packet { Scene = world.Scene, Location = world.Location, RailsId = world.RailsId,
                TrainPosition = world.TrainPosition, WorldEpoch = world.WorldEpoch };
            _loadStarted = LoadTime.Elapsed.TotalSeconds;
            _awaitingLoad = true;
        }
        internal static void Tick()
        {
            if (ClientSession && (Loading || _awaitingLoad) && LoadTime.Elapsed.TotalSeconds - _loadStarted > 120)
                Fail("ClientLoadTimeout");
        }
        private static void CheckLoad(string __0, float __1, int __2)
        {
            if (!ClientSession) return;
            if (_authorized == null || !_awaitingLoad || LoadFailure != null || !LanSaveIsolation.Enforce() ||
                _authorized.Scene != __0 || _authorized.TrainPosition != __1 || _authorized.RailsId != __2)
            { Fail("UnauthorizedClientLoad"); throw new InvalidOperationException("Client scene load rejected"); }
        }
        private static bool PlaceTrain(int __0, float __1)
        {
            if (!ClientSession) return true;
            try
            {
                if (_authorized == null || !Loading || LoadFailure != null || _authorized.RailsId != __0 ||
                    _authorized.TrainPosition != __1 || !LanSaveIsolation.Enforce())
                    throw new InvalidOperationException("Unapproved client rail placement");
                var spline = LanWorldCatalog.ResolveLoadedRails(_authorized, __1);
                var manager = GlobalManager.global;
                if (manager == null || manager.controlledTrain == null) throw new InvalidOperationException("Client train unavailable");
                manager.controlledTrain.SetOnRails(spline);
                manager.controlledTrain.SetPosition(__1);
                return false; // Never enter the native fallback to arbitrary rails.
            }
            catch (Exception ex) { Fail(ex.GetType().Name); throw; }
        }
        private static void TrackLoad(ref IEnumerator __result) { __result = Track(__result); }
        private static IEnumerator Track(IEnumerator original)
        {
            _activeLoads++;
            try
            {
                yield return TrackNested(original);
            }
            finally
            {
                _activeLoads--;
                if (ClientSession) _awaitingLoad = false;
            }
        }
        private static IEnumerator TrackNested(IEnumerator original)
        {
            try
            {
                while (!ClientSession || LoadFailure == null)
                {
                    bool more; object current;
                    try { more = original.MoveNext(); current = more ? original.Current : null; }
                    catch (Exception ex) { if (ClientSession) Fail(ex.GetType().Name); throw; }
                    if (!more) yield break;
                    var nested = current as IEnumerator;
                    yield return ClientSession && nested != null ? TrackNested(nested) : current;
                }
            }
            finally
            {
                try { (original as IDisposable)?.Dispose(); }
                catch (Exception ex) { if (ClientSession) Fail(ex.GetType().Name); throw; }
            }
        }
        internal static void Shutdown()
        {
            if (LanSaveIsolation.Active) return; // Native coroutines outlive the socket/plugin.
            ClientSession = false;
            _harmony?.UnpatchSelf(); _harmony = null;
        }
    }
}
