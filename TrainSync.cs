using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using FluffyUnderware.Curvy.Controllers;
using FluffyUnderware.Curvy.Examples;
using UnityEngine;
using Zompiercer.Train;

namespace ZompiercerLAN
{
    // Unity objects are only accessed on the main thread. UDP fragments contain a
    // bounded, explicit layout format, never the game's serialized object graph.
    internal sealed class TrainSync
    {
        private readonly Action<Packet> _send;
        private readonly ManualLogSource _log;
        private readonly LanPeerWorkLimits _workLimits;
        private byte[] _hostLayout;
        private string _hostScene;
        private int _hostRevision, _ackedRevision, _sendChunk;
        private float _nextCapture, _nextMotion, _nextChunkCycle;
        private uint _sequence;
        private int _incomingRevision, _receivedChunks, _appliedRevision;
        private byte[][] _chunks;
        private byte[] _pendingLayout;
        private float _nextApply, _incomingStarted;
        private int _receivedBytes;
        private bool _building, _frozen;
        private const float ChunkTimeout = 30f, PreparedTimeout = 120f;
        private TrainFrame _latest;
        private readonly List<MotionSample> _motion = new List<MotionSample>(8);
        private const float MotionDelay = 0.15f;
        private sealed class MotionSample
        {
            internal TrainFrame Frame;
            internal float ReceivedAt;
        }
        private TrainManager _clientTrain;
        private TrainController _clientController;
        private bool _placed;
        private static readonly System.Reflection.BindingFlags PlayerFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly System.Reflection.FieldInfo BelowCurrent = typeof(ZombieFighterController).GetField("TrainBelowCurrent", PlayerFields);
        private static readonly System.Reflection.FieldInfo BelowPrevious = typeof(ZombieFighterController).GetField("TrainBelowPrevious", PlayerFields);
        internal bool Ready { get { return !_frozen && _clientTrain != null && _placed && _appliedRevision > 0; } }
        internal string Status { get; private set; } = "Ожидание поезда хоста";
        internal string StairHint { get; private set; } = "";

        internal TrainSync(Action<Packet> send, ManualLogSource log, LanPeerWorkLimits workLimits) { _send = send; _log = log; _workLimits=workLimits; }
        internal void PeerJoined() { _ackedRevision = 0; _sendChunk = 0; _nextChunkCycle = 0; }

        internal void Receive(Packet p, bool host)
        {
            if (_frozen) return;
            ExpireIncoming();
            if (host)
            {
                if (p.Kind == PacketKind.TrainLayoutAck && p.Revision == _hostRevision) _ackedRevision = p.Revision;
                return;
            }
            if (p.Kind == PacketKind.TrainMotion)
            {
                if (_latest != null && p.Train.Sequence <= _latest.Sequence) return;
                if (_latest != null && _latest.Scene != p.Train.Scene) ResetIncoming();
                float now = Time.unscaledTime;
                if (_motion.Count != 0)
                {
                    var last = _motion[_motion.Count - 1];
                    var a = last.Frame.Cars[0].Root;
                    var b = p.Train.Cars[0].Root;
                    if (last.Frame.Scene != p.Train.Scene ||
                        last.Frame.Cars.Length != p.Train.Cars.Length || now - last.ReceivedAt > 1f ||
                        Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)) > 8f)
                        _motion.Clear();
                }
                _latest = p.Train;
                var sample = new MotionSample { Frame = p.Train, ReceivedAt = now };
                if (_motion.Count != 0 && _motion[_motion.Count - 1].ReceivedAt == now)
                    _motion[_motion.Count - 1] = sample;
                else _motion.Add(sample);
                if (_motion.Count > 8) _motion.RemoveAt(0);
                return;
            }
            if (p.Kind != PacketKind.TrainLayoutChunk || p.Revision < _appliedRevision || p.Revision < _incomingRevision) return;
            if (p.Revision == _appliedRevision) { Ack(p.Revision); return; }
            if (p.Chunk == null || p.Chunk.Length == 0 || p.Chunk.Length > LanProtocol.ChunkSize ||
                p.ChunkCount < 1 || p.ChunkCount > (LanProtocol.MaxLayoutBytes + LanProtocol.ChunkSize - 1) / LanProtocol.ChunkSize ||
                p.ChunkIndex < 0 || p.ChunkIndex >= p.ChunkCount) return;
            if (p.Revision != _incomingRevision || (_chunks == null && _pendingLayout == null))
            {
                if(p.Revision!=_incomingRevision && !_workLimits.LayoutChange(Time.unscaledTime)) return;
                ResetIncoming();
                _incomingRevision = p.Revision;
                _incomingStarted = Time.unscaledTime;
                _chunks = new byte[p.ChunkCount][];
            }
            if (_pendingLayout != null || _chunks == null || _chunks.Length != p.ChunkCount || _chunks[p.ChunkIndex] != null) return;
            if (_receivedBytes + p.Chunk.Length > LanProtocol.MaxLayoutBytes)
            { ResetIncoming(); Report("Слишком большой состав поезда"); return; }
            _chunks[p.ChunkIndex] = p.Chunk;
            _receivedBytes += p.Chunk.Length;
            _receivedChunks++;
            if (_receivedChunks != _chunks.Length) return;
            _pendingLayout = new byte[_receivedBytes];
            int offset = 0;
            foreach (var chunk in _chunks) { Buffer.BlockCopy(chunk, 0, _pendingLayout, offset, chunk.Length); offset += chunk.Length; }
            _chunks = null;
            _incomingStarted = Time.unscaledTime;
        }

        private void ResetIncoming()
        {
            TrainLayout.CancelPending();
            _building = false;
            _chunks = null;
            _pendingLayout = null;
            _receivedChunks = _receivedBytes = 0;
            // Keep the revision high-water mark: delayed older chunks cannot replace it.
        }

        private void ExpireIncoming()
        {
            if ((_chunks != null || _pendingLayout != null) &&
                Time.unscaledTime - _incomingStarted > (_pendingLayout == null ? ChunkTimeout : PreparedTimeout))
            { ResetIncoming(); Report("Время получения построек истекло; ожидание повторной передачи"); }
        }

        internal void Tick(bool host, bool worldReady)
        {
            if (_frozen) return;
            ExpireIncoming();
            if (!worldReady) { ResetIncoming(); return; }
            try
            {
                if (host) TickHost();
                else TickClient();
            }
            catch (Exception ex)
            {
                bool deferred = ex.Message.IndexOf("deferred while", StringComparison.Ordinal) >= 0;
                Report(deferred ? "Выйдите из поезда для обновления построек" : "Поезд: " + ex.Message);
                // 1.4.13: what held it back, at most twice a minute.
                if (deferred && Time.unscaledTime >= _deferLogAt) { _deferLogAt = Time.unscaledTime + 30f; _log.LogInfo("Train layout change held back by " + TrainLayout.DeferredBy); }
            }
        }

        private void TickHost()
        {
            var train = GlobalManager.global.controlledTrain;
            if (train == null || train.Cars == null || train.Cars.Length == 0) return;
            if (train.Cars.Length > LanProtocol.MaxCars) throw new InvalidDataException("Поддерживается до " + LanProtocol.MaxCars + " вагонов основного поезда");
            float now = Time.unscaledTime;
            string scene = GlobalSceneManager.global.CurrentSceneName;
            if (now >= _nextCapture)
            {
                _nextCapture = now + 2f;
                byte[] layout = TrainLayout.Capture(train);
                if (layout.Length == 0 || layout.Length > LanProtocol.MaxLayoutBytes) throw new InvalidDataException("Размер построек поезда превышает лимит");
                if (_hostLayout == null || scene != _hostScene || !_hostLayout.SequenceEqual(layout))
                {
                    if (_hostLayout == null || scene != _hostScene) LogTrainIdentity("host", train);
                    _hostLayout = layout; _hostScene = scene; _hostRevision++;
                    _sendChunk = 0; _nextChunkCycle = 0;
                    _log.LogInfo("Train layout revision " + _hostRevision + ", bytes=" + layout.Length +
                        (TrainLayout.DecorDropped > 0 ? ", paint/wires/signs of " + TrainLayout.DecorDropped + " objects did not fit" : ""));
                }
            }
            if (_hostLayout == null) return;
            if (now >= _nextMotion)
            {
                _nextMotion = now + 0.1f;
                var controller = GlobalManager.global.controlledTrainController;
                var frame = new TrainFrame { Sequence = ++_sequence, Revision = _hostRevision, Scene = scene,
                    Speed = controller == null ? 0f : controller.Velocity, Thrust = controller == null ? 0f : controller.Thrust,
                    Flags = controller == null ? (byte)0 : (byte)((controller.engineOn ? 1 : 0) | (controller.Brake ? 2 : 0) |
                    (controller.reverse ? 4 : 0) | (controller.hornOn ? 8 : 0) |
                    (controller.locomotiveSpotlight != null && controller.locomotiveSpotlight.activeSelf ? 16 : 0)),
                    Cars = train.Cars.Select(c => new CarPose { Root = Capture(c.transform), Body = Capture(c.Waggon.transform),
                        Front = Capture(c.FrontAxis.transform), Back = Capture(c.BackAxis.transform) }).ToArray() };
                // The cab's controls and whether the guest may use them (0.10.0).
                try { LanTrainControl.Capture(controller, frame); }
                catch (Exception ex) { _log.LogWarning("Train controls not captured: " + ex.Message); }
                // 1.4.3: the main tank's fuel and the fuel stations near the guest.
                try { CaptureFuel(controller, frame); LanFuelStation.Capture(frame); }
                catch (Exception ex) { if (!_fuelFailed) { _fuelFailed = true; _log.LogWarning("Fuel not captured: " + ex.GetType().Name + ": " + ex.Message); } }
                _send(new Packet { Kind = PacketKind.TrainMotion, Train = frame });
            }
            if (_ackedRevision == _hostRevision || now < _nextChunkCycle) return;
            int count = (_hostLayout.Length + LanProtocol.ChunkSize - 1) / LanProtocol.ChunkSize;
            // Pace retransmissions instead of bursting a whole layout into the socket.
            for (int i = 0; i < 2 && _sendChunk < count; i++, _sendChunk++)
            {
                int start = _sendChunk * LanProtocol.ChunkSize;
                var chunk = new byte[Math.Min(LanProtocol.ChunkSize, _hostLayout.Length - start)];
                Buffer.BlockCopy(_hostLayout, start, chunk, 0, chunk.Length);
                _send(new Packet { Kind = PacketKind.TrainLayoutChunk, Revision = _hostRevision, ChunkIndex = _sendChunk, ChunkCount = count, Chunk = chunk });
            }
            if (_sendChunk == count) { _sendChunk = 0; _nextChunkCycle = now + 2f; }
        }

        private void TickClient()
        {
            if (_latest == null || _latest.Scene != GlobalSceneManager.global.CurrentSceneName) return;
            var train = GlobalManager.global.controlledTrain;
            if (train == null || train.Cars == null || train.Cars.Length == 0) return;
            if (train.Cars.Length != _latest.Cars.Length) throw new InvalidDataException("Разное число вагонов; обновите мод и версию игры на обоих ПК");
            if (_clientTrain != train) PrepareClient(train);
            if (Ready) UpdateStairs();
            if (_pendingLayout == null || _incomingRevision != _latest.Revision || Time.unscaledTime < _nextApply) return;
            // The scene loader initially places a fresh client on its own starter
            // train. Release this temporary attachment before the first layout.
            if (_appliedRevision == 0) DetachPlayer();
            if (!_building)
            {
                _nextApply = Time.unscaledTime + 1f;
                TrainLayout.Apply(train, _pendingLayout);
                _building = true;
                _nextApply = 0f;
            }
            try
            {
                if (!TrainLayout.Advance(train)) return;
            }
            catch
            {
                _building = TrainLayout.HasPending;
                _nextApply = Time.unscaledTime + 1f;
                throw;
            }
            _building = false;
            BlockCabControls(train);
            // Each layout commit disables the cab again; restore its request-only controls.
            try { LanTrainControl.PrepareGuest(_clientController); }
            catch (Exception ex) { _log.LogWarning("Shared train controls unavailable: " + ex.Message); }
            _appliedRevision = _incomingRevision;
            _pendingLayout = null;
            _chunks = null;
            Ack(_appliedRevision);
            Render(!_placed);
            Status = "Поезд хоста синхронизирован";
            _log.LogInfo("Applied host train layout revision " + _appliedRevision + " (" + TrainLayout.Summary() + ")" +
                (TrainLayout.DecorSkipped > 0 ? "; paint/wires/signs of " + TrainLayout.DecorSkipped + " objects could not be read" : ""));
        }

        private void PrepareClient(TrainManager train)
        {
            TrainInteractionRays.Install();
            TrainGroundProbes.Install();
            GuestStairs.Install();
            GuestStairs.Log = message => _log.LogInfo(message);
            LogTrainIdentity("client", train);
            if (_clientTrain != null)
            {
                ResetIncoming();
                _appliedRevision = 0;
            }
            _clientTrain = train;
            _clientController = train.GetComponent<TrainController>();
            train.enabled = false;
            if (_clientController != null) _clientController.enabled = false;
            foreach (var car in train.Cars) car.enabled = false;
            foreach (var spline in train.GetComponentsInChildren<SplineController>(true)) { spline.Speed = 0f; spline.enabled = false; }
            BlockCabControls(train);
            // The host may let the guest drive: its native cab controls become requests (0.10.0).
            try { LanTrainControl.PrepareGuest(_clientController); }
            catch (Exception ex) { _log.LogWarning("Shared train controls unavailable: " + ex.Message); }
            _placed = false;
            _log.LogInfo("Client train now follows host; local train simulation and cab controls disabled");
        }

        private static void DisableControl(MonoBehaviour control)
        {
            control.enabled = false;
            foreach (var collider in control.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        }

        private static void BlockCabControls(TrainManager train)
        {
            foreach (var control in train.GetComponentsInChildren<HardwareButton>(true)) DisableControl(control);
            foreach (var control in train.GetComponentsInChildren<Lever>(true)) DisableControl(control);
        }

        internal void Render(bool snap = false)
        {
            if (_frozen || _clientTrain == null || _latest == null || _appliedRevision == 0 ||
                GlobalSceneManager.global == null || GlobalSceneManager.global.SceneCurrentlyLoading ||
                _latest.Scene != GlobalSceneManager.global.CurrentSceneName) return;
            if (_clientTrain.Cars == null || _clientTrain.Cars.Length != _latest.Cars.Length) return;
            if (snap)
            {
                _motion.Clear();
                _motion.Add(new MotionSample { Frame = _latest, ReceivedAt = Time.unscaledTime });
            }
            TrainFrame fromFrame = _latest, toFrame = _latest;
            float t = 1f;
            if (!snap && _placed && _motion.Count > 1)
            {
                float targetTime = Time.unscaledTime - MotionDelay;
                while (_motion.Count > 2 && _motion[1].ReceivedAt <= targetTime) _motion.RemoveAt(0);
                if (targetTime < _motion[0].ReceivedAt)
                    fromFrame = toFrame = _motion[0].Frame;
                else if (targetTime < _motion[1].ReceivedAt)
                {
                    fromFrame = _motion[0].Frame;
                    toFrame = _motion[1].Frame;
                    t = Mathf.Clamp01((targetTime - _motion[0].ReceivedAt) /
                        (_motion[1].ReceivedAt - _motion[0].ReceivedAt));
                }
            }
            // Native CharacterController.Move and layout commits can precede this
            // LateUpdate while autoSyncTransforms is disabled.
            Physics.SyncTransforms();
            if (_placed) UpdatePassengerAttachment();
            for (int i = 0; i < _clientTrain.Cars.Length; i++)
            {
                var target = toFrame.Cars[i];
                var from = fromFrame.Cars[i];
                var car = _clientTrain.Cars[i];
                ApplyPose(car.transform, from.Root, target.Root, t);
                ApplyPose(car.Waggon.transform, from.Body, target.Body, t);
                ApplyPose(car.FrontAxis.transform, from.Front, target.Front, t);
                ApplyPose(car.BackAxis.transform, from.Back, target.Back, t);
            }
            _clientTrain.Speed = _latest.Speed;
            if (_clientController != null)
            {
                _clientController.Velocity = _latest.Speed; _clientController.Thrust = _latest.Thrust;
                _clientController.engineOn = (_latest.Flags & 1) != 0;
                _clientController.Brake = (_latest.Flags & 2) != 0;
                _clientController.reverse = (_latest.Flags & 4) != 0;
                _clientController.hornOn = (_latest.Flags & 8) != 0;
                if (_clientController.locomotiveSpotlight != null) _clientController.locomotiveSpotlight.SetActive((_latest.Flags & 16) != 0);
                Sound(_clientController.engineSound, _clientController.engineOn);
                Sound(_clientController.hornSound, _clientController.hornOn);
                LanTrainControl.Mirror(_latest);
                try { ApplyFuel(_clientController, _latest); }
                catch (Exception ex) { if (!_fuelFailed) { _fuelFailed = true; _log.LogWarning("Host fuel not shown: " + ex.GetType().Name + ": " + ex.Message); } }
            }
            LanFuelStation.Mirror(_latest);
            _placed = true;
            Physics.SyncTransforms();
        }

        // 1.4.3: the guest's train does not run, so its tank kept the guest's own save; it shows the host's.
        private bool _fuelFailed;
        internal static TrainFuelTank MainTank(TrainController controller)
        {
            if (controller == null) return null;
            if (controller.trainFuelTank != null) return controller.trainFuelTank;
            var engine = controller.GetComponentInChildren<Locomotive>(true);
            return engine == null ? null : engine.trainFuelTank;
        }
        private static void CaptureFuel(TrainController controller, TrainFrame frame)
        {
            var tank = MainTank(controller);
            if (tank == null || !(tank.maxFuelAmount > 0f) || tank.maxFuelAmount >= 100000f) return;
            frame.FuelMax = tank.maxFuelAmount;
            frame.Fuel = Mathf.Clamp(tank.currentFuelAmount, 0f, tank.maxFuelAmount);
        }
        private static void ApplyFuel(TrainController controller, TrainFrame frame)
        {
            if (frame.FuelMax <= 0f) return;
            var tank = MainTank(controller);
            if (tank != null) { tank.maxFuelAmount = frame.FuelMax; tank.currentFuelAmount = frame.Fuel; }
            var engine = controller.GetComponentInChildren<Locomotive>(true);
            if (engine != null && engine.trainFuelTank != null && engine.trainFuelTank != tank)
            { engine.trainFuelTank.maxFuelAmount = frame.FuelMax; engine.trainFuelTank.currentFuelAmount = frame.Fuel; }
        }

        private void UpdatePassengerAttachment()
        {
            var player = GlobalManager.global.controlledChar;
            if (player == null || player.controller == null || !player.controller.enabled) return;
            var bounds = player.controller.bounds;
            var foot = new Vector3(bounds.center.x, bounds.min.y + 0.25f, bounds.center.z);
            TrainCar support = null;
            float radius = player.controller.radius * 0.6f;
            var offsets = new[] { Vector3.zero, Vector3.right * radius, Vector3.left * radius, Vector3.forward * radius, Vector3.back * radius };
            foreach (var offset in offsets)
            {
                // Physical train geometry includes passive copies of host construction.
                var hit = Physics.RaycastAll(foot + offset, Vector3.down, 1.8f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(h => !h.collider.transform.IsChildOf(player.transform)).OrderBy(h => h.distance).FirstOrDefault();
                if (hit.collider == null) continue;
                var car = hit.collider.GetComponentInParent<TrainCar>();
                if (car != null && car.transform.IsChildOf(_clientTrain.transform) &&
                    Vector3.Angle(hit.normal, Vector3.up) <= player.controller.slopeLimit &&
                    (hit.distance <= 0.25f + Mathf.Max(0.15f, player.controller.skinWidth * 2f) ||
                     player.OnTrainCar == car))
                { support = car; break; }
            }
            if (support != null)
            {
                if (player.transform.parent != support.transform) player.transform.SetParent(support.transform, true);
                player.OnTrainCar = support;
                BelowCurrent?.SetValue(player, support); BelowPrevious?.SetValue(player, support);
                // The original ground probe ignores replica floors and can see water
                // underneath a bridge. The nearby physical train floor takes priority.
                player.preSwimming = false; player.swimming = false;
            }
            else if (player.transform.IsChildOf(_clientTrain.transform)) DetachPlayer();
        }

        private void UpdateStairs()
        {
            StairHint = "";
            var player = GlobalManager.global.controlledChar;
            var input = Zompiercer.Inputs.InputController.Global;
            var camera = GlobalManager.global.MainCamera;
            if (player == null || camera == null || input == null || !player.controlEnabled || (int)input.CurrentState != 1) return;
            var hit = Physics.RaycastAll(camera.transform.position, camera.transform.forward, 3f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => !h.collider.transform.IsChildOf(player.transform)).OrderBy(h => h.distance).FirstOrDefault();
            Transform target = null; Stair stair = null;
            // 1.1.1: a ladder of a part the host built, through its use zone (nothing solid in front).
            Transform zoneUp; float zoneDistance;
            if (TrainLayout.StairZoneInView(camera.transform.position, camera.transform.forward, 3f, out zoneUp, out zoneDistance) &&
                (hit.collider == null || zoneDistance <= hit.distance + .1f))
                target = zoneUp;
            else
            {
                if (hit.collider == null || !hit.collider.transform.IsChildOf(_clientTrain.transform)) return;
                stair = hit.collider.GetComponentInParent<Stair>();
                var simple = hit.collider.GetComponentInParent<SimpleStair>();
                var part = hit.collider.GetComponentInParent<TrainPart>();
                if (stair == null && simple == null && part != null) stair = part.GetComponentInChildren<Stair>();
                target = stair != null ? stair.UpPosition : simple != null ? simple.UpPosition : null;
                var replica = hit.collider.GetComponentInParent<LanTrainReplica>();
                if (target == null && replica != null && replica.Functional && replica.StairTargets != null)
                    target = replica.StairTargets.Where(t => t != null).OrderBy(t => Vector3.Distance(t.position, hit.point)).FirstOrDefault();
                if (target == null || (part != null && !part.IsFunctional())) return;
            }
            if (stair != null && stair.IsBlockedByDoor()) { GuestStairs.Report(GuestStairs.Why(stair)); DoorClosed(); return; }
            // Standalone copied stairs have no local build-system mount links. Also
            // check the actual host-controlled door leaves next to the landing.
            var closedDoor = _clientTrain.GetComponentsInChildren<DoorController>(true).FirstOrDefault(d => d != null && !d.Open && !TrainLayout.HiddenDoor(d) && TrainLayout.DoorPart(d) &&
                d.Doors != null && d.Doors.Any(leaf => leaf != null && Vector3.Distance(leaf.transform.position, target.position) < 1.4f));
            if (closedDoor != null || TrainLayout.ReplicaDoorBlocks(target.position))
            {
                GuestStairs.Report("landing " + GuestStairs.PathOf(target) + ": " + (closedDoor != null ? "closed door of the guest's train " + GuestStairs.PathOf(closedDoor.transform)
                    : TrainLayout.ReplicaDoorNear(target.position)));
                DoorClosed(); return;
            }
            StairHint = "Взаимодействие: подняться в поезд";
            // The game's own "climb" prompt (a copied part has no native stair to show it).
            var info = Zompiercer.Inventory.InventoryController.global == null ? null : Zompiercer.Inventory.InventoryController.global.simpleInfoDisplayer;
            if (info != null) info.SetIconText("Climb", (Zompiercer.Inputs.Actions)1, (Zompiercer.Inputs.Actions)0, true);
            bool use = input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown;
            if (!use || Vector3.Distance(player.transform.position, target.position) > 6f) return;
            var controller = player.controller;
            if (controller == null) return;
            var center = target.position + controller.center;
            float half = Mathf.Max(0f, controller.height * 0.5f - controller.radius);
            if (Physics.OverlapCapsule(center - Vector3.up * half, center + Vector3.up * half, controller.radius * 0.9f, ~0,
                QueryTriggerInteraction.Ignore).Any(c => !c.transform.IsChildOf(player.transform)))
            { StairHint = "Место у лестницы занято"; return; }
            bool enabled = controller.enabled;
            controller.enabled = false;
            DetachPlayer();
            player.transform.position = target.position;
            player.GravityVector = 0f; player.IgnoreNextFallDamage = true;
            controller.enabled = enabled;
            Physics.SyncTransforms();
            UpdatePassengerAttachment();
        }

        // 1.1.6: as the game says it at its own ladders (the guest opens train doors since 1.1.6).
        private void DoorClosed()
        {
            StairHint = "Дверь закрыта: сначала откройте её";
            var info = Zompiercer.Inventory.InventoryController.global == null ? null : Zompiercer.Inventory.InventoryController.global.simpleInfoDisplayer;
            if (info == null || GlobalSoundEffects.global == null) return;
            info.color = GlobalSoundEffects.global.colorRed;
            info.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey("DoorClosed").ToUpper());
        }

        internal void Freeze()
        {
            _frozen = true;
            if (_clientTrain != null) LanTrainControl.ReleaseGuest();
            ResetIncoming();
            _motion.Clear();
            StairHint = "";
            if (_clientTrain == null) return;
            _clientTrain.Speed = 0f;
            if (_clientController != null)
            {
                _clientController.Velocity = 0f;
                Sound(_clientController.engineSound, false); Sound(_clientController.hornSound, false);
            }
            Status = "Поезд остановлен: нет связи с хостом";
            StairHint = "";
        }

        private static void Sound(AudioSource source, bool playing)
        {
            if (source == null || source.clip == null) return;
            if (playing && !source.isPlaying) source.Play();
            else if (!playing && source.isPlaying) source.Stop();
        }

        internal static TrainCar GetCar(int index)
        {
            var manager = GlobalManager.global;
            var train = manager == null ? null : manager.controlledTrain;
            if (train == null || train.Cars == null || index < 0 || index >= train.Cars.Length) return null;
            return train.Cars[index].GetComponentInChildren<TrainCar>();
        }

        internal static void DetachPlayer()
        {
            var player = GlobalManager.global.controlledChar;
            if (player == null) return;
            player.transform.SetParent(null, true); player.OnTrainCar = null;
            BelowCurrent?.SetValue(player, null); BelowPrevious?.SetValue(player, null);
        }

        private void Ack(int revision) { _send(new Packet { Kind = PacketKind.TrainLayoutAck, Revision = revision }); }
        private void Report(string message) { if (Status != message) { Status = message; _log.LogWarning(message); } }
        private float _deferLogAt;
        private void LogTrainIdentity(string side, TrainManager controlled)
        {
            var managers = UnityEngine.Object.FindObjectsOfType<TrainManager>();
            var controllers = UnityEngine.Object.FindObjectsOfType<TrainController>();
            var cars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
            var curvy = UnityEngine.Object.FindObjectsOfType<CurvyTrain>();
            var controlledController = GlobalManager.global.controlledTrainController;
            string roots = string.Join(", ", managers.Take(4).Select(x =>
                x.name + "#" + x.GetInstanceID() + " cars=" + (x.Cars == null ? -1 : x.Cars.Length)).ToArray());
            string controllerIds = string.Join(",", controllers.Take(4).Select(x => x.GetInstanceID().ToString()).ToArray());
            _log.LogInfo("Train identity " + side + ": controlled=" + controlled.name + "#" + controlled.GetInstanceID() +
                ", controlledController=" + (controlledController == null ? "none" : controlledController.GetInstanceID().ToString()) +
                ", managers=" + managers.Length + " [" + roots + "], cars=" + cars.Length + ", controllers=" + controllers.Length +
                " [" + controllerIds + "], curvy=" + curvy.Length);
        }
        private static NetPose Capture(Transform t)
        {
            var p = t.position; var q = t.rotation;
            return new NetPose { X = p.x, Y = p.y, Z = p.z, QX = q.x, QY = q.y, QZ = q.z, QW = q.w };
        }
        private static void ApplyPose(Transform transform, NetPose from, NetPose to, float t)
        {
            transform.position = Vector3.Lerp(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z), t);
            transform.rotation = Quaternion.Slerp(new Quaternion(from.QX, from.QY, from.QZ, from.QW), new Quaternion(to.QX, to.QY, to.QZ, to.QW), t);
        }
    }

    // 1.4.13, guest: the game's ladders on the guest's copy of the host's train are blocked only by a door that
    // train shows there. Stair.Update keeps the last door it found and never forgets it, so a door of the guest's
    // own train that the host does not have (hidden) stayed "closed" over the ladder for good; and a closed door
    // the host built (shown as a copy, with no DoorController) did not block it.
    internal static class GuestStairs
    {
        private static bool _installed;
        internal static void Install()
        {
            if (_installed) return;
            var harmony = new Harmony("local.zompiercer.lan.guest-stairs");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Stair), "IsBlockedByDoor"), prefix: new HarmonyMethod(typeof(GuestStairs), nameof(BlockedPrefix)));
                _installed = true;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        // 1.4.14: why a ladder is "closed" for the guest, at most every 10 seconds, to the log.
        internal static Action<string> Log;
        private static float _reportAt;
        internal static void Report(string why)
        {
            if (Log == null || Time.unscaledTime < _reportAt) return;
            _reportAt = Time.unscaledTime + 10f;
            Log("Ladder closed for the guest: " + why);
        }
        internal static string Why(Stair stair)
        {
            var door = stair.doorController;
            string own = door == null ? "no door of its own" : "its door " + PathOf(door.transform) + (TrainLayout.HiddenDoor(door) ? " (hidden)" : "") +
                (door.Open ? " open" : " closed") + (door.IsFunctional() ? "" : ", not functional");
            string copy = stair.UpPosition == null ? null : TrainLayout.ReplicaDoorNear(stair.UpPosition.position);
            return "ladder " + PathOf(stair.transform) + ": " + own + (copy != null ? "; " + copy : "");
        }
        internal static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names.ToArray());
        }
        private static bool BlockedPrefix(Stair __instance, ref bool __result)
        {
            if (!LanSaveIsolation.Active || __instance == null) return true;
            try
            {
                var door = __instance.doorController;
                bool shown = door != null && !TrainLayout.HiddenDoor(door);
                __result = shown && door.IsFunctional() && !door.Open || __instance.UpPosition != null && TrainLayout.ReplicaDoorBlocks(__instance.UpPosition.position);
                if (__result) Report(Why(__instance));
                return false;
            }
            catch (Exception) { return true; }
        }
    }

    // Retain support for legacy layer-2 geometry in the character's ground probes.
    // Physical construction now uses Ground; interaction filtering is separate.
    internal static class TrainGroundProbes
    {
        private static bool _installed;
        internal static void Install()
        {
            if (_installed) return;
            var harmony = new Harmony("local.zompiercer.lan.train-ground");
            try
            {
                foreach (string name in new[] { "Update", "Jump" })
                    harmony.Patch(AccessTools.Method(typeof(ZombieFighterController), name),
                        transpiler: new HarmonyMethod(typeof(TrainGroundProbes), nameof(ReplaceGroundRays)));
                _installed = true;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }

        private static IEnumerable<CodeInstruction> ReplaceGroundRays(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if (instruction.opcode == OpCodes.Call && method != null && method.DeclaringType == typeof(Physics) && method.Name == "Raycast")
                {
                    var parameters = method.GetParameters();
                    if ((parameters.Length == 3 || parameters.Length == 4) &&
                        parameters[0].ParameterType == typeof(Vector3) && parameters[1].ParameterType == typeof(Vector3) &&
                        parameters[2].ParameterType == typeof(RaycastHit).MakeByRefType() &&
                        (parameters.Length == 3 || parameters[3].ParameterType == typeof(float)))
                    {
                        var character = new CodeInstruction(OpCodes.Ldarg_0);
                        character.labels.AddRange(instruction.labels); instruction.labels.Clear();
                        character.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                        result.Add(character);
                        instruction.operand = AccessTools.Method(typeof(TrainGroundProbes),
                            parameters.Length == 3 ? nameof(GroundRay) : nameof(GroundRayLimited));
                        replaced++;
                    }
                }
                result.Add(instruction);
            }
            if (replaced != (__originalMethod.Name == "Update" ? 2 : 1))
                throw new InvalidOperationException("Unsupported native train ground probes: " + __originalMethod.Name);
            return result;
        }

        private static bool GroundRay(Vector3 origin, Vector3 direction, out RaycastHit hit, ZombieFighterController player)
        {
            return GroundRayLimited(origin, direction, out hit, float.PositiveInfinity, player);
        }

        private static bool GroundRayLimited(Vector3 origin, Vector3 direction, out RaycastHit hit, float distance, ZombieFighterController player)
        {
            bool found = Physics.Raycast(origin, direction, out hit, distance);
            if (!TrainLayout.HasGroundSupport(player)) return found;
            // Update starts rays one metre above the character; Jump uses 0.2m.
            // Never let a distant floor attach someone standing below/away from it.
            float limit = Mathf.Min(distance, 3f);
            foreach (var candidate in Physics.RaycastAll(origin, direction, limit, 1 << 2, QueryTriggerInteraction.Ignore))
                if (TrainLayout.IsGroundSupport(candidate.collider) && (!found || candidate.distance < hit.distance))
                { hit = candidate; found = true; }
            return found;
        }
    }
}
