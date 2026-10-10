using System;
using System.Collections.Generic;
using System.Text;
using FluffyUnderware.Curvy.Examples;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using Zompiercer;
using Zompiercer.Inventory;
using Zompiercer.Train;

namespace ZompiercerLAN
{
    // Shared driving (0.10.0). The host may let the guest use the train's own controls:
    // the cab's buttons (engine, brake, reverse, spotlight, horn...), its throttle lever,
    // the cab light switches, repairs of broken levers with a spare lever (item 66), and
    // the light switches of parts the host built. The guest's train is a copy that only
    // follows the host (TrainSync): its native controls stay the guest's own objects, so
    // every use of one becomes a request (LanStorage.StorageControl) and the host does
    // the same on its own train, as its own hand would. The host's train frame brings the
    // result back (button states, broken buttons, cab lights, levers) and whether the host
    // allows it at all.
    //
    // Controls are matched by their order among the train's native controls (not inside a
    // built part, furniture or replica) and checked by a hash of their place in the train,
    // so a different train never matches. Main thread only.
    internal static class LanTrainControl
    {
        internal const float Reach = 3.5f;
        private const int MaxLights = 16, SwitchAction = 1;
        // Host: the guest may drive (LAN panel; [Guests] FriendMayDrive).
        internal static bool HostAllows;
        // Guest: what the host's latest train frame says.
        internal static bool GuestAllowed;
        // Guest: the host's player is dead (0.12.0).
        internal static bool HostDead;
        // Guest: in the host's world with a usable session (requests are answered).
        internal static bool GuestAttached;
        // Guest: queues one control request (kind, index, check, value, escrow); false if not queued.
        internal static Func<int, int, int, int, object[], bool> GuestRequest;
        internal static string Failure { get; private set; }
        private static Harmony _harmony;
        private static Action<string> _log;
        private static float _refusedAt = -10f, _nextLeverSend, _unsharedLogAt = -10f;
        private static readonly float[] LeverEditedAt = new float[LanProtocol.MaxLevers];
        private static readonly bool[] LeverPending = new bool[LanProtocol.MaxLevers];

        internal sealed class Controls
        {
            internal TrainController Owner;
            internal HardwareButton[] Buttons;
            internal Lever[] Levers;
            internal LightSwitcher[] Lights;
            internal int[] ButtonChecks, LeverChecks, LightChecks;
            internal readonly Dictionary<Component, int> Index = new Dictionary<Component, int>();
        }
        private static Controls _host, _guest;
        // Native interaction and the direct crosshair path may see the same frame.
        private static readonly Dictionary<Component, int> SwitchedFrame = new Dictionary<Component, int>();
        private static readonly Dictionary<Lever, int> LeverUpFrame = new Dictionary<Lever, int>();
        private static readonly Dictionary<Lever, int> LeverDownFrame = new Dictionary<Lever, int>();
        private static bool FirstThisFrame<T>(Dictionary<T, int> frames, T control)
        {
            int frame;
            if (frames.TryGetValue(control, out frame) && frame == Time.frameCount) return false;
            frames[control] = Time.frameCount;
            return true;
        }
        // Guest: the native events of its copy's buttons, removed while it follows the host.
        private static readonly Dictionary<HardwareButton, KeyValuePair<UnityEvent, EventWithBool>> Events = new Dictionary<HardwareButton, KeyValuePair<UnityEvent, EventWithBool>>();

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.train-control");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(HardwareButton), "Switch"), prefix: new HarmonyMethod(typeof(LanTrainControl), nameof(SwitchPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(HardwareButton), "LeverButtonRepair"), prefix: new HarmonyMethod(typeof(LanTrainControl), nameof(RepairPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Lever), "ModifyValue"), prefix: new HarmonyMethod(typeof(LanTrainControl), nameof(LeverPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(LightSwitcher), "Switch"), prefix: new HarmonyMethod(typeof(LanTrainControl), nameof(LightPrefix)));
            }
            catch (Exception ex)
            {
                // Without the hooks the guest's copy of the cab stays switched off (TrainSync).
                _harmony.UnpatchSelf();
                Failure = ex.GetType().Name + ": " + ex.Message;
                log("Train control hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { ReleaseGuest(); _harmony?.UnpatchSelf(); _harmony = null; GuestRequest = null; GuestAttached = false; GuestAllowed = false; }

        // ---- The train's native controls, in a fixed order ----

        internal static Controls Of(TrainController controller, ref Controls cache)
        {
            if (controller == null) return null;
            if (cache != null && cache.Owner == controller && Alive(cache)) return cache;
            var set = new Controls { Owner = controller };
            var buttons = new List<HardwareButton>(); var levers = new List<Lever>(); var lights = new List<LightSwitcher>();
            foreach (var b in controller.GetComponentsInChildren<HardwareButton>(true))
                if (Native(b) && !b.trainDetection && buttons.Count < LanStorage.MaxControlIndex) buttons.Add(b);
            foreach (var l in controller.GetComponentsInChildren<Lever>(true))
                if (Native(l) && levers.Count < LanProtocol.MaxLevers) levers.Add(l);
            foreach (var s in controller.GetComponentsInChildren<LightSwitcher>(true))
                if (Native(s) && lights.Count < MaxLights) lights.Add(s);
            set.Buttons = buttons.ToArray(); set.Levers = levers.ToArray(); set.Lights = lights.ToArray();
            set.ButtonChecks = new int[set.Buttons.Length]; set.LeverChecks = new int[set.Levers.Length]; set.LightChecks = new int[set.Lights.Length];
            for (int i = 0; i < set.Buttons.Length; i++) { set.ButtonChecks[i] = Check(controller.transform, set.Buttons[i], set.Buttons[i].functionName); set.Index[set.Buttons[i]] = i; }
            for (int i = 0; i < set.Levers.Length; i++) { set.LeverChecks[i] = Check(controller.transform, set.Levers[i], "lever"); set.Index[set.Levers[i]] = i; }
            for (int i = 0; i < set.Lights.Length; i++) { set.LightChecks[i] = Check(controller.transform, set.Lights[i], "light"); set.Index[set.Lights[i]] = i; }
            cache = set;
            return set;
        }

        private static bool Native(Component c)
        {
            return c != null && c.GetComponentInParent<TrainPart>() == null && c.GetComponentInParent<Furniture>() == null &&
                c.GetComponentInParent<LanTrainReplica>() == null;
        }
        private static bool Alive(Controls set)
        {
            foreach (var b in set.Buttons) if (b == null) return false;
            foreach (var l in set.Levers) if (l == null) return false;
            foreach (var s in set.Lights) if (s == null) return false;
            return true;
        }

        // FNV-1a of the control's path under the train and its function: equal on both sides
        // for the same native prefab, different for anything else.
        internal static int Check(Transform root, Component control, string function)
        {
            var names = new List<string>();
            for (var t = control.transform; t != null && t != root; t = t.parent) names.Add(t.name);
            names.Reverse();
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(string.Join("/", names.ToArray()) + "|" + control.GetType().Name + "|" + (function ?? "")))
            { hash ^= b; hash *= 16777619; }
            return unchecked((int)hash);
        }

        // ---- Host ----

        // The host's train frame: its controls' state and the permission.
        internal static void Capture(TrainController controller, TrainFrame frame)
        {
            if (HostAllows) frame.Flags |= 32;
            if (LanGuestDeath.LocalDead()) frame.Flags |= 64;
            var set = Of(controller, ref _host);
            if (set == null) return;
            uint buttons = 0, broken = 0; ushort lights = 0;
            for (int i = 0; i < set.Buttons.Length; i++)
            {
                if (set.Buttons[i].State) buttons |= 1u << i;
                if (set.Buttons[i].defective) broken |= 1u << i;
            }
            for (int i = 0; i < set.Lights.Length; i++) if (set.Lights[i].active) lights |= (ushort)(1 << i);
            frame.Buttons = buttons; frame.Broken = broken; frame.Lights = lights;
            frame.Levers = new float[set.Levers.Length];
            for (int i = 0; i < set.Levers.Length; i++) frame.Levers[i] = Mathf.Clamp(set.Levers[i].Value, -1000f, 1000f);
        }

        // One validated request of the guest (LanStorage.DecodeControl), done as the host's own hand would.
        internal static byte Host(int kind, int index, int check, int value, Vector3? guest)
        {
            var manager = GlobalManager.global; var scenes = GlobalSceneManager.global;
            // 1.1.6: doors do not need the driving permission.
            if (kind == LanStorage.ControlDoor)
            {
                if (manager == null || scenes == null || scenes.SceneCurrentlyLoading || manager.controlledTrain == null) return LanStorage.Unavailable;
                if (guest == null) return LanStorage.Lost;
                return HostDoor(manager.controlledTrain, index, check, value, guest.Value);
            }
            if (!HostAllows) return LanStorage.Locked;
            if (manager == null || scenes == null || scenes.SceneCurrentlyLoading) return LanStorage.Unavailable;
            if (guest == null) return LanStorage.Lost;
            if (kind == LanStorage.ControlPartLight) return HostPartLight(manager.controlledTrain, index, check, value, guest.Value);
            var set = Of(manager.controlledTrainController, ref _host);
            if (set == null) return LanStorage.Unavailable;
            Component target; int expected;
            switch (kind)
            {
                case LanStorage.ControlButton: case LanStorage.ControlRepair:
                    if (index >= set.Buttons.Length) return LanStorage.NotFound;
                    target = set.Buttons[index]; expected = set.ButtonChecks[index]; break;
                case LanStorage.ControlLever:
                    if (index >= set.Levers.Length) return LanStorage.NotFound;
                    target = set.Levers[index]; expected = set.LeverChecks[index]; break;
                case LanStorage.ControlCabLight:
                    if (index >= set.Lights.Length) return LanStorage.NotFound;
                    target = set.Lights[index]; expected = set.LightChecks[index]; break;
                default: return LanStorage.Invalid;
            }
            if (check != expected) return LanStorage.NotFound;
            if (Vector3.Distance(target.transform.position, guest.Value) > Reach) return LanStorage.TooFar;
            switch (kind)
            {
                case LanStorage.ControlButton:
                    var button = (HardwareButton)target;
                    if (button.defective) return LanStorage.Broken;
                    button.Switch();
                    return LanStorage.Ok;
                case LanStorage.ControlRepair:
                    var broken = (HardwareButton)target;
                    if (!broken.defective) return LanStorage.NotAllowed;
                    broken.LeverButtonRepair(); // the spare lever came from the guest's ledger
                    return LanStorage.Ok;
                case LanStorage.ControlLever:
                    var lever = (Lever)target;
                    float v = Mathf.Clamp(value / 1000f, lever.MinValue, lever.MaxValue);
                    lever.Value = v;
                    if (lever.OnChange != null) lever.OnChange.Invoke(v);
                    return LanStorage.Ok;
                default:
                    var light = (LightSwitcher)target;
                    if (light.trainPart != null && !light.trainPart.IsFunctional()) return LanStorage.NotAllowed;
                    light.Switch();
                    return LanStorage.Ok;
            }
        }

        private static byte HostPartLight(TrainManager train, int carIndex, int identity, int kind, Vector3 guest)
        {
            if (train == null) return LanStorage.Unavailable;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return LanStorage.NotFound; }
            Component target = null;
            if (kind == LanStorage.TrainFurniture)
            {
                if (car.AllFurniture != null)
                    foreach (var furniture in car.AllFurniture) if (furniture != null && furniture.GetInstanceID() == identity) { target = furniture; break; }
            }
            else foreach (var part in car.GetComponentsInChildren<TrainPart>(true)) if (part != null && part.GetInstanceID() == identity) { target = part; break; }
            if (target == null) return LanStorage.NotFound;
            var light = SwitchOf(target);
            if (light == null) return LanStorage.NotAllowed;
            if (Vector3.Distance(light.transform.position, guest) > Reach + 1.5f && Vector3.Distance(target.transform.position, guest) > Reach + 1.5f) return LanStorage.TooFar;
            if (light.trainPart != null && !light.trainPart.IsFunctional()) return LanStorage.NotAllowed;
            light.Switch();
            return LanStorage.Ok;
        }

        // 1.1.6: a train door, opened or closed as the host's own hand would.
        internal const float DoorReach = 5f;
        private static byte HostDoor(TrainManager train, int car, int identity, int value, Vector3 guest)
        {
            int owner, hash; bool open;
            LanStorage.DoorFields(value, out owner, out open, out hash);
            DoorController door;
            try { door = TrainLayout.FindHostDoor(train, car, owner, identity, hash); }
            catch (Exception) { return LanStorage.NotFound; }
            if (door == null) return LanStorage.NotFound;
            bool near = Vector3.Distance(door.transform.position, guest) <= DoorReach;
            if (door.Doors != null) foreach (var leaf in door.Doors) if (leaf != null && Vector3.Distance(leaf.transform.position, guest) <= DoorReach) near = true;
            if (!near) return LanStorage.TooFar;
            if (door.automatic || door.Dependent || door.notInteractive || !door.IsFunctional()) return LanStorage.NotAllowed;
            if (door.broken || door.destroyed) return LanStorage.Broken;
            if (door.locked) return LanStorage.Closed;
            if (door.Open != open) door.Switch();
            return door.Open == open ? LanStorage.Ok : LanStorage.NotAllowed;
        }

        // The light switch of a built part or furniture (or of its prefab).
        internal static LightSwitcher SwitchOf(Component owner)
        {
            if (owner == null) return null;
            foreach (var interacting in owner.GetComponentsInChildren<InteractingObjects>(true))
                if (interacting != null && interacting.lightSwitcher != null) return interacting.lightSwitcher;
            return owner.GetComponentInChildren<LightSwitcher>(true);
        }

        // ---- Guest ----

        private static bool Active { get { return LanSaveIsolation.Active && GuestAttached && _guest != null; } }

        // The guest's copy of the cab: native controls usable (hover, animation), their own
        // events removed (a press is a request; the host's frame moves them).
        internal static void PrepareGuest(TrainController controller)
        {
            // Called again after every applied host layout: the host's permission stays as last mirrored.
            bool allowed = GuestAllowed, hostDead = HostDead;
            ReleaseGuest();
            GuestAllowed = allowed; HostDead = hostDead;
            var set = Of(controller, ref _guest);
            if (set == null || Failure != null) return;
            foreach (var b in set.Buttons)
            {
                Events[b] = new KeyValuePair<UnityEvent, EventWithBool>(b.OnDown, b.OnSwitch);
                b.OnDown = new UnityEvent(); b.OnSwitch = new EventWithBool();
                Enable(b);
            }
            // Cab light switches are never switched off (TrainSync.BlockCabControls): leave their colliders as they are.
            foreach (var l in set.Levers) Enable(l);
            for (int i = 0; i < LeverPending.Length; i++) { LeverPending[i] = false; LeverEditedAt[i] = -10f; }
            _log?.Invoke("Guest cab ready: " + set.Buttons.Length + " buttons, " + set.Levers.Length + " levers, " + set.Lights.Length + " lights");
        }
        private static void Enable(MonoBehaviour control)
        {
            control.enabled = true;
            foreach (var collider in control.GetComponentsInChildren<Collider>(true)) collider.enabled = true;
        }
        internal static void ReleaseGuest()
        {
            HostDead = false;
            foreach (var pair in Events)
                if (pair.Key != null) { pair.Key.OnDown = pair.Value.Key; pair.Key.OnSwitch = pair.Value.Value; }
            Events.Clear(); SwitchedFrame.Clear(); LeverUpFrame.Clear(); LeverDownFrame.Clear();
            _guest = null; GuestAllowed = false;
        }

        // The host's frame on the guest's copy, without the copy's own effects.
        internal static void Mirror(TrainFrame frame)
        {
            bool allowed = (frame.Flags & 32) != 0;
            if (allowed != GuestAllowed) _log?.Invoke(allowed ? "The host lets the guest drive" : "The host does not let the guest drive");
            GuestAllowed = allowed;
            HostDead = (frame.Flags & 64) != 0;
            var set = _guest;
            if (set == null || !Alive(set)) return;
            for (int i = 0; i < set.Buttons.Length; i++)
            {
                var b = set.Buttons[i];
                bool broken = (frame.Broken >> i & 1) != 0;
                if (b.defective != broken) { b.defective = broken; if (b.MovablePart != null) b.MovablePart.gameObject.SetActive(!broken); } // as UpdateDefective
                bool state = (frame.Buttons >> i & 1) != 0;
                if (b.State != state) b.Set(state); // events removed: animation, lamp and sound only
            }
            for (int i = 0; i < set.Lights.Length; i++)
            {
                var s = set.Lights[i];
                bool on = (frame.Lights >> i & 1) != 0;
                if (s.active == on) continue;
                s.SetInnerState(on);
                if (s.lightObjects != null) foreach (var lamp in s.lightObjects) if (lamp != null) lamp.SetActive(on);
            }
            float now = Time.unscaledTime;
            for (int i = 0; i < set.Levers.Length && i < frame.Levers.Length; i++)
                if (!LeverPending[i] && now - LeverEditedAt[i] > 1f) set.Levers[i].Value = frame.Levers[i];
        }

        private static void Refuse()
        {
            if (Time.unscaledTime - _refusedAt < 2f) return;
            _refusedAt = Time.unscaledTime;
            LanStorageGame.Notice(LanStorage.Describe(LanStorage.Locked));
        }
        private static void Dropped()
        {
            _log?.Invoke("Train control request not sent: earlier ones still wait for the host");
            if (Time.unscaledTime - _refusedAt < 2f) return;
            _refusedAt = Time.unscaledTime;
            LanStorageGame.Notice("Хост ещё не ответил; нажмите ещё раз");
        }
        private static bool GuestIndex(Component control, out int index)
        {
            index = -1;
            return Active && _guest.Index.TryGetValue(control, out index);
        }

        // 1.1.1: a cab button or lever the guest used in the host's world that did not become a request.
        private static void Unshared(Component control)
        {
            if (!GuestAttached || Time.unscaledTime - _unsharedLogAt < 5f) return;
            _unsharedLogAt = Time.unscaledTime;
            _log?.Invoke("Train control not shared: " + control.name + " (save isolation " + LanSaveIsolation.Active +
                ", cab copy " + (_guest != null) + ", listed " + (_guest != null && _guest.Index.ContainsKey(control)) + ")");
        }

        private static bool SwitchPrefix(HardwareButton __instance)
        {
            try
            {
                int index;
                // 1.4.3: a fuel station's lever or pump button in the host's world.
                if (LanFuelStation.GuestSwitch(__instance)) return false;
                // 1.4.4: a lever or button of the location in the host's world.
                if (LanSceneStates.GuestSwitch(__instance)) return false;
                if (!GuestIndex(__instance, out index)) { Unshared(__instance); return true; }
                if (__instance.defective) return false; // the game does nothing either
                if (__instance.Toggle && !FirstThisFrame(SwitchedFrame, (Component)__instance)) return false;
                if (!GuestAllowed) { Refuse(); return false; }
                if (GuestRequest == null || !GuestRequest(LanStorage.ControlButton, index, _guest.ButtonChecks[index], 0, null)) Dropped();
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Train button hook failed: " + ex.Message); return !Active; }
        }

        // The game takes the spare lever from the belt right after this call: it becomes the escrow.
        private static bool RepairPrefix(HardwareButton __instance)
        {
            try
            {
                int index;
                if (!GuestIndex(__instance, out index)) return true;
                var spare = BeltItem();
                if (spare == null || spare.itemID != LanStorage.LeverItem) return false;
                var escrow = LanStorage.Normalize(new object[] { spare.itemID, 1, spare.Save() });
                if (!GuestAllowed) { Refuse(); new LanStorageGuestWorld().Give(escrow); return false; }
                if (GuestRequest == null || !GuestRequest(LanStorage.ControlRepair, index, _guest.ButtonChecks[index], 0, escrow))
                { Dropped(); new LanStorageGuestWorld().Give(escrow); }
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Train repair hook failed: " + ex.Message); return !Active; }
        }
        private static InventoryItem BeltItem()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var controller = InventoryController.global;
            if (player == null || controller == null || player.beltInventory == null) return null;
            int slot = controller.selectedBeltIcon;
            if (slot < 0 || slot >= player.beltInventory.Length || player.beltInventory[slot] == null) return null;
            var content = player.beltInventory[slot].content;
            return content == null || content.Count == 0 ? null : content[0];
        }

        // The lever moves on the guest's copy at once; its value goes to the host (Tick).
        private static bool LeverPrefix(Lever __instance, float ChangeValue)
        {
            try
            {
                int index;
                if (!GuestIndex(__instance, out index)) { Unshared(__instance); return true; }
                if (!FirstThisFrame(ChangeValue >= 0f ? LeverUpFrame : LeverDownFrame, __instance)) return false;
                if (!GuestAllowed) { Refuse(); return false; }
                __instance.Value = Mathf.Clamp(__instance.Value + ChangeValue * Time.deltaTime, __instance.MinValue, __instance.MaxValue);
                LeverEditedAt[index] = Time.unscaledTime; LeverPending[index] = true;
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Train lever hook failed: " + ex.Message); return !Active; }
        }

        private static bool LightPrefix(LightSwitcher __instance)
        {
            try
            {
                int index;
                if (!GuestIndex(__instance, out index))
                {
                    // A lamp of a part the host built, shown by the guest's own native object.
                    int car, kind, identity;
                    if (!Active || !TrainLayout.TryIdentifyLight(__instance.transform, out car, out kind, out identity)) return true;
                    if (!GuestAllowed) { Refuse(); return false; }
                    if (GuestRequest == null || !GuestRequest(LanStorage.ControlPartLight, car, identity, kind, null)) Dropped();
                    return false;
                }
                if (!GuestAllowed) { Refuse(); return false; }
                if (GuestRequest == null || !GuestRequest(LanStorage.ControlCabLight, index, _guest.LightChecks[index], 0, null)) Dropped();
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Train light hook failed: " + ex.Message); return !Active; }
        }

        // Guest, every frame: lever values to the host (at most five a second).
        internal static void Tick()
        {
            var set = _guest;
            if (!Active || set == null || GuestRequest == null) return;
            if (!GuestAllowed)
            {
                Array.Clear(LeverPending, 0, LeverPending.Length);
                return;
            }
            float now = Time.unscaledTime;
            if (now < _nextLeverSend) return;
            for (int i = 0; i < set.Levers.Length; i++)
            {
                if (!LeverPending[i] || set.Levers[i] == null) continue;
                int value = Mathf.RoundToInt(Mathf.Clamp(set.Levers[i].Value, -1000f, 1000f) * 1000f);
                if (GuestRequest(LanStorage.ControlLever, i, set.LeverChecks[i], value, null)) { LeverPending[i] = false; LeverEditedAt[i] = now; }
                _nextLeverSend = now + 0.2f;
                return;
            }
        }

        // Direct native-control input path when the regular interaction controller
        // does not reach the cab. Uses the game's camera, mask, range and bound inputs.
        private static bool TickNativeLook(bool use)
        {
            var selection = SelectionManager.Global;
            var input = Zompiercer.Inputs.InputController.Global;
            var camera = GlobalManager.global == null ? null : GlobalManager.global.MainCamera;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (selection == null || input == null || camera == null || player == null || !player.controlEnabled || LanGuestDeath.Dead || (int)input.CurrentState != 1 ||
                (GuiRaycaster.global != null && GuiRaycaster.global.CursorOnGUI)) return false;
            Ray ray = camera.ScreenPointToRay(input.isKbMouseDevice ? input.MousePosition : new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, Mathf.Min(Reach, selection.MaxInteractionRange), selection.layerMask)) return false;
            int index;
            var button = hit.collider.GetComponentInParent<HardwareButton>();
            // Repairs retain native belt removal/escrow; momentary buttons retain
            // native hold/release, including releasing after the player looks away.
            if (button != null && button.Toggle && !button.defective && GuestIndex(button, out index))
            {
                var hint = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
                if (hint != null)
                {
                    hint.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey(button.functionName).ToUpper());
                    hint.SetIconText("Switch", (Zompiercer.Inputs.Actions)SwitchAction, (Zompiercer.Inputs.Actions)0, true);
                }
                if (use) SwitchPrefix(button);
                return true;
            }
            var lever = hit.collider.GetComponentInParent<Lever>();
            if (lever == null || !GuestIndex(lever, out index)) return false;
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            if (info != null)
            {
                info.SetText(Mathf.RoundToInt(lever.Value * 100f) + "%");
                info.SetIconText("Thrust", new[] { (Zompiercer.Inputs.Actions)11, (Zompiercer.Inputs.Actions)12 }, "+", (Zompiercer.Inputs.Actions)0, true);
            }
            if (input.isKbMouseDevice ? input.isLeverUpPressed : input.gp_LeverUpPressed) LeverPrefix(lever, 1f);
            if (input.isKbMouseDevice ? input.isLeverDownPressed : input.gp_LeverDownPressed) LeverPrefix(lever, -1f);
            return true;
        }

        // Guest (1.1.6): a door of the host's train in view: the game's own prompt, and the use key
        // asks the host. Doors need no driving permission.
        internal static bool TickDoor(bool use)
        {
            if (!LanSaveIsolation.Active || !GuestAttached) return false;
            RaycastHit hit;
            if (!LanTrainBuild.LookHit(out hit) || hit.distance > Reach) return false;
            int car, owner, identity, hash; bool open;
            if (!TrainLayout.TryIdentifyDoor(hit.collider, out car, out owner, out identity, out hash, out open)) return false;
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            if (info != null) info.SetIconText(open ? "Close" : "Open", (Zompiercer.Inputs.Actions)DoorAction, (Zompiercer.Inputs.Actions)0, true);
            if (!use) return true;
            if (GuestRequest == null || !GuestRequest(LanStorage.ControlDoor, car, identity, LanStorage.DoorValue(owner, !open, hash), null)) Dropped();
            return true;
        }
        private const int DoorAction = 5; // the action the game shows on its own doors
        // The host did it: the door moves at once on the guest's copy too.
        internal static void DoorDone(byte[] payload)
        {
            try
            {
                int kind, car, identity, value; object[] escrow;
                LanStorage.DecodeControl(payload, out kind, out car, out identity, out value, out escrow);
                if (kind != LanStorage.ControlDoor) return;
                int owner, hash; bool open;
                LanStorage.DoorFields(value, out owner, out open, out hash);
                TrainLayout.GuestSetDoor(car, owner, identity, hash, open);
            }
            catch (Exception ex) { _log?.Invoke("Train door not shown: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Guest: native cab controls or a light switch of a host-built part.
        internal static bool TickLook(bool use)
        {
            if (!Active || Failure != null) return false;
            if (TickNativeLook(use)) return true;
            RaycastHit hit;
            if (!LanTrainBuild.LookHit(out hit)) return false;
            int car, kind, identity;
            if (!TrainLayout.TryIdentifyLight(hit.collider, out car, out kind, out identity)) return false;
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            if (info != null)
            {
                info.SetText("СВЕТ");
                info.SetIconText("Switch", (Zompiercer.Inputs.Actions)SwitchAction, (Zompiercer.Inputs.Actions)0, true);
            }
            if (!use) return true;
            if (!GuestAllowed) { Refuse(); return true; }
            if (GuestRequest == null || !GuestRequest(LanStorage.ControlPartLight, car, identity, kind, null)) Dropped();
            return true;
        }
    }
}
