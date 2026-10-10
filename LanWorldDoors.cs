using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Scene doors (DoorController outside the train; train doors travel with the
    // train layout). The host is authoritative: it sends the state of the doors
    // around the guest, changed doors first, the rest round-robin so a door that
    // differs in the guest's own copy of the scene is corrected within seconds.
    // The guest's E on a scene door becomes a request: the host checks that the
    // guest stands next to that door and switches it natively. 1.2.0: the host also sends
    // whether a door is locked, and a locked door yields to the guest's key in hand when the
    // host's ledger says the guest owns that key. Main thread only.
    internal sealed class LanWorldDoors
    {
        private const float Radius = 80f, ScanEvery = 4f, SendEvery = .2f, Reach = 4.5f, Match = .05f;
        private const int PerBatch = 16;
        private static Harmony _harmony;
        private static Action<string> _staticLog;
        private static bool _hookLogged;
        // Guest: handles E on a scene door; null when no session is attached.
        internal static Action<DoorController> GuestToggle;
        // Guest (1.2.0): unlocks a scene door with the key item in hand.
        internal static Action<DoorController, int> GuestUnlock;
        // Host (1.2.0): the guest's ledger (null: no keys are accepted).
        internal Func<LanGuestLedger> Ledger;
        // Guest: the door it asked to unlock, and when (its unlock sound plays when the host agrees).
        private DoorController _unlocking;
        private float _unlockingAt = -100f;

        private struct Cell : IEquatable<Cell>
        {
            internal readonly int X, Y, Z;
            internal Cell(int x, int y, int z) { X = x; Y = y; Z = z; }
            internal static Cell Of(Vector3 p) { return new Cell(Mathf.RoundToInt(p.x * 20f), Mathf.RoundToInt(p.y * 20f), Mathf.RoundToInt(p.z * 20f)); }
            public bool Equals(Cell o) { return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object o) { return o is Cell && Equals((Cell)o); }
            public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
        }

        private readonly bool _host;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly Dictionary<Cell, List<DoorController>> _index = new Dictionary<Cell, List<DoorController>>();
        private readonly List<DoorController> _near = new List<DoorController>();
        private readonly Dictionary<DoorController, byte> _sent = new Dictionary<DoorController, byte>();
        private string _scene;
        private uint _epoch, _sequence, _received;
        private bool _ready;
        private float _indexAt = -100f, _scanAt, _sendAt;
        private int _cursor;

        internal LanWorldDoors(bool host, Action<Packet> send, Action<string> log) { _host = host; _send = send; _log = log; }

        internal static void Initialize(Action<string> log)
        {
            _staticLog = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.world-doors");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(InteractingObjectsController), "Update"),
                    prefix: new HarmonyMethod(typeof(LanWorldDoors), nameof(InteractPrefix)));
            }
            catch (Exception ex) { _harmony.UnpatchSelf(); log("World door hook unavailable: " + ex.GetType().Name + ": " + ex.Message); }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; GuestToggle = null; GuestUnlock = null; }

        // Guest: E on a scene door asks the host instead of switching the local copy.
        private static void InteractPrefix()
        {
            var toggle = GuestToggle;
            if (toggle == null) return;
            try
            {
                var input = Zompiercer.Inputs.InputController.Global;
                var selection = SelectionManager.Global;
                var door = selection == null ? null : selection.HoveredDoorController;
                if (input == null || door == null || (int)input.CurrentState != 1 || InTrain(door)) return;
                bool use = input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown;
                if (!use) return;
                input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false;
                if (door.locked)
                {
                    int key = KeyInHand(door);
                    var unlock = GuestUnlock;
                    if (key >= 0 && unlock != null) { unlock(door, key); return; }
                    // 1.4.13: the game's own sound for it (some scene doors have none: it threw before the request).
                    try { door.LockedSound(); } catch (Exception) { }
                }
                toggle(door);
            }
            catch (Exception ex)
            {
                if (!_hookLogged) { _hookLogged = true; _staticLog?.Invoke("World door input hook failed: " + ex); }
            }
        }

        internal void Tick(string scene, uint epoch, bool ready, Vector3? guest)
        {
            if (scene != _scene || epoch != _epoch)
            {
                _scene = scene; _epoch = epoch; _sequence = 0; _received = 0; _cursor = 0;
                _index.Clear(); _near.Clear(); _sent.Clear(); _indexAt = -100f; _scanAt = 0;
            }
            _ready = ready && epoch != 0 && !string.IsNullOrEmpty(scene);
            if (!_host || !_ready) return;
            float now = Time.unscaledTime;
            if (guest == null) return;
            if (now >= _scanAt)
            {
                _scanAt = now + ScanEvery;
                Rebuild(now);
                _near.Clear();
                foreach (var list in _index.Values)
                    foreach (var door in list)
                        if (door != null && (door.transform.position - guest.Value).sqrMagnitude <= Radius * Radius) _near.Add(door);
                var gone = new List<DoorController>();
                foreach (var door in _sent.Keys) if (door == null || !_near.Contains(door)) gone.Add(door);
                foreach (var door in gone) _sent.Remove(door);
            }
            if (now < _sendAt || _near.Count == 0) return;
            _sendAt = now + SendEvery;
            var batch = new List<DoorFrame>();
            var included = new HashSet<DoorController>();
            // Changed doors first, then the next ones in turn.
            foreach (var door in _near)
            {
                if (batch.Count >= LanProtocol.MaxDoorBatch) break;
                byte last;
                if (door == null || _sent.TryGetValue(door, out last) && last == Flags(door)) continue;
                batch.Add(Frame(door)); included.Add(door);
            }
            for (int visited = 0; visited < _near.Count && batch.Count < PerBatch; visited++)
            {
                if (_cursor >= _near.Count) _cursor = 0;
                var door = _near[_cursor++];
                if (door == null || included.Contains(door)) continue;
                batch.Add(Frame(door)); included.Add(door);
            }
            if (batch.Count == 0) return;
            foreach (var door in included) _sent[door] = Flags(door);
            _send(new Packet { Kind = PacketKind.DoorStates, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, Doors = batch.ToArray() });
        }

        internal void Receive(Packet p, Vector3? guest)
        {
            if (!_ready || p.WorldEpoch != _epoch || p.Scene != _scene) return;
            if (_host)
            {
                if (p.Kind != PacketKind.DoorToggle || guest == null) return;
                var at = new Vector3(p.X, p.Y, p.Z);
                var door = Find(at);
                if (door == null || InTrain(door) || (door.transform.position - guest.Value).sqrMagnitude > Reach * Reach) return;
                if (p.Action == LanProtocol.DoorUnlock)
                {
                    if (door.locked && OwnsKey(p.Revision, door.keyType)) { door.locked = false; door.UnlockSound(); }
                }
                else
                {
                    bool wanted = p.Action == 1;
                    if (!door.locked && !door.broken && !door.destroyed && door.Open != wanted) door.Switch();
                }
                _sent.Remove(door); // answer with the resulting state at once
                if (!_near.Contains(door)) _near.Add(door);
                _sendAt = 0;
                return;
            }
            if (p.Kind != PacketKind.DoorStates || (_received != 0 && unchecked((int)(p.Sequence - _received)) <= 0)) return;
            _received = p.Sequence;
            foreach (var frame in p.Doors)
            {
                var door = Find(new Vector3(frame.X, frame.Y, frame.Z));
                if (door == null || InTrain(door)) continue;
                bool open = (frame.Flags & (LanProtocol.DoorOpenFlag | LanProtocol.DoorBrokenFlag)) != 0; // a broken door no longer blocks the way
                if (door.Open != open) door.Set(open);
                bool locked = (frame.Flags & LanProtocol.DoorLockedFlag) != 0;
                if (door.locked != locked)
                {
                    door.locked = locked;
                    if (!locked && door == _unlocking && Time.unscaledTime - _unlockingAt < 5f) { door.UnlockSound(); _unlocking = null; }
                }
            }
        }

        // Guest (1.2.0): ask the host to unlock this door with key item `key` (in hand).
        internal void RequestUnlock(DoorController door, int key)
        {
            if (_host || !_ready || door == null || key < 0 || key > LanProtocol.MaxKeyItem) return;
            _unlocking = door; _unlockingAt = Time.unscaledTime;
            var p = door.transform.position;
            _send(new Packet { Kind = PacketKind.DoorToggle, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, X = p.x, Y = p.y, Z = p.z, Action = LanProtocol.DoorUnlock, Revision = key });
        }

        // Guest: the key item in the selected belt slot that fits this door (as the game checks), or -1.
        private static int KeyInHand(DoorController door)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var inventories = InventoryController.global;
            if (player == null || inventories == null || player.beltInventory == null) return -1;
            int slot = inventories.selectedBeltIcon;
            if (slot < 0 || slot >= player.beltInventory.Length || player.beltInventory[slot] == null) return -1;
            var content = player.beltInventory[slot].content;
            if (content == null || content.Count == 0 || content[0] == null) return -1;
            var item = content[0];
            var number = item.GetComponent<KeyNumber>();
            return number != null && number.keyNumber == door.keyType ? item.itemID : -1;
        }

        // Host: the guest's ledger holds key item `key` with this door's number.
        private bool OwnsKey(int key, int number)
        {
            var book = Ledger == null ? null : Ledger();
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            if (book == null || prefabs == null || key < 0 || key >= prefabs.Length || prefabs[key] == null || prefabs[key].GetComponent<KeyNumber>() == null) return false;
            try { return book.Has(LanStorage.Normalize(new object[] { key, 1, new object[] { number, null, null, null } })); }
            catch (System.IO.InvalidDataException) { return false; }
        }

        // Guest: ask the host to switch this door.
        internal void RequestToggle(DoorController door)
        {
            if (_host || !_ready || door == null) return;
            var p = door.transform.position;
            _send(new Packet { Kind = PacketKind.DoorToggle, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, X = p.x, Y = p.y, Z = p.z, Action = (byte)(door.Open ? 0 : 1) });
        }

        private DoorController Find(Vector3 at)
        {
            var door = Lookup(at);
            if (door == null && Rebuild(Time.unscaledTime)) door = Lookup(at);
            return door;
        }

        private DoorController Lookup(Vector3 at)
        {
            var c = Cell.Of(at);
            DoorController best = null; float bestDistance = Match * Match;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        List<DoorController> list;
                        if (!_index.TryGetValue(new Cell(c.X + x, c.Y + y, c.Z + z), out list)) continue;
                        foreach (var door in list)
                        {
                            if (door == null) continue;
                            float d = (door.transform.position - at).sqrMagnitude;
                            if (d <= bestDistance) { best = door; bestDistance = d; }
                        }
                    }
            return best;
        }

        // At most one scene search per three seconds.
        private bool Rebuild(float now)
        {
            if (now - _indexAt < 3f) return false;
            _indexAt = now;
            _index.Clear();
            foreach (var door in Object.FindObjectsOfType<DoorController>())
            {
                if (door == null || InTrain(door) || door.GetComponentInParent<ZombieFighterController>() != null) continue;
                var cell = Cell.Of(door.transform.position);
                List<DoorController> list;
                if (!_index.TryGetValue(cell, out list)) _index.Add(cell, list = new List<DoorController>());
                list.Add(door);
            }
            return true;
        }

        private static bool InTrain(DoorController door)
        {
            var manager = GlobalManager.global;
            var train = manager == null ? null : manager.controlledTrain;
            return door.GetComponentInParent<LanTrainReplica>() != null || door.GetComponentInParent<Zompiercer.Train.TrainCar>() != null ||
                train != null && door.transform.IsChildOf(train.transform);
        }
        private static byte Flags(DoorController door)
        {
            return (byte)((door.Open ? LanProtocol.DoorOpenFlag : 0) | (door.broken || door.destroyed ? LanProtocol.DoorBrokenFlag : 0) | (door.locked ? LanProtocol.DoorLockedFlag : 0));
        }
        private static DoorFrame Frame(DoorController door)
        {
            var p = door.transform.position;
            return new DoorFrame { X = p.x, Y = p.y, Z = p.z, Flags = Flags(door) };
        }
    }
}
