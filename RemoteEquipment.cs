using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Observes the local player's actual firearm shots and (1.1.0) melee swings, counted together:
    // the other side shows a shot or a swing by the item. No remote object calls this class.
    internal static class RemoteEquipment
    {
        internal const int NoItem = -1;
        private const string HarmonyId = "local.zompiercer.lan.remote-equipment";
        private static Harmony _harmony;
        private static uint _shotSequence;
        private static int _lastShotItemId = NoItem;

        internal static uint LocalShotSequence { get { return _shotSequence; } }
        internal static int LastShotItemId { get { return _lastShotItemId; } }

        // 1.4.6: the player's dash (the game's dodge on Space, ZombieFighterController "bounce"):
        // State.Dash, a count of dashes times 8 plus the direction of the last one in eighths of a
        // turn clockwise from facing (LanProtocol.ValidDash). Seen by polling every frame.
        private static readonly string[] DashFlags = { "bounceF", "bounceFR", "bounceR", "bounceBR", "bounceB", "bounceBL", "bounceL", "bounceFL" };
        private static AccessTools.FieldRef<ZombieFighterController, bool>[] _dashFlags;
        private static bool _dashUnavailable, _dashing;
        internal static byte LocalDash { get; private set; }

        internal static void ObserveDash(ZombieFighterController character)
        {
            if (character == null || _dashUnavailable) return;
            try
            {
                if (_dashFlags == null)
                {
                    var flags = new AccessTools.FieldRef<ZombieFighterController, bool>[DashFlags.Length];
                    for (int i = 0; i < flags.Length; i++) flags[i] = AccessTools.FieldRefAccess<ZombieFighterController, bool>(DashFlags[i]);
                    _dashFlags = flags;
                }
                int direction = -1;
                for (int i = 0; i < _dashFlags.Length; i++) if (_dashFlags[i](character)) { direction = i; break; }
                if (direction >= 0 && !_dashing)
                {
                    int count = LocalDash >> 3;
                    count = count >= LanProtocol.MaxDashCount ? 1 : count + 1;
                    LocalDash = (byte)(count << 3 | direction);
                }
                _dashing = direction >= 0;
            }
            catch (Exception) { _dashUnavailable = true; }
        }

        // 1.4.7: how the player moves (State.Motion) and its throws (State.Throws), seen every frame:
        // running; in the air for a moment, or just jumped (the game's TimeOfJumpOrDash, not a dash);
        // swimming; a throw held back (Gun.ThrowingPhase 1) and let go (2: a new count); the item it
        // eats, drinks or puts on (InventoryController.consumeSomething: food 10, drink 11, bandage 14,
        // medkit 16 and pills 13; not filling a bottle at a rain tank).
        internal const float AirDelay = .12f, JumpWindow = .25f;
        internal const int FoodType = 10, DrinkType = 11, BandageItem = 14, MedkitItem = 16, PillsItem = 13;
        private static readonly System.Reflection.FieldInfo JumpedAt = typeof(ZombieFighterController).GetField("TimeOfJumpOrDash",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo Draining = AccessTools.Field(typeof(InventoryController), "CurrentlyDrainingFromRainwaterTank");
        private static float _airSince = -1f;
        private static int _throwPhase;
        private static bool _motionUnavailable;
        internal static byte LocalMotion { get; private set; }
        internal static byte LocalThrows { get; private set; }
        // 1.4.8: presses of the use key in play (State.Interacts): the partner reaches for a door, a
        // lever or a button, or bends down for an item.
        internal static byte LocalInteracts { get; private set; }
        private static bool _useHeld;

        internal static void ObserveMotion(ZombieFighterController character)
        {
            if (character == null || _motionUnavailable) { LocalMotion = 0; return; }
            try
            {
                float now = Time.time;
                bool swim = character.swimming;
                var body = character.controller;
                bool grounded = body == null || !body.enabled || body.isGrounded;
                if (grounded || swim) _airSince = -1f;
                else if (_airSince < 0f) _airSince = now;
                bool jumped = JumpedAt != null && !_dashing && now - (float)JumpedAt.GetValue(null) < JumpWindow;
                bool air = !swim && (jumped || _airSince >= 0f && now - _airSince > AirDelay);
                var gun = character.GetSelectedGun();
                int phase = gun == null ? 0 : gun.ThrowingPhase;
                if (phase == 2 && _throwPhase != 2) LocalThrows = unchecked((byte)(LocalThrows + 1));
                _throwPhase = phase;
                LocalMotion = (byte)((character.runing ? LanProtocol.MotionRun : 0) | (air ? LanProtocol.MotionAir : 0) |
                    (swim ? LanProtocol.MotionSwim : 0) | (phase == 1 ? LanProtocol.MotionCock : 0) | UseOf(InventoryController.global) << LanProtocol.UseShift);
                var input = Zompiercer.Inputs.InputController.Global;
                bool use = input != null && (int)input.CurrentState == 1 && (input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown);
                if (use && !_useHeld) LocalInteracts = unchecked((byte)(LocalInteracts + 1));
                _useHeld = use;
            }
            catch (Exception) { _motionUnavailable = true; LocalMotion = 0; }
        }

        private static int UseOf(InventoryController inventory)
        {
            if (inventory == null || !inventory.consumeSomething || Draining != null && Draining.GetValue(inventory) as UnityEngine.Object != null) return LanProtocol.UseNone;
            var item = inventory.reservedItem;
            if (item == null) return LanProtocol.UseNone;
            if (item.itemID == BandageItem) return LanProtocol.UseBandage;
            if (item.itemID == MedkitItem || item.itemID == PillsItem) return LanProtocol.UseMedicine;
            if ((int)item.itemType == DrinkType) return LanProtocol.UseDrink;
            if ((int)item.itemType == FoodType) return LanProtocol.UseEat;
            return LanProtocol.UseNone;
        }

        internal static int CaptureLocalItemId()
        {
            var manager = GlobalManager.global;
            var character = manager == null ? null : manager.controlledChar;
            var inventory = InventoryController.global;
            if (character == null || inventory == null || character.beltInventory == null) return NoItem;
            // 1.1.6: the bare fists are in hand whatever the selected belt slot holds.
            var gun = character.GetSelectedGun();
            if (gun != null && gun.Hands) return NoItem;
            int slot = inventory.selectedBeltIcon;
            if (slot < 0 || slot >= character.beltInventory.Length) return NoItem;
            var cell = character.beltInventory[slot];
            if (cell == null || cell.content == null || cell.content.Count == 0) return NoItem;
            var item = cell.content[0];
            return item == null || (item.weaponData == null && item.GetComponent<WeaponData>() == null)
                ? NoItem : item.itemID;
        }

        internal static void Init()
        {
            if (_harmony != null) return;
            var harmony = new Harmony(HarmonyId);
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Gun), "Shot"),
                    new HarmonyMethod(AccessTools.Method(typeof(RemoteEquipment), "BeforeShot")),
                    new HarmonyMethod(AccessTools.Method(typeof(RemoteEquipment), "AfterShot")));
                harmony.Patch(AccessTools.Method(typeof(Gun), "MeleeStrike"),
                    new HarmonyMethod(AccessTools.Method(typeof(RemoteEquipment), "BeforeSwing")),
                    new HarmonyMethod(AccessTools.Method(typeof(RemoteEquipment), "AfterSwing")));
                _shotSequence = 0;
                _lastShotItemId = NoItem;
                _harmony = harmony;
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            _harmony = null;
            _shotSequence = 0;
            _lastShotItemId = NoItem;
        }

        private static void BeforeShot(Gun __instance, out int __state)
        {
            try { __state = __instance == null ? 0 : __instance.magazine; }
            catch (Exception) { __state = 0; }
        }

        private static void AfterShot(Gun __instance, int __state)
        {
            try
            {
                if (_harmony == null || __instance == null || __instance.weaponType != 0 ||
                    __instance.magazine >= __state) return;
                Count(__instance);
            }
            catch (Exception) { /* Observation must never interrupt the game's fire path. */ }
        }

        // A swing starts when MeleeStrike sets meleeAttack (it refuses on cooldown, low stamina or
        // a broken item, and in the guest's world when the host's ledger forbids it).
        private static void BeforeSwing(Gun __instance, out bool __state)
        {
            try { __state = __instance != null && __instance.meleeAttack; }
            catch (Exception) { __state = true; }
        }

        private static void AfterSwing(Gun __instance, bool __state)
        {
            try
            {
                if (_harmony == null || __instance == null || __state || !__instance.meleeAttack) return;
                Count(__instance);
            }
            catch (Exception) { /* Observation must never interrupt the game's melee path. */ }
        }

        // One shot or swing of the player's own selected gun, with the item it was made with.
        private static void Count(Gun gun)
        {
            var manager = GlobalManager.global;
            var character = manager == null ? null : manager.controlledChar;
            var firearm = character == null ? null : character.zombieFighterFireArmWeapon;
            if (firearm == null || firearm.GetSelectedGun() != gun) return;
            int itemId = CaptureLocalItemId();
            var database = ItemsDataBase.Global;
            var prefabs = database == null ? null : database.itemPrefab;
            var prefab = prefabs == null || itemId < 0 || itemId >= prefabs.Length
                ? null : prefabs[itemId];
            var data = prefab == null ? null : prefab.GetComponent<WeaponData>();
            _lastShotItemId = data != null && data.ID == gun.ID ? itemId : NoItem;
            unchecked { ++_shotSequence; }
        }
    }

    // Attach after the NPC avatar is created. This object owns only display geometry and effects.
    // 1.0.9: the weapon sits in the avatar's right palm (the NPC rigs are generic, so the bone is
    // found by name) the way the game's armed NPCs hold their rifle. Every item model of the game
    // follows one layout: its working end (muzzle, blade, head) at -X, grip or butt at +X, a
    // firearm's top at -Z. The hold below was measured from the game's own data: the military
    // NPC's rifle under its palm and the same rifle as an item.
    internal sealed class RemoteEquipmentVisual : MonoBehaviour
    {
        private enum Hold { Hand, Firearm, Bow }
        // Palm space: where a gun's muzzle and top point, and where the palm sits on a gun, as a
        // share of the item's size from its -X/-Y/-Z corner (along the length: from the muzzle).
        private static readonly Vector3 GunForward = new Vector3(-.9655f, -.0265f, .259f), GunUp = new Vector3(-.2571f, -.0599f, -.9645f);
        private static readonly Vector3 GunGrip = new Vector3(.775f, .611f, .659f);
        // The centre of the fist (the middle of a pistol grip) in palm space: a handle or a bow
        // goes through it, its working end towards the thumb.
        private static readonly Vector3 Fist = new Vector3(-.104f, -.025f, .030f);
        private static readonly Quaternion ItemFrame = Quaternion.Inverse(Quaternion.LookRotation(Vector3.left, Vector3.back));
        private Hold _hold;
        private GameObject _held;
        private GameObject _flash;
        private Light _flashLight;
        private Material _flashMaterial;
        private AudioSource _audio;
        private int _itemId = RemoteEquipment.NoItem;
        private uint _lastShotSequence;
        private bool _haveShotBaseline;
        private float _flashUntil;
        private float _nextBuildRetry;
        private float _nextShotEffect;

        internal void Apply(int itemId, uint shotSequence)
        {
            Apply(itemId, shotSequence, itemId);
        }

        // A firearm is shown in hand (the avatar then takes the armed pose); any other item is
        // swung on a strike.
        internal bool TwoHanded { get { return _held != null && _hold == Hold.Firearm && !_hidden; } }
        // 1.4.7: put away while the partner eats, drinks, bandages itself or swims.
        private bool _hidden;
        internal bool Hidden
        {
            get { return _hidden; }
            set { _hidden = value; if (_held != null && _held.activeSelf == value) _held.SetActive(!value); }
        }
        // 1.4.6: the held item's length (m): how far the supporting hand reaches along a firearm.
        internal float HeldLength { get; private set; }
        internal bool Swings { get { return _held != null && _hold != Hold.Firearm && !_hidden; } }
        internal float LastShotAt { get; private set; } = -100f;
        private AudioClip _swingSound;
        private float _swingSoundAt = -1f;
        // 1.1.1: shots the partner's state reported, shown one by one at the weapon's own rate.
        private int _pendingShots;
        private float _shotInterval = .1f;
        // 1.4.8: strikes with bare fists (counted with no item in hand on either side), one by one at
        // the fists' own rate, each with their swing sound; Punches counts those shown.
        private int _pendingPunches;
        private float _nextPunch;
        internal int Punches { get; private set; }

        internal void Apply(int itemId, uint shotSequence, int shotItemId)
        {
            if (!_haveShotBaseline)
            {
                _lastShotSequence = shotSequence;
                _haveShotBaseline = true;
            }

            if (itemId != _itemId && Time.unscaledTime >= _nextBuildRetry)
            {
                _itemId = itemId;
                RebuildHeldVisual();
            }
            else if (_held == null && _itemId >= 0 && Time.unscaledTime >= _nextBuildRetry)
                RebuildHeldVisual();

            // A state packet can carry several shots: each is shown, at the weapon's rate (Update).
            int shots = unchecked((int)(shotSequence - _lastShotSequence));
            if (shots > 0)
            {
                _lastShotSequence = shotSequence;
                if (_held != null && shotItemId == _itemId) _pendingShots = Mathf.Min(_pendingShots + shots, 12);
                else if (_itemId == RemoteEquipment.NoItem && shotItemId == RemoteEquipment.NoItem) _pendingPunches = Mathf.Min(_pendingPunches + shots, 4);
            }
        }

        private void Update()
        {
            if (_flash != null && _flash.activeSelf && Time.unscaledTime >= _flashUntil)
                _flash.SetActive(false);
            if (_pendingShots > 0 && Time.unscaledTime >= _nextShotEffect)
            {
                _pendingShots--;
                _nextShotEffect = Time.unscaledTime + _shotInterval;
                LastShotAt = Time.unscaledTime;
                ShowShot();
            }
            if (_pendingPunches > 0 && Time.unscaledTime >= _nextPunch)
            {
                _pendingPunches--;
                Punches++;
                var fists = Fists();
                _nextPunch = Time.unscaledTime + (fists == null ? .45f : Mathf.Clamp(fists.meleeStrikeTimeForAttackEnd, .2f, .8f));
                if (fists != null && fists.meeleSwingSound != null)
                {
                    CreateAudio();
                    _swingSound = fists.meeleSwingSound; _swingSoundAt = Time.unscaledTime + Mathf.Clamp(fists.meeleSwingSoundDelay, 0f, .5f);
                }
            }
            if (_swingSoundAt >= 0f && Time.unscaledTime >= _swingSoundAt)
            {
                _swingSoundAt = -1f;
                if (_audio != null && _swingSound != null) _audio.PlayOneShot(_swingSound);
            }
        }

        private void RebuildHeldVisual()
        {
            _nextBuildRetry = Time.unscaledTime + 0.25f;
            if (_held != null) Object.Destroy(_held);
            if (_flashMaterial != null) Object.Destroy(_flashMaterial);
            _held = null;
            _flash = null;
            _flashLight = null;
            _flashMaterial = null;
            if (_itemId == RemoteEquipment.NoItem) return;
            _nextBuildRetry = Time.unscaledTime + 1f;

            var database = ItemsDataBase.Global;
            if (database == null || database.itemPrefab == null || _itemId < 0 ||
                _itemId >= database.itemPrefab.Length) return;
            var prefab = database.itemPrefab[_itemId];
            if (prefab == null || prefab.GetComponent<WeaponData>() == null) return;

            HideBakedNpcWeapons();
            var hand = FindRightHand();
            _hold = HoldOf(prefab.GetComponent<WeaponData>(), out _shotInterval);
            _pendingShots = 0;
            _held = new GameObject("LAN held weapon visual");
            _held.transform.SetParent(hand == null ? transform : hand, false);
            if (_hidden) _held.SetActive(false);
            // The item in its own axes (see the class comment), at its own size.
            var model = new GameObject("Model");
            model.transform.SetParent(_held.transform, false);
            model.transform.localScale = prefab.transform.localScale;
            CopyGeometry(prefab.transform, model.transform, true, ExcludedLowerLods(prefab));
            Vector3 min, max;
            if (!Bounds(_held.transform, out min, out max)) { min = -Vector3.one * .1f; max = Vector3.one * .1f; }
            var size = max - min; var centre = (min + max) / 2f;
            HeldLength = size.x;
            if (hand == null)
            {
                // No palm bone: beside the body, pointing forward.
                _held.transform.localPosition = new Vector3(0.35f, 1.25f, 0.25f);
                _held.transform.localRotation = ItemFrame;
            }
            else if (_hold == Hold.Firearm)
            {
                var rotation = Quaternion.LookRotation(GunForward, GunUp) * ItemFrame;
                _held.transform.localRotation = rotation;
                _held.transform.localPosition = -(rotation * (min + Vector3.Scale(size, GunGrip)));
            }
            else
            {
                // A handle (near the +X end) or a bow (its middle) through the fist, the working
                // end or the upper limb towards the thumb.
                var rotation = Quaternion.LookRotation(GunUp, GunForward) * ItemFrame;
                var handle = _hold == Hold.Bow ? centre : new Vector3(max.x - Mathf.Clamp(size.x * .12f, .05f, .12f), centre.y, centre.z);
                _held.transform.localRotation = rotation;
                _held.transform.localPosition = Fist - rotation * handle;
            }
            // The muzzle: the -X end, near the top.
            if (_hold == Hold.Firearm) CreateFlash(new Vector3(min.x - .02f, centre.y, Mathf.Lerp(min.z, max.z, .17f)));
            CreateAudio();
        }

        private void CreateAudio()
        {
            if (_audio != null) return;
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 1f;
            _audio.dopplerLevel = 0f;
            _audio.minDistance = 1f;
            _audio.maxDistance = 30f;
        }

        // 1.4.8: the local player's bare fists (its gun with Hands), for their rate and sound.
        private static Gun Fists()
        {
            var manager = GlobalManager.global;
            var character = manager == null ? null : manager.controlledChar;
            var guns = character == null || character.zombieFighterFireArmWeapon == null ? null : character.zombieFighterFireArmWeapon.gunList;
            if (guns != null) foreach (var gun in guns) if (gun != null && gun.Hands) return gun;
            return null;
        }

        // The weapon's kind, from the local player's gun of the same item (both sides have the same list).
        // interval: the time between two of its shots or swings.
        private static Hold HoldOf(WeaponData data, out float interval)
        {
            interval = .4f;
            var manager = GlobalManager.global;
            var character = manager == null ? null : manager.controlledChar;
            var guns = character == null || character.zombieFighterFireArmWeapon == null ? null : character.zombieFighterFireArmWeapon.gunList;
            if (data == null || guns == null) return Hold.Hand;
            foreach (var gun in guns)
            {
                if (gun == null || gun.ID != data.ID) continue;
                interval = Mathf.Clamp(gun.weaponType == 0 ? gun.currentBetweenShotsTime : gun.meleeStrikeTimeForAttackEnd, .05f, .8f);
                if (gun.weaponType != 0 || gun.bulletPrefab == null) return Hold.Hand;
                var bullet = gun.bulletPrefab.GetComponent<BulletScript>();
                return bullet != null && bullet.arrow ? Hold.Bow : Hold.Firearm;
            }
            return Hold.Hand;
        }

        // The geometry's bounds in the space of the given transform.
        private static bool Bounds(Transform space, out Vector3 min, out Vector3 max)
        {
            min = Vector3.one * float.MaxValue; max = -min; bool any = false;
            foreach (var filter in space.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh; if (mesh == null) continue;
                var b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var at = space.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    min = Vector3.Min(min, at); max = Vector3.Max(max, at); any = true;
                }
            }
            return any;
        }

        private Transform FindRightHand()
        {
            // The game's NPC rigs (RigSkuf*, RigW*, RigSlim*): the avatar's own palm, not the
            // invisible armed driver's.
            var motion = GetComponent<RemoteAvatarMotion>();
            if (motion != null && motion.RightPalm != null) return motion.RightPalm;
            string prefix;
            var palm = RemoteAvatarMotion.FindBone(transform, "RArmPalm", out prefix);
            if (palm != null) return palm;
            var animator = GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                try
                {
                    var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    if (hand != null) return hand;
                }
                catch (InvalidOperationException) { }
            }
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "CATRigRArmPalm" || child.name == "RightHand" ||
                    child.name == "Hand_R" || child.name == "Bip001 R Hand") return child;
            }
            return null;
        }

        private void HideBakedNpcWeapons()
        {
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "Pickup_AKM_LOD0" || child.name == "Pickup_HuntingRifle_LOD0")
                    child.gameObject.SetActive(false);
            }
        }

        private static HashSet<Renderer> ExcludedLowerLods(InventoryItem prefab)
        {
            var excluded = new HashSet<Renderer>();
            foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                    foreach (var renderer in lods[i].renderers) excluded.Add(renderer);
            }
            return excluded;
        }

        private static void CopyGeometry(Transform source, Transform target, bool isRoot,
            HashSet<Renderer> excluded)
        {
            if (!isRoot && !source.gameObject.activeSelf) return;
            var filter = source.GetComponent<MeshFilter>();
            var meshRenderer = source.GetComponent<MeshRenderer>();
            if (filter != null && filter.sharedMesh != null && meshRenderer != null &&
                meshRenderer.enabled && !excluded.Contains(meshRenderer))
            {
                target.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var display = target.gameObject.AddComponent<MeshRenderer>();
                display.sharedMaterials = meshRenderer.sharedMaterials;
                display.shadowCastingMode = meshRenderer.shadowCastingMode;
                display.receiveShadows = meshRenderer.receiveShadows;
            }
            var skinned = source.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null && skinned.enabled &&
                !excluded.Contains(skinned))
            {
                var skinCopy = new GameObject("Skinned geometry");
                skinCopy.transform.SetParent(target, false);
                skinCopy.AddComponent<MeshFilter>().sharedMesh = skinned.sharedMesh;
                skinCopy.AddComponent<MeshRenderer>().sharedMaterials = skinned.sharedMaterials;
            }
            for (int i = 0; i < source.childCount; i++)
            {
                var child = source.GetChild(i);
                if (!child.gameObject.activeSelf) continue;
                var copy = new GameObject(child.name);
                copy.transform.SetParent(target, false);
                copy.transform.localPosition = child.localPosition;
                copy.transform.localRotation = child.localRotation;
                copy.transform.localScale = child.localScale;
                CopyGeometry(child, copy.transform, false, excluded);
            }
        }

        private void CreateFlash(Vector3 muzzle)
        {
            _flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _flash.name = "LAN muzzle flash visual";
            _flash.transform.SetParent(_held.transform, false);
            _flash.transform.localPosition = muzzle;
            _flash.transform.localScale = Vector3.one * 0.12f;
            var collider = _flash.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            var shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                _flashMaterial = new Material(shader);
                _flashMaterial.color = new Color(1f, 0.72f, 0.25f, 1f);
                _flash.GetComponent<Renderer>().sharedMaterial = _flashMaterial;
            }
            _flashLight = _flash.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.color = new Color(1f, 0.65f, 0.2f);
            _flashLight.range = 2.5f;
            _flashLight.intensity = 2f;
            _flash.SetActive(false);
        }

        private void ShowShot()
        {
            if (_flash != null)
            {
                _flashUntil = Time.unscaledTime + 0.06f;
                _flash.transform.localScale = Vector3.one * UnityEngine.Random.Range(.14f, .2f);
                _flash.SetActive(true);
            }
            if (_audio == null) return;
            var manager = GlobalManager.global;
            var character = manager == null ? null : manager.controlledChar;
            var firearm = character == null ? null : character.zombieFighterFireArmWeapon;
            var database = ItemsDataBase.Global;
            if (firearm == null || firearm.gunList == null || database == null ||
                database.itemPrefab == null || _itemId < 0 || _itemId >= database.itemPrefab.Length) return;
            var item = database.itemPrefab[_itemId];
            var data = item == null ? null : item.GetComponent<WeaponData>();
            if (data == null) return;
            foreach (var gun in firearm.gunList)
            {
                if (gun == null || gun.ID != data.ID) continue;
                if (_hold != Hold.Firearm)
                {
                    // The game's own swing sound, after its own delay.
                    _swingSound = gun.meeleSwingSound;
                    if (_swingSound != null) _swingSoundAt = Time.unscaledTime + Mathf.Clamp(gun.meeleSwingSoundDelay, 0f, .5f);
                    return;
                }
                if (gun.shotSound == null || gun.shotSound.Length == 0) return;
                var clip = gun.shotSound[UnityEngine.Random.Range(0, gun.shotSound.Length)];
                if (clip != null) _audio.PlayOneShot(clip);
                return;
            }
        }

        private void OnDestroy()
        {
            if (_flashMaterial != null) Object.Destroy(_flashMaterial);
            if (_held != null) Object.Destroy(_held);
        }
    }
}
