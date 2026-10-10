using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Owner of a robot dog (1.2.1): HostOwner, a guest's profile id, or none yet. Kept in the dog's
    // own save data (one element after the game's own seventeen, which the game ignores).
    internal sealed class LanRobotOwner : MonoBehaviour
    {
        internal string Owner = "";
        // 1.2.2: a guest's dog its owner left switched on is switched off while the owner is away
        // (so the host can carry it), and back on when the owner returns.
        internal bool Parked;
        internal float AwaySince = -1f;
    }
    // Guest: the local stand-in of a host object it carries; the game holds, drops, throws and places it.
    internal sealed class LanCarriedProxy : MonoBehaviour { internal int HostId; }

    // Carryable objects and robot dogs together (1.2.1). Every barrel and dog lives in the host's
    // world. A barrel is shared: either player carries it; the guest picks a host barrel up through
    // the host, holds a local stand-in the game handles as its own, and when the game lets go of it
    // (dropped, thrown, put into a holder) the host does the same with the real one. A robot dog
    // belongs to whoever first opens its window or picks it up (dogs the host had already switched on
    // are the host's). Only its owner uses it: a guest's dog follows the guest (the host's game runs
    // it, so its turret hurts the host's zombies), waits while the guest is away, and the guest sets
    // it up in a window of the mod; its slots open in the game's storage window. 1.2.2: anyone may
    // carry any dog (the host also a guest's one); its window, devices and use stay its owner's.
    // Main thread only.
    internal sealed class LanTakables : ILanTakableWorld
    {
        internal const string HostOwner = "H";
        private const float Reach = 4f, WindowReach = 5f, ReleaseReach = 6f, HoldAhead = .6f, HoldUp = 1.05f, DogUp = .85f, GoneAfter = 2f,
            InfoEvery = .5f, ProxyAfterAnswer = .4f, ProxyMax = 3f, MaxThrow = 30f;
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }
        private static AccessTools.FieldRef<RobotDogAiController, Transform> _character;
        private static FieldInfo _devices, _deviceInventory, _deviceDevice;

        // ---- Host side, set every frame by the plugin ----
        internal static bool Hosting;
        internal static Func<string> GuestProfile;   // the attached guest's profile id (null: none)
        internal static Func<Transform> GuestAvatar; // the guest's avatar in this world (null: not shown)
        internal static Func<Vector3?> GuestPosition;
        internal static Func<bool> GuestCrouch, GuestAble; // crouching; here, alive and up
        // The object the guest carries in this world, the colliders that were on, and its last sight.
        private static TakableItem _carried;
        private static readonly List<Collider> _disabled = new List<Collider>();
        private static float _carriedSeen;

        // ---- Guest side ----
        private static LanStorageClient _client;
        private static TakableItem _proxy;
        private static int _proxyHost, _taking;
        private static bool _proxyDog, _released, _sent, _answered;
        private static float _releasedAt, _releasedFixed, _destroyAt;
        // The robot window: which dog (its host id), what the host last said, the input it holds.
        private static int _windowDog;
        private static LanStorage.RobotState _info;
        private static float _infoAt, _openedAt;
        private static bool _infoPending, _setPending;
        private static string _windowNote = "";
        // A slot chosen in the window, opened by the next Update (not from OnGUI): dog, kind, slot.
        private static int _slotDog, _slotKind, _slotIndex;
        private static ZombieFighterController _heldPlayer;
        private static bool _previousControl, _previousCursor;
        private static CursorLockMode _previousLock;
        // Guest: the host id it carries (its world copy stays hidden), or 0.
        internal static int ProxyHostId { get { return _proxy != null ? _proxyHost : 0; } }
        internal static bool Carrying { get { return _proxy != null && !_released; } }
        internal static bool WindowOpen { get { return _windowDog != 0; } }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.takables");
            try
            {
                _character = AccessTools.FieldRefAccess<RobotDogAiController, Transform>("_character");
                _devices = AccessTools.Field(typeof(RobotDogAiController), "Devices");
                var data = AccessTools.Inner(typeof(RobotDogAiController), "DeviceData");
                _deviceInventory = AccessTools.Field(data, "inventory"); _deviceDevice = AccessTools.Field(data, "device");
                if (_devices == null || _deviceInventory == null || _deviceDevice == null) throw new MissingFieldException("RobotDogAiController.Devices");
                _harmony.Patch(AccessTools.Method(typeof(RobotDogAiController), "Save"), postfix: new HarmonyMethod(typeof(LanTakables), nameof(SavePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(RobotDogAiController), "Load"), postfix: new HarmonyMethod(typeof(LanTakables), nameof(LoadPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(RobotDogAiController), "Update"), prefix: new HarmonyMethod(typeof(LanTakables), nameof(RobotUpdatePrefix)), postfix: new HarmonyMethod(typeof(LanTakables), nameof(RobotUpdatePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(InteractingObjectsController), "OnRobotWindowHandler"), prefix: new HarmonyMethod(typeof(LanTakables), nameof(RobotWindowPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(InteractingObjectsController), "Update"), prefix: new HarmonyMethod(typeof(LanTakables), nameof(InteractPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieFighterRays), "TakeTakeableItem"), prefix: new HarmonyMethod(typeof(LanTakables), nameof(TakePrefix)));
            }
            catch (Exception ex)
            {
                // Without these hooks dogs and barrels stay the host's, as before 1.2.1.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Robot dog and barrel hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown()
        {
            GuestAbort(); HostRelease();
            _harmony?.UnpatchSelf(); _harmony = null;
            Hosting = false; GuestProfile = null; GuestAvatar = null; GuestPosition = null; GuestCrouch = null; GuestAble = null;
        }

        private static void Once(string text) { if (_logged) return; _logged = true; _log?.Invoke(text); }

        // ---- Owners ----

        private static string OwnerOf(RobotDogAiController dog)
        {
            var tag = dog.GetComponent<LanRobotOwner>();
            string owner = tag == null ? "" : tag.Owner ?? "";
            return owner.Length == 0 && dog.acquired ? HostOwner : owner; // switched on before 1.2.1: the host's
        }
        private static void SetOwner(RobotDogAiController dog, string owner)
        {
            var tag = dog.GetComponent<LanRobotOwner>();
            if (tag == null) tag = dog.gameObject.AddComponent<LanRobotOwner>();
            tag.Owner = owner;
        }
        private static bool GuestOwned(RobotDogAiController dog) { return OwnerOf(dog).Length == 64; }
        // The local player of this world (host or solo) touches a dog: its own, or now its own.
        private static bool HostMay(RobotDogAiController dog)
        {
            if (dog.GetComponent<LanCarriedProxy>() != null) return true;
            string owner = OwnerOf(dog);
            if (owner.Length == 0) { SetOwner(dog, HostOwner); return true; }
            return owner == HostOwner;
        }
        // Host: the attached guest uses a dog: its own, or now its own.
        private static bool GuestMay(RobotDogAiController dog)
        {
            string me = GuestProfile?.Invoke();
            if (string.IsNullOrEmpty(me)) return false;
            string owner = OwnerOf(dog);
            if (owner.Length == 0) { SetOwner(dog, me); _log?.Invoke("Robot dog now belongs to the guest"); return true; }
            return owner == me;
        }
        // Host: a dog's frame bits for the attached guest.
        internal static int OwnerBits(TakableItem takable)
        {
            var dog = takable == null ? null : takable.GetComponent<RobotDogAiController>();
            if (dog == null) return 0;
            string owner = OwnerOf(dog), me = GuestProfile?.Invoke();
            return (owner.Length != 0 ? LanProtocol.TakableClaimed : 0) | (owner.Length == 64 && owner == me ? LanProtocol.TakableYours : 0);
        }
        internal static bool CarriedByGuest(TakableItem takable) { return takable != null && takable == _carried; }

        private static void SavePostfix(RobotDogAiController __instance, ref object[] __result)
        {
            try
            {
                if (__result == null) return;
                // A guest's stand-in never goes into a save as carried.
                if (__instance.GetComponent<LanCarriedProxy>() != null && __result.Length > 14) __result[14] = false;
                var tag = __instance.GetComponent<LanRobotOwner>();
                if (tag == null || string.IsNullOrEmpty(tag.Owner) || __result.Length != 17) return;
                var data = new object[18];
                Array.Copy(__result, data, 17);
                data[17] = tag.Owner;
                __result = data;
            }
            catch (Exception ex) { Once("Robot owner not saved: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void LoadPostfix(RobotDogAiController __instance, object[] Data)
        {
            string owner = Data != null && Data.Length > 17 ? Data[17] as string : null;
            if (owner != null && (owner == HostOwner || LanGuestStore.ValidProfileId(owner))) SetOwner(__instance, owner);
        }

        // A guest's dog follows the guest's avatar here, and waits while the guest is away.
        private static bool RobotUpdatePrefix(RobotDogAiController __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (__instance.GetComponent<LanCarriedProxy>() != null) return true;
                string owner = OwnerOf(__instance);
                if (owner.Length != 64)
                {
                    var manager = GlobalManager.global;
                    var me = manager == null || manager.controlledChar == null ? null : manager.controlledChar.transform;
                    if (me != null && _character(__instance) != null && _character(__instance) != me) _character(__instance) = me;
                    return true;
                }
                var avatar = Hosting && owner == GuestProfile?.Invoke() && GuestAble != null && GuestAble() && __instance.takableItem != _carried ? GuestAvatar?.Invoke() : null;
                var tag = __instance.GetComponent<LanRobotOwner>();
                if (avatar != null)
                {
                    if (tag != null)
                    {
                        tag.AwaySince = -1f;
                        if (tag.Parked && !__instance.takableItem.taked && __instance.takableItem.OnTrainCar == null) { tag.Parked = false; __instance.SetActive(true); }
                    }
                    _character(__instance) = avatar; __state = true; return true;
                }
                // Its owner is away: after a few seconds the dog switches off (the host may carry it).
                if (tag != null && __instance.takableItem != _carried)
                {
                    float now = Time.unscaledTime;
                    if (tag.AwaySince < 0f) tag.AwaySince = now;
                    else if (__instance.robotActivated && now - tag.AwaySince > 5f) { tag.Parked = true; __instance.SetActive(false); }
                }
                var agent = __instance.navMeshAgent;
                if (agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = true;
                __instance.state = "Idle";
                return false;
            }
            catch (Exception ex) { Once("Robot dog owner check failed: " + ex.GetType().Name + ": " + ex.Message); return true; }
        }
        private static void RobotUpdatePostfix(RobotDogAiController __instance, bool __state)
        {
            if (__state && GuestCrouch != null) __instance.stealthMode = GuestCrouch();
        }

        // The local player cannot open or pick up a guest's dog, nor use what it carries.
        private static bool RobotWindowPrefix()
        {
            try
            {
                var gui = Zompiercer.GUI.GUIManager.Global;
                var dog = SelectionManager.Global == null ? null : SelectionManager.Global.HoveredRobotDogAiController;
                if (dog == null || gui != null && gui.RoboDogGUIControllerWindow != null && gui.RoboDogGUIControllerWindow.activeSelf) return true;
                if (HostMay(dog)) return true;
                LanStorageGame.Notice("Это робопёс друга");
                return false;
            }
            catch (Exception ex) { Once("Robot window check failed: " + ex.GetType().Name + ": " + ex.Message); return true; }
        }
        private static void InteractPrefix()
        {
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                var hovered = SelectionManager.Global == null ? null : SelectionManager.Global.HoveredObject;
                if (input == null || hovered == null) return;
                bool use = input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown;
                if (!use) return;
                var dog = hovered.GetComponentInParent<RobotDogAiController>();
                if (dog == null || HostMay(dog)) return;
                input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false;
                LanStorageGame.Notice("Это робопёс друга");
            }
            catch (Exception ex) { Once("Robot use check failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        // 1.2.2: carrying is for anyone; an unclaimed dog becomes the carrier's.
        private static bool TakePrefix(TakableItem item)
        {
            if (item == null || item.GetComponent<LanCarriedProxy>() != null) return true;
            if (item == _carried) { LanStorageGame.Notice("Это несёт друг"); return false; }
            var dog = item.GetComponent<RobotDogAiController>();
            if (dog != null) HostMay(dog);
            return true;
        }

        // ---- Host: what the guest carries ----

        // Every frame: the carried object stays in the guest avatar's hands; it falls where it is
        // when the guest is gone, down or dead for a while.
        internal static void HostTick()
        {
            if (_carried == null) { _disabled.Clear(); return; }
            float now = Time.unscaledTime;
            var avatar = Hosting && GuestAble != null && GuestAble() ? GuestAvatar?.Invoke() : null;
            if (avatar != null)
            {
                _carriedSeen = now;
                var yaw = Quaternion.Euler(0f, avatar.eulerAngles.y, 0f);
                bool dog = _carried.GetComponent<RobotDogAiController>() != null;
                _carried.transform.SetPositionAndRotation(avatar.position + yaw * new Vector3(0f, dog ? DogUp : HoldUp, HoldAhead), yaw);
                return;
            }
            if (!Hosting || now - _carriedSeen > GoneAfter) HostRelease();
        }

        // The carried object falls where it is (the guest left).
        internal static void HostRelease()
        {
            var t = _carried;
            if (t == null) return;
            try
            {
                Detach();
                var dog = t.GetComponent<RobotDogAiController>();
                if (dog != null) PlaceDog(dog, t, t.transform.position, t.transform.rotation, null);
                else Drop(t, t.transform.position, t.transform.rotation, Vector3.zero);
                _log?.Invoke("The guest's carried object was put down where it was");
            }
            catch (Exception ex) { Once("Carried object not put down: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public byte Takable(object[] r, out byte[] answer, out int amount)
        {
            answer = null; amount = 0;
            int op = (int)r[0], hostId = (int)r[1];
            if (Failure != null) return LanStorage.NotAllowed;
            if (!Hosting || string.IsNullOrEmpty(GuestProfile?.Invoke())) return LanStorage.Unavailable;
            var guest = GuestPosition?.Invoke();
            if (guest == null) return LanStorage.Lost;
            try
            {
                if (op == LanStorage.StationUse) return LanFuelStation.Host(hostId, (int)r[2], guest.Value); // 1.4.3
                if (op == LanStorage.TakableTake) return Take(hostId, guest.Value);
                if (op == LanStorage.TakableRelease) return Release(hostId, (int)r[2], (int)r[3], (float[])r[4], (float[])r[5], guest.Value);
                byte status;
                var dog = GuestDog(hostId, guest.Value, out status);
                if (dog == null) return status;
                if (op == LanStorage.RobotSet)
                {
                    int field = (int)r[2], value = (int)r[3];
                    if (field == LanStorage.RobotActive)
                    {
                        if (dog.takableItem == _carried) return LanStorage.NotAllowed;
                        if (value == 1 && dog.takableItem != null && dog.takableItem.OnTrainCar != null) return LanStorage.NotAllowed;
                        var tag = dog.GetComponent<LanRobotOwner>();
                        if (tag != null) tag.Parked = false;
                        dog.SetActive(value == 1);
                    }
                    else if (field == LanStorage.RobotBehavior) dog.behaviorMode = value;
                    else if (field == LanStorage.RobotAttack) dog.attackMode = value;
                    else dog.openDoorMode = value;
                }
                else if (op == LanStorage.RobotMedkit)
                {
                    var device = Device(dog, (int)r[2]);
                    var kit = device == null ? null : device.GetComponentInChildren<StaticMedKit>();
                    if (kit == null || kit.cooldown == null) return LanStorage.NotFound;
                    if (!kit.cooldown.IsReady()) return LanStorage.Busy;
                    kit.cooldown.Use();
                    amount = Mathf.Clamp(Mathf.RoundToInt(kit.HealAmount * 100f), 0, 1000000);
                    LanGuestCredits.Health(kit.HealAmount); // 1.3.0: the guest's checked health
                    return LanStorage.Ok;
                }
                answer = LanStorage.EncodeRobot(State(dog));
                return LanStorage.Ok;
            }
            catch (Exception ex) { Once("Takable request failed: " + ex.GetType().Name + ": " + ex.Message); return LanStorage.Invalid; }
        }

        private static TakableItem FindTakable(int hostId)
        {
            if (_carried != null && _carried.GetInstanceID() == hostId) return _carried;
            foreach (var t in Object.FindObjectsOfType<TakableItem>()) if (t != null && t.GetInstanceID() == hostId) return t;
            return null;
        }

        // A dog the attached guest owns (or now owns) within reach.
        private static RobotDogAiController GuestDog(int hostId, Vector3 guest, out byte status)
        {
            status = LanStorage.NotFound;
            var t = FindTakable(hostId);
            var dog = t == null ? null : t.GetComponent<RobotDogAiController>();
            if (dog == null || !dog.gameObject.activeInHierarchy) return null;
            if (!GuestMay(dog)) { status = LanStorage.NotYours; return null; }
            if (t != _carried && Vector3.Distance(dog.transform.position, guest) > WindowReach) { status = LanStorage.TooFar; return null; }
            status = LanStorage.Ok;
            return dog;
        }

        private static byte Take(int hostId, Vector3 guest)
        {
            if (_carried != null) return _carried.GetInstanceID() == hostId ? LanStorage.Ok : LanStorage.Occupied;
            var t = FindTakable(hostId);
            if (t == null || !LanWorldItems.Carryable(t)) return LanStorage.NotFound;
            if (Vector3.Distance(t.transform.position, guest) > Reach) return LanStorage.TooFar;
            var dog = t.GetComponent<RobotDogAiController>();
            if (dog != null)
            {
                if (!GuestMay(dog)) return LanStorage.NotYours;
                if (dog.robotActivated) return LanStorage.NotAllowed;
            }
            else if (t.typeOfItem != 0) return LanStorage.NotAllowed;
            if (GuestAvatar?.Invoke() == null) return LanStorage.Lost;
            if (t.StoredInHolder != null) { t.StoredInHolder.AttachedItem = null; t.StoredInHolder = null; }
            t.OnTrainCar = null;
            if (dog != null && dog.navMeshAgent != null) dog.navMeshAgent.enabled = false;
            var body = t.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            _disabled.Clear();
            foreach (var collider in t.GetComponentsInChildren<Collider>(true)) if (collider.enabled) { collider.enabled = false; _disabled.Add(collider); }
            t.transform.SetParent(null, true);
            _carried = t; _carriedSeen = Time.unscaledTime;
            HostTick();
            return LanStorage.Ok;
        }

        private static void Detach()
        {
            foreach (var collider in _disabled) if (collider != null) collider.enabled = true;
            _disabled.Clear(); _carried = null;
        }

        private static byte Release(int hostId, int mode, int car, float[] pose, float[] velocity, Vector3 guest)
        {
            var t = _carried;
            if (t == null || t.GetInstanceID() != hostId) return LanStorage.NotFound;
            Transform carTransform = null;
            if (car >= 0) { var found = TrainSync.GetCar(car); carTransform = found == null ? null : found.transform; }
            var at = new Vector3(pose[0], pose[1], pose[2]);
            var rotation = new Quaternion(pose[3], pose[4], pose[5], pose[6]);
            if (carTransform != null) { at = carTransform.TransformPoint(at); rotation = carTransform.rotation * rotation; }
            else if (car >= 0) { at = t.transform.position; rotation = t.transform.rotation; } // no such car here: where the guest holds it
            if (Vector3.Distance(at, guest) > ReleaseReach) { at = t.transform.position; rotation = t.transform.rotation; }
            Detach();
            var dog = t.GetComponent<RobotDogAiController>();
            if (dog != null) { PlaceDog(dog, t, at, rotation, carTransform); return LanStorage.Ok; }
            if (mode == LanStorage.ReleaseHolder && PutInHolder(t, at)) return LanStorage.Ok;
            var v = mode == LanStorage.ReleaseThrow ? Vector3.ClampMagnitude(new Vector3(velocity[0], velocity[1], velocity[2]), MaxThrow) : Vector3.zero;
            Drop(t, at, rotation, v);
            return LanStorage.Ok;
        }

        private static void Drop(TakableItem t, Vector3 at, Quaternion rotation, Vector3 velocity)
        {
            t.transform.SetParent(null, true);
            t.transform.SetPositionAndRotation(at, rotation);
            var body = t.GetComponent<Rigidbody>();
            if (body == null) return;
            body.isKinematic = false;
            body.velocity = velocity;
        }

        // As the game's own placing of a dog (ZombieFighterRays.DropRobotRoutine).
        private static void PlaceDog(RobotDogAiController dog, TakableItem t, Vector3 at, Quaternion rotation, Transform car)
        {
            var yaw = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
            if (car != null)
            {
                if (dog.navMeshAgent != null) dog.navMeshAgent.enabled = false;
                t.transform.SetPositionAndRotation(at, yaw);
                t.transform.SetParent(car, true);
                var local = t.transform.localEulerAngles; local.x = 0f; local.z = 0f; t.transform.localEulerAngles = local;
                t.OnTrainCar = car.GetComponent<Zompiercer.Train.TrainCar>();
                return;
            }
            NavMeshHit hit;
            if (NavMesh.SamplePosition(at, out hit, 2f, -1)) at = hit.position;
            t.transform.SetParent(null, true);
            t.transform.SetPositionAndRotation(at, yaw);
            t.OnTrainCar = null;
            if (dog.navMeshAgent != null) dog.navMeshAgent.enabled = true;
        }

        private static bool PutInHolder(TakableItem t, Vector3 at)
        {
            TakableHolder best = null; float bestDistance = 1f;
            foreach (var holder in Object.FindObjectsOfType<TakableHolder>())
            {
                if (holder == null || holder.AttachedItem != null) continue;
                float d = Vector3.Distance(holder.transform.position, at);
                if (d < bestDistance) { best = holder; bestDistance = d; }
            }
            if (best == null) return false;
            best.PutIn(t);
            return true;
        }

        // ---- Host: robot dog details ----

        private static IList Devices(RobotDogAiController dog) { return _devices == null ? null : _devices.GetValue(dog) as IList; }
        private static RobotDevice Device(RobotDogAiController dog, int slot)
        {
            var list = Devices(dog);
            return list == null || slot < 0 || slot >= list.Count || list[slot] == null ? null : _deviceDevice.GetValue(list[slot]) as RobotDevice;
        }
        // A slot's inventory (LanStorage.RobotSlotDevice/Upgrade/Storage), or null.
        internal static Inventory RobotInventory(RobotDogAiController dog, int kind, int slot)
        {
            if (dog == null || slot < 0) return null;
            if (kind == LanStorage.RobotSlotUpgrade) return dog.InventoryUpgrades != null && slot < dog.InventoryUpgrades.Count ? dog.InventoryUpgrades[slot] : null;
            var list = Devices(dog);
            if (list == null || slot >= list.Count || list[slot] == null) return null;
            if (kind == LanStorage.RobotSlotDevice) return _deviceInventory.GetValue(list[slot]) as Inventory;
            var device = _deviceDevice.GetValue(list[slot]) as RobotDevice;
            return device == null ? null : device.inventory;
        }
        // Host storage (LanStorageGame): the dog of a robot target, when the attached guest may use it.
        internal static RobotDogAiController StorageDog(int hostId, out byte status)
        {
            status = LanStorage.NotFound;
            var t = FindTakable(hostId);
            var dog = t == null ? null : t.GetComponent<RobotDogAiController>();
            if (dog == null) return null;
            if (!GuestMay(dog)) { status = LanStorage.NotYours; return null; }
            status = LanStorage.Ok;
            return dog;
        }
        internal static bool StillGuests(RobotDogAiController dog)
        {
            if (dog == null) return false;
            string me = GuestProfile?.Invoke();
            return !string.IsNullOrEmpty(me) && OwnerOf(dog) == me;
        }
        // A device that holds things of its own cannot leave its slot while they are there (as the
        // game's lock panel over that slot).
        internal static bool DeviceInUse(RobotDogAiController dog, int slot)
        {
            var device = Device(dog, slot);
            var inventory = device == null ? null : device.inventory;
            return inventory != null && inventory.content != null && inventory.content.Count > 0;
        }
        internal static void SaveDevice(RobotDogAiController dog, int slot)
        {
            try { var device = Device(dog, slot); if (device != null) device.SaveToItem(); }
            catch (Exception ex) { Once("Robot device state not saved: " + ex.GetType().Name + ": " + ex.Message); }
        }
        internal static void RobotChanged(RobotDogAiController dog)
        {
            try { dog.UpdateDevices(); dog.UpdateUpgradeValues(); }
            catch (Exception ex) { Once("Robot devices not updated: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static LanStorage.RobotState State(RobotDogAiController dog)
        {
            var s = new LanStorage.RobotState
            {
                Active = dog.robotActivated, Broken = dog.robotBroken, OnTrain = dog.takableItem != null && dog.takableItem.OnTrainCar != null,
                Behavior = Mathf.Clamp(dog.behaviorMode, 0, 1), Attack = Mathf.Clamp(dog.attackMode, 0, 3), Doors = Mathf.Clamp(dog.openDoorMode, 0, 1),
                Battery = Percent(dog.currentRoboBatteryCapacity, dog.maxRoboBatteryCapacity), Durability = Percent(dog.currentRoboDurability, dog.maxRoboDurability)
            };
            var list = Devices(dog);
            int count = list == null ? 0 : Math.Min(list.Count, LanStorage.MaxRobotSlots);
            s.Devices = new int[count]; s.Storage = new bool[count]; s.Medkit = new int[count];
            for (int i = 0; i < count; i++)
            {
                var inventory = list[i] == null ? null : _deviceInventory.GetValue(list[i]) as Inventory;
                s.Devices[i] = ItemIn(inventory);
                var device = list[i] == null ? null : _deviceDevice.GetValue(list[i]) as RobotDevice;
                s.Storage[i] = device != null && device.inventory != null;
                var kit = device == null ? null : device.GetComponentInChildren<StaticMedKit>();
                s.Medkit[i] = kit == null || kit.cooldown == null ? -1 : kit.cooldown.IsReady() ? 1 : 0;
            }
            int upgrades = dog.InventoryUpgrades == null ? 0 : Math.Min(dog.InventoryUpgrades.Count, LanStorage.MaxRobotSlots);
            s.Upgrades = new int[upgrades];
            for (int i = 0; i < upgrades; i++) s.Upgrades[i] = ItemIn(dog.InventoryUpgrades[i]);
            return s;
        }
        private static int ItemIn(Inventory inventory)
        {
            var item = inventory == null || inventory.content == null || inventory.content.Count == 0 ? null : inventory.content[0];
            return item == null || item.itemID < 0 || item.itemID > 65535 ? -1 : item.itemID;
        }
        private static int Percent(float now, float max)
        {
            if (float.IsNaN(now) || float.IsNaN(max) || max <= 0f) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(100f * now / max), 0, 100);
        }

        // ---- Guest: looking at a host object ----

        private static bool HandsFree(ZombieFighterController player)
        {
            var inventories = InventoryController.global;
            if (player == null || inventories == null || player.zombieFighterRays == null || player.zombieFighterRays.takableItemTaked != null) return false;
            int slot = inventories.selectedBeltIcon;
            var belt = player.beltInventory;
            return belt == null || slot < 0 || slot >= belt.Length || belt[slot] == null || belt[slot].content == null || belt[slot].content.Count == 0;
        }

        // The guest looks at a host barrel or dog (`use`/`reload`: the keys this frame). True: handled.
        internal static bool GuestLook(LanWorldReplica shown, bool use, bool reload, LanStorageClient client)
        {
            if (shown == null || shown.Kind != LanWorldItems.TakableKind || Failure != null) return false;
            _client = client;
            var input = Zompiercer.Inputs.InputController.Global;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            int state = shown.Amount >> 8;
            if ((state & (LanProtocol.TakableHeldByHost | LanProtocol.TakableHeldByGuest)) != 0) return true;
            bool dog = shown.ItemId == LanWorldItems.RobotDogPrefab, free = HandsFree(player);
            if (!dog)
            {
                if (!free)
                {
                    if (info != null) { info.color = GlobalSoundEffects.global.colorRed; info.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey("FreeYourHandsToPickUpAnItem").ToUpper()); }
                    return true;
                }
                if (info != null) info.SetIconText("PickUp", (Zompiercer.Inputs.Actions)49, (Zompiercer.Inputs.Actions)0, true);
                if (use) { Consume(input); Pick(shown.HostId, shown.ItemId); }
                return true;
            }
            bool mine = (state & LanProtocol.TakableYours) != 0, claimed = (state & LanProtocol.TakableClaimed) != 0, active = (state & LanProtocol.TakableActive) != 0;
            if (claimed && !mine)
            {
                if (info != null) { info.color = GlobalSoundEffects.global.colorRed; info.SetText("РОБОПЁС ХОСТА"); }
                return true;
            }
            if (info != null)
            {
                if (!claimed) info.SetText("РОБОПЁС (НИЧЕЙ)");
                info.SetIconText("Robot", (Zompiercer.Inputs.Actions)1, (Zompiercer.Inputs.Actions)0, true);
                if (!active && free) info.SetIconText("PickUp", (Zompiercer.Inputs.Actions)62, (Zompiercer.Inputs.Actions)0, true);
            }
            if (use)
            {
                Consume(input);
                // 1.4.1: the game's own robot window; the mod's window when it is unavailable.
                if (LanRobotWindow.Available) LanRobotWindow.Open(shown.HostId, client); else OpenWindow(shown.HostId);
            }
            else if (reload && !active && free) { Consume(input); Pick(shown.HostId, shown.ItemId); }
            return true;
        }
        private static void Consume(Zompiercer.Inputs.InputController input)
        {
            if (input == null) return;
            input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false; input.isReloadDown = false;
        }

        private static void Pick(int hostId, int itemId)
        {
            var client = _client;
            if (client == null || _proxy != null || _taking != 0) return;
            _taking = hostId;
            if (!client.Takable(LanStorage.CarryPayload(hostId), (status, amount, answer) =>
                {
                    _taking = 0;
                    if (status == LanStorage.Ok) StartCarry(hostId, itemId);
                    // 1.4.13: in the words of carrying (the storage words told of putting things away).
                    else if (status == LanStorage.NotAllowed) LanStorageGame.Notice(itemId == LanWorldItems.RobotDogPrefab ? "Сначала выключите робопса" : "Это нельзя взять");
                    else if (status != LanStorage.Busy) LanStorageGame.Notice(LanStorage.Describe(status));
                }))
                _taking = 0;
        }

        private static void StartCarry(int hostId, int itemId)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var rays = player == null ? null : player.zombieFighterRays;
            GameObject prefab = null;
            if (itemId == LanWorldItems.RobotDogPrefab) prefab = GlobalManager.global.ControlledRobotPrefab == null ? null : GlobalManager.global.ControlledRobotPrefab.gameObject;
            else
            {
                var misc = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.MiscPrefabs;
                prefab = misc != null && itemId >= 0 && itemId < misc.Count && misc[itemId] != null ? misc[itemId].gameObject : null;
            }
            if (rays == null || rays.takableItemTaked != null || prefab == null || _proxy != null)
            {
                // Cannot hold it after all: the host puts it down where the guest stands.
                if (player != null) SendRelease(hostId, LanStorage.ReleaseDrop, player.transform.position + player.transform.forward * .5f + Vector3.up * .3f, Quaternion.identity, Vector3.zero, null);
                return;
            }
            var staging = new GameObject("LAN carried staging");
            staging.SetActive(false);
            GameObject copy = null;
            try
            {
                copy = Object.Instantiate(prefab, staging.transform, false);
                copy.name = "LAN carried host object";
                foreach (var agent in copy.GetComponentsInChildren<NavMeshAgent>(true)) agent.enabled = false;
                // An inactive dog in hands, as the game carries one (an active one would look for its navigation).
                var robot = copy.GetComponent<RobotDogAiController>();
                if (robot != null) robot.robotActivated = false;
                copy.AddComponent<LanCarriedProxy>().HostId = hostId;
                copy.transform.SetParent(null, false);
                copy.transform.position = player.transform.position + Vector3.up;
                copy.SetActive(true);
                var takable = copy.GetComponent<TakableItem>();
                if (takable == null) throw new MissingComponentException("TakableItem");
                rays.TakeTakeableItem(takable);
                _proxy = takable; _proxyHost = hostId; _proxyDog = itemId == LanWorldItems.RobotDogPrefab;
                _released = _sent = _answered = false;
            }
            catch (Exception ex)
            {
                Once("Carried copy failed: " + ex.GetType().Name + ": " + ex.Message);
                if (copy != null) Object.Destroy(copy);
                SendRelease(hostId, LanStorage.ReleaseDrop, player.transform.position + Vector3.up * .3f, Quaternion.identity, Vector3.zero, null);
            }
            finally { Object.Destroy(staging); }
        }

        // ---- Guest: every frame while attached to the host's world ----

        internal static void GuestTick(LanStorageClient client)
        {
            _client = client;
            float now = Time.unscaledTime;
            LanRobotWindow.Tick(client);
            if (_slotDog != 0)
            {
                int dog = _slotDog; _slotDog = 0;
                LanStorageGame.OpenWindow(client, LanStorage.RobotPayload(dog, _slotKind, _slotIndex));
            }
            if (_windowDog != 0) WindowTick(now);
            if (_proxy == null) return;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var rays = player == null ? null : player.zombieFighterRays;
            if (!_released)
            {
                if (rays != null && rays.takableItemTaked == _proxy) return; // still in hands
                _released = true; _releasedAt = now; _releasedFixed = Time.fixedTime;
                return;
            }
            if (!_sent)
            {
                // A throw shows in the body's velocity after the next physics step.
                if (!_proxyDog && _proxy.StoredInHolder == null && Time.fixedTime <= _releasedFixed + Time.fixedDeltaTime * 1.5f && now - _releasedAt < .3f) return;
                _sent = SendProxyRelease();
                if (!_sent && now - _releasedAt > 5f) _sent = true; // never stuck: the host puts it down when the guest goes
                return;
            }
            if (_answered && now >= _destroyAt || now - _releasedAt > ProxyMax) DestroyProxy();
        }

        private static bool SendProxyRelease()
        {
            var t = _proxy;
            var body = t.GetComponent<Rigidbody>();
            var velocity = body == null || body.isKinematic ? Vector3.zero : body.velocity;
            int mode = _proxyDog ? LanStorage.ReleasePlace : t.StoredInHolder != null ? LanStorage.ReleaseHolder : velocity.magnitude > 2f ? LanStorage.ReleaseThrow : LanStorage.ReleaseDrop;
            var at = mode == LanStorage.ReleaseHolder ? t.StoredInHolder.transform.position : t.transform.position;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            Zompiercer.Train.TrainCar car = _proxyDog ? t.OnTrainCar : mode == LanStorage.ReleaseHolder ? t.StoredInHolder.GetComponentInParent<Zompiercer.Train.TrainCar>() :
                player == null ? null : player.OnTrainCar;
            return SendRelease(_proxyHost, mode, at, t.transform.rotation, velocity, car);
        }

        private static bool SendRelease(int hostId, int mode, Vector3 at, Quaternion rotation, Vector3 velocity, Zompiercer.Train.TrainCar car)
        {
            var client = _client;
            if (client == null) return false;
            int index = car == null ? -1 : car.GetCarIndex();
            if (index >= 0 && index < LanProtocol.MaxCars)
            {
                var local = car.transform.InverseTransformPoint(at);
                if (Mathf.Abs(local.x) < LanStorage.MaxCarLocal && Mathf.Abs(local.y) < LanStorage.MaxCarLocal && Mathf.Abs(local.z) < LanStorage.MaxCarLocal)
                { at = local; rotation = Quaternion.Inverse(car.transform.rotation) * rotation; }
                else index = -1;
            }
            else index = -1;
            rotation = Quaternion.Normalize(rotation);
            velocity = Vector3.ClampMagnitude(velocity, LanStorage.MaxThrowSpeed - 1f);
            byte[] payload;
            try { payload = LanStorage.ReleasePayload(hostId, mode, index, new[] { at.x, at.y, at.z, rotation.x, rotation.y, rotation.z, rotation.w }, new[] { velocity.x, velocity.y, velocity.z }); }
            catch (System.IO.InvalidDataException) { return false; }
            return client.Takable(payload, (status, amount, answer) =>
            {
                if (hostId == _proxyHost) { _answered = true; _destroyAt = Time.unscaledTime + ProxyAfterAnswer; }
                if (status != LanStorage.Ok && status != LanStorage.NotFound) LanStorageGame.Notice(LanStorage.Describe(status));
            });
        }

        private static void DestroyProxy()
        {
            int host = _proxyHost;
            if (_proxy != null) Object.Destroy(_proxy.gameObject);
            _proxy = null; _proxyHost = 0; _released = _sent = _answered = false;
            if (host != 0) LanWorldItems.Reveal(host);
        }

        // The guest leaves the host's world (or cannot act now): its stand-in goes, and the host is
        // asked to put the real object down where the guest stands.
        internal static void GuestAbort()
        {
            CloseWindow();
            LanRobotWindow.Close();
            _taking = 0; _slotDog = 0;
            if (_proxy == null) return;
            try
            {
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                var rays = player == null ? null : player.zombieFighterRays;
                if (!_released)
                {
                    if (rays != null && rays.takableItemTaked == _proxy) rays.takableItemTaked = null;
                    if (_client != null && player != null) SendRelease(_proxyHost, LanStorage.ReleaseDrop, _proxy.transform.position, _proxy.transform.rotation, Vector3.zero, player.OnTrainCar);
                }
            }
            catch (Exception ex) { Once("Carried copy not let go: " + ex.GetType().Name + ": " + ex.Message); }
            DestroyProxy();
            _client = null;
        }

        // ---- Guest: the robot window ----

        private static void OpenWindow(int hostId)
        {
            if (_client == null) return;
            _windowDog = hostId; _info = null; _infoPending = false; _setPending = false; _windowNote = "Связь с хостом…";
            _openedAt = Time.unscaledTime; _infoAt = -100f;
            Hold();
        }
        internal static void CloseWindow()
        {
            if (_windowDog == 0) return;
            _windowDog = 0; _info = null;
            ReleaseHold();
        }

        private static void WindowTick(float now)
        {
            var at = LanWorldItems.ShownPosition(_windowDog);
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (_client == null || player == null || at == null || Vector3.Distance(at.Value, player.transform.position) > WindowReach + 1f) { CloseWindow(); return; }
            Hold();
            if (!_infoPending && !_setPending && now - _infoAt > InfoEvery) Ask(LanStorage.RobotInfoPayload(_windowDog), false);
        }

        private static void Ask(byte[] payload, bool setting)
        {
            int dog = _windowDog;
            if (setting) _setPending = true; else _infoPending = true;
            _infoAt = Time.unscaledTime;
            if (!_client.Takable(payload, (status, amount, answer) =>
                {
                    if (setting) _setPending = false; else _infoPending = false;
                    if (dog != _windowDog) return;
                    if (status != LanStorage.Ok) { _windowNote = LanStorage.Describe(status); if (status == LanStorage.NotYours) CloseWindow(); return; }
                    try { _info = LanStorage.DecodeRobot(answer); _windowNote = ""; }
                    catch (System.IO.InvalidDataException ex) { _windowNote = "Хост прислал повреждённое состояние"; Once("Robot state rejected: " + ex.Message); }
                }))
            { if (setting) _setPending = false; else _infoPending = false; }
        }

        private static void Medkit(int slot)
        {
            int dog = _windowDog;
            _setPending = true;
            if (!_client.Takable(LanStorage.RobotMedkitPayload(dog, slot), (status, amount, answer) =>
                {
                    _setPending = false;
                    if (status != LanStorage.Ok) { _windowNote = status == LanStorage.Busy ? "Аптечка ещё не готова" : LanStorage.Describe(status); return; }
                    try
                    {
                        var bar = GlobalManager.global.controlledChar.zombieFighterIndicatorsBar;
                        bar.SetCure(true);
                        bar.cureTimer += amount / 100f / Math.Max(.01f, ZombieFighterIndicatorsBar.MedKitHPS);
                        _windowNote = "Аптечка робопса лечит";
                    }
                    catch (Exception ex) { Once("Robot medkit not applied: " + ex.GetType().Name + ": " + ex.Message); }
                }))
                _setPending = false;
        }

        // 1.4.1: the medkit of device `slot` of the guest's dog `dog` heals the guest (as Medkit; for the
        // game's robot window, LanRobotWindow); `done` runs on the host's answer.
        internal static void UseMedkit(int dog, int slot, LanStorageClient client, Action done)
        {
            if (client == null || dog == 0) return;
            if (!client.Takable(LanStorage.RobotMedkitPayload(dog, slot), (status, amount, answer) =>
                {
                    done?.Invoke();
                    if (status != LanStorage.Ok) { LanStorageGame.Notice(status == LanStorage.Busy ? "Аптечка ещё не готова" : LanStorage.Describe(status)); return; }
                    try
                    {
                        var bar = GlobalManager.global.controlledChar.zombieFighterIndicatorsBar;
                        bar.SetCure(true);
                        bar.cureTimer += amount / 100f / Math.Max(.01f, ZombieFighterIndicatorsBar.MedKitHPS);
                        LanStorageGame.Notice("Аптечка робопса лечит");
                    }
                    catch (Exception ex) { Once("Robot medkit not applied: " + ex.GetType().Name + ": " + ex.Message); }
                }))
                LanStorageGame.Notice("Хост ещё отвечает на прошлые запросы");
        }

        private static void Hold()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (player == null) return;
            if (_heldPlayer != player)
            {
                ReleaseHold();
                _heldPlayer = player; _previousControl = player.controlEnabled; _previousCursor = Cursor.visible; _previousLock = Cursor.lockState;
            }
            player.controlEnabled = false; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private static void ReleaseHold()
        {
            var player = _heldPlayer;
            _heldPlayer = null;
            if (player == null || GlobalManager.global == null || GlobalManager.global.controlledChar != player) return;
            player.controlEnabled = _previousControl; Cursor.lockState = _previousLock; Cursor.visible = _previousCursor;
        }

        private static GUIStyle _title, _text;
        private static readonly string[] Behaviors = { "Ждать", "Следовать" };
        private static readonly string[] Doors = { "Не открывать", "Открывать" };

        // Guest OnGUI: the window of its own robot dog.
        internal static void DrawWindow()
        {
            if (_windowDog == 0) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.E) && Time.unscaledTime - _openedAt > .4f)
            { e.Use(); CloseWindow(); return; }
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _text = new GUIStyle(GUI.skin.label) { wordWrap = true };
            }
            float width = Math.Min(460f, Screen.width - 32f), height = Math.Min(560f, Screen.height - 32f);
            var box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            GUI.Box(box, GUIContent.none);
            GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 12f, box.y + 8f, box.width - 24f, box.height - 16f));
            GUILayout.Label("Ваш робопёс", _title);
            var s = _info;
            if (s == null) GUILayout.Label(_windowNote.Length != 0 ? _windowNote : "Связь с хостом…", _text);
            else
            {
                bool busy = _setPending;
                GUILayout.Label((s.Active ? "Включён" : "Выключен") + (s.Broken ? " · сломан" : "") + " · заряд " + s.Battery + "% · прочность " + s.Durability + "%", _text);
                GUI.enabled = !busy && (s.Active || !s.OnTrain);
                if (GUILayout.Button(s.Active ? "Выключить" : s.OnTrain ? "На поезде не включается" : "Включить")) Ask(LanStorage.RobotSetPayload(_windowDog, LanStorage.RobotActive, s.Active ? 0 : 1), true);
                GUI.enabled = !busy;
                GUILayout.Label("Поведение", _text);
                int behavior = GUILayout.Toolbar(s.Behavior, Behaviors);
                if (behavior != s.Behavior) Ask(LanStorage.RobotSetPayload(_windowDog, LanStorage.RobotBehavior, behavior), true);
                GUILayout.Label("Атака: " + Localized("AttackModeText" + s.Attack), _text);
                int attack = GUILayout.Toolbar(s.Attack, new[] { "1", "2", "3", "4" });
                if (attack != s.Attack) Ask(LanStorage.RobotSetPayload(_windowDog, LanStorage.RobotAttack, attack), true);
                GUILayout.Label("Двери", _text);
                int doors = GUILayout.Toolbar(s.Doors, Doors);
                if (doors != s.Doors) Ask(LanStorage.RobotSetPayload(_windowDog, LanStorage.RobotDoors, doors), true);
                GUILayout.Space(6f);
                GUILayout.Label("Устройства", _text);
                for (int i = 0; i < s.Devices.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button((i + 1) + ": " + Name(s.Devices[i]))) OpenSlot(LanStorage.RobotSlotDevice, i);
                    if (s.Storage[i] && GUILayout.Button("Хранилище", GUILayout.Width(110f))) OpenSlot(LanStorage.RobotSlotStorage, i);
                    if (s.Medkit[i] >= 0)
                    {
                        GUI.enabled = !busy && s.Medkit[i] == 1;
                        if (GUILayout.Button(s.Medkit[i] == 1 ? "Лечиться" : "Аптечка…", GUILayout.Width(110f))) Medkit(i);
                        GUI.enabled = !busy;
                    }
                    GUILayout.EndHorizontal();
                }
                if (s.Upgrades.Length != 0)
                {
                    GUILayout.Label("Улучшения", _text);
                    for (int i = 0; i < s.Upgrades.Length; i++)
                        if (GUILayout.Button((i + 1) + ": " + Name(s.Upgrades[i]))) OpenSlot(LanStorage.RobotSlotUpgrade, i);
                }
                GUI.enabled = true;
                if (_windowNote.Length != 0) GUILayout.Label(_windowNote, _text);
                if (!s.Active) GUILayout.Label("Взять на руки: R, глядя на робопса.", _text);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Закрыть (E / Esc)")) CloseWindow();
            GUILayout.EndArea();
        }

        private static void OpenSlot(int kind, int slot)
        {
            _slotDog = _windowDog; _slotKind = kind; _slotIndex = slot;
            CloseWindow();
        }
        private static string Name(int id) { return id < 0 ? "пусто" : LanStorageGame.ItemName(id); }
        private static string Localized(string key)
        {
            try { var text = Zompiercer.GUI.LocalizationCore.GetTextByKey(key); return string.IsNullOrEmpty(text) ? key : text; }
            catch (Exception) { return key; }
        }
    }
}
