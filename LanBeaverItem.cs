using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // A collectible that uses the game's ordinary figurine inventory, drop,
    // placement and save code. Everything visual is made at runtime, so both
    // peers get the same item without installing an extra asset bundle.
    internal static class LanBeaverItem
    {
        internal const int ItemId = 4095;
        internal const int FallbackId = 149; // ZombieEverymanTOY in the shipped database.

        private static Harmony _harmony;
        private static Action<string> _log;
        private static ItemsDataBase _registeredDatabase;
        private static InventoryItem _prefab;

        internal static bool Ready
        {
            get
            {
                var db = ItemsDataBase.Global;
                return db != null && db == _registeredDatabase && db.itemPrefab != null &&
                       db.itemPrefab.Length > ItemId && db.itemPrefab[ItemId] == _prefab &&
                       db.items != null && db.items.Count > ItemId && db.items[ItemId] != null;
            }
        }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.beaver-item");
            try
            {
                var awake = AccessTools.DeclaredMethod(typeof(ItemsDataBase), "Awake");
                if (awake == null) throw new MissingMethodException("ItemsDataBase.Awake");
                _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(LanBeaverItem), nameof(DatabaseAwakePostfix)));
                TryRegister(ItemsDataBase.Global);
            }
            catch (Exception ex)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
                _log?.Invoke("Beaver figurine registration unavailable: " + ex.Message);
            }
        }

        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            _log = null;
            // A live save or inventory may still reference the prefab after the
            // plugin component is destroyed. Leave registered assets for this run.
        }

        private static void DatabaseAwakePostfix(ItemsDataBase __instance)
        {
            TryRegister(__instance);
        }

        private static void TryRegister(ItemsDataBase db)
        {
            if (db == null || db.items == null || db.itemPrefab == null) return;
            if (!LanBeaverSave.Ready) return;
            if (db == _registeredDatabase && Ready) return;

            GameObject templates = null;
            InventoryItem beaver = null;
            InventoryItem reserved = null;
            try
            {
                if (db.items.Count <= FallbackId || db.itemPrefab.Length <= FallbackId ||
                    db.items[FallbackId] == null || db.itemPrefab[FallbackId] == null ||
                    db.itemPrefab[FallbackId].itemID != FallbackId ||
                    db.items[FallbackId].name != "ZombieEverymanTOY")
                    throw new InvalidOperationException("expected vanilla figurine 149 is absent");

                // Never take over an ID used by the game or another mod.
                if (db.itemPrefab.Length > ItemId && db.itemPrefab[ItemId] != null)
                    throw new InvalidOperationException("item ID 4095 is already occupied");
                if (db.items.Count > ItemId && db.items[ItemId] != null)
                    throw new InvalidOperationException("item data ID 4095 is already occupied");

                // Native world/train loading instantiates itemPrefab[id] and never
                // activates the copy, so an inactive template loads invisible and the
                // next save (FindObjectsOfType) drops it. Templates stay active
                // themselves, like vanilla prefabs; this inactive holder keeps them
                // dormant and out of FindObjectsOfType.
                templates = new GameObject("LAN item templates");
                templates.SetActive(false);
                templates.transform.SetParent(db.transform, false);

                var original = db.itemPrefab[FallbackId];
                beaver = CloneInactive(original, "LAN Beaver Figurine");
                beaver.itemID = ItemId;
                beaver.Name = "LANBeaverFigurine";
                beaver.HideInMenu = false;
                beaver.amount = 1;
                beaver.itemType = (EInventoryItemType)beaver.type;
                BuildBeaver(beaver.gameObject);
                beaver.transform.SetParent(templates.transform, false);
                beaver.gameObject.SetActive(true);

                // Native FindItemIdByName iterates the full prefab array without
                // null checks. Hidden entries keep the sparse custom ID safe.
                reserved = CloneInactive(original, "LAN Reserved Item Slot");
                reserved.itemID = FallbackId;
                reserved.Name = "LANReservedItemSlot";
                reserved.HideInMenu = true;
                reserved.amount = 1;
                reserved.transform.SetParent(templates.transform, false);

                var icon = CreateIcon();
                var data = new ItemsDataBase.ItemData
                {
                    name = "Фигурка бобра",
                    description = "Коллекционная фигурка бобра — подарок за игру по сети.",
                    weght = 1f,
                    icon = icon
                };
                var emptyData = new ItemsDataBase.ItemData
                {
                    name = "",
                    description = "",
                    weght = 0f,
                    icon = icon
                };

                var prefabs = new InventoryItem[Math.Max(db.itemPrefab.Length, ItemId + 1)];
                Array.Copy(db.itemPrefab, prefabs, db.itemPrefab.Length);
                for (var i = 0; i < ItemId; ++i)
                    if (prefabs[i] == null) prefabs[i] = reserved;
                var entries = new List<ItemsDataBase.ItemData>(db.items);
                while (entries.Count < ItemId) entries.Add(emptyData);
                for (var i = 0; i < ItemId; ++i)
                    if (entries[i] == null) entries[i] = emptyData;
                prefabs[ItemId] = beaver;
                if (entries.Count == ItemId) entries.Add(data);
                else entries[ItemId] = data;

                // Commit only after all assets and tables are ready.
                db.itemPrefab = prefabs;
                db.items = entries;
                _registeredDatabase = db;
                _prefab = beaver;
                _log?.Invoke("Beaver figurine registered as item " + ItemId);
            }
            catch (Exception ex)
            {
                if (beaver != null) Object.Destroy(beaver.gameObject);
                if (reserved != null) Object.Destroy(reserved.gameObject);
                if (templates != null) Object.Destroy(templates);
                _log?.Invoke("Beaver figurine registration failed: " + ex.Message);
            }
        }

        private static InventoryItem CloneInactive(InventoryItem original, string name)
        {
            var staging = new GameObject("LAN item clone staging");
            staging.SetActive(false);
            GameObject copy = null;
            try
            {
                // The inactive parent keeps Awake from running during cloning;
                // the vanilla source prefab is never changed.
                copy = Object.Instantiate(original.gameObject, staging.transform, false);
                copy.SetActive(false);
                copy.transform.SetParent(null, false);
                copy.name = name;
                var item = copy.GetComponent<InventoryItem>();
                if (item == null) throw new InvalidOperationException("figurine clone has no InventoryItem");
                return item;
            }
            catch
            {
                if (copy != null) Object.Destroy(copy);
                throw;
            }
            finally
            {
                Object.Destroy(staging);
            }
        }

        private static void BuildBeaver(GameObject root)
        {
            // Retain the toy's native InventoryItem, Rigidbody and pickup
            // colliders. Its renderers are replaced by the beaver sculpture.
            // 1.4.14: the toy's meshes go, not only its renderers: the game's occlusion culling (GDOC) registers
            // a scene's renderers and LOD groups after a load or a location change and switches them on and
            // off itself, so the plain figurine showed again on a beaver lying on the train.
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = false;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = null;
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) skinned.sharedMesh = null;
            }
            foreach (var lod in root.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lod);
            var colliders = root.GetComponents<BoxCollider>();
            if (colliders.Length > 1)
            {
                colliders[1].center = new Vector3(0f, 0.19f, -0.065f);
                colliders[1].size = new Vector3(0.29f, 0.34f, 0.58f);
            }

            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ??
                         Shader.Find("Diffuse") ?? Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("no usable figurine shader");
            var fur = Material(shader, new Color(0.47f, 0.24f, 0.105f));
            var face = Material(shader, new Color(0.64f, 0.38f, 0.18f));
            var tail = Material(shader, new Color(0.24f, 0.13f, 0.075f));
            var nose = Material(shader, new Color(0.11f, 0.075f, 0.055f));
            var ivory = Material(shader, new Color(0.97f, 0.92f, 0.78f));
            var plinth = Material(shader, new Color(0.50f, 0.39f, 0.20f));

            Shape(root, PrimitiveType.Cylinder, "Display base", new Vector3(0f, 0.018f, 0f), new Vector3(0.28f, 0.018f, 0.28f), plinth);
            // Primitive sphere scale describes its full diameter. Overlap each
            // piece so the silhouette remains joined from every camera angle.
            Shape(root, PrimitiveType.Sphere, "Body", new Vector3(0f, 0.16f, -0.02f), new Vector3(0.24f, 0.25f, 0.27f), fur);
            Shape(root, PrimitiveType.Sphere, "Broad flat tail", new Vector3(0f, 0.067f, -0.23f), new Vector3(0.22f, 0.035f, 0.32f), tail);
            Shape(root, PrimitiveType.Sphere, "Head", new Vector3(0f, 0.278f, 0.093f), new Vector3(0.18f, 0.18f, 0.17f), fur);
            Shape(root, PrimitiveType.Sphere, "Muzzle", new Vector3(0f, 0.235f, 0.16f), new Vector3(0.145f, 0.085f, 0.105f), face);
            Shape(root, PrimitiveType.Sphere, "Nose", new Vector3(0f, 0.245f, 0.215f), new Vector3(0.06f, 0.038f, 0.038f), nose);
            for (var side = -1; side <= 1; side += 2)
            {
                Shape(root, PrimitiveType.Sphere, "Ear", new Vector3(side * 0.072f, 0.35f, 0.068f), new Vector3(0.064f, 0.068f, 0.045f), face);
                Shape(root, PrimitiveType.Sphere, "Eye", new Vector3(side * 0.055f, 0.297f, 0.163f), new Vector3(0.022f, 0.024f, 0.018f), nose);
                Shape(root, PrimitiveType.Cube, "Buck tooth", new Vector3(side * 0.018f, 0.192f, 0.213f), new Vector3(0.029f, 0.065f, 0.028f), ivory);
                Shape(root, PrimitiveType.Sphere, "Front paw", new Vector3(side * 0.064f, 0.075f, 0.105f), new Vector3(0.07f, 0.07f, 0.13f), face);
            }
        }

        private static Material Material(Shader shader, Color color)
        {
            var material = new Material(shader) { color = color, hideFlags = HideFlags.HideAndDontSave };
            return material;
        }

        private static void Shape(GameObject parent, PrimitiveType type, string name, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.layer = parent.layer;
            part.transform.SetParent(parent.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static Sprite CreateIcon()
        {
            const int width = 96;
            var pixels = new Color32[width * width];
            var brown = new Color32(118, 61, 26, 255);
            var light = new Color32(171, 105, 51, 255);
            var dark = new Color32(52, 28, 19, 255);
            var tooth = new Color32(248, 235, 204, 255);
            var gold = new Color32(191, 151, 72, 255);
            Ellipse(pixels, width, 48, 19, 32, 8, gold);
            Ellipse(pixels, width, 48, 32, 21, 12, dark); // flat tail behind body
            Ellipse(pixels, width, 48, 43, 25, 26, brown);
            Ellipse(pixels, width, 30, 68, 9, 10, light);
            Ellipse(pixels, width, 66, 68, 9, 10, light);
            Ellipse(pixels, width, 48, 65, 28, 24, brown);
            Ellipse(pixels, width, 48, 55, 18, 11, light);
            Ellipse(pixels, width, 36, 70, 3, 4, dark);
            Ellipse(pixels, width, 60, 70, 3, 4, dark);
            Ellipse(pixels, width, 48, 60, 5, 4, dark);
            Rect(pixels, width, 42, 47, 47, 57, tooth);
            Rect(pixels, width, 49, 47, 54, 57, tooth);
            var texture = new Texture2D(width, width, TextureFormat.RGBA32, false);
            texture.name = "LAN Beaver Figurine Icon";
            texture.filterMode = FilterMode.Point;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, width, width), new Vector2(.5f, .5f), width);
            sprite.name = "LAN Beaver Figurine";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static void Ellipse(Color32[] pixels, int width, int cx, int cy, int rx, int ry, Color32 color)
        {
            for (var y = Math.Max(0, cy - ry); y <= Math.Min(width - 1, cy + ry); ++y)
                for (var x = Math.Max(0, cx - rx); x <= Math.Min(width - 1, cx + rx); ++x)
                {
                    var dx = (x - cx) / (float)rx;
                    var dy = (y - cy) / (float)ry;
                    if (dx * dx + dy * dy <= 1f) pixels[y * width + x] = color;
                }
        }

        private static void Rect(Color32[] pixels, int width, int left, int bottom, int right, int top, Color32 color)
        {
            for (var y = bottom; y <= top; ++y)
                for (var x = left; x <= right; ++x)
                    pixels[y * width + x] = color;
        }
    }
}
