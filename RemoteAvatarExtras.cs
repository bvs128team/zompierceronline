using UnityEngine;

namespace ZompiercerLAN
{
    // 1.1.9: what the other player's avatar gives off besides its pose: the beams of its helmet and
    // weapon flashlights (lights set up like this player's own helmet flashlight) and the sound of
    // its steps (the game's NPC footsteps; the game has no footstep sounds for the player itself).
    // Display only: no gameplay scripts.
    internal sealed class RemoteAvatarExtras : MonoBehaviour
    {
        private const float StepWalk = .52f, StepRun = .34f, StepCrouch = .7f, RunSpeed = 3.2f, MinSpeed = .6f;
        private Light _helmet, _gun;
        private Transform _head, _palm;
        private AudioSource _steps;
        private AudioClip[] _clips;
        private float _nextStep, _pitch;
        private bool _helmetOn, _gunOn;

        // Where it looks (the avatar root's yaw while standing), set with its pose every frame.
        internal float Yaw { get; set; }

        // head, palm: the avatar's head and right palm bones (null: at a standing person's height).
        internal void Initialize(Transform head, Transform palm)
        {
            _head = head; _palm = palm;
            var own = OwnHelmetLight();
            _helmet = MakeLight("LAN partner helmet light", own);
            _gun = MakeLight("LAN partner weapon light", own);
            _steps = gameObject.AddComponent<AudioSource>();
            _steps.playOnAwake = false; _steps.spatialBlend = 1f; _steps.minDistance = 1.5f; _steps.maxDistance = 25f; _steps.rolloffMode = AudioRolloffMode.Linear;
            foreach (var npc in Resources.FindObjectsOfTypeAll<NPCEffects>())
                if (npc != null && npc.NPCFootSteps != null && npc.NPCFootSteps.Length > 0) { _clips = npc.NPCFootSteps; break; }
        }

        private Light MakeLight(string name, Light own)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(transform, false);
            var light = holder.AddComponent<Light>();
            light.type = LightType.Spot; light.range = 25f; light.spotAngle = 55f; light.intensity = 1.6f;
            light.color = new Color(1f, .96f, .88f); light.shadows = LightShadows.None;
            if (own != null) { light.range = own.range; light.spotAngle = own.spotAngle; light.intensity = own.intensity; light.color = own.color; light.cookie = own.cookie; light.renderMode = own.renderMode; }
            light.enabled = false;
            return light;
        }

        private static Light OwnHelmetLight()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var weapon = player == null ? null : player.zombieFighterFireArmWeapon;
            return weapon == null || weapon.flashlight == null ? null : weapon.flashlight.GetComponentInChildren<Light>(true);
        }

        // Every frame: the flashlights, the view's pitch (degrees, up positive), and the steps.
        internal void Apply(bool helmet, bool gun, float pitch, bool walking, float speed, bool crouching, bool lying)
        {
            _helmetOn = helmet && !lying; _gunOn = gun && !lying && _palm != null; _pitch = pitch;
            if (_steps == null || _clips == null || lying || !walking || speed < MinSpeed) { _nextStep = 0f; return; }
            float now = Time.unscaledTime;
            if (_nextStep <= 0f) _nextStep = now + .15f;
            if (now < _nextStep) return;
            _nextStep = now + (crouching ? StepCrouch : speed >= RunSpeed ? StepRun : StepWalk);
            float volume = GlobalSoundEffects.global == null ? 1f : Mathf.Clamp01(GlobalSoundEffects.global.EffectsVolume);
            var clip = _clips[Random.Range(0, _clips.Length)];
            if (clip != null) _steps.PlayOneShot(clip, (crouching ? .35f : speed >= RunSpeed ? .9f : .6f) * volume);
        }

        // After the pose: the beams go where the partner looks.
        private void LateUpdate()
        {
            var look = Quaternion.Euler(-_pitch, Yaw, 0f);
            if (_helmet != null)
            {
                _helmet.enabled = _helmetOn;
                if (_helmetOn) _helmet.transform.SetPositionAndRotation((_head != null ? _head.position : transform.position + Vector3.up * 1.65f) + look * new Vector3(0f, .12f, .12f), look);
            }
            if (_gun != null)
            {
                _gun.enabled = _gunOn;
                if (_gunOn) _gun.transform.SetPositionAndRotation(_palm.position + look * new Vector3(0f, .05f, .45f), look);
            }
        }
    }
}
