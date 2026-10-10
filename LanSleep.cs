using System;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;

namespace ZompiercerLAN
{
    // Sleeping together (1.2.2). The game's sleep runs the world clock fast for the hours chosen in
    // its bed window. With a partner in this world, the one who lies down waits under a dark screen
    // ("waiting for the partner", Space or E to get up) and the partner hears of it; once both lie,
    // the host starts the sleep for the fewer of the two chosen hours and both sleep it out with the
    // game's own sleep (the guest's clock follows the host's). Alone, the game sleeps as ever.
    // Main thread only.
    internal static class LanSleep
    {
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Set by the plugin every frame: a partner in this world (alive), whether this side hosts,
        // how to tell the partner (action, hours), and the partner's name for messages.
        internal static Func<bool> PartnerHere;
        internal static bool Hosting;
        internal static Action<byte, int> Send;
        // Host (1.3.0 guest checks): the guest sleeps now, for this many hours.
        internal static Action<int> GuestSleeps;

        private static GlobalSleepController _controller;
        private static bool _bypass, _waiting;
        private static int _hours, _partnerHours;
        private static float _waitingSince, _health;
        private static GUIStyle _style;
        internal static bool Waiting { get { return _waiting; } }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.sleep");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(GlobalSleepController), "GoToSleepButton"), prefix: new HarmonyMethod(typeof(LanSleep), nameof(SleepPrefix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Sleep hook unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { StandUp(false); _harmony?.UnpatchSelf(); _harmony = null; PartnerHere = null; Send = null; GuestSleeps = null; }

        // The game's "go to sleep" button: alone it sleeps; with a partner here it lies down and waits.
        private static bool SleepPrefix(GlobalSleepController __instance)
        {
            if (_bypass || PartnerHere == null || Send == null) return true;
            if (!PartnerHere())
            {
                // Alone (the host is down or away): the game's sleep; the host still hears of it (1.3.0 checks).
                if (!Hosting) Send(LanProtocol.SleepWait, Mathf.Clamp(Mathf.RoundToInt(__instance.timeToSleep), 1, LanProtocol.MaxSleepHours));
                return true;
            }
            try
            {
                _controller = __instance;
                _hours = Mathf.Clamp(Mathf.RoundToInt(__instance.timeToSleep), 1, LanProtocol.MaxSleepHours);
                var gui = Zompiercer.GUI.GUIManager.Global;
                if (gui != null && gui.sleepActivatorBase != null) gui.sleepActivatorBase.SetActive(false);
                if (GlobalTintController.Global != null) GlobalTintController.Global.SetGlobalTint(true);
                var player = GlobalManager.global.controlledChar;
                if (player != null) { player.controlEnabled = false; _health = player.zombieFighterIndicatorsBar == null ? 0f : player.zombieFighterIndicatorsBar.currentHealth; }
                _waiting = true; _waitingSince = Time.unscaledTime;
                Send(LanProtocol.SleepWait, _hours);
                _log?.Invoke("Lying down to sleep " + _hours + " h; waiting for the partner");
                if (Hosting && _partnerHours > 0) StartShared();
            }
            catch (Exception ex) { _log?.Invoke("Shared sleep failed, sleeping alone: " + ex.Message); _waiting = false; return true; }
            return false;
        }

        // The partner's Sleep packet.
        internal static void Receive(byte action, int hours)
        {
            if (action == LanProtocol.SleepWait)
            {
                if (Hosting) GuestSleeps?.Invoke(hours); // 1.3.0: the guest's sleep may grow from now on
                bool was = _partnerHours > 0;
                _partnerHours = hours;
                if (Hosting && _waiting) { StartShared(); return; }
                if (!was && !_waiting) LanNotify.Alert(LanSkinPicker.PartnerLabel(Hosting) + " лёг спать: ляг тоже, чтобы промотать время");
            }
            else if (action == LanProtocol.SleepCancel)
            {
                if (_partnerHours > 0 && !_waiting) LanNotify.Message(LanSkinPicker.PartnerLabel(Hosting) + " встал");
                _partnerHours = 0;
            }
            else if (action == LanProtocol.SleepStart && !Hosting) Sleep(hours);
        }

        // Host: both lie: the fewer of the two chosen hours, for both.
        private static void StartShared()
        {
            int hours = Math.Max(1, Math.Min(_hours, _partnerHours));
            Send?.Invoke(LanProtocol.SleepStart, hours);
            if (Hosting) GuestSleeps?.Invoke(hours);
            Sleep(hours);
        }

        // The game's own sleep for `hours`.
        private static void Sleep(int hours)
        {
            var controller = _controller != null ? _controller : GlobalSleepController.global;
            _waiting = false; _partnerHours = 0;
            if (controller == null) { _log?.Invoke("Shared sleep: no sleep controller here"); return; }
            try
            {
                controller.timeToSleep = hours;
                _bypass = true;
                try { controller.GoToSleepButton(); } finally { _bypass = false; }
                _log?.Invoke("Shared sleep for " + hours + " h");
            }
            catch (Exception ex) { _log?.Invoke("Shared sleep not started: " + ex.Message); StandUp(false); }
        }

        // Guest: a copy of a host bed in sight; the game's own bed hint and key open the bed window.
        internal static bool LookAtBed(Zompiercer.Inputs.InputController input)
        {
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            var gui = Zompiercer.GUI.GUIManager.Global;
            var sleep = GlobalSleepController.global;
            if (info == null || gui == null || sleep == null || gui.sleepActivatorBase == null || gui.sleepActivatorBase.activeSelf || _waiting) return true;
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrainController;
            if (train != null && train.Velocity != 0f)
            {
                info.color = GlobalSoundEffects.global.colorRed;
                info.SetText(Zompiercer.GUI.LocalizationCore.GetTextByKey("SleepInMovingTrainWarning").ToUpper());
                return true;
            }
            info.color = GlobalSoundEffects.global.colorGreen;
            info.SetIconText("GoToSleep", (Zompiercer.Inputs.Actions)29, (Zompiercer.Inputs.Actions)0, true);
            bool lie = input.isKbMouseDevice ? input.isLeverUpDown : input.gp_isAltUseDown;
            if (!lie) return true;
            input.isLeverUpDown = false; input.gp_isAltUseDown = false;
            gui.GlobalInventoryCloser();
            sleep.BackFromBed();
            sleep.sleepConditions = 2; // a bed, as the game's own beds
            return true;
        }

        // Gets up from waiting (`tell`: the partner hears of it).
        private static void StandUp(bool tell)
        {
            if (!_waiting) return;
            _waiting = false;
            try
            {
                if (GlobalTintController.Global != null) GlobalTintController.Global.SetGlobalTint(false);
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (player != null) player.controlEnabled = true;
                var gui = Zompiercer.GUI.GUIManager.Global;
                if (gui != null) { gui.CursorLock(); gui.UpdateStateInput(); }
            }
            catch (Exception ex) { _log?.Invoke("Getting up failed: " + ex.Message); }
            if (tell) Send?.Invoke(LanProtocol.SleepCancel, 0);
        }

        // Every frame: alone again (the partner left), hurt, or a game menu: the waiting ends.
        internal static void Tick()
        {
            if (!_waiting) return;
            bool partner = PartnerHere != null && PartnerHere();
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var bar = player == null ? null : player.zombieFighterIndicatorsBar;
            if (bar != null && bar.currentHealth < _health - .5f) { StandUp(true); LanNotify.Message("Вас ранили — вы встали"); return; }
            if (!partner) { int hours = _hours; StandUp(false); Sleep(hours); return; } // alone now: the game's sleep
            if (player != null) player.controlEnabled = false;
        }

        // OnGUI: the waiting screen; Space or E gets up.
        internal static void Draw()
        {
            if (!_waiting) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Space || e.keyCode == KeyCode.E) && Time.unscaledTime - _waitingSince > .5f)
            { e.Use(); StandUp(true); return; }
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(new Rect(0f, Screen.height * .4f, Screen.width, 120f),
                "Ждём, пока ляжет напарник (" + LanSkinPicker.PartnerLabel(Hosting) + ")…\nПробел или E — встать", _style);
        }

        // A session ended or changed world.
        internal static void Reset() { StandUp(false); _partnerHours = 0; }
    }
}
