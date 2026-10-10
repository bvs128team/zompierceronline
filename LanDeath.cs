using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Zompiercer.GUI;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Death of the guest in the host's world (0.12.0). The game's own death screen offers only
    // the main menu, which would end the session; instead the guest waits RespawnSeconds and
    // comes back next to the host. Everything it carried stays where it fell: the host moves
    // the guest's whole ledger into loot bags there (LanStorageHost, storage request 12), and
    // the guest empties its own inventories when the host confirms. The host's death is the
    // game's own (death screen, main menu, load); the guest is told about it.
    // Main thread only.
    internal static class LanGuestDeath
    {
        internal const float RespawnSeconds = 10f, AnswerTimeout = 20f, RespawnHealth = .5f, RespawnNeeds = .35f;
        private static readonly FieldInfo Detector = AccessTools.Field(typeof(ZombieFighterIndicatorsBar), "zombieFighterDamagDetector");
        private static Harmony _harmony;
        private static Action<string> _log;
        private static GUIStyle _title, _text;

        // Set by the plugin: true while this player is a guest placed in the host's world.
        internal static Func<bool> Attached;
        internal static bool Dead { get; private set; }
        internal static bool Answered { get; private set; }
        // Where it fell: car-local when Car >= 0.
        internal static int Car { get; private set; }
        internal static Vector3 Point { get; private set; }
        private static float _diedAt;
        private static string _outcome;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.death");
            try { _harmony.Patch(AccessTools.Method(typeof(GUIManager), "YouDied"), prefix: new HarmonyMethod(typeof(LanGuestDeath), nameof(YouDiedPrefix))); }
            catch (Exception ex) { _harmony.UnpatchSelf(); log("Death hook unavailable (the game's death screen is used): " + ex.GetType().Name + ": " + ex.Message); }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; Dead = false; Attached = null; }

        private static bool YouDiedPrefix()
        {
            try
            {
                if (Attached == null || !Attached()) return true;
                Begin();
                return false;
            }
            catch (Exception ex) { _log?.Invoke("Guest death not handled: " + ex.Message); Dead = false; return true; }
        }

        private static void Begin()
        {
            var manager = GlobalManager.global;
            var c = manager.controlledChar;
            Dead = true; Answered = false; _diedAt = Time.unscaledTime;
            _outcome = "Сообщаем хосту…";
            c.controlEnabled = false;
            try { var gui = manager.canvas != null ? manager.canvas : Object.FindObjectOfType<GUIManager>(); if (gui != null) gui.GlobalInventoryCloser(); } catch (Exception) { }
            var position = c.transform.position;
            int car = -1; var point = position;
            var train = manager.controlledTrain; var on = c.OnTrainCar;
            if (on != null && train != null && on.transform.IsChildOf(train.transform))
            {
                int index = on.GetCarIndex();
                var local = on.transform.InverseTransformPoint(position);
                if (index >= 0 && index < LanProtocol.MaxCars && Mathf.Abs(local.x) < LanStorage.MaxCarLocal && Mathf.Abs(local.y) < LanStorage.MaxCarLocal && Mathf.Abs(local.z) < LanStorage.MaxCarLocal)
                { car = index; point = local; }
            }
            Car = car; Point = point;
            _log?.Invoke("Guest died; respawn next to the host in " + RespawnSeconds + " s");
        }

        // The host's answer to the death request (null status: no answer, no session).
        internal static void Answer(byte status, int stacks)
        {
            if (!Dead) return;
            Answered = true;
            if (status == LanStorage.Ok)
            {
                _outcome = stacks > 0 ? "Ваши вещи остались в сумке на месте гибели" : "С собой не было вещей";
                ClearLocalItems();
            }
            else _outcome = "Вещи остались при вас: " + LanStorage.Describe(status);
        }

        internal static bool ReadyToRespawn
        {
            get
            {
                float dead = Time.unscaledTime - _diedAt;
                return Dead && dead >= RespawnSeconds && (Answered || dead >= AnswerTimeout);
            }
        }

        // Back on its feet: some health, not starving, the body visible, control back.
        // The caller moves it next to the host.
        internal static void Respawn()
        {
            if (!Dead) return;
            Dead = false;
            var c = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (c == null) return;
            var bar = c.zombieFighterIndicatorsBar;
            if (bar != null)
            {
                bar.deathRegister = false;
                bar.currentHealth = Mathf.Max(bar.currentHealth, bar.MaxHealth * RespawnHealth);
                bar.currentHunger = Mathf.Max(bar.currentHunger, bar.maxHunger * RespawnNeeds);
                bar.currentThirst = Mathf.Max(bar.currentThirst, bar.maxThirst * RespawnNeeds);
                bar.currentSleep = Mathf.Max(bar.currentSleep, bar.maxSleep * RespawnNeeds);
                var detector = Detector == null ? null : Detector.GetValue(bar) as ZombieFighterDamagDetector;
                if (detector != null)
                {
                    detector.burning = false; detector.burningTimer = 0f;
                    if (detector.charBody != null) detector.charBody.SetActive(true);
                }
            }
            c.controlEnabled = true;
            _log?.Invoke("Guest respawned");
        }

        // The host confirmed: the guest's own copies of its belongings are gone too.
        private static void ClearLocalItems()
        {
            var c = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (c == null || c.AllInventory == null) return;
            foreach (var inventory in c.AllInventory)
            {
                if (inventory == null || inventory.content == null) continue;
                foreach (var item in inventory.content.ToArray())
                {
                    if (item == null || item.amount <= 0) continue;
                    try { inventory.Extract(item, item.amount); } catch (Exception ex) { _log?.Invoke("Item not removed after death: " + ex.Message); }
                }
            }
            // Not dropped by the player: nothing of this goes to the host as a drop.
            LanWorldItems.Dropped.Clear(); LanWorldItems.Placed.Clear();
        }

        // Is this game's own player dead (the host's flag for the guest)? 1.1.9: lying downed is not
        // dead (LanDowned keeps the game's death flag up meanwhile).
        internal static bool LocalDead()
        {
            var c = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var bar = c == null ? null : c.zombieFighterIndicatorsBar;
            return bar != null && !LanDowned.Down && (bar.deathRegister || bar.currentHealth <= 0f);
        }

        // OnGUI: the guest's death screen.
        internal static void Draw()
        {
            if (!Dead) return;
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold, richText = false };
                _title.normal.textColor = new Color(.9f, .2f, .15f);
                _text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 17, wordWrap = true, richText = false };
                _text.normal.textColor = Color.white;
            }
            var box = new Rect(Screen.width / 2f - 260f, Screen.height * .32f, 520f, 150f);
            GUI.Box(box, GUIContent.none); GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x, box.y + 12f, box.width, 40f), "Вы погибли", _title);
            GUI.Label(new Rect(box.x + 16f, box.y + 56f, box.width - 32f, 44f), _outcome ?? "", _text);
            float left = Mathf.Max(0f, RespawnSeconds - (Time.unscaledTime - _diedAt));
            string wait = left > 0f ? "Возрождение у поезда хоста через " + Mathf.CeilToInt(left) + " с" :
                Answered ? "Возрождение…" : "Ждём ответа хоста…";
            GUI.Label(new Rect(box.x + 16f, box.y + 104f, box.width - 32f, 30f), wait, _text);
        }
    }
}
