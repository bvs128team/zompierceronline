using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Zompiercer.Train;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Guest building and refuelling on the host's train (0.8.0). The guest's train shows
    // the host's construction (TrainLayout); a hit of the guest's tool on a host blueprint
    // part or unassembled furniture becomes a build request, and a full canister poured
    // into a host fuel tank a refuel request. The host does both on its own objects; the
    // next train layout shows the result. 0.8.1: the guest's own construction mode places
    // new blueprints too. It works on the guest's copy of the train, whose mount points are
    // made to match the host's (TrainLayout); confirming a placement asks the host, which
    // places the blueprint on its own train. Removing and building in that mode stay closed
    // (the host's parts are never edited from the guest's copy). 1.4.0: the paint and wire modes
    // and signs work on the host's train through LanTrainDecor. 1.4.2: the guest's furniture kits are
    // placed by the host; the guest takes host parts and furniture apart (their recipe or kit comes
    // back to it), repairs them, and takes blueprints away, all through the host. Main thread only.
    internal static class LanTrainBuild
    {
        private const float Look = 3f;
        private const int BuildAction = 51, RefuelAction = 55;
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Guest: one build hit (car, kind, host id, tool item id, recipe spent on completion); null outside a usable session.
        internal static Func<int, int, int, int, int[][], bool> GuestBuild;
        // Guest: places one blueprint (part id, car, point type or -1, point, pose); null outside a usable session.
        internal static Func<int, int, int, float[], float[], bool> GuestPlace;
        // Guest (1.4.2): a furniture kit (escrow, car, pose, answer, give back) and a dismantling request
        // (mode, car, owner, host id, tool, answer); null outside a usable session.
        internal static Func<object[], int, float[], Action<byte>, Action<object[]>, bool> GuestFurniture;
        internal static Func<int, int, int, int, int, Action<byte, bool>, bool> GuestDismantle;
        private const int DisassembleAction = 54, RepairAction = 84, RemoveAction = 83;
        private static int[] _furnitureBefore;
        private static object[] _kit;
        private static Inventory _kitSource;
        // Guest attached to the host's world: construction works on the host's train only.
        internal static bool GuestAttached;
        private static readonly FieldInfo SelectedPoint = AccessTools.Field(typeof(TrainConstructor), "SelectedTrainMountPoint");
        private static float _menuNoticeAt = -10f;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.train-build");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Gun), "MeleeAssemblyTarget"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(AssemblyPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Gun), "MeleeDisassemblyTarget"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(DisassemblyPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(GlobalCraftController), "MovePreviewObjectToMouse"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(FurniturePrefix)), postfix: new HarmonyMethod(typeof(LanTrainBuild), nameof(FurniturePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieFighterFireArmWeapon), "GunShot"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(GunShotPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainConstructor), "Update"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(ConstructorPrefix)));
                if (SelectedPoint == null) throw new MissingMemberException("TrainConstructor.SelectedTrainMountPoint");
                _harmony.Patch(AccessTools.Method(typeof(TrainConstructor), "ConfirmPlacement"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(ConfirmPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainController), "GetMountPointsOfType"), postfix: new HarmonyMethod(typeof(LanTrainBuild), nameof(PointsPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainPart), "GetNearestMountPoint"), postfix: new HarmonyMethod(typeof(LanTrainBuild), nameof(NearestPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainMountPoint), "CreateMarker"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(MarkerPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Gun), "GetHoveredTrainPart"), postfix: new HarmonyMethod(typeof(LanTrainBuild), nameof(HoveredPartPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainPart), "Remove"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(RemovePrefix)));
                // Wiring and painting go to the host (LanTrainDecor); without a usable session they stay closed.
                _harmony.Patch(AccessTools.Method(typeof(Zompiercer.Electricity.WireEditor), "SetEditMode"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(ModePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Zompiercer.Paint.PaintManager), "SetPaintMode"), prefix: new HarmonyMethod(typeof(LanTrainBuild), nameof(ModePrefix)));
            }
            catch (Exception ex)
            {
                // Without the hooks a guest could build only in its own world: refused.
                _harmony.UnpatchSelf();
                Failure = ex.GetType().Name + ": " + ex.Message;
                log("Train build hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; GuestBuild = null; GuestPlace = null; GuestFurniture = null; GuestDismantle = null; GuestAttached = false; }

        // ---- Hooks (guest only) ----
        private static bool Active { get { return LanSaveIsolation.Active && GuestAttached; } }

        // Without a usable session the guest's construction mode stays closed: what it
        // placed would exist only in its own world.
        private static bool ConstructorPrefix(TrainConstructor __instance)
        {
            if (!Active || GuestPlace != null) return true;
            try
            {
                if (__instance.PreviewObject != null) { UnityEngine.Object.Destroy(__instance.PreviewObject.gameObject); __instance.PreviewObject = null; }
                var input = Zompiercer.Inputs.InputController.Global;
                if (input != null && input.isBuildMenuPressed && Time.unscaledTime - _menuNoticeAt > 2f)
                {
                    _menuNoticeAt = Time.unscaledTime;
                    LanStorageGame.Notice("Строить на поезде хоста можно, когда хост сохраняет ваш профиль");
                }
            }
            catch (Exception ex) { _log?.Invoke("Construction hook failed: " + ex.Message); }
            return false;
        }

        // ---- Guest construction mode on the host's train (0.8.1) ----
        // Only points that exist and are free on the host's train.
        private static bool Usable(TrainMountPoint point)
        {
            if (!TrainLayout.GuestPointUsable(point)) return false;
            // A floor needs the platform block under its point (ConfirmPlacement reads it).
            var preview = TrainConstructor.Global == null ? null : TrainConstructor.Global.PreviewObject;
            return preview == null || !preview.Floor || point.ParentBlock != null;
        }
        private static void PointsPostfix(ref List<TrainMountPoint> __result)
        {
            if (!Active || __result == null) return;
            try { __result.RemoveAll(point => !Usable(point)); }
            catch (Exception ex) { _log?.Invoke("Mount point filter failed: " + ex.Message); __result.Clear(); }
        }
        private static void NearestPostfix(ref TrainMountPoint __result)
        {
            if (!Active || __result == null) return;
            try { if (!Usable(__result)) __result = null; }
            catch (Exception) { __result = null; }
        }
        private static bool MarkerPrefix(TrainMountPoint __instance)
        {
            if (!Active) return true;
            try { return Usable(__instance); }
            catch (Exception) { return false; }
        }
        // In construction mode the game removes or builds the hovered part; never the guest's copy.
        private static void HoveredPartPostfix(ref TrainPart __result) { if (Active) __result = null; }
        private static bool RemovePrefix() { return !Active; }
        private static bool ModePrefix(bool value)
        {
            if (!Active || !value || LanTrainDecor.GuestRequest != null) return true;
            LanStorageGame.Notice("Красить и проводить провода на поезде хоста можно, когда хост сохраняет ваш профиль");
            return false;
        }

        // Confirming a placement asks the host (the preview stays for the next one).
        private static bool ConfirmPrefix(TrainConstructor __instance)
        {
            if (!Active) return true;
            try
            {
                var preview = __instance.PreviewObject;
                var place = GuestPlace;
                if (preview == null) return false;
                if (place == null) { LanStorageGame.Notice("Строить на поезде хоста можно, когда хост сохраняет ваш профиль"); return false; }
                var point = SelectedPoint.GetValue(__instance) as TrainMountPoint;
                var platform = __instance.TargetTrainPlatform;
                var checker = preview.GetComponent<CollisionChecker>();
                bool blocked = preview.Floor && point != null && point.ParentBlock != null && point.ParentBlock.BlockBelow != null;
                if (blocked || checker != null && checker.Intersects || point == null && platform == null) { Sound(__instance.SoundPreviewCantBePlaced); return false; }
                var car = point != null ? point.GetComponentInParent<TrainCar>() : platform.GetComponentInParent<TrainCar>();
                var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
                int index = TrainLayout.CarIndex(train, car);
                if (index < 0) { LanStorageGame.Notice("Строить можно только на поезде хоста"); return false; }
                var t = car.transform;
                var at = t.InverseTransformPoint(preview.transform.position);
                var turn = Quaternion.Inverse(t.rotation) * preview.transform.rotation;
                var spot = point == null ? Vector3.zero : t.InverseTransformPoint(point.transform.position);
                if (place(__instance.SelectedPartID, index, point == null ? -1 : point.ID, new[] { spot.x, spot.y, spot.z }, new[] { at.x, at.y, at.z, turn.x, turn.y, turn.z, turn.w }))
                {
                    Sound(__instance.SoundPreviewPlaced);
                    LanStorageGame.Notice("Чертёж отправлен хосту");
                }
                else LanStorageGame.Notice("Хост ещё отвечает на прошлый запрос");
            }
            catch (Exception ex) { _log?.Invoke("Placement hook failed: " + ex); }
            return false;
        }
        private static void Sound(AudioClip clip)
        {
            try { if (clip != null && GlobalSoundEffects.global != null) GlobalSoundEffects.global.PlayGlobalSound(clip); }
            catch (Exception) { }
        }

        // Holding attack with the right tool on a host blueprint swings the tool natively
        // (Gun.Repairs); the hit itself arrives in MeleeAssemblyTarget.
        private static bool GunShotPrefix(ZombieFighterFireArmWeapon __instance)
        {
            if (!Active) return true;
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                if (input == null || (int)input.CurrentState != 1) return true;
                bool attack = input.isKbMouseDevice ? input.isAttackActionPressed : input.gp_isAttackPressed;
                // 1.4.2: the game's disassembly (aim with a tool, or the gamepad's disassemble button).
                bool apart = input.isKbMouseDevice ? input.isAimPressed : input.isDisassemblePressed;
                if (!attack && !apart) return true;
                var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
                var gun = __instance.GetSelectedGun();
                if (tool == null || gun == null || gun.weaponType != 2) return true;
                if (attack)
                {
                    var target = Aim() ?? Repairable(AimBuilt());
                    if (target == null || !Fits(target, tool)) return true;
                    gun.Repairs();
                    return false;
                }
                var built = AimBuilt();
                if (built == null || !Fits(built, tool)) return true;
                gun.MeleeRightMouseButton();
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Build swing hook failed: " + ex.Message); return true; }
        }

        // The hit: the host's blueprint is built by the host, never the guest's copy.
        private static bool AssemblyPrefix(Gun __instance)
        {
            if (!Active) return true;
            try
            {
                var build = GuestBuild;
                var target = Aim() ?? Repairable(AimBuilt());
                var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
                if (build == null) { LanStorageGame.Notice("Строить на поезде хоста можно, когда хост сохраняет ваш профиль"); return false; }
                if (target == null || tool == null || !Fits(target, tool)) return false;
                int[][] needs = null;
                if (target.Blueprint && target.Kind == LanStorage.TrainPart)
                {
                    var part = PartPrefab(target.Prefab);
                    needs = part == null ? new int[0][] : LanStorageGame.Needs(part.Recipe);
                    string missing = Missing(needs);
                    if (missing != null) { LanStorageGame.Notice("Не хватает: " + missing); return false; }
                }
                if (build(target.Car, target.Kind, target.Identity, tool.itemID, needs))
                    LanWorldWork.GunWear.Invoke(__instance, new object[] { -1f }); // the tool wears as natively
            }
            catch (Exception ex) { _log?.Invoke("Build hit hook failed: " + ex.Message); }
            return false;
        }

        // 1.4.2: the disassembly hit of the guest's tool: the host takes its part or furniture apart (never
        // the guest's own copy, which would give it the recipe from nothing).
        private static bool DisassemblyPrefix(Gun __instance)
        {
            if (!Active) return true;
            try
            {
                var dismantle = GuestDismantle;
                var target = AimBuilt();
                var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
                if (dismantle == null) { LanStorageGame.Notice("Разбирать на поезде хоста можно, когда хост сохраняет ваш профиль"); return false; }
                bool attached;
                if (target == null || tool == null || !Fits(target, tool) || !CanTakeApart(target, out attached)) return false;
                if (dismantle(LanStorage.DismantleHit, target.Car, target.Kind, target.Identity, tool.itemID, Dismantled))
                    LanWorldWork.GunWear.Invoke(__instance, new object[] { -1f }); // the tool wears as natively
            }
            catch (Exception ex) { _log?.Invoke("Disassembly hit hook failed: " + ex.Message); }
            return false;
        }
        private static void Dismantled(byte status, bool done)
        {
            if (status == LanStorage.Ok) { if (done) LanStorageGame.Notice("Разобрано"); return; }
            if (status == LanStorage.Busy) return;
            // 1.4.11: in the words of taking apart (the storage words told of putting things away).
            LanStorageGame.Notice(status == LanStorage.NotAllowed ? "Это нельзя разобрать" : status == LanStorage.NotFound ? "Этого у хоста уже нет" : LanStorage.Describe(status));
        }

        // 1.4.2: a furniture kit placed from the belt (GlobalCraftController): the guest's own copy of the
        // train must not get it; the kit goes to the host, which places the unassembled furniture.
        private static void FurniturePrefix()
        {
            _furnitureBefore = null; _kit = null; _kitSource = null;
            if (!Active) return;
            // Only the frame of the game's own "place" press (keyboard: attack; gamepad: use released).
            var input = Zompiercer.Inputs.InputController.Global;
            if (input == null || !(input.isKbMouseDevice ? input.isAttackActionDown : input.gp_isUseOpenTalkUp)) return;
            try
            {
                var manager = GlobalManager.global;
                var train = manager == null ? null : manager.controlledTrain;
                if (train == null || train.Cars == null || InventoryController.global == null) return;
                var counts = new int[train.Cars.Length];
                for (int i = 0; i < counts.Length; i++)
                {
                    var car = TrainLayout.GetCar(train, i);
                    counts[i] = car.AllFurniture == null ? 0 : car.AllFurniture.Count;
                }
                var belt = manager.controlledChar.beltInventory;
                int slot = InventoryController.global.selectedBeltIcon;
                var kit = belt != null && slot >= 0 && slot < belt.Length && belt[slot] != null && belt[slot].content != null && belt[slot].content.Count > 0 ? belt[slot].content[0] : null;
                if (kit == null) return;
                _kit = LanStorage.Normalize(new object[] { kit.itemID, 1, kit.Save() });
                _kitSource = kit.inInventory;
                _furnitureBefore = counts;
            }
            catch (Exception) { _furnitureBefore = null; }
        }
        private static void FurniturePostfix()
        {
            var before = _furnitureBefore; _furnitureBefore = null;
            if (before == null) return;
            try
            {
                var train = GlobalManager.global.controlledTrain;
                for (int i = 0; i < before.Length; i++)
                {
                    var car = TrainLayout.GetCar(train, i);
                    if (car.AllFurniture == null || car.AllFurniture.Count <= before[i]) continue;
                    var placed = car.AllFurniture[car.AllFurniture.Count - 1];
                    var at = car.transform.InverseTransformPoint(placed.transform.position);
                    var turn = Quaternion.Normalize(Quaternion.Inverse(car.transform.rotation) * placed.transform.rotation);
                    car.AllFurniture.Remove(placed);
                    placed.InTrainCar = null;
                    Object.Destroy(placed.gameObject);
                    var kit = _kit; var source = _kitSource;
                    Action<object[]> giveBack = back => GiveBack(source, back);
                    var place = GuestFurniture;
                    if (place == null || kit == null || !place(kit, i, new[] { at.x, at.y, at.z, turn.x, turn.y, turn.z, turn.w }, FurnitureAnswered, giveBack))
                    {
                        if (kit != null) { try { giveBack(kit); } catch (Exception ex) { _log?.Invoke("Furniture kit not given back: " + ex.Message); } }
                        LanStorageGame.Notice(place == null ? "Ставить мебель на поезд хоста можно, когда хост сохраняет ваш профиль" : "Хост ещё отвечает на прошлые запросы");
                    }
                    return;
                }
            }
            catch (Exception ex) { _log?.Invoke("Furniture placement hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void FurnitureAnswered(byte status)
        {
            if (status == LanStorage.Ok) LanStorageGame.Notice("Мебель поставлена у хоста: соберите её инструментом");
            else LanStorageGame.Notice("Хост не поставил мебель: " + LanStorage.Describe(status));
        }
        // What did not go to the host goes back where it was (the belt), or else into the backpack.
        private static void GiveBack(Inventory source, object[] entry)
        {
            if (source != null)
            {
                var instance = LanStorageGame.Create(entry);
                bool stored = false;
                try { stored = source.Add(instance, true); }
                finally { if (!stored && instance != null) Object.Destroy(instance.gameObject); }
                if (stored) return;
            }
            new LanStorageGuestWorld().Give(entry);
        }

        // ---- Host (1.4.2) ----

        // The unassembled furniture of kit `kit` where the guest placed it, as GlobalCraftController does.
        internal static byte HostFurniture(int kit, int carIndex, float[] pose, Vector3? guest)
        {
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var scenes = GlobalSceneManager.global;
            if (train == null || scenes == null || scenes.SceneCurrentlyLoading) return LanStorage.Unavailable;
            if (guest == null) return LanStorage.Lost;
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            var item = prefabs != null && kit >= 0 && kit < prefabs.Length ? prefabs[kit] : null;
            var prefab = item == null ? null : item.craftObjectPrefab;
            if (prefab == null) return LanStorage.NotAllowed;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return LanStorage.NotFound; }
            var at = car.transform.TransformPoint(new Vector3(pose[0], pose[1], pose[2]));
            var rotation = car.transform.rotation * Quaternion.Normalize(new Quaternion(pose[3], pose[4], pose[5], pose[6]));
            if (Vector3.Distance(at, guest.Value) > 8f) return LanStorage.TooFar;
            var placed = Object.Instantiate(prefab);
            placed.transform.position = at;
            placed.transform.rotation = rotation;
            placed.gameObject.transform.SetParent(car.transform);
            placed.notAssembled = true;
            placed.TurnInBlueprint();
            placed.InTrainCar = car;
            car.AllFurniture.Add(placed);
            try { GlobalSoundEffects.global.CraftedObjectPreinstallation(); } catch (Exception) { }
            return LanStorage.Ok;
        }

        // One disassembly hit of the guest's tool, as Gun.MeleeDisassemblyTarget does it, except that
        // what taking it apart gives goes to the guest (the game would give it to the host's own player);
        // or a blueprint part taken away, as the game's own "Remove" on a blueprint.
        internal static byte HostDismantle(int mode, int carIndex, int owner, int identity, int tool, Vector3? guest, out object[] given, out bool done)
        {
            given = null; done = false;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var scenes = GlobalSceneManager.global;
            if (train == null || scenes == null || scenes.SceneCurrentlyLoading) return LanStorage.Unavailable;
            if (guest == null) return LanStorage.Lost;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return LanStorage.NotFound; }
            Component target = null;
            if (owner == LanStorage.TrainFurniture)
            {
                if (car.AllFurniture != null) foreach (var f in car.AllFurniture) if (f != null && f.GetInstanceID() == identity) { target = f; break; }
            }
            else foreach (var part in car.GetComponentsInChildren<TrainPart>(true)) if (part != null && part.GetInstanceID() == identity) { target = part; break; }
            if (target == null) return LanStorage.NotFound;
            if (Distance(target, guest.Value) > LanStorageGame.Reach + 1.5f) return LanStorage.TooFar;
            if (mode == LanStorage.DismantleBlueprint)
            {
                var blueprint = target as TrainPart;
                if (blueprint == null || !blueprint.Blueprint) return LanStorage.NotAllowed;
                blueprint.Remove();
                done = true;
                return LanStorage.Ok;
            }
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            var toolPrefab = prefabs != null && tool < prefabs.Length ? prefabs[tool] : null;
            var gun = LanWorldWork.HostGun(tool);
            if (toolPrefab == null || gun == null || gun.furnitureBuildPower <= 0f) return LanStorage.NotAllowed;
            float power = gun.furnitureBuildPower;
            var furniture = target as Furniture;
            if (furniture != null)
            {
                if (furniture.notAssembled || furniture.Unbreakable || !ToolFits(furniture, toolPrefab)) return LanStorage.NotAllowed;
                var inside = furniture.GetComponent<Inventory>();
                if (inside != null && inside.content != null && inside.content.Count > 0) return LanStorage.NotEmpty;
                if (furniture.CompletionBarCurrent - power > 0f) { furniture.AddCompletionBar(-power); return LanStorage.Ok; }
                // Taken apart: its kit, to the guest (Furniture.Update would give it to the host's own player).
                given = new object[] { LanStorage.Normalize(new object[] { furniture.itemData, 1, new object[4] }) };
                if (furniture.snapPoint != null) furniture.snapPoint.occupiedBy = null;
                Object.Destroy(furniture.gameObject);
                try { GlobalSoundEffects.global.PickUpItem(); } catch (Exception) { }
                done = true;
                return LanStorage.Ok;
            }
            var built = (TrainPart)target;
            if (built.Blueprint || !built.IsBuilt() || Mathf.RoundToInt(built.RequiredTool) != toolPrefab.GetToolType()) return LanStorage.NotAllowed;
            if (!built.IsCanBeDeconstructed()) return LanStorage.NotAllowed; // 1.4.11: built parts are attached to it
            var storage = built.GetComponentInChildren<Inventory>();
            if (storage != null && storage.content != null && storage.content.Count > 0) return LanStorage.NotEmpty;
            var holders = built.takableHoldersController;
            if (holders != null && holders.Holders != null) foreach (var holder in holders.Holders) if (holder != null && holder.AttachedItem != null) return LanStorage.NotEmpty;
            float next = built.BuildPointsCurrent - power;
            if (next > 0f) { built.BuildPointsCurrent = next; return LanStorage.Ok; }
            // Taken apart: its recipe, to the guest (TrainPart.ConstructWithTool gives it to the host's own player).
            var recipe = LanStorageGame.Needs(built.Recipe);
            built.BuildPointsCurrent = 0f;
            built.Remove();
            var back = new List<object>();
            foreach (var need in recipe) if (need[1] > 0 && back.Count < LanStorage.MaxRefundItems) back.Add(LanStorage.Normalize(new object[] { need[0], need[1], new object[4] }));
            given = back.ToArray();
            done = true;
            return LanStorage.Ok;
        }
        private static float Distance(Component target, Vector3 guest)
        {
            float best = Vector3.Distance(target.transform.position, guest);
            foreach (var collider in target.GetComponentsInChildren<Collider>(false))
                if (collider != null && collider.enabled && !collider.isTrigger) best = Mathf.Min(best, Vector3.Distance(collider.bounds.ClosestPoint(guest), guest));
            return best;
        }

        // ---- Guest: look-at hints and pouring into a copied tank ----
        // True when the crosshair is on a host blueprint or a host fuel tank copy.
        internal static bool Tick(LanStorageClient client)
        {
            var input = Zompiercer.Inputs.InputController.Global;
            if (input == null || client == null) return false;
            RaycastHit hit;
            if (!LookHit(out hit)) return false;
            // 1.2.2: a bed of the host's train, as the game's own bed (sleeping together, LanSleep).
            if (hit.collider.GetComponentInParent<SleepController>() == null && TrainLayout.TryIdentifyBed(hit.collider)) return LanSleep.LookAtBed(input);
            // 1.4.0: a sign of the host's train: the game's own text window, the host changes it.
            Component sign; string signText;
            if (TrainLayout.TryIdentifySign(hit.collider, out sign, out signText))
            {
                bool handled = LanTrainDecor.LookAtSign(input, hit.collider, signText);
                // 1.4.13: a cabinet with a name plate: the plate is edited with its own key, and the use key still
                // opens the cabinet (the look goes on to its storage), as in the game.
                int storageCar, storageKind, storageId;
                if (!TrainLayout.TryIdentifyStorage(hit.collider, out storageCar, out storageKind, out storageId)) return handled;
            }
            int carIndex;
            if (hit.collider.GetComponentInParent<TrainFuelTank>() == null && TrainLayout.TryIdentifyFuelTank(hit.collider, out carIndex))
            {
                var can = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
                if (can == null || can.itemID != LanWorldWork.FullCanister) return false;
                var info = Hint();
                if (info != null)
                {
                    info.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey("Fuel").ToUpper());
                    info.SetIconText(Zompiercer.GUI.LocalizationCore.GetTextByKey("Refuel").ToUpper(), (Zompiercer.Inputs.Actions)RefuelAction, (Zompiercer.Inputs.Actions)0, false);
                }
                bool pour = input.isKbMouseDevice ? input.isAttackActionDown : input.gp_isUseOpenTalkDown;
                if (pour && !client.Busy) PourInto(carIndex, hit.point, can);
                return true;
            }
            var target = TrainLayout.TryIdentifyBuild(hit.collider);
            if (target == null) return LookAtBuilt(input, hit.collider);
            var hint = Hint();
            if (hint == null) return true;
            // 1.4.2: as the game's own "Remove" on a blueprint (the use key), through the host.
            if (target.Kind == LanStorage.TrainPart)
            {
                hint.SetIconText("Remove", (Zompiercer.Inputs.Actions)RemoveAction, (Zompiercer.Inputs.Actions)0, true);
                if (input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isRemoveBuildItemDown)
                {
                    input.isUseKeyDown = false; input.gp_isRemoveBuildItemDown = false;
                    var dismantle = GuestDismantle;
                    if (dismantle == null) LanStorageGame.Notice("Убирать чертежи на поезде хоста можно, когда хост сохраняет ваш профиль");
                    else if (!dismantle(LanStorage.DismantleBlueprint, target.Car, target.Kind, target.Identity, -1, Dismantled)) LanStorageGame.Notice("Хост ещё отвечает на прошлые запросы");
                }
            }
            var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
            float max = target.Kind == LanStorage.TrainPart ? (PartPrefab(target.Prefab) == null ? 0f : PartPrefab(target.Prefab).BuildPointsMaximum)
                : (FurniturePrefab(target.Prefab) == null ? 0f : FurniturePrefab(target.Prefab).completionBarMax);
            var text = new StringBuilder(Title(target).ToUpper());
            if (max > 0f) text.Append("  ").Append(Mathf.Clamp(Mathf.RoundToInt(100f * target.Progress / max), 0, 100)).Append('%');
            if (target.Kind == LanStorage.TrainPart)
            {
                var part = PartPrefab(target.Prefab);
                var needs = part == null ? new int[0][] : LanStorageGame.Needs(part.Recipe);
                if (needs.Length > 0) text.Append('\n').Append(Recipe(needs));
            }
            if (tool == null || !Fits(target, tool))
            {
                hint.color = GlobalSoundEffects.global != null ? GlobalSoundEffects.global.colorRed : hint.color;
                text.Append('\n').Append(Zompiercer.GUI.LocalizationCore.GetTextByKey("WrongTool").ToUpper());
                hint.SetText(text.ToString());
                return true;
            }
            hint.SetText(text.ToString());
            hint.SetIconText("Build", (Zompiercer.Inputs.Actions)BuildAction, (Zompiercer.Inputs.Actions)0, true);
            return true;
        }

        // 1.4.2: a built host part or assembled furniture with a fitting tool in hand: the game's own hints
        // to take it apart (and to repair it when it is not whole). Other uses of the object go on.
        private static bool LookAtBuilt(Zompiercer.Inputs.InputController input, Collider collider)
        {
            var target = TrainLayout.TryIdentifyBuild(collider, true);
            var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
            if (target == null || tool == null || !Fits(target, tool)) return false;
            var hint = Hint();
            if (hint == null) return false;
            // 1.4.11: as the game's own hint: unbreakable furniture offers nothing, a part with built parts on it says so.
            bool attached;
            if (!CanTakeApart(target, out attached))
            {
                if (!attached) return false;
                hint.color = GlobalSoundEffects.global != null ? GlobalSoundEffects.global.colorRed : hint.color;
                hint.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey("There is attached parts").ToUpper());
                input.canDisassemble = false;
                return false;
            }
            float max = MaxProgress(target);
            hint.color = GlobalSoundEffects.global != null ? GlobalSoundEffects.global.colorWhite : hint.color;
            if (max > 0f && target.Progress < max)
            {
                hint.SetText(Title(target).ToUpper() + "  " + Mathf.Clamp(Mathf.RoundToInt(100f * target.Progress / max), 0, 100) + "%");
                hint.SetIconText("Repair", (Zompiercer.Inputs.Actions)RepairAction, (Zompiercer.Inputs.Actions)0, true);
            }
            hint.SetIconText("Disassemble", (Zompiercer.Inputs.Actions)DisassembleAction, (Zompiercer.Inputs.Actions)0, true);
            input.canDisassemble = true;
            return false;
        }
        private static float MaxProgress(TrainLayout.BuildTarget target)
        {
            if (target.Kind == LanStorage.TrainPart) { var part = PartPrefab(target.Prefab); return part == null ? 0f : part.BuildPointsMaximum; }
            var furniture = FurniturePrefab(target.Prefab);
            return furniture == null ? 0f : furniture.completionBarMax;
        }

        private static void PourInto(int carIndex, Vector3 point, InventoryItem can)
        {
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return; }
            var slot = can.inInventory;
            if (slot == null) return;
            var escrow = LanStorage.Normalize(new object[] { can.itemID, 1, can.Save() });
            slot.Extract(can, 1);
            if (!LanWorldWork.Pour(car, point, escrow)) new LanStorageGuestWorld().Give(escrow);
        }

        // ---- Helpers ----
        internal static bool LookHit(out RaycastHit best)
        {
            best = default(RaycastHit);
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var camera = Camera.main;
            if (player == null || camera == null) return false;
            bool any = false;
            foreach (var hit in Physics.RaycastAll(new Ray(camera.transform.position, camera.transform.forward), Look, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(player.transform)) continue;
                if (!any || hit.distance < best.distance) { best = hit; any = true; }
            }
            return any;
        }
        private static TrainLayout.BuildTarget Aim()
        {
            RaycastHit hit;
            return LookHit(out hit) ? TrainLayout.TryIdentifyBuild(hit.collider) : null;
        }
        // 1.4.2: a built host part or assembled furniture in front of the guest.
        private static TrainLayout.BuildTarget AimBuilt()
        {
            RaycastHit hit;
            return LookHit(out hit) ? TrainLayout.TryIdentifyBuild(hit.collider, true) : null;
        }
        // A built object that is not whole (partly taken apart) can be repaired with the tool.
        private static TrainLayout.BuildTarget Repairable(TrainLayout.BuildTarget target)
        {
            if (target == null) return null;
            float max = MaxProgress(target);
            return max > 0f && target.Progress < max - .001f ? target : null;
        }

        private static bool Fits(TrainLayout.BuildTarget target, InventoryItem tool)
        {
            if (target.Kind == LanStorage.TrainPart)
            {
                var part = PartPrefab(target.Prefab);
                return part != null && Mathf.RoundToInt(part.RequiredTool) == tool.GetToolType();
            }
            var furniture = ShownFurniture(target);
            return furniture != null && ToolFits(furniture, tool);
        }
        // 1.4.11: whether this player's game shows blueprints now (a tool in hand or a part being planned:
        // TrainConstructor._displayingBlueprintsNow); its blueprints are seen and solid only then.
        private static AccessTools.FieldRef<TrainConstructor, bool> _blueprintsShown;
        private static bool _blueprintsShownFailed;
        internal static bool BlueprintsShownNow()
        {
            var constructor = TrainConstructor.Global;
            if (constructor == null || _blueprintsShownFailed) return true;
            try
            {
                if (_blueprintsShown == null) _blueprintsShown = AccessTools.FieldRefAccess<TrainConstructor, bool>("_displayingBlueprintsNow");
                return _blueprintsShown(constructor);
            }
            catch (Exception ex) { _blueprintsShownFailed = true; _log?.Invoke("Blueprint visibility unavailable: " + ex.Message); return true; }
        }

        // 1.4.11: the game's rule for furniture: a wood tool for wooden furniture, a metal tool for metal
        // furniture (a piece that is both takes either; Furniture.GetToolType names only the first).
        internal static bool ToolFits(Furniture furniture, InventoryItem tool)
        {
            return furniture != null && tool != null && (tool.toolWood && furniture.furnitureWood || tool.toolMetal && furniture.furnitureMetal);
        }
        // The guest's own native furniture when the host's is shown by it (its flags are its own), else the kit.
        private static Furniture ShownFurniture(TrainLayout.BuildTarget target)
        {
            var native = target.Shown as Furniture;
            return native != null ? native : FurniturePrefab(target.Prefab);
        }
        // 1.4.11: whether the host would take it apart (HostDismantle): furniture that is not unbreakable;
        // a part with no built part on its own mount points (attached says which refused it).
        private static bool CanTakeApart(TrainLayout.BuildTarget target, out bool attached)
        {
            attached = false;
            if (target.Kind != LanStorage.TrainPart) { var furniture = ShownFurniture(target); return furniture != null && !furniture.Unbreakable; }
            attached = TrainLayout.HasAttachedParts(target, PartPrefab);
            return !attached;
        }

        private static TrainPart PartPrefab(int id)
        {
            var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
            return parts != null && parts.Parts != null && id >= 0 && id < parts.Parts.Count ? parts.Parts[id] : null;
        }
        private static Furniture FurniturePrefab(int id)
        {
            var db = ItemsDataBase.Global;
            return db != null && db.AllFurniture != null && id >= 0 && id < db.AllFurniture.Count ? db.AllFurniture[id] : null;
        }
        private static string Title(TrainLayout.BuildTarget target)
        {
            try
            {
                if (target.Kind == LanStorage.TrainPart)
                {
                    var part = PartPrefab(target.Prefab);
                    if (part != null && !string.IsNullOrEmpty(part.Name)) return Zompiercer.GUI.LocalizationCore.GetTextByKey(part.Name);
                }
                else
                {
                    var furniture = FurniturePrefab(target.Prefab);
                    if (furniture != null) return furniture.name.Replace("(Clone)", "");
                }
            }
            catch (Exception) { }
            return Zompiercer.GUI.LocalizationCore.GetTextByKey("Build");
        }

        private static long Have(int id)
        {
            var backpack = LanStorageGame.Backpack();
            long total = 0;
            if (backpack != null && backpack.content != null) foreach (var item in backpack.content) if (item != null && item.itemID == id) total += item.amount;
            return total;
        }
        // "Name have/need, ..." for what is short, or null when the backpack has everything.
        private static string Missing(int[][] needs)
        {
            var text = new StringBuilder();
            foreach (var need in needs)
            {
                long have = Have(need[0]);
                if (have >= need[1]) continue;
                if (text.Length > 0) text.Append(", ");
                text.Append(LanStorageGame.ItemName(need[0])).Append(' ').Append(have).Append('/').Append(need[1]);
            }
            return text.Length == 0 ? null : text.ToString();
        }
        private static string Recipe(int[][] needs)
        {
            var text = new StringBuilder();
            foreach (var need in needs)
            {
                if (text.Length > 0) text.Append("  ");
                text.Append(LanStorageGame.ItemName(need[0]).ToUpper()).Append(' ').Append(Math.Min(Have(need[0]), need[1])).Append('/').Append(need[1]);
            }
            return text.ToString();
        }

        private static Zompiercer.GUI.SimpleInfoDisplayer Hint()
        {
            var controller = InventoryController.global;
            return controller == null ? null : controller.simpleInfoDisplayer;
        }
    }
}
