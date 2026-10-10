using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZompiercerLAN
{
    // Render a little behind reception so packet jitter does not restart each interpolation.
    // 1.4.6: on the sender's clock. Every State carries the time it was sent, so samples keep the
    // spacing they were taken with however the relay delivers them; the avatar is shown one send
    // interval plus the jitter of the last seconds behind the fastest packet (not a fixed 150 ms
    // behind arrival), briefly carried on past the newest sample when one is late, and its shown
    // speed drives the walk. A dash is replayed when the shown time reaches it.
    // 1.4.7: so is a climb. The game's ladders and stairs move the player to the top at once (E);
    // a step from one sample to the next straight up by ClimbMin..ClimbMax is taken for one.
    internal sealed class PlayerPoseBuffer
    {
        private sealed class Sample
        {
            internal double At;
            internal float Yaw;
            internal Vector3 Position;
            internal int Car;
            internal string Scene;
        }
        // Delay bounds (s), how far the newest sample is carried on (s), a jump that is not walked (m).
        internal const float MinDelay = .06f, MaxDelay = .4f, MaxCarry = .1f, Teleport = 8f;
        internal const float ClimbMin = 1f, ClimbMax = 6f, ClimbAside = 2.5f, ClimbStep = .3f;
        private readonly List<Sample> _samples = new List<Sample>();
        private readonly double[] _transit = new double[64];
        private int _transits, _nextTransit;
        private double _offset, _shown = double.MinValue, _dashAt = double.NaN, _climbAt = double.NaN;
        private float _jitter, _interval = .05f, _delay = .12f, _lastFrame = -1f;
        private uint _lastSent;
        private bool _haveClock;
        private int _dashCount = -1;
        private byte _dashDirection;

        internal void Clear()
        {
            _samples.Clear();
            _transits = 0; _nextTransit = 0; _haveClock = false;
            _jitter = 0f; _interval = .05f; _delay = .12f; _lastFrame = -1f;
            _shown = double.MinValue; _dashAt = double.NaN; _dashCount = -1; _climbAt = double.NaN;
        }

        // The current delay behind the fastest delivery, for the log.
        internal float Delay { get { return _delay; } }

        internal void Push(Packet packet)
        {
            double now = Time.unscaledTime, sent = packet.SentAt / 1000.0;
            // A clock that went back or leapt ahead: the partner's game restarted its session.
            if (_haveClock && (packet.SentAt < _lastSent || packet.SentAt - _lastSent > 60000u))
            { _samples.Clear(); _transits = 0; _nextTransit = 0; _shown = double.MinValue; _dashAt = double.NaN; _climbAt = double.NaN; }
            else if (_haveClock) _interval = Mathf.Lerp(_interval, Mathf.Clamp((packet.SentAt - _lastSent) / 1000f, .02f, .25f), .1f);
            _haveClock = true; _lastSent = packet.SentAt;
            // Transit = arrival minus sending, plus the (unknown, fixed) difference of the two clocks;
            // the smallest of the last 64 is the fastest path, the rest of a sample's is its jitter.
            double transit = now - sent;
            _transit[_nextTransit] = transit; _nextTransit = (_nextTransit + 1) % _transit.Length;
            if (_transits < _transit.Length) _transits++;
            _offset = double.MaxValue;
            for (int i = 0; i < _transits; i++) _offset = Math.Min(_offset, _transit[i]);
            float late = (float)(transit - _offset);
            _jitter = Mathf.Lerp(_jitter, late, late > _jitter ? .5f : .02f);

            var sample = new Sample { At = sent, Car = packet.CarIndex, Scene = packet.Scene,
                Position = packet.CarIndex < 0 ? new Vector3(packet.X, packet.Y, packet.Z) : new Vector3(packet.LocalX, packet.LocalY, packet.LocalZ),
                Yaw = packet.CarIndex < 0 ? packet.Yaw : packet.LocalYaw };
            if (_samples.Count > 0)
            {
                var last = _samples[_samples.Count - 1];
                var step = sample.Position - last.Position;
                if (last.Car != sample.Car || last.Scene != sample.Scene || sample.At - last.At > 1.0 ||
                    step.magnitude > Teleport) _samples.Clear();
                else if (sample.At <= last.At) _samples.RemoveAt(_samples.Count - 1);
                else if (step.y >= ClimbMin && step.y <= ClimbMax && new Vector2(step.x, step.z).magnitude <= ClimbAside &&
                    sample.At - last.At <= ClimbStep) _climbAt = last.At;
            }
            _samples.Add(sample);
            if (_samples.Count > 32) _samples.RemoveAt(0);

            // The first state of a session only sets the count; a new count is a dash that began
            // within the last send interval.
            int count = packet.Dash >> 3;
            if (_dashCount < 0) _dashCount = count;
            else if (count != _dashCount)
            {
                _dashCount = count; _dashDirection = (byte)(packet.Dash & 7);
                _dashAt = sent - _interval * .5;
            }
        }

        // The car the partner is shown in (its space), -1: the world.
        internal int ShownCar { get; private set; } = -1;

        // Where the partner is shown now; speed: its shown ground speed (m/s), 0 when held.
        internal bool Get(out Vector3 position, out float yaw, out float speed)
        {
            position = Vector3.zero; yaw = 0f; speed = 0f;
            if (_samples.Count == 0) return false;
            float frame = Time.unscaledTime;
            if (frame != _lastFrame)
            {
                // The delay grows at once with the jitter and shrinks slowly, so the shown time
                // neither starves nor jumps; it never runs backwards.
                float target = Mathf.Clamp(_interval + _jitter + .015f, MinDelay, MaxDelay);
                float step = _lastFrame < 0f ? 1f : Mathf.Clamp(frame - _lastFrame, 0f, .25f);
                _delay = target > _delay ? Mathf.MoveTowards(_delay, target, .5f * step) : Mathf.MoveTowards(_delay, target, .05f * step);
                _lastFrame = frame;
                _shown = Math.Max(_shown, frame - _offset - _delay);
            }
            double at = _shown;
            while (_samples.Count > 2 && _samples[1].At <= at) _samples.RemoveAt(0);
            var a = _samples[0];
            var b = _samples.Count > 1 ? _samples[1] : a;
            if (b != a && b.At > a.At)
            {
                double span = b.At - a.At;
                var velocity = (b.Position - a.Position) / (float)span;
                if (at <= b.At)
                {
                    float t = (float)Math.Max(0.0, (at - a.At) / span);
                    position = Vector3.Lerp(a.Position, b.Position, t);
                    yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
                    if (at >= a.At) speed = new Vector2(velocity.x, velocity.z).magnitude;
                }
                else
                {
                    // Late: on along the last step for a moment, then held.
                    double over = at - b.At;
                    position = b.Position + velocity * (float)Math.Min(over, MaxCarry);
                    yaw = b.Yaw;
                    if (over < MaxCarry) speed = new Vector2(velocity.x, velocity.z).magnitude;
                }
            }
            else { position = a.Position; yaw = a.Yaw; }
            var car = a.Car < 0 ? null : TrainSync.GetCar(a.Car);
            if (a.Car >= 0 && car == null) return false;
            ShownCar = a.Car;
            if (car != null)
            {
                position = car.transform.TransformPoint(position);
                yaw = (car.transform.rotation * Quaternion.Euler(0f, yaw, 0f)).eulerAngles.y;
            }
            return true;
        }

        // A climb the shown time has reached (shown from where it began).
        internal bool TakeClimb()
        {
            if (double.IsNaN(_climbAt) || _shown < _climbAt) return false;
            _climbAt = double.NaN;
            return true;
        }

        // A dash the shown time has reached: its direction (eighths of a turn clockwise from facing).
        internal bool TakeDash(out byte direction)
        {
            direction = _dashDirection;
            if (double.IsNaN(_dashAt) || _shown < _dashAt) return false;
            _dashAt = double.NaN;
            return true;
        }
    }
}
