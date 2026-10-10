using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Zompiercer.Train;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Game side of shared storages (LanStorage). Main thread only.
    //  * Host: finds the container the guest points at, checks that the guest stands
    //    next to it and moves items with the game's own Inventory methods.
    //  * Guest: never opens its own copy of a loot container while it plays as a
    //    client (that copy holds the guest's locally generated loot, which must not
    //    reach the profile stored by the host); E on a container or a train storage
    //    opens the host's container in the game's own inventory windows instead.
    internal static class LanStorageGame
    {
        internal const float Reach = 4f;          // metres from the container's colliders
        internal const float TrainRay = 3f;       // guest's look distance for train storages
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }
        // Guest: handles E on a scene loot inventory; null outside a usable session.
        internal static Action<Inventory> GuestOpenScene;
        internal static bool WindowOpen { get; private set; }
        private static float _closedAt = -10f, _openedAt = -10f;
        // The key that closed the window must not reopen it in the next frames.
        internal static bool RecentlyClosed { get { return Time.unscaledTime - _closedAt < 0.35f; } }
        private static string _notice;
        private static float _noticeUntil;

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.storage");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "Update"),
                    prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(InventoryUpdatePrefix)));
                // The host's container is shown in the game's own windows: every move
                // into or out of the mirror becomes a request to the host instead.
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "TransferTo"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(TransferPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(DragItemMenu), "Confirm"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(ConfirmPrefix)));
                var guardBool = new HarmonyMethod(typeof(LanStorageGame), nameof(MirrorGuardBool));
                var guardVoid = new HarmonyMethod(typeof(LanStorageGame), nameof(MirrorGuardVoid));
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Add", new[] { typeof(InventoryItem), typeof(bool) }), prefix: guardBool);
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Add", new[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }), prefix: guardBool);
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Extract", new[] { typeof(InventoryItem), typeof(int) }), prefix: guardVoid);
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Extract", new[] { typeof(int), typeof(int) }), prefix: guardVoid);
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "ExtractUnsafe"), prefix: guardVoid);
                _harmony.Patch(AccessTools.Method(typeof(Inventory), "Remove"), prefix: guardVoid);
                var use = new HarmonyMethod(typeof(LanStorageGame), nameof(MirrorUsePrefix));
                foreach (var name in new[] { "TryUseFoodOrWater", "GetResourcesFromItem", "DisassemblePlasticBottle", "TryToEquipArmor" })
                    _harmony.Patch(AccessTools.Method(typeof(InventoryController), name), prefix: use);
            }
            catch (Exception ex)
            {
                // Without the hook a guest could loot its local copies: guests are refused.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Shared storage hook unavailable: " + Failure);
                return;
            }
            // Optional (guest manual crafting): without it the guest's manual work is refused as before,
            // and a changed Workbench.Update does not take the shared storages down.
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Workbench), "Update"),
                    transpiler: new HarmonyMethod(typeof(LanStorageGame), nameof(ReplaceWorkbenchWindow)));
                if (!ManualWorkHooked) log("Guest manual crafting unavailable: Workbench.Update has changed");
            }
            catch (Exception ex) { ManualWorkHooked = false; log("Guest manual crafting unavailable: " + ex.GetType().Name + ": " + ex.Message); }
            // 1.1.5: the guest crafts in the game's own craft window on a stand-in workbench.
            // Without every hook the stand-in would take ingredients for a local queue: no window then.
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Workbench), "AddInQueue"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(AddInQueuePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Workbench), "ReturnResourcesFromQueue"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(ReturnResourcesPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Workbench), "RemoveFromQueue"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(RemoveFromQueuePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Workbench), "StopCurrentCraftProcess"), prefix: new HarmonyMethod(typeof(LanStorageGame), nameof(StopCraftPrefix)));
                CraftHooked = true;
            }
            catch (Exception ex) { CraftHooked = false; log("Guest craft window unavailable: " + ex.GetType().Name + ": " + ex.Message); }
        }
        // True when the stand-in workbench's queue methods are requests (the guest's craft window).
        internal static bool CraftHooked { get; private set; }
        internal static Func<Workbench, bool> GuestWorking;
        // True once Workbench.Update reads the window flag through WorkbenchWindowOpen.
        internal static bool ManualWorkHooked { get; private set; }

        // Read the host's real flag without changing it. The guest can also supply
        // manual work, but only through the currently open, in-range host view.
        internal static bool WorkbenchWindowOpen(Workbench bench)
        {
            if (bench.WindowOpened) return true;
            var working = GuestWorking;
            if (working == null) return false;
            try { return working(bench); }
            catch (Exception) { return false; } // never break the game's own workbench
        }

        // Exactly one read of WindowOpened is replaced; any other shape leaves the method as it is.
        internal static IEnumerable<CodeInstruction> ReplaceWorkbenchWindow(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(Workbench), "WindowOpened");
            var replacement = AccessTools.Method(typeof(LanStorageGame), nameof(WorkbenchWindowOpen));
            var list = new List<CodeInstruction>(instructions);
            int found = -1, count = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].opcode == OpCodes.Ldfld && Equals(list[i].operand, field)) { found = i; count++; }
            ManualWorkHooked = false;
            if (field == null || replacement == null || count != 1) return list;
            list[found].opcode = OpCodes.Call; list[found].operand = replacement;
            ManualWorkHooked = true;
            return list;
        }

        internal static void Shutdown() { GuestWorking = null; ManualWorkHooked = false; CraftHooked = false; _harmony?.UnpatchSelf(); _harmony = null; GuestOpenScene = null; WindowOpen = false; DestroyMirror(); _closing = null; }

        // The game's own red warning next to the crosshair (about three seconds);
        // a plain box only when the game's controller is not there.
        internal static void Log(string text) { _log?.Invoke(text); }

        internal static void Notice(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var controller = InventoryController.global;
            if (controller != null)
            {
                controller.warningMessageText = text.ToUpper(); controller.warningMessage = true; controller.warningMessageTimer = -2f;
                _noticeUntil = 0f; return;
            }
            _notice = text; _noticeUntil = Time.unscaledTime + 4f;
        }

        // The game's own look-at hints, as on its own loose items and chests.
        internal static void HintPickUp(int itemId, int amount)
        {
            var info = Hint();
            if (info == null) return;
            info.SetText(Name(itemId).ToUpper() + (amount > 1 ? " - " + amount : ""));
            info.SetIconText("PickUp", (Zompiercer.Inputs.Actions)49, (Zompiercer.Inputs.Actions)0, true);
        }
        internal static void HintOpen(string name)
        {
            var info = Hint();
            if (info == null) return;
            if (!string.IsNullOrEmpty(name)) info.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey(name).ToUpper());
            info.SetIconText("Open", (Zompiercer.Inputs.Actions)5, (Zompiercer.Inputs.Actions)0, true);
        }
        private static Zompiercer.GUI.SimpleInfoDisplayer Hint()
        {
            var controller = InventoryController.global; var selection = SelectionManager.Global;
            if (controller == null || controller.simpleInfoDisplayer == null) return null;
            // Something of the guest's own world under the crosshair has its own hint.
            if (selection != null && selection.HoveredObject != null) return null;
            return controller.simpleInfoDisplayer;
        }
        private static string _bagName;
        internal static string BagName()
        {
            if (_bagName != null) return _bagName;
            try
            {
                var loot = GlobalObjectLootController.global;
                var bag = loot == null || loot.PrefabZombieLoot == null ? null : loot.PrefabZombieLoot.GetComponentInChildren<Inventory>(true);
                _bagName = bag == null || bag.Name == null ? "" : bag.Name;
            }
            catch (Exception) { _bagName = ""; }
            return _bagName;
        }

        // Native E on a hovered inventory. A client never reaches its local loot copies.
        private static void InventoryUpdatePrefix(InventoryController __instance)
        {
            if (!LanSaveIsolation.Active) return;
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                // The E that opened the host's container must not close it again at once.
                if (input != null && WindowOpen && Time.unscaledTime - _openedAt < 0.25f) { input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false; }
                var selection = SelectionManager.Global;
                if (input == null || selection == null || selection.HoveredObject == null || __instance.draggedIcon != null) return;
                bool use = input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown;
                if (!use || (int)input.CurrentState != 1) return;
                var inventory = selection.HoveredObject.GetComponent<Inventory>();
                if (inventory == null || !inventory.IsLootInventory || inventory.IsBackpack) return;
                input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false;
                if (WindowOpen || RecentlyClosed) return;
                var open = GuestOpenScene;
                if (open == null) Notice("Хранилища открываются только у хоста, во время игры вместе");
                else open(inventory);
            }
            catch (Exception ex)
            {
                if (!_logged) { _logged = true; _log?.Invoke("Shared storage input hook failed: " + ex); }
            }
        }

        // ---- Guest: targets ----
        internal static byte[] SceneTarget(Inventory inventory)
        {
            var scenes = GlobalSceneManager.global;
            string name = inventory.Name ?? "";
            if (scenes == null || string.IsNullOrEmpty(scenes.CurrentSceneName) || name.Length == 0 || name.Length > LanStorage.MaxName) return null;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            if (train != null && inventory.transform.IsChildOf(train.transform))
            {
                foreach (var collider in inventory.GetComponentsInChildren<Collider>(true))
                {
                    int car, kind, identity;
                    if (TrainLayout.TryIdentifyStorage(collider, out car, out kind, out identity)) return LanStorage.TrainPayload(car, kind, identity);
                }
                return null;
            }
            var p = inventory.transform.position;
            return LanStorage.ScenePayload(scenes.CurrentSceneName, p.x, p.y, p.z, name);
        }

        // The train storage in front of the guest's camera (nearest solid hit only).
        internal static byte[] TrainTargetInView() { Workbench bench; return TrainTargetInView(out bench); }
        internal static byte[] TrainTargetInView(out Workbench bench)
        {
            bench = null;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var camera = Camera.main;
            if (player == null || camera == null) return null;
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit best = default(RaycastHit);
            bool any = false;
            foreach (var hit in Physics.RaycastAll(ray, TrainRay, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(player.transform)) continue;
                if (!any || hit.distance < best.distance) { best = hit; any = true; }
            }
            int car, kind, identity;
            if (!any || !TrainLayout.TryIdentifyStorage(best.collider, out car, out kind, out identity)) return null;
            bench = TrainLayout.WorkbenchOf(best.collider);
            return LanStorage.TrainPayload(car, kind, identity);
        }

        // ---- Guest: the host's container in the game's own windows ----
        // The guest gets a local mirror: an Inventory under its character (world saves
        // skip inventories of the character) filled with copies of the host's items.
        // The native windows show it like any chest. Moves into or out of it never
        // touch it: they become requests, and the mirror is refilled from the host's
        // next view. Nothing in the mirror ever reaches the guest's belongings.
        private static byte[] _target;
        private static Inventory _mirror;
        // At a host workbench (1.1.5) the game's craft window works on a stand-in Workbench next
        // to the mirror: the host workbench's recipes, a disabled Update, and its queue methods
        // turned into requests. The host's queue and progress come with each view.
        private static Workbench _craftBench;
        private static LanStorage.View _shown;
        private static readonly Dictionary<InventoryItem, int> MirrorIndex = new Dictionary<InventoryItem, int>();
        private static LanStorageClient _client, _closing;
        private static int _filling;
        private static bool _dirty, _fillLogged;
        private static string _lastMessage;

        internal static void OpenWindow(LanStorageClient client, byte[] target, Workbench bench = null, Inventory local = null)
        {
            if (target == null) { Notice("Это хранилище нельзя открыть по сети"); return; }
            CloseWindow(client);
            var controller = InventoryController.global;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (controller == null || player == null || player.inventory == null) return;
            if (!controller.IsAllInventoryClosed()) controller.CloseAllInventoryWindows();
            bool recipes = bench != null && bench.AvailableRecipes != null && bench.AvailableRecipes.Count > 0;
            bool crafting = recipes && CraftHooked && GlobalCraftController.global != null && Zompiercer.GUI.GUIManager.Global != null;
            _mirror = CreateMirror(player, local, crafting ? bench : null);
            _craftBench = crafting ? _mirror.GetComponent<Workbench>() : null;
            ResetCraftState();
            if (!(crafting ? ShowCraft(controller, player) : ShowNative(controller, player)))
            {
                CloseNative();
                DestroyMirror(); Notice("Окно хранилища не открылось"); return;
            }
            if (recipes && !crafting) Notice("Крафт у верстака хоста недоступен в этой версии игры");
            _target = target;
            _client = client; _closing = null; _shown = null; _dirty = true;
            client.Open(target);
            WindowOpen = true; _openedAt = Time.unscaledTime;
            var input = Zompiercer.Inputs.InputController.Global;
            if (input != null) { input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false; }
        }

        private static Inventory CreateMirror(ZombieFighterController player, Inventory local, Workbench bench)
        {
            var holder = new GameObject("ZompiercerLAN host storage");
            holder.SetActive(false);
            holder.transform.SetParent(player.transform, false);
            var mirror = holder.AddComponent<Inventory>();
            mirror.Name = local != null && local.Name != null ? local.Name : "";
            if (mirror.content == null) mirror.content = new List<InventoryItem>();
            if (mirror.onlyForItems == null) mirror.onlyForItems = new List<int>();
            if (mirror.OnContentChanged == null) mirror.OnContentChanged = new UnityEngine.Events.UnityEvent();
            mirror.maxWeight = local != null && local.maxWeight > 0f ? local.maxWeight : 100000f;
            Workbench craft = null;
            if (bench != null)
            {
                // Workbench.Awake reads the AudioSource (open, close and done sounds) and the
                // Inventory of its own object: both are here before it runs.
                var sound = holder.AddComponent<AudioSource>();
                sound.playOnAwake = false;
                var source = bench.GetComponent<AudioSource>();
                if (source != null) { sound.volume = source.volume; sound.spatialBlend = 0f; }
                craft = holder.AddComponent<Workbench>();
                craft.enabled = false; // the host crafts; this one never runs its Update
            }
            holder.SetActive(true);
            if (craft != null)
            {
                craft.Name = bench.Name;
                craft.AvailableRecipes = new List<InventoryItem>(bench.AvailableRecipes);
                craft.NeedManualWork = bench.NeedManualWork;
                craft.Queue = new List<Workbench.QueueItem>();
                craft.workingOn = null; craft.timeToCompletion = 0f;
                craft.inventory = mirror;
                craft.audioSource = holder.GetComponent<AudioSource>();
                craft.openWorkench = bench.openWorkench; craft.closeWorkench = bench.closeWorkench; craft.AudioOnCraftComplete = bench.AudioOnCraftComplete;
            }
            return mirror;
        }

        // The same windows and places as the game's own E on a workbench
        // (InventoryController.OpenWorkbench): craft window, workbench right, backpack left.
        private static bool ShowCraft(InventoryController controller, ZombieFighterController player)
        {
            var gui = Zompiercer.GUI.GUIManager.Global; var craft = GlobalCraftController.global; var bench = _craftBench;
            if (bench == null || gui.craftWindow == null || gui.craftWindow.activeSelf) return false;
            try
            {
                if (gui.titleTableText != null) gui.titleTableText.text = Zompiercer.GUI.LocalizationCore.GetTextByKey(bench.Name).ToUpper();
                craft.connectedTo = bench;
                bench.WindowOpened = true;
                gui.OpenCloseCraftWindow();
                craft.RegenerateQueue();
                var window = controller.OpenInventoryWindow(_mirror);
                if (window == null) return false;
                Place(window, 630f);
                var bag = controller.OpenInventoryWindow(player.inventory);
                if (bag == null) return false;
                Place(bag, -630f);
                var opened = controller.OpenedInventoryWindows;
                if (opened != null && opened.Length > 1) { opened[1] = window; opened[0] = bag; }
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Host workbench window failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
        private static void Place(InventoryWindow window, float x)
        {
            var rect = window.GetComponent<RectTransform>();
            if (rect != null) rect.anchoredPosition = new Vector2(x, -10f);
            if (window.CloseButton != null) window.CloseButton.SetActive(false);
        }

        // The same windows and places as the game's own E on a chest.
        private static bool ShowNative(InventoryController controller, ZombieFighterController player)
        {
            var bag = controller.OpenInventoryWindow(player.inventory);
            if (bag == null) return false;
            if (controller.positionForInventoryA != null) bag.transform.localPosition = controller.positionForInventoryA.localPosition;
            var window = controller.OpenInventoryWindow(_mirror);
            if (window == null) { controller.CloseAllInventoryWindows(); return false; }
            if (controller.positionForInventoryB != null) window.transform.localPosition = controller.positionForInventoryB.localPosition;
            var opened = controller.OpenedInventoryWindows;
            if (opened != null && opened.Length > 1) { opened[0] = bag; opened[1] = window; }
            controller.openedInventoryCell = _mirror;
            return true;
        }

        // Closes the windows and the host's container at once.
        internal static void CloseWindow(LanStorageClient client)
        {
            if (_closing != null) { _closing.Close(); _closing = null; }
            if (!WindowOpen) return;
            WindowOpen = false; _closedAt = Time.unscaledTime;
            client?.Close();
            CloseNative();
            DestroyMirror();
        }

        // Guest, every frame: follow the native windows and the host's view.
        internal static void TickWindow(LanStorageClient client)
        {
            // The host's answers (refusals, "backpack full") as the game's own warnings.
            string message = client.Message;
            if (message != _lastMessage) { _lastMessage = message; if (message != "Открытие…") Notice(message); }
            // Closed by the player: requests already made (take all) still finish.
            if (_closing != null && (_closing != client || !client.Busy || Time.unscaledTime - _closedAt > 8f))
            { if (_closing == client) client.Close(); _closing = null; }
            if (!WindowOpen) return;
            if (_mirror == null || _mirror.connectedInventoryWindow == null || CraftWindowClosed())
            {
                WindowOpen = false; _closedAt = Time.unscaledTime;
                CloseNative(); // whatever of the windows is still open (drag, craft window, inventories)
                DestroyMirror();
                if (client.Busy) _closing = client; else client.Close();
                return;
            }
            if (!client.IsOpen && client.Current == null)
            {
                // Refused or ended by the host: its reason stays on screen.
                string reason = client.Message;
                CloseWindow(client);
                if (!string.IsNullOrEmpty(reason)) Notice(reason);
                return;
            }
            if ((_dirty || client.Current != _shown) && !Dragging()) Fill(client.Current);
            if (!ReferenceEquals(_craftBench, null)) ShowCraftState(client.Current);
        }

        private static void Fill(LanStorage.View view)
        {
            var mirror = _mirror;
            _filling++;
            try
            {
                foreach (var old in mirror.content) if (old != null) Object.Destroy(old.gameObject);
                mirror.content.Clear(); MirrorIndex.Clear();
                if (view != null)
                {
                    for (int i = 0; i < view.Items.Length; i++)
                    {
                        InventoryItem item;
                        try { item = Create((object[])view.Items[i]); }
                        catch (Exception ex)
                        {
                            if (!_fillLogged) { _fillLogged = true; _log?.Invoke("Host storage item not shown: " + ex.GetType().Name + ": " + ex.Message); }
                            continue;
                        }
                        // What Inventory.Add does, without stack merging: one entry per host entry.
                        item.gameObject.SetActive(false);
                        item.transform.SetParent(mirror.transform, false);
                        item.inInventory = mirror;
                        mirror.content.Add(item);
                        MirrorIndex[item] = i;
                    }
                    mirror.Name = view.Name ?? "";
                    if (view.MaxWeight > 0f) mirror.maxWeight = view.MaxWeight;
                    mirror.currentWeight = view.Weight;
                }
                var window = mirror.connectedInventoryWindow;
                if (window != null)
                {
                    if (window.title != null && mirror.Name.Length > 0)
                        window.title.text = Zompiercer.GUI.LocalizationCore.GetTextByKey(mirror.Name).ToUpper();
                    window.Regenerate();
                }
            }
            finally { _filling--; }
            _shown = view; _dirty = false;
        }

        private static void CloseNative()
        {
            var mirror = _mirror;
            var controller = InventoryController.global;
            if (mirror == null || controller == null) return;
            StopMirrorDrag();
            if (!CloseCraftWindow() && mirror.connectedInventoryWindow != null) controller.CloseAllInventoryWindows();
            if (controller.openedInventoryCell == mirror) controller.openedInventoryCell = null;
        }

        // The game's craft window on the stand-in, closed the game's way (windows, input state, sound).
        private static bool CloseCraftWindow()
        {
            var bench = _craftBench; var craft = GlobalCraftController.global;
            if (ReferenceEquals(bench, null) || craft == null || !ReferenceEquals(craft.connectedTo, bench)) return false;
            try { Zompiercer.GUI.GUIManager.Global.CloseCraftWindow(); }
            catch (Exception ex) { _log?.Invoke("Craft window not closed: " + ex.GetType().Name + ": " + ex.Message); }
            if (ReferenceEquals(craft.connectedTo, bench)) craft.connectedTo = null;
            return true;
        }
        private static bool CraftWindowClosed()
        {
            if (ReferenceEquals(_craftBench, null)) return false;
            var craft = GlobalCraftController.global;
            return craft == null || !ReferenceEquals(craft.connectedTo, _craftBench);
        }

        private static void DestroyMirror()
        {
            CloseCraftWindow();
            var mirror = _mirror;
            _mirror = null; _craftBench = null; _shown = null; _client = null; MirrorIndex.Clear(); ResetCraftState();
            if (mirror == null) return;
            foreach (var item in mirror.content) if (item != null) Object.Destroy(item.gameObject);
            mirror.content.Clear();
            Object.Destroy(mirror.gameObject);
        }

        // A dragged icon or an open amount dialog still points at a mirror item.
        private static bool Dragging()
        {
            var controller = InventoryController.global;
            if (controller != null && controller.draggedIcon != null) return true;
            var menu = DragMenu();
            return menu != null && (menu.from == _mirror || menu.to == _mirror);
        }
        private static DragItemMenu DragMenu()
        {
            var core = Object.FindObjectOfType<Zompiercer.GUI.GuiCore>();
            var menu = core == null ? null : core.dragItemMenu;
            return menu != null && menu.isActiveAndEnabled ? menu : null;
        }
        private static void StopMirrorDrag()
        {
            var mirror = _mirror;
            if (mirror == null) return;
            try
            {
                var controller = InventoryController.global;
                var dragged = controller == null || controller.draggedIcon == null ? null : controller.draggedIcon.connectedInventoryItem;
                if (dragged != null && dragged.inInventory == mirror) controller.StopDrag();
                var menu = DragMenu();
                if (menu != null && (menu.from == mirror || menu.to == mirror)) menu.Close();
            }
            catch (Exception ex) { _log?.Invoke("Shared storage drag not stopped: " + ex.Message); }
        }

        // ---- Guest: native moves become requests ----
        private static bool Mirror(Inventory inventory) { return !ReferenceEquals(_mirror, null) && _filling == 0 && inventory == _mirror; }

        // 1.4.1: other host-backed inventories shown in the game's windows (the device and upgrade slots
        // of the guest's robot dog window, LanRobotWindow): moves into or out of them become requests too.
        internal static Func<Inventory, bool> ExtraMirror;
        internal static Action<Inventory, InventoryItem, int> ExtraPut, ExtraTake;
        private static bool Extra(Inventory inventory)
        {
            var extra = ExtraMirror;
            if (extra == null || ReferenceEquals(inventory, null)) return false;
            try { return extra(inventory); }
            catch (Exception) { return false; }
        }
        // A move between an extra mirror and anything else; false when neither side is one.
        private static bool ExtraMove(Inventory from, Inventory to, InventoryItem item, int amount)
        {
            bool source = Extra(from), target = Extra(to);
            if (!source && !target) return false;
            try
            {
                if (source && target) Notice("Сначала возьмите предмет в рюкзак");
                else if (source) ExtraTake?.Invoke(from, item, amount);
                else ExtraPut?.Invoke(to, item, amount);
            }
            catch (Exception ex) { _log?.Invoke("Robot slot move failed: " + ex.GetType().Name + ": " + ex.Message); }
            return true;
        }
        // The backpack item as an escrowed entry (removed from the backpack), or null.
        internal static object[] EscrowFromBackpack(InventoryItem item, int amount) { return Escrow(item, amount); }

        // Drag and drop, quick transfer and take all.
        private static bool TransferPrefix(Inventory __instance, InventoryItem newItem, Inventory to)
        {
            if (newItem != null && ExtraMove(__instance, to, newItem, newItem.amount)) return false;
            if (ReferenceEquals(_mirror, null) || _filling > 0 || newItem == null) return true;
            bool from = __instance == _mirror, into = to == _mirror;
            if (!from && !into) return true;
            try
            {
                if (from && !into) TakeFromMirror(newItem, newItem.amount);
                else if (into && !from) PutIntoMirror(newItem, newItem.amount);
            }
            catch (Exception ex) { _log?.Invoke("Shared storage move failed: " + ex); }
            return false;
        }

        // The amount dialog of a split stack.
        private static bool ConfirmPrefix(DragItemMenu __instance)
        {
            if (Extra(__instance.from) || Extra(__instance.to))
            {
                var dragged = __instance.dragbleItem;
                if (InventoryController.global != null) InventoryController.global.FrameWhenAmountTransferDone = Time.frameCount;
                int count = dragged == null ? 0 : Math.Min(__instance.transferAmount, dragged.amount);
                if (count > 0) ExtraMove(__instance.from, __instance.to, dragged, count);
                __instance.Close();
                return false;
            }
            if (ReferenceEquals(_mirror, null) || _filling > 0) return true;
            bool from = __instance.from == _mirror, into = __instance.to == _mirror;
            if (!from && !into) return true;
            try
            {
                var item = __instance.dragbleItem;
                if (InventoryController.global != null) InventoryController.global.FrameWhenAmountTransferDone = Time.frameCount;
                int amount = item == null ? 0 : Math.Min(__instance.transferAmount, item.amount);
                if (into && __instance.to != null && item != null) amount = Math.Min(amount, __instance.to.HowMuchCanFit(item));
                if (amount > 0 && from != into)
                {
                    if (from) TakeFromMirror(item, amount); else PutIntoMirror(item, amount);
                    if (GlobalSoundEffects.global != null) GlobalSoundEffects.global.PickUpItem();
                }
            }
            catch (Exception ex) { _log?.Invoke("Shared storage split failed: " + ex); }
            __instance.Close();
            return false;
        }

        // Anything else that would change the mirror (placing an item from it in the
        // world, scripts) is refused; the next frame shows the host's view again.
        private static bool MirrorGuardBool(Inventory __instance, ref bool __result)
        {
            if (!Mirror(__instance) && !Extra(__instance)) return true;
            __result = false; _dirty = true;
            return false;
        }
        private static bool MirrorGuardVoid(Inventory __instance)
        {
            if (!Mirror(__instance) && !Extra(__instance)) return true;
            _dirty = true;
            return false;
        }
        // Eating, disassembling or wearing straight from the host's container.
        private static bool MirrorUsePrefix(object[] __args)
        {
            var item = __args == null || __args.Length == 0 ? null : __args[0] as InventoryItem;
            if (item == null || !Mirror(item.inInventory) && !Extra(item.inInventory)) return true;
            Notice("Сначала возьмите предмет в рюкзак");
            return false;
        }

        private static void TakeFromMirror(InventoryItem item, int amount)
        {
            var client = _client; var shown = _shown;
            int index;
            if (client == null || shown == null || !MirrorIndex.TryGetValue(item, out index) || index >= shown.Items.Length) return;
            // The view the player saw may be older than the client's: find the same entry.
            var current = client.Current;
            if (current != shown)
            {
                index = current == null ? -1 : FindEntry(current, (object[])shown.Items[index]);
                if (index < 0) { _dirty = true; Notice("Этот предмет уже взяли"); return; }
            }
            client.Take(index, amount);
        }
        private static int FindEntry(LanStorage.View view, object[] entry)
        {
            byte[] fingerprint = LanStorage.Fingerprint(entry);
            for (int i = 0; i < view.Items.Length; i++)
                if (LanStorage.SameFingerprint(LanStorage.Fingerprint((object[])view.Items[i]), fingerprint)) return i;
            return -1;
        }

        private static void PutIntoMirror(InventoryItem item, int amount)
        {
            var client = _client;
            if (client == null || client.Current == null) { Notice("Хранилище хоста ещё загружается"); return; }
            if (client.Busy) { Notice("Хост ещё переносит вещи, подождите"); return; }
            if (!Personal(item)) { Notice("Сначала переложите предмет в рюкзак"); return; }
            Put(client, item, amount);
        }

        internal static void DrawNotice()
        {
            string text = Time.unscaledTime < _noticeUntil ? _notice : null;
            if (string.IsNullOrEmpty(text)) return;
            float width = Math.Min(520f, Screen.width - 24f);
            GUI.Box(new Rect((Screen.width - width) / 2f, Screen.height * 0.62f, width, 28f), text);
        }

        // ---- Guest: the game's craft window at a host workbench (1.1.5) ----
        private static LanStorage.View _craftShown;
        private static float _craftShownAt, _cancelAt = -10f;
        private static string _queueKey = "";
        private static int _queueUnits, _shownSecond = -1;
        private static AccessTools.FieldRef<Workbench, float> _remaining;
        private static bool _remainingFailed;

        private static void ResetCraftState() { _craftShown = null; _queueKey = ""; _queueUnits = 0; _shownSecond = -1; }
        private static bool IsStandIn(Workbench bench) { return !ReferenceEquals(_craftBench, null) && ReferenceEquals(bench, _craftBench); }

        // The game's Craft button (GlobalCraftController.CraftButton, once per craft after its own
        // ingredient check): the ingredients of one craft leave the backpack now and the request
        // is queued at once, so the guest's belongings are not reported to the host before the
        // host has taken them on its ledger (crafts of one recipe travel together).
        private static bool AddInQueuePrefix(Workbench __instance, InventoryItem prefab, int amount)
        {
            if (!IsStandIn(__instance)) return true;
            try
            {
                var client = _client; var bench = _craftBench;
                bool offered = false;
                if (prefab != null && bench.AvailableRecipes != null)
                    foreach (var recipe in bench.AvailableRecipes) if (recipe != null && recipe.itemID == prefab.itemID) { offered = true; break; }
                if (client == null || _target == null || !offered) return false;
                for (int i = 0; i < Math.Max(1, amount); i++)
                {
                    var escrow = EscrowRecipe(prefab);
                    if (escrow == null) { Notice("Не хватает ресурсов"); break; }
                    if (!client.Craft(_target, prefab.itemID, 1, escrow))
                    {
                        GiveBack(escrow);
                        Notice("Хост ещё не принял прошлые заказы; подождите");
                        break;
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke("Craft not started: " + ex.GetType().Name + ": " + ex.Message); }
            return false;
        }

        // The game's queue button (QueueItemUI.OnPressed: ReturnResourcesFromQueue, then
        // RemoveFromQueue) and the gamepad's cancel (StopCurrentCraftProcess): the host does it.
        private static bool ReturnResourcesPrefix(Workbench __instance, int index)
        {
            if (!IsStandIn(__instance)) return true;
            CancelOrder(index);
            return false;
        }
        private static bool RemoveFromQueuePrefix(Workbench __instance) { return !IsStandIn(__instance); }
        private static bool StopCraftPrefix(Workbench __instance)
        {
            if (!IsStandIn(__instance)) return true;
            if (__instance.workingOn != null) CancelOrder(0);
            return false;
        }
        private static void CancelOrder(int index)
        {
            try
            {
                var bench = _craftBench; var client = _client;
                if (client == null || bench.Queue == null || index < 0 || index >= bench.Queue.Count || bench.Queue[index] == null || bench.Queue[index].Prefab == null) return;
                if (!client.CancelCraft(index, bench.Queue[index].Prefab.itemID)) { Notice("Хост ещё не ответил; нажмите ещё раз"); return; }
                _cancelAt = Time.unscaledTime;
            }
            catch (Exception ex) { _log?.Invoke("Craft cancel not sent: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // The host's queue and progress on the stand-in, as the game's window reads them.
        private static void ShowCraftState(LanStorage.View view)
        {
            var bench = _craftBench; var craft = GlobalCraftController.global;
            var state = view == null ? null : view.Craft;
            if (view != _craftShown) { _craftShown = view; _craftShownAt = Time.time; }
            bool regenerate = false;
            string key = QueueKey(state);
            if (key != _queueKey)
            {
                var queue = new List<Workbench.QueueItem>();
                int units = 0;
                if (state != null)
                    foreach (var entry in state.Queue)
                    {
                        var prefab = Prefab(entry[0]);
                        if (prefab == null) { queue.Clear(); units = 0; break; }
                        queue.Add(new Workbench.QueueItem { Prefab = prefab, Amount = entry[1] });
                        units += entry[1];
                    }
                // Fewer crafts waiting and none cancelled here: one is done (the game's sound).
                if (units < _queueUnits && Time.unscaledTime - _cancelAt > 3f) PlayDone(bench);
                bench.Queue = queue; _queueKey = key; _queueUnits = units; regenerate = true;
            }
            var working = state == null || state.WorkingOn < 0 ? null : Prefab(state.WorkingOn);
            float total = working == null ? 0f : Mathf.Max(0.01f, working.TimeToCraft);
            float done = state == null ? 0f : state.Elapsed;
            if (working != null && state.Working) done += Time.time - _craftShownAt;
            done = working == null ? 0f : Mathf.Clamp(done, 0f, total * 0.999f);
            bench.workingOn = working; bench.timeToCompletion = done;
            float left = working == null ? 0f : total - done;
            SetRemaining(bench, left);
            if (craft == null || !ReferenceEquals(craft.connectedTo, bench)) return;
            if (regenerate) { craft.RegenerateQueue(); _shownSecond = -1; }
            int second = Mathf.CeilToInt(left);
            var shown = craft.SpawnedQueueItemUI;
            if (working != null && second != _shownSecond && shown != null && shown.Count > 0 && shown[0] != null)
            { _shownSecond = second; shown[0].SetRemainingTime(left); }
        }
        private static string QueueKey(LanStorage.CraftState state)
        {
            if (state == null) return "";
            var key = new System.Text.StringBuilder();
            foreach (var entry in state.Queue) key.Append(entry[0]).Append(':').Append(entry[1]).Append(';');
            return key.ToString();
        }
        private static InventoryItem Prefab(int id)
        {
            var items = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            return items == null || id < 0 || id >= items.Length ? null : items[id];
        }
        private static void SetRemaining(Workbench bench, float seconds)
        {
            if (_remainingFailed) return;
            try
            {
                if (_remaining == null) _remaining = AccessTools.FieldRefAccess<Workbench, float>("_timeRemainingCurrentCraft");
                _remaining(bench) = seconds;
            }
            catch (Exception) { _remainingFailed = true; }
        }
        private static void PlayDone(Workbench bench)
        {
            try { if (bench.audioSource != null && bench.AudioOnCraftComplete != null) bench.audioSource.PlayOneShot(bench.AudioOnCraftComplete); }
            catch (Exception) { }
        }

        // One craft's ingredients out of the backpack, as Inventory.Save() entries, or null
        // when they are not all there (nothing is removed then).
        private static object[] EscrowRecipe(InventoryItem recipe)
        {
            var backpack = Backpack();
            if (backpack == null || recipe == null || recipe.recipe == null) return null;
            var needs = Needs(recipe);
            if (needs.Count == 0) return null;
            foreach (var need in needs) if (Count(backpack, need[0]) < need[1]) return null;
            var escrow = new List<object>();
            try
            {
                foreach (var need in needs)
                {
                    int left = need[1];
                    foreach (var item in backpack.content.ToArray())
                    {
                        if (left == 0) break;
                        if (item == null || item.itemID != need[0] || item.amount < 1) continue;
                        int take = Math.Min(left, item.amount);
                        escrow.Add(LanStorage.Normalize(new object[] { item.itemID, take, item.Save() }));
                        backpack.Extract(item, take); left -= take;
                    }
                    if (left > 0) throw new InvalidOperationException("ingredients changed");
                }
                return escrow.ToArray();
            }
            catch { GiveBack(escrow.ToArray()); throw; }
        }
        private static void GiveBack(object[] escrow)
        {
            var world = new LanStorageGuestWorld();
            foreach (object[] item in escrow) { try { world.Give(item); } catch (Exception ex) { _log?.Invoke("Craft escrow return failed: " + ex.Message); } }
        }

        // A construction recipe as [id, amount] pairs (amounts as the game rounds them).
        internal static int[][] Needs(RecipeData[] recipe)
        {
            var needs = new List<int[]>();
            if (recipe != null) foreach (var r in recipe) { int n = r == null ? 0 : Mathf.RoundToInt(r.Amount); if (n > 0) needs.Add(new[] { r.ID, n }); }
            return needs.ToArray();
        }
        private static List<int[]> Needs(InventoryItem recipe)
        {
            var needs = new List<int[]>();
            foreach (var r in recipe.recipe) { int n = r == null ? 0 : Mathf.RoundToInt(r.Amount); if (n > 0) needs.Add(new[] { r.ID, n }); }
            return needs;
        }
        private static long Count(Inventory inventory, int id)
        {
            long total = 0;
            if (inventory != null && inventory.content != null) foreach (var item in inventory.content) if (item != null && item.itemID == id) total += item.amount;
            return total;
        }

        private static void Put(LanStorageClient client, InventoryItem item, int amount)
        {
            object[] escrow = null;
            try { escrow = Escrow(item, amount); }
            catch (Exception ex) { _log?.Invoke("Storage put not started: " + ex.GetType().Name + ": " + ex.Message); }
            if (escrow == null) { Notice("Этот предмет нельзя передать"); return; }
            if (!client.Put(escrow))
            {
                // Not sent: give the escrowed item back at once.
                try { new LanStorageGuestWorld().Give(escrow); }
                catch (Exception ex) { _log?.Invoke("Storage escrow return failed: " + ex); }
            }
        }

        // Only the backpack: belt and equipment items may be in the hands or worn.
        private static bool Personal(InventoryItem item)
        {
            var backpack = Backpack();
            return backpack != null && item != null && item.inInventory == backpack;
        }

        // Removes `amount` of a backpack item and returns it as an Inventory.Save()
        // entry, or null when it cannot travel (nothing is removed then).
        private static object[] Escrow(InventoryItem item, int amount)
        {
            var backpack = Backpack();
            if (backpack == null || item == null || item.inInventory != backpack || item.amount < 1) return null;
            amount = Math.Max(1, Math.Min(amount, item.amount));
            var entry = new object[] { item.itemID, amount, item.Save() };
            LanStorage.ValidateItem(entry);
            LanGuestCharacter.ValidateItems(new object[] { entry }, 0);
            if (!LanStorage.ItemFits(entry)) return null;
            entry = LanStorage.Normalize(entry);
            backpack.Extract(item, amount);
            return entry;
        }

        internal static Inventory Backpack()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            return player == null ? null : player.inventory;
        }

        internal static string ItemName(int id) { return Name(id); }
        private static string Name(int id)
        {
            string name;
            if (Names.TryGetValue(id, out name)) return name;
            try
            {
                var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
                name = prefabs != null && id < prefabs.Length && prefabs[id] != null ? prefabs[id].GetName() : null;
            }
            catch (Exception) { name = null; }
            if (string.IsNullOrEmpty(name)) name = "Предмет " + id;
            Names[id] = name;
            return name;
        }

        private static string Wear(int id, object[] entry)
        {
            var extra = (object[])entry[2];
            if (!(extra[0] is float)) return "";
            try
            {
                var prefabs = ItemsDataBase.Global.itemPrefab;
                var weapon = prefabs[id] == null ? null : prefabs[id].weaponData;
                if (weapon != null && weapon.weaponDurabilityMax > 0)
                    return "  ·  " + Mathf.Clamp(Mathf.RoundToInt(100f * (float)extra[0] / weapon.weaponDurabilityMax), 0, 100) + "%";
            }
            catch (Exception) { }
            return "";
        }

        // Instantiates an item exactly as Inventory.Load does; the caller adds it.
        internal static InventoryItem Create(object[] entry)
        {
            LanGuestCharacter.ValidateItems(new object[] { entry }, 0);
            var prefab = ItemsDataBase.Global.itemPrefab[(int)entry[0]];
            var instance = Object.Instantiate(prefab);
            try
            {
                instance.amount = (int)entry[1];
                instance.Load((object[])((object[])entry[2]).Clone());
                return instance;
            }
            catch { Object.Destroy(instance.gameObject); throw; }
        }

        // A container shared with the guest: a native loot inventory that is no part
        // of any character, item, robot or equipment slot.
        internal static bool Shared(Inventory inventory)
        {
            return inventory != null && inventory.content != null && (inventory.IsLootInventory || inventory.GetComponent<Workbench>() != null) && !inventory.IsBackpack &&
                !inventory.backpack && !inventory.belt && !inventory.eqipment && !inventory.robotDevice && !inventory.robotUpdates &&
                inventory.zombieFighterController == null && inventory.equipmentModSlotItem == null &&
                inventory.GetComponentInParent<ZombieFighterController>() == null && inventory.GetComponentInParent<InventoryItem>() == null;
        }

        internal static void Regenerate(Inventory inventory)
        {
            try { if (inventory.connectedInventoryWindow != null) inventory.connectedInventoryWindow.Regenerate(); }
            catch (Exception) { }
        }
    }

    // Host world: every request is checked against the host's own objects.
    internal sealed class LanStorageHostWorld : ILanStorageHostWorld, ILanStoragePlacementWorld, ILanTrainControlWorld, ILanDeathWorld, ILanStorageViewWorld, ILanThrowWorld, ILanDecorWorld, ILanBuildWorld
    {
        private sealed class Container
        {
            internal Inventory Inventory;
            internal Component Owner;
            internal Collider[] Colliders;
            internal string Scene;
            // 1.2.1: a slot of the guest's robot dog.
            internal RobotDogAiController Robot;
            internal int RobotKind, RobotSlot;
        }
        private readonly Func<Vector3?> _guest;
        private readonly LanWorldItems _items;
        internal LanStorageHostWorld(Func<Vector3?> guest, LanWorldItems items) { _guest = guest; _items = items; }

        private Container _guestView;
        public void SetGuestView(object container)
        {
            _guestView = container as Container;
            LanStorageGame.GuestWorking = _guestView == null ? (Func<Workbench, bool>)null : IsGuestWorking;
        }
        private bool IsGuestWorking(Workbench bench)
        {
            // Without the Workbench.Update hook a queued manual recipe would not advance: refuse it.
            return LanStorageGame.ManualWorkHooked && _guestView != null && bench != null && bench.inventory == _guestView.Inventory &&
                Check(_guestView) == LanStorage.Ok;
        }

        public object Resolve(object[] target, out byte status)
        {
            status = LanStorage.NotFound;
            var scenes = GlobalSceneManager.global; var manager = GlobalManager.global;
            if (scenes == null || manager == null || scenes.SceneCurrentlyLoading || string.IsNullOrEmpty(scenes.CurrentSceneName)) { status = LanStorage.Gone; return null; }
            var train = manager.controlledTrain;
            Inventory inventory = null; Component owner = null;
            RobotDogAiController robot = null;
            if ((int)target[0] == LanStorage.RobotTarget)
            {
                byte mine;
                robot = LanTakables.StorageDog((int)target[1], out mine);
                if (robot == null) { status = mine; return null; }
                inventory = LanTakables.RobotInventory(robot, (int)target[2], (int)target[3]);
                owner = robot;
            }
            else if ((int)target[0] == LanStorage.BagTarget)
            {
                inventory = _items == null ? null : _items.FindBag((int)target[1]);
                owner = inventory == null ? null : LanDeathBag.RootOf(inventory);
            }
            else if ((int)target[0] == LanStorage.SceneTarget)
            {
                if ((string)target[1] != scenes.CurrentSceneName) return null;
                var at = new Vector3((float)target[2], (float)target[3], (float)target[4]);
                string name = (string)target[5];
                foreach (var candidate in Object.FindObjectsOfType<Inventory>())
                {
                    if (candidate == null || candidate.Name != name || (candidate.transform.position - at).sqrMagnitude > 0.0025f) continue;
                    if (train != null && candidate.transform.IsChildOf(train.transform)) continue;
                    if (inventory != null) return null; // two containers at one place: refuse
                    inventory = candidate; owner = candidate;
                }
            }
            else
            {
                TrainCar car;
                try { car = TrainLayout.GetCar(train, (int)target[1]); }
                catch (Exception) { return null; }
                int identity = (int)target[3];
                if ((int)target[2] == LanStorage.TrainFurniture)
                {
                    if (car.AllFurniture != null)
                        foreach (var furniture in car.AllFurniture)
                            if (furniture != null && furniture.GetInstanceID() == identity)
                            {
                                var bench = furniture.GetComponentInChildren<Workbench>(true);
                                inventory = furniture.inventory != null ? furniture.inventory : bench == null ? null : bench.inventory;
                                owner = furniture; break;
                            }
                }
                else
                    foreach (var part in car.GetComponentsInChildren<TrainPart>(true))
                        if (part != null && part.GetInstanceID() == identity) { inventory = part.inventory; owner = part; break; }
            }
            if (inventory == null) return null;
            if (robot == null && !LanStorageGame.Shared(inventory)) { status = LanStorage.NotAllowed; return null; }
            var container = new Container { Inventory = inventory, Owner = owner, Scene = scenes.CurrentSceneName,
                Colliders = owner.GetComponentsInChildren<Collider>(true), Robot = robot,
                RobotKind = robot == null ? 0 : (int)target[2], RobotSlot = robot == null ? 0 : (int)target[3] };
            status = Check(container);
            return status == LanStorage.Ok ? container : null;
        }

        public byte Check(object token)
        {
            var c = (Container)token;
            var scenes = GlobalSceneManager.global;
            if (c.Inventory == null || c.Owner == null || scenes == null || scenes.SceneCurrentlyLoading || scenes.CurrentSceneName != c.Scene) return LanStorage.Gone;
            var furniture = c.Owner as Furniture; var part = c.Owner as TrainPart;
            if (furniture != null && furniture.notAssembled || part != null && part.Blueprint) return LanStorage.NotAllowed;
            if (c.Robot != null)
            {
                if (!LanTakables.StillGuests(c.Robot)) return LanStorage.NotYours;
                if (LanTakables.RobotInventory(c.Robot, c.RobotKind, c.RobotSlot) != c.Inventory) return LanStorage.Gone; // the device left its slot
            }
            var guest = _guest();
            if (guest == null) { TooFarLog("the guest's position is not known right now", c); return LanStorage.Lost; }
            float distance = Distance(c, guest.Value);
            if (distance <= LanStorageGame.Reach) return LanStorage.Ok;
            TooFarLog("guest at " + guest.Value.ToString("F1") + ", " + distance.ToString("0.0") + " m away", c);
            return LanStorage.TooFar;
        }
        private static float _tooFarLogAt = -10f;
        private static void TooFarLog(string why, Container c)
        {
            if (Time.unscaledTime - _tooFarLogAt < 3f) return;
            _tooFarLogAt = Time.unscaledTime;
            LanStorageGame.Log("Storage too far: " + why + "; storage '" + (c.Inventory == null ? "?" : c.Inventory.Name) + "' at " + c.Owner.transform.position.ToString("F1") +
                ", " + c.Colliders.Length + " collider(s)");
        }

        private static float Distance(Container c, Vector3 guest)
        {
            float best = float.PositiveInfinity;
            foreach (var collider in c.Colliders)
                if (collider != null && collider.enabled) best = Math.Min(best, Vector3.Distance(collider.bounds.ClosestPoint(guest), guest));
            if (float.IsPositiveInfinity(best))
                foreach (var shown in c.Owner.GetComponentsInChildren<Renderer>())
                    if (shown != null && shown.enabled) best = Math.Min(best, Vector3.Distance(shown.bounds.ClosestPoint(guest), guest));
            if (float.IsPositiveInfinity(best)) best = Vector3.Distance(c.Owner.transform.position, guest);
            return best;
        }

        public LanStorage.View Snapshot(object token)
        {
            var inventory = ((Container)token).Inventory;
            var items = inventory.Save();
            if (items == null || items.Length > LanGuestSchema.MaxItems) return null;
            var view = new LanStorage.View { Name = inventory.Name ?? "", Weight = inventory.currentWeight, MaxWeight = inventory.maxWeight, Items = items };
            var bench = BenchOf((Container)token);
            if (bench != null) view.Craft = CraftOf(bench);
            return view;
        }

        // The workbench whose own inventory this container is (1.1.5: queue, progress, cancel).
        private static Workbench BenchOf(Container c)
        {
            if (c == null || c.Owner == null || c.Inventory == null) return null;
            var bench = c.Owner.GetComponentInChildren<Workbench>(true);
            return bench != null && bench.inventory == c.Inventory && bench.AvailableRecipes != null && bench.Queue != null ? bench : null;
        }
        private static LanStorage.CraftState CraftOf(Workbench bench)
        {
            var queue = new List<int[]>();
            foreach (var entry in bench.Queue)
            {
                // Stop at anything odd: indexes must stay those of the host's queue (cancel).
                if (queue.Count >= LanStorage.MaxCraftQueue || entry == null || entry.Prefab == null || entry.Amount < 1 ||
                    entry.Prefab.itemID < 0 || entry.Prefab.itemID > 65535) break;
                queue.Add(new[] { entry.Prefab.itemID, Math.Min(entry.Amount, LanStorage.MaxAmount) });
            }
            var working = bench.workingOn;
            bool known = working != null && working.itemID >= 0 && working.itemID <= 65535;
            float seconds = bench.timeToCompletion;
            if (float.IsNaN(seconds) || seconds < 0f) seconds = 0f;
            return new LanStorage.CraftState
            {
                WorkingOn = known ? working.itemID : -1,
                Elapsed = known ? (int)Math.Min(LanStorage.MaxCraftSeconds, Math.Floor(seconds)) : 0,
                Working = known && (!bench.NeedManualWork || LanStorageGame.WorkbenchWindowOpen(bench)),
                Queue = queue.ToArray()
            };
        }

        public byte CancelCraft(object token, int index, int recipe)
        {
            var bench = BenchOf((Container)token);
            if (bench == null) return LanStorage.NotAllowed;
            if (index >= bench.Queue.Count) return LanStorage.NotFound;
            var entry = bench.Queue[index];
            if (entry == null || entry.Prefab == null || entry.Prefab.itemID != recipe) return LanStorage.NotFound;
            // As the game's queue button: the ingredients of one craft go into the workbench.
            bench.ReturnResourcesFromQueue(index);
            bench.RemoveFromQueue(index);
            return LanStorage.Ok;
        }

        public byte Take(object token, int index, byte[] fingerprint, int amount, out object[] taken)
        {
            var c = (Container)token;
            if (c.Robot != null && c.RobotKind == LanStorage.RobotSlotDevice)
            {
                // 1.2.1: as the game's lock over the slot; the device's state goes into its item first.
                if (LanTakables.DeviceInUse(c.Robot, c.RobotSlot)) { taken = null; return LanStorage.DeviceInUse; }
                LanTakables.SaveDevice(c.Robot, c.RobotSlot);
            }
            byte status = TakeItem(token, index, fingerprint, amount, out taken);
            if (status == LanStorage.Ok && c.Robot != null) LanTakables.RobotChanged(c.Robot);
            return status;
        }
        private byte TakeItem(object token, int index, byte[] fingerprint, int amount, out object[] taken)
        {
            taken = null;
            var inventory = ((Container)token).Inventory;
            if (Busy(inventory)) return LanStorage.Busy;
            InventoryItem item = null;
            var content = inventory.content;
            if (index < content.Count && Matches(content[index], fingerprint)) item = content[index];
            else foreach (var candidate in content) if (Matches(candidate, fingerprint)) { item = candidate; break; }
            if (item == null || item.amount < 1) return LanStorage.NotFound;
            amount = Math.Min(amount, item.amount);
            var entry = new object[] { item.itemID, amount, item.Save() };
            try { LanStorage.ValidateItem(entry); }
            catch (InvalidDataException) { return LanStorage.Invalid; }
            if (!LanStorage.ItemFits(entry)) return LanStorage.TooLarge;
            inventory.Extract(item, amount);
            inventory.wasChecked = true;
            LanStorageGame.Regenerate(inventory);
            taken = LanStorage.Normalize(entry);
            DestroyIfEmptied(inventory);
            return LanStorage.Ok;
        }
        // 1.4.19: a loot bag (a zombie's, a dead guest's) goes once it is empty, as the game removes it when its window
        // closes (InventoryWindow.Close: Inventory.CheckDestroyIfEmpty); the guest takes without a window here, so an
        // emptied bag stayed where it lay. Not while the host has the bag open itself (its window does it then).
        private static void DestroyIfEmptied(Inventory inventory)
        {
            try
            {
                if (inventory == null || !inventory.DestroyIfEmpty || inventory.content.Count != 0 || inventory.connectedInventoryWindow != null) return;
                inventory.CheckDestroyIfEmpty();
            }
            catch (Exception) { }
        }

        public byte Put(object token, object[] item, out int accepted)
        {
            byte status = PutItem(token, item, out accepted);
            if (status == LanStorage.Ok && ((Container)token).Robot != null) LanTakables.RobotChanged(((Container)token).Robot);
            return status;
        }
        private byte PutItem(object token, object[] item, out int accepted)
        {
            accepted = 0;
            var inventory = ((Container)token).Inventory;
            if (Busy(inventory)) return LanStorage.Busy;
            InventoryItem instance;
            try { instance = LanStorageGame.Create(item); }
            catch (InvalidDataException) { return LanStorage.Invalid; }
            bool stored = false;
            try
            {
                if (!inventory.IsAllowedItem(instance) || !inventory.IsCanPutHere(instance)) return LanStorage.NotAllowed;
                int fit = inventory.HowMuchCanFit(instance);
                if (fit < 1) return LanStorage.NoSpace;
                instance.amount = accepted = Math.Min(fit, (int)item[1]);
                if (!inventory.Add(instance, true)) { accepted = 0; return LanStorage.NoSpace; }
                stored = true;
                inventory.wasChecked = true;
                LanStorageGame.Regenerate(inventory);
                return LanStorage.Ok;
            }
            finally { if (!stored && instance != null) Object.Destroy(instance.gameObject); }
        }

        public byte Pickup(int hostId, int itemId, int amount, out object[] taken)
        {
            taken = null;
            var item = _items == null ? null : _items.FindItem(hostId);
            if (item == null || item.itemID != itemId) return LanStorage.NotFound;
            var guest = _guest();
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(item.transform.position, guest.Value) > LanWorldItems.Reach) return LanStorage.TooFar;
            amount = Math.Min(amount, item.amount);
            var entry = new object[] { item.itemID, amount, item.Save() };
            try { LanStorage.ValidateItem(entry); }
            catch (InvalidDataException) { return LanStorage.Invalid; }
            if (!LanStorage.ItemFits(entry)) return LanStorage.TooLarge;
            if (amount >= item.amount) { item.amount = 0; item.gameObject.SetActive(false); Object.Destroy(item.gameObject); _items.Removed(hostId); }
            else item.amount -= amount;
            taken = LanStorage.Normalize(entry);
            return LanStorage.Ok;
        }

        public byte Drop(object[] item, float x, float y, float z)
        {
            var guest = _guest();
            var at = new Vector3(x, y, z);
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(at, guest.Value) > LanWorldItems.Reach) return LanStorage.TooFar;
            InventoryItem instance;
            try { instance = LanStorageGame.Create(item); }
            catch (InvalidDataException) { return LanStorage.Invalid; }
            // On a train the game parents loose items to the car (kinematic, where placed);
            // elsewhere they fall from just above the point.
            TrainCar car = null;
            foreach (var hit in Physics.RaycastAll(at + Vector3.up, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                var found = hit.collider == null ? null : hit.collider.GetComponentInParent<TrainCar>();
                if (found != null) { car = found; break; }
            }
            instance.transform.position = car != null ? at : at + Vector3.up * .1f;
            if (!instance.gameObject.activeSelf) instance.gameObject.SetActive(true);
            if (car != null) instance.ParentToTrainCar(car);
            else foreach (var body in instance.GetComponentsInChildren<Rigidbody>()) body.isKinematic = false;
            return LanStorage.Ok;
        }

        // The guest died (0.12.0). Its belongings go into the game's own loot bags (the zombie
        // loot prefab) where it fell: on the ground they drop like a zombie's bag, aboard a car
        // they lie still on it and ride along. An item no bag takes lies loose next to them.
        internal const float DeathReach = 12f;
        internal const int MaxDeathBags = 4;
        // Host: where the bags are (car index and car-local point, or -1 and the world point).
        internal Action<int, Vector3> GuestDied;
        public byte Death(object[] items, float x, float y, float z, int carIndex)
        {
            var loot = GlobalObjectLootController.global;
            if (loot == null || loot.PrefabZombieLoot == null) return LanStorage.Unavailable;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            TrainCar car = null;
            if (carIndex >= 0)
            {
                try { car = TrainLayout.GetCar(train, carIndex); }
                catch (Exception) { car = null; }
            }
            var at = car == null ? new Vector3(x, y, z) : car.transform.TransformPoint(new Vector3(x, y, z));
            // Where the host last saw the guest decides when its own claim is far from that.
            var guest = _guest();
            if (guest != null && Vector3.Distance(at, guest.Value) > DeathReach) { at = guest.Value; car = null; }
            if (car == null) car = CarUnder(at);
            var bags = new List<Inventory>();
            Inventory bag = null;
            int loose = 0;
            foreach (object[] entry in items)
            {
                InventoryItem instance;
                try { instance = LanStorageGame.Create(entry); }
                catch (Exception ex) { LanStorageGame.Log("Dead guest's item " + entry[0] + " not recreated: " + ex.Message); continue; }
                bool stored = false;
                for (int attempt = 0; attempt < 2 && !stored; attempt++)
                {
                    if (bag == null || attempt == 1)
                    {
                        if (bags.Count >= MaxDeathBags) break;
                        bag = NewDeathBag(loot, at, car, bags.Count);
                        if (bag == null) break;
                        bags.Add(bag);
                    }
                    try
                    {
                        bag.maxWeight = 1000000f;
                        stored = bag.IsAllowedItem(instance) && bag.IsCanPutHere(instance) && bag.HowMuchCanFit(instance) >= instance.amount && bag.Add(instance, true);
                    }
                    catch (Exception ex) { LanStorageGame.Log("Dead guest's bag refused an item: " + ex.Message); }
                }
                if (!stored) { LieLoose(instance, at + new Vector3(.3f * (loose % 3), 0f, .3f * (loose / 3 % 3)), car); loose++; }
            }
            foreach (var filled in bags)
            {
                // As the game does with a zombie's bag: nothing more goes in.
                filled.maxWeight = 0f;
                filled.wasChecked = true;
                LanStorageGame.Regenerate(filled);
            }
            LanStorageGame.Log("Dead guest's belongings: " + bags.Count + " bag(s)" + (car != null ? " on a car" : "") + (loose > 0 ? ", " + loose + " loose" : ""));
            int carIndex2 = car == null ? -1 : TrainLayout.CarIndex(train, car);
            GuestDied?.Invoke(carIndex2, carIndex2 < 0 ? at : car.transform.InverseTransformPoint(at));
            return LanStorage.Ok;
        }
        private static TrainCar CarUnder(Vector3 at)
        {
            foreach (var hit in Physics.RaycastAll(at + Vector3.up, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                var found = hit.collider == null ? null : hit.collider.GetComponentInParent<TrainCar>();
                if (found != null) return found;
            }
            return null;
        }
        private static Inventory NewDeathBag(GlobalObjectLootController loot, Vector3 at, TrainCar car, int index)
        {
            var offset = new Vector3(.45f * (index % 2), 0f, .45f * (index / 2));
            Transform bag = null;
            try
            {
                bag = Object.Instantiate(loot.PrefabZombieLoot, at + offset + Vector3.up * (car != null ? .15f : loot.LootVerticalPosOffset), Quaternion.identity);
                bag.gameObject.AddComponent<LanDeathBag>();
                var inventory = bag.GetComponentInChildren<Inventory>();
                if (inventory == null || !LanStorageGame.Shared(inventory)) { Object.Destroy(bag.gameObject); return null; }
                if (car != null)
                {
                    foreach (var body in bag.GetComponentsInChildren<Rigidbody>()) { body.isKinematic = true; }
                    bag.SetParent(car.transform, true);
                }
                else
                {
                    var body = bag.GetComponent<Rigidbody>();
                    if (body != null) body.velocity = Vector3.up * loot.LootVerticalSpeed;
                }
                return inventory;
            }
            catch (Exception ex)
            {
                LanStorageGame.Log("Bag for the dead guest not created: " + ex.GetType().Name + ": " + ex.Message);
                if (bag != null) Object.Destroy(bag.gameObject);
                return null;
            }
        }
        private static void LieLoose(InventoryItem instance, Vector3 at, TrainCar car)
        {
            instance.transform.position = car != null ? at : at + Vector3.up * .1f;
            if (!instance.gameObject.activeSelf) instance.gameObject.SetActive(true);
            if (car != null) instance.ParentToTrainCar(car);
            else foreach (var body in instance.GetComponentsInChildren<Rigidbody>()) body.isKinematic = false;
        }

        public byte PlaceItem(object[] item, int carIndex, float[] pose)
        {
            var guest = _guest();
            if (guest == null) return LanStorage.Lost;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            TrainCar car = null;
            if (carIndex >= 0)
            {
                try { car = TrainLayout.GetCar(train, carIndex); }
                catch (Exception) { return LanStorage.NotFound; }
            }
            var local = new Vector3(pose[0], pose[1], pose[2]);
            var at = car == null ? local : car.transform.TransformPoint(local);
            if (Vector3.Distance(at, guest.Value) > LanWorldItems.Reach) return LanStorage.TooFar;
            RaycastHit support;
            if (!Physics.Raycast(at + Vector3.up * .15f, Vector3.down, out support, .3f, ~0, QueryTriggerInteraction.Ignore) ||
                Vector3.Angle(support.normal, Vector3.up) >= 30f ||
                (car != null && support.collider.GetComponentInParent<TrainCar>() != car)) return LanStorage.NotAllowed;
            InventoryItem instance = null;
            try
            {
                instance = LanStorageGame.Create(item);
                var rotation = new Quaternion(pose[3], pose[4], pose[5], pose[6]);
                instance.transform.position = at;
                instance.transform.rotation = car == null ? rotation : car.transform.rotation * rotation;
                instance.gameObject.SetActive(true);
                if (car != null) instance.ParentToTrainCar(car);
                else foreach (var body in instance.GetComponentsInChildren<Rigidbody>()) body.isKinematic = instance.KeepKinematic;
                return LanStorage.Ok;
            }
            catch (Exception ex)
            {
                if (instance != null) { instance.gameObject.SetActive(false); Object.Destroy(instance.gameObject); }
                LanStorageGame.Log("Item placement refused: " + ex.GetType().Name);
                return LanStorage.Invalid;
            }
        }

        public int[][] Recipe(object[] target, int recipe, out byte status)
        {
            var container = Resolve(target, out status) as Container;
            if (container == null) return null;
            var bench = BenchOf(container);
            if (bench == null) { status = LanStorage.NotAllowed; return null; }
            if (bench.NeedManualWork && !IsGuestWorking(bench)) { status = LanStorage.Manual; return null; }
            foreach (var item in bench.AvailableRecipes)
            {
                if (item == null || item.itemID != recipe || item.recipe == null) continue;
                var needs = new List<int[]>();
                foreach (var r in item.recipe) { int n = r == null ? 0 : Mathf.RoundToInt(r.Amount); if (n > 0) needs.Add(new[] { r.ID, n }); }
                if (needs.Count == 0) break;
                status = LanStorage.Ok;
                return needs.ToArray();
            }
            status = LanStorage.NotAllowed;
            return null;
        }

        public byte Queue(object[] target, int recipe, int count)
        {
            byte status;
            var container = Resolve(target, out status) as Container;
            if (container == null) return status;
            var bench = BenchOf(container);
            InventoryItem prefab = null;
            if (bench != null && bench.AvailableRecipes != null) foreach (var item in bench.AvailableRecipes) if (item != null && item.itemID == recipe) { prefab = item; break; }
            if (prefab == null || bench.Queue == null) return LanStorage.NotAllowed;
            if (bench.NeedManualWork && !IsGuestWorking(bench)) return LanStorage.Manual;
            // Native AddInQueue would take the ingredients from the host's own backpack.
            var last = bench.Queue.Count > 0 ? bench.Queue[bench.Queue.Count - 1] : null;
            if (last != null && last.Prefab == prefab) last.Amount += count;
            else bench.Queue.Add(new Workbench.QueueItem { Prefab = prefab, Amount = count });
            // The host's own craft window on this workbench shows the new order too.
            var craft = GlobalCraftController.global;
            if (craft != null && craft.connectedTo == bench) craft.RegenerateQueue();
            return LanStorage.Ok;
        }

        public byte Work(int kind, float x, float y, float z, int tool, int extra, object[] escrow, out object[] given, out int detail)
        { return LanWorldWork.HostWork(kind, new Vector3(x, y, z), tool, extra, escrow, _guest(), out given, out detail); }

        // A blueprint as TrainConstructor.ConfirmPlacement places it, on the host's own mount
        // point at the guest's place (same car, type and car-local position) or on the car's
        // platform for parts placed on surfaces. Placing is free; building pays the recipe.
        // The host's train controls for the guest (0.10.0); the repair's spare lever is already off the guest's ledger.
        public byte Control(int kind, int index, int check, int value, object[] escrow)
        {
            return LanTrainControl.Host(kind, index, check, value, _guest());
        }
        // 1.4.0: the guest's grenade, dynamite or C4 (already off its ledger), thrown from its hand.
        public byte Throw(int item, int car, float[] at, float[] turn, int type) { return LanThrows.Host(item, car, at, turn, type, _guest()); }
        // 1.4.0: paint, wires and sign text of the host's train.
        public byte Decor(LanStorage.DecorRequest request) { return LanTrainDecor.Host(request, _guest()); }
        // 1.4.2: the guest's furniture kits, and taking the host's train apart.
        public byte PlaceFurniture(int kit, int car, float[] pose) { return LanTrainBuild.HostFurniture(kit, car, pose, _guest()); }
        public byte Dismantle(int mode, int car, int owner, int identity, int tool, out object[] given, out bool done)
        { return LanTrainBuild.HostDismantle(mode, car, owner, identity, tool, _guest(), out given, out done); }

        public byte Place(int partId, int carIndex, int pointType, float[] point, float[] pose)
        {
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var scenes = GlobalSceneManager.global;
            var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
            if (train == null || scenes == null || scenes.SceneCurrentlyLoading || parts == null || parts.Parts == null) return LanStorage.Unavailable;
            var prefab = partId < parts.Parts.Count ? parts.Parts[partId] : null;
            if (prefab == null || prefab.HideInMenu) return LanStorage.NotAllowed;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return LanStorage.NotFound; }
            var local = new Vector3(pose[0], pose[1], pose[2]);
            var localRotation = new Quaternion(pose[3], pose[4], pose[5], pose[6]);
            localRotation.Normalize();
            var at = car.transform.TransformPoint(local);
            var guest = _guest();
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(at, guest.Value) > 12f) return LanStorage.TooFar;
            var rotation = car.transform.rotation * localRotation;
            TrainPart placed;
            if (pointType >= 0)
            {
                if (prefab.PlacedOnSurface || prefab.TypeID != pointType) return LanStorage.NotAllowed;
                var wanted = new Vector3(point[0], point[1], point[2]);
                TrainMountPoint mount = null; float best = .0025f; // 5 cm
                foreach (var candidate in car.GetComponentsInChildren<TrainMountPoint>())
                {
                    if (candidate == null || candidate.ID != pointType) continue;
                    float d = (car.transform.InverseTransformPoint(candidate.transform.position) - wanted).sqrMagnitude;
                    if (d <= best) { best = d; mount = candidate; }
                }
                if (mount == null) return LanStorage.NotFound;
                if (mount.AttachedPart != null) return LanStorage.Occupied;
                if (prefab.Floor && (mount.ParentBlock == null || mount.ParentBlock.BlockBelow != null)) return LanStorage.NotAllowed;
                if (mount.SubPointOfPart != null)
                {
                    var variant = prefab.GetPrefabVariantFor(mount.SubPointOfPart);
                    if (variant != null) prefab = variant; // what the game selects by itself here
                }
                // The point's own pose, turned by one of the part's rotation variants.
                var mountRotation = mount.transform.rotation;
                Quaternion chosen = mountRotation; float angle = Quaternion.Angle(mountRotation, rotation);
                if (prefab.RotationVariants != null)
                    foreach (var variant in prefab.RotationVariants)
                    {
                        var turned = mountRotation * Quaternion.Euler(variant);
                        float a = Quaternion.Angle(turned, rotation);
                        if (a < angle) { angle = a; chosen = turned; }
                    }
                if (angle > 5f) return LanStorage.Invalid;
                placed = Object.Instantiate(prefab, mount.transform.position, chosen);
                placed.transform.parent = mount.transform;
                placed.AttachedToPoint = mount;
                mount.AttachedPart = placed;
                placed.TurnInBlueprint();
            }
            else
            {
                if (!prefab.PlacedOnSurface) return LanStorage.NotAllowed;
                var platform = car.trainPlatform;
                if (platform == null) return LanStorage.NotFound;
                placed = Object.Instantiate(prefab, at, rotation);
                placed.transform.parent = platform.transform;
                placed.TurnInBlueprint();
                placed.OnTrainPlatform = platform;
            }
            // 1.4.11: shown (and solid) as the host's own blueprints are now: the game applies that only when it
            // changes, so a guest's new blueprint stayed solid underfoot until the host took a tool and put it away.
            placed.SetBlueprintVisible(LanTrainBuild.BlueprintsShownNow());
            try
            {
                var tc = TrainConstructor.Global;
                if (tc != null && tc.SoundPreviewPlaced != null && GlobalSoundEffects.global != null) GlobalSoundEffects.global.PlayGlobalSound(tc.SoundPreviewPlaced);
            }
            catch (Exception) { }
            return LanStorage.Ok;
        }

        // One hit of the guest's tool, as Gun.MeleeAssemblyTarget does it, except that a
        // part's recipe is paid from the guest's ledger (TrainPart.ConstructWithTool would
        // take it from the host's own backpack).
        public byte Build(int carIndex, int kind, int identity, int tool, Func<int[][], bool> pay, out bool completed)
        {
            completed = false;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var scenes = GlobalSceneManager.global;
            if (train == null || scenes == null || scenes.SceneCurrentlyLoading) return LanStorage.Unavailable;
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
            var guest = _guest();
            if (guest == null) return LanStorage.Lost;
            var near = new Container { Owner = target, Colliders = target.GetComponentsInChildren<Collider>(true) };
            // Blueprints often have no enabled collider: then the distance is to the part's pivot.
            if (Distance(near, guest.Value) > LanStorageGame.Reach + 1.5f) return LanStorage.TooFar;
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            var toolPrefab = prefabs != null && tool < prefabs.Length ? prefabs[tool] : null;
            var gun = LanWorldWork.HostGun(tool);
            if (toolPrefab == null || gun == null || gun.furnitureBuildPower <= 0f) return LanStorage.NotAllowed;
            float power = gun.furnitureBuildPower;
            var assembly = target as Furniture;
            if (assembly != null && !assembly.notAssembled)
            {
                // 1.4.2: an assembled furniture taken partly apart: the tool repairs it (Gun.MeleeAssemblyTarget).
                if (!LanTrainBuild.ToolFits(assembly, toolPrefab)) return LanStorage.NotAllowed;
                float missing = assembly.completionBarMax - assembly.CompletionBarCurrent;
                if (missing > 0f) assembly.AddCompletionBar(Math.Min(power, missing));
                return LanStorage.Ok;
            }
            if (assembly != null)
            {
                if (!assembly.notAssembled || !LanTrainBuild.ToolFits(assembly, toolPrefab)) return LanStorage.NotAllowed;
                float left = assembly.completionBarMax - assembly.CompletionBarCurrent;
                if (left <= 0f) return LanStorage.Ok; // its Update finishes it
                assembly.AddCompletionBar(Math.Min(power, left));
                completed = power >= left;
                return LanStorage.Ok;
            }
            var blueprint = (TrainPart)target;
            if (!blueprint.Blueprint && blueprint.IsBuilt() && Mathf.RoundToInt(blueprint.RequiredTool) == toolPrefab.GetToolType())
            {
                // 1.4.2: a built part taken partly apart: the tool repairs it (TrainPart.ConstructWithTool).
                blueprint.BuildPointsCurrent = Mathf.Clamp(blueprint.BuildPointsCurrent + power, 0f, blueprint.BuildPointsMaximum);
                return LanStorage.Ok;
            }
            if (!blueprint.Blueprint || Mathf.RoundToInt(blueprint.RequiredTool) != toolPrefab.GetToolType()) return LanStorage.NotAllowed;
            if (!blueprint.IsHasFoundation()) return LanStorage.NotAllowed;
            float next = Mathf.Clamp(blueprint.BuildPointsCurrent + power, 0f, blueprint.BuildPointsMaximum);
            if (next < blueprint.BuildPointsMaximum) { blueprint.BuildPointsCurrent = next; return LanStorage.Ok; }
            if (!pay(LanStorageGame.Needs(blueprint.Recipe))) return LanStorage.Missing;
            blueprint.BuildPointsCurrent = next;
            blueprint.ReplaceWithRealObject();
            completed = true;
            return LanStorage.Ok;
        }

        private static bool Matches(InventoryItem item, byte[] fingerprint)
        {
            if (item == null || item.amount < 1) return false;
            try { return LanStorage.SameFingerprint(LanStorage.Fingerprint(new object[] { item.itemID, item.amount, item.Save() }), fingerprint); }
            catch (InvalidDataException) { return false; }
        }

        // The host is dragging or splitting a stack of this very container.
        private static bool Busy(Inventory inventory)
        {
            var controller = InventoryController.global;
            if (controller != null && controller.draggedIcon != null && controller.openedInventoryCell == inventory) return true;
            var menu = Object.FindObjectOfType<DragItemMenu>();
            return menu != null && menu.isActiveAndEnabled && (menu.from == inventory || menu.to == inventory);
        }
    }

    // Guest world: received items go into the backpack (over the weight limit
    // rather than lost); the window computes room with the native formula.
    internal sealed class LanStorageGuestWorld : ILanStorageGuestWorld
    {
        public int Room(object[] item)
        {
            var backpack = LanStorageGame.Backpack();
            var db = ItemsDataBase.Global;
            int id = (int)item[0];
            if (backpack == null || db == null || db.items == null || id >= db.items.Count || db.items[id] == null) return 0;
            float one = db.items[id].weght;
            if (one <= 0f) return LanStorage.MaxAmount;
            return Math.Max(0, Mathf.FloorToInt((backpack.maxWeight - backpack.currentWeight) / one));
        }

        // What ConstructWithTool takes on completion, from the backpack only.
        public void Spend(int[][] needs)
        {
            var backpack = LanStorageGame.Backpack();
            if (backpack == null || needs == null) return;
            foreach (var need in needs)
            {
                int left = need[1];
                foreach (var item in backpack.content.ToArray())
                {
                    if (left <= 0) break;
                    if (item == null || item.itemID != need[0] || item.amount < 1) continue;
                    int take = Math.Min(left, item.amount);
                    backpack.Extract(item, take); left -= take;
                }
            }
        }

        public void Give(object[] item)
        {
            var backpack = LanStorageGame.Backpack();
            if (backpack == null) throw new InvalidOperationException("Backpack unavailable");
            var instance = LanStorageGame.Create(item);
            bool stored = false;
            try { stored = backpack.Add(instance, true); }
            finally { if (!stored && instance != null) Object.Destroy(instance.gameObject); }
            if (!stored) throw new InvalidOperationException("Backpack refused the item");
        }
    }
}
