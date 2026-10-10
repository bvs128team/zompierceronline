using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // 1.1.7: a boss fight starts only when every player of the session is in the arena. The game
    // closes and locks the arena gates (TrapDoorCloser, the city mall of the railway worker boss)
    // as soon as its own player crosses the trigger right behind them.
    // Host: while the guest plays in this world, the trap waits until the guest has passed the
    // gates too (tracked from its positions, LanArenaZone) and then closes them natively; until
    // then the boss neither sees nor hears anyone and takes no harm: no damage, no lost limbs, no
    // fire, no reaction to hits (the host sees why). Without a guest the game works as before.
    // Guest: its own copy of the trap never acts; the gates follow the host's door states.
    // Main thread only.
    internal static class LanBossArena
    {
        private const float ScanEvery = 10f, HintEvery = 10f, ImmuneHintEvery = 5f, BossReach = 90f, MaxZone = 20f, Margin = .5f, Height = 2.5f;
        internal const string BossId = "boss railway worker";
        private static Harmony _harmony;
        private static Action<string> _info, _warn, _hint;
        private static MethodInfo _trigger;
        private static bool _releasing, _failLogged, _guestRequired;
        private static float _scanAt, _immuneHintAt;
        private static readonly List<Arena> Arenas = new List<Arena>();
        // Bosses whose fight has begun: never held again.
        private static readonly HashSet<int> Released = new HashSet<int>();
        // Held bosses: the health of each limb when the hold began. A hit still lowers it (the game
        // does that inline), so it is put back every frame: no limb is weakened before the fight.
        private static readonly Dictionary<int, Limbs> Calm = new Dictionary<int, Limbs>();

        private sealed class Limbs
        {
            internal ZombieAIController Ai;
            internal LimbDetector[] Parts;
            internal float[] Health;
        }

        private sealed class Arena
        {
            internal TrapDoorCloser Trap;
            internal Vector3 Origin, Inward, Across;
            internal LanArenaZone Zone;
            internal readonly LanArenaZone.Track Host = new LanArenaZone.Track(), Guest = new LanArenaZone.Track();
            internal bool Pending;
            internal float HintAt;

            internal void Local(Vector3 p, out float x, out float y, out float z)
            {
                var d = p - Origin;
                x = Vector3.Dot(d, Across); y = d.y; z = Vector3.Dot(d, Inward);
            }

            internal void Move(LanArenaZone.Track track, Vector3 p)
            {
                float x, y, z; Local(p, out x, out y, out z);
                Zone.Move(track, x, y, z);
            }

            // The game's own trigger saw this player: inside, wherever its position falls.
            internal void Enter(LanArenaZone.Track track, Vector3 p)
            {
                float x, y, z; Local(p, out x, out y, out z);
                track.Have = true; track.Inside = true; track.X = x; track.Y = y; track.Z = z;
            }
        }

        internal static void Initialize(Action<string> info, Action<string> warn, Action<string> hint)
        {
            _info = info; _warn = warn; _hint = hint;
            if (_harmony != null) return;
            _harmony = new Harmony("local.zompiercer.lan.boss-arena");
            try
            {
                _trigger = AccessTools.Method(typeof(TrapDoorCloser), "OnTriggerEnter");
                _harmony.Patch(AccessTools.Method(typeof(TrapDoorCloser), "OnTriggerEnter"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(TriggerPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(TrapDoorCloser), "Update"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(UpdatePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "CheckLOS"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(SightPrefix)));
                // A held boss takes no harm and does not react to it.
                _harmony.Patch(AccessTools.Method(typeof(ZombieHP), "CauseDamage"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(DamagePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieDamagDetector), "DetectionBulletHits"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(DetectorPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieDamagDetector), "DetectionDamage"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(DetectorPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieDamagDetector), "SetOnFire"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(DetectorPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieDamagDetector), "Dead"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(DetectorPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAppearanceDismemberment), "DetachmentHead"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(PartPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAppearanceDismemberment), "DetachmentHandR"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(PartPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAppearanceDismemberment), "DetachmentHandL"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(PartPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAppearanceDismemberment), "DetachmentLegR"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(PartPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAppearanceDismemberment), "DetachmentLegL"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(PartPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "OnAttacked"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(AggroPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "ReactionToInjury"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(AggroPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "AttackPlayer"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(AggroPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieAIController), "SetTarget"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(TargetPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(DefensiveElementDetector), "OnTriggerStay"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(ContactPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ZombieTrainDetector), "OnTriggerEnter"), prefix: new HarmonyMethod(typeof(LanBossArena), nameof(ContactPrefix)));
            }
            catch (Exception ex) { _harmony.UnpatchSelf(); _trigger = null; warn("Boss arena hook unavailable: " + ex.GetType().Name + ": " + ex.Message); }
        }

        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf(); _harmony = null; _trigger = null; _guestRequired = false;
            Arenas.Clear(); Released.Clear(); Calm.Clear();
        }

        // Every frame. hosting: this game hosts a session; guestRequired: the guest plays in this
        // world (alive or dead); guest: where it stands while alive; guestDead: waiting to respawn.
        internal static void Tick(bool hosting, bool guestRequired, Vector3? guest, bool guestDead)
        {
            try
            {
                _guestRequired = hosting && guestRequired;
                if (Arenas.RemoveAll(a => a.Trap == null) > 0) _scanAt = 0f; // another scene: look again soon
                if (Calm.Count > 0) KeepLimbs();
                if (Arenas.Count == 0) Released.Clear();
                if (_guestRequired && Time.unscaledTime >= _scanAt) { _scanAt = Time.unscaledTime + ScanEvery; Scan(); }
                if (Arenas.Count == 0) return;
                var manager = GlobalManager.global;
                var player = manager == null ? null : manager.controlledChar;
                foreach (var arena in Arenas)
                {
                    var trap = arena.Trap;
                    if (trap.trapActivated) { arena.Pending = false; continue; }
                    if (player != null) arena.Move(arena.Host, player.transform.position);
                    if (!_guestRequired || guestDead) arena.Guest.Reset();
                    else if (guest != null) arena.Move(arena.Guest, guest.Value);
                    if (LanArenaZone.ShouldClose(arena.Pending, trap.trapActivated, arena.Host.Inside, _guestRequired, arena.Guest.Inside)) { Close(arena, player); continue; }
                    if (arena.Pending && _guestRequired && arena.Host.Inside && Time.unscaledTime >= arena.HintAt)
                    {
                        arena.HintAt = Time.unscaledTime + HintEvery;
                        _hint?.Invoke("Арена босса: ворота закроются и бой начнётся, когда друг тоже зайдёт внутрь");
                    }
                }
            }
            catch (Exception ex) { Fail("Boss arena tick failed: ", ex); }
        }

        private static void Scan()
        {
            foreach (var trap in Object.FindObjectsOfType<TrapDoorCloser>())
                if (trap != null && !trap.trapActivated && trap.doorController != null && Find(trap) == null) Add(trap);
        }

        private static Arena Find(TrapDoorCloser trap)
        {
            foreach (var arena in Arenas) if (arena.Trap == trap) return arena;
            return null;
        }

        // The arena frame: origin at the gates, z toward the trigger behind them; the zone is the
        // trigger itself (its trigger colliders) with a margin.
        private static Arena Add(TrapDoorCloser trap)
        {
            var corners = new List<Vector3>();
            foreach (var collider in trap.GetComponents<Collider>())
            {
                if (collider == null || !collider.isTrigger) continue;
                var box = collider as BoxCollider;
                for (int i = 0; i < 8; i++)
                {
                    var sign = new Vector3((i & 1) == 0 ? -.5f : .5f, (i & 2) == 0 ? -.5f : .5f, (i & 4) == 0 ? -.5f : .5f);
                    if (box != null) corners.Add(box.transform.TransformPoint(box.center + Vector3.Scale(box.size, sign)));
                    else { var b = collider.bounds; corners.Add(b.center + Vector3.Scale(b.size, sign)); }
                }
            }
            if (corners.Count == 0) return null;
            var center = Vector3.zero;
            foreach (var p in corners) center += p;
            center /= corners.Count;
            var origin = trap.doorController.transform.position;
            var inward = center - origin; inward.y = 0f;
            if (inward.sqrMagnitude < .09f) { inward = trap.transform.forward; inward.y = 0f; }
            if (inward.sqrMagnitude < 1e-4f) return null;
            inward.Normalize();
            var arena = new Arena { Trap = trap, Origin = origin, Inward = inward, Across = Vector3.Cross(Vector3.up, inward) };
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var p in corners)
            {
                float x, y, z; arena.Local(p, out x, out y, out z);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            if (maxX - minX > MaxZone || maxY - minY > MaxZone || maxZ - minZ > MaxZone || maxZ <= 0f) return null;
            arena.Zone = new LanArenaZone(minX - Margin, maxX + Margin, minY - Height, maxY + Height,
                Math.Max(minZ - Margin, -LanArenaZone.OutsideBy / 2f), maxZ + Margin);
            Arenas.Add(arena);
            _info?.Invoke("Boss arena found: " + trap.name + " (gates wait for both players)");
            return arena;
        }

        // The native trap fires for the host's character: everyone is inside now.
        private static void Close(Arena arena, ZombieFighterController player)
        {
            arena.Pending = false;
            var trap = arena.Trap;
            var collider = player == null ? null : player.GetComponent<CharacterController>();
            if (_trigger != null && collider != null)
            {
                _releasing = true;
                try { _trigger.Invoke(trap, new object[] { collider }); }
                finally { _releasing = false; }
            }
            else
            {
                var door = trap.doorController;
                door.notInteractive = false;
                door.Switch();
                if (trap.locked) door.locked = true;
                trap.trapActivated = true;
                if (trap.soundSource != null) trap.soundSource.SetActive(true);
            }
            _info?.Invoke("Boss arena closed: both players are inside");
        }

        private static bool TriggerPrefix(TrapDoorCloser __instance, Collider col)
        {
            if (_releasing) return true;
            try
            {
                if (LanSaveIsolation.Active) return false; // guest: the host's gates decide
                if (!_guestRequired || __instance == null || __instance.trapActivated || __instance.doorController == null ||
                    col == null || col.GetComponent<CharacterController>() == null) return true;
                var arena = Find(__instance) ?? Add(__instance);
                if (arena == null) return true;
                var manager = GlobalManager.global;
                var player = manager == null ? null : manager.controlledChar;
                if (player != null && col.GetComponentInParent<ZombieFighterController>() == player) arena.Enter(arena.Host, player.transform.position);
                if (arena.Host.Inside && arena.Guest.Inside) { arena.Pending = false; return true; }
                arena.Pending = true;
                return false;
            }
            catch (Exception ex) { Fail("Boss arena trigger failed: ", ex); return true; }
        }

        // Guest: the gates are the host's; keep them unusable while held open, as on the host.
        private static bool UpdatePrefix(TrapDoorCloser __instance)
        {
            if (!LanSaveIsolation.Active) return true;
            var door = __instance == null ? null : __instance.doorController;
            if (door != null && !__instance.trapActivated) { door.notInteractive = door.Open; door.locked = __instance.locked && !door.Open; }
            return false;
        }

        private static bool SightPrefix(ZombieAIController __instance)
        {
            if (!Holds(__instance)) return true;
            __instance.playerIsInSight = false; __instance.aiHeardPlayer = false; __instance.iRememberlastPositionPlayer = false;
            return false;
        }

        private static bool Active { get { return _guestRequired && Arenas.Count != 0; } }
        // 1.4.5: the guest plays in this host's world (LanBossReward).
        internal static bool GuestPlays { get { return _guestRequired; } }

        private static ZombieAIController Ai(Component part)
        {
            if (part == null) return null;
            var ai = part.GetComponent<ZombieAIController>();
            return ai != null ? ai : part.GetComponentInParent<ZombieAIController>();
        }

        // Damage (not healing) to a held boss is dropped.
        private static bool DamagePrefix(ZombieHP __instance, float damage)
        {
            if (!Active || damage <= 0f || __instance == null || !Holds(__instance.zombieAIController != null ? __instance.zombieAIController : Ai(__instance))) return true;
            ImmuneHint();
            return false;
        }

        // Hit reactions, fire and death.
        private static bool DetectorPrefix(ZombieDamagDetector __instance)
        { return !Active || __instance == null || !Holds(__instance.zombieAIController != null ? __instance.zombieAIController : Ai(__instance)); }

        // Cut-off head, hands and legs.
        private static bool PartPrefix(ZombieAppearanceDismemberment __instance) { return !Active || !Holds(Ai(__instance)); }

        private static bool AggroPrefix(ZombieAIController __instance) { return !Active || !Holds(__instance); }

        private static bool TargetPrefix(ZombieAIController __instance, Transform value) { return value == null || !Active || !Holds(__instance); }

        // Spikes and other defences, and a moving train: they write the health directly.
        private static bool ContactPrefix(Component __instance) { return !Active || !Holds(Ai(__instance)); }

        private static void ImmuneHint()
        {
            if (Time.unscaledTime < _immuneHintAt) return;
            _immuneHintAt = Time.unscaledTime + ImmuneHintEvery;
            _hint?.Invoke("Босс неуязвим, пока вы оба не войдёте на его арену");
        }

        // Every frame: held bosses get their limbs' health back; a boss no longer held keeps the
        // values from before its hold and leaves the list.
        private static void KeepLimbs()
        {
            List<int> done = null;
            foreach (var pair in Calm)
            {
                var limbs = pair.Value;
                if (limbs.Ai != null)
                    for (int i = 0; i < limbs.Parts.Length; i++)
                        if (limbs.Parts[i] != null) limbs.Parts[i].limbHP = limbs.Health[i];
                if (limbs.Ai == null || !Holds(limbs.Ai)) (done ?? (done = new List<int>())).Add(pair.Key);
            }
            if (done != null) foreach (int id in done) Calm.Remove(id);
        }

        private static void RememberLimbs(ZombieAIController ai, int id)
        {
            if (Calm.ContainsKey(id)) return;
            var parts = new List<LimbDetector>();
            foreach (var part in ai.GetComponentsInChildren<LimbDetector>(true))
                if (part != null && (part.zombieHP == null || part.zombieHP == ai.zombieHP)) parts.Add(part);
            var limbs = new Limbs { Ai = ai, Parts = parts.ToArray(), Health = new float[parts.Count] };
            for (int i = 0; i < parts.Count; i++) limbs.Health[i] = parts[i].limbHP;
            Calm.Add(id, limbs);
        }

        // Host: a boss next to arena gates still open while the guest is not inside yet stays calm
        // and unharmed. A held boss never sees anyone, has no target and loses no health, so a boss
        // that does (already fighting when the guest arrived or came back) keeps fighting.
        internal static bool Holds(ZombieAIController ai)
        {
            try
            {
                if (!_guestRequired || Arenas.Count == 0 || ai == null || ai.dead || ai.ID != BossId && ai.skillEatHead == null) return false;
                int id = ai.GetInstanceID();
                if (Released.Contains(id)) return false;
                var at = ai.transform.position;
                bool near = false;
                foreach (var arena in Arenas)
                    if (arena.Trap != null && !arena.Trap.trapActivated && (arena.Origin - at).sqrMagnitude < BossReach * BossReach) { near = true; break; }
                if (!near) return false;
                var hp = ai.zombieHP;
                if (ai.aiInjuredByPlayer || hp != null && hp.currentHealth < hp.maxHealth - .01f || ai.playerIsInSight || ai.target != null)
                {
                    Released.Add(id);
                    return false;
                }
                RememberLimbs(ai, id);
                return true;
            }
            catch (Exception ex) { Fail("Boss arena hold failed: ", ex); return false; }
        }

        private static void Fail(string what, Exception ex)
        {
            if (_failLogged) return;
            _failLogged = true;
            _warn?.Invoke(what + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
