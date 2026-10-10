using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Explosions of the host's world for the guest (1.2.2). A barrel that catches fire burns for a
    // while and blows up; a grenade, dynamite or C4 the host threw blows up. The host tells the guest
    // which prefab and where (Explosion packet); the guest shows the prefab's own fire, smoke, blast,
    // scorch, burning ground and debris, and is hurt by the blast as the game hurts its player
    // (CountHitsAndExplosion.DestroyAndPushByAddForce: the explosion's damage over the distance, when
    // nothing stands between). The host's zombies, doors and objects are the host's game. Main thread only.
    internal static class LanExplosions
    {
        private const float LoopLifetime = 30f, LoopMatch = 1.5f;
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        internal static string Failure { get; private set; }
        // Host: sends an explosion of this world (null: no guest attached).
        internal static Action<byte, string, Vector3> Send;
        // Guest: the prefabs by name, and the fire and smoke it shows for a burning barrel.
        private static Dictionary<string, CountHitsAndExplosion> _prefabs;
        private sealed class Loop { internal Vector3 At; internal GameObject Fire, Smoke; internal float Until; }
        private static readonly List<Loop> _loops = new List<Loop>();

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.explosions");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(CountHitsAndExplosion), "Update"), prefix: new HarmonyMethod(typeof(LanExplosions), nameof(UpdatePrefix)), postfix: new HarmonyMethod(typeof(LanExplosions), nameof(UpdatePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(CountHitsAndExplosion), "DestroyAndPushByAddForce"), prefix: new HarmonyMethod(typeof(LanExplosions), nameof(BlastPrefix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Explosion hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; Send = null; Clear(); }

        private static void Once(string text) { if (_logged) return; _logged = true; _log?.Invoke(text); }

        // A prefab's name as the guest knows it: the instance's name without "(Clone)".
        private static string NameOf(CountHitsAndExplosion e)
        {
            string name = e.gameObject.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) name = name.Substring(0, clone);
            name = name.Trim();
            return LanProtocol.ValidExplosive(name) ? name : null;
        }

        // ---- Host ----

        private static void UpdatePrefix(CountHitsAndExplosion __instance, out bool __state) { __state = __instance.fireLoopBarrelSpawned != null; }
        private static void UpdatePostfix(CountHitsAndExplosion __instance, bool __state)
        {
            if (__state || __instance == null || __instance.fireLoopBarrelSpawned == null) return;
            var send = Send; string name = NameOf(__instance);
            if (send != null && name != null) send(LanProtocol.ExplosionFire, name, __instance.transform.position);
        }
        private static void BlastPrefix(CountHitsAndExplosion __instance)
        {
            LanThrows.Blasted(__instance);
            var send = Send; string name = __instance == null ? null : NameOf(__instance);
            if (send != null && name != null) send(LanProtocol.ExplosionBlast, name, __instance.transform.position);
        }

        // ---- Guest ----

        private static CountHitsAndExplosion Prefab(string name)
        {
            if (_prefabs == null)
            {
                _prefabs = new Dictionary<string, CountHitsAndExplosion>();
                foreach (var e in Resources.FindObjectsOfTypeAll<CountHitsAndExplosion>())
                    if (e != null && !e.gameObject.scene.IsValid() && !_prefabs.ContainsKey(e.gameObject.name)) _prefabs.Add(e.gameObject.name, e);
            }
            CountHitsAndExplosion prefab;
            if (_prefabs.TryGetValue(name, out prefab) && prefab != null) return prefab;
            _prefabs = null; // assets loaded later: look again next time
            return null;
        }

        internal static void Receive(byte action, string name, Vector3 at)
        {
            try
            {
                var prefab = Prefab(name);
                if (prefab == null) { Once("Explosion prefab not found here: " + name); return; }
                if (action == LanProtocol.ExplosionFire) Fire(prefab, at);
                else { Blast(prefab, at); LanThrows.Exploded(name, at); } // 1.4.0: the guest's own throw has gone off
            }
            catch (Exception ex) { Once("Explosion not shown: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Fire(CountHitsAndExplosion prefab, Vector3 at)
        {
            var loop = new Loop { At = at, Until = Time.unscaledTime + LoopLifetime };
            if (prefab.fireLoopBarrel != null) loop.Fire = Object.Instantiate(prefab.fireLoopBarrel, at, Quaternion.identity);
            if (prefab.smokeLoopBarrel != null) loop.Smoke = Object.Instantiate(prefab.smokeLoopBarrel, at, Quaternion.Euler(-90f, 0f, 0f));
            _loops.Add(loop);
        }

        // As CountHitsAndExplosion.Update and DestroyAndPushByAddForce, for this player only.
        private static void Blast(CountHitsAndExplosion prefab, Vector3 at)
        {
            // The barrel's own fire gives way to the blast.
            for (int i = _loops.Count - 1; i >= 0; i--)
                if ((_loops[i].At - at).sqrMagnitude <= LoopMatch * LoopMatch) { Remove(_loops[i]); _loops.RemoveAt(i); }
            RaycastHit ground;
            bool grounded = Physics.Raycast(new Ray(at, Vector3.down), out ground, 100f, prefab.groundLayer.value);
            if (prefab.explosionObjectType == 0)
            {
                if (grounded)
                {
                    var point = ground.point + ground.normal * .1f;
                    if (prefab.explosionEffect != null) Object.Instantiate(prefab.explosionEffect, point, Quaternion.identity);
                    if (prefab.explosionBurn != null) Object.Instantiate(prefab.explosionBurn, point, Quaternion.identity).parent = ground.transform;
                    if (prefab.burningZoneLoop != null) Object.Instantiate(prefab.burningZoneLoop, ground.point, Quaternion.identity);
                }
                if (prefab.piecesOfObject != null) Object.Instantiate(prefab.piecesOfObject, at, Quaternion.Euler(-90f, 0f, 0f));
            }
            else
            {
                if (prefab.explosionEffect != null) Object.Instantiate(prefab.explosionEffect, at, Quaternion.identity);
                if (grounded && prefab.explosionBurn != null) Object.Instantiate(prefab.explosionBurn, ground.point + ground.normal * .1f, Quaternion.identity).parent = ground.transform;
            }
            Hurt(prefab, at);
            ExplosionForce.Apply(at + new Vector3(0f, -3f, 0f), prefab.explotionRadius, prefab.power);
        }

        private static void Hurt(CountHitsAndExplosion prefab, Vector3 at)
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var bar = player == null ? null : player.zombieFighterIndicatorsBar;
            if (bar == null || prefab.explotionDamage <= 0f) return;
            var origin = at + prefab.RaycasеOriginOffset;
            foreach (var collider in Physics.OverlapSphere(at, prefab.explotionRadius))
            {
                if (collider == null || collider.GetComponentInParent<ZombieFighterIndicatorsBar>() != bar) continue;
                RaycastHit hit;
                if (!Physics.Raycast(origin, collider.bounds.center - origin, out hit, float.PositiveInfinity, GlobalManager.global.explosionLayerMask.value) || hit.collider != collider) continue;
                float distance = Math.Max(.1f, Vector3.Distance(at, bar.transform.position));
                bar.CauseDamage(prefab.explotionDamage / distance, true, false, (DamageType)1);
                return; // once, as the game damages its player once per collider it finds first
            }
        }

        internal static void Tick()
        {
            if (_loops.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = _loops.Count - 1; i >= 0; i--) if (now >= _loops[i].Until) { Remove(_loops[i]); _loops.RemoveAt(i); }
        }
        private static void Remove(Loop loop)
        {
            if (loop.Fire != null) Object.Destroy(loop.Fire);
            if (loop.Smoke != null) Object.Destroy(loop.Smoke);
        }
        internal static void Clear() { foreach (var loop in _loops) Remove(loop); _loops.Clear(); }
    }
}
