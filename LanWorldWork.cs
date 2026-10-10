using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Work on the world through the host (0.7.0): melee hits on resource objects,
    // gathering with a knife, digging with a shovel, filling a canister at a gas
    // station; 0.8.0: pouring a full canister into a fuel tank of the host's train. The guest's own copy of these objects is never used; the host acts on
    // its objects with its own copy of the guest's tool and hands out the result. 1.2.2: filling
    // empty bottles at, and pouring dirty water into, the rainwater tank of the host's train.
    internal static class LanWorldWork
    {
        internal const int EmptyCanister = 92, FullCanister = 8, Sand = 89, Soil = 296;
        // The game's empty plastic bottle and bottle of dirty water (InventoryController); 0.5 l each.
        internal const int EmptyBottle = 10, DirtyBottle = 61;
        internal const float BottleVolume = .5f;
        private const int Flower = 2;
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Guest: sends a work request; null when no usable session is attached.
        internal static Func<int, Vector3, int, int, object[], bool> GuestWork;

        private static readonly FieldInfo GunResource = AccessTools.Field(typeof(Gun), "resourceContent");
        private static readonly FieldInfo GunCollected = AccessTools.Field(typeof(Gun), "collectedResource");
        private static readonly FieldInfo GunFurniture = AccessTools.Field(typeof(Gun), "HitFurniture");
        private static readonly FieldInfo GunPart = AccessTools.Field(typeof(Gun), "HitTrainPart");
        private static readonly FieldInfo GunHit = AccessTools.Field(typeof(Gun), "raycastHit");
        internal static readonly MethodInfo GunWear = AccessTools.Method(typeof(Gun), "SubtractWeaponDurability");
        private static readonly FieldInfo SpawnedItem = AccessTools.Field(typeof(CollectedResource), "spawnedItemID");
        private static readonly FieldInfo FillingTank = AccessTools.Field(typeof(InventoryController), "CurrentlyFillingFuelTank");

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.world-work");
            try
            {
                if (GunResource == null || GunCollected == null || GunFurniture == null || GunPart == null || GunHit == null || GunWear == null || SpawnedItem == null || FillingTank == null)
                    throw new MissingMemberException("Gun/CollectedResource members changed");
                _harmony.Patch(AccessTools.Method(typeof(Gun), "MeleeDisassemblyTarget"), prefix: new HarmonyMethod(typeof(LanWorldWork), nameof(MeleePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "CheckGasStationAndFillGasoline"), prefix: new HarmonyMethod(typeof(LanWorldWork), nameof(FuelPrefix)));
                // The game's own "refuel from a canister" ends in these two calls.
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "GetResourcesFromItem"), prefix: new HarmonyMethod(typeof(LanWorldWork), nameof(PourPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(TrainFuelTank), "Fill"), prefix: new HarmonyMethod(typeof(LanWorldWork), nameof(TankFillPrefix)));
                // 1.2.2: bottles at the host's rainwater tank, before the game uses the bottle in hand.
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "Update"), prefix: new HarmonyMethod(typeof(LanWorldWork), nameof(WaterPrefix)));
            }
            catch (Exception ex)
            {
                _harmony.UnpatchSelf();
                Failure = ex.GetType().Name + ": " + ex.Message;
                log("World work hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; GuestWork = null; _pouring = null; }

        // Guest: a melee hit on its own copy of a resource becomes a request to the host.
        private static bool MeleePrefix(Gun __instance)
        {
            if (!LanSaveIsolation.Active) return true;
            var work = GuestWork;
            if (work == null) return true; // solo or not attached: the guest's own world
            try
            {
                __instance.DoRaycast();
                var tool = InventoryController.global == null ? null : InventoryController.global.GetActiveIntenvoryItem();
                if (tool == null) return false;
                // The host's train and furniture are never taken apart from here.
                if (GunFurniture.GetValue(__instance) != null || GunPart.GetValue(__instance) != null) return false;
                var resource = GunResource.GetValue(__instance) as ResourceContent;
                var collected = GunCollected.GetValue(__instance) as CollectedResource;
                bool sent = false;
                if (resource != null) sent = work(LanStorage.WorkHit, resource.transform.position, tool.itemID, 0, null);
                else if (tool.toolShovel)
                {
                    var detector = Zompiercer.Farm.TerrainResourceDetector.Global;
                    int type = detector == null ? 0 : (int)detector.ResourceType;
                    var hit = (RaycastHit)GunHit.GetValue(__instance);
                    if ((type == 1 || type == 2) && hit.collider != null) sent = work(LanStorage.WorkDig, hit.point, tool.itemID, type, null);
                }
                else if (tool.toolKnife && collected != null) sent = work(LanStorage.WorkGather, collected.transform.position, tool.itemID, 0, null);
                // The tool wears as natively; the host wears its ledger copy alike.
                if (sent) GunWear.Invoke(__instance, new object[] { -1f });
            }
            catch (Exception ex) { _log?.Invoke("Melee work hook failed: " + ex.Message); }
            return false;
        }

        // Guest: filling a canister at a gas station is the host's fuel.
        private static bool FuelPrefix(InventoryController __instance)
        {
            if (!LanSaveIsolation.Active) return true;
            var work = GuestWork;
            if (work == null) return true;
            try
            {
                var station = SelectionManager.Global == null ? null : SelectionManager.Global.HoveredGasStation;
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (station == null || player == null || player.beltInventory == null) return false;
                var slot = player.beltInventory[__instance.selectedBeltIcon];
                var can = slot == null || slot.content == null || slot.content.Count == 0 ? null : slot.content[0];
                if (can == null || can.itemID != EmptyCanister) return false;
                var escrow = LanStorage.Normalize(new object[] { can.itemID, 1, can.Save() });
                slot.Extract(can, 1);
                if (!work(LanStorage.WorkFuel, station.transform.position, -1, 0, escrow))
                    new LanStorageGuestWorld().Give(escrow);
            }
            catch (Exception ex) { _log?.Invoke("Fuel hook failed: " + ex.Message); }
            return false;
        }

        // ---- Guest: refuelling the host's train ----
        private static TrainFuelTank _pouring;

        // The game has taken the full canister and now wants to give back the empty one:
        // the canister goes to the host instead, which fills its own tank and hands back the empty one.
        private static bool PourPrefix(InventoryController __instance, InventoryItem fromItem)
        {
            var tank = FillingTank.GetValue(__instance) as TrainFuelTank;
            if (!LanSaveIsolation.Active || GuestWork == null || tank == null || fromItem == null || fromItem.itemID != FullCanister) return true;
            object[] escrow = null;
            try
            {
                escrow = LanStorage.Normalize(new object[] { fromItem.itemID, 1, fromItem.Save() });
                if (!Pour(tank, tank.transform.position, escrow)) new LanStorageGuestWorld().Give(escrow);
            }
            catch (Exception ex)
            {
                _log?.Invoke("Refuel hook failed: " + ex.Message);
                if (escrow != null) { try { new LanStorageGuestWorld().Give(escrow); } catch (Exception) { } }
            }
            return false;
        }
        // The guest's own copy of a tank on the host's train never fills by itself.
        private static bool TankFillPrefix(TrainFuelTank __instance, float value)
        {
            if (!LanSaveIsolation.Active || GuestWork == null || value <= 0f) return true;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            return train == null || !__instance.transform.IsChildOf(train.transform);
        }

        // Sends a full canister (already out of the inventory) to the tank at `point` of the host's train.
        internal static bool Pour(TrainFuelTank shown, Vector3 point, object[] escrow)
        {
            var work = GuestWork;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var car = shown != null ? shown.GetComponentInParent<Zompiercer.Train.TrainCar>() : null;
            if (car == null && train != null)
                foreach (var hit in Physics.OverlapSphere(point, .1f, ~0, QueryTriggerInteraction.Ignore))
                    if ((car = hit.GetComponentInParent<Zompiercer.Train.TrainCar>()) != null) break;
            int index = TrainLayout.CarIndex(train, car);
            if (work == null || index < 0) { LanStorageGame.Notice("Заправлять можно только поезд хоста"); return false; }
            var local = car.transform.InverseTransformPoint(point);
            if (!work(LanStorage.WorkRefuel, local, -1, index, escrow)) return false;
            _pouring = shown;
            return true;
        }
        internal static bool Pour(Zompiercer.Train.TrainCar car, Vector3 point, object[] escrow)
        {
            var work = GuestWork;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            int index = TrainLayout.CarIndex(train, car);
            if (work == null || index < 0) return false;
            _pouring = null;
            return work(LanStorage.WorkRefuel, car.transform.InverseTransformPoint(point), -1, index, escrow);
        }

        // Guest: the host's answer to a refuel, its tank's level x100; shown on the guest's own copy.
        internal static void Refuelled(int level)
        {
            var tank = _pouring; _pouring = null;
            if (tank != null) tank.currentFuelAmount = Mathf.Clamp(level / 100f, 0f, tank.maxFuelAmount);
            LanStorageGame.Notice("Бак поезда хоста: " + Mathf.RoundToInt(level / 100f));
        }

        // ---- Guest: the rainwater tank of the host's train (1.2.2) ----
        // The guest's copy of the tank has no script: with an empty bottle (or one of dirty water) in
        // hand and the copy in sight, the game's own keys fill one or every bottle (pour one or all);
        // the host does it at its own tank. The level comes from the host (a read every 1.5 s in sight).
        private static float _waterLevel = -1f, _waterReadAt = -100f;
        private static int _waterCar = -1;
        private static void WaterPrefix(InventoryController __instance)
        {
            if (!LanSaveIsolation.Active) return;
            var work = GuestWork;
            if (work == null) return;
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (input == null || player == null || (int)input.CurrentState != 1 || __instance.consumeSomething || player.beltInventory == null) return;
                RaycastHit hit; int car; float max;
                if (!LanTrainBuild.LookHit(out hit) || !TrainLayout.TryIdentifyWaterTank(hit.collider, out car, out max)) return;
                var train = GlobalManager.global.controlledTrain;
                var carObject = TrainLayout.GetCar(train, car);
                var local = carObject.transform.InverseTransformPoint(hit.point);
                float now = Time.unscaledTime;
                if (car != _waterCar) { _waterCar = car; _waterLevel = -1f; }
                if (now - _waterReadAt > 1.5f && work(LanStorage.WorkWaterLevel, local, -1, car, null)) _waterReadAt = now;
                var info = __instance.simpleInfoDisplayer;
                if (info != null)
                    info.SetText((_waterLevel < 0f ? "…" : Math.Round(_waterLevel, 1).ToString()) + " / " + max + " " + Zompiercer.GUI.LocalizationCore.GetTextByKey("DirtyWater").ToUpper());
                var slot = player.beltInventory[__instance.selectedBeltIcon];
                var bottle = slot == null || slot.content == null || slot.content.Count == 0 ? null : slot.content[0];
                if (bottle == null || bottle.itemID != EmptyBottle && bottle.itemID != DirtyBottle) return;
                bool fill = bottle.itemID == EmptyBottle;
                if (info != null)
                {
                    info.color = GlobalSoundEffects.global.colorGreen;
                    info.SetIconText(fill ? "TakeWater" : "Pour out the water", (Zompiercer.Inputs.Actions)2, (Zompiercer.Inputs.Actions)0, true);
                    if (bottle.amount > 1) info.SetIconText(fill ? "Fill everything with water" : "Pour out all the water", (Zompiercer.Inputs.Actions)3, (Zompiercer.Inputs.Actions)0, true);
                }
                bool one = input.isKbMouseDevice ? input.isUiMainActionDown : input.gp_isAttackDown;
                bool all = input.isKbMouseDevice ? input.isUiSecondDown : input.gp_isAimDown;
                if (!one && !all) return;
                // The game would use (or take apart) the bottle in hand: this press is the tank's.
                input.isUiMainActionDown = false; input.isUiSecondDown = false; input.gp_isAttackDown = false; input.gp_isAimDown = false;
                int count = all ? bottle.amount : 1;
                if (fill)
                {
                    count = Math.Min(count, Mathf.FloorToInt(player.inventory.GetFreeMass() / BottleVolume));
                    if (_waterLevel >= 0f) count = Math.Min(count, Mathf.FloorToInt(_waterLevel / BottleVolume + .001f));
                    if (count < 1) { LanStorageGame.Notice(_waterLevel >= 0f && _waterLevel < BottleVolume ? Zompiercer.GUI.LocalizationCore.GetTextByKey("NotEnoughWater") : "Не хватает места в рюкзаке"); return; }
                }
                else if (_waterLevel >= 0f) count = Math.Min(count, Mathf.FloorToInt((max - _waterLevel) / BottleVolume + .001f));
                if (count < 1) { LanStorageGame.Notice(Zompiercer.GUI.LocalizationCore.GetTextByKey("The tank is full")); return; }
                count = Math.Min(count, LanStorage.MaxWaterBottles);
                var escrow = LanStorage.Normalize(new object[] { bottle.itemID, count, bottle.Save() });
                slot.Extract(bottle, count);
                if (work(fill ? LanStorage.WorkWater : LanStorage.WorkPourWater, local, -1, car, escrow))
                {
                    _waterReadAt = now;
                    if (player.zombieFighterEffects != null) player.zombieFighterEffects.RandomFillingBottleSound();
                }
                else new LanStorageGuestWorld().Give(escrow);
            }
            catch (Exception ex) { _log?.Invoke("Water hook failed: " + ex.Message); }
        }

        // Guest: the host's answer about its rainwater tank, its level x100.
        internal static void Watered(int kind, int level)
        {
            _waterLevel = level / 100f;
            if (kind != LanStorage.WorkWaterLevel) LanStorageGame.Notice("Бак с водой хоста: " + Math.Round(_waterLevel, 1));
        }

        // ---- Host ----
        internal static byte HostWork(int kind, Vector3 at, int tool, int extra, object[] escrow, Vector3? guest, out object[] given, out int detail)
        {
            given = null; detail = 0;
            if (kind == LanStorage.WorkRefuel) return HostRefuel(at, extra, escrow, guest, out given, out detail);
            if (kind >= LanStorage.WorkWater) return HostWater(kind, at, extra, escrow, guest, out given, out detail);
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(at, guest.Value) > 5f) return LanStorage.TooFar;
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            if (prefabs == null) return LanStorage.Unavailable;
            var toolPrefab = tool >= 0 && tool < prefabs.Length ? prefabs[tool] : null;
            var gun = tool >= 0 ? HostGun(tool) : null;
            switch (kind)
            {
                case LanStorage.WorkHit:
                {
                    if (gun == null || gun.furnitureBuildPower <= 0) return LanStorage.NotAllowed;
                    var target = Nearest<ResourceContent>(at, .3f);
                    if (target == null) return LanStorage.NotFound;
                    float power = gun.furnitureBuildPower;
                    if (target.durability <= power) target.ObjectParsed(); // what falls out lies in this world
                    target.durability -= power;
                    return LanStorage.Ok;
                }
                case LanStorage.WorkGather:
                {
                    if (toolPrefab == null || !toolPrefab.toolKnife || gun == null) return LanStorage.NotAllowed;
                    var resource = Nearest<CollectedResource>(at, .3f);
                    if (resource == null || !resource.gameObject.activeInHierarchy) return LanStorage.NotFound;
                    if ((int)resource.ResourceType != Flower) return LanStorage.NotAllowed;
                    int id = (int)SpawnedItem.GetValue(resource), amount = Math.Max(1, gun.resourceExtractionPower);
                    if (id < 0 || id >= prefabs.Length || prefabs[id] == null) return LanStorage.Invalid;
                    given = new object[] { id, amount, new object[4] };
                    resource.resourceQuantity -= amount;
                    if (resource.resourceQuantity <= 0) resource.gameObject.SetActive(false);
                    return LanStorage.Ok;
                }
                case LanStorage.WorkDig:
                {
                    if (toolPrefab == null || !toolPrefab.toolShovel || gun == null) return LanStorage.NotAllowed;
                    int id = extra == 1 ? Sand : Soil;
                    if (id >= prefabs.Length || prefabs[id] == null) return LanStorage.Invalid;
                    given = new object[] { id, Math.Max(1, gun.resourceExtractionPower), new object[4] };
                    return LanStorage.Ok;
                }
                case LanStorage.WorkFuel:
                {
                    if (escrow == null || (int)escrow[0] != EmptyCanister || (int)escrow[1] != 1) return LanStorage.NotAllowed;
                    var station = Nearest<GasStation>(at, 1f);
                    if (station == null) return LanStorage.NotFound;
                    if (station.FuelQuantityCurrent < 10) return LanStorage.Empty;
                    if (FullCanister >= prefabs.Length || prefabs[FullCanister] == null) return LanStorage.Invalid;
                    station.Fill(-10);
                    given = new object[] { FullCanister, 1, new object[4] };
                    return LanStorage.Ok;
                }
            }
            return LanStorage.Invalid;
        }

        // A full canister into the fuel tank of car `index` nearest to `local` (that car's space).
        private static byte HostRefuel(Vector3 local, int index, object[] escrow, Vector3? guest, out object[] given, out int detail)
        {
            given = null; detail = 0;
            if (escrow == null || (int)escrow[0] != FullCanister || (int)escrow[1] != 1) return LanStorage.NotAllowed;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            if (train == null) return LanStorage.Unavailable;
            Zompiercer.Train.TrainCar car;
            try { car = TrainLayout.GetCar(train, index); }
            catch (Exception) { return LanStorage.NotFound; }
            var at = car.transform.TransformPoint(local);
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(at, guest.Value) > 5f) return LanStorage.TooFar;
            TrainFuelTank tank = null; float best = 16f; // within 4 m of the point the guest aimed at
            foreach (var candidate in car.GetComponentsInChildren<TrainFuelTank>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                float d = (candidate.transform.position - at).sqrMagnitude;
                foreach (var collider in candidate.GetComponentsInChildren<Collider>())
                    if (collider != null && collider.enabled) d = Math.Min(d, (collider.ClosestPoint(at) - at).sqrMagnitude);
                if (d <= best) { best = d; tank = candidate; }
            }
            if (tank == null) return LanStorage.NotFound;
            if (tank.currentFuelAmount >= tank.maxFuelAmount - .01f) return LanStorage.Full;
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            var full = prefabs != null && FullCanister < prefabs.Length ? prefabs[FullCanister] : null;
            var value = full == null ? null : full.GetComponent<LootObjectValue>();
            if (value == null || value.FuelValue <= 0f || EmptyCanister >= prefabs.Length || prefabs[EmptyCanister] == null) return LanStorage.Invalid;
            tank.Fill(value.FuelValue);
            given = new object[] { EmptyCanister, 1, new object[4] };
            detail = Mathf.RoundToInt(Mathf.Clamp(tank.currentFuelAmount, 0f, 9999f) * 100f);
            return LanStorage.Ok;
        }

        // 1.2.2: the rainwater tank of car `index` nearest to `local`: read, fill bottles, pour back.
        private static byte HostWater(int kind, Vector3 local, int index, object[] escrow, Vector3? guest, out object[] given, out int detail)
        {
            given = null; detail = 0;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            if (train == null) return LanStorage.Unavailable;
            Zompiercer.Train.TrainCar car;
            try { car = TrainLayout.GetCar(train, index); }
            catch (Exception) { return LanStorage.NotFound; }
            var at = car.transform.TransformPoint(local);
            if (guest == null) return LanStorage.Lost;
            if (Vector3.Distance(at, guest.Value) > 5f) return LanStorage.TooFar;
            RainwaterTank tank = null; float best = 16f;
            foreach (var candidate in car.GetComponentsInChildren<RainwaterTank>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled || candidate.trainPart != null && candidate.trainPart.Blueprint) continue;
                float d = (candidate.transform.position - at).sqrMagnitude;
                foreach (var collider in candidate.GetComponentsInChildren<Collider>())
                    if (collider != null && collider.enabled) d = Math.Min(d, (collider.ClosestPoint(at) - at).sqrMagnitude);
                if (d <= best) { best = d; tank = candidate; }
            }
            if (tank == null) return LanStorage.NotFound;
            if (kind == LanStorage.WorkWaterLevel) { detail = WaterLevel(tank); return LanStorage.Ok; }
            int count = (int)escrow[1];
            float volume = count * BottleVolume;
            if (count < 1) return LanStorage.Invalid;
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            if (prefabs == null || EmptyBottle >= prefabs.Length || DirtyBottle >= prefabs.Length || prefabs[EmptyBottle] == null || prefabs[DirtyBottle] == null) return LanStorage.Invalid;
            if (kind == LanStorage.WorkWater)
            {
                if ((int)escrow[0] != EmptyBottle) return LanStorage.NotAllowed;
                if (tank.VolumeCurrent + .001f < volume) return LanStorage.Empty;
                tank.Fill(-volume);
                given = new object[] { DirtyBottle, count, new object[4] };
            }
            else
            {
                if ((int)escrow[0] != DirtyBottle) return LanStorage.NotAllowed;
                if (tank.VolumeCurrent + volume > tank.VolumeMax + .001f) return LanStorage.Full;
                tank.Fill(volume);
                given = new object[] { EmptyBottle, count, new object[4] }; // what the game's pouring leaves
            }
            detail = WaterLevel(tank);
            return LanStorage.Ok;
        }
        private static int WaterLevel(RainwaterTank tank) { return Mathf.RoundToInt(Mathf.Clamp(tank.VolumeCurrent, 0f, 99999f) * 100f); }

        // The host's own Gun for the guest's tool (same prefab, same stats).
        internal static Gun HostGun(int itemId)
        {
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (prefabs == null || itemId < 0 || itemId >= prefabs.Length || prefabs[itemId] == null || player == null || player.zombieFighterFireArmWeapon == null) return null;
            var data = prefabs[itemId].GetComponent<WeaponData>();
            if (data == null || player.zombieFighterFireArmWeapon.gunList == null) return null;
            foreach (var gun in player.zombieFighterFireArmWeapon.gunList) if (gun != null && gun.ID == data.ID) return gun;
            return null;
        }

        private static T Nearest<T>(Vector3 at, float within) where T : Component
        {
            T best = null; float distance = within * within;
            foreach (var candidate in Object.FindObjectsOfType<T>())
            {
                float d = (candidate.transform.position - at).sqrMagnitude;
                if (d <= distance) { best = candidate; distance = d; }
            }
            return best;
        }
    }
}
