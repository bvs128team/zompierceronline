using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // The guest's grenades, dynamite and C4 (1.4.0). The game throws an explosive in Gun.OnThrow: one
    // leaves the backpack and a thrown copy flies off and blows up by itself (CountHitsAndExplosion).
    // In the host's world the guest's throw becomes a request: the item goes into escrow, the host
    // takes it off the guest's ledger and throws the same explosive from the guest's hand with the
    // speed of its own copy of the weapon. It blows up among the host's zombies, barrels and doors and
    // hurts whoever stands close; the guest sees and feels that blast as any other explosion of the
    // host (LanExplosions). The guest's own thrown copy only flies: it never blows up, and goes when
    // the host's blast comes (or the host refuses, or after a while). Throwables that are not
    // explosives stay as the game has them. Main thread only.
    internal static class LanThrows
    {
        private const float Match = 10f, Linger = 8f, HandReach = 3.5f;
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Guest: throws (escrowed item, car, at, camera turn, type, answer); false when it could not be
        // sent. Null outside a usable session: the game throws as ever.
        internal static Func<object[], int, float[], float[], int, Action<byte>, Action<object[]>, bool> GuestThrow;
        // Host: the guest's avatar (its colliders never stop the guest's own throw), and whether the
        // guest is up (1.4.1: no throws while it lies downed or dead).
        internal static Func<Transform> GuestAvatar;
        internal static Func<bool> GuestUp;

        // Guest: thrown copies waiting for the host's blast.
        private sealed class Shown { internal CountHitsAndExplosion Explosive; internal string Name; internal float Until; }
        private static readonly List<Shown> _shown = new List<Shown>();

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.throws");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Gun), "OnThrow"), prefix: new HarmonyMethod(typeof(LanThrows), nameof(ThrowPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(CountHitsAndExplosion), "Update"), prefix: new HarmonyMethod(typeof(LanThrows), nameof(FusePrefix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Throw hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; GuestThrow = null; GuestAvatar = null; GuestUp = null; Clear(); }

        // An explosive thrown by a Gun: its prefab blows up by itself and the guest knows it by name.
        private static bool Explosive(Rigidbody prefab)
        {
            return prefab != null && prefab.GetComponent<CountHitsAndExplosion>() != null && LanProtocol.ValidExplosive(prefab.name);
        }

        // ---- Guest ----

        // The game's OnThrow, except that the throw that counts happens at the host.
        private static bool ThrowPrefix(Gun __instance)
        {
            var request = GuestThrow;
            if (request == null || !LanSaveIsolation.Active || __instance == null || !Explosive(__instance.thrownObjectPrefab)) return true;
            try
            {
                var manager = GlobalManager.global;
                var player = manager == null ? null : manager.controlledChar;
                var camera = Camera.main;
                var hand = __instance.GrenadeInHand;
                var item = __instance.GetAmmoItem();
                if (player == null || camera == null || hand == null || item == null || item.inInventory == null) return true;
                var escrow = LanStorage.Normalize(new object[] { item.itemID, 1, item.Save() });
                LanGuestCharacter.ValidateItems(new object[] { escrow }, 0);
                // Where the hand lets go and how the camera looks, in the space of the car under the player.
                var at = hand.transform.position;
                var turn = camera.transform.rotation;
                var car = player.OnTrainCar;
                int index = TrainLayout.CarIndex(manager.controlledTrain, car);
                if (index >= 0 && index < LanProtocol.MaxCars)
                {
                    var local = car.transform.InverseTransformPoint(at);
                    if (Mathf.Abs(local.x) < LanStorage.MaxCarLocal && Mathf.Abs(local.y) < LanStorage.MaxCarLocal && Mathf.Abs(local.z) < LanStorage.MaxCarLocal)
                    { at = local; turn = Quaternion.Inverse(car.transform.rotation) * turn; }
                    else index = -1;
                }
                else index = -1;
                turn = Quaternion.Normalize(turn);
                int type = __instance.ThrowingType == 0 ? LanStorage.ThrowStandard : LanStorage.ThrowAlternative;
                // As the game: the explosive leaves its inventory (here into escrow), the hand, and flies.
                var source = item.inInventory;
                source.Extract(item, 1);
                Shown shown = null;
                Action<object[]> giveBack = back => GiveBack(source, back);
                if (!request(escrow, index, new[] { at.x, at.y, at.z }, new[] { turn.x, turn.y, turn.z, turn.w }, type, status => Answered(shown, status), giveBack))
                {
                    try { giveBack(escrow); }
                    catch (Exception ex) { _log?.Invoke("Throw not given back: " + ex.Message); }
                    LanStorageGame.Notice("Хост ещё отвечает на прошлые запросы: бросок не удался");
                    return false; // nothing leaves the hand; OnThrowEnd puts it back
                }
                if (__instance.ExplosiveEffects != null) __instance.ExplosiveEffects.SetActive(false);
                hand.SetActive(false);
                __instance.AudioPlay(__instance.SoundOnThrow, 1f);
                var body = Object.Instantiate(__instance.thrownObjectPrefab, hand.transform.position, hand.transform.rotation);
                body.AddForce(camera.transform.TransformDirection(type == LanStorage.ThrowStandard ? __instance.ThrowVelocityStandard : __instance.ThrowVelocityAlternative), ForceMode.VelocityChange);
                var explosive = body.GetComponent<CountHitsAndExplosion>();
                shown = new Shown { Explosive = explosive, Name = __instance.thrownObjectPrefab.name, Until = Time.unscaledTime + Mathf.Max(explosive.explotionTimeRandom2, 1f) + Linger };
                _shown.Add(shown);
            }
            catch (Exception ex) { _log?.Invoke("Throw hook failed: " + ex.GetType().Name + ": " + ex.Message); }
            return false;
        }

        // 1.4.1: a throw that did not happen goes back to the inventory it was taken from (the belt),
        // or else into the backpack.
        private static void GiveBack(Inventory source, object[] escrow)
        {
            if (source != null)
            {
                var instance = LanStorageGame.Create(escrow);
                bool stored = false;
                try { stored = source.Add(instance, true); }
                finally { if (!stored && instance != null) Object.Destroy(instance.gameObject); }
                if (stored) return;
            }
            new LanStorageGuestWorld().Give(escrow);
        }

        // The host's answer: a refused throw comes back (GiveBack, through LanStorageClient) and its copy goes.
        private static void Answered(Shown shown, byte status)
        {
            if (status == LanStorage.Ok || shown == null) return;
            Remove(shown); _shown.Remove(shown);
            LanStorageGame.Notice(status == LanStorage.Busy ? "Хост не успел: бросок вернулся в рюкзак" : "Хост не принял бросок: " + LanStorage.Describe(status));
        }

        // A thrown copy of the guest never blows up by itself (its fuse is held back).
        private static void FusePrefix(CountHitsAndExplosion __instance)
        {
            if (_shown.Count == 0) return;
            foreach (var shown in _shown) if (shown.Explosive == __instance) { __instance.explotionTimer = -100000f; return; }
        }

        // The host's blast of `name` at `at`: the guest's nearest waiting copy of it goes.
        internal static void Exploded(string name, Vector3 at)
        {
            Shown best = null; float distance = Match * Match;
            foreach (var shown in _shown)
            {
                if (shown.Explosive == null || shown.Name != name) continue;
                float d = (shown.Explosive.transform.position - at).sqrMagnitude;
                if (d <= distance) { distance = d; best = shown; }
            }
            if (best != null) { Remove(best); _shown.Remove(best); }
        }

        internal static void Tick()
        {
            if (_shown.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = _shown.Count - 1; i >= 0; i--)
                if (_shown[i].Explosive == null || now >= _shown[i].Until) { Remove(_shown[i]); _shown.RemoveAt(i); }
        }
        private static void Remove(Shown shown) { if (shown.Explosive != null) Object.Destroy(shown.Explosive.gameObject); }
        internal static void Clear() { foreach (var shown in _shown) Remove(shown); _shown.Clear(); }

        // ---- Host ----

        private sealed class Thrown { internal float At; internal Vector3 From, Guest; }
        private static readonly Dictionary<CountHitsAndExplosion, Thrown> _thrown = new Dictionary<CountHitsAndExplosion, Thrown>();
        // 1.4.19: one of the guest's throws went off here (LanExplosions, host).
        internal static void Blasted(CountHitsAndExplosion explosive)
        {
            Thrown thrown;
            if (explosive == null || !_thrown.TryGetValue(explosive, out thrown)) return;
            _thrown.Remove(explosive);
            var at = explosive.transform.position;
            _log?.Invoke("Guest's throw went off at " + at.ToString("F1") + ", " + Vector3.Distance(at, thrown.Guest).ToString("0.0") + " m from where the guest stood, " +
                Vector3.Distance(at, thrown.From).ToString("0.0") + " m from the hand, after " + (Time.time - thrown.At).ToString("0.0") + " s");
        }

        // The guest's throw of explosive item `item` as the game throws it, from the guest's hand.
        internal static byte Host(int item, int carIndex, float[] at, float[] turn, int type, Vector3? guest)
        {
            var manager = GlobalManager.global;
            var player = manager == null ? null : manager.controlledChar;
            var scenes = GlobalSceneManager.global;
            if (player == null || scenes == null || scenes.SceneCurrentlyLoading || player.zombieFighterFireArmWeapon == null || player.zombieFighterFireArmWeapon.gunList == null) return LanStorage.Unavailable;
            if (guest == null) return LanStorage.Lost;
            if (GuestUp != null && !GuestUp()) return LanStorage.NotAllowed;
            Gun gun = null;
            foreach (var candidate in player.zombieFighterFireArmWeapon.gunList)
                if (candidate != null && candidate.ammoID == item && Explosive(candidate.thrownObjectPrefab)) { gun = candidate; break; }
            if (gun == null) return LanStorage.NotAllowed;
            var position = new Vector3(at[0], at[1], at[2]);
            var rotation = Quaternion.Normalize(new Quaternion(turn[0], turn[1], turn[2], turn[3]));
            if (carIndex >= 0)
            {
                Zompiercer.Train.TrainCar car;
                try { car = TrainLayout.GetCar(manager.controlledTrain, carIndex); }
                catch (Exception) { return LanStorage.NotFound; }
                position = car.transform.TransformPoint(position);
                rotation = car.transform.rotation * rotation;
            }
            if (Vector3.Distance(position, guest.Value) > HandReach) return LanStorage.TooFar;
            var body = Object.Instantiate(gun.thrownObjectPrefab, position, rotation);
            var avatar = GuestAvatar?.Invoke();
            if (avatar != null)
                foreach (var mine in body.GetComponentsInChildren<Collider>(true))
                    foreach (var theirs in avatar.GetComponentsInChildren<Collider>(true))
                        if (mine != null && theirs != null) Physics.IgnoreCollision(mine, theirs, true);
            var velocity = rotation * (type == LanStorage.ThrowStandard ? gun.ThrowVelocityStandard : gun.ThrowVelocityAlternative);
            body.AddForce(velocity, ForceMode.VelocityChange);
            if (gun.SoundOnThrow != null) AudioSource.PlayClipAtPoint(gun.SoundOnThrow, position);
            // 1.4.19: where the guest's throw starts and (Blasted) where it goes off, for the log ("it blows up in me").
            var explosive = body.GetComponent<CountHitsAndExplosion>();
            if (explosive != null)
            {
                foreach (var gone in new List<CountHitsAndExplosion>(_thrown.Keys)) if (gone == null) _thrown.Remove(gone);
                if (_thrown.Count < 32) _thrown[explosive] = new Thrown { At = Time.time, From = position, Guest = guest.Value };
            }
            _log?.Invoke("Guest threw " + gun.thrownObjectPrefab.name + " from " + position.ToString("F1") + " (guest at " + guest.Value.ToString("F1") +
                (carIndex >= 0 ? ", car " + carIndex : "") + ", " + (type == LanStorage.ThrowStandard ? "standard" : "alternative") + "), velocity " + velocity.ToString("F1"));
            return LanStorage.Ok;
        }
    }
}
