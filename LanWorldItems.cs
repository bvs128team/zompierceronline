using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Loose items and zombie loot bags of the host's world (0.7.0). The host sends
    // what lies around the guest (changes first, the rest in turn); the guest shows
    // script-free copies and hides the loose items its own copy of the world
    // generated. Picking up and dropping go through the host (LanStorage requests),
    // so the guest's ledger only ever grows by what the host hands out.
    // Items placed on the train travel with the train layout instead. 1.1.6: the host's carryable
    // objects (barrels, robot dogs: TakableItem, not inventory items) too, shown by script-free
    // copies that follow their car on the train and walk (a dog) while they move. 1.2.1: also one
    // a player holds (the guest sees it in the host avatar's hands; one the guest carries is the
    // guest's own stand-in, LanTakables), and whose robot dog it is. Main thread only.
    internal sealed class LanWorldItems
    {
        internal const float Radius = 40f, Reach = 3.5f, Look = 3f;
        private const float ScanEvery = 1f, SendEvery = .2f, Stale = 6f, HideEvery = 1f;
        private const int MaxShown = 160;
        internal const byte ItemKind = 0, BagKind = 1, GoneKind = 2, TakableKind = 3;
        // A robot dog has no PrefabID (GlobalManager.ControlledRobotPrefab).
        internal const int RobotDogPrefab = LanProtocol.MaxTakablePrefab;

        private sealed class HostEntry { internal Component Source; internal WorldItemFrame Frame; }
        private sealed class Shown
        {
            internal GameObject Root; internal int ItemId, Amount; internal byte Kind; internal float Seen; internal bool PickedUp;
            // A carryable object (1.1.6): where it should be (in its parent's space) and its animator.
            internal bool Placed; internal Vector3 Target; internal Quaternion TargetRotation = Quaternion.identity;
            internal Animator Animator; internal HashSet<string> Parameters;
            // 1.2.1: in the host avatar's hands; the last frame (shown again when the guest lets go).
            internal bool HeldByHost;
            internal WorldItemFrame Last;
            // 1.4.1: a robot dog stands up with the game's "Activation" (held a second) and folds with
            // "Deactivation"; null until its first frame.
            internal bool? Unfolded; internal string Pulse; internal float PulseUntil;
            // 1.4.13: a robot dog's devices (as the last frame said) and their copies, slot by slot.
            internal byte[] Devices; internal GameObject[] DeviceCopies;
        }

        private static Harmony _harmony;
        private static Action<string> _staticLog;
        // Guest: items its player just dropped, booked at LateUpdate when they are placed.
        internal static readonly List<InventoryItem> Dropped = new List<InventoryItem>();
        internal static readonly HashSet<InventoryItem> Placed = new HashSet<InventoryItem>();
        // Guest attached to the host's world: picking up and dropping go through the host.
        internal static bool GuestActive;
        // Guest (1.2.1): the host's avatar here (it holds what the host carries), and this session's copies.
        internal static Func<Transform> PartnerAvatar;
        private static LanWorldItems _guestCurrent;

        private readonly bool _host;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        // Host: every loose item and bag by instance id (train items too, for pick-ups).
        private readonly Dictionary<int, HostEntry> _objects = new Dictionary<int, HostEntry>();
        private readonly List<HostEntry> _near = new List<HostEntry>();
        private readonly Dictionary<int, WorldItemFrame> _sent = new Dictionary<int, WorldItemFrame>();
        private readonly List<int> _gone = new List<int>();
        private readonly Dictionary<int, Shown> _shown = new Dictionary<int, Shown>();
        private string _scene;
        private uint _epoch, _sequence, _received;
        private bool _ready;
        private float _scanAt, _sendAt, _hideAt, _rescanAt;
        private int _cursor;

        internal LanWorldItems(bool host, Action<Packet> send, Action<string> log) { _host = host; _send = send; _log = log; }

        internal static void Initialize(Action<string> log)
        {
            _staticLog = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.world-items");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Remove"), postfix: new HarmonyMethod(typeof(LanWorldItems), nameof(RemovePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Add", new[] { typeof(InventoryItem), typeof(bool) }), prefix: new HarmonyMethod(typeof(LanWorldItems), nameof(DuplicateAddPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "Update"), prefix: new HarmonyMethod(typeof(LanWorldItems), nameof(PickupPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "Update"), prefix: new HarmonyMethod(typeof(LanWorldItems), nameof(PlacementPrefix)));
            }
            catch (Exception ex)
            {
                _harmony.UnpatchSelf();
                Failure = ex.GetType().Name + ": " + ex.Message;
                log("World item hooks unavailable: " + Failure);
            }
        }
        internal static string Failure { get; private set; }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; Dropped.Clear(); Placed.Clear(); GuestActive = false; }

        private static bool DuplicateAddPrefix(Inventory __instance, InventoryItem newItem, ref bool __result)
        {
            // A stale hovered object must not be appended to an inventory twice.
            if (newItem == null || newItem.inInventory != __instance || __instance.content == null || !__instance.content.Contains(newItem)) return true;
            __result = false;
            return false;
        }

        // Guest: an item left one of the player's own inventories (dropped or placed).
        private static void RemovePostfix(Inventory __instance, InventoryItem target)
        {
            // Harmony binds by name: the game's parameter is `target`.
            if (!LanSaveIsolation.Active || !GuestActive || target == null) return;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (player == null || player.AllInventory == null || !player.AllInventory.Contains(__instance)) return;
            if (!Dropped.Contains(target)) Dropped.Add(target);
            var controller = InventoryController.global;
            if (controller != null && controller.ItemPreviewForPlacement != null && controller.AppropriatePosForPreview &&
                controller.draggedIcon != null && controller.draggedIcon.connectedInventoryItem == target) Placed.Add(target);
        }

        // Guest: E on one of its own (hidden or not yet hidden) loose items does nothing.
        private static void PickupPrefix()
        {
            if (!LanSaveIsolation.Active || !GuestActive) return;
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                var selection = SelectionManager.Global;
                if (input == null || selection == null || selection.HoveredObject == null) return;
                var item = selection.HoveredObject.GetComponent<InventoryItem>();
                if (item == null) return;
                input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false; input.isTakeItemAccept_Down = false;
            }
            catch (Exception ex) { _staticLog?.Invoke("World item input hook failed: " + ex.Message); }
        }

        // The ordinary interaction ray skips host construction. A placement
        // preview needs its physical surface, while native furniture stays hidden
        // from selection. Only replace the hit data used by the preview.
        private static void PlacementPrefix(InventoryController __instance)
        {
            if (!LanSaveIsolation.Active || !GuestActive || __instance.ItemPreviewForPlacement == null) return;
            var selection = SelectionManager.Global;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (selection == null || player == null) return;
            RaycastHit best = default(RaycastHit); bool any = false;
            foreach (var hit in Physics.RaycastAll(selection.ray, Look, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(player.transform) ||
                    hit.collider.transform.IsChildOf(__instance.ItemPreviewForPlacement.transform)) continue;
                if (!any || hit.distance < best.distance) { best = hit; any = true; }
            }
            selection.raycastHit = best;
        }

        internal void Tick(string scene, uint epoch, bool ready, Vector3? guest)
        {
            if (scene != _scene || epoch != _epoch)
            {
                _scene = scene; _epoch = epoch; _sequence = 0; _received = 0; _cursor = 0;
                _objects.Clear(); _near.Clear(); _sent.Clear(); _gone.Clear(); ClearShown();
            }
            _ready = ready && epoch != 0 && !string.IsNullOrEmpty(scene);
            if (!_ready) return;
            float now = Time.unscaledTime;
            if (!_host) { GuestTick(now); return; }
            if (now >= _scanAt) { _scanAt = now + ScanEvery; Scan(guest); }
            if (now >= _sendAt && guest != null) { _sendAt = now + SendEvery; SendBatch(); }
        }

        // ---- Host ----
        private void Scan(Vector3? guest)
        {
            _objects.Clear();
            // 1.4.16: also what the game's occlusion culling switched off (Present).
            foreach (var item in Object.FindObjectsOfType<InventoryItem>(true))
                if (Loose(item)) _objects[item.GetInstanceID()] = new HostEntry { Source = item, Frame = Frame(item) };
            string bag = BagName();
            if (bag != null)
                foreach (var inventory in Object.FindObjectsOfType<Inventory>())
                    if (inventory != null && inventory.transform.root.name == bag && LanStorageGame.Shared(inventory))
                        _objects[inventory.GetInstanceID()] = new HostEntry { Source = inventory, Frame = Frame(inventory) };
            // 1.1.6: carryable objects, also those on a train car. 1.2.1: also while a player carries one.
            foreach (var takable in Object.FindObjectsOfType<TakableItem>(true))
                if (Carryable(takable) || HeldByHost(takable)) _objects[takable.GetInstanceID()] = new HostEntry { Source = takable, Frame = Frame(takable) };
            // A dead guest's bags (0.12.0), also those lying on a train car.
            foreach (var marker in Object.FindObjectsOfType<LanDeathBag>())
            {
                var inventory = marker == null ? null : marker.GetComponentInChildren<Inventory>();
                if (inventory != null && LanStorageGame.Shared(inventory))
                    _objects[inventory.GetInstanceID()] = new HostEntry { Source = inventory, Frame = Frame(inventory) };
            }
            _near.Clear();
            if (guest != null)
            {
                foreach (var entry in _objects.Values)
                {
                    var item = entry.Source as InventoryItem;
                    if (item != null && item.ParentedToTraincCar != null) continue; // shown by the train layout
                    var p = entry.Source.transform.position;
                    if ((p - guest.Value).sqrMagnitude <= Radius * Radius) _near.Add(entry);
                }
                var at = guest.Value;
                _near.Sort((a, b) => (a.Source.transform.position - at).sqrMagnitude.CompareTo((b.Source.transform.position - at).sqrMagnitude));
                if (_near.Count > MaxShown) _near.RemoveRange(MaxShown, _near.Count - MaxShown);
            }
            var kept = new HashSet<int>(); foreach (var entry in _near) kept.Add(entry.Frame.HostId);
            foreach (var id in new List<int>(_sent.Keys)) if (!kept.Contains(id)) { _sent.Remove(id); if (!_gone.Contains(id)) _gone.Add(id); }
            // 1.4.14: what the guest is sent of carryable objects (fuel canisters in the train's holders among them),
            // when it changes, for the report "the guest does not see the canisters on the train".
            int carryable = 0, held = 0;
            foreach (var entry in _near) { var t = entry.Source as TakableItem; if (t == null) continue; carryable++; if (t.StoredInHolder != null) held++; }
            if (carryable != _carryableLogged || held != _heldLogged)
            { _carryableLogged = carryable; _heldLogged = held; _log?.Invoke("Carryable objects near the guest: " + carryable + " (" + held + " in train holders)"); }
        }
        private int _carryableLogged = -1, _heldLogged = -1, _takablesLogged;

        private void SendBatch()
        {
            var batch = new List<WorldItemFrame>();
            int bytes = 0;
            while (_gone.Count > 0 && batch.Count < LanProtocol.MaxItemBatch)
            {
                var gone = new WorldItemFrame { HostId = _gone[0], ItemId = -1, Kind = GoneKind, Pose = Identity() };
                if (!Fits(ref bytes, gone)) break;
                batch.Add(gone);
                _gone.RemoveAt(0);
            }
            var included = new HashSet<int>();
            foreach (var entry in _near)
            {
                if (batch.Count >= LanProtocol.MaxItemBatch) break;
                if (entry.Source == null) continue;
                var frame = FrameOf(entry.Source);
                WorldItemFrame last;
                if (_sent.TryGetValue(frame.HostId, out last) && Same(last, frame)) continue;
                if (!Fits(ref bytes, frame)) break;
                batch.Add(frame); included.Add(frame.HostId); _sent[frame.HostId] = frame;
            }
            for (int visited = 0; visited < _near.Count && batch.Count < LanProtocol.MaxItemBatch; visited++)
            {
                if (_cursor >= _near.Count) _cursor = 0;
                var entry = _near[_cursor++];
                if (entry.Source == null || included.Contains(entry.Frame.HostId)) continue;
                var frame = FrameOf(entry.Source);
                if (!Fits(ref bytes, frame)) break;
                batch.Add(frame); included.Add(frame.HostId); _sent[frame.HostId] = frame;
            }
            if (batch.Count == 0) return;
            _send(new Packet { Kind = PacketKind.WorldItems, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, Items = batch.ToArray() });
        }

        // Host lookups for pick-ups and bags, with one fresh scan on a miss.
        internal InventoryItem FindItem(int hostId)
        {
            HostEntry entry;
            if (!_objects.TryGetValue(hostId, out entry) || entry.Source == null) { Rescan(); _objects.TryGetValue(hostId, out entry); }
            var item = entry == null ? null : entry.Source as InventoryItem;
            return item != null && Loose(item) ? item : null;
        }
        internal Inventory FindBag(int hostId)
        {
            HostEntry entry;
            if (!_objects.TryGetValue(hostId, out entry) || entry.Source == null) { Rescan(); _objects.TryGetValue(hostId, out entry); }
            return entry == null ? null : entry.Source as Inventory;
        }
        internal void Removed(int hostId) { _objects.Remove(hostId); _sent.Remove(hostId); if (!_gone.Contains(hostId)) _gone.Add(hostId); _sendAt = 0; }
        private void Rescan()
        {
            float now = Time.unscaledTime;
            if (now < _rescanAt) return;
            _rescanAt = now + .5f; _scanAt = now + ScanEvery;
            Scan(null);
        }

        // 1.4.16: in the world, also while the game's occlusion culling (GDOC) has switched it off because the
        // host's camera does not see it (a barrel or a canister in a holder outside the train, an item behind the
        // host): FindObjectsOfType skipped it and activeInHierarchy is false, so the guest never got it.
        // 1.4.18: or one of its parents (a barrel rack outside the train switched off with the barrels in it).
        internal static bool Present(GameObject go)
        {
            if (go == null) return false;
            if (go.activeInHierarchy) return true;
            for (var t = go.transform; t != null; t = t.parent)
                if (!t.gameObject.activeSelf && t.GetComponent<Bearroll.GDOC_Occludee>() == null) return false;
            return true;
        }
        // Guest: its own copy, hidden, must stay so: the occlusion culling would switch it on again when seen.
        private static void HideOwn(GameObject go)
        {
            foreach (var occludee in go.GetComponentsInChildren<Bearroll.GDOC_Occludee>(true)) Object.Destroy(occludee);
            go.SetActive(false);
        }

        internal static bool Loose(InventoryItem item)
        {
            if (item == null || item.inInventory != null || !Present(item.gameObject)) return false;
            if (item.GetComponentInParent<ZombieFighterController>() != null || item.GetComponentInParent<LanWorldReplica>() != null ||
                item.GetComponentInParent<LanTrainReplica>() != null) return false;
            if (item.transform.parent != null && item.transform.parent.GetComponentInParent<InventoryItem>() != null) return false;
            var controller = InventoryController.global;
            if (controller != null && controller.ItemPreviewForPlacement == item) return false;
            var db = ItemsDataBase.Global;
            return db != null && db.itemPrefab != null && item.itemID >= 0 && item.itemID < db.itemPrefab.Length && db.itemPrefab[item.itemID] != null && item.amount >= 1;
        }
        private static string BagName()
        {
            var loot = GlobalObjectLootController.global;
            return loot == null || loot.PrefabZombieLoot == null ? null : loot.PrefabZombieLoot.name + "(Clone)";
        }
        private static WorldItemFrame FrameOf(Component source)
        {
            var item = source as InventoryItem; if (item != null) return Frame(item);
            var takable = source as TakableItem; if (takable != null) return Frame(takable);
            return Frame((Inventory)source);
        }
        // 1.1.6: a carryable object the guest should see: lying or standing, not carried, of a
        // prefab the guest has too.
        internal static bool Carryable(TakableItem takable)
        {
            if (takable == null || !Present(takable.gameObject) || Carried(takable) || TakablePrefab(takable) < 0) return false;
            // 1.4.13: a fuel station's gun is LanFuelStation's: as a carryable copy it lay over the guest's own gun,
            // took its use key and asked the host to carry it like a barrel (refused), so the station never worked.
            if (LanFuelStation.IsStationGun(takable)) return false;
            return takable.GetComponentInParent<ZombieFighterController>() == null && takable.GetComponentInParent<LanWorldReplica>() == null &&
                takable.GetComponentInParent<LanTrainReplica>() == null;
        }
        // 1.4.18: carried now (in the host player's hands). The game leaves taked set on what the player put down
        // with the use key or hung in a holder (ZombieFighterRays.DropItem does not clear it): barrels and canisters
        // in the train's holders, never sent to the guest.
        private static bool Carried(TakableItem takable)
        {
            if (!takable.taked || takable.StoredInHolder != null) return false;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            return player != null && player.zombieFighterRays != null && player.zombieFighterRays.takableItemTaked == takable;
        }
        // 1.2.1: the host's own player holds it.
        private static bool HeldByHost(TakableItem takable)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            return takable != null && takable.taked && takable.gameObject.activeInHierarchy && player != null && player.zombieFighterRays != null &&
                player.zombieFighterRays.takableItemTaked == takable && TakablePrefab(takable) >= 0;
        }
        private static int TakablePrefab(TakableItem takable)
        {
            if (takable.GetComponent<RobotDogAiController>() != null)
                return GlobalManager.global != null && GlobalManager.global.ControlledRobotPrefab != null ? RobotDogPrefab : -1;
            var id = takable.GetComponent<PrefabID>();
            var misc = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.MiscPrefabs;
            return id != null && misc != null && id.ID >= 0 && id.ID < misc.Count && id.ID < RobotDogPrefab && misc[id.ID] != null ? id.ID : -1;
        }
        // Its prefab, its car + 1 (0: in the world) and a dog's state, its pose in that car's space.
        private static WorldItemFrame Frame(TakableItem takable)
        {
            var manager = GlobalManager.global;
            var train = manager == null ? null : manager.controlledTrain;
            var car = takable.GetComponentInParent<Zompiercer.Train.TrainCar>();
            bool hostHolds = HeldByHost(takable), guestHolds = LanTakables.CarriedByGuest(takable);
            int index = car == null || train == null || hostHolds || guestHolds ? -1 : TrainLayout.CarIndex(train, car);
            NetPose pose = Pose(takable.transform);
            if (index >= 0 && index < LanProtocol.MaxCars)
            {
                var p = car.transform.InverseTransformPoint(takable.transform.position);
                var q = Quaternion.Inverse(car.transform.rotation) * takable.transform.rotation;
                if (Mathf.Abs(p.x) <= 256f && Mathf.Abs(p.y) <= 256f && Mathf.Abs(p.z) <= 256f) pose = new NetPose { X = p.x, Y = p.y, Z = p.z, QX = q.x, QY = q.y, QZ = q.z, QW = q.w };
                else index = -1;
            }
            else index = -1;
            int state = 0;
            var dog = takable.GetComponent<RobotDogAiController>();
            if (dog != null)
            {
                if (dog.robotActivated) state |= LanProtocol.TakableActive;
                if (dog.state == "Walk") state |= LanProtocol.TakableWalk;
                else if (dog.state == "Run" || dog.state == "Jump") state |= LanProtocol.TakableRun;
                state |= LanTakables.OwnerBits(takable);
            }
            if (hostHolds) state |= LanProtocol.TakableHeldByHost;
            if (guestHolds) state |= LanProtocol.TakableHeldByGuest;
            return new WorldItemFrame { HostId = takable.GetInstanceID(), ItemId = TakablePrefab(takable), Amount = index + 1 | state << 8, Kind = TakableKind, Pose = pose,
                Devices = dog == null ? null : Devices(dog) };
        }
        // 1.4.13: what a dog's device slots hold (the RobotDevice id the game spawns for the slot's item,
        // NoDevice: empty), up to the last slot in use. The guest showed its dogs bare.
        private static byte[] Devices(RobotDogAiController dog)
        {
            var slots = dog.Devices;
            int count = slots == null ? 0 : Math.Min(slots.Count, LanProtocol.MaxDogDevices), used = 0;
            var ids = new byte[count];
            for (int i = 0; i < count; i++)
            {
                var item = slots[i] == null || slots[i].inventory == null ? null : slots[i].inventory.GetFirstItem();
                int id = item == null ? -1 : item.RobotDeviceID;
                ids[i] = id >= 0 && id < LanProtocol.NoDevice ? (byte)id : LanProtocol.NoDevice;
                if (ids[i] != LanProtocol.NoDevice) used = i + 1;
            }
            if (used < count) Array.Resize(ref ids, used);
            return ids;
        }
        private static WorldItemFrame Frame(InventoryItem item)
        { return new WorldItemFrame { HostId = item.GetInstanceID(), ItemId = item.itemID, Amount = Math.Min(item.amount, LanStorage.MaxAmount), Kind = ItemKind, Pose = Pose(item.transform) }; }
        private static WorldItemFrame Frame(Inventory bag)
        { return new WorldItemFrame { HostId = bag.GetInstanceID(), ItemId = -1, Amount = 0, Kind = BagKind, Pose = Pose(LanDeathBag.RootOf(bag).transform) }; }
        private static NetPose Pose(Transform t)
        {
            var p = t.position; var q = t.rotation;
            return new NetPose { X = p.x, Y = p.y, Z = p.z, QX = q.x, QY = q.y, QZ = q.z, QW = q.w };
        }
        private static NetPose Identity() { return new NetPose { QW = 1 }; }
        private static bool Same(WorldItemFrame a, WorldItemFrame b)
        {
            return a.ItemId == b.ItemId && a.Amount == b.Amount && a.Kind == b.Kind &&
                Math.Abs(a.Pose.X - b.Pose.X) < .05f && Math.Abs(a.Pose.Y - b.Pose.Y) < .05f && Math.Abs(a.Pose.Z - b.Pose.Z) < .05f &&
                Math.Abs(a.Pose.QX * b.Pose.QX + a.Pose.QY * b.Pose.QY + a.Pose.QZ * b.Pose.QZ + a.Pose.QW * b.Pose.QW) > .9995f && // 1.1.6: turning counts
                SameBytes(a.Devices, b.Devices);
        }
        private static bool SameBytes(byte[] a, byte[] b)
        {
            int count = a == null ? 0 : a.Length;
            if (count != (b == null ? 0 : b.Length)) return false;
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return false;
            return true;
        }
        // 1.4.13: a dog's frame carries its devices: a batch keeps within the packet's room.
        private static bool Fits(ref int bytes, WorldItemFrame frame)
        {
            int size = LanProtocol.ItemBytes(frame);
            if (bytes + size > LanProtocol.MaxItemBytes) return false;
            bytes += size; return true;
        }

        // ---- Guest ----
        internal void Receive(Packet p)
        {
            if (_host || !_ready || p.Kind != PacketKind.WorldItems || p.WorldEpoch != _epoch || p.Scene != _scene) return;
            if (_received != 0 && unchecked((int)(p.Sequence - _received)) <= 0) return;
            _received = p.Sequence;
            float now = Time.unscaledTime;
            int created = 0;
            foreach (var frame in p.Items)
            {
                Shown shown;
                _shown.TryGetValue(frame.HostId, out shown);
                if (frame.Kind == GoneKind) { if (shown != null) { Object.Destroy(shown.Root); _shown.Remove(frame.HostId); } continue; }
                if (shown != null && (shown.Kind != frame.Kind || shown.ItemId != frame.ItemId)) { Object.Destroy(shown.Root); _shown.Remove(frame.HostId); shown = null; }
                if (shown == null)
                {
                    if (_shown.Count >= MaxShown || created >= 6) continue; // the next refresh brings it
                    var root = CreateVisual(frame);
                    if (root == null) continue;
                    created++;
                    // 1.4.14: which carryable objects of the host this guest shows (the first 30 of a session).
                    if (frame.Kind == TakableKind && _takablesLogged < 30)
                    { _takablesLogged++; _log?.Invoke("Host carryable object shown: " + root.name + " prefab " + frame.ItemId + " car " + ((frame.Amount & 255) - 1)); }
                    shown = new Shown { Root = root, ItemId = frame.ItemId, Kind = frame.Kind };
                    _shown.Add(frame.HostId, shown);
                    var marker = root.GetComponent<LanWorldReplica>();
                    marker.HostId = frame.HostId; marker.ItemId = frame.ItemId; marker.Kind = frame.Kind;
                    // 1.4.13: its animator is found once the copy is shown (FindAnimator).
                }
                if (shown.Amount != frame.Amount && frame.Kind != TakableKind) shown.PickedUp = false;
                shown.Seen = now; shown.Amount = frame.Amount;
                shown.Root.GetComponent<LanWorldReplica>().Amount = frame.Amount;
                if (frame.Kind == TakableKind) { shown.Last = frame; PlaceTakable(shown, frame); ShowDevices(shown, frame.Devices); continue; }
                shown.Root.transform.SetPositionAndRotation(new Vector3(frame.Pose.X, frame.Pose.Y, frame.Pose.Z), new Quaternion(frame.Pose.QX, frame.Pose.QY, frame.Pose.QZ, frame.Pose.QW));
                shown.Root.SetActive(!shown.PickedUp);
            }
        }

        // 1.1.6: a carryable object in its car (which the guest's copy of the train follows) or in
        // the world; it glides to each new place, and a dog walks or runs while it moves.
        private static void PlaceTakable(Shown shown, WorldItemFrame frame)
        {
            int car = (frame.Amount & 255) - 1, state = frame.Amount >> 8;
            // 1.2.1: the guest's own stand-in shows what it carries; the host's in its avatar's hands.
            shown.HeldByHost = (state & LanProtocol.TakableHeldByHost) != 0;
            if ((state & LanProtocol.TakableHeldByGuest) != 0 || frame.HostId == LanTakables.ProxyHostId) { shown.Root.SetActive(false); return; }
            if (shown.HeldByHost) { shown.Placed = false; shown.Root.transform.SetParent(null, false); HoldByHost(shown); return; }
            Transform parent = null;
            if (car >= 0)
            {
                var carObject = TrainSync.GetCar(car);
                if (carObject == null) { shown.Root.SetActive(false); return; }
                parent = carObject.transform;
            }
            var t = shown.Root.transform;
            shown.Target = new Vector3(frame.Pose.X, frame.Pose.Y, frame.Pose.Z);
            shown.TargetRotation = new Quaternion(frame.Pose.QX, frame.Pose.QY, frame.Pose.QZ, frame.Pose.QW);
            if (!shown.Placed || t.parent != parent || (t.localPosition - shown.Target).sqrMagnitude > 25f)
            { t.SetParent(parent, false); t.localPosition = shown.Target; t.localRotation = shown.TargetRotation; shown.Placed = true; }
            shown.Root.SetActive(true);
            if (shown.Parameters == null) FindAnimator(shown);
            if (shown.Animator == null) return;
            bool active = (state & LanProtocol.TakableActive) != 0, walk = (state & LanProtocol.TakableWalk) != 0, run = (state & LanProtocol.TakableRun) != 0;
            if (shown.ItemId == RobotDogPrefab && shown.Unfolded != active)
            {
                if (shown.Pulse != null) SetBool(shown, shown.Pulse, false);
                shown.Pulse = active ? "Activation" : "Deactivation"; shown.PulseUntil = Time.unscaledTime + 1f;
                SetBool(shown, shown.Pulse, true);
                shown.Unfolded = active;
            }
            SetBool(shown, "Idle1", active && !walk && !run);
            SetBool(shown, "Walk", active && walk);
            SetBool(shown, "Run", active && run);
        }
        // 1.4.13: an animator tells its parameters only while its object is active (the copy was still inactive
        // when they were read, so a dog never got "Activation" and walked folded); and a dog's body animator is
        // the one with "Activation" (a turret's or a lamp's may come first).
        private static void FindAnimator(Shown shown)
        {
            shown.Parameters = new HashSet<string>();
            shown.Animator = null;
            foreach (var animator in shown.Root.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.runtimeAnimatorController == null || !animator.isActiveAndEnabled) continue;
                var names = new HashSet<string>();
                foreach (var parameter in animator.parameters) names.Add(parameter.name);
                if (names.Count == 0) continue;
                bool body = names.Contains("Activation");
                if (shown.Animator == null || body)
                {
                    shown.Animator = animator; shown.Parameters = names;
                    if (body) return;
                }
            }
        }
        private static void SetBool(Shown shown, string name, bool value)
        {
            if (shown.Parameters != null && shown.Parameters.Contains(name) && shown.Animator.isActiveAndEnabled) shown.Animator.SetBool(name, value);
        }

        // 1.2.1: in front of the host's avatar, as the game holds a carried object.
        private static void HoldByHost(Shown shown)
        {
            var avatar = PartnerAvatar?.Invoke();
            if (avatar == null) { shown.Root.SetActive(false); return; }
            var yaw = Quaternion.Euler(0f, avatar.eulerAngles.y, 0f);
            bool dog = shown.ItemId == RobotDogPrefab;
            shown.Root.transform.SetPositionAndRotation(avatar.position + yaw * new Vector3(0f, dog ? .85f : 1.05f, .6f), yaw);
            shown.Root.SetActive(true);
            if (shown.Animator != null) { SetBool(shown, "Idle1", false); SetBool(shown, "Walk", false); SetBool(shown, "Run", false); }
        }

        // Guest (1.2.1): the guest let go of its stand-in; the host's copy shows as last heard.
        internal static void Reveal(int hostId)
        {
            var current = _guestCurrent;
            Shown shown;
            if (current == null || !current._shown.TryGetValue(hostId, out shown) || shown.Root == null || shown.Last == null) return;
            shown.Placed = false;
            PlaceTakable(shown, shown.Last);
        }

        // Guest (1.2.1): where the host's copy with this id is (null: not shown).
        internal static Vector3? ShownPosition(int hostId)
        {
            var current = _guestCurrent;
            Shown shown;
            if (current == null || !current._shown.TryGetValue(hostId, out shown) || shown.Root == null) return null;
            return shown.Root.transform.position;
        }

        private void GuestTick(float now)
        {
            _guestCurrent = this;
            var stale = new List<int>();
            foreach (var pair in _shown) if (pair.Value.Root == null || now - pair.Value.Seen > Stale) stale.Add(pair.Key);
            foreach (int id in stale) { if (_shown[id].Root != null) Object.Destroy(_shown[id].Root); _shown.Remove(id); }
            float smooth = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 10f);
            foreach (var shown in _shown.Values)
            {
                if (shown.Pulse != null && now >= shown.PulseUntil && shown.Animator != null) { SetBool(shown, shown.Pulse, false); shown.Pulse = null; }
                if (shown.Kind == TakableKind && shown.HeldByHost && shown.Root != null) { HoldByHost(shown); continue; }
                if (shown.Kind != TakableKind || !shown.Placed || shown.Root == null) continue;
                var t = shown.Root.transform;
                t.localPosition = Vector3.Lerp(t.localPosition, shown.Target, smooth);
                t.localRotation = Quaternion.Slerp(t.localRotation, shown.TargetRotation, smooth);
            }
            if (now < _hideAt) return;
            _hideAt = now + HideEvery;
            // The guest's own copy of the world generated these: they are not the host's.
            foreach (var item in Object.FindObjectsOfType<InventoryItem>(true))
                if (Loose(item) && !Dropped.Contains(item)) HideOwn(item.gameObject);
            // 1.1.6: so are its carryable objects (barrels of the scene, a robot dog of its own train).
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var held = player == null || player.zombieFighterRays == null ? null : player.zombieFighterRays.takableItemTaked;
            foreach (var takable in Object.FindObjectsOfType<TakableItem>(true))
            {
                if (!Present(takable.gameObject)) continue;
                if (takable == held || takable.GetComponentInParent<ZombieFighterController>() != null || takable.GetComponent<LanCarriedProxy>() != null) continue;
                // 1.4.3: a fuel station's gun shows the host's (LanFuelStation).
                if (LanFuelStation.IsStationGun(takable)) continue;
                // 1.2.1: its holders on the train are free for what the guest carries (the host decides).
                if (takable.StoredInHolder != null) { takable.StoredInHolder.AttachedItem = null; takable.StoredInHolder = null; }
                HideOwn(takable.gameObject);
            }
        }

        internal void ClearShown()
        {
            if (_guestCurrent == this) _guestCurrent = null;
            foreach (var shown in _shown.Values) if (shown.Root != null) Object.Destroy(shown.Root);
            _shown.Clear();
        }
        internal void HidePickedItem(int hostId)
        {
            Shown shown;
            if (_shown.TryGetValue(hostId, out shown)) { shown.PickedUp = true; shown.Root.SetActive(false); }
        }

        // A script-free copy: transforms, meshes, materials; colliders only for the
        // guest's own look-at ray (layer 2, which the character does not collide with).
        private static GameObject CreateVisual(WorldItemFrame frame)
        {
            Component prefab = null;
            if (frame.Kind == ItemKind)
            {
                var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
                if (prefabs != null && frame.ItemId < prefabs.Length) prefab = prefabs[frame.ItemId];
            }
            else if (frame.Kind == TakableKind)
            {
                var misc = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.MiscPrefabs;
                if (frame.ItemId == RobotDogPrefab) prefab = GlobalManager.global == null ? null : GlobalManager.global.ControlledRobotPrefab;
                else if (misc != null && frame.ItemId < misc.Count) prefab = misc[frame.ItemId];
            }
            else
            {
                var loot = GlobalObjectLootController.global;
                prefab = loot == null ? null : loot.PrefabZombieLoot;
            }
            if (prefab == null) return null;
            var staging = new GameObject("LAN world item staging");
            staging.SetActive(false);
            GameObject copy = null;
            try
            {
                copy = Object.Instantiate(prefab.gameObject, staging.transform, false);
                copy.name = "LAN host item " + prefab.name; // 1.4.14: the prefab, for the log
                // 1.4.13: a robot dog's device mounts (where the game puts a slot's device: the slot's inventory).
                Transform[] slots = null;
                var dog = frame.Kind == TakableKind ? copy.GetComponent<RobotDogAiController>() : null;
                if (dog != null && dog.Devices != null)
                {
                    slots = new Transform[Math.Min(dog.Devices.Count, LanProtocol.MaxDogDevices)];
                    for (int i = 0; i < slots.Length; i++)
                    {
                        var slot = dog.Devices[i] == null || dog.Devices[i].inventory == null ? null : dog.Devices[i].inventory.transform;
                        slots[i] = slot != null && slot.IsChildOf(copy.transform) ? slot : null;
                    }
                }
                // A carryable object keeps its animator (a robot dog walks); nothing else animates.
                Strip(copy, frame.Kind == TakableKind);
                copy.AddComponent<LanWorldReplica>().DeviceSlots = slots;
                copy.transform.SetParent(null, false);
                copy.SetActive(false);
                return copy;
            }
            catch (Exception ex)
            {
                _staticLog?.Invoke("Host item copy failed: " + ex.GetType().Name + ": " + ex.Message);
                if (copy != null) Object.Destroy(copy);
                return null;
            }
            finally { Object.Destroy(staging); }
        }

        // A copy without the game's scripts, sounds or agents, on layer 2: only its solid colliders stay.
        // 1.4.13: scripts go last to first (one a later script requires goes after it).
        private static void Strip(GameObject copy, bool animated)
        {
            var behaviours = copy.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = behaviours.Length - 1; i >= 0; i--) Object.DestroyImmediate(behaviours[i]);
            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = true; }
            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) { audio.playOnAwake = false; audio.enabled = false; }
            foreach (var animator in copy.GetComponentsInChildren<Animator>(true)) animator.enabled = animated;
            foreach (var agent in copy.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) Object.DestroyImmediate(agent);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) collider.enabled = !collider.isTrigger;
        }

        // Guest (1.4.13): the devices on a host robot dog (a turret, a lamp, a medkit, a box) as the game puts them
        // on: each device's model in its slot, without its scripts (it neither shoots, lights nor heals here).
        private static void ShowDevices(Shown shown, byte[] devices)
        {
            if (shown.ItemId != RobotDogPrefab || shown.Root == null || SameBytes(shown.Devices, devices)) return;
            shown.Devices = devices;
            if (shown.DeviceCopies != null) foreach (var old in shown.DeviceCopies) if (old != null) Object.Destroy(old);
            var slots = shown.Root.GetComponent<LanWorldReplica>().DeviceSlots;
            shown.DeviceCopies = new GameObject[slots == null ? 0 : slots.Length];
            for (int i = 0; i < shown.DeviceCopies.Length && devices != null && i < devices.Length; i++)
                if (devices[i] != LanProtocol.NoDevice && slots[i] != null) shown.DeviceCopies[i] = DeviceCopy(devices[i], slots[i]);
        }
        private static bool _deviceFailed;
        private static GameObject DeviceCopy(int id, Transform slot)
        {
            var list = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.RobotDevices;
            if (list == null || id >= list.Count || list[id] == null) return null;
            var staging = new GameObject("LAN robot device staging");
            staging.SetActive(false);
            GameObject copy = null;
            try
            {
                copy = Object.Instantiate(list[id].gameObject, staging.transform, false);
                copy.name = "LAN host robot device";
                Strip(copy, false);
                foreach (var light in copy.GetComponentsInChildren<Light>(true)) light.enabled = false; // its switch is the host's
                // As UpdateDevices: the device's own scale, in its slot's place and turn.
                copy.transform.parent = slot;
                copy.transform.localPosition = Vector3.zero; copy.transform.localEulerAngles = Vector3.zero;
                return copy;
            }
            catch (Exception ex)
            {
                if (!_deviceFailed) { _deviceFailed = true; _staticLog?.Invoke("Robot device copy failed: " + ex.GetType().Name + ": " + ex.Message); }
                if (copy != null) Object.Destroy(copy);
                return null;
            }
            finally { Object.Destroy(staging); }
        }

        // Guest: the host item or bag in front of the camera, if any.
        internal static LanWorldReplica InView()
        {
            var shown = InViewAny();
            return shown != null && shown.Kind != TakableKind ? shown : null; // 1.1.6: carryable objects stay the host's
        }
        // 1.2.1: whatever host copy is in front of the camera (1.4.11: as LookAt finds it).
        internal static LanWorldReplica InViewAny()
        {
            var seen = LookAt(Look, collider => collider.GetComponentInParent<LanWorldReplica>() != null);
            return seen == null ? null : seen.GetComponentInParent<LanWorldReplica>();
        }

        // Guest (1.4.11): the host copy the player looks at for a pick-up (`wanted`), as the game's own
        // look-at ray would meet it: only colliders on the layers that ray uses (SelectionManager.layerMask)
        // hide what is behind them; a scene's invisible volumes on other layers do not. A shown host shelf
        // or cabinet (TrainLayout.SupportLayer) may be one box around its shelves: an item inside that box
        // is still the one looked at, an item behind it is not.
        internal static Collider LookAt(float reach, Func<Collider, bool> wanted)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var camera = Camera.main;
            if (player == null || camera == null) return null;
            var hits = Physics.RaycastAll(new Ray(camera.transform.position, camera.transform.forward), reach, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var selection = SelectionManager.Global;
            int blocking = selection == null ? ~0 : selection.layerMask.value;
            Collider shell = null;
            foreach (var hit in hits)
            {
                var collider = hit.collider;
                if (collider == null || collider.transform.IsChildOf(player.transform)) continue;
                if (wanted(collider))
                {
                    if (shell == null) return collider;
                    var box = shell.bounds; box.Expand(.1f);
                    return box.Contains(collider.bounds.center) ? collider : null;
                }
                if (collider.gameObject.layer == TrainLayout.SupportLayer) { if (shell == null) shell = collider; continue; }
                if ((blocking & 1 << collider.gameObject.layer) != 0) return null;
            }
            return null;
        }
    }

    // Marks a bag holding a dead guest's belongings (0.12.0); it may lie on a train car.
    internal sealed class LanDeathBag : MonoBehaviour
    {
        // The object that stands for a bag: this marker, or the bag prefab's root.
        internal static Component RootOf(Inventory bag)
        {
            var marker = bag.GetComponentInParent<LanDeathBag>();
            return marker != null ? (Component)marker : bag.transform.root;
        }
    }

    internal sealed class LanWorldReplica : MonoBehaviour
    {
        internal int HostId, ItemId, Amount;
        internal byte Kind;
        internal Transform[] DeviceSlots; // 1.4.13: a robot dog's device mounts
    }
}
