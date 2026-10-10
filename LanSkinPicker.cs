using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // 1.1.6: how the other player sees this player: one of the game's own NPC models
    // (NPCManager.ModelName), chosen in the main menu (bottom left) with a turning preview,
    // stored in [Player] Look and sent in every player state. 1.1.9: the nickname, typed in the same
    // box, stored in [Player] Name and sent in every player state too. Main thread only.
    internal static class LanSkinPicker
    {
        // Two men and two women whose clothes are whole: no cut-out tears in their textures
        // (the other civilian models wear the torn post-apocalyptic shirt or ripped jeans).
        internal const string ManDefault = "RegularMenNPC_Driver_01", WomanDefault = "RegularWomanNPC_Farmer_01";
        private static readonly string[] Allowed = { ManDefault, "RegularMenNPC_Farmer_01", WomanDefault, "RegularWomanNPC_Military_04" };
        private static readonly string[] Labels = { "Мужчина в клетчатой рубашке", "Мужчина в комбинезоне", "Женщина в комбинезоне", "Женщина в камуфляже" };
        private const float Width = 240f, Height = 352f, Margin = 16f, TurnSpeed = 25f;
        private static ConfigEntry<string> _entry, _nameEntry;
        private static string _typed = "";
        // 1.1.9: this player's nickname (LanProtocol.ValidName; empty: none), and the partner's as its
        // states bring it (set by the plugin; empty: none).
        internal static string NickName { get; private set; } = "";
        internal static string Partner = "";
        private static Action<string> _log;
        internal static byte Chosen { get; private set; } = LanProtocol.NoSkin;
        // NoSkin first, then the allowed models this game has.
        private static List<byte> _choices;
        private static Zompiercer.GUI.GUIManager _menuGui;
        private static float _menuGuiAt;
        private static bool _visible;
        // The preview: a script-free copy of the model far below the world, seen only by its own
        // camera (a layer no game object uses) and drawn into a texture.
        private static GameObject _root, _model;
        private static Camera _camera;
        private static RenderTexture _texture;
        private const byte Unshown = 254;
        private static byte _shown = Unshown;
        private static int _layer = -2;
        private static float _angle = 160f;
        private static GUIStyle _title, _name;

        internal static void Initialize(ConfigEntry<string> entry, ConfigEntry<string> nameEntry, Action<string> log)
        {
            _entry = entry; _nameEntry = nameEntry; _log = log;
            Chosen = Parse(entry.Value);
            NickName = LanProtocol.CleanName(nameEntry.Value);
            _typed = NickName;
        }

        // How the partner is called on screen: its nickname, or what it is in this session.
        internal static string PartnerLabel(bool hosting) { return Partner.Length != 0 ? Partner : hosting ? "Друг" : "Хост"; }

        internal static void Shutdown() { DestroyPreview(); }

        internal static string ModelId(byte skin)
        { return skin <= LanProtocol.MaxSkin && Enum.IsDefined(typeof(NPCManager.ModelName), (int)skin) ? ((NPCManager.ModelName)skin).ToString() : null; }

        private static byte Parse(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return LanProtocol.NoSkin;
            for (int i = 0; i <= LanProtocol.MaxSkin; i++)
                if (string.Equals(ModelId((byte)i), value, StringComparison.OrdinalIgnoreCase) && Array.IndexOf(Allowed, ModelId((byte)i)) >= 0) return (byte)i;
            return LanProtocol.NoSkin;
        }

        internal static string Label(byte skin)
        {
            int index = Array.IndexOf(Allowed, ModelId(skin));
            return index < 0 ? "Как обычно" : Labels[index];
        }

        private static CharacterModel Model(byte skin)
        {
            string id = ModelId(skin);
            var npc = NPCManager.Global;
            if (id == null || npc == null || npc.NPCModels == null) return null;
            foreach (var model in npc.NPCModels) if (model != null && model.ID == id) return model;
            return null;
        }

        private static void BuildChoices()
        {
            if (_choices != null && _choices.Count > 1) return;
            var npc = NPCManager.Global;
            _choices = new List<byte> { LanProtocol.NoSkin };
            foreach (var id in Allowed)
                for (int i = 0; i <= LanProtocol.MaxSkin; i++)
                    if (ModelId((byte)i) == id && (npc == null || npc.NPCModels == null || Model((byte)i) != null)) _choices.Add((byte)i);
        }

        // The game's title menu (not the pause menu of a running game).
        private static bool MainMenuVisible()
        {
            var manager = GlobalManager.global;
            var gui = manager == null ? null : manager.canvas;
            if (gui == null)
            {
                if (_menuGui == null && Time.unscaledTime >= _menuGuiAt)
                { _menuGuiAt = Time.unscaledTime + 1f; _menuGui = Object.FindObjectOfType<Zompiercer.GUI.GUIManager>(); }
                gui = _menuGui;
            }
            return gui != null && gui.mainGameMenu != null && gui.mainGameMenu.activeInHierarchy && (manager == null || manager.controlledChar == null);
        }

        // Every frame (Update): the preview lives only while the title menu shows.
        internal static void Tick()
        {
            try
            {
                _visible = MainMenuVisible();
                if (!_visible) { if (_root != null) DestroyPreview(); return; }
                if (_shown != Chosen) ShowPreview();
                if (_model != null)
                {
                    _angle = Mathf.Repeat(_angle + TurnSpeed * Time.unscaledDeltaTime, 360f);
                    _model.transform.localRotation = Quaternion.Euler(0f, _angle, 0f);
                }
            }
            catch (Exception ex) { _log?.Invoke("Look preview failed: " + ex.GetType().Name + ": " + ex.Message); DestroyPreview(); _layer = -1; }
        }

        // OnGUI: the box at the bottom left of the title menu.
        internal static void Draw()
        {
            if (!_visible) return;
            BuildChoices();
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                _name = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            }
            // 1.4.1: at the top left; the bottom left holds the main menu's own links.
            var box = new Rect(Margin, Margin, Width, Height);
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x + 8f, box.y + 6f, box.width - 16f, 36f), "Ваш вид для напарника (LAN)", _title);
            GUI.Label(new Rect(box.x + 10f, box.y + 46f, 44f, 24f), "Ник:", _name);
            string typed = GUI.TextField(new Rect(box.x + 56f, box.y + 46f, box.width - 66f, 24f), _typed ?? "", LanProtocol.MaxNameChars);
            if (typed != _typed)
            {
                _typed = typed;
                string clean = LanProtocol.CleanName(typed);
                if (clean != NickName) { NickName = clean; if (_nameEntry != null) _nameEntry.Value = clean; }
            }
            var view = new Rect(box.x + 10f, box.y + 78f, box.width - 20f, box.height - 130f);
            if (_texture != null && _model != null) GUI.DrawTexture(view, _texture, ScaleMode.ScaleToFit, false);
            else GUI.Label(view, Chosen == LanProtocol.NoSkin ? "Как обычно:\nхост — мужчина,\nдруг — женщина" : "Без превью", _title);
            int index = Math.Max(0, _choices.IndexOf(Chosen));
            var row = new Rect(box.x + 8f, box.y + box.height - 46f, box.width - 16f, 38f);
            if (GUI.Button(new Rect(row.x, row.y, 36f, row.height), "◀")) Choose(_choices[(index + _choices.Count - 1) % _choices.Count]);
            GUI.Label(new Rect(row.x + 40f, row.y, row.width - 80f, row.height), Label(Chosen), _name);
            if (GUI.Button(new Rect(row.xMax - 36f, row.y, 36f, row.height), "▶")) Choose(_choices[(index + 1) % _choices.Count]);
        }

        private static void Choose(byte skin)
        {
            Chosen = skin;
            if (_entry != null) _entry.Value = ModelId(skin) ?? "";
        }

        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return -1;
        }

        private static void ShowPreview()
        {
            _shown = Chosen;
            if (_model != null) { Object.Destroy(_model); _model = null; }
            var model = Model(Chosen);
            if (_layer == -2) _layer = FreeLayer();
            if (model == null || _layer < 0) return;
            if (_root == null)
            {
                _root = new GameObject("LAN look preview");
                Object.DontDestroyOnLoad(_root);
                _root.transform.position = new Vector3(0f, -5000f, 0f);
                _texture = new RenderTexture(256, 320, 16) { name = "LAN look preview" };
                var eye = new GameObject("LAN look preview camera");
                eye.transform.SetParent(_root.transform, false);
                eye.transform.localPosition = new Vector3(0f, 1.05f, 3.4f);
                eye.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                _camera = eye.AddComponent<Camera>();
                _camera.cullingMask = 1 << _layer;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(.08f, .09f, .1f, 1f);
                _camera.fieldOfView = 34f; _camera.nearClipPlane = .1f; _camera.farClipPlane = 12f;
                _camera.allowHDR = false; _camera.allowMSAA = false; _camera.depth = -100f;
                _camera.targetTexture = _texture;
                var sun = new GameObject("LAN look preview light");
                sun.transform.SetParent(_root.transform, false);
                sun.transform.localRotation = Quaternion.Euler(25f, 200f, 0f);
                var light = sun.AddComponent<Light>();
                light.type = LightType.Directional; light.cullingMask = 1 << _layer; light.intensity = 1.2f; light.shadows = LightShadows.None;
            }
            var staging = new GameObject("LAN look preview staging");
            staging.SetActive(false);
            try
            {
                _model = LanPlugin.DisplayCopy(model, staging.transform, "LAN look preview model");
                _model.transform.SetParent(_root.transform, false);
            }
            finally { Object.Destroy(staging); }
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.Euler(0f, _angle, 0f);
            foreach (var t in _model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _layer;
            var animator = _model.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.runtimeAnimatorController = model.AnimationControllerNormal;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.enabled = true;
            }
            _model.SetActive(true);
        }

        private static void DestroyPreview()
        {
            if (_model != null) Object.Destroy(_model);
            if (_root != null) Object.Destroy(_root);
            if (_texture != null) { _texture.Release(); Object.Destroy(_texture); }
            _model = null; _root = null; _camera = null; _texture = null; _shown = Unshown;
        }
    }
}
