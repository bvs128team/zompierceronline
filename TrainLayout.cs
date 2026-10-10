using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluffyUnderware.Curvy.Examples;
using UnityEngine;
using Zompiercer.Train;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // A visual, non-interactive copy of the host's train construction and of the items
    // placed on it. Inventory contents, fuel and other gameplay state are deliberately
    // outside this format. Version 6 (1.4.0) adds paint, wires and sign texts (TrainDecor); version 7
    // (1.4.1) gives each of their records its length, so one record a guest cannot read is skipped.
    internal static class TrainLayout
    {
        internal const int MaximumBytes = 256 * 1024;
        private const int MaximumObjects = 2048;
        private const int MaximumDoorsPerObject = 32;
        private const int MaximumBaseDoorsPerCar = 64;
        private const uint Magic = 0x32544C5A; // ZLT2
        private const byte Version = 7;
        private const int MaximumDoorPathBytes = 256;
        private const byte FurnitureKind = 1;
        private const byte PartKind = 2;
        internal const byte FurnitureKindId = FurnitureKind, PartKindId = PartKind;
        private const byte ItemKind = 3;        // an InventoryItem placed on a car (ParentToTrainCar)
        private const int MaximumItems = 512;
        private const int IgnoreRaycastLayer = 2;
        // The shipped Character layer ignores layer 2. Physical passenger support
        // must use Ground; native interaction rays are filtered separately.
        internal const int SupportLayer = 10;
        private static Runtime _runtime;
        private static readonly HashSet<Collider> GroundColliders = new HashSet<Collider>();

        internal static bool HasGroundSupport(ZombieFighterController player)
        {
            return _runtime != null && _runtime.Initialized && _runtime.Train != null &&
                GlobalManager.global != null && GlobalManager.global.controlledChar == player &&
                GlobalManager.global.controlledTrain == _runtime.Train &&
                GlobalSceneManager.global != null && !GlobalSceneManager.global.SceneCurrentlyLoading;
        }

        internal static bool IsGroundSupport(Collider collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                !collider.isTrigger && GroundColliders.Contains(collider);
        }

        private sealed class Entry
        {
            internal byte Kind;
            internal ushort Car;
            internal int Identity;
            internal int Prefab;
            internal int X, Y, Z;
            internal ushort RX, RY, RZ;
            internal bool Blueprint;
            internal int Progress;
            internal DoorState[] Doors;
            // 1.4.0: paint, wires and sign text (null: none, or not carried).
            internal TrainDecor Decor;

            internal long Key { get { return ((long)Kind << 32) | (uint)Identity; } }

            internal bool SameGeometry(Entry other)
            {
                return other != null && Kind == other.Kind && Car == other.Car &&
                    Prefab == other.Prefab && X == other.X && Y == other.Y && Z == other.Z &&
                    RX == other.RX && RY == other.RY && RZ == other.RZ &&
                    Blueprint == other.Blueprint;
            }
        }

        private sealed class DoorState
        {
            internal string Path;
            internal bool Open;
        }

        private sealed class Native
        {
            internal Component Component;
            internal Entry Layout;
            internal Renderer[] Renderers;
            internal bool[] RendererEnabled;
            internal Collider[] Colliders;
            internal bool[] ColliderEnabled;
            internal GameObject[] ColliderObjects;
            internal int[] ColliderLayers;
            internal MonoBehaviour[] Behaviours;
            internal bool[] BehaviourEnabled;
            internal DoorController[] Doors;
            internal bool[] DoorOpen;
            internal bool[] DoorInteractive;
            // 1.4.0: its surfaces, inputs, switch and sign text, as the host's decor addresses them.
            internal Paintable[] Paintables;
            internal LogicInput[] Inputs;
            internal LogicOutput Output;
            internal UnityEngine.UI.Text SignText;

            internal void SetVisible(bool visible)
            {
                for (int i = 0; i < Renderers.Length; i++)
                    if (Renderers[i] != null) Renderers[i].enabled = visible && RendererEnabled[i];
                for (int i = 0; i < Colliders.Length; i++)
                    if (Colliders[i] != null)
                        Colliders[i].enabled = visible && ColliderEnabled[i] && !Colliders[i].isTrigger;
                for (int i = 0; i < ColliderObjects.Length; i++)
                    if (ColliderObjects[i] != null) ColliderObjects[i].layer = SupportLayer;
                // Native train furniture remains present for the game's references.
                for (int i = 0; i < Behaviours.Length; i++)
                    if (Behaviours[i] != null) Behaviours[i].enabled = false;
                foreach (var door in Doors)
                    if (door != null) door.notInteractive = true;
            }

            internal void Restore()
            {
                for (int i = 0; i < Doors.Length; i++)
                    if (Doors[i] != null)
                    {
                        SetDoor(Doors[i], DoorOpen[i], true);
                        Doors[i].notInteractive = DoorInteractive[i];
                    }
                for (int i = 0; i < Renderers.Length; i++)
                    if (Renderers[i] != null) Renderers[i].enabled = RendererEnabled[i];
                for (int i = 0; i < Colliders.Length; i++)
                    if (Colliders[i] != null) Colliders[i].enabled = ColliderEnabled[i];
                for (int i = 0; i < ColliderObjects.Length; i++)
                    if (ColliderObjects[i] != null) ColliderObjects[i].layer = ColliderLayers[i];
                for (int i = 0; i < Behaviours.Length; i++)
                    if (Behaviours[i] != null) Behaviours[i].enabled = BehaviourEnabled[i];
            }
        }

        private sealed class Replica
        {
            internal Component Component;
            internal Entry Layout;
            internal ReplicaDoor[] Doors;
            // 1.4.11: a blueprint copy shown (and solid) now; null: as made or reset by a commit.
            internal bool? BlueprintShown;
        }

        private sealed class ReplicaDoor
        {
            internal string Path;
            // Where the door controller was (1.1.6): the guest's look ray may hit its frame.
            internal Transform Controller;
            internal Transform[] Leaves;
            internal Vector3[] OpenPositions, ClosePositions, OpenRotations, CloseRotations;
            internal bool Open;
        }

        private sealed class Runtime
        {
            internal TrainManager Train;
            internal TrainCar[] Cars;
            internal readonly List<Native> Natives = new List<Native>();
            internal readonly Dictionary<long, Replica> Replicas = new Dictionary<long, Replica>();
            internal Dictionary<long, Entry> Applied = new Dictionary<long, Entry>();
            // Native objects standing in for the host's, by the host's key (1.4.0 decor).
            internal Dictionary<long, Native> ShownNatives = new Dictionary<long, Native>();
            internal DoorController[][] BaseDoors;
            internal bool[][] BaseDoorOpen;
            internal bool[][] BaseDoorInteractive;
            internal DoorController[] RootDoors;
            internal bool[] RootDoorOpen;
            internal bool[] RootDoorInteractive;
            internal bool Initialized;
            // Host identity of every shown object: native component or replica marker.
            internal readonly Dictionary<Component, Entry> Hosted = new Dictionary<Component, Entry>();
            internal Pending Pending;
            internal float ConstructionTokens = MaximumObjects, LastConstructionBudget;
            // Guest construction (0.8.1): mount points as the host has them.
            internal readonly HashSet<Component> UnusedNatives = new HashSet<Component>();
            internal readonly Dictionary<long, List<Vector3>> Occupied = new Dictionary<long, List<Vector3>>();
            internal readonly List<KeyValuePair<TrainMountPoint, TrainPart>> Released = new List<KeyValuePair<TrainMountPoint, TrainPart>>();
            internal readonly Dictionary<TrainMountPoint, bool> Usable = new Dictionary<TrainMountPoint, bool>();
        }

        // One replacement at a time; newly created objects remain inactive until commit.
        private sealed class Pending
        {
            internal Snapshot Snapshot;
            internal Entry[] Entries;
            internal int Index;
            internal readonly HashSet<Native> Used = new HashSet<Native>();
            internal readonly Dictionary<long, Native> Natives = new Dictionary<long, Native>();
            internal readonly Dictionary<long, Replica> Replicas = new Dictionary<long, Replica>();
            internal readonly List<Replica> Created = new List<Replica>();
        }

        private sealed class Snapshot
        {
            internal Dictionary<long, Entry> Entries;
            internal DoorState[][] BaseDoors;
            internal DoorState[] RootDoors;
        }

        internal static byte[] Capture(TrainManager train)
        {
            var cars = ResolveCars(train);
            var entries = new List<Entry>();
            var baseDoors = new DoorController[cars.Length][];
            var rootDoors = FindRootDoors(train, cars);
            int items = 0;
            if (rootDoors.Length > MaximumBaseDoorsPerCar)
                throw new InvalidOperationException("Train root has too many doors");
            for (ushort i = 0; i < cars.Length; i++)
            {
                var car = cars[i];
                baseDoors[i] = FindBaseDoors(car);
                if (baseDoors[i].Length > MaximumBaseDoorsPerCar)
                    throw new InvalidOperationException("Train car has too many base doors");
                if (car.AllFurniture == null) throw new InvalidOperationException("Train furniture list missing");
                foreach (var furniture in car.AllFurniture)
                {
                    if (furniture == null) continue;
                    if (furniture.GetComponentInParent<LanTrainReplica>() != null) continue;
                    var entry = MakeEntry(FurnitureKind, i, furniture, furniture.PrefabID,
                        furniture.notAssembled, furniture.CompletionBarCurrent, car);
                    if (!entry.Blueprint) entry.Decor = LanTrainDecor.Capture(furniture);
                    entries.Add(entry);
                }
                foreach (var part in car.GetComponentsInChildren<TrainPart>(true))
                {
                    if (part == null || part.GetComponentInParent<LanTrainReplica>() != null) continue;
                    var entry = MakeEntry(PartKind, i, part, part.PrefabID,
                        part.Blueprint, part.BuildPointsCurrent, car);
                    if (!entry.Blueprint) entry.Decor = LanTrainDecor.Capture(part);
                    entries.Add(entry);
                }
                foreach (var item in car.GetComponentsInChildren<InventoryItem>(false))
                {
                    if (items >= MaximumItems) break;
                    if (!PlacedItem(item, car)) continue;
                    entries.Add(MakeEntry(ItemKind, i, item, item.itemID, false, 0f, car));
                    items++;
                }
            }
            if (entries.Count > MaximumObjects) throw new InvalidOperationException("Train layout exceeds 2048 objects");
            foreach (var entry in entries) ValidatePrefab(entry, cars[entry.Car]);
            entries.Sort((a, b) =>
            {
                int byCar = a.Car.CompareTo(b.Car);
                if (byCar != 0) return byCar;
                int byKind = a.Kind.CompareTo(b.Kind);
                return byKind != 0 ? byKind : a.Identity.CompareTo(b.Identity);
            });
            using (var stream = new MemoryStream(16 + entries.Count * 36))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((ushort)cars.Length);
                writer.Write((ushort)entries.Count);
                for (int i = 0; i < baseDoors.Length; i++)
                    WriteDoors(writer, cars[i].transform, baseDoors[i]);
                WriteDoors(writer, train.transform, rootDoors);
                foreach (var e in entries) Write(writer, e);
                if (stream.Length > MaximumBytes) throw new InvalidOperationException("Train layout exceeds 256 KiB");
                // 1.4.0: [count, then entry index, length (1.4.1) and TrainDecor] for as many decorated
                // objects as still fit; construction never gives way to decor.
                var decor = new List<KeyValuePair<int, byte[]>>();
                long room = MaximumBytes - stream.Length - 2;
                DecorDropped = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Decor == null) continue;
                    var bytes = entries[i].Decor.Encode();
                    if (bytes.Length > TrainDecor.MaxBytes || room < 4 + bytes.Length) { DecorDropped++; continue; }
                    room -= 4 + bytes.Length;
                    decor.Add(new KeyValuePair<int, byte[]>(i, bytes));
                }
                writer.Write((ushort)decor.Count);
                foreach (var pair in decor) { writer.Write((ushort)pair.Key); writer.Write((ushort)pair.Value.Length); writer.Write(pair.Value); }
                if (stream.Length > MaximumBytes) throw new InvalidOperationException("Train layout exceeds 256 KiB");
                return stream.ToArray();
            }
        }

        // Host: decorated objects the last layout had no room for (0: all fitted). Guest: decor records
        // of the last layout it could not read (1.4.1), shown without their decor.
        internal static int DecorDropped { get; private set; }
        internal static int DecorSkipped { get; private set; }

        internal static void Apply(TrainManager train, byte[] bytes)
        {
            var cars = ResolveCars(train);
            var snapshot = Decode(bytes, train, cars);
            var desired = snapshot.Entries;
            Runtime state = _runtime;
            if (state == null || state.Train != train)
            {
                Cleanup();
                state = new Runtime { Train = train, Cars = cars };
                CollectNatives(state);
                _runtime = state;
            }

            CancelPending();
            var entries = new Entry[desired.Count];
            desired.Values.CopyTo(entries, 0);
            state.Pending = new Pending { Snapshot = snapshot, Entries = entries };
        }

        internal static bool HasPending { get { return _runtime != null && _runtime.Pending != null; } }

        internal static void CancelPending()
        {
            if (_runtime == null || _runtime.Pending == null) return;
            foreach (var replica in _runtime.Pending.Created)
                if (replica.Component != null) Object.Destroy(replica.Component.gameObject);
            _runtime.Pending = null;
        }

        // Checked budget: an individual Unity Instantiate cannot be interrupted.
        internal static bool Advance(TrainManager train)
        {
            var state = _runtime;
            if (state == null || state.Train != train || state.Pending == null) return false;
            var pending = state.Pending;
            float now = Time.unscaledTime;
            state.ConstructionTokens = Mathf.Min(MaximumObjects, state.ConstructionTokens +
                Mathf.Max(0, now - state.LastConstructionBudget) * 64f);
            state.LastConstructionBudget = now;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int processed = 0;
            try
            {
                while (pending.Index < pending.Entries.Length && processed < 8 && watch.Elapsed.TotalMilliseconds < 3)
                {
                    var e = pending.Entries[pending.Index];
                    var native = FindNative(state.Natives, pending.Used, e, state.Cars[e.Car]);
                    if (native != null)
                    {
                        pending.Used.Add(native);
                        pending.Natives.Add(e.Key, native);
                    }
                    else
                    {
                        if (EmbeddedFurniture(e, state.Cars[e.Car]))
                            throw new InvalidOperationException("Embedded locomotive furniture missing from client prefab");
                        Replica replica;
                        if (!state.Replicas.TryGetValue(e.Key, out replica) || replica.Component == null || !replica.Layout.SameGeometry(e))
                        {
                            if (state.ConstructionTokens < 1f) return false;
                            state.ConstructionTokens -= 1f;
                            replica = CreateReplica(e, state.Cars[e.Car]);
                            pending.Created.Add(replica);
                        }
                        if (replica.Doors == null) throw new InvalidDataException("Existing train replica doors missing");
                        pending.Replicas.Add(e.Key, replica);
                    }
                    pending.Index++;
                    processed++;
                }
                if (pending.Index != pending.Entries.Length) return false;
                // Check again at commit: the player can board during preparation.
                int playerCar; Vector3 playerAt;
                PlayerSpot(state, out playerCar, out playerAt);
                if (PlayerAboard(train) && (!state.Initialized || RemovesGeometry(state.Applied, pending.Snapshot.Entries, playerCar, playerAt)))
                    throw new InvalidOperationException("Train layout change deferred while the local player is aboard");
                Commit(state, pending);
                state.Pending = null;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.IndexOf("deferred while", StringComparison.Ordinal) < 0) CancelPending();
                throw;
            }
            catch { CancelPending(); throw; }
        }

        private static void Commit(Runtime state, Pending pending)
        {
            var snapshot = pending.Snapshot;
            var desired = snapshot.Entries;
            var nativesByKey = pending.Natives;
            var usedNatives = pending.Used;
            var nextReplicas = pending.Replicas;
            var cars = state.Cars;
            var train = state.Train;
            // Commit contains no construction. All replacement objects are prepared.
            foreach (var pair in nativesByKey)
                ApplyDoors(pair.Value.Component.transform, pair.Value.Doors, desired[pair.Key].Doors, !state.Initialized);
            foreach (var native in state.Natives)
                native.SetVisible(usedNatives.Contains(native));
            for (int i = 0; i < state.BaseDoors.Length; i++)
            {
                ApplyDoors(cars[i].transform, state.BaseDoors[i], snapshot.BaseDoors[i], !state.Initialized);
                foreach (var door in state.BaseDoors[i]) door.notInteractive = true;
            }
            ApplyDoors(train.transform, state.RootDoors, snapshot.RootDoors, !state.Initialized);
            var retained = new HashSet<Replica>(nextReplicas.Values);
            foreach (var old in state.Replicas)
                if (!retained.Contains(old.Value) && old.Value.Component != null)
                {
                    old.Value.Component.gameObject.SetActive(false);
                    Object.Destroy(old.Value.Component.gameObject);
                }
            state.Replicas.Clear();
            foreach (var pair in nextReplicas) state.Replicas.Add(pair.Key, pair.Value);
            foreach (var pair in nextReplicas)
            {
                var e = desired[pair.Key];
                var replica = pair.Value;
                var pickupMarker = replica.Component as LanTrainReplica;
                if (e.Kind == ItemKind && replica.Layout.Progress != e.Progress && pickupMarker != null)
                { pickupMarker.PickedUp = false; replica.Component.gameObject.SetActive(true); }
                replica.Layout = e;
                var marker = replica.Component as LanTrainReplica;
                if (marker != null) marker.Functional = !e.Blueprint;
                ApplyReplicaDoors(replica.Doors, e.Doors);
                foreach (var collider in replica.Component.GetComponentsInChildren<Collider>(true))
                    SetReplicaCollider(collider, e);
                replica.BlueprintShown = null;
            }
            foreach (var replica in pending.Created) replica.Component.gameObject.SetActive(true);
            state.ShownNatives = new Dictionary<long, Native>(nativesByKey);
            ApplyDecor(nativesByKey, nextReplicas, desired);
            GroundColliders.Clear();
            foreach (var native in usedNatives)
                foreach (var collider in native.Colliders)
                    if (collider != null && collider.enabled && !collider.isTrigger) GroundColliders.Add(collider);
            foreach (var replica in nextReplicas.Values)
                if (replica.Layout.Kind != ItemKind && !replica.Layout.Blueprint)
                foreach (var collider in replica.Component.GetComponentsInChildren<Collider>(true))
                    if (collider.enabled && !collider.isTrigger) GroundColliders.Add(collider);
            state.Applied = desired;
            state.Hosted.Clear();
            foreach (var pair in nativesByKey) state.Hosted[pair.Value.Component] = desired[pair.Key];
            foreach (var pair in nextReplicas) state.Hosted[pair.Value.Component] = desired[pair.Key];
            RefreshMountPoints(state, usedNatives, desired);
            state.Initialized = true;
            Physics.SyncTransforms();
        }

        // 1.4.0: the host's paint, wires and sign texts on every shown object. Switches are found by
        // the host id of their part first, so an input may take its signal from any shown part.
        private static void ApplyDecor(Dictionary<long, Native> natives, Dictionary<long, Replica> replicas, Dictionary<long, Entry> desired)
        {
            var outputs = new Dictionary<int, LogicOutput>();
            foreach (var pair in natives)
            {
                var e = desired[pair.Key];
                if (e.Kind == PartKind && !e.Blueprint && pair.Value.Output != null) outputs[e.Identity] = pair.Value.Output;
            }
            foreach (var pair in replicas)
            {
                var e = desired[pair.Key]; var marker = pair.Value.Component as LanTrainReplica;
                if (e.Kind == PartKind && !e.Blueprint && marker != null && marker.Output != null) outputs[e.Identity] = marker.Output;
            }
            foreach (var pair in natives)
            {
                var e = desired[pair.Key]; var native = pair.Value;
                if (e.Kind == ItemKind || e.Blueprint) continue;
                // The native's scripts are switched off while it stands in for the host's object; its sign text shows.
                if (native.SignText != null) native.SignText.enabled = true;
                LanTrainDecor.Apply(e.Decor, native.Paintables, native.Inputs, native.SignText, outputs);
            }
            foreach (var pair in replicas)
            {
                var e = desired[pair.Key]; var marker = pair.Value.Component as LanTrainReplica;
                if (marker == null || e.Kind == ItemKind || e.Blueprint) continue;
                LanTrainDecor.Apply(e.Decor, marker.Paintables, marker.Inputs, marker.SignText, outputs);
            }
        }

        // Guest (1.4.0): the host's decor as last received, again (a change of the guest's the host did not make).
        internal static void ReapplyDecor()
        {
            var state = _runtime;
            if (state == null || !state.Initialized || state.Pending != null) return;
            ApplyDecor(state.ShownNatives, state.Replicas, state.Applied);
        }

        // Guest (1.4.0): the shown host object (built furniture or part) that `start` belongs to,
        // its host identity, and the component standing for it here (native object or copy).
        internal static bool TryIdentifyDecor(Transform start, out int car, out int kind, out int identity, out Component shown)
        {
            car = kind = identity = 0; shown = null;
            var state = _runtime;
            if (start == null || state == null || !state.Initialized) return false;
            for (var t = start; t != null; t = t.parent)
            {
                Entry entry = null; Component found = null;
                foreach (var candidate in new Component[] { t.GetComponent<LanTrainReplica>(), t.GetComponent<Furniture>(), t.GetComponent<TrainPart>() })
                    if (candidate != null && state.Hosted.TryGetValue(candidate, out entry)) { found = candidate; break; }
                if (found == null)
                {
                    if (t.GetComponent<TrainCar>() != null) return false;
                    continue;
                }
                if (entry.Blueprint || (entry.Kind != FurnitureKind && entry.Kind != PartKind)) return false;
                car = entry.Car; kind = entry.Kind; identity = entry.Identity; shown = found;
                return true;
            }
            return false;
        }
        // The surfaces and wire inputs of a shown object, in the host's order.
        internal static Paintable[] DecorPaintables(Component shown)
        {
            var marker = shown as LanTrainReplica;
            if (marker != null) return marker.Paintables ?? new Paintable[0];
            var native = NativeOf(shown);
            return native == null || native.Paintables == null ? new Paintable[0] : native.Paintables;
        }
        internal static LogicInput[] DecorInputs(Component shown)
        {
            var marker = shown as LanTrainReplica;
            if (marker != null) return marker.Inputs ?? new LogicInput[0];
            var native = NativeOf(shown);
            return native == null || native.Inputs == null ? new LogicInput[0] : native.Inputs;
        }
        private static Native NativeOf(Component shown)
        {
            var state = _runtime;
            if (state == null || shown == null) return null;
            foreach (var native in state.Natives) if (native.Component == shown) return native;
            return null;
        }
        // Guest (1.4.0): a shown copy of a host sign under `collider`, and what it reads.
        internal static bool TryIdentifySign(Collider collider, out Component shown, out string text)
        {
            text = null; shown = null;
            int car, kind, identity;
            if (collider == null || !TryIdentifyDecor(collider.transform, out car, out kind, out identity, out shown)) return false;
            var marker = shown as LanTrainReplica;
            var sign = marker != null ? marker.SignText : NativeOf(shown) == null ? null : NativeOf(shown).SignText;
            if (sign == null) return false;
            text = sign.text;
            return true;
        }
        // The components of `root` that belong to it rather than to a furniture or part inside it.
        internal static T[] OwnedBy<T>(Component root) where T : Component
        {
            return root == null ? new T[0] : Owned(root, root.GetComponentsInChildren<T>(true));
        }

        // Guest: the host's identity of a shown train object that has a storage, if
        // `collider` belongs to one. Blueprints never qualify. The host decides
        // whether that storage may be opened.
        internal static bool TryIdentifyStorage(Collider collider, out int car, out int kind, out int identity)
        {
            car = kind = identity = 0;
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return false;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                Component found = null;
                Entry entry = null;
                foreach (var candidate in new Component[] { t.GetComponent<LanTrainReplica>(), t.GetComponent<Furniture>(), t.GetComponent<TrainPart>() })
                    if (candidate != null && state.Hosted.TryGetValue(candidate, out entry)) { found = candidate; break; }
                if (found == null)
                {
                    if (t.GetComponent<TrainCar>() != null) return false;
                    continue;
                }
                if (entry.Blueprint || !HasStorage(found, entry, state.Cars[entry.Car])) return false;
                car = entry.Car; kind = entry.Kind; identity = entry.Identity;
                return true;
            }
            return false;
        }

        // Guest: the host's identity of an item placed on the train, if `collider` belongs to its copy.
        internal static bool TryIdentifyItem(Collider collider, out int hostId, out int itemId)
        { int amount; return TryIdentifyItem(collider, out hostId, out itemId, out amount); }
        internal static bool TryIdentifyItem(Collider collider, out int hostId, out int itemId, out int amount)
        {
            hostId = itemId = amount = 0;
            var state = _runtime;
            var marker = collider == null ? null : collider.GetComponentInParent<LanTrainReplica>();
            Entry entry;
            if (marker == null || marker.PickedUp || state == null || !state.Hosted.TryGetValue(marker, out entry) || entry.Kind != ItemKind) return false;
            hostId = entry.Identity; itemId = entry.Prefab; amount = entry.Progress;
            return true;
        }
        internal static void HidePickedItem(int hostId)
        {
            if (_runtime == null) return;
            foreach (var pair in _runtime.Hosted)
                if (pair.Value.Kind == ItemKind && pair.Value.Identity == hostId)
                {
                    var marker = pair.Key as LanTrainReplica;
                    if (marker != null) { marker.PickedUp = true; marker.gameObject.SetActive(false); }
                }
        }

        // Guest: the workbench data (recipes) of a shown train object, from the native
        // object or, for a script-free copy, from its furniture prefab.
        internal static Workbench WorkbenchOf(Collider collider)
        {
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return null;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                foreach (var candidate in new Component[] { t.GetComponent<LanTrainReplica>(), t.GetComponent<Furniture>() })
                {
                    Entry entry;
                    if (candidate == null || !state.Hosted.TryGetValue(candidate, out entry)) continue;
                    if (entry.Kind != FurnitureKind || entry.Blueprint) return null;
                    if (candidate is Furniture) return candidate.GetComponentInChildren<Workbench>(true);
                    var db = ItemsDataBase.Global;
                    if (db == null || db.AllFurniture == null || entry.Prefab >= db.AllFurniture.Count || db.AllFurniture[entry.Prefab] == null) return null;
                    return db.AllFurniture[entry.Prefab].GetComponentInChildren<Workbench>(true);
                }
                if (t.GetComponent<TrainCar>() != null) return null;
            }
            return null;
        }

        private static bool HasStorage(Component shown, Entry e, TrainCar car)
        {
            if (e.Kind == ItemKind) return false;
            if (shown is Furniture) return HasFurnitureStorage((Furniture)shown);
            if (shown is TrainPart) return ((TrainPart)shown).inventory != null || shown.GetComponent<Inventory>() != null;
            if (e.Kind == FurnitureKind)
            {
                if (EmbeddedFurniture(e, car)) return false;
                var db = ItemsDataBase.Global;
                return db != null && db.AllFurniture != null && e.Prefab < db.AllFurniture.Count && HasFurnitureStorage(db.AllFurniture[e.Prefab]);
            }
            var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
            return parts != null && parts.Parts != null && e.Prefab < parts.Parts.Count && parts.Parts[e.Prefab] != null &&
                (parts.Parts[e.Prefab].inventory != null || parts.Parts[e.Prefab].GetComponent<Inventory>() != null);
        }

        private static bool HasFurnitureStorage(Furniture furniture)
        {
            if (furniture == null) return false;
            // Furniture/Workbench.inventory is assigned by Awake, which never runs
            // on an asset prefab. Replicas have their native scripts removed, so
            // inspect the prefab's components without activating or modifying it.
            if (furniture.inventory != null || furniture.GetComponent<Inventory>() != null) return true;
            var bench = furniture.GetComponentInChildren<Workbench>(true);
            return bench != null && (bench.inventory != null || bench.GetComponent<Inventory>() != null);
        }

        private static void AddStairZones(List<StairZone> zones, Component stair, Transform up)
        {
            if (stair == null || up == null) return;
            foreach (var box in stair.GetComponentsInChildren<BoxCollider>(true))
                zones.Add(new StairZone { Box = box.transform, Center = box.center, Size = box.size, Up = up });
        }

        // Guest: the nearest stair or ladder zone of a functional copied part on the look ray
        // within `range`, with where it leads.
        internal static bool StairZoneInView(Vector3 origin, Vector3 direction, float range, out Transform up, out float distance)
        {
            up = null; distance = float.MaxValue;
            foreach (var replica in LanTrainReplica.All)
            {
                if (replica == null || !replica.Functional || replica.StairZones == null || !replica.isActiveAndEnabled) continue;
                foreach (var zone in replica.StairZones)
                {
                    if (zone.Box == null || zone.Up == null) continue;
                    var local = new Ray(zone.Box.InverseTransformPoint(origin), zone.Box.InverseTransformVector(direction));
                    float along;
                    if (!new Bounds(zone.Center, zone.Size).IntersectRay(local, out along)) continue;
                    float d = Vector3.Distance(origin, zone.Box.TransformPoint(local.GetPoint(along)));
                    if (d <= range && d < distance) { distance = d; up = zone.Up; }
                }
            }
            return up != null;
        }

        // Built copies are solid (Ground support); blueprints and placed items stay on layer 2: the guest's
        // own look-at ray finds them (to build, to pick up). Layer 2 still meets the character (the game's
        // collision matrix is the default one): blueprint copies follow ShowBlueprints (1.4.11).
        private static void SetReplicaCollider(Collider collider, Entry e)
        {
            if (collider.isTrigger) { collider.enabled = false; return; }
            collider.enabled = true;
            collider.gameObject.layer = e.Blueprint || e.Kind == ItemKind ? IgnoreRaycastLayer : SupportLayer;
        }

        // Guest: a host blueprint part or unassembled furniture shown under `collider` (1.4.2: or, with
        // `built`, a built part or assembled furniture, to take apart or repair).
        internal sealed class BuildTarget { internal int Car, Kind, Identity, Prefab; internal float Progress; internal bool Blueprint; internal Component Shown; }
        internal static BuildTarget TryIdentifyBuild(Collider collider, bool built = false)
        {
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return null;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                Entry entry = null; Component shown = null;
                foreach (var candidate in new Component[] { t.GetComponent<LanTrainReplica>(), t.GetComponent<Furniture>(), t.GetComponent<TrainPart>() })
                    if (candidate != null && state.Hosted.TryGetValue(candidate, out entry)) { shown = candidate; break; }
                if (shown == null)
                {
                    if (t.GetComponent<TrainCar>() != null) return null;
                    continue;
                }
                if (entry.Blueprint == built || (entry.Kind != FurnitureKind && entry.Kind != PartKind)) return null;
                return new BuildTarget { Car = entry.Car, Kind = entry.Kind, Identity = entry.Identity, Prefab = entry.Prefab, Progress = entry.Progress / 1000f, Blueprint = entry.Blueprint, Shown = shown };
            }
            return null;
        }

        // Guest (1.4.11): blueprint copies are seen and stood on as the game's own blueprints: only while the
        // player holds a tool or plans a part (TrainConstructor then shows them, SetBlueprintVisible turns their
        // renderers and colliders on). A floor blueprint was solid underfoot all the time before.
        internal static void ShowBlueprints(bool shown)
        {
            var state = _runtime;
            if (state == null) return;
            foreach (var replica in state.Replicas.Values)
            {
                if (replica.Component == null || replica.Layout == null || !replica.Layout.Blueprint || replica.BlueprintShown == shown) continue;
                replica.BlueprintShown = shown;
                foreach (var renderer in replica.Component.GetComponentsInChildren<Renderer>(true)) renderer.enabled = shown;
                foreach (var collider in replica.Component.GetComponentsInChildren<Collider>(true)) if (!collider.isTrigger) collider.enabled = shown;
            }
        }

        // Guest (1.4.11): whether a built part has a fully built part on one of its own mount points, as the
        // host's TrainPart.IsCanBeDeconstructed checks it. An attached part sits at its point (the game finds
        // it there on load: same mount type, under 0.1 m, FindMountPointInPosition), so the car-relative
        // places of the layout tell, for the guest's own natives and for the copies alike.
        internal static bool HasAttachedParts(BuildTarget target, Func<int, TrainPart> prefabOf)
        {
            var state = _runtime;
            if (state == null || !state.Initialized || target == null || target.Kind != PartKind) return false;
            Entry self = null;
            foreach (var e in state.Applied.Values) if (e.Kind == PartKind && e.Car == target.Car && e.Identity == target.Identity) { self = e; break; }
            var prefab = self == null ? null : prefabOf(self.Prefab);
            if (prefab == null || prefab.SubMountPoints == null) return false;
            var pose = Matrix4x4.TRS(Place(self), Turn(self), prefab.transform.localScale);
            foreach (var point in prefab.SubMountPoints)
            {
                if (point == null) continue;
                var at = pose.MultiplyPoint3x4(prefab.transform.InverseTransformPoint(point.transform.position));
                foreach (var e in state.Applied.Values)
                {
                    if (e.Kind != PartKind || e.Car != self.Car || e.Blueprint || e == self) continue;
                    var other = prefabOf(e.Prefab);
                    if (other == null || other.TypeID != point.ID || e.Progress / 1000f < other.BuildPointsMaximum - .001f) continue;
                    if ((Place(e) - at).sqrMagnitude < .01f) return true;
                }
            }
            return false;
        }
        private static Vector3 Place(Entry e) { return new Vector3(e.X / 1000f, e.Y / 1000f, e.Z / 1000f); }
        private static Quaternion Turn(Entry e) { return Quaternion.Euler(e.RX / 10f, e.RY / 10f, e.RZ / 10f); }

        // Guest: the host's identity of a built part or furniture with a light switch (0.10.0),
        // shown as a script-free copy or as the guest's own native object.
        internal static bool TryIdentifyLight(Collider collider, out int car, out int kind, out int identity)
        { return TryIdentifyLight(collider == null ? null : collider.transform, out car, out kind, out identity); }
        internal static bool TryIdentifyLight(Transform start, out int car, out int kind, out int identity)
        {
            car = kind = identity = 0;
            var state = _runtime;
            if (start == null || state == null || !state.Initialized) return false;
            for (var t = start; t != null; t = t.parent)
            {
                Component found = null;
                Entry entry = null;
                foreach (var candidate in new Component[] { t.GetComponent<LanTrainReplica>(), t.GetComponent<Furniture>(), t.GetComponent<TrainPart>() })
                    if (candidate != null && state.Hosted.TryGetValue(candidate, out entry)) { found = candidate; break; }
                if (found == null)
                {
                    if (t.GetComponent<TrainCar>() != null) return false;
                    continue;
                }
                if (entry.Blueprint || (entry.Kind != FurnitureKind && entry.Kind != PartKind)) return false;
                Component owner = found;
                if (found is LanTrainReplica)
                {
                    owner = null;
                    if (entry.Kind == FurnitureKind)
                    {
                        var db = ItemsDataBase.Global;
                        if (db != null && db.AllFurniture != null && entry.Prefab < db.AllFurniture.Count) owner = db.AllFurniture[entry.Prefab];
                    }
                    else
                    {
                        var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
                        if (parts != null && parts.Parts != null && entry.Prefab < parts.Parts.Count) owner = parts.Parts[entry.Prefab];
                    }
                }
                if (LanTrainControl.SwitchOf(owner) == null) return false;
                car = entry.Car; kind = entry.Kind; identity = entry.Identity;
                return true;
            }
            return false;
        }

        // Guest: the car of a shown script-free copy of a host object that carries a fuel tank.
        internal static bool TryIdentifyFuelTank(Collider collider, out int car)
        {
            car = -1;
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return false;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                Entry entry;
                var marker = t.GetComponent<LanTrainReplica>();
                if (marker != null && state.Hosted.TryGetValue(marker, out entry))
                {
                    if (entry.Blueprint) return false;
                    Component prefab = null;
                    if (entry.Kind == FurnitureKind)
                    {
                        var db = ItemsDataBase.Global;
                        if (db != null && db.AllFurniture != null && entry.Prefab < db.AllFurniture.Count) prefab = db.AllFurniture[entry.Prefab];
                    }
                    else if (entry.Kind == PartKind)
                    {
                        var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
                        if (parts != null && parts.Parts != null && entry.Prefab < parts.Parts.Count) prefab = parts.Parts[entry.Prefab];
                    }
                    if (prefab == null || prefab.GetComponentInChildren<TrainFuelTank>(true) == null) return false;
                    car = entry.Car; return true;
                }
                if (t.GetComponent<TrainCar>() != null) return false;
            }
            return false;
        }

        // Guest (1.2.2): a shown script-free copy of a host bed (not a sleeping bag).
        internal static bool TryIdentifyBed(Collider collider)
        {
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return false;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                Entry entry;
                var marker = t.GetComponent<LanTrainReplica>();
                if (marker != null && state.Hosted.TryGetValue(marker, out entry))
                {
                    if (entry.Blueprint) return false;
                    Component prefab = null;
                    if (entry.Kind == FurnitureKind)
                    {
                        var db = ItemsDataBase.Global;
                        if (db != null && db.AllFurniture != null && entry.Prefab < db.AllFurniture.Count) prefab = db.AllFurniture[entry.Prefab];
                    }
                    else if (entry.Kind == PartKind)
                    {
                        var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
                        if (parts != null && parts.Parts != null && entry.Prefab < parts.Parts.Count) prefab = parts.Parts[entry.Prefab];
                    }
                    var bed = prefab == null ? null : prefab.GetComponentInChildren<SleepController>(true);
                    return bed != null && !bed.sleepbag;
                }
                if (t.GetComponent<TrainCar>() != null) return false;
            }
            return false;
        }

        // Guest (1.2.2): the car of a shown script-free copy of a host object with a rainwater tank,
        // and that tank's capacity (from the prefab).
        internal static bool TryIdentifyWaterTank(Collider collider, out int car, out float max)
        {
            car = -1; max = 0f;
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return false;
            for (var t = collider.transform; t != null; t = t.parent)
            {
                Entry entry;
                var marker = t.GetComponent<LanTrainReplica>();
                if (marker != null && state.Hosted.TryGetValue(marker, out entry))
                {
                    if (entry.Blueprint) return false;
                    Component prefab = null;
                    if (entry.Kind == FurnitureKind)
                    {
                        var db = ItemsDataBase.Global;
                        if (db != null && db.AllFurniture != null && entry.Prefab < db.AllFurniture.Count) prefab = db.AllFurniture[entry.Prefab];
                    }
                    else if (entry.Kind == PartKind)
                    {
                        var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
                        if (parts != null && parts.Parts != null && entry.Prefab < parts.Parts.Count) prefab = parts.Parts[entry.Prefab];
                    }
                    var tank = prefab == null ? null : prefab.GetComponentInChildren<RainwaterTank>(true);
                    if (tank == null) return false;
                    car = entry.Car; max = tank.VolumeMax; return true;
                }
                if (t.GetComponent<TrainCar>() != null) return false;
            }
            return false;
        }

        // The index both sides use for `car` (the order of the train's resolved cars), or -1.
        internal static int CarIndex(TrainManager train, TrainCar car)
        {
            if (train == null || car == null) return -1;
            try { return Array.IndexOf(ResolveCars(train), car); }
            catch (Exception) { return -1; }
        }

        // ---- Guest construction (0.8.1): the guest's mount points as the host has them ----
        // Hidden own parts of the guest neither offer points nor hold them; points the host's
        // parts take (same car, type and car-local place) are taken.
        private static void RefreshMountPoints(Runtime state, HashSet<Native> used, Dictionary<long, Entry> desired)
        {
            ReleaseRestore(state);
            state.UnusedNatives.Clear(); state.Occupied.Clear(); state.Usable.Clear();
            foreach (var native in state.Natives)
                if (native.Component != null && !used.Contains(native)) state.UnusedNatives.Add(native.Component);
            foreach (var component in state.UnusedNatives)
            {
                var part = component as TrainPart;
                var point = part == null ? null : part.AttachedToPoint;
                if (point != null && point.AttachedPart == part)
                { state.Released.Add(new KeyValuePair<TrainMountPoint, TrainPart>(point, part)); point.AttachedPart = null; }
            }
            var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
            foreach (var e in desired.Values)
            {
                if (e.Kind != PartKind || parts == null || parts.Parts == null || e.Prefab >= parts.Parts.Count || parts.Parts[e.Prefab] == null) continue;
                long key = PointKey(e.Car, parts.Parts[e.Prefab].TypeID);
                List<Vector3> list;
                if (!state.Occupied.TryGetValue(key, out list)) state.Occupied[key] = list = new List<Vector3>();
                list.Add(new Vector3(e.X / 1000f, e.Y / 1000f, e.Z / 1000f));
            }
        }
        private static void ReleaseRestore(Runtime state)
        {
            foreach (var pair in state.Released)
                if (pair.Key != null && pair.Value != null && pair.Key.AttachedPart == null) pair.Key.AttachedPart = pair.Value;
            state.Released.Clear();
        }
        private static long PointKey(int car, int type) { return ((long)car << 32) | (uint)type; }

        // Guest: may the construction mode use this point (host-consistent and free)?
        internal static bool GuestPointUsable(TrainMountPoint point)
        {
            var state = _runtime;
            if (point == null || state == null || !state.Initialized) return false;
            bool usable;
            if (state.Usable.TryGetValue(point, out usable)) return usable;
            usable = PointUsable(state, point);
            state.Usable[point] = usable;
            return usable;
        }
        private static bool PointUsable(Runtime state, TrainMountPoint point)
        {
            TrainCar car = null;
            for (var t = point.transform.parent; t != null; t = t.parent)
            {
                if (t.GetComponent<LanTrainReplica>() != null)
                {
                    // Upgrades preview on the part itself (UpgradePreviewer): not on a script-free copy.
                    if (point.ID == 38 || point.ID == 39) return false;
                    break;
                }
                Component owner = t.GetComponent<TrainPart>();
                if (owner == null) owner = t.GetComponent<Furniture>();
                if (owner != null) { if (state.UnusedNatives.Contains(owner)) return false; break; }
                if ((car = t.GetComponent<TrainCar>()) != null) break;
            }
            if (car == null) car = point.GetComponentInParent<TrainCar>();
            int index = Array.IndexOf(state.Cars, car);
            if (index < 0) return false;
            List<Vector3> taken;
            if (state.Occupied.TryGetValue(PointKey(index, point.ID), out taken))
            {
                var local = car.transform.InverseTransformPoint(point.transform.position);
                foreach (var at in taken) if ((at - local).sqrMagnitude < .0009f) return false; // 3 cm
            }
            return true;
        }

        internal static void Cleanup()
        {
            GroundColliders.Clear();
            if (_runtime == null) return;
            ReleaseRestore(_runtime);
            CancelPending();
            foreach (var native in _runtime.Natives) native.Restore();
            for (int i = 0; i < _runtime.BaseDoors.Length; i++)
                for (int j = 0; j < _runtime.BaseDoors[i].Length; j++)
                {
                    var door = _runtime.BaseDoors[i][j];
                    if (door == null) continue;
                    SetDoor(door, _runtime.BaseDoorOpen[i][j], true);
                    door.notInteractive = _runtime.BaseDoorInteractive[i][j];
                }
            for (int i = 0; i < _runtime.RootDoors.Length; i++)
            {
                var door = _runtime.RootDoors[i];
                if (door == null) continue;
                SetDoor(door, _runtime.RootDoorOpen[i], true);
                door.notInteractive = _runtime.RootDoorInteractive[i];
            }
            foreach (var replica in _runtime.Replicas.Values)
                if (replica.Component != null) Object.Destroy(replica.Component.gameObject);
            _runtime = null;
        }

        internal static TrainCar GetCar(TrainManager train, int index)
        {
            var cars = ResolveCars(train);
            if (index < 0 || index >= cars.Length) throw new ArgumentOutOfRangeException("index");
            return cars[index];
        }

        private static TrainCar[] ResolveCars(TrainManager train)
        {
            if (train == null || train.Cars == null || train.Cars.Length == 0 || train.Cars.Length > 64)
                throw new InvalidOperationException("Train cars unavailable");
            var cars = new TrainCar[train.Cars.Length];
            for (int i = 0; i < cars.Length; i++)
            {
                if (train.Cars[i] == null) throw new InvalidOperationException("Train car missing");
                var found = train.Cars[i].GetComponentsInChildren<TrainCar>(true);
                if (found.Length != 1 || found[0].GetCarIndex() != i)
                    throw new InvalidOperationException("Train car mapping is ambiguous");
                cars[i] = found[0];
            }
            return cars;
        }

        private static Entry MakeEntry(byte kind, ushort carIndex, Component component, int prefab,
            bool blueprint, float progress, TrainCar car)
        {
            var p = car.transform.InverseTransformPoint(component.transform.position);
            var r = (Quaternion.Inverse(car.transform.rotation) * component.transform.rotation).eulerAngles;
            var doors = Owned(component, component.GetComponentsInChildren<DoorController>(true));
            if (doors.Length > MaximumDoorsPerObject)
                throw new InvalidOperationException("Train object has too many doors");
            return new Entry
            {
                Kind = kind, Car = carIndex, Identity = component.GetInstanceID(), Prefab = prefab,
                X = Position(p.x), Y = Position(p.y), Z = Position(p.z),
                RX = Rotation(r.x), RY = Rotation(r.y), RZ = Rotation(r.z),
                Blueprint = blueprint, Progress = kind == ItemKind ? ((InventoryItem)component).amount : Progress(progress), Doors = DoorStates(component.transform, doors)
            };
        }

        // Host: an item lying on this car, not in any inventory or hand.
        private static bool PlacedItem(InventoryItem item, TrainCar car)
        {
            if (item == null || item.inInventory != null || item.ParentedToTraincCar != car || !item.gameObject.activeInHierarchy ||
                item.GetComponentInParent<LanTrainReplica>() != null || item.transform.parent == null ||
                item.transform.parent.GetComponentInParent<InventoryItem>() != null) return false;
            var db = ItemsDataBase.Global;
            if (db == null || db.itemPrefab == null || item.itemID < 0 || item.itemID >= db.itemPrefab.Length || db.itemPrefab[item.itemID] == null) return false;
            var p = car.transform.InverseTransformPoint(item.transform.position);
            return Finite(p.x) && Finite(p.y) && Finite(p.z) && Mathf.Abs(p.x) <= 256f && Mathf.Abs(p.y) <= 256f && Mathf.Abs(p.z) <= 256f;
        }

        private static int Position(float value)
        {
            if (!Finite(value) || Mathf.Abs(value) > 256f)
                throw new InvalidOperationException("Train object position outside car bounds");
            return Mathf.RoundToInt(value * 1000f);
        }

        private static ushort Rotation(float value)
        {
            if (!Finite(value)) throw new InvalidOperationException("Invalid train object rotation");
            return (ushort)(Mathf.RoundToInt(Mathf.Repeat(value, 360f) * 10f) % 3600);
        }

        private static int Progress(float value)
        {
            if (!Finite(value) || value < 0f || value > 100000f)
                throw new InvalidOperationException("Invalid train object build progress");
            return Mathf.RoundToInt(value * 1000f);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static void Write(BinaryWriter writer, Entry e)
        {
            writer.Write(e.Kind); writer.Write(e.Car); writer.Write(e.Identity); writer.Write(e.Prefab);
            writer.Write(e.X); writer.Write(e.Y); writer.Write(e.Z);
            writer.Write(e.RX); writer.Write(e.RY); writer.Write(e.RZ);
            writer.Write(e.Blueprint); writer.Write(e.Progress);
            WriteDoorStates(writer, e.Doors);
        }

        private static Snapshot Decode(byte[] bytes, TrainManager train, TrainCar[] cars)
        {
            if (bytes == null || bytes.Length < 9 || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Invalid train layout size");
            var entries = new Dictionary<long, Entry>();
            var baseDoorStates = new DoorState[cars.Length][];
            DoorState[] rootDoorStates;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version ||
                    reader.ReadUInt16() != cars.Length)
                    throw new InvalidDataException("Incompatible train layout");
                int count = reader.ReadUInt16();
                if (count > MaximumObjects || bytes.Length < 10 + cars.Length + count * 35)
                    throw new InvalidDataException("Invalid train object count");
                for (int car = 0; car < cars.Length; car++)
                {
                    baseDoorStates[car] = ReadDoors(reader, MaximumBaseDoorsPerCar);
                }
                rootDoorStates = ReadDoors(reader, MaximumBaseDoorsPerCar);
                var ordered = new List<Entry>(count);
                for (int i = 0; i < count; i++)
                {
                    var e = new Entry
                    {
                        Kind = reader.ReadByte(), Car = reader.ReadUInt16(),
                        Identity = reader.ReadInt32(), Prefab = reader.ReadInt32(),
                        X = reader.ReadInt32(), Y = reader.ReadInt32(), Z = reader.ReadInt32(),
                        RX = reader.ReadUInt16(), RY = reader.ReadUInt16(), RZ = reader.ReadUInt16(),
                        Blueprint = ReadFlag(reader), Progress = reader.ReadInt32()
                    };
                    e.Doors = ReadDoors(reader, MaximumDoorsPerObject);
                    if ((e.Kind != FurnitureKind && e.Kind != PartKind && e.Kind != ItemKind) || e.Car >= cars.Length ||
                        e.Identity == 0 || e.X < -256000 || e.X > 256000 ||
                        e.Y < -256000 || e.Y > 256000 || e.Z < -256000 || e.Z > 256000 ||
                        e.RX >= 3600 || e.RY >= 3600 || e.RZ >= 3600 ||
                        e.Progress < 0 || e.Progress > 100000000)
                        throw new InvalidDataException("Invalid train object field");
                    ValidatePrefab(e, cars[e.Car]);
                    if (entries.ContainsKey(e.Key)) throw new InvalidDataException("Duplicate train object identity");
                    entries.Add(e.Key, e);
                    ordered.Add(e);
                }
                int decorated = reader.ReadUInt16();
                if (decorated > count) throw new InvalidDataException("Invalid train decor count");
                var seen = new HashSet<int>();
                int skipped = 0;
                for (int i = 0; i < decorated; i++)
                {
                    int index = reader.ReadUInt16(), length = reader.ReadUInt16();
                    if (index >= ordered.Count || !seen.Add(index) || length < 1 || length > TrainDecor.MaxBytes) throw new InvalidDataException("Invalid train decor object");
                    var blob = reader.ReadBytes(length);
                    if (blob.Length != length) throw new EndOfStreamException();
                    var e = ordered[index];
                    if (e.Kind == ItemKind || e.Blueprint) { skipped++; continue; }
                    try { e.Decor = TrainDecor.Decode(blob); }
                    catch (InvalidDataException) { skipped++; }
                }
                DecorSkipped = skipped;
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing train layout data");
            }
            return new Snapshot { Entries = entries, BaseDoors = baseDoorStates,
                RootDoors = rootDoorStates };
        }

        private static void ValidatePrefab(Entry e, TrainCar car)
        {
            if (e.Prefab < 0) throw new InvalidDataException("Negative train prefab ID");
            if (e.Kind == ItemKind)
            {
                var items = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
                if (items == null || e.Prefab >= items.Length || items[e.Prefab] == null || items[e.Prefab].itemID != e.Prefab || e.Blueprint || e.Doors.Length != 0 || e.Progress < 1 || e.Progress > LanStorage.MaxAmount)
                    throw new InvalidDataException("Unknown placed item ID " + e.Prefab);
                return;
            }
            if (e.Kind == FurnitureKind)
            {
                if (EmbeddedFurniture(e, car)) return;
                var db = ItemsDataBase.Global;
                if (db == null || db.AllFurniture == null || e.Prefab >= db.AllFurniture.Count ||
                    db.AllFurniture[e.Prefab] == null)
                    throw new InvalidDataException("Unknown furniture prefab ID " + e.Prefab);
            }
            else
            {
                var ctor = TrainConstructor.Global;
                var db = ctor == null ? null : ctor.PartsDataBase;
                if (db == null || db.Parts == null || e.Prefab >= db.Parts.Count ||
                    db.Parts[e.Prefab] == null)
                    throw new InvalidDataException("Unknown train part prefab ID " + e.Prefab);
            }
        }

        private static bool ReadFlag(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new InvalidDataException("Invalid train object flag");
            return value != 0;
        }

        private static bool EmbeddedFurniture(Entry e, TrainCar car)
        {
            var parent = car.transform.parent;
            return e.Kind == FurnitureKind && e.Prefab == 0 &&
                parent != null && parent.name == "TrainCarA";
        }

        private static DoorController[] FindBaseDoors(TrainCar car)
        {
            var result = new List<DoorController>();
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            foreach (var door in car.GetComponentsInChildren<DoorController>(true))
            {
                if (door == null || (player != null && door.transform.IsChildOf(player.transform))) continue;
                bool ownedByObject = false;
                var current = door.transform;
                while (current != null && current != car.transform)
                {
                    if (current.GetComponent<LanTrainReplica>() != null ||
                        current.GetComponent<Furniture>() != null || current.GetComponent<TrainPart>() != null)
                    { ownedByObject = true; break; }
                    current = current.parent;
                }
                if (!ownedByObject)
                {
                    ValidateDoor(door);
                    result.Add(door);
                }
            }
            return result.ToArray();
        }

        private static DoorController[] FindRootDoors(TrainManager train, TrainCar[] cars)
        {
            var result = new List<DoorController>();
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            foreach (var door in train.GetComponentsInChildren<DoorController>(true))
            {
                if (door == null || (player != null && door.transform.IsChildOf(player.transform))) continue;
                bool underCar = false;
                foreach (var car in cars)
                    if (door.transform.IsChildOf(car.transform)) { underCar = true; break; }
                if (underCar || door.GetComponentInParent<LanTrainReplica>() != null ||
                    door.GetComponentInParent<Furniture>() != null ||
                    door.GetComponentInParent<TrainPart>() != null) continue;
                ValidateDoor(door);
                result.Add(door);
            }
            return result.ToArray();
        }

        private static bool[] DoorStates(DoorController[] doors)
        {
            var states = new bool[doors.Length];
            for (int i = 0; i < doors.Length; i++)
            {
                ValidateDoor(doors[i]);
                states[i] = doors[i].Open;
            }
            return states;
        }

        private static DoorState[] DoorStates(Transform root, DoorController[] doors)
        {
            var states = new DoorState[doors.Length];
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doors.Length; i++)
            {
                ValidateDoor(doors[i]);
                string path = DoorPath(root, doors[i]);
                if (!paths.Add(path)) throw new InvalidOperationException("Duplicate train door path");
                states[i] = new DoorState { Path = path, Open = doors[i].Open };
            }
            return states;
        }

        private static string DoorPath(Transform root, DoorController door)
        {
            var segments = new List<string>();
            Transform current = door.transform;
            while (current != root)
            {
                if (current == null) throw new InvalidOperationException("Train door is outside its owner");
                int sameNameBefore = 0;
                var parent = current.parent;
                if (parent != null)
                    for (int i = 0; i < current.GetSiblingIndex(); i++)
                        if (parent.GetChild(i).name == current.name) sameNameBefore++;
                segments.Add(current.name.Length + ":" + current.name + "#" + sameNameBefore);
                current = parent;
            }
            segments.Reverse();
            var components = door.GetComponents<DoorController>();
            int ordinal = Array.IndexOf(components, door);
            if (ordinal < 0) throw new InvalidOperationException("Train door component missing");
            return string.Join("/", segments.ToArray()) + "@" + ordinal;
        }

        private static void WriteDoors(BinaryWriter writer, Transform root, DoorController[] doors)
        {
            WriteDoorStates(writer, DoorStates(root, doors));
        }

        private static void WriteDoorStates(BinaryWriter writer, DoorState[] doors)
        {
            writer.Write((byte)doors.Length);
            foreach (var door in doors)
            {
                byte[] path = Encoding.UTF8.GetBytes(door.Path);
                if (path.Length == 0 || path.Length > MaximumDoorPathBytes)
                    throw new InvalidOperationException("Train door path too long");
                writer.Write((ushort)path.Length);
                writer.Write(path);
                writer.Write(door.Open);
            }
        }

        private static DoorState[] ReadDoors(BinaryReader reader, int maximum)
        {
            int count = reader.ReadByte();
            if (count > maximum) throw new InvalidDataException("Invalid train door count");
            var doors = new DoorState[count];
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadUInt16();
                if (length == 0 || length > MaximumDoorPathBytes)
                    throw new InvalidDataException("Invalid train door path size");
                byte[] bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                string path = Encoding.UTF8.GetString(bytes);
                if (!paths.Add(path)) throw new InvalidDataException("Duplicate train door path");
                doors[i] = new DoorState { Path = path, Open = ReadFlag(reader) };
            }
            return doors;
        }

        private static void ValidateDoor(DoorController door)
        {
            if (door == null || door.Doors == null)
                throw new InvalidOperationException("Train door controller is incomplete");
            foreach (var leaf in door.Doors)
                if (leaf == null) throw new InvalidOperationException("Train door leaf is missing");
        }

        private static void ApplyDoors(Transform root, DoorController[] doors, DoorState[] desired, bool force)
        {
            if (doors == null || desired == null) throw new InvalidDataException("Train doors missing");
            var byPath = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var state in desired) byPath.Add(state.Path, state.Open);
            for (int i = 0; i < doors.Length; i++)
            {
                bool open;
                if (byPath.TryGetValue(DoorPath(root, doors[i]), out open))
                    SetDoor(doors[i], open, force);
                doors[i].notInteractive = true;
            }
        }

        private static void SetDoor(DoorController door, bool open, bool force)
        {
            if (door == null) return;
            bool stale = door.Open != open;
            if (!stale && door.Doors != null)
                foreach (var leaf in door.Doors)
                    if (leaf != null && leaf.Open != open) { stale = true; break; }
            if (!force && !stale) return;
            if (open) door.SetOpenInstant();
            else door.SetCloseInstant();
        }

        private static void CollectNatives(Runtime state)
        {
            state.BaseDoors = new DoorController[state.Cars.Length][];
            state.BaseDoorOpen = new bool[state.Cars.Length][];
            state.BaseDoorInteractive = new bool[state.Cars.Length][];
            state.RootDoors = FindRootDoors(state.Train, state.Cars);
            if (state.RootDoors.Length > MaximumBaseDoorsPerCar)
                throw new InvalidOperationException("Train root has too many doors");
            state.RootDoorOpen = DoorStates(state.RootDoors);
            state.RootDoorInteractive = Array.ConvertAll(state.RootDoors, x => x.notInteractive);
            for (ushort i = 0; i < state.Cars.Length; i++)
            {
                var car = state.Cars[i];
                state.BaseDoors[i] = FindBaseDoors(car);
                if (state.BaseDoors[i].Length > MaximumBaseDoorsPerCar)
                    throw new InvalidOperationException("Train car has too many base doors");
                state.BaseDoorOpen[i] = DoorStates(state.BaseDoors[i]);
                state.BaseDoorInteractive[i] = Array.ConvertAll(state.BaseDoors[i], x => x.notInteractive);
                if (car.AllFurniture != null)
                    foreach (var furniture in car.AllFurniture)
                        if (furniture != null && furniture.GetComponentInParent<LanTrainReplica>() == null)
                            state.Natives.Add(SnapshotNative(furniture,
                                MakeEntry(FurnitureKind, i, furniture, furniture.PrefabID,
                                    furniture.notAssembled, furniture.CompletionBarCurrent, car)));
                foreach (var part in car.GetComponentsInChildren<TrainPart>(true))
                    if (part != null && part.GetComponentInParent<LanTrainReplica>() == null)
                        state.Natives.Add(SnapshotNative(part,
                            MakeEntry(PartKind, i, part, part.PrefabID,
                                part.Blueprint, part.BuildPointsCurrent, car)));
            }
        }

        private static Native SnapshotNative(Component component, Entry entry)
        {
            var native = new Native { Component = component, Layout = entry };
            native.Renderers = Owned(component, component.GetComponentsInChildren<Renderer>(true));
            native.Colliders = Owned(component, component.GetComponentsInChildren<Collider>(true));
            native.Behaviours = Owned(component, component.GetComponentsInChildren<MonoBehaviour>(true));
            native.Doors = Owned(component, component.GetComponentsInChildren<DoorController>(true));
            native.DoorOpen = DoorStates(native.Doors);
            native.DoorInteractive = Array.ConvertAll(native.Doors, x => x.notInteractive);
            native.RendererEnabled = Array.ConvertAll(native.Renderers, x => x.enabled);
            native.ColliderEnabled = Array.ConvertAll(native.Colliders, x => x.enabled);
            var colliderObjects = new List<GameObject>();
            foreach (var collider in native.Colliders)
                if (!colliderObjects.Contains(collider.gameObject)) colliderObjects.Add(collider.gameObject);
            native.ColliderObjects = colliderObjects.ToArray();
            native.ColliderLayers = Array.ConvertAll(native.ColliderObjects, x => x.layer);
            native.BehaviourEnabled = Array.ConvertAll(native.Behaviours, x => x.enabled);
            native.Paintables = OwnedBy<Paintable>(component);
            native.Inputs = OwnedBy<LogicInput>(component);
            var outputs = OwnedBy<LogicOutput>(component);
            native.Output = outputs.Length > 0 ? outputs[0] : null;
            var signs = OwnedBy<TextSign>(component);
            native.SignText = signs.Length > 0 ? signs[0].text : null;
            return native;
        }

        private static T[] Owned<T>(Component root, T[] values) where T : Component
        {
            var result = new List<T>();
            var manager = GlobalManager.global;
            var player = manager == null ? null : manager.controlledChar;
            foreach (var value in values)
            {
                if (value == null) continue;
                if (player != null && value.transform.IsChildOf(player.transform)) continue;
                if (value is TrainCar || value is TrainController || value is TrainPlatform ||
                    value is TrainManager || value is ZombieFighterController) continue;
                var t = value.transform;
                bool owned = true;
                while (t != root.transform)
                {
                    if (t.GetComponent<Furniture>() != null || t.GetComponent<TrainPart>() != null ||
                        t.GetComponent<ZombieFighterController>() != null)
                    { owned = false; break; }
                    t = t.parent;
                    if (t == null) { owned = false; break; }
                }
                if (owned) result.Add(value);
            }
            return result.ToArray();
        }

        private static Native FindNative(List<Native> natives, HashSet<Native> used, Entry e, TrainCar car)
        {
            foreach (var native in natives)
                if (!used.Contains(native) && native.Component != null &&
                    (native.Layout.SameGeometry(e) ||
                     (EmbeddedFurniture(e, car) &&
                      native.Layout.Kind == e.Kind && native.Layout.Car == e.Car &&
                      native.Layout.Prefab == 0)))
                    return native;
            return null;
        }

        private static Replica CreateReplica(Entry e, TrainCar car)
        {
            Component copy = null;
            var staging = new GameObject("LAN train replica staging");
            staging.SetActive(false);
            try
            {
                if (e.Kind == ItemKind)
                    copy = Object.Instantiate(ItemsDataBase.Global.itemPrefab[e.Prefab], staging.transform, false);
                else if (e.Kind == FurnitureKind)
                {
                    var furniture = Object.Instantiate(ItemsDataBase.Global.AllFurniture[e.Prefab], staging.transform, false);
                    copy = furniture;
                    furniture.InTrainCar = null;
                    furniture.notAssembled = e.Blueprint;
                    furniture.SetCompletionBarCurrent(e.Progress / 1000f);
                }
                else
                {
                    var part = Object.Instantiate(TrainConstructor.Global.PartsDataBase.Parts[e.Prefab], staging.transform, false);
                    copy = part;
                    part.Blueprint = e.Blueprint;
                    part.Preview = false;
                    part.BuildPointsCurrent = e.Progress / 1000f;
                }
                copy.gameObject.SetActive(false);
                var marker = copy.gameObject.AddComponent<LanTrainReplica>();
                marker.Functional = !e.Blueprint;
                var stairs = copy.GetComponentsInChildren<Stair>(true);
                var simpleStairs = copy.GetComponentsInChildren<SimpleStair>(true);
                marker.StairTargets = new Transform[stairs.Length + simpleStairs.Length];
                for (int i = 0; i < stairs.Length; i++) marker.StairTargets[i] = stairs[i].UpPosition;
                for (int i = 0; i < simpleStairs.Length; i++) marker.StairTargets[stairs.Length + i] = simpleStairs[i].UpPosition;
                // 1.1.1: a ladder is used through a trigger box (the copy switches triggers off and
                // may have no solid rungs), so its boxes are kept as zones for the guest's look ray.
                var zones = new List<StairZone>();
                foreach (var stair in stairs) AddStairZones(zones, stair, stair.UpPosition);
                foreach (var stair in simpleStairs) AddStairZones(zones, stair, stair.UpPosition);
                marker.StairZones = zones.ToArray();
                copy.transform.SetParent(car.transform, false);
                copy.transform.localPosition = new Vector3(e.X / 1000f, e.Y / 1000f, e.Z / 1000f);
                copy.transform.localRotation = Quaternion.Euler(e.RX / 10f, e.RY / 10f, e.RZ / 10f);
                if (e.Blueprint)
                {
                    if (copy is Furniture) ((Furniture)copy).TurnInBlueprint();
                    else ((TrainPart)copy).TurnInBlueprint();
                }
                else if (copy is TrainPart)
                {
                    var built = (TrainPart)copy;
                    MaterialsRestorer.RestoreMaterials(copy.gameObject, built);
                    built.CallUpdateFrameVisibility();
                }
                var doors = Owned(copy, copy.GetComponentsInChildren<DoorController>(true));
                ApplyDoors(copy.transform, doors, e.Doors, true);
                var visualDoors = CaptureReplicaDoors(copy.transform, doors);
                foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                    if (collider.isTrigger) collider.enabled = false;
                foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }
                // Keep visual-only objects out of interaction rays. Solid colliders
                // must instead collide with the shipped Character layer.
                foreach (var transform in copy.GetComponentsInChildren<Transform>(true))
                    transform.gameObject.layer = IgnoreRaycastLayer;
                // Placed items stay on layer 2: the guest's look-at ray finds them, the character does not stand on them.
                foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                    SetReplicaCollider(collider, e);
                foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true))
                { audio.playOnAwake = false; audio.enabled = false; }
                foreach (var animator in copy.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                // Remove native scripts while INACTIVE. Merely disabling them still
                // permits Awake when activated. Door poses survive as plain transforms.
                var root = copy.gameObject;
                // 1.4.0: a built object keeps its painted surfaces, wire inputs, switch and sign text
                // (plain data, no Update): the host's decor shows on them, the game's paint and wire
                // modes work on them.
                var kept = new HashSet<MonoBehaviour>();
                if (!e.Blueprint && e.Kind != ItemKind)
                {
                    marker.Paintables = OwnedBy<Paintable>(copy);
                    marker.Inputs = OwnedBy<LogicInput>(copy);
                    var outputs = OwnedBy<LogicOutput>(copy);
                    marker.Output = outputs.Length > 0 ? outputs[0] : null;
                    var signs = OwnedBy<TextSign>(copy);
                    marker.SignText = signs.Length > 0 ? signs[0].text : null;
                    foreach (var p in marker.Paintables) kept.Add(p);
                    foreach (var input in marker.Inputs) kept.Add(input);
                    foreach (var output in outputs) kept.Add(output);
                    if (marker.SignText != null) kept.Add(marker.SignText);
                }
                // Mount points (plain data, no Awake/Update) stay: the guest's construction
                // mode builds on the host's parts too. Their part is the prefab, never the copy.
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (!(behaviour is LanTrainReplica) && !(behaviour is TrainMountPoint) && !kept.Contains(behaviour)) Object.DestroyImmediate(behaviour);
                var partPrefab = e.Kind == PartKind && TrainConstructor.Global != null && TrainConstructor.Global.PartsDataBase != null ? TrainConstructor.Global.PartsDataBase.Parts[e.Prefab] : null;
                foreach (var point in root.GetComponentsInChildren<TrainMountPoint>(true))
                {
                    if (partPrefab == null) { Object.DestroyImmediate(point); continue; }
                    point.SubPointOfPart = partPrefab; point.AttachedPart = null; point.ParentBlock = null;
                }
                // Activation belongs to the atomic replacement commit.
                return new Replica { Component = marker, Layout = e, Doors = visualDoors };
            }
            catch
            {
                if (copy != null) Object.Destroy(copy.gameObject);
                throw;
            }
            finally { Object.Destroy(staging); }
        }

        private static ReplicaDoor[] CaptureReplicaDoors(Transform root, DoorController[] doors)
        {
            var result = new ReplicaDoor[doors.Length];
            for (int i = 0; i < doors.Length; i++)
            {
                var door = doors[i]; ValidateDoor(door);
                int count = door.Doors.Length;
                var visual = new ReplicaDoor { Path = DoorPath(root, door), Controller = door.transform, Leaves = new Transform[count],
                    OpenPositions = new Vector3[count], ClosePositions = new Vector3[count],
                    OpenRotations = new Vector3[count], CloseRotations = new Vector3[count] };
                for (int j = 0; j < count; j++)
                {
                    var leaf = door.Doors[j];
                    if (!leaf.transform.IsChildOf(root) && leaf.transform != root)
                        throw new InvalidDataException("Replica door outside its visual root");
                    visual.Leaves[j] = leaf.transform;
                    visual.OpenPositions[j] = leaf.OpenPos; visual.ClosePositions[j] = leaf.ClosePos;
                    visual.OpenRotations[j] = leaf.RotationB; visual.CloseRotations[j] = leaf.RotationA;
                }
                result[i] = visual;
                visual.Open = door.Open;
            }
            return result;
        }

        private static void ApplyReplicaDoors(ReplicaDoor[] doors, DoorState[] desired)
        {
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var state in desired) states.Add(state.Path, state.Open);
            foreach (var door in doors)
            {
                bool open;
                if (!states.TryGetValue(door.Path, out open)) continue;
                door.Open = open;
                for (int i = 0; i < door.Leaves.Length; i++)
                {
                    if (door.Leaves[i] == null) continue;
                    door.Leaves[i].localPosition = open ? door.OpenPositions[i] : door.ClosePositions[i];
                    door.Leaves[i].localEulerAngles = open ? door.OpenRotations[i] : door.CloseRotations[i];
                }
            }
        }

        // ---- Train doors through the host (1.1.6) ----

        // 23 bits of the FNV-1a hash of a door's path under its owner: equal on both sides for the
        // same prefab (the layout already matches doors by this path).
        internal static int DoorHash(string path)
        {
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(path)) { hash ^= b; hash *= 16777619; }
            return (int)(hash & LanStorage.DoorHashMask);
        }

        // Host: the door a guest's request names, or null.
        internal static DoorController FindHostDoor(TrainManager train, int carIndex, int owner, int identity, int hash)
        {
            var cars = ResolveCars(train);
            if (carIndex < 0 || carIndex >= cars.Length) return null;
            var car = cars[carIndex];
            Transform root; DoorController[] doors;
            switch (owner)
            {
                case LanStorage.DoorFurniture:
                case LanStorage.DoorPart:
                    Component found = null;
                    if (owner == LanStorage.DoorFurniture)
                    {
                        if (car.AllFurniture != null)
                            foreach (var furniture in car.AllFurniture) if (furniture != null && furniture.GetInstanceID() == identity) { found = furniture; break; }
                    }
                    else foreach (var part in car.GetComponentsInChildren<TrainPart>(true)) if (part != null && part.GetInstanceID() == identity) { found = part; break; }
                    if (found == null || found.GetComponentInParent<LanTrainReplica>() != null) return null;
                    root = found.transform; doors = Owned(found, found.GetComponentsInChildren<DoorController>(true));
                    break;
                case LanStorage.DoorCar: root = car.transform; doors = FindBaseDoors(car); break;
                case LanStorage.DoorTrain: root = train.transform; doors = FindRootDoors(train, cars); break;
                default: return null;
            }
            foreach (var door in doors)
                if (door != null && DoorHash(DoorPath(root, door)) == hash) return door;
            return null;
        }

        // Guest: the host door under `collider` (a door of a copied part, or one of the guest's own
        // native doors that shows a host door), what the request names it by, and whether it is open.
        internal static bool TryIdentifyDoor(Collider collider, out int car, out int owner, out int identity, out int hash, out bool open)
        {
            car = owner = identity = hash = 0; open = false;
            var state = _runtime;
            if (collider == null || state == null || !state.Initialized) return false;
            var marker = collider.GetComponentInParent<LanTrainReplica>();
            if (marker != null)
            {
                foreach (var replica in state.Replicas.Values)
                {
                    if (replica.Component != marker || replica.Doors == null) continue;
                    var e = replica.Layout;
                    if (e == null || e.Blueprint || (e.Kind != FurnitureKind && e.Kind != PartKind)) return false;
                    foreach (var door in replica.Doors)
                    {
                        bool hit = door.Controller != null && collider.transform.IsChildOf(door.Controller);
                        foreach (var leaf in door.Leaves) if (leaf != null && collider.transform.IsChildOf(leaf)) hit = true;
                        if (!hit) continue;
                        car = e.Car; owner = e.Kind == FurnitureKind ? LanStorage.DoorFurniture : LanStorage.DoorPart;
                        identity = e.Identity; hash = DoorHash(door.Path); open = door.Open;
                        return true;
                    }
                    return false;
                }
                return false;
            }
            var native = collider.GetComponentInParent<DoorController>();
            var leafOf = collider.GetComponentInParent<Door>();
            if (native == null && leafOf != null) native = leafOf.ConnectedDoorController;
            if (native == null || !native.transform.IsChildOf(state.Train.transform)) return false;
            open = native.Open;
            for (var t = native.transform; t != null && t != state.Train.transform; t = t.parent)
            {
                Entry entry;
                foreach (var candidate in new Component[] { t.GetComponent<Furniture>(), t.GetComponent<TrainPart>() })
                {
                    if (candidate == null) continue;
                    if (!state.Hosted.TryGetValue(candidate, out entry) || entry.Blueprint) return false;
                    car = entry.Car; owner = entry.Kind == FurnitureKind ? LanStorage.DoorFurniture : LanStorage.DoorPart;
                    identity = entry.Identity; hash = DoorHash(DoorPath(candidate.transform, native));
                    return true;
                }
            }
            for (int i = 0; i < state.Cars.Length; i++)
                if (Array.IndexOf(state.BaseDoors[i], native) >= 0)
                { car = i; owner = LanStorage.DoorCar; hash = DoorHash(DoorPath(state.Cars[i].transform, native)); return true; }
            if (Array.IndexOf(state.RootDoors, native) >= 0)
            { car = 0; owner = LanStorage.DoorTrain; hash = DoorHash(DoorPath(state.Train.transform, native)); return true; }
            return false;
        }

        // Guest: the host opened or closed that door; show it at once (the next layout agrees).
        internal static void GuestSetDoor(int car, int owner, int identity, int hash, bool open)
        {
            var state = _runtime;
            if (state == null || !state.Initialized || car < 0 || car >= state.Cars.Length) return;
            if (owner == LanStorage.DoorFurniture || owner == LanStorage.DoorPart)
            {
                byte kind = owner == LanStorage.DoorFurniture ? FurnitureKind : PartKind;
                foreach (var pair in state.Hosted)
                {
                    var e = pair.Value;
                    if (e.Kind != kind || e.Identity != identity || e.Car != car || pair.Key == null) continue;
                    Replica replica;
                    if (state.Replicas.TryGetValue(e.Key, out replica) && replica.Component == pair.Key)
                    {
                        foreach (var door in replica.Doors)
                            if (DoorHash(door.Path) == hash) ApplyReplicaDoors(new[] { door }, new[] { new DoorState { Path = door.Path, Open = open } });
                        return;
                    }
                    foreach (var door in Owned(pair.Key, pair.Key.GetComponentsInChildren<DoorController>(true)))
                        if (DoorHash(DoorPath(pair.Key.transform, door)) == hash) ShowDoor(door, open);
                    return;
                }
                return;
            }
            var root = owner == LanStorage.DoorCar ? state.Cars[car].transform : state.Train.transform;
            var doors = owner == LanStorage.DoorCar ? state.BaseDoors[car] : state.RootDoors;
            foreach (var door in doors)
                if (door != null && DoorHash(DoorPath(root, door)) == hash) ShowDoor(door, open);
        }
        private static void ShowDoor(DoorController door, bool open)
        {
            if (door.Open == open) return;
            if (!door.broken) door.Set(open); // the game's own swing and sound
            if (door.Open != open) SetDoor(door, open, true);
            door.notInteractive = true;
        }

        // Guest (1.4.13): a door of the guest's own train that the host's train does not show (inside a part or
        // furniture of its own that is hidden): it is no door for the ladders.
        internal static bool HiddenDoor(DoorController door)
        {
            var state = _runtime;
            if (door == null || state == null || !state.Initialized || state.UnusedNatives.Count == 0) return false;
            for (var t = door.transform; t != null; t = t.parent)
            {
                Component owner = t.GetComponent<TrainPart>();
                if (owner == null) owner = t.GetComponent<Furniture>();
                if (owner != null) return state.UnusedNatives.Contains(owner);
                if (t.GetComponent<TrainCar>() != null) return false;
            }
            return false;
        }

        // 1.4.14, guest: what the last applied layout shows (for the log): own parts kept and hidden, host copies.
        internal static string Summary()
        {
            var state = _runtime;
            if (state == null || !state.Initialized) return "not applied";
            int kept = 0; foreach (var native in state.Natives) if (native.Component != null && !state.UnusedNatives.Contains(native.Component)) kept++;
            return "own parts kept " + kept + ", hidden " + state.UnusedNatives.Count + ", host copies " + state.Replicas.Count;
        }

        // 1.4.16: the player is placing a part on a surface (a picture on a wall): its preview follows the game's
        // look-at ray (TrainInteractionRays lets that ray meet the host's copies then).
        internal static bool SurfacePlanning()
        {
            var constructor = TrainConstructor.Global;
            return constructor != null && constructor.PreviewObject != null && constructor.PreviewObject.PlacedOnSurface;
        }

        // 1.4.15: the game's ladder is closed only by the door of a door part (Stair.IsBlockedByDoor: the
        // DoorController a part attached to its parent's points has; TrainPart.Awake leaves it null on a part with
        // an inventory). The closed doors of a wardrobe or any furniture next to a landing closed the ladder.
        private static readonly Dictionary<int, bool> _doorParts = new Dictionary<int, bool>();
        private static bool DoorPart(Replica replica)
        {
            var e = replica.Layout;
            if (e == null || e.Kind != PartKind) return false;
            bool door;
            if (_doorParts.TryGetValue(e.Prefab, out door)) return door;
            var parts = TrainConstructor.Global == null ? null : TrainConstructor.Global.PartsDataBase;
            var prefab = parts == null || parts.Parts == null || e.Prefab >= parts.Parts.Count ? null : parts.Parts[e.Prefab];
            door = prefab != null && prefab.GetComponentInChildren<DoorController>(true) != null && prefab.GetComponentInChildren<Zompiercer.Inventory.Inventory>(true) == null;
            if (prefab != null) _doorParts[e.Prefab] = door;
            return door;
        }
        internal static bool DoorPart(DoorController door)
        {
            if (door == null || door.GetComponentInParent<Furniture>() != null) return false;
            var part = door.GetComponentInParent<TrainPart>();
            return part != null && part.GetComponentInChildren<Zompiercer.Inventory.Inventory>(true) == null;
        }

        // 1.4.14: which closed copy of a host door blocks a landing (for the log), or null.
        internal static string ReplicaDoorNear(Vector3 landing)
        {
            if (_runtime == null) return null;
            foreach (var replica in _runtime.Replicas.Values)
                if (DoorPart(replica))
                foreach (var door in replica.Doors)
                    if (!door.Open)
                        foreach (var leaf in door.Leaves)
                            if (leaf != null && Vector3.Distance(leaf.position, landing) < 1.4f)
                                return "closed host door copy " + door.Path + " (leaf " + leaf.name + ", " + Vector3.Distance(leaf.position, landing).ToString("0.00") + " m from the landing)";
            return null;
        }

        internal static bool ReplicaDoorBlocks(Vector3 landing)
        {
            if (_runtime == null) return false;
            foreach (var replica in _runtime.Replicas.Values)
                if (DoorPart(replica))
                foreach (var door in replica.Doors)
                    if (!door.Open)
                        foreach (var leaf in door.Leaves)
                            if (leaf != null && Vector3.Distance(leaf.position, landing) < 1.4f) return true;
            return false;
        }

        private static bool PlayerAboard(TrainManager train)
        {
            var manager = GlobalManager.global;
            var player = manager == null ? null : manager.controlledChar;
            if (player == null) return false;
            return player.OnTrainCar != null || player.transform.IsChildOf(train.transform);
        }

        // 1.4.13: what held the last change back (for the log). Only changes near the player hold one back: in its
        // car within DeferNear of it, where a floor could go from under it. A door or a part far along the train
        // (a door over a ladder too) held every update back for as long as the guest stayed aboard.
        internal static string DeferredBy { get; private set; } = "";
        private const float DeferNear = 6f;
        private static void PlayerSpot(Runtime state, out int car, out Vector3 at)
        {
            car = -1; at = Vector3.zero;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (player == null || state.Cars == null) return;
            var on = player.OnTrainCar != null ? player.OnTrainCar : player.GetComponentInParent<TrainCar>();
            int index = on == null ? -1 : Array.IndexOf(state.Cars, on);
            if (index < 0) return;
            car = index; at = on.transform.InverseTransformPoint(player.transform.position);
        }
        private static bool RemovesGeometry(Dictionary<long, Entry> old, Dictionary<long, Entry> next, int playerCar, Vector3 playerAt)
        {
            foreach (var pair in old)
            {
                // Items and blueprints are no support (1.4.11: a blueprint removed or built while the guest
                // stood aboard held every later change back until it stepped off): never deferred.
                if (pair.Value.Kind == ItemKind || pair.Value.Blueprint) continue;
                Entry newer;
                if (next.TryGetValue(pair.Key, out newer) && pair.Value.SameGeometry(newer)) continue;
                var at = Place(pair.Value);
                if (playerCar >= 0 && (pair.Value.Car != playerCar || (at - playerAt).sqrMagnitude > DeferNear * DeferNear)) continue;
                DeferredBy = "kind " + pair.Value.Kind + " prefab " + pair.Value.Prefab + " car " + pair.Value.Car + (newer == null ? " removed" : " moved") +
                    (playerCar >= 0 ? ", " + Vector3.Distance(at, playerAt).ToString("0.0") + " m from the player" : ", the player's car unknown");
                return true;
            }
            return false;
        }
    }

    internal sealed class LanTrainReplica : MonoBehaviour
    {
        internal bool PickedUp;
        internal bool Functional;
        // 1.4.0: the copy's own surfaces, wire inputs, switch and sign text (kept for the host's decor).
        internal Paintable[] Paintables;
        internal LogicInput[] Inputs;
        internal LogicOutput Output;
        internal UnityEngine.UI.Text SignText;
        internal Transform[] StairTargets;
        internal StairZone[] StairZones;
        // Copies in the scene (Awake waits until a copy is first active).
        internal static readonly List<LanTrainReplica> All = new List<LanTrainReplica>();
        private void Awake() { All.Add(this); }
        private void OnDestroy() { All.Remove(this); }
    }

    // A copied part's stair or ladder box (in Box's own space) and the place it leads to.
    internal struct StairZone
    {
        internal Transform Box, Up;
        internal Vector3 Center, Size;
    }
}
