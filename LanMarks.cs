using System;
using System.Collections.Generic;
using UnityEngine;
using Zompiercer.Inventory;
using Zompiercer.Train;

namespace ZompiercerLAN
{
    // Marks and pointers (0.10.0). Either player marks what is under the crosshair (middle
    // mouse button by default): an enemy (a zombie), a find (an item, a bag, a storage) or a
    // point / direction (anything else, or the sky). Both players see each mark with its
    // distance; marks off screen become arrows at the screen's edge, and so does the partner.
    // A mark on the train is kept in its car's own space, so it rides along. Marking one of
    // one's own marks again removes it. Main thread only; drawn in OnGUI.
    internal sealed class LanMarks
    {
        // 1.1.6: at most one mark a second and two of each player at once (0.3 s and three before).
        internal const float Lifetime = 25f, Range = 400f, SkyDistance = 120f, MinInterval = 1f;
        internal const int PerPlayer = 2;
        private sealed class Mark
        {
            internal int Id, Car;
            internal byte Type;
            internal Vector3 Point;
            internal float Until;
            internal bool Own;
        }
        private readonly Action<Packet> _send;
        private readonly bool _hosting;
        private readonly List<Mark> _marks = new List<Mark>();
        private uint _sequence, _received;
        private int _nextId;
        private float _nextPlace, _toastUntil;
        private string _scene, _toast;
        private uint _epoch;
        private bool _ready;
        private GUIStyle _style, _shadow;
        // A dead player's belongings (0.12.0): one long-lived local pointer, gone when reached.
        internal const float StashLifetime = 900f, StashReached = 2.5f;
        private bool _stash;
        private int _stashCar;
        private Vector3 _stashPoint;
        private string _stashLabel;
        private float _stashUntil;

        // [Marks] Key: "Mouse2" (the game's middle mouse button) or a keyboard key name.
        internal static KeyCode Key = KeyCode.Mouse2;
        internal static bool ShowPartner = true;

        internal LanMarks(bool hosting, Action<Packet> send) { _hosting = hosting; _send = send; }

        private string PartnerName { get { return LanSkinPicker.PartnerLabel(_hosting); } } // 1.1.9: its nickname, if any

        internal void Tick(string scene, uint epoch, bool ready)
        {
            if (scene != _scene || epoch != _epoch) { _marks.Clear(); _received = 0; _scene = scene; _epoch = epoch; _stash = false; }
            _ready = ready && !string.IsNullOrEmpty(scene) && epoch != 0;
            float now = Time.unscaledTime;
            _marks.RemoveAll(m => now >= m.Until);
            if (Key != KeyCode.Mouse2) return;
            // The game resets its middle-button flag (isMmbDown) every frame but never sets it,
            // and its legacy Input manager is disabled (Input.GetMouseButtonDown throws), so the
            // button is read from the Input System mouse; a press is its up-to-down edge.
            bool down = MiddleButtonDown();
            bool pressed = down && !_middleWasDown;
            _middleWasDown = down;
            if (!_ready) return;
            var input = Zompiercer.Inputs.InputController.Global;
            pressed |= input != null && input.isMmbDown;
            if (pressed && input != null && (int)input.CurrentState == 1) Place();
        }

        private bool _middleWasDown;
        private static bool _mouseFailed;
        private static bool MiddleButtonDown()
        {
            if (_mouseFailed) return false;
            try { return ReadMiddleButton(); }
            catch (Exception ex)
            {
                // Without the Input System the configured keyboard key ([Marks] Key) still works.
                _mouseFailed = true;
                Debug.LogWarning("[Zompiercer LAN] Middle mouse button unavailable for marks: " + ex.GetType().Name);
                return false;
            }
        }
        // Separate method: a missing Input System assembly fails here, inside the caller's try.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadMiddleButton()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.middleButton.isPressed;
        }

        // OnGUI: a keyboard key, when one is configured instead of the middle button.
        internal void KeyEvent(Event e)
        {
            if (!_ready || Key == KeyCode.Mouse2 || e.type != EventType.KeyDown || e.keyCode != Key) return;
            var input = Zompiercer.Inputs.InputController.Global;
            if (input != null && (int)input.CurrentState != 1) return;
            Place(); e.Use();
        }

        private void Place()
        {
            float now = Time.unscaledTime;
            if (now < _nextPlace) return;
            _nextPlace = now + MinInterval;
            var camera = Camera.main;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (camera == null || player == null) return;
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit best = default(RaycastHit); bool any = false;
            foreach (var hit in Physics.RaycastAll(ray, Range, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(player.transform)) continue;
                if (!any || hit.distance < best.distance) { best = hit; any = true; }
            }
            byte type = any ? Classify(best.collider) : LanProtocol.MarkPoint;
            Vector3 point = any ? best.point : ray.origin + ray.direction * SkyDistance;
            // Marking one's own mark again takes it away.
            foreach (var own in _marks)
                if (own.Own && Vector3.Distance(World(own), point) < 2f)
                {
                    _marks.Remove(own);
                    Send(LanProtocol.MarkClear, own.Id, -1, Vector3.zero);
                    return;
                }
            int car = -1; Vector3 local = point;
            var trainCar = any ? best.collider.GetComponentInParent<TrainCar>() : null;
            var train = GlobalManager.global.controlledTrain;
            if (trainCar != null && train != null)
            {
                car = TrainLayout.CarIndex(train, trainCar);
                if (car >= 0) local = trainCar.transform.InverseTransformPoint(point);
                if (car < 0 || car >= LanProtocol.MaxCars || Mathf.Abs(local.x) > 256f || Mathf.Abs(local.y) > 256f || Mathf.Abs(local.z) > 256f) { car = -1; local = point; }
            }
            if (Mathf.Abs(local.x) >= 99999f || Mathf.Abs(local.y) >= 99999f || Mathf.Abs(local.z) >= 99999f) return;
            _nextId = _nextId % 65535 + 1;
            Add(new Mark { Id = _nextId, Type = type, Car = car, Point = local, Until = now + Lifetime, Own = true });
            Send(type, _nextId, car, local);
        }

        private static byte Classify(Collider collider)
        {
            if (collider.GetComponentInParent<ZombieAIController>() != null) return LanProtocol.MarkEnemy;
            int hostId, itemId;
            if (collider.GetComponentInParent<InventoryItem>() != null || collider.GetComponentInParent<Inventory>() != null ||
                TrainLayout.TryIdentifyItem(collider, out hostId, out itemId)) return LanProtocol.MarkFind;
            int car, kind, identity;
            if (TrainLayout.TryIdentifyStorage(collider, out car, out kind, out identity)) return LanProtocol.MarkFind;
            return LanProtocol.MarkPoint;
        }

        private void Send(byte type, int id, int car, Vector3 at)
        {
            _send(new Packet { Kind = PacketKind.Mark, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence,
                Action = type, Revision = id, CarIndex = car, X = at.x, Y = at.y, Z = at.z });
        }

        internal void Receive(Packet p)
        {
            if (!_ready || p.WorldEpoch != _epoch || p.Scene != _scene) return;
            if (_received != 0 && unchecked((int)(p.Sequence - _received)) <= 0) return;
            _received = p.Sequence;
            _marks.RemoveAll(m => !m.Own && m.Id == p.Revision);
            if (p.Action == LanProtocol.MarkClear) return;
            Add(new Mark { Id = p.Revision, Type = p.Action, Car = p.CarIndex, Point = new Vector3(p.X, p.Y, p.Z), Until = Time.unscaledTime + Lifetime, Own = false });
            _toast = PartnerName + " отметил: " + Label(p.Action).ToLowerInvariant();
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        // A pointer to belongings left at a point (car-local when car >= 0), and a message.
        internal void Stash(int car, Vector3 point, string label)
        {
            _stash = true; _stashCar = car; _stashPoint = point; _stashLabel = label; _stashUntil = Time.unscaledTime + StashLifetime;
        }
        internal void Toast(string text) { _toast = text; _toastUntil = Time.unscaledTime + 5f; }

        private void Add(Mark mark)
        {
            int count = 0;
            for (int i = _marks.Count - 1; i >= 0; i--)
                if (_marks[i].Own == mark.Own && ++count >= PerPlayer) _marks.RemoveAt(i);
            _marks.Add(mark);
        }

        private static Vector3 World(Mark m) { return World(m.Car, m.Point); }
        private static Vector3 World(int car, Vector3 point)
        {
            if (car < 0) return point;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            try { return TrainLayout.GetCar(train, car).transform.TransformPoint(point); }
            catch (Exception) { return point; }
        }

        private static string Label(byte type)
        {
            switch (type)
            {
                case LanProtocol.MarkEnemy: return "Враг";
                case LanProtocol.MarkFind: return "Находка";
                default: return "Сюда";
            }
        }
        private static Color ColorOf(byte type)
        {
            switch (type)
            {
                case LanProtocol.MarkEnemy: return new Color(1f, .3f, .25f);
                case LanProtocol.MarkFind: return new Color(1f, .85f, .2f);
                default: return new Color(.4f, .8f, 1f);
            }
        }

        // OnGUI (repaint): marks, the partner and the last toast.
        internal void Draw(Vector3? partner)
        {
            if (!_ready || Event.current.type != EventType.Repaint) return;
            var camera = Camera.main;
            if (camera == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false };
                _shadow = new GUIStyle(_style); _shadow.normal.textColor = new Color(0f, 0f, 0f, .8f);
            }
            Vector3 eye = camera.transform.position;
            foreach (var m in _marks)
            {
                var at = World(m);
                Pointer(camera, at, ColorOf(m.Type), (m.Own ? "" : PartnerName + ": ") + Label(m.Type) + "  " + Mathf.RoundToInt(Vector3.Distance(eye, at)) + " м", "◆", m.Own ? .8f : 1f);
            }
            if (_stash)
            {
                var at = World(_stashCar, _stashPoint);
                float distance = Vector3.Distance(eye, at);
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (Time.unscaledTime > _stashUntil || player != null && Vector3.Distance(player.transform.position, at) < StashReached) _stash = false;
                else Pointer(camera, at + Vector3.up * .5f, new Color(1f, .6f, .1f), _stashLabel + "  " + Mathf.RoundToInt(distance) + " м", "▼", 1f);
            }
            if (ShowPartner && partner != null)
            {
                var at = partner.Value + Vector3.up * 2.1f;
                float distance = Vector3.Distance(eye, at);
                if (distance > 3f) Pointer(camera, at, new Color(.45f, 1f, .55f), PartnerName + "  " + Mathf.RoundToInt(distance) + " м", "●", .9f);
            }
            if (_toast != null && Time.unscaledTime < _toastUntil)
                Text(new Rect(0f, Screen.height * .18f, Screen.width, 30f), _toast, new Color(1f, 1f, 1f, .95f));
        }

        private void Pointer(Camera camera, Vector3 world, Color color, string label, string glyph, float alpha)
        {
            color.a = alpha;
            var screen = camera.WorldToScreenPoint(world);
            float x = screen.x, y = Screen.height - screen.y;
            bool behind = screen.z < 0f;
            if (behind) { x = Screen.width - x; y = Screen.height - y; }
            const float margin = 40f;
            bool inside = !behind && x >= margin && x <= Screen.width - margin && y >= margin && y <= Screen.height - margin;
            if (inside)
            {
                Text(new Rect(x - 20f, y - 14f, 40f, 28f), glyph, color);
                Text(new Rect(x - 150f, y + 10f, 300f, 22f), label, color);
                return;
            }
            // Off screen: an arrow on the edge, pointing towards it.
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var direction = new Vector2(x, y) - center;
            if (behind && direction.sqrMagnitude < 1f) direction = Vector2.down;
            direction.Normalize();
            float scale = Mathf.Min((Screen.width / 2f - margin) / Mathf.Max(Mathf.Abs(direction.x), 1e-4f), (Screen.height / 2f - margin) / Mathf.Max(Mathf.Abs(direction.y), 1e-4f));
            var edge = center + direction * scale;
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, edge);
            Text(new Rect(edge.x - 16f, edge.y - 14f, 32f, 28f), "➤", color);
            GUI.matrix = matrix;
            var labelAt = edge - direction * 34f;
            Text(new Rect(labelAt.x - 150f, labelAt.y - 11f, 300f, 22f), label, color);
        }

        private void Text(Rect rect, string text, Color color)
        {
            var shadowRect = rect; shadowRect.x += 1f; shadowRect.y += 1f;
            GUI.Label(shadowRect, text, _shadow);
            var previous = GUI.color; GUI.color = color;
            GUI.Label(rect, text, _style);
            GUI.color = previous;
        }
    }
}
