using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Zompiercer.Train;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Fuel stations together (1.4.3). A station (its switchboard lever, its pump button, its fuel
    // gun on a hose) lives in the host's world; the host's train frame says how it stands
    // (StationFrame) and the guest's own copy of the station only shows that: it never pumps.
    // The guest's lever and pump presses and what it does with the gun (takes it, puts it into the
    // tank of the host's train, hangs it back on the station, lets go of it) are requests the host
    // does as its own hand would (takable op StationUse); while the guest holds the gun, the host's
    // gun stays in the guest avatar's hand. Fuel flows only on the host, and the train frame brings
    // the tank's level back (TrainSync). Main thread only.
    internal static class LanFuelStation
    {
        private const float Reach = 4f, HoseLength = 5f, LoseAfter = 2f, PendingFor = 3f, Quiet = 1f, StaleAfter = 3f;
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }
        private static AccessTools.FieldRef<FuelStationController, Transform> _gunPoint;
        private static AccessTools.FieldRef<FuelStationController, HardwareButton> _pump;
        private static AccessTools.FieldRef<FuelStationController, FuelStationGunController> _gun;
        private static AccessTools.FieldRef<FuelStationController, float> _fuel, _speed;
        private static AccessTools.FieldRef<FuelStationController, Text> _text;
        private static AccessTools.FieldRef<FuelStationController, bool> _active;
        private static AccessTools.FieldRef<FuelStationController, AudioSource> _engine;
        private static AccessTools.FieldRef<FuelStationSwitchboard, FuelStationController> _boardStation;
        private static AccessTools.FieldRef<FuelStationSwitchboard, HardwareButton> _boardLever;
        private static AccessTools.FieldRef<FuelStationGunController, TrainFuelTank> _connected;
        private static AccessTools.FieldRef<TrainFuelTank, Transform> _tankPoint;
        private static AccessTools.FieldRef<ZombieFighterRays, FuelStationController> _seenStation;
        private static AccessTools.FieldRef<ZombieFighterRays, TrainFuelTank> _seenTank;

        // The scene's stations and switchboards, found again every few seconds.
        private static FuelStationController[] _stations = new FuelStationController[0];
        private static FuelStationSwitchboard[] _boards = new FuelStationSwitchboard[0];
        private static float _scanAt = -100f;
        private static readonly Dictionary<FuelStationController, int> Keys = new Dictionary<FuelStationController, int>();

        // ---- Host ----
        // The gun the guest holds, and when its avatar was last seen.
        private static FuelStationGunController _guestGun;
        private static float _guestSeen;

        // ---- Guest ----
        // Queues one request (StationPayload) with its answer; false if not queued.
        internal static Func<byte[], Action<byte, int, byte[]>, bool> GuestRequest;
        private static readonly Dictionary<int, StationFrame> Frames = new Dictionary<int, StationFrame>();
        private static float _framesAt = -100f, _pendingUntil, _quietUntil, _strayFrom = -1f, _noticeAt = -10f;
        private static int _pressFrame = -1;
        private static uint _frameSequence;
        private static bool _bypass;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.fuel-station");
            try
            {
                _gunPoint = AccessTools.FieldRefAccess<FuelStationController, Transform>("refuelingGunPoint");
                _pump = AccessTools.FieldRefAccess<FuelStationController, HardwareButton>("button");
                _gun = AccessTools.FieldRefAccess<FuelStationController, FuelStationGunController>("fuelStationGun");
                _fuel = AccessTools.FieldRefAccess<FuelStationController, float>("fuelAmount");
                _speed = AccessTools.FieldRefAccess<FuelStationController, float>("refuelingSpeed");
                _text = AccessTools.FieldRefAccess<FuelStationController, Text>("fuelAmountText");
                _active = AccessTools.FieldRefAccess<FuelStationController, bool>("gasStationActivated");
                _engine = AccessTools.FieldRefAccess<FuelStationController, AudioSource>("fuelPumpEngine");
                _boardStation = AccessTools.FieldRefAccess<FuelStationSwitchboard, FuelStationController>("fuelStation");
                _boardLever = AccessTools.FieldRefAccess<FuelStationSwitchboard, HardwareButton>("lever");
                _connected = AccessTools.FieldRefAccess<FuelStationGunController, TrainFuelTank>("connectedTrainFuelTank");
                _tankPoint = AccessTools.FieldRefAccess<TrainFuelTank, Transform>("refuelingGunPoint");
                _seenStation = AccessTools.FieldRefAccess<ZombieFighterRays, FuelStationController>("fuelStationDetected");
                _seenTank = AccessTools.FieldRefAccess<ZombieFighterRays, TrainFuelTank>("trainFuelTankDetected");
                _harmony.Patch(AccessTools.Method(typeof(FuelStationController), "Update"),
                    prefix: new HarmonyMethod(typeof(LanFuelStation), nameof(UpdatePrefix)), postfix: new HarmonyMethod(typeof(LanFuelStation), nameof(UpdatePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieFighterRays), "TakeTakeableItem"), prefix: new HarmonyMethod(typeof(LanFuelStation), nameof(TakePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieFighterRays), "DropItem"), prefix: new HarmonyMethod(typeof(LanFuelStation), nameof(DropPrefix)));
            }
            catch (Exception ex)
            {
                // Without these hooks each player's station stays its own, as before 1.4.3.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Fuel station hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown()
        {
            HostLetGo(false); Clear();
            _harmony?.UnpatchSelf(); _harmony = null;
        }
        // Guest: the session ended or the scene changed.
        internal static void Clear()
        {
            Frames.Clear(); _framesAt = -100f; _frameSequence = 0; _pendingUntil = 0f; _quietUntil = 0f; _strayFrom = -1f;
            GuestRequest = null;
        }

        private static void Once(string text) { if (_logged) return; _logged = true; _log?.Invoke(text); }

        // ---- The scene's stations ----

        private static FuelStationController[] Stations()
        {
            float now = Time.unscaledTime;
            bool stale = now >= _scanAt;
            if (!stale) foreach (var s in _stations) if (s == null) { stale = true; break; }
            if (stale)
            {
                _stations = Object.FindObjectsOfType<FuelStationController>();
                _boards = Object.FindObjectsOfType<FuelStationSwitchboard>();
                _scanAt = now + 5f;
                var gone = new List<FuelStationController>();
                foreach (var pair in Keys) if (pair.Key == null) gone.Add(pair.Key);
                foreach (var s in gone) Keys.Remove(s);
            }
            return _stations;
        }

        // FNV-1a of the station's scene and path: the same on both sides for the same scene object.
        internal static int Key(FuelStationController station)
        {
            int key;
            if (Keys.TryGetValue(station, out key)) return key;
            var names = new List<string>();
            for (var t = station.transform; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            key = PathKey(station.gameObject.scene.name, string.Join("/", names.ToArray()));
            Keys[station] = key;
            return key;
        }
        internal static int PathKey(string scene, string path)
        {
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes((scene ?? "") + "|" + (path ?? ""))) { hash ^= b; hash *= 16777619; }
            int key = unchecked((int)hash);
            return key == 0 ? 1 : key;
        }

        private static HardwareButton Lever(FuelStationController station)
        {
            foreach (var board in _boards) if (board != null && _boardStation(board) == station) return _boardLever(board);
            return null;
        }
        private static FuelStationController StationOf(FuelStationGunController gun)
        {
            if (gun == null) return null;
            foreach (var s in Stations()) if (s != null && _gun(s) == gun) return s;
            return null;
        }
        internal static bool IsStationGun(TakableItem takable)
        {
            return Failure == null && takable != null && takable.GetComponent<FuelStationGunController>() != null;
        }
        // A station's lever or pump button (LanSceneStates leaves them to this class).
        internal static bool IsStationButton(HardwareButton button)
        {
            if (Failure != null || button == null) return false;
            foreach (var s in Stations()) if (s != null && (_pump(s) == button || Lever(s) == button)) return true;
            return false;
        }
        private static ZombieFighterRays LocalRays()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            return player == null ? null : player.zombieFighterRays;
        }
        private static TrainFuelTank LocalMainTank()
        {
            var manager = GlobalManager.global;
            if (manager == null) return null;
            var controller = manager.controlledTrainController;
            if (controller == null && manager.controlledTrain != null) controller = manager.controlledTrain.GetComponent<TrainController>();
            return TrainSync.MainTank(controller);
        }
        private static bool Pumping(FuelStationController s)
        {
            var gun = _gun(s); var pump = _pump(s);
            var tank = gun == null ? null : _connected(gun);
            return _fuel(s) > 0f && _active(s) && pump != null && pump.State && tank != null && tank.currentFuelAmount < tank.maxFuelAmount;
        }
        private static void Place(FuelStationGunController gun, Transform point)
        {
            var body = gun.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            var t = gun.transform;
            t.SetParent(null, true);
            t.SetPositionAndRotation(point.position, point.rotation);
            t.SetParent(point, true);
        }

        // ---- Host ----

        // The stations nearest to the guest (or to the host's player) for the host's train frame.
        internal static void Capture(TrainFrame frame)
        {
            if (Failure != null || !LanTakables.Hosting) return;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var guest = LanTakables.GuestPosition?.Invoke();
            Vector3 near = guest ?? (player != null ? player.transform.position : Vector3.zero);
            var list = new List<FuelStationController>();
            foreach (var s in Stations()) if (s != null && s.isActiveAndEnabled && _gun(s) != null) list.Add(s);
            if (list.Count == 0) return;
            list.Sort((a, b) => (a.transform.position - near).sqrMagnitude.CompareTo((b.transform.position - near).sqrMagnitude));
            var rays = LocalRays();
            var main = LocalMainTank();
            var frames = new List<StationFrame>();
            foreach (var s in list)
            {
                if (frames.Count >= LanProtocol.MaxStations) break;
                var gun = _gun(s);
                var lever = Lever(s); var pump = _pump(s);
                byte flags = 0;
                if (_active(s)) flags |= LanProtocol.StationActive;
                if (lever != null && lever.State) flags |= LanProtocol.StationLever;
                if (pump != null && pump.State) flags |= LanProtocol.StationPump;
                if (Pumping(s)) flags |= LanProtocol.StationPumping;
                byte where;
                var t = gun.transform;
                var tank = _connected(gun);
                if (gun == _guestGun) where = LanProtocol.GunGuest;
                else if (rays != null && rays.takableItemTaked != null && rays.takableItemTaked.gameObject == gun.gameObject) where = LanProtocol.GunHost;
                else if (t.parent != null && t.parent == _gunPoint(s)) where = LanProtocol.GunAtStation;
                else if (tank != null && tank == main && t.parent != null && t.parent == _tankPoint(tank)) where = LanProtocol.GunTank;
                else where = LanProtocol.GunLoose;
                var st = new StationFrame { Key = Key(s), Fuel = Mathf.Clamp(_fuel(s), 0f, 99999f), Flags = flags, Gun = where };
                if (LanProtocol.GunPosed(where))
                {
                    var p = t.position; var q = t.rotation;
                    if (Mathf.Abs(p.x) >= 99999f || Mathf.Abs(p.y) >= 99999f || Mathf.Abs(p.z) >= 99999f) continue;
                    st.GunPose = new NetPose { X = p.x, Y = p.y, Z = p.z, QX = q.x, QY = q.y, QZ = q.z, QW = q.w };
                }
                frames.Add(st);
            }
            frame.Stations = frames.ToArray();
        }

        // A validated request of the guest (LanStorage.StationUse), done as the host's own hand would.
        internal static byte Host(int key, int action, Vector3 guest)
        {
            if (Failure != null) return LanStorage.NotAllowed;
            FuelStationController station = null; float best = float.MaxValue;
            foreach (var s in Stations())
            {
                if (s == null || !s.isActiveAndEnabled || Key(s) != key) continue;
                float d = (s.transform.position - guest).sqrMagnitude;
                if (d < best) { best = d; station = s; }
            }
            if (station == null) return LanStorage.NotFound;
            var gun = _gun(station);
            switch (action)
            {
                case LanStorage.StationLever:
                case LanStorage.StationPump:
                    var button = action == LanStorage.StationLever ? Lever(station) : _pump(station);
                    if (button == null) return LanStorage.NotFound;
                    if (Vector3.Distance(button.transform.position, guest) > Reach) return LanStorage.TooFar;
                    if (button.defective) return LanStorage.Broken;
                    button.Switch();
                    return LanStorage.Ok;
                case LanStorage.StationTake:
                    if (gun == null) return LanStorage.NotFound;
                    if (gun == _guestGun) return LanStorage.Ok;
                    if (_guestGun != null) return LanStorage.Occupied;
                    var rays = LocalRays();
                    if (rays != null && rays.takableItemTaked != null && rays.takableItemTaked.gameObject == gun.gameObject) return LanStorage.Occupied;
                    if (Vector3.Distance(gun.transform.position, guest) > Reach) return LanStorage.TooFar;
                    if (LanTakables.GuestAvatar?.Invoke() == null) return LanStorage.Lost;
                    _connected(gun) = null;
                    var body = gun.GetComponent<Rigidbody>();
                    if (body != null) body.isKinematic = true;
                    gun.transform.SetParent(null, true);
                    _guestGun = gun; _guestSeen = Time.unscaledTime;
                    HostTick();
                    _log?.Invoke("The guest took the fuel gun");
                    return LanStorage.Ok;
                default:
                    if (gun == null || gun != _guestGun) return LanStorage.NotFound;
                    if (action == LanStorage.StationTank)
                    {
                        var tank = LocalMainTank();
                        var point = tank == null ? null : _tankPoint(tank);
                        if (point == null) return LanStorage.Unavailable;
                        if (Vector3.Distance(point.position, guest) > HoseLength) return LanStorage.TooFar;
                        _guestGun = null;
                        Place(gun, point);
                        _connected(gun) = tank;
                        _log?.Invoke("The guest put the fuel gun into the train's tank");
                        return LanStorage.Ok;
                    }
                    if (action == LanStorage.StationBack)
                    {
                        var point = _gunPoint(station);
                        if (point == null) return LanStorage.NotFound;
                        if (Vector3.Distance(station.transform.position, guest) > HoseLength) return LanStorage.TooFar;
                        _guestGun = null;
                        Place(gun, point);
                        return LanStorage.Ok;
                    }
                    HostLetGo(false);
                    return LanStorage.Ok;
            }
        }

        // Every frame: the gun the guest holds stays in its avatar's hand; it hangs on its hose when
        // the guest is gone, down or dead for a while.
        internal static void HostTick()
        {
            var gun = _guestGun;
            if (gun == null) return;
            if (!LanTakables.Hosting) { HostLetGo(true); return; }
            float now = Time.unscaledTime;
            var avatar = LanTakables.GuestAble != null && LanTakables.GuestAble() ? LanTakables.GuestAvatar?.Invoke() : null;
            if (avatar == null) { if (now - _guestSeen > LoseAfter) HostLetGo(true); return; }
            _guestSeen = now;
            var yaw = Quaternion.Euler(0f, avatar.eulerAngles.y, 0f);
            gun.transform.SetPositionAndRotation(avatar.position + yaw * new Vector3(.25f, 1.1f, .5f), yaw);
        }
        private static void HostLetGo(bool log)
        {
            var gun = _guestGun;
            _guestGun = null;
            if (gun == null) return;
            try
            {
                gun.transform.SetParent(null, true);
                var body = gun.GetComponent<Rigidbody>();
                if (body != null) body.isKinematic = false;
                if (log) _log?.Invoke("The fuel gun the guest held now hangs on its hose");
            }
            catch (Exception ex) { Once("Fuel gun not let go: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---- The station's own Update ----

        // The guest's copy only shows the host's station. On the host the game's own Update runs,
        // except that a gun far from its station lets go of the gun only, not of anything else the
        // host's player carries (the game drops whatever is in the hands).
        private static bool UpdatePrefix(FuelStationController __instance, out TakableItem __state)
        {
            __state = null;
            try
            {
                if (Failure != null) return true;
                if (LanSaveIsolation.Active) { GuestUpdate(__instance); return false; }
                if (!LanTakables.Hosting) return true;
                var gun = _gun(__instance);
                if (gun == null || Vector3.Distance(gun.transform.position, __instance.transform.position) < HoseLength) return true;
                if (gun == _guestGun) { _guestGun = null; _log?.Invoke("The guest went too far with the fuel gun; it hangs on its hose"); }
                var rays = LocalRays();
                if (rays != null && rays.takableItemTaked != null && rays.takableItemTaked.gameObject != gun.gameObject)
                { __state = rays.takableItemTaked; rays.takableItemTaked = null; }
                return true;
            }
            catch (Exception ex) { Once("Fuel station hook failed: " + ex.GetType().Name + ": " + ex.Message); return !LanSaveIsolation.Active; }
        }
        private static void UpdatePostfix(TakableItem __state)
        {
            if (__state == null) return;
            var rays = LocalRays();
            if (rays != null && rays.takableItemTaked == null) rays.takableItemTaked = __state;
        }

        // ---- Guest ----

        // The host's train frame: how its stations stand.
        internal static void Mirror(TrainFrame frame)
        {
            if (Failure != null || frame == null || frame.Stations == null || !LanSaveIsolation.Active || frame.Sequence == _frameSequence) return;
            _frameSequence = frame.Sequence;
            Frames.Clear();
            foreach (var s in frame.Stations) if (s != null) Frames[s.Key] = s;
            _framesAt = Time.unscaledTime;
        }

        private static void GuestUpdate(FuelStationController s)
        {
            var gun = _gun(s);
            var take = gun == null ? null : gun.GetComponent<TakableItem>();
            var rays = LocalRays();
            bool held = take != null && rays != null && rays.takableItemTaked == take;
            float now = Time.unscaledTime;
            if (gun != null) _connected(gun) = null; // the guest's copy never pumps
            // The hose: the game lets go of the gun five metres from its station.
            if (held && Vector3.Distance(gun.transform.position, s.transform.position) >= HoseLength)
            {
                rays.takableItemTaked = null; held = false;
                gun.transform.SetParent(null, true);
                Send(s, LanStorage.StationDrop, false);
            }
            var engine = _engine(s);
            StationFrame f;
            if (!Frames.TryGetValue(Key(s), out f) || now - _framesAt > StaleAfter)
            {
                if (engine != null && engine.isPlaying) engine.Stop();
                return;
            }
            _fuel(s) = f.Fuel;
            _active(s) = (f.Flags & LanProtocol.StationActive) != 0;
            var text = _text(s);
            if (text != null) text.text = Mathf.Round(f.Fuel).ToString();
            bool pumping = (f.Flags & LanProtocol.StationPumping) != 0;
            if (engine != null)
            {
                if (pumping && !engine.isPlaying) engine.Play();
                else if (!pumping && engine.isPlaying) engine.Stop();
            }
            Show(_pump(s), (f.Flags & LanProtocol.StationPump) != 0);
            Show(Lever(s), (f.Flags & LanProtocol.StationLever) != 0);
            if (gun == null) return;
            bool settled = now >= _quietUntil;
            if (held)
            {
                // The host says someone else has it now: the guest's hands let go.
                if (f.Gun == LanProtocol.GunGuest || !settled) { _strayFrom = -1f; return; }
                if (_strayFrom < 0f) { _strayFrom = now; return; }
                if (now - _strayFrom < Quiet) return;
                rays.takableItemTaked = null; _strayFrom = -1f;
            }
            else if (f.Gun == LanProtocol.GunGuest && settled)
            {
                // The host still keeps it in the guest's hands (a refused put-back): let it go there.
                if (_strayFrom < 0f) _strayFrom = now;
                else if (now - _strayFrom > 1.5f) { _strayFrom = -1f; Send(s, LanStorage.StationDrop, false); }
            }
            else _strayFrom = -1f;
            ShowGun(s, gun, f);
        }

        private static void Show(HardwareButton button, bool state)
        {
            if (button != null && button.State != state) button.Set(state); // its sound, lamp and the switchboard's signals
        }

        private static void ShowGun(FuelStationController s, FuelStationGunController gun, StationFrame f)
        {
            var body = gun.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) body.isKinematic = true;
            var t = gun.transform;
            Transform point = null;
            if (f.Gun == LanProtocol.GunAtStation) point = _gunPoint(s);
            else if (f.Gun == LanProtocol.GunTank) { var tank = LocalMainTank(); point = tank == null ? null : _tankPoint(tank); }
            if (point != null)
            {
                if (t.parent != point) { t.SetParent(point, false); t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; }
                return;
            }
            var p = f.GunPose;
            if (p == null) return;
            if (t.parent != null) t.SetParent(null, true);
            var at = new Vector3(p.X, p.Y, p.Z);
            var turn = new Quaternion(p.QX, p.QY, p.QZ, p.QW);
            float k = Mathf.Clamp01(Time.deltaTime * 12f);
            if ((t.position - at).sqrMagnitude > 25f) t.SetPositionAndRotation(at, turn);
            else t.SetPositionAndRotation(Vector3.Lerp(t.position, at, k), Quaternion.Slerp(t.rotation, turn, k));
        }

        // A lever or pump button of a station: the host presses its own.
        internal static bool GuestSwitch(HardwareButton button)
        {
            if (Failure != null || button == null || !LanSaveIsolation.Active) return false;
            FuelStationController station = null; int action = 0;
            foreach (var s in Stations())
            {
                if (s == null) continue;
                if (_pump(s) == button) { station = s; action = LanStorage.StationPump; break; }
                if (Lever(s) == button) { station = s; action = LanStorage.StationLever; break; }
            }
            if (station == null) return false;
            if (_pressFrame == Time.frameCount) return true;
            _pressFrame = Time.frameCount;
            Send(station, action, true);
            return true;
        }

        private static bool Send(FuelStationController s, int action, bool wait)
        {
            float now = Time.unscaledTime;
            var send = GuestRequest;
            if (send == null) { Notice("Станция хоста сейчас недоступна"); return false; }
            if (wait && now < _pendingUntil) { Notice("Хост ещё не ответил; нажмите ещё раз"); return false; }
            byte[] payload;
            try { payload = LanStorage.StationPayload(Key(s), action); }
            catch (InvalidDataException) { return false; }
            var station = s;
            if (!send(payload, (status, amount, answer) => Answered(station, action, status)))
            {
                if (wait) Notice("Хост ещё не ответил; нажмите ещё раз");
                return false;
            }
            if (wait) _pendingUntil = now + PendingFor;
            _quietUntil = now + PendingFor;
            return true;
        }

        private static void Answered(FuelStationController station, int action, byte status)
        {
            _pendingUntil = 0f; _quietUntil = Time.unscaledTime + Quiet;
            try
            {
                if (status != LanStorage.Ok)
                {
                    _log?.Invoke("Fuel station request " + action + " refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    if (status != LanStorage.Busy && action != LanStorage.StationDrop) Notice(Describe(status, action));
                    return;
                }
                if (action != LanStorage.StationTake || station == null) return;
                // The host gave the gun to the guest's hands: the game's own pick-up shows it.
                var gun = _gun(station);
                var take = gun == null ? null : gun.GetComponent<TakableItem>();
                var rays = LocalRays();
                if (take == null || rays == null || rays.takableItemTaked != null || LanGuestDeath.Dead || !LanSaveIsolation.Active)
                { Send(station, LanStorage.StationDrop, false); return; }
                _bypass = true;
                try { rays.TakeTakeableItem(take); }
                finally { _bypass = false; }
            }
            catch (Exception ex) { Once("Fuel station answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static string Describe(byte status, int action)
        {
            switch (status)
            {
                case LanStorage.TooFar: return "Подойдите ближе";
                case LanStorage.Occupied: return "Пистолет сейчас у хоста";
                case LanStorage.Broken: return "Сломано";
                case LanStorage.Unavailable: return action == LanStorage.StationTank ? "Бак поезда хоста недоступен" : LanStorage.Describe(status);
                case LanStorage.NotFound: return action == LanStorage.StationLever || action == LanStorage.StationPump ? "У хоста нет такой станции" : "Хост не видит пистолет у вас в руках";
                default: return LanStorage.Describe(status);
            }
        }
        private static void Notice(string text)
        {
            if (Time.unscaledTime - _noticeAt < .5f) return;
            _noticeAt = Time.unscaledTime;
            LanStorageGame.Notice(text);
        }

        // The game picks the gun up: the guest asks the host first; the host's player cannot take
        // the gun the guest holds.
        private static bool TakePrefix(TakableItem item)
        {
            if (_bypass || item == null || Failure != null) return true;
            try
            {
                var gun = item.GetComponent<FuelStationGunController>();
                if (gun == null) return true;
                if (LanSaveIsolation.Active)
                {
                    var s = StationOf(gun);
                    if (s == null) return true;
                    StationFrame f;
                    if (Frames.TryGetValue(Key(s), out f) && f.Gun == LanProtocol.GunHost) { Notice("Пистолет сейчас у хоста"); return false; }
                    Send(s, LanStorage.StationTake, true);
                    return false;
                }
                if (LanTakables.Hosting && gun == _guestGun) { Notice("Пистолет сейчас у друга"); return false; }
                return true;
            }
            catch (Exception ex) { Once("Fuel gun pick-up hook failed: " + ex.GetType().Name + ": " + ex.Message); return !LanSaveIsolation.Active; }
        }

        // The game puts the gun down (into a tank, back on its station, or lets go): the guest's copy
        // does it at once and the host does the same with its gun.
        private static bool DropPrefix(ZombieFighterRays __instance)
        {
            if (_bypass || Failure != null || !LanSaveIsolation.Active) return true;
            try
            {
                var take = __instance.takableItemTaked;
                var gun = take == null ? null : take.GetComponent<FuelStationGunController>();
                var s = StationOf(gun);
                if (s == null) return true;
                int action = LanStorage.StationDrop;
                var tank = _seenTank(__instance);
                if (_seenStation(__instance) != null) action = LanStorage.StationBack;
                else if (tank != null)
                {
                    if (tank != LocalMainTank()) { Notice("Пистолет вставляется только в бак локомотива"); return false; }
                    action = LanStorage.StationTank;
                }
                Send(s, action, false);
                return true;
            }
            catch (Exception ex) { Once("Fuel gun put-down hook failed: " + ex.GetType().Name + ": " + ex.Message); return true; }
        }
    }
}
