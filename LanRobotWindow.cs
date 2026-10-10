using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // The guest's robot dog in the game's own robot window (1.4.1). The window works on a robot
    // controller: the guest gets a stand-in, a copy of the robot prefab that is switched off right
    // after its Awake (no AI, never saved, never seen), holding the host dog's settings and showing its
    // devices and upgrades in its slot inventories. A setting changed in the window becomes a robot
    // request (storage request 15); an item dragged between the backpack and a slot opens that slot at
    // the host (robot slot target) and puts or takes it there, as the guest's storages do. The host's
    // next robot state refills the stand-in. A device's storage and medkit, which the game uses on the
    // dog itself, are buttons under the window, with the dog's battery and durability. Main thread only.
    internal static class LanRobotWindow
    {
        private const float InfoEvery = 1f, Reach = 4f, MoveTimeout = 10f;
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }
        private static AccessTools.FieldRef<RoboDogGUIController, RobotDogAiController> RobotOf;
        private static AccessTools.FieldRef<RoboDogGUIController, Zompiercer.Inputs.SettingElement> StatusOf, BehaviorOf, AttackOf, DoorsOf;

        private static LanStorageClient _client;
        private static int _dog;                     // host id of the dog shown (0: none)
        private static RobotDogAiController _standIn;
        private static LanStorage.RobotState _state;
        private static bool _syncing, _connected, _infoPending;
        private static float _infoAt, _openedAt;
        private static int _setsPending;
        // A move between the backpack and a slot: the slot is opened at the host, then put or taken.
        private sealed class Move { internal int Kind, Index, Amount; internal object[] Escrow; internal bool Sent; internal float Since; }
        private static Move _move;
        private static readonly Dictionary<Inventory, int[]> Slots = new Dictionary<Inventory, int[]>();
        private static GUIStyle _text;

        internal static bool Showing { get { return _dog != 0; } }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.robot-window");
            try
            {
                RobotOf = AccessTools.FieldRefAccess<RoboDogGUIController, RobotDogAiController>("robotDogAiController");
                StatusOf = AccessTools.FieldRefAccess<RoboDogGUIController, Zompiercer.Inputs.SettingElement>("elementStatus");
                BehaviorOf = AccessTools.FieldRefAccess<RoboDogGUIController, Zompiercer.Inputs.SettingElement>("elementBehavior");
                AttackOf = AccessTools.FieldRefAccess<RoboDogGUIController, Zompiercer.Inputs.SettingElement>("elementAttack");
                DoorsOf = AccessTools.FieldRefAccess<RoboDogGUIController, Zompiercer.Inputs.SettingElement>("elementOpenDoor");
                _harmony.Patch(AccessTools.Method(typeof(RoboDogGUIController), "RobotActivate"), prefix: new HarmonyMethod(typeof(LanRobotWindow), nameof(ActivatePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(RoboDogGUIController), "BehaviorMode"), prefix: new HarmonyMethod(typeof(LanRobotWindow), nameof(BehaviorPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(RoboDogGUIController), "AttackMode"), prefix: new HarmonyMethod(typeof(LanRobotWindow), nameof(AttackPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(RoboDogGUIController), "OpenDoorMode"), prefix: new HarmonyMethod(typeof(LanRobotWindow), nameof(DoorsPrefix)));
                LanStorageGame.ExtraMirror = IsSlot;
                LanStorageGame.ExtraPut = PutIntoSlot;
                LanStorageGame.ExtraTake = TakeFromSlot;
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                LanStorageGame.ExtraMirror = null; LanStorageGame.ExtraPut = null; LanStorageGame.ExtraTake = null;
                log("Game robot window unavailable for the guest (the mod's own window is used): " + Failure);
            }
        }
        internal static void Shutdown()
        {
            Close();
            _harmony?.UnpatchSelf(); _harmony = null;
            LanStorageGame.ExtraMirror = null; LanStorageGame.ExtraPut = null; LanStorageGame.ExtraTake = null;
        }
        internal static bool Available { get { return _harmony != null && Failure == null; } }

        private static void Once(string text) { if (_logged) return; _logged = true; _log?.Invoke(text); }

        // ---- Opening and closing ----

        // E on the guest's own dog: the host's state first, then the window.
        internal static void Open(int hostId, LanStorageClient client)
        {
            if (client == null || _dog != 0) return;
            _client = client; _dog = hostId; _connected = false; _state = null; _openedAt = Time.unscaledTime;
            LanStorageGame.Notice("Связь с хостом…");
            Info();
        }

        private static Zompiercer.GUI.GUIManager Gui() { return Zompiercer.GUI.GUIManager.Global; }
        private static bool WindowShown()
        {
            var gui = Gui();
            return gui != null && gui.RoboDogGUIControllerWindow != null && gui.RoboDogGUIControllerWindow.activeSelf;
        }

        private static void Connect()
        {
            var gui = Gui();
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var prefab = GlobalManager.global == null ? null : GlobalManager.global.ControlledRobotPrefab;
            if (gui == null || gui.roboDogGUIController == null || player == null || prefab == null || WindowShown()) { Close(); return; }
            var inventory = InventoryController.global;
            if (inventory != null && !inventory.IsAllInventoryClosed()) inventory.CloseAllInventoryWindows();
            GameObject copy = null;
            try
            {
                var staging = new GameObject("LAN robot window staging");
                staging.SetActive(false);
                try
                {
                    copy = Object.Instantiate(prefab.gameObject, staging.transform, false);
                    copy.name = "LAN robot window stand-in";
                    foreach (var agent in copy.GetComponentsInChildren<NavMeshAgent>(true)) agent.enabled = false;
                    foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                    foreach (var c in copy.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                    foreach (var a in copy.GetComponentsInChildren<AudioSource>(true)) { a.playOnAwake = false; a.enabled = false; }
                    foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
                    var robot = copy.GetComponent<RobotDogAiController>();
                    if (robot == null) throw new MissingComponentException("RobotDogAiController");
                    robot.robotActivated = false;
                    copy.AddComponent<LanRobotStandIn>();
                    copy.transform.SetParent(null, false);
                    copy.transform.position = player.transform.position + Vector3.down * 500f;
                }
                finally { Object.Destroy(staging); }
                // Awake (the devices find their slot inventories), then off: no Start, Update or save.
                copy.SetActive(true);
                copy.SetActive(false);
                _standIn = copy.GetComponent<RobotDogAiController>();
                Apply(_state, true);
                gui.roboDogGUIController.ConnectToObject(_standIn);
                if (!WindowShown()) throw new InvalidOperationException("the window did not open");
                _connected = true;
                LockPanels();
            }
            catch (Exception ex)
            {
                Once("Game robot window failed: " + ex.GetType().Name + ": " + ex.Message);
                if (copy != null) Object.Destroy(copy);
                _standIn = null; _dog = 0; _state = null;
                LanStorageGame.Notice("Окно робопса не открылось");
            }
        }

        internal static void Close()
        {
            if (_dog == 0) return;
            if (_connected && WindowShown() && RobotOf != null && Gui().roboDogGUIController != null && RobotOf(Gui().roboDogGUIController) == _standIn)
            {
                try { Gui().CloseRoboDogWindow(); } catch (Exception) { }
            }
            var move = _move; _move = null;
            if (move != null && !move.Sent && move.Escrow != null)
            {
                try { new LanStorageGuestWorld().Give(move.Escrow); }
                catch (Exception ex) { _log?.Invoke("Robot slot escrow not returned: " + ex.Message); }
            }
            if (move != null && _client != null) _client.Close();
            foreach (var slot in Slots.Keys) if (slot != null) Clear(slot);
            Slots.Clear();
            if (_standIn != null) Object.Destroy(_standIn.gameObject);
            _standIn = null; _dog = 0; _state = null; _connected = false; _infoPending = false; _setsPending = 0;
        }

        // ---- Every frame while the guest is attached ----

        internal static void Tick(LanStorageClient client)
        {
            if (_dog == 0) return;
            _client = client;
            float now = Time.unscaledTime;
            if (client == null) { Close(); return; }
            if (_connected && !WindowShown()) { Close(); return; }
            if (!_connected && _state == null && now - _openedAt > 6f) { LanStorageGame.Notice("Хост не ответил"); Close(); return; }
            var at = LanWorldItems.ShownPosition(_dog);
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (player == null || at == null || Vector3.Distance(at.Value, player.transform.position) > Reach + 1f) { Close(); return; }
            TickMove(client, now);
            if (_move == null && !_infoPending && _setsPending == 0 && now - _infoAt > InfoEvery) Info();
        }

        private static void Info()
        {
            var client = _client; int dog = _dog;
            if (client == null) return;
            _infoPending = true; _infoAt = Time.unscaledTime;
            if (!client.Takable(LanStorage.RobotInfoPayload(dog), (status, amount, answer) =>
                {
                    _infoPending = false;
                    if (dog != _dog) return;
                    Answered(status, answer);
                }))
                _infoPending = false;
        }

        private static void Answered(byte status, byte[] answer)
        {
            if (status != LanStorage.Ok)
            {
                if (status != LanStorage.Busy) LanStorageGame.Notice(LanStorage.Describe(status));
                if (status == LanStorage.NotYours || !_connected && status != LanStorage.Busy) Close();
                else Apply(_state, false);
                return;
            }
            try { _state = LanStorage.DecodeRobot(answer); }
            catch (System.IO.InvalidDataException ex) { Once("Robot state rejected: " + ex.Message); return; }
            if (!_connected) Connect(); else Apply(_state, false);
        }

        // The stand-in and the open window as the host's dog is.
        private static void Apply(LanStorage.RobotState s, bool fresh)
        {
            var robot = _standIn;
            if (s == null || robot == null) return;
            _syncing = true;
            try
            {
                robot.robotActivated = s.Active; robot.behaviorMode = s.Behavior; robot.attackMode = s.Attack; robot.openDoorMode = s.Doors;
                if (!fresh)
                {
                    var window = Gui() == null ? null : Gui().roboDogGUIController;
                    if (window != null)
                    {
                        // As ConnectToObject sets them (no change events).
                        StatusOf(window)?.SetIndexStringList(s.Active ? 1 : 0);
                        BehaviorOf(window)?.SetIndexStringList(s.Behavior);
                        AttackOf(window)?.SetIndexStringList(s.Attack);
                        DoorsOf(window)?.SetIndexStringList(s.Doors);
                    }
                }
                if (fresh) Slots.Clear();
                for (int i = 0; robot.Devices != null && i < robot.Devices.Count; i++)
                {
                    var slot = robot.Devices[i] == null ? null : robot.Devices[i].inventory;
                    if (slot == null) continue;
                    if (fresh) Slots[slot] = new[] { LanStorage.RobotSlotDevice, i };
                    if (_move == null) Fill(slot, s.Devices != null && i < s.Devices.Length ? s.Devices[i] : -1);
                }
                for (int i = 0; robot.InventoryUpgrades != null && i < robot.InventoryUpgrades.Count; i++)
                {
                    var slot = robot.InventoryUpgrades[i];
                    if (slot == null) continue;
                    if (fresh) Slots[slot] = new[] { LanStorage.RobotSlotUpgrade, i };
                    if (_move == null) Fill(slot, s.Upgrades != null && i < s.Upgrades.Length ? s.Upgrades[i] : -1);
                }
                if (!fresh) LockPanels();
            }
            catch (Exception ex) { Once("Robot window not refreshed: " + ex.GetType().Name + ": " + ex.Message); }
            finally { _syncing = false; }
        }

        // A device whose own storage holds something cannot be taken out (as the game's window shows it).
        private static void LockPanels()
        {
            var window = Gui() == null ? null : Gui().roboDogGUIController;
            if (window == null || window.LockPanels == null || _state == null || _state.Storage == null) return;
            for (int i = 0; i < window.LockPanels.Count && i < _state.Storage.Length; i++)
                if (window.LockPanels[i] != null) window.LockPanels[i].SetActive(_state.Storage[i]);
        }

        // A slot shows the host's item (one, as the game puts devices and upgrades).
        private static void Fill(Inventory slot, int id)
        {
            if (slot.content == null) slot.content = new List<InventoryItem>();
            if (slot.content.Count == (id >= 0 ? 1 : 0) && (id < 0 || slot.content[0] != null && slot.content[0].itemID == id)) return;
            Clear(slot);
            if (id >= 0)
            {
                InventoryItem item;
                try { item = LanStorageGame.Create(LanStorage.Normalize(new object[] { id, 1, new object[4] })); }
                catch (Exception ex) { Once("Robot slot item not shown: " + ex.GetType().Name + ": " + ex.Message); item = null; }
                if (item != null)
                {
                    item.gameObject.SetActive(false);
                    item.transform.SetParent(slot.transform, false);
                    item.inInventory = slot;
                    slot.content.Add(item);
                }
            }
            if (slot.connectedInventoryWindow != null) slot.connectedInventoryWindow.Regenerate();
        }
        private static void Clear(Inventory slot)
        {
            if (slot.content == null) return;
            foreach (var old in slot.content) if (old != null) Object.Destroy(old.gameObject);
            slot.content.Clear();
        }

        // ---- The window's settings ----

        private static bool Ours(RoboDogGUIController window)
        {
            return _standIn != null && window != null && RobotOf != null && RobotOf(window) == _standIn;
        }
        private static void Set(int field, int value)
        {
            var client = _client; int dog = _dog;
            if (client == null || dog == 0) return;
            _setsPending++;
            if (!client.Takable(LanStorage.RobotSetPayload(dog, field, value), (status, amount, answer) =>
                {
                    _setsPending = Math.Max(0, _setsPending - 1);
                    if (dog != _dog) return;
                    Answered(status, answer);
                }))
            {
                _setsPending = Math.Max(0, _setsPending - 1);
                LanStorageGame.Notice("Хост ещё отвечает на прошлые запросы");
                Apply(_state, false);
            }
        }
        // On or off: the host switches its dog; the stand-in only shows it.
        private static bool ActivatePrefix(RoboDogGUIController __instance, int volume)
        {
            if (!Ours(__instance)) return true;
            if (!_syncing)
            {
                Set(LanStorage.RobotActive, volume == 1 ? 1 : 0);
                try { GlobalSoundEffects.global.RobotModeSelection(); } catch (Exception) { }
            }
            return false;
        }
        // The game sets the stand-in's mode and its description; the host sets its dog's.
        private static void BehaviorPrefix(RoboDogGUIController __instance, int volume) { if (Ours(__instance) && !_syncing) Set(LanStorage.RobotBehavior, volume); }
        private static void AttackPrefix(RoboDogGUIController __instance, int volume) { if (Ours(__instance) && !_syncing) Set(LanStorage.RobotAttack, volume); }
        private static void DoorsPrefix(RoboDogGUIController __instance, int volume) { if (Ours(__instance) && !_syncing) Set(LanStorage.RobotDoors, volume); }

        // ---- Devices and upgrades: moves between the backpack and a slot ----

        private static bool IsSlot(Inventory inventory) { return _dog != 0 && Slots.ContainsKey(inventory); }

        private static void PutIntoSlot(Inventory slot, InventoryItem item, int amount)
        {
            int[] where;
            if (_client == null || !Slots.TryGetValue(slot, out where)) return;
            if (_move != null) { LanStorageGame.Notice("Подождите: хост ещё меняет устройства"); return; }
            if (slot.content != null && slot.content.Count > 0) { LanStorageGame.Notice("Сначала снимите то, что стоит в этой ячейке"); return; }
            object[] escrow = null;
            try { escrow = LanStorageGame.EscrowFromBackpack(item, 1); }
            catch (Exception ex) { Once("Robot slot put not started: " + ex.GetType().Name + ": " + ex.Message); }
            if (escrow == null) { LanStorageGame.Notice("Положите предмет в рюкзак, потом в ячейку"); return; }
            _move = new Move { Kind = where[0], Index = where[1], Escrow = escrow, Since = Time.unscaledTime };
            _client.Open(LanStorage.RobotPayload(_dog, where[0], where[1]));
        }
        private static void TakeFromSlot(Inventory slot, InventoryItem item, int amount)
        {
            int[] where;
            if (_client == null || !Slots.TryGetValue(slot, out where)) return;
            if (_move != null) { LanStorageGame.Notice("Подождите: хост ещё меняет устройства"); return; }
            if (where[0] == LanStorage.RobotSlotDevice && _state != null && _state.Storage != null && where[1] < _state.Storage.Length && _state.Storage[where[1]])
            { LanStorageGame.Notice(LanStorage.Describe(LanStorage.DeviceInUse)); return; }
            _move = new Move { Kind = where[0], Index = where[1], Amount = Math.Max(1, amount), Since = Time.unscaledTime };
            _client.Open(LanStorage.RobotPayload(_dog, where[0], where[1]));
        }

        // The slot open at the host: the put or take, then closed and the dog asked again.
        private static void TickMove(LanStorageClient client, float now)
        {
            var move = _move;
            if (move == null) return;
            if (!move.Sent)
            {
                if (client.IsOpen && client.Current != null && !client.Busy)
                {
                    bool sent = move.Escrow != null ? client.Put(move.Escrow) : client.Current.Items.Length > 0;
                    if (move.Escrow == null && sent) client.Take(0, move.Amount);
                    if (!sent)
                    {
                        if (move.Escrow != null) GiveBack(move.Escrow);
                        if (move.Escrow == null) LanStorageGame.Notice("В этой ячейке у хоста ничего нет");
                        Finish(client); return;
                    }
                    move.Sent = true;
                }
                else if (!client.IsOpen && !client.Busy || now - move.Since > MoveTimeout)
                {
                    // The host refused the slot (its reason is shown from the client's message) or did not answer.
                    if (move.Escrow != null) GiveBack(move.Escrow);
                    Finish(client);
                }
                return;
            }
            if (!client.Busy) Finish(client);
        }
        private static void GiveBack(object[] escrow)
        {
            try { new LanStorageGuestWorld().Give(escrow); }
            catch (Exception ex) { _log?.Invoke("Robot slot escrow not returned: " + ex.Message); }
        }
        private static void Finish(LanStorageClient client)
        {
            _move = null;
            client.Close();
            _infoAt = -100f; // the host's dog as it is now
        }

        // ---- OnGUI: under the game's window, what the game uses on the dog itself ----

        internal static void Draw()
        {
            if (_dog == 0 || !_connected || _state == null || !WindowShown()) return;
            var s = _state;
            if (_text == null) _text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14 };
            int buttons = 0;
            for (int i = 0; s.Devices != null && i < s.Devices.Length; i++)
            {
                if (s.Storage != null && i < s.Storage.Length && s.Storage[i]) buttons++;
                if (s.Medkit != null && i < s.Medkit.Length && s.Medkit[i] >= 0) buttons++;
            }
            float width = Math.Max(320f, 150f * buttons + 20f), height = buttons > 0 ? 78f : 40f;
            var box = new Rect((Screen.width - width) / 2f, Screen.height - height - 12f, width, height);
            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x, box.y + 6f, box.width, 24f), (s.Broken ? "Сломан · " : "") + "Заряд " + s.Battery + "% · прочность " + s.Durability + "%", _text);
            float x = box.x + 10f;
            for (int i = 0; s.Devices != null && i < s.Devices.Length; i++)
            {
                if (s.Storage != null && i < s.Storage.Length && s.Storage[i] && GUI.Button(new Rect(x, box.y + 36f, 140f, 32f), "Хранилище " + (i + 1)))
                { int dog = _dog, slot = i; var client = _client; Close(); LanStorageGame.OpenWindow(client, LanStorage.RobotPayload(dog, LanStorage.RobotSlotStorage, slot)); return; }
                if (s.Storage != null && i < s.Storage.Length && s.Storage[i]) x += 150f;
                if (s.Medkit != null && i < s.Medkit.Length && s.Medkit[i] >= 0)
                {
                    GUI.enabled = s.Medkit[i] == 1 && _setsPending == 0;
                    if (GUI.Button(new Rect(x, box.y + 36f, 140f, 32f), s.Medkit[i] == 1 ? "Аптечка " + (i + 1) : "Аптечка…")) LanTakables.UseMedkit(_dog, i, _client, () => _infoAt = -100f);
                    GUI.enabled = true;
                    x += 150f;
                }
            }
        }
    }

    // The guest's stand-in dog of the game's robot window (1.4.1).
    internal sealed class LanRobotStandIn : MonoBehaviour { }
}
