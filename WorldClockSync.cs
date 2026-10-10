using System;
using UnityEngine;

namespace ZompiercerLAN
{
    // Sent only by the host in an already ordered world-state packet. Days are
    // Enviro's zero-based day index, not DateTime.DayOfYear.
    internal sealed class ClockSnapshot
    {
        internal int Year;
        internal int Day;
        internal float Hour;
        internal float HoursPerRealSecond;

        internal bool IsValid
        {
            get
            {
                return Year >= 1 && Year <= 9999 &&
                       Day >= 0 && Day <= 366 &&
                       IsFinite(Hour) && Hour >= 0f && Hour <= 24f &&
                       IsFinite(HoursPerRealSecond) &&
                       HoursPerRealSecond >= 0f && HoursPerRealSecond <= 24f;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal sealed class WorldClockSync
    {
        // Four world-state samples per second are expected. A missing packet
        // can be bridged briefly; a stale or disconnected clock stops moving.
        private const float MaxExtrapolationSeconds = 1.5f;
        private const float LastHourBeforeMidnight = 23.9999f;

        private ClockSnapshot _sample;
        private ClockSnapshot _lastApplied;
        private float _receivedAt;
        private bool _haveSample;
        private bool _haveApplied;
        private bool _frozen;
        private TimeOfDay _disabledClock;
        private bool _disabledClockWasEnabled;
        private EnviroTime _modifiedTime;
        private EnviroTime.TimeProgressMode _originalProgressMode;

        internal static bool Capture(out ClockSnapshot snapshot)
        {
            snapshot = null;
            var manager = EnviroSkyMgr.instance;
            if (manager == null) return false;

            EnviroCore sky = ActiveSky(manager);
            if (sky == null || !sky.started || sky.GameTime == null) return false;

            var gameTime = sky.GameTime;
            if (gameTime.DaysInYear < 1 || gameTime.DaysInYear > 366 ||
                gameTime.Days < 0 || gameTime.Days >= gameTime.DaysInYear)
                return false;
            float nativeHour = manager.GetTimeOfDay();
            if (float.IsNaN(nativeHour) || float.IsInfinity(nativeHour)) return false;

            // TimeOfDay.Move can present an hour just beyond 24 after a day
            // change. Its day field has already advanced, so normalize only
            // the hour; do not advance the day a second time.
            float hour = nativeHour % 24f;
            if (hour < 0f) hour += 24f;

            float rate = 0f;
            var clock = TimeOfDay.Global;
            if (clock != null && clock.isActiveAndEnabled && clock.Interval > 0d &&
                clock.CycleLengthInMinutes > 0d &&
                gameTime.ProgressTime == EnviroTime.TimeProgressMode.None &&
                Time.timeScale > 0f)
            {
                double nativeRate = 24d * Time.timeScale /
                                    (clock.CycleLengthInMinutes * 60d);
                if (!double.IsNaN(nativeRate) && !double.IsInfinity(nativeRate))
                    rate = (float)Math.Min(24d, nativeRate);
            }

            snapshot = new ClockSnapshot
            {
                Year = gameTime.Years,
                Day = gameTime.Days,
                Hour = hour,
                HoursPerRealSecond = rate
            };
            return snapshot.IsValid;
        }

        internal bool Receive(ClockSnapshot snapshot, float receivedAt)
        {
            if (snapshot == null || !snapshot.IsValid ||
                float.IsNaN(receivedAt) || float.IsInfinity(receivedAt))
                return false;
            _sample = Copy(snapshot);
            _receivedAt = receivedAt;
            _haveSample = true;
            _haveApplied = false;
            _frozen = false;
            return true;
        }

        // Call from the client's LateUpdate, after its matching scene is ready.
        // The host never calls Render. Enviro's following Update recalculates
        // the sun, lighting, and sky from the time set here.
        internal void Render(bool sceneReady)
        {
            if (!sceneReady || !_haveSample) return;

            var manager = EnviroSkyMgr.instance;
            if (manager == null) return;
            EnviroCore sky = ActiveSky(manager);
            if (sky == null || !sky.started || sky.GameTime == null) return;
            if (_sample.Day >= sky.GameTime.DaysInYear) return;

            DisableLocalClock();
            if (!ReferenceEquals(_modifiedTime, sky.GameTime))
            {
                RestoreProgressMode();
                _modifiedTime = sky.GameTime;
                _originalProgressMode = _modifiedTime.ProgressTime;
            }
            _modifiedTime.ProgressTime = EnviroTime.TimeProgressMode.None;

            ClockSnapshot target = Copy(_sample);
            float elapsed = _frozen ? 0f : Time.unscaledTime - _receivedAt;
            if (elapsed < 0f) elapsed = 0f;
            if (elapsed > MaxExtrapolationSeconds) elapsed = MaxExtrapolationSeconds;
            target.Hour += target.HoursPerRealSecond * elapsed;

            // Wait for the host's next calendar sample at midnight. Advancing
            // the day locally would cause two owners of rollover events.
            if (target.Hour >= 24f) target.Hour = LastHourBeforeMidnight;
            if (target.Hour < 0f) target.Hour = 0f;

            int hour = (int)target.Hour;
            float minutePart = (target.Hour - hour) * 60f;
            int minute = (int)minutePart;
            int second = (int)((minutePart - minute) * 60f);
            manager.SetTime(target.Year, target.Day, hour, minute, second);
            manager.SetTimeOfDay(target.Hour);
            sky.ResetHourEventTimer();

            _lastApplied = target;
            _lastApplied.HoursPerRealSecond = 0f;
            _haveApplied = true;

            var clock = TimeOfDay.Global;
            if (clock != null)
            {
                float localHour = target.Hour;
                clock.SetCityLights(localHour <= clock.CityLightsOff ||
                                    localHour >= clock.CityLightsOn);
            }
        }

        internal void Freeze()
        {
            if (!_haveSample) return;
            if (_haveApplied) _sample = Copy(_lastApplied);
            _sample.HoursPerRealSecond = 0f;
            _frozen = true;
        }

        // Use when the old session is discarded. A disconnect that must retain
        // the last visible time should call Freeze instead.
        internal void Reset()
        {
            _haveSample = false;
            _haveApplied = false;
            _frozen = false;
            RestoreProgressMode();
            if (_disabledClock != null) _disabledClock.enabled = _disabledClockWasEnabled;
            _disabledClock = null;
        }

        private void DisableLocalClock()
        {
            var clock = TimeOfDay.Global;
            if (clock != _disabledClock)
            {
                if (_disabledClock != null) _disabledClock.enabled = _disabledClockWasEnabled;
                _disabledClock = clock;
                _disabledClockWasEnabled = clock != null && clock.enabled;
            }
            if (clock != null) clock.enabled = false;
        }

        private static ClockSnapshot Copy(ClockSnapshot source)
        {
            return new ClockSnapshot
            {
                Year = source.Year,
                Day = source.Day,
                Hour = source.Hour,
                HoursPerRealSecond = source.HoursPerRealSecond
            };
        }

        private void RestoreProgressMode()
        {
            if (_modifiedTime == null) return;
            _modifiedTime.ProgressTime = _originalProgressMode;
            _modifiedTime = null;
        }

        private static EnviroCore ActiveSky(EnviroSkyMgr manager)
        {
            switch (manager.currentEnviroSkyVersion)
            {
                case EnviroSkyMgr.EnviroSkyVersion.HD:
                    return EnviroSky.instance;
                case EnviroSkyMgr.EnviroSkyVersion.LW:
                    return EnviroSkyLite.instance;
                default:
                    return null;
            }
        }
    }
}
