using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ZompiercerLAN
{
    // 1.1.9: revive. A player who loses all health while the partner can still help lies downed
    // instead of dying: the game's own death flag keeps zombies away, the player cannot move or act,
    // and its view drops to the ground. The partner revives it by holding the use key next to it for
    // ReviveSeconds. After DownedSeconds, when nobody can help any more, or when the player holds the
    // use key for GiveUpSeconds, the game's own death follows (for the guest LanGuestDeath: its
    // belongings in a bag, back at the host's train). Host and guest alike. Main thread only.
    internal static class LanDowned
    {
        internal const float DownedSeconds = 30f, ReviveSeconds = 3f, GiveUpSeconds = 2f, ReviveReach = 2.5f, AcceptReach = 4f, ReviveHealth = .3f, ResendEvery = .25f;
        private static readonly MethodInfo DeadMethod = AccessTools.Method(typeof(ZombieFighterIndicatorsBar), "Dead");
        private static readonly MethodInfo DisableCrouchMethod = AccessTools.Method(typeof(ZombieFighterController), "DisableCrouch");
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _dying, _useHeld, _prompt, _refusalLogged;
        private static float _downAt, _helpedAt = -100f, _giveUpSince = -1f, _reviveSince = -1f, _sentAt = -100f;
        private static GUIStyle _title, _text, _small;

        // Set by the plugin while a session runs.
        internal static Func<bool> PartnerCanHelp; // a partner in this world who is up, or will be back (a respawning guest)
        internal static Func<bool> PartnerDown;    // the partner lies downed
        internal static Func<Vector3?> PartnerAt;  // where the partner is (world), when known
        internal static Action<byte> Send;         // a Revive packet to the partner

        internal static bool Down { get; private set; }
        // The local player's hold on reviving the partner, 0..1.
        internal static float Progress { get; private set; }
        internal static float Left { get { return Down ? Mathf.Max(0f, DownedSeconds - (Time.unscaledTime - _downAt)) : 0f; } }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.downed");
            try
            {
                if (DeadMethod == null) throw new MissingMethodException("ZombieFighterIndicatorsBar.Dead");
                _harmony.Patch(AccessTools.Method(typeof(ZombieFighterIndicatorsBar), "Dead"), prefix: new HarmonyMethod(typeof(LanDowned), nameof(DeadPrefix)));
            }
            catch (Exception ex) { _harmony.UnpatchSelf(); log("Revive unavailable (deaths are the game's own): " + ex.GetType().Name + ": " + ex.Message); }
        }

        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf(); _harmony = null;
            PartnerCanHelp = null; PartnerDown = null; PartnerAt = null; Send = null;
        }

        private static ZombieFighterController Player() { var manager = GlobalManager.global; return manager == null ? null : manager.controlledChar; }

        // The game's death of this player: downed instead, while the partner can help.
        private static bool DeadPrefix(ZombieFighterIndicatorsBar __instance)
        {
            if (_dying) return true;
            try
            {
                var c = Player();
                if (Down || c == null || c.zombieFighterIndicatorsBar != __instance || PartnerCanHelp == null || !PartnerCanHelp()) return true;
                Enter(c, __instance);
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Downed state not entered: " + ex.GetType().Name + ": " + ex.Message); Down = false; return true; }
        }

        private static void Enter(ZombieFighterController c, ZombieFighterIndicatorsBar bar)
        {
            Down = true; _downAt = Time.unscaledTime; _helpedAt = -100f; _giveUpSince = -1f; _useHeld = false; _refusalLogged = false;
            bar.deathRegister = true; // zombies leave a fallen player be, as they do a dead one
            bar.currentHealth = 0f;
            c.controlEnabled = false;
            try { var gui = GlobalManager.global.canvas; if (gui != null) gui.GlobalInventoryCloser(); } catch (Exception) { }
            Crouch(c, true);
            try { if (c.zombieFighterEffects != null) c.zombieFighterEffects.RandomAttackPainSound(); } catch (Exception) { }
            _log?.Invoke("Player downed: the partner can revive it within " + DownedSeconds + " s");
        }

        // Every frame (Update).
        internal static void Tick()
        {
            try
            {
                TrackUse();
                var c = Player();
                if (Down) TickDown(c); else TickReviver(c);
            }
            catch (Exception ex) { _log?.Invoke("Revive tick failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void TickDown(ZombieFighterController c)
        {
            var bar = c == null ? null : c.zombieFighterIndicatorsBar;
            if (bar == null || !bar.deathRegister) { Down = false; return; } // a load or a respawn ended it
            c.controlEnabled = false;
            bar.currentHealth = 0f;
            float now = Time.unscaledTime;
            if (_useHeld) { if (_giveUpSince < 0f) _giveUpSince = now; } else _giveUpSince = -1f;
            bool alone = PartnerCanHelp == null || !PartnerCanHelp();
            if (alone) Die(c, bar, "nobody can help any more");
            else if (Left <= 0f) Die(c, bar, "nobody came in time");
            else if (_giveUpSince >= 0f && now - _giveUpSince >= GiveUpSeconds) Die(c, bar, "gave up");
        }

        // The game's own death after all (the guest's: LanGuestDeath takes over in YouDied).
        private static void Die(ZombieFighterController c, ZombieFighterIndicatorsBar bar, string why)
        {
            Down = false;
            _log?.Invoke("Downed player dies: " + why);
            Crouch(c, false);
            bar.deathRegister = false;
            _dying = true;
            try { DeadMethod.Invoke(bar, null); }
            finally { _dying = false; }
        }

        private static void Revive(ZombieFighterController c)
        {
            var bar = c.zombieFighterIndicatorsBar;
            Down = false;
            bar.deathRegister = false;
            bar.currentHealth = Mathf.Max(bar.currentHealth, bar.MaxHealth * ReviveHealth);
            c.controlEnabled = true;
            Crouch(c, false);
            _log?.Invoke("Player revived by the partner");
            LanNotify.Message("Вас подняли");
        }

        // The partner's Revive packet (the plugin checked its session and world).
        internal static void Receive(byte action)
        {
            var c = Player();
            if (!Down || c == null) return;
            if (action == LanProtocol.ReviveHold) { _helpedAt = Time.unscaledTime; return; }
            if (action != LanProtocol.ReviveDone) return;
            var at = PartnerAt == null ? null : PartnerAt();
            if (at == null || Vector3.Distance(at.Value, c.transform.position) > AcceptReach)
            {
                if (!_refusalLogged) { _refusalLogged = true; _log?.Invoke("Revive refused: the partner is not next to this player"); }
                return;
            }
            Revive(c);
        }

        // Next to the downed partner, looking at it, holding the use key: the hold is sent until it
        // completes; then "revived" is sent until the partner's state says it is up.
        private static void TickReviver(ZombieFighterController c)
        {
            _prompt = false;
            float now = Time.unscaledTime;
            var bar = c == null ? null : c.zombieFighterIndicatorsBar;
            var at = PartnerAt == null ? null : PartnerAt();
            var input = Zompiercer.Inputs.InputController.Global;
            bool partnerDown = PartnerDown != null && PartnerDown();
            if (!partnerDown || at == null || bar == null || bar.deathRegister || !c.controlEnabled || input == null || (int)input.CurrentState != 1)
            { _reviveSince = -1f; Progress = 0f; return; }
            var flat = at.Value - c.transform.position; float rise = flat.y; flat.y = 0f;
            var camera = GlobalManager.global.MainCamera != null ? GlobalManager.global.MainCamera : Camera.main;
            bool facing = camera != null && Vector3.Angle(camera.transform.forward, at.Value + Vector3.up * .3f - camera.transform.position) < 45f;
            _prompt = flat.magnitude <= ReviveReach && Mathf.Abs(rise) < 2f && facing;
            if (!_prompt || !_useHeld) { _reviveSince = -1f; Progress = 0f; return; }
            // The hold is for the partner, not for a door or item behind it.
            input.isUseKeyDown = false; input.gp_isUseOpenTalkDown = false;
            if (_reviveSince < 0f) _reviveSince = now;
            Progress = Mathf.Clamp01((now - _reviveSince) / ReviveSeconds);
            if (now - _sentAt >= ResendEvery) { _sentAt = now; Send?.Invoke(Progress >= 1f ? LanProtocol.ReviveDone : LanProtocol.ReviveHold); }
        }

        // The use key held down (keyboard: from its press to its release, checked against the action
        // itself; gamepad: the game's own held flag).
        private static void TrackUse()
        {
            var input = Zompiercer.Inputs.InputController.Global;
            if (input == null) { _useHeld = false; return; }
            if (!input.isKbMouseDevice) { _useHeld = input.gp_isuseOpenTalkPressed; return; }
            if (input.isUseKeyDown) _useHeld = true;
            if (input.isUseKeyUp) _useHeld = false;
            try
            {
                var use = input.Controls == null ? null : input.Controls.KbMouse.Use;
                if (_useHeld && use != null && use.enabled && use.ReadValue<float>() < .5f) _useHeld = false;
            }
            catch (Exception) { }
        }

        // The view drops as in the game's own crouch; standing up restores the game's height.
        private static void Crouch(ZombieFighterController c, bool down)
        {
            try
            {
                if (down)
                {
                    c.crouch = true;
                    if (c.controller != null) { c.controller.height = .6f; c.controller.center = new Vector3(0f, .3f, 0f); }
                }
                else
                {
                    c.crouch = false;
                    DisableCrouchMethod?.Invoke(c, null);
                }
                Zompiercer.Bus.Bus<Zompiercer.Bus.Events.SetCrouchEvent>.Execute(new Zompiercer.Bus.Events.SetCrouchEvent(down));
            }
            catch (Exception ex) { _log?.Invoke("Downed view not changed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // OnGUI: the downed player's screen, or the reviver's prompt. partner: its name as shown.
        internal static void Draw(string partner)
        {
            if (!Down && !_prompt) return;
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 28, fontStyle = FontStyle.Bold, richText = false };
                _title.normal.textColor = new Color(.95f, .25f, .2f);
                _text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 17, wordWrap = true, richText = false };
                _text.normal.textColor = Color.white;
                _small = new GUIStyle(_text) { fontSize = 14 };
                _small.normal.textColor = new Color(.85f, .85f, .85f);
            }
            var previous = GUI.color;
            if (Down)
            {
                GUI.color = new Color(.45f, 0f, 0f, .35f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = previous;
                var box = new Rect(Screen.width / 2f - 270f, Screen.height * .3f, 540f, 140f);
                GUI.Box(box, GUIContent.none); GUI.Box(box, GUIContent.none);
                GUI.Label(new Rect(box.x, box.y + 10f, box.width, 40f), "Вы тяжело ранены", _title);
                bool helped = Time.unscaledTime - _helpedAt < .6f;
                GUI.Label(new Rect(box.x + 16f, box.y + 54f, box.width - 32f, 30f),
                    helped ? partner + " поднимает вас…" : partner + " может поднять вас: " + Mathf.CeilToInt(Left) + " с", _text);
                GUI.Label(new Rect(box.x + 16f, box.y + 94f, box.width - 32f, 30f), _giveUpSince >= 0f ? "Сдаётесь…" : "Удерживайте E, чтобы сдаться", _small);
                return;
            }
            var frame = new Rect(Screen.width / 2f - 210f, Screen.height * .62f, 420f, 64f);
            GUI.Box(frame, GUIContent.none);
            GUI.Label(new Rect(frame.x, frame.y + 6f, frame.width, 26f), Progress > 0f ? "Поднимаем: " + partner + "…" : "Удерживайте E — поднять: " + partner, _text);
            var bar = new Rect(frame.x + 20f, frame.y + 38f, frame.width - 40f, 12f);
            GUI.color = new Color(.15f, .15f, .15f, .9f); GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(.35f, .85f, .4f, 1f); GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Progress, bar.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
