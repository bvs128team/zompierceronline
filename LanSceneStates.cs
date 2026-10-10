using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Train;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Location progress together (1.4.4). Walls and gates blown up with C4 (DestroyableByBomb), trap
    // walls, collapsing floors, bridges (ObjectActivator), drained swamps (WaterDrainer), the world's own
    // levers and buttons, and resources picked clean live in the host's world: only the host's explosions
    // break a wall, and the guest's copy of the location kept its own state. The host sends how they
    // stand, by the game's own ObjectID (SceneFrame), changed ones first and the rest in turn, and the
    // guest's copy takes it as the game loads a save. The guest's lever or button in the host's world,
    // and a trap or bridge it sets off, is a request the host does as its own (SceneUse). Train doors,
    // scene doors, quests and fuel stations have their own sync. 1.4.5: what the host took apart for
    // resources (ResourceContent: boxes, wrecks, fallen trees; the game destroys them and saves nothing,
    // so they are known by their place) goes away for the guest too, and a presence trigger
    // (CharPresenceTrigger: trap walls, the train's emergency brake) the guest walks into goes off on the
    // host as well. Main thread only.
    internal sealed class LanSceneStates
    {
        private const float IndexEvery = 10f, ScanEvery = 2f, SendEvery = .25f, ResourceRadius = 150f, UseEvery = .3f;
        private const float ButtonReach = 4.5f, TrapReach = 25f, CollapseReach = 20f, ActivatorReach = 8f, TriggerReach = 30f;
        private const int PerBatch = 16;
        private static Harmony _harmony;
        private static Action<string> _staticLog;
        private static bool _logged;
        internal static string Failure { get; private set; }
        private static IList _allObjectIds;
        private static AccessTools.FieldRef<DestroyableByBomb, bool> _bombDone;
        private static AccessTools.FieldRef<TrapWall, bool> _trapDone;
        private static AccessTools.FieldRef<StructuralDestructionAndRepair, bool> _collapseDone, _collapsible;
        private static AccessTools.FieldRef<ObjectActivator, bool> _raised;
        private static AccessTools.FieldRef<WaterDrainer, bool> _draining;
        private static AccessTools.FieldRef<WaterDrainer, float> _drained;
        private static AccessTools.FieldRef<CharPresenceTrigger, bool> _entered;
        private static AccessTools.FieldRef<CharPresenceTrigger, UnityEngine.Events.UnityEvent> _onEnter;
        // Host (1.4.5): what was taken apart in this scene, by place key, and where.
        private static readonly Dictionary<int, Vector3> Parsed = new Dictionary<int, Vector3>();
        private static string _parsedScene;
        // Guest: the copy the hooks report to, and whether the host's state is being applied.
        private static LanSceneStates _guestCurrent;
        private static bool _applying;
        private static int _pressFrame = -1;

        private sealed class Entry { internal int Id; internal byte Kind; internal Component Target; internal Vector3 At; }
        private readonly bool _host;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();
        private readonly Dictionary<Component, Entry> _byTarget = new Dictionary<Component, Entry>();
        private readonly List<Entry> _objects = new List<Entry>(), _resources = new List<Entry>(), _relevant = new List<Entry>();
        private readonly Dictionary<int, int> _sent = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _usedAt = new Dictionary<int, float>();
        // Guest (1.4.5): its copies of what can be taken apart, by place key.
        private readonly Dictionary<int, ResourceContent> _parts = new Dictionary<int, ResourceContent>();
        private string _scene;
        private uint _epoch, _sequence, _received;
        private bool _ready;
        private float _indexAt = -100f, _scanAt, _sendAt;
        private int _cursor;

        internal LanSceneStates(bool host, Action<Packet> send, Action<string> log) { _host = host; _send = send; _log = log; }

        internal static void Initialize(Action<string> log)
        {
            _staticLog = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.scene-states");
            try
            {
                var field = AccessTools.Field(typeof(Zompiercer.SaveLoad.SaveLoadCore), "AllObjectID");
                _allObjectIds = field == null ? null : field.GetValue(null) as IList;
                if (_allObjectIds == null) throw new MissingFieldException("SaveLoadCore.AllObjectID");
                _bombDone = AccessTools.FieldRefAccess<DestroyableByBomb, bool>("Destroyed");
                _trapDone = AccessTools.FieldRefAccess<TrapWall, bool>("Activated");
                _collapseDone = AccessTools.FieldRefAccess<StructuralDestructionAndRepair, bool>("trapActivated");
                _collapsible = AccessTools.FieldRefAccess<StructuralDestructionAndRepair, bool>("destructible");
                _raised = AccessTools.FieldRefAccess<ObjectActivator, bool>("Activated");
                _draining = AccessTools.FieldRefAccess<WaterDrainer, bool>("Drain");
                _drained = AccessTools.FieldRefAccess<WaterDrainer, float>("CurrentDuration");
                _entered = AccessTools.FieldRefAccess<CharPresenceTrigger, bool>("ActivationLimiter");
                _onEnter = AccessTools.FieldRefAccess<CharPresenceTrigger, UnityEngine.Events.UnityEvent>("OnEnter");
                _harmony.Patch(AccessTools.Method(typeof(TrapWall), "Activate"), prefix: new HarmonyMethod(typeof(LanSceneStates), nameof(TrapPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(StructuralDestructionAndRepair), "Set"), prefix: new HarmonyMethod(typeof(LanSceneStates), nameof(CollapsePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ObjectActivator), "Interact"), prefix: new HarmonyMethod(typeof(LanSceneStates), nameof(RaisePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(CharPresenceTrigger), "OnTriggerEnter"), prefix: new HarmonyMethod(typeof(LanSceneStates), nameof(PresencePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ResourceContent), "ObjectParsed"), prefix: new HarmonyMethod(typeof(LanSceneStates), nameof(ParsedPrefix)));
            }
            catch (Exception ex)
            {
                // Without these the guest's copy of a location keeps its own walls and levers, as before 1.4.4.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Location object hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; _guestCurrent = null; }
        internal void Clear() { if (_guestCurrent == this) _guestCurrent = null; }

        private static void Once(string text) { if (_logged) return; _logged = true; _staticLog?.Invoke(text); }

        // ---- The scene's location objects, by the game's ObjectID ----

        private void Rebuild(float now)
        {
            _indexAt = now + IndexEvery;
            _byId.Clear(); _byTarget.Clear(); _objects.Clear(); _resources.Clear();
            var twice = new HashSet<int>();
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            foreach (var item in _allObjectIds)
            {
                var oid = item as ObjectID;
                if (oid == null || oid.ID == 0) continue;
                byte kind; Component target;
                if (!KindOf(oid.gameObject, out kind, out target) || Foreign(target, train)) continue;
                if (_byId.ContainsKey(oid.ID)) { twice.Add(oid.ID); continue; }
                var entry = new Entry { Id = oid.ID, Kind = kind, Target = target };
                _byId.Add(oid.ID, entry);
            }
            foreach (int id in twice) _byId.Remove(id); // an id two objects share names neither
            foreach (var entry in _byId.Values)
            {
                _byTarget[entry.Target] = entry;
                (entry.Kind == LanProtocol.SceneResource ? _resources : _objects).Add(entry);
            }
            if (_host) return;
            _parts.Clear();
            var places = new HashSet<int>();
            foreach (var part in Object.FindObjectsOfType<ResourceContent>())
            {
                if (part == null || Foreign(part, train)) continue;
                int key = PlaceKey(part);
                if (!places.Add(key)) _parts.Remove(key); // two at one place: neither
                else _parts[key] = part;
            }
        }
        // FNV-1a of the object's name and place (5 cm): the same on both sides for a scene object.
        internal static int PlaceKey(Component c)
        {
            string name = c.gameObject.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) name = name.Substring(0, clone);
            var p = c.transform.position;
            uint hash = 2166136261;
            foreach (char ch in name.Trim() + "|" + Mathf.RoundToInt(p.x * 20f) + "|" + Mathf.RoundToInt(p.y * 20f) + "|" + Mathf.RoundToInt(p.z * 20f))
            { hash ^= ch; hash *= 16777619; }
            int key = unchecked((int)hash);
            return key == 0 ? 1 : key;
        }
        private static bool KindOf(GameObject go, out byte kind, out Component target)
        {
            kind = 0;
            if ((target = go.GetComponent<DestroyableByBomb>()) != null) kind = LanProtocol.SceneBomb;
            else if ((target = go.GetComponent<TrapWall>()) != null) kind = LanProtocol.SceneTrap;
            else if ((target = go.GetComponent<StructuralDestructionAndRepair>()) != null) kind = LanProtocol.SceneCollapse;
            else if ((target = go.GetComponent<ObjectActivator>()) != null) kind = LanProtocol.SceneActivator;
            else if ((target = go.GetComponent<WaterDrainer>()) != null) kind = LanProtocol.SceneDrainer;
            else if ((target = go.GetComponent<CollectedResource>()) != null) kind = LanProtocol.SceneResource;
            else if ((target = go.GetComponent<CharPresenceTrigger>()) != null) kind = LanProtocol.SceneTrigger;
            else
            {
                var button = go.GetComponent<HardwareButton>();
                if (button != null && !LanFuelStation.IsStationButton(button)) { target = button; kind = LanProtocol.SceneButton; }
            }
            return kind != 0;
        }
        // The train, its builds and copies, players and robot dogs are not the location's.
        private static bool Foreign(Component c, FluffyUnderware.Curvy.Examples.TrainManager train)
        {
            return c.GetComponentInParent<TrainCar>() != null || c.GetComponentInParent<LanTrainReplica>() != null || c.GetComponentInParent<TrainPart>() != null ||
                c.GetComponentInParent<Furniture>() != null || c.GetComponentInParent<ZombieFighterController>() != null || c.GetComponentInParent<RobotDogAiController>() != null ||
                train != null && c.transform.IsChildOf(train.transform);
        }
        private Entry Find(int id, byte kind)
        {
            Entry entry;
            if ((!_byId.TryGetValue(id, out entry) || entry.Target == null) && Time.unscaledTime - (_indexAt - IndexEvery) > 3f) { Rebuild(Time.unscaledTime); _byId.TryGetValue(id, out entry); }
            return entry != null && entry.Target != null && entry.Kind == kind ? entry : null;
        }

        // Flags 1 when done; a swamp's drained time in `value`.
        private static byte State(Entry e, out float value)
        {
            value = 0f;
            switch (e.Kind)
            {
                case LanProtocol.SceneBomb: return (byte)(_bombDone((DestroyableByBomb)e.Target) ? 1 : 0);
                case LanProtocol.SceneTrap: return (byte)(_trapDone((TrapWall)e.Target) ? 1 : 0);
                case LanProtocol.SceneCollapse: return (byte)(_collapseDone((StructuralDestructionAndRepair)e.Target) ? 1 : 0);
                case LanProtocol.SceneActivator: return (byte)(_raised((ObjectActivator)e.Target) ? 1 : 0);
                case LanProtocol.SceneDrainer:
                    var drainer = (WaterDrainer)e.Target;
                    value = Mathf.Clamp(_drained(drainer), 0f, 99999f);
                    return (byte)(_draining(drainer) ? 1 : 0);
                case LanProtocol.SceneButton: return (byte)(((HardwareButton)e.Target).State ? 1 : 0);
                case LanProtocol.SceneTrigger: return (byte)(_entered((CharPresenceTrigger)e.Target) ? 1 : 0);
                case LanProtocol.SceneParsed: return 1;
                default:
                    // Picked clean: the game hides a resource once nothing is left (another reason to be
                    // switched off is not the guest's business).
                    return (byte)(((CollectedResource)e.Target).resourceQuantity <= 0 ? 1 : 0);
            }
        }
        // What the guest last got: the flags and the drained time to the second.
        private static int Stamp(byte flags, float value) { return flags | Mathf.RoundToInt(value) << 1; }

        // ---- Host ----

        internal void Tick(string scene, uint epoch, bool ready, Vector3? guest)
        {
            if (scene != _scene || epoch != _epoch)
            {
                _scene = scene; _epoch = epoch; _sequence = 0; _received = 0; _cursor = 0;
                _byId.Clear(); _byTarget.Clear(); _objects.Clear(); _resources.Clear(); _relevant.Clear(); _sent.Clear(); _usedAt.Clear(); _parts.Clear();
                _indexAt = -100f; _scanAt = 0f;
            }
            _ready = ready && epoch != 0 && !string.IsNullOrEmpty(scene) && Failure == null;
            if (!_host && _ready) _guestCurrent = this;
            else if (_guestCurrent == this) _guestCurrent = null;
            if (!_ready) return;
            float now = Time.unscaledTime;
            if (now >= _indexAt) Rebuild(now);
            if (!_host || guest == null) return;
            try
            {
                if (now >= _scanAt)
                {
                    // Every object of the location, and the resources picked clean near the guest.
                    _scanAt = now + ScanEvery;
                    _relevant.Clear();
                    foreach (var e in _objects) if (e.Target != null) _relevant.Add(e);
                    float far = ResourceRadius * ResourceRadius;
                    foreach (var e in _resources)
                    {
                        float ignored;
                        if (e.Target != null && State(e, out ignored) == 1 && (e.Target.transform.position - guest.Value).sqrMagnitude <= far) _relevant.Add(e);
                    }
                    if (_parsedScene == _scene)
                        foreach (var pair in Parsed)
                            if ((pair.Value - guest.Value).sqrMagnitude <= far) _relevant.Add(new Entry { Id = pair.Key, Kind = LanProtocol.SceneParsed, At = pair.Value });
                }
                if (now < _sendAt || _relevant.Count == 0) return;
                _sendAt = now + SendEvery;
                var batch = new List<SceneFrame>();
                var included = new HashSet<int>();
                foreach (var e in _relevant)
                {
                    if (batch.Count >= LanProtocol.MaxSceneBatch) break;
                    if (e.Target == null && e.Kind != LanProtocol.SceneParsed) continue;
                    float value; byte flags = State(e, out value);
                    int last;
                    if (_sent.TryGetValue(e.Id, out last) && last == Stamp(flags, value)) continue;
                    batch.Add(new SceneFrame { Id = e.Id, Kind = e.Kind, Flags = flags, Value = value }); included.Add(e.Id);
                }
                for (int visited = 0; visited < _relevant.Count && batch.Count < PerBatch; visited++)
                {
                    if (_cursor >= _relevant.Count) _cursor = 0;
                    var e = _relevant[_cursor++];
                    if (e.Target == null && e.Kind != LanProtocol.SceneParsed || included.Contains(e.Id)) continue;
                    float value; byte flags = State(e, out value);
                    batch.Add(new SceneFrame { Id = e.Id, Kind = e.Kind, Flags = flags, Value = value }); included.Add(e.Id);
                }
                if (batch.Count == 0) return;
                foreach (var frame in batch) _sent[frame.Id] = Stamp(frame.Flags, frame.Value);
                _send(new Packet { Kind = PacketKind.SceneStates, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, SceneObjects = batch.ToArray() });
            }
            catch (Exception ex) { Once("Location objects not sent: " + ex.GetType().Name + ": " + ex.Message); }
        }

        internal void Receive(Packet p, Vector3? guest)
        {
            if (!_ready || p.WorldEpoch != _epoch || p.Scene != _scene) return;
            try
            {
                if (_host) { if (p.Kind == PacketKind.SceneUse && guest != null) HostUse(p.Revision, p.Action, guest.Value); return; }
                if (p.Kind != PacketKind.SceneStates || (_received != 0 && unchecked((int)(p.Sequence - _received)) <= 0)) return;
                _received = p.Sequence;
                foreach (var frame in p.SceneObjects)
                {
                    if (frame.Kind == LanProtocol.SceneParsed) { TakeApart(frame.Id); continue; }
                    var e = Find(frame.Id, frame.Kind);
                    if (e != null) Apply(e, frame);
                }
            }
            catch (Exception ex) { Once("Location objects not applied: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // A lever, trap or bridge the guest used, done as the host's own player would.
        private void HostUse(int id, byte kind, Vector3 guest)
        {
            if (!LanProtocol.ValidSceneUse(id, kind)) return;
            var e = Find(id, kind);
            if (e == null) { _log?.Invoke("The guest's location object " + id + " is not in this world"); return; }
            float reach = kind == LanProtocol.SceneButton ? ButtonReach : kind == LanProtocol.SceneTrap ? TrapReach : kind == LanProtocol.SceneCollapse ? CollapseReach :
                kind == LanProtocol.SceneTrigger ? TriggerReach : ActivatorReach;
            if ((e.Target.transform.position - guest).sqrMagnitude > reach * reach) { _log?.Invoke("The guest's location object " + id + " is too far from the guest"); return; }
            switch (kind)
            {
                case LanProtocol.SceneButton:
                    var button = (HardwareButton)e.Target;
                    if (button.defective) return;
                    button.Switch();
                    break;
                case LanProtocol.SceneTrap:
                    ((TrapWall)e.Target).Activate();
                    break;
                case LanProtocol.SceneCollapse:
                    var floor = (StructuralDestructionAndRepair)e.Target;
                    if (!_collapsible(floor) || _collapseDone(floor)) return;
                    floor.Set(true);
                    break;
                case LanProtocol.SceneTrigger:
                    // As CharPresenceTrigger.OnTriggerEnter does for the host's own player.
                    var trigger = (CharPresenceTrigger)e.Target;
                    if (_entered(trigger)) return;
                    _entered(trigger) = true;
                    _onEnter(trigger)?.Invoke();
                    var controller = GlobalManager.global == null ? null : GlobalManager.global.controlledTrainController;
                    if (controller != null) controller.EmergencyBraking();
                    _log?.Invoke("The guest set off a trigger of the location");
                    break;
                default:
                    ((ObjectActivator)e.Target).Interact();
                    break;
            }
            _sent.Remove(id); // answer with the new state at once
            if (!_relevant.Contains(e)) _relevant.Add(e);
            _sendAt = 0f;
        }

        // ---- Guest ----

        // The host's state on the guest's copy, as the game's own load sets it.
        private static void Apply(Entry e, SceneFrame frame)
        {
            float local; byte flags = State(e, out local);
            bool done = frame.Flags == 1;
            _applying = true;
            try
            {
                switch (e.Kind)
                {
                    case LanProtocol.SceneBomb:
                        if (done && flags == 0) ((DestroyableByBomb)e.Target).Load(new object[] { true }); // nothing builds a blown wall back
                        break;
                    case LanProtocol.SceneTrap:
                        if (done && flags == 0) ((TrapWall)e.Target).Load(new object[] { true }); // the game never opens one again
                        break;
                    case LanProtocol.SceneCollapse:
                        if (done && flags == 0) ((StructuralDestructionAndRepair)e.Target).Load(new object[] { true });
                        break;
                    case LanProtocol.SceneActivator:
                        if (done && flags == 0) ((ObjectActivator)e.Target).Load(new object[] { true });
                        break;
                    case LanProtocol.SceneDrainer:
                        if (done != (flags == 1) || Mathf.Abs(local - frame.Value) > 1f) ((WaterDrainer)e.Target).Load(new object[] { done, frame.Value });
                        break;
                    case LanProtocol.SceneButton:
                        var button = (HardwareButton)e.Target;
                        if (button.State != done) button.Set(done); // its lamp, sound and what it drives, as on the host
                        break;
                    case LanProtocol.SceneTrigger:
                        if (done && flags == 0) ((CharPresenceTrigger)e.Target).Load(new object[] { true }); // spent, as after a load
                        break;
                    default:
                        if (done && flags == 0) ((CollectedResource)e.Target).Load(new object[] { 0 });
                        break;
                }
            }
            finally { _applying = false; }
        }

        // Guest: the host took this apart: so does the guest's copy (what fell out is the host's world item).
        private void TakeApart(int key)
        {
            ResourceContent part;
            if (!_parts.TryGetValue(key, out part)) return;
            _parts.Remove(key);
            if (part != null) Object.Destroy(part.gameObject);
        }

        private bool Use(Entry e)
        {
            float now = Time.unscaledTime, last;
            // A held button is pressed and let go quickly; traps and bridges may be set off frame after frame.
            if (e.Kind != LanProtocol.SceneButton && _usedAt.TryGetValue(e.Id, out last) && now - last < UseEvery) return true;
            _usedAt[e.Id] = now;
            _send(new Packet { Kind = PacketKind.SceneUse, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, Revision = e.Id, Action = e.Kind });
            return true;
        }
        private static bool GuestEntry(Component target, byte kind, out LanSceneStates owner, out Entry entry)
        {
            entry = null; owner = _guestCurrent;
            if (owner == null || _applying || target == null || !LanSaveIsolation.Active) return false;
            return owner._byTarget.TryGetValue(target, out entry) && entry.Kind == kind;
        }

        // A lever or button of the location: the host switches its own (the guest's copy follows).
        internal static bool GuestSwitch(HardwareButton button)
        {
            LanSceneStates owner; Entry e;
            if (Failure != null || !GuestEntry(button, LanProtocol.SceneButton, out owner, out e)) return false;
            if (_pressFrame == Time.frameCount) return true;
            _pressFrame = Time.frameCount;
            if (button.defective) return true; // the game does nothing either
            return owner.Use(e);
        }
        // A trap wall, a collapsing floor or a bridge set off by the guest: at once in its copy, and the
        // host sets off its own.
        private static void TrapPrefix(TrapWall __instance)
        {
            try { LanSceneStates owner; Entry e; if (GuestEntry(__instance, LanProtocol.SceneTrap, out owner, out e) && !_trapDone(__instance)) owner.Use(e); }
            catch (Exception ex) { Once("Trap wall hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void CollapsePrefix(StructuralDestructionAndRepair __instance, bool value)
        {
            try { LanSceneStates owner; Entry e; if (value && GuestEntry(__instance, LanProtocol.SceneCollapse, out owner, out e) && !_collapseDone(__instance)) owner.Use(e); }
            catch (Exception ex) { Once("Collapsing floor hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void PresencePrefix(CharPresenceTrigger __instance, Collider collider)
        {
            try
            {
                LanSceneStates owner; Entry e;
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (player == null || collider == null || collider.GetComponentInParent<ZombieFighterController>() != player) return;
                if (GuestEntry(__instance, LanProtocol.SceneTrigger, out owner, out e) && !_entered(__instance)) owner.Use(e);
            }
            catch (Exception ex) { Once("Presence trigger hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        // Host: something of the scene is taken apart (the game destroys it right after).
        private static void ParsedPrefix(ResourceContent __instance)
        {
            try
            {
                if (__instance == null || LanSaveIsolation.Active) return;
                var scenes = GlobalSceneManager.global;
                string scene = scenes == null ? null : scenes.CurrentSceneName;
                if (scene == null) return;
                if (scene != _parsedScene) { Parsed.Clear(); _parsedScene = scene; }
                if (Parsed.Count < 4096) Parsed[PlaceKey(__instance)] = __instance.transform.position;
            }
            catch (Exception ex) { Once("Taken-apart object not noted: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void RaisePrefix(ObjectActivator __instance)
        {
            try { LanSceneStates owner; Entry e; if (GuestEntry(__instance, LanProtocol.SceneActivator, out owner, out e) && !_raised(__instance)) owner.Use(e); }
            catch (Exception ex) { Once("Bridge hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
