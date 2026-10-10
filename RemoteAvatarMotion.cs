using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZompiercerLAN
{
    // Third-person animation of the other player (1.0.9). The avatar is a game NPC model (man:
    // RigSkuf* bones, woman: RigW*) whose own controller has idle and walk only. With a firearm,
    // an invisible military NPC (RigSlim*, the same 65-bone rig with the same bone axes) plays
    // the game's "NPC with AK" controller (armed idle, walk and aim); after it animates, every
    // bone's local rotation is copied by name onto the avatar, so the avatar holds the weapon
    // in both hands as the game's armed NPCs do. Display only: no gameplay scripts.
    // 1.1.0: crouching, looking up or down and melee swings, for which the game's NPCs have no
    // clips, are bone turns about the avatar's own right axis over whichever pose the animators made.
    // 1.1.9: a reload (the rifle dips, the left hand works the magazine) and lying (downed or dead:
    // the plugin lays the whole avatar on its back; here only the armed pose gives way to idle).
    // 1.4.6: the controllers' states are entered directly (the armed idle let the aim in only near
    // the end of its 3.8 s loop). Armed, the legs are the avatar's own (idle or walk, so aiming on
    // the move walks) and the upper body is the military NPC's aim, the weapon lowered forward
    // about the shoulder unless aiming (its armed idle carried the rifle across the hips, stock out);
    // both hands are placed where that NPC has them relative to its chest (the models' arms and
    // shoulders differ in length, so copied turns alone left the supporting hand off the weapon);
    // the dash is a roll.
    // 1.4.7: running (a longer stride, a lean), jumping and falling (legs drawn up), swimming (lying
    // forward, kicking, the breaststroke), a climb (the game moves the player to the top of a ladder
    // at once; here it climbs there hand over hand), a throw (held back, then let go), eating,
    // drinking and bandaging (hands to the mouth or round the forearm) and a flinch when hit.
    // 1.4.8: strikes by weapon (an overhead blow with a hammer, a chop with an axe or a shovel, a
    // sideways swing with a bat or a wrench, a stab with a knife, jabs with bare fists, hand after
    // hand) and the use key: a reach where it looks (the left hand with a firearm), or bending down
    // for an item when it looks down.
    internal sealed class RemoteAvatarMotion : MonoBehaviour
    {
        // The pace (m/s) at which the NPC walk cycles keep up with the ground (an estimate):
        // a faster partner walks the same clip faster, within PaceMin..PaceMax.
        internal const float WalkClipSpeed = 1.4f, PaceMin = .8f, PaceMax = 2.2f;
        // Crouch: knee bend and forward lean in degrees; how fast pose changes follow (per second).
        internal const float CrouchKnee = 60f, CrouchLean = 15f, Follow = 12f, MaxBodyPitch = 70f;
        // A swing: the arm rises forward over the head, strikes down past the body, comes back.
        internal const float SwingRise = .22f, SwingStrike = .12f, SwingReturn = .3f, SwingHigh = -130f, SwingLow = 15f;
        // A shot (1.1.1): the chest and arms kick back and settle.
        internal const float RecoilKick = 7f, RecoilSettle = .07f;
        // A reload (1.1.9): how far the chest bends and the rifle dips, the left arm's reach and pace.
        internal const float ReloadLean = 10f, ReloadDip = 18f, ReloadReach = -25f, ReloadWork = -20f, ReloadPace = 6f;
        // 1.4.6: a state change blends this long (s); the clips' rifle is this long (m), a shorter
        // firearm brings the supporting hand back towards the grip (at least this share of the way).
        internal const float Blend = .15f, RifleLength = .85f, MinSupport = .2f;
        // 1.4.6: how far (degrees) the weapon is lowered about the right shoulder when not aiming,
        // and running (m/s and faster).
        internal const float LowReady = 35f, RunLowReady = 50f, RunSpeed = 3.2f;
        // 1.4.6: a roll (the dash): its length (s), knee bend, back curl and head tuck at its middle
        // (degrees), how far the pelvis sinks (share of its height); the share of it spent turning
        // towards the dash at either end.
        internal const float RollTime = .6f, RollKnee = 95f, RollCurl = 60f, RollHead = 40f, RollSink = .35f, RollFace = .15f;
        // 1.4.7: running: the stride's growth and the lean (degrees); in the air: how far the knees come up;
        // swimming: the body's tilt, kick and stroke (degrees) and their pace; a climb (s); a throw:
        // the arm held back (degrees), how it comes over and settles (s); a flinch (s); using: the bites'
        // and the bandage's pace (per second).
        internal const float RunStride = .35f, RunLean = 12f, AirKnee = 45f, SwimTilt = 75f, SwimKick = 22f, SwimStroke = 45f, SwimPace = 3.5f;
        internal const float ClimbTime = .7f, ThrowBack = -150f, ThrowRise = .15f, ThrowStrike = .12f, ThrowReturn = .33f, ThrowFollow = 30f;
        internal const float FlinchTime = .45f, BitePace = 9f, WrapPace = 8f;
        // 1.4.8: a chop (rise, strike, return in s; arm high and low, chest turn in degrees); a sideways
        // swing (wind-up, strike, return; how far back and across, the arm raised); a stab (pull, thrust,
        // hold, return); a jab (thrust, hold, return); a reach or bending down (rise, hold, return; below
        // this pitch it bends; back curl and knee bend in degrees).
        internal const float ChopRise = .26f, ChopStrike = .13f, ChopReturn = .35f, ChopHigh = -165f, ChopLow = 35f, ChopTwist = 20f;
        internal const float SideWind = .22f, SideStrike = .14f, SideReturn = .32f, SideBack = 75f, SideAcross = -85f, SideRaise = -85f;
        internal const float StabPull = .08f, StabThrust = .08f, StabHold = .05f, StabReturn = .22f;
        internal const float JabThrust = .08f, JabHold = .04f, JabReturn = .2f;
        internal const float ReachRise = .15f, ReachHold = .12f, ReachReturn = .25f, BendPitch = 35f, BendCurl = 45f, BendKnee = 40f;
        // 1.4.8: how a held melee weapon strikes, by item (ItemsDataBase ids): knives and the dagger stab,
        // bats and wrenches swing sideways, axes and shovels chop, the rest (hammers) strike overhead.
        internal enum Strike : byte { Overhead, Chop, Sideways, Stab }
        internal static Strike StrikeOf(int itemId)
        {
            switch (itemId)
            {
                case 56: case 187: case 188: return Strike.Stab;
                case 27: case 54: case 184: case 55: case 94: case 186: return Strike.Sideways;
                case 93: case 189: case 191: case 192: case 193: return Strike.Chop;
                default: return Strike.Overhead;
            }
        }
        internal static readonly int IdleState = Animator.StringToHash("NPCMarkStandIdle"), WalkState = Animator.StringToHash("NPCMarkWalk"),
            AimState = Animator.StringToHash("MRAKMAimingIdle");
        private const string Palm = "RArmPalm", Hub = "Hub001";
        private Driven _own, _driver;
        private GameObject _driverObject;
        private Transform[] _from = new Transform[0], _to = new Transform[0];
        private bool[] _upper = new bool[0];
        private int _upperRoot = -1;
        // 1.4.6: where the plugin's log goes (which way each animator is switched, once).
        internal static Action<string> Log;
        private Transform _fromHub, _toHub;
        private Vector3 _fromHubRest, _toHubRest;
        private float _hubScale = 1f;
        private bool _armed, _layered;

        // An animator of the avatar (its own, or the armed driver's) and what it was checked for.
        private sealed class Driven
        {
            internal Animator Animator;
            internal string Role;
            private bool _checked, _walk, _aim, _direct;
            // Known once the animator was active: whether it has the aim state and is switched directly.
            internal bool CanAim { get { Check(); return _aim && _direct; } }

            private void Check()
            {
                if (_checked || Animator == null || !Animator.isActiveAndEnabled) return;
                _checked = true; _walk = Has(Animator, "Walk"); _aim = Has(Animator, "Aim");
                try { _direct = Animator.HasState(0, IdleState) && Animator.HasState(0, WalkState) && (!_aim || Animator.HasState(0, AimState)); }
                catch (Exception) { _direct = false; }
                var controller = Animator.runtimeAnimatorController;
                Log?.Invoke("Partner " + Role + " animator " + (controller == null ? "?" : controller.name) +
                    (_direct ? ": states entered directly" : ": switched by its parameters") + (_aim ? ", with aim" : ""));
            }

            // Puts it into a state (IdleState, WalkState, AimState). The parameters follow, so the
            // controller's own transitions keep it there; a controller without these states is
            // switched by the parameters alone, as before 1.4.6.
            internal void Set(int state, float speed)
            {
                if (Animator == null || !Animator.isActiveAndEnabled) return;
                Check();
                if (state == AimState && !_aim) state = IdleState;
                if (_walk) Animator.SetBool("Walk", state == WalkState);
                if (_aim) Animator.SetBool("Aim", state == AimState);
                Animator.speed = speed;
                if (!_direct) return;
                if (Animator.IsInTransition(0) ? Animator.GetNextAnimatorStateInfo(0).shortNameHash == state
                    : Animator.GetCurrentAnimatorStateInfo(0).shortNameHash == state) return;
                Animator.CrossFadeInFixedTime(state, Blend, 0);
            }
        }

        // A bone turned by the procedural layer. Each frame starts from the animated pose; a bone
        // the animators did not write this frame (culled, or not in the clip) starts from its last
        // animated rotation instead of piling the turns up.
        private sealed class Turned
        {
            internal Transform Bone;
            internal Quaternion Base, Written, Rest;
            internal bool Seen;
        }
        private Turned _lThigh, _lShin, _lFoot, _rThigh, _rShin, _rFoot, _spine2, _spine3, _neck, _head, _arm, _lArm, _hub;
        private Turned[] _turned = new Turned[0];
        private Transform _pelvis;
        private Vector3 _pelvisBase, _pelvisWritten;
        private bool _pelvisSeen;
        private float _legLength, _crouch, _crouchTarget, _pitch, _pitchTarget, _swingAt = -100f, _shotAt = -100f, _reload, _reloadTarget;
        // 1.4.6: the arms (upper arm, forearm, palm) and chests of the avatar and the driver, the
        // ratio of their reaches, the pelvis height at rest; the held firearm's length; the roll.
        private Transform _rUpper, _rLower, _rPalm, _lUpper, _lLower, _lPalm, _chest, _driverChest, _driverRight, _driverLeft;
        private float _armScale = 1f, _hubHeight = .8f, _heldLength = RifleLength, _rollAt = -100f, _lower, _lowerTarget;
        private byte _rollDirection;
        private bool _armsReady, _handsReady, _walking;
        // 1.4.7: how much each motion shows (followed towards its target) and when the last events began.
        private float _run, _runTarget, _air, _airTarget, _swim, _swimTarget, _cock, _cockTarget, _use, _useTarget;
        private float _throwAt = -100f, _throwRise = ThrowRise, _flinchAt = -100f, _climbAt = -100f;
        private byte _useKind;
        // 1.4.8: the held melee weapon's strike, the last jab (and which hand), the last use key press
        // (and whether it bent down for it).
        internal Strike StrikeKind { get; set; }
        private float _jabAt = -100f, _reachAt = -100f;
        private bool _jabLeft = true, _reachDown;

        // The avatar's right palm bone (not the driver's), for the held weapon.
        internal Transform RightPalm { get; private set; }

        // A bone whose name ends with the suffix; prefix: the rest of its name (the rig's prefix).
        internal static Transform FindBone(Transform root, string suffix, out string prefix)
        {
            foreach (var bone in root.GetComponentsInChildren<Transform>(true))
                if (bone.name.EndsWith(suffix, StringComparison.Ordinal) && bone.name.StartsWith("Rig", StringComparison.Ordinal))
                { prefix = bone.name.Substring(0, bone.name.Length - suffix.Length); return bone; }
            prefix = null;
            return null;
        }

        // own: the avatar's animator (idle and walk). driver: a display copy of a military NPC,
        // or null; it is destroyed when it cannot drive this avatar. Called before the avatar
        // is first shown, so both rigs are still in their prefab poses.
        internal string Initialize(Animator own, GameObject driver, RuntimeAnimatorController armed)
        {
            _own = new Driven { Animator = own, Role = "own" };
            string prefix, driverPrefix;
            RightPalm = FindBone(transform, Palm, out prefix);
            var avatarBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (RightPalm != null)
                foreach (var bone in GetComponentsInChildren<Transform>(true))
                    if (bone.name.StartsWith(prefix, StringComparison.Ordinal)) avatarBones[bone.name.Substring(prefix.Length)] = bone;
            PrepareTurns(avatarBones);
            LogHead();
            if (driver == null) return "no military NPC model";
            if (armed == null || RightPalm == null || FindBone(driver.transform, Palm, out driverPrefix) == null)
            { Destroy(driver); return "no armed controller or no palm bone"; }
            var from = new List<Transform>(); var to = new List<Transform>();
            var driverBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var bone in driver.GetComponentsInChildren<Transform>(true))
            {
                Transform match;
                if (!bone.name.StartsWith(driverPrefix, StringComparison.Ordinal)) continue;
                string name = bone.name.Substring(driverPrefix.Length);
                driverBones[name] = bone;
                if (!avatarBones.TryGetValue(name, out match)) continue;
                // 1.4.14: nothing on the head (eyes, jaw, hair): the military NPC's rest turns of those differ from
                // this model's, and copying them rolled the eyes up and twisted the hair while a weapon was held.
                if (_head != null && match != _head.Bone && match.IsChildOf(_head.Bone)) continue;
                from.Add(bone); to.Add(match);
                if (bone.name.EndsWith(Hub, StringComparison.Ordinal)) { _fromHub = bone; _toHub = match; }
            }
            if (from.Count < 40) { Destroy(driver); return "the rigs differ (" + from.Count + " shared bones)"; }
            _from = from.ToArray(); _to = to.ToArray();
            // The upper body (the spine and all on it) for aiming on the move.
            Transform spine;
            _upper = new bool[_to.Length];
            if (avatarBones.TryGetValue("Spine1", out spine))
                for (int i = 0; i < _to.Length; i++) { _upper[i] = _to[i].IsChildOf(spine); if (_to[i] == spine) _upperRoot = i; }
            if (_fromHub != null)
            {
                // The pelvis moves with the clips; scaled by hip height for a shorter or taller model.
                _fromHubRest = _fromHub.localPosition; _toHubRest = _toHub.localPosition;
                _hubScale = Mathf.Abs(_fromHubRest.y) > .01f ? Mathf.Clamp(_toHubRest.y / _fromHubRest.y, .5f, 2f) : 1f;
            }
            PrepareHands(avatarBones, driverBones);
            foreach (var renderer in driver.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            var animator = driver.GetComponentInChildren<Animator>(true);
            if (animator == null) { Destroy(driver); _from = _to = new Transform[0]; _upper = new bool[0]; return "no animator on the military NPC"; }
            animator.runtimeAnimatorController = armed;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // its renderers are off
            animator.enabled = true;
            _driver = new Driven { Animator = animator, Role = "armed" };
            _driverObject = driver;
            driver.SetActive(false);
            driver.transform.SetParent(transform, false);
            driver.transform.localPosition = Vector3.zero; driver.transform.localRotation = Quaternion.identity;
            return null;
        }

        // 1.4.14: what hangs on the model's head, once, to the log (for its eyes and hair).
        private void LogHead()
        {
            if (_head == null || Log == null) return;
            var names = new List<string>();
            foreach (var bone in _head.Bone.GetComponentsInChildren<Transform>(true))
                if (bone != _head.Bone && names.Count < 40) names.Add(bone.name + (bone.childCount > 0 ? "(" + bone.childCount + ")" : ""));
            Log("Partner head bones: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())));
        }

        // The rig's legs ("LLeg1" thigh, "LLeg2" shin, "LLegAnkle" foot), spine, neck ("Spine1 1"),
        // head ("Hub003"), right upper arm and (1.4.6) pelvis; a missing bone only drops its part.
        private void PrepareTurns(Dictionary<string, Transform> bones)
        {
            Func<string, Turned> find = name => { Transform bone; return bones.TryGetValue(name, out bone) ? new Turned { Bone = bone, Rest = bone.localRotation } : null; };
            _lThigh = find("LLeg1"); _lShin = find("LLeg2"); _lFoot = find("LLegAnkle");
            _rThigh = find("RLeg1"); _rShin = find("RLeg2"); _rFoot = find("RLegAnkle");
            _spine2 = find("Spine2"); _spine3 = find("Spine3"); _neck = find("Spine1 1"); _head = find("Hub003");
            _arm = find("RArm1"); _lArm = find("LArm1"); _hub = find(Hub);
            Transform pelvis;
            if (bones.TryGetValue(Hub, out pelvis)) { _pelvis = pelvis; _hubHeight = Mathf.Clamp(pelvis.position.y - transform.position.y, .5f, 1.2f); }
            var list = new List<Turned>();
            foreach (var turned in new[] { _lThigh, _lShin, _lFoot, _rThigh, _rShin, _rFoot, _spine2, _spine3, _neck, _head, _arm, _lArm, _hub })
                if (turned != null) list.Add(turned);
            _turned = list.ToArray();
            if (_lThigh != null && _lShin != null && _lFoot != null)
                _legLength = Vector3.Distance(_lThigh.Bone.position, _lShin.Bone.position) + Vector3.Distance(_lShin.Bone.position, _lFoot.Bone.position);
            // Both arms ("RArm1" upper arm, "RArm21" forearm, "RArmPalm"; the twist bones "RArm22/23"
            // lie along the forearm) and the chest ("Hub002"), for the hands' reach.
            Func<string, Transform> get = name => { Transform found; return bones.TryGetValue(name, out found) ? found : null; };
            _rUpper = get("RArm1"); _rLower = get("RArm21"); _rPalm = get("RArmPalm");
            _lUpper = get("LArm1"); _lLower = get("LArm21"); _lPalm = get("LArmPalm");
            _chest = get("Hub002");
            _armsReady = _head != null && !new[] { _rUpper, _rLower, _rPalm, _lUpper, _lLower, _lPalm, _chest }.Any(b => b == null);
            // 1.4.8: the forearms and palms the hands' reach turns start each frame from their animated
            // pose too (a clip that does not write them must not keep a reach for good).
            var reached = new List<Turned>(_turned);
            foreach (var bone in new[] { _rLower, _rPalm, _lLower, _lPalm })
                if (bone != null) reached.Add(new Turned { Bone = bone, Rest = bone.localRotation });
            _turned = reached.ToArray();
        }

        // The military NPC's chest and palms ("Hub002", "RArmPalm", "LArmPalm") and how much longer
        // this avatar's reach from chest to palm is than that NPC's. Without one of them no hand is placed.
        private void PrepareHands(Dictionary<string, Transform> avatar, Dictionary<string, Transform> driver)
        {
            Func<string, Transform> bone = name => { Transform found; return driver.TryGetValue(name, out found) ? found : null; };
            _driverChest = bone("Hub002"); _driverRight = bone("RArmPalm"); _driverLeft = bone("LArmPalm");
            float own = ArmReach(avatar), theirs = ArmReach(driver);
            _armScale = own > .1f && theirs > .1f ? Mathf.Clamp(own / theirs, .7f, 1.4f) : 1f;
            _handsReady = _armsReady && _driverChest != null && _driverRight != null && _driverLeft != null;
        }

        // The rest lengths from the chest to the right palm (collarbone, upper arm, the forearm's parts).
        private static float ArmReach(Dictionary<string, Transform> bones)
        {
            float length = 0f;
            foreach (var name in new[] { "RArmCollarbone", "RArm1", "RArm21", "RArm22", "RArm23", "RArmPalm" })
            {
                Transform bone;
                if (!bones.TryGetValue(name, out bone)) return 0f;
                length += bone.localPosition.magnitude;
            }
            return length;
        }

        // Read once each animator is active (an inactive animator lists no parameters).
        private static bool Has(Animator animator, string parameter)
        {
            try { foreach (var p in animator.parameters) if (p.name == parameter) return true; }
            catch (Exception) { }
            return false;
        }

        // Every frame: armed (a firearm in hand) and its length (m), walking, the partner's pace
        // (m/s), aiming (down the sights, or a recent shot), crouching, the view's pitch (degrees, up
        // positive) and the times (Time.unscaledTime; long ago when none) of the last melee swing and
        // the last shot; 1.1.9: reloading, and lying (downed or dead: no armed pose, no walk, no crouch).
        internal void Apply(bool armed, float heldLength, bool walking, float speed, bool aiming, bool crouching, float pitch, float swingAt, float shotAt, bool reloading, bool lying)
        {
            if (lying) { armed = false; walking = false; crouching = false; reloading = false; aiming = false; pitch = 0f; _rollAt = -100f; }
            _reloadTarget = reloading ? 1f : 0f;
            _crouchTarget = crouching ? 1f : 0f;
            _pitchTarget = Mathf.Clamp(pitch, -LanProtocol.MaxPitch, LanProtocol.MaxPitch);
            _swingAt = swingAt; _shotAt = shotAt;
            _heldLength = heldLength > .05f ? heldLength : RifleLength;
            _walking = walking;
            if (_driverObject == null) armed = false;
            if (armed != _armed)
            {
                _armed = armed;
                _driverObject?.SetActive(armed);
            }
            float pace = walking ? Mathf.Clamp(speed / WalkClipSpeed, PaceMin, PaceMax) : 1f;
            // The avatar's own walk carries the legs; armed, the driver's aim is the upper body (the
            // weapon lowered unless aiming). A controller without a directly entered aim falls back
            // to the driver's whole armed idle, walk and aim, as before 1.4.6.
            _own?.Set(walking ? WalkState : IdleState, pace);
            if (!armed) { _layered = false; _lower = _lowerTarget = 0f; return; }
            _layered = _driver.CanAim && _own != null && _own.Animator != null && _own.Animator.isActiveAndEnabled;
            if (_layered) _driver.Set(AimState, 1f);
            else _driver.Set(aiming ? AimState : walking ? WalkState : IdleState, aiming ? 1f : pace);
            _lowerTarget = !_layered || aiming ? 0f : walking && (speed >= RunSpeed || _runTarget > 0f) ? RunLowReady : LowReady;
        }

        // 1.4.7: every frame before Apply: running, in the air, swimming, a throw held back, and what
        // it uses (LanProtocol.UseNone .. UseMedicine).
        internal void Moves(bool run, bool air, bool swim, bool cock, byte use)
        {
            _runTarget = run ? 1f : 0f; _airTarget = air ? 1f : 0f; _swimTarget = swim ? 1f : 0f; _cockTarget = cock ? 1f : 0f;
            if (use != LanProtocol.UseNone) _useKind = use;
            _useTarget = use != LanProtocol.UseNone ? 1f : 0f;
        }

        // 1.4.7: the partner let a throw go, was hit, climbed a ladder (shown climbing for ClimbTime).
        internal void Throw() { _throwRise = ThrowRise * (1f - _cock); _throwAt = Time.unscaledTime; }
        // 1.4.8: a jab with bare fists (hand after hand); a press of the use key.
        internal void Jab() { _jabLeft = !_jabLeft; _jabAt = Time.unscaledTime; }
        internal void Interact() { _reachDown = _pitchTarget < -BendPitch; _reachAt = Time.unscaledTime; }
        internal void Flinch() { _flinchAt = Time.unscaledTime; }
        internal void Climb() { _climbAt = Time.unscaledTime; }
        internal bool IsClimbing { get { float age = Time.unscaledTime - _climbAt; return age >= 0f && age < ClimbTime; } }
        internal float ClimbShare { get { return Mathf.Clamp01((Time.unscaledTime - _climbAt) / ClimbTime); } }

        // 1.4.6: the partner dashed (direction in eighths of a turn clockwise from its facing).
        internal void Dash(byte direction)
        {
            _rollAt = Time.unscaledTime;
            _rollDirection = (byte)(direction % LanProtocol.DashDirections);
        }

        // After the animators: the driver's pose onto the avatar, then the procedural layer.
        private void LateUpdate()
        {
            bool copy = _armed && _driverObject != null;
            if (copy)
            {
                // Over the avatar's own legs the spine takes the driver's turn relative to the body as a
                // whole (an aiming stance may turn the hips; the walking ones face ahead), the rest of
                // the upper body its turns relative to the spine.
                for (int i = 0; i < _from.Length; i++)
                    if (!_layered) _to[i].localRotation = _from[i].localRotation;
                    else if (i == _upperRoot) _to[i].rotation = _from[i].rotation;
                    else if (_upper[i]) _to[i].localRotation = _from[i].localRotation;
                if (!_layered && _fromHub != null) _toHub.localPosition = _toHubRest + (_fromHub.localPosition - _fromHubRest) * _hubScale;
            }
            float follow = 1f - Mathf.Exp(-Follow * Time.unscaledDeltaTime);
            _crouch = Mathf.Lerp(_crouch, _crouchTarget, follow);
            _pitch = Mathf.Lerp(_pitch, _pitchTarget, follow);
            _reload = Mathf.Lerp(_reload, _reloadTarget, follow);
            _lower = Mathf.Lerp(_lower, _lowerTarget, follow);
            float quick = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            _run = Mathf.Lerp(_run, _runTarget, follow); _air = Mathf.Lerp(_air, _airTarget, quick);
            _swim = Mathf.Lerp(_swim, _swimTarget, follow); _cock = Mathf.Lerp(_cock, _cockTarget, quick);
            _use = Mathf.Lerp(_use, _useTarget, follow);
            foreach (var turned in _turned) Restore(turned);
            RestorePelvis();
            var right = transform.right;
            Stride(right);
            if (_crouch > .001f)
            {
                // Knees forward, shins back, feet flat; the pelvis drops by what the legs lost in height.
                float knee = _crouch * CrouchKnee;
                Turn(_lThigh, -knee, right); Turn(_lShin, 2f * knee, right); Turn(_lFoot, -knee, right);
                Turn(_rThigh, -knee, right); Turn(_rShin, 2f * knee, right); Turn(_rFoot, -knee, right);
                if (_pelvis != null) _pelvis.position += Vector3.down * (_legLength * (1f - Mathf.Cos(knee * Mathf.Deg2Rad)));
            }
            // Looking up or down: the body bends with it, the more so with a rifle (the arms hang
            // from the chest, so the weapon follows the spine's share). 1.4.13: looking up unarmed, the upper
            // back takes more and the head and neck less (a head tilted 40 degrees back pulled a long hairdo,
            // skinned to the neck and the upper back too, through the body). 1.4.14: less still (the hair has no
            // physics on this copy: it stays as the head turns it): at 90 degrees up the head and neck tilt ~18.
            float body = Mathf.Clamp(_pitch, -MaxBodyPitch, MaxBodyPitch);
            float lean = _crouch * CrouchLean;
            bool up = !_armed && _pitch > 0f;
            Turn(_spine2, (_armed ? .4f : up ? .25f : .1f) * -body + lean * .5f, right);
            Turn(_spine3, (_armed ? .4f : up ? .35f : .15f) * -body + lean * .5f, right);
            Turn(_neck, (_armed ? .1f : up ? .08f : .3f) * -_pitch, right);
            Turn(_head, (_armed ? .1f : up ? .12f : .45f) * -_pitch, right);
            if (!_armed) { Striking(right); Throwing(right); Jabbing(right); }
            else
            {
                if (copy) Hands();
                float age = Time.unscaledTime - _shotAt;
                if (age >= 0f && age < 6f * RecoilSettle) Turn(_spine3, -RecoilKick * Mathf.Exp(-age / RecoilSettle), right);
                if (_reload > .01f)
                {
                    float work = Mathf.Sin(Time.unscaledTime * ReloadPace) * .5f + .5f;
                    Turn(_spine3, ReloadLean * _reload, right);
                    Turn(_arm, ReloadDip * _reload, right);
                    Turn(_lArm, (ReloadReach + ReloadWork * work) * _reload, right);
                }
            }
            float now = Time.unscaledTime;
            Airborne(right); Climbing(right, now); Using(right, now); Reaching(right, now); Flinching(right, now);
            Swimming(right, now);
            Roll();
            foreach (var turned in _turned) turned.Written = turned.Bone.localRotation;
            if (_pelvis != null) _pelvisWritten = _pelvis.localPosition;
        }

        // 1.4.6: both palms where the military NPC has them, seen from its chest, on this avatar's
        // chest (so they follow its bends), scaled to its reach: the weapon in the right palm, the
        // left on the fore-end; along a shorter firearm the left comes back towards the right; not
        // aiming, all of it lowered about the right shoulder. While reloading the left hand lets go
        // (the reload's own turns move it).
        private void Hands()
        {
            if (!_handsReady) return;
            var turn = _chest.rotation * Quaternion.Inverse(_driverChest.rotation);
            var right = _chest.position + turn * ((_driverRight.position - _driverChest.position) * _armScale);
            var left = _chest.position + turn * ((_driverLeft.position - _driverChest.position) * _armScale);
            left = right + (left - right) * Mathf.Clamp(_heldLength / RifleLength, MinSupport, 1.3f);
            Quaternion rightTurn = turn * _driverRight.rotation, leftTurn = turn * _driverLeft.rotation;
            if (_lower > .01f)
            {
                // Low ready: the weapon and both hands go down about the right shoulder.
                var drop = Quaternion.AngleAxis(_lower, transform.right);
                var shoulder = _rUpper.position;
                right = shoulder + drop * (right - shoulder); left = shoulder + drop * (left - shoulder);
                rightTurn = drop * rightTurn; leftTurn = drop * leftTurn;
            }
            Reach(_rUpper, _rLower, _rPalm, right, rightTurn, 1f);
            Reach(_lUpper, _lLower, _lPalm, left, leftTurn, 1f - _reload);
        }

        // 1.4.8: the held melee weapon's strike since the last swing began.
        private void Striking(Vector3 right)
        {
            float age = Time.unscaledTime - _swingAt;
            var up = transform.up;
            switch (StrikeKind)
            {
                case Strike.Chop:
                {
                    // Both arms over the right shoulder, the chest turned back, then down across.
                    float arm = Curve(age, ChopRise, ChopStrike, ChopReturn, ChopHigh, ChopLow);
                    if (arm == 0f) return;
                    Turn(_arm, arm, right); Turn(_lArm, arm * .85f, right);
                    Turn(_spine3, arm / ChopHigh * ChopTwist, up);
                    return;
                }
                case Strike.Sideways:
                {
                    // The arms raised level and swung back to the right, then across to the left; the chest follows.
                    if (age < 0f || age >= SideWind + SideStrike + SideReturn) return;
                    float raise, yaw;
                    if (age < SideWind) { raise = Mathf.SmoothStep(0f, 1f, age / SideWind); yaw = SideBack * raise; }
                    else if (age < SideWind + SideStrike) { raise = 1f; yaw = Mathf.Lerp(SideBack, SideAcross, Mathf.SmoothStep(0f, 1f, (age - SideWind) / SideStrike)); }
                    else { float t = Mathf.SmoothStep(0f, 1f, (age - SideWind - SideStrike) / SideReturn); raise = 1f - t; yaw = Mathf.Lerp(SideAcross, 0f, t); }
                    Turn(_arm, SideRaise * raise, right); Turn(_arm, yaw, up);
                    Turn(_lArm, SideRaise * .85f * raise, right); Turn(_lArm, yaw, up);
                    Turn(_spine3, yaw * .3f, up); Turn(_spine2, yaw * .15f, up);
                    return;
                }
                case Strike.Stab:
                {
                    // Drawn back, then thrust straight ahead at full reach, the chest leaning in.
                    if (age < 0f || age >= StabPull + StabThrust + StabHold + StabReturn) return;
                    if (age < StabPull) { Turn(_arm, 25f * Mathf.SmoothStep(0f, 1f, age / StabPull), right); return; }
                    float w = Envelope(age - StabPull, StabThrust, StabHold, StabReturn);
                    Turn(_spine3, 8f * w, right);
                    if (_armsReady) Reach(_rUpper, _rLower, _rPalm, Ahead(_rUpper, _rLower, _rPalm, transform.forward - up * .08f), _rPalm.rotation, w);
                    return;
                }
                default:
                    Turn(_arm, SwingAngle(age), right);
                    return;
            }
        }

        // 1.4.8: a jab: one hand straight at head height towards the middle, the chest turning behind it.
        private void Jabbing(Vector3 right)
        {
            float age = Time.unscaledTime - _jabAt;
            if (!_armsReady || age < 0f || age >= JabThrust + JabHold + JabReturn) return;
            float w = Envelope(age, JabThrust, JabHold, JabReturn);
            Turn(_spine3, (_jabLeft ? 12f : -12f) * w, transform.up);
            var direction = transform.forward + transform.up * .12f + (_jabLeft ? right : -right) * .12f;
            if (_jabLeft) Reach(_lUpper, _lLower, _lPalm, Ahead(_lUpper, _lLower, _lPalm, direction), _lPalm.rotation, w);
            else Reach(_rUpper, _rLower, _rPalm, Ahead(_rUpper, _rLower, _rPalm, direction), _rPalm.rotation, w);
        }

        // 1.4.8: the use key: a hand out where the partner looks (the left one when it holds a
        // firearm); looking down it bends (back and knees) and reaches to the ground before it.
        private void Reaching(Vector3 right, float now)
        {
            float age = now - _reachAt;
            if (!_armsReady || age < 0f || age >= ReachRise + ReachHold + ReachReturn) return;
            float w = Envelope(age, ReachRise, ReachHold, ReachReturn);
            var up = transform.up; var forward = transform.forward;
            Vector3 target;
            float side = _armed ? -.1f : .12f;
            if (_reachDown)
            {
                float knee = BendKnee * w;
                Turn(_lThigh, -knee, right); Turn(_lShin, 2f * knee, right); Turn(_lFoot, -knee, right);
                Turn(_rThigh, -knee, right); Turn(_rShin, 2f * knee, right); Turn(_rFoot, -knee, right);
                if (_pelvis != null) _pelvis.position -= up * (_legLength * (1f - Mathf.Cos(knee * Mathf.Deg2Rad)));
                Turn(_spine2, BendCurl * .5f * w, right); Turn(_spine3, BendCurl * .5f * w, right);
                target = transform.position + forward * .45f + up * .12f + right * side;
            }
            else target = _chest.position + Quaternion.AngleAxis(-Mathf.Clamp(_pitch, -60f, 60f), right) * forward * .6f + right * side;
            if (_armed) Reach(_lUpper, _lLower, _lPalm, target, _lPalm.rotation, w);
            else Reach(_rUpper, _rLower, _rPalm, target, _rPalm.rotation, w);
        }

        // A point a whole arm's length (as the arm is now) from the shoulder along a direction.
        private static Vector3 Ahead(Transform upper, Transform lower, Transform end, Vector3 direction)
        {
            float length = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, end.position);
            return upper.position + direction.normalized * (length * .95f);
        }

        // 0..1: up over rise, held, back down over the return (smoothed).
        internal static float Envelope(float age, float rise, float hold, float back)
        {
            if (age < 0f) return 0f;
            if (age < rise) return Mathf.SmoothStep(0f, 1f, age / rise);
            age -= rise;
            if (age < hold) return 1f;
            age -= hold;
            return age < back ? 1f - Mathf.SmoothStep(0f, 1f, age / back) : 0f;
        }

        // A blow's turn (degrees): up to high over rise, through to low over strike, back to 0.
        internal static float Curve(float age, float rise, float strike, float back, float high, float low)
        {
            if (age < 0f) return 0f;
            if (age < rise) return high * Mathf.SmoothStep(0f, 1f, age / rise);
            age -= rise;
            if (age < strike) return Mathf.Lerp(high, low, age / strike);
            age -= strike;
            return age < back ? Mathf.Lerp(low, 0f, Mathf.SmoothStep(0f, 1f, age / back)) : 0f;
        }

        // 1.4.7: running, a longer stride (the legs' turns from rest grow) and a lean forward.
        private void Stride(Vector3 right)
        {
            if (_run < .01f) return;
            if (_walking)
                foreach (var leg in new[] { _lThigh, _rThigh, _lShin, _rShin })
                    if (leg != null) leg.Bone.localRotation = Quaternion.SlerpUnclamped(leg.Rest, leg.Bone.localRotation, 1f + RunStride * _run);
            Turn(_spine2, RunLean * .5f * _run, right); Turn(_spine3, RunLean * .5f * _run, right);
        }

        // 1.4.7: in the air the knees come up (the right a little less), the free arms rise a little.
        private void Airborne(Vector3 right)
        {
            if (_air < .01f) return;
            float knee = AirKnee * _air;
            Turn(_lThigh, -1.2f * knee, right); Turn(_lShin, 1.8f * knee, right); Turn(_lFoot, -.6f * knee, right);
            Turn(_rThigh, -.9f * knee, right); Turn(_rShin, 1.5f * knee, right); Turn(_rFoot, -.5f * knee, right);
            if (!_armed) { Turn(_arm, -20f * _air, right); Turn(_lArm, -20f * _air, right); }
        }

        // 1.4.7: a climb: hand over hand and step over step, strongest mid-way.
        private void Climbing(Vector3 right, float now)
        {
            float age = now - _climbAt;
            if (age < 0f || age >= ClimbTime) return;
            float c = Mathf.Sin(age / ClimbTime * Mathf.PI), beat = Mathf.Sin(age * 14f);
            if (!_armed) { Turn(_arm, (-150f + 25f * beat) * c, right); Turn(_lArm, (-150f - 25f * beat) * c, right); }
            Turn(_lThigh, (-45f + 25f * beat) * c, right); Turn(_lShin, 60f * c, right);
            Turn(_rThigh, (-45f - 25f * beat) * c, right); Turn(_rShin, 60f * c, right);
        }

        // 1.4.7: a throw over the shoulder: the arm held back (the chest turned with it, the other arm
        // forward), then over and down, then back.
        private void Throwing(Vector3 right)
        {
            float age = Time.unscaledTime - _throwAt;
            bool going = age >= 0f && age < _throwRise + ThrowStrike + ThrowReturn;
            float arm = going ? ThrowAngle(age, _throwRise) : ThrowBack * _cock;
            if (Mathf.Abs(arm) < .01f) return;
            Turn(_arm, arm, right);
            float back = Mathf.Clamp01(arm / ThrowBack);
            Turn(_spine3, arm / ThrowBack * 20f, transform.up);
            Turn(_lArm, -50f * back, right);
        }

        // The throwing arm's turn (degrees about the right axis) at a time after the throw began.
        internal static float ThrowAngle(float age, float rise)
        {
            if (age < 0f) return 0f;
            if (age < rise) return ThrowBack * Mathf.SmoothStep(0f, 1f, age / rise);
            age -= rise;
            if (age < ThrowStrike) return Mathf.Lerp(ThrowBack, ThrowFollow, Mathf.SmoothStep(0f, 1f, age / ThrowStrike));
            age -= ThrowStrike;
            return age < ThrowReturn ? Mathf.Lerp(ThrowFollow, 0f, Mathf.SmoothStep(0f, 1f, age / ThrowReturn)) : 0f;
        }

        // 1.4.7: eating or drinking: the right hand at the mouth (bites; drinking, the head back);
        // a bandage or a medkit: the left forearm before the chest (the belly for a medkit), the right
        // hand going round it, the head down to watch.
        private void Using(Vector3 right, float now)
        {
            if (_use < .01f || !_armsReady) return;
            var up = transform.up; var forward = transform.forward;
            if (_useKind == LanProtocol.UseEat || _useKind == LanProtocol.UseDrink)
            {
                bool drink = _useKind == LanProtocol.UseDrink;
                if (drink) Turn(_head, -15f * _use, right);
                var mouth = _head.Bone.position + forward * .12f - up * .07f + (drink ? Vector3.zero : up * (.02f * Mathf.Sin(now * BitePace)));
                Reach(_rUpper, _rLower, _rPalm, mouth, _rPalm.rotation, _use);
                return;
            }
            var centre = _chest.position + forward * (_useKind == LanProtocol.UseBandage ? .32f : .26f) - up * (_useKind == LanProtocol.UseBandage ? .12f : .32f);
            Turn(_head, 20f * _use, right);
            Reach(_lUpper, _lLower, _lPalm, centre - right * .05f, _lPalm.rotation, _use);
            float turn = now * WrapPace;
            Reach(_rUpper, _rLower, _rPalm, centre + right * .07f + (up * Mathf.Sin(turn) + forward * Mathf.Cos(turn)) * .05f, _rPalm.rotation, _use);
        }

        // 1.4.7: hit: the chest and head jerk back and the knees give a little, then settle.
        private void Flinching(Vector3 right, float now)
        {
            float age = now - _flinchAt;
            if (age < 0f || age >= FlinchTime) return;
            float k = age < .06f ? age / .06f : Mathf.Exp(-(age - .06f) / .12f);
            Turn(_spine2, -8f * k, right); Turn(_spine3, -8f * k, right); Turn(_head, -12f * k, right);
            Turn(_lThigh, -10f * k, right); Turn(_lShin, 20f * k, right); Turn(_rThigh, -10f * k, right); Turn(_rShin, 20f * k, right);
            if (_pelvis != null) _pelvis.position -= transform.up * (.03f * k);
        }

        // 1.4.7: swimming: legs kicking in turn and the breaststroke (free arms), then the whole body
        // tilts forward about the chest, as if lying on the water.
        private void Swimming(Vector3 right, float now)
        {
            if (_swim < .01f || _hub == null) return;
            float kick = Mathf.Sin(now * SwimPace * 2f) * SwimKick * _swim;
            Turn(_lThigh, kick, right); Turn(_rThigh, -kick, right);
            if (!_armed)
            {
                float stroke = (-110f + SwimStroke * Mathf.Sin(now * SwimPace)) * _swim;
                Turn(_arm, stroke, right); Turn(_lArm, stroke, right);
            }
            var pivot = _chest != null ? _chest.position : _hub.Bone.position + transform.up * (.6f * _hubHeight);
            _hub.Bone.RotateAround(pivot, right, SwimTilt * _swim);
        }

        // Two-bone reach: the elbow stays on the side the animators bent it to, the palm turns
        // as given; a target out of reach is approached along the same line. weight 0..1.
        internal static void Reach(Transform upper, Transform lower, Transform end, Vector3 target, Quaternion rotation, float weight)
        {
            if (weight <= .001f || float.IsNaN(target.x) || float.IsNaN(target.y) || float.IsNaN(target.z)) return;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            target = Vector3.Lerp(c, target, weight);
            float upperLength = (b - a).magnitude, lowerLength = (c - b).magnitude;
            var toTarget = target - a;
            float distance = toTarget.magnitude;
            if (upperLength < 1e-3f || lowerLength < 1e-3f || distance < 1e-3f) return;
            float reach = Mathf.Clamp(distance, Mathf.Abs(upperLength - lowerLength) + 1e-3f, upperLength + lowerLength - 1e-3f);
            var direction = toTarget / distance;
            var bend = (b - a) - direction * Vector3.Dot(b - a, direction);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.Cross(direction, upper.right);
            if (bend.sqrMagnitude < 1e-8f) return;
            bend.Normalize();
            float along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2f * reach);
            float side = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            var elbow = a + direction * along + bend * side;
            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            b = lower.position; c = end.position;
            lower.rotation = Quaternion.FromToRotation(c - b, a + direction * reach - b) * lower.rotation;
            end.rotation = Quaternion.Slerp(end.rotation, rotation, weight);
        }

        // 1.4.6: the roll. The body turns to face the dash (the back of it for a dash backwards),
        // tucks (knees in, back curled, head down, the pelvis low) and goes once round over its
        // head (over its back backwards) about its middle, then turns back to the view.
        private void Roll()
        {
            float age = Time.unscaledTime - _rollAt;
            if (_hub == null || age < 0f || age >= RollTime) return;
            float p = age / RollTime, tuck = Mathf.Sin(p * Mathf.PI);
            float heading = Mathf.DeltaAngle(0f, _rollDirection * 45f);
            bool back = Mathf.Abs(heading) > 90f;
            float face = (back ? Mathf.DeltaAngle(180f, heading) : heading) *
                Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p / RollFace)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - p) / RollFace));
            // The tuck about the body's own right axis; then the whole body turns, then rolls about
            // the turned axis.
            var up = transform.up; var right = transform.right;
            float knee = tuck * RollKnee;
            Turn(_lThigh, -1.4f * knee, right); Turn(_lShin, 2f * knee, right); Turn(_lFoot, -.6f * knee, right);
            Turn(_rThigh, -1.4f * knee, right); Turn(_rShin, 2f * knee, right); Turn(_rFoot, -.6f * knee, right);
            Turn(_spine2, tuck * RollCurl * .5f, right); Turn(_spine3, tuck * RollCurl * .5f, right);
            Turn(_neck, tuck * RollHead * .5f, right); Turn(_head, tuck * RollHead * .5f, right);
            var hub = _hub.Bone;
            hub.position += -up * (tuck * RollSink * _hubHeight);
            var pivot = hub.position + up * (.15f * _hubHeight);
            hub.RotateAround(new Vector3(transform.position.x, pivot.y, transform.position.z), up, face);
            hub.RotateAround(pivot, Quaternion.AngleAxis(face, up) * right, (back ? -360f : 360f) * Mathf.SmoothStep(0f, 1f, p));
        }

        // The upper arm's turn (degrees about the right axis, negative raises it forward) at a
        // time after a swing began.
        internal static float SwingAngle(float age)
        {
            if (age < 0f || age > SwingRise + SwingStrike + SwingReturn) return 0f;
            if (age < SwingRise) return SwingHigh * Mathf.SmoothStep(0f, 1f, age / SwingRise);
            age -= SwingRise;
            if (age < SwingStrike) return Mathf.Lerp(SwingHigh, SwingLow, age / SwingStrike);
            age -= SwingStrike;
            return Mathf.Lerp(SwingLow, 0f, Mathf.SmoothStep(0f, 1f, age / SwingReturn));
        }

        private static void Restore(Turned turned)
        {
            if (turned.Seen && turned.Bone.localRotation == turned.Written) turned.Bone.localRotation = turned.Base;
            else turned.Base = turned.Bone.localRotation;
            turned.Seen = true;
        }

        private void RestorePelvis()
        {
            if (_pelvis == null) return;
            if (_pelvisSeen && _pelvis.localPosition == _pelvisWritten) _pelvis.localPosition = _pelvisBase;
            else _pelvisBase = _pelvis.localPosition;
            _pelvisSeen = true;
        }

        private static void Turn(Turned turned, float degrees, Vector3 axis)
        {
            if (turned == null || Mathf.Abs(degrees) < .01f) return;
            turned.Bone.rotation = Quaternion.AngleAxis(degrees, axis) * turned.Bone.rotation;
        }
    }
}
