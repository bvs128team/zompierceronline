using System;
using UnityEngine;

namespace ZompiercerLAN
{
    // Notifications (1.0.8): a short chime and a line at the top of the screen when the other
    // player connects or the connection is lost, when the partner writes in chat, and when a
    // friend has confirmed the code and waits for the host's permission; the host used to miss
    // these with the LAN panel closed. The chime is made here (no game or file sound), and the
    // partner's text is shown as plain text. Main thread only; drawn in OnGUI.
    internal static class LanNotify
    {
        internal const float ShowSeconds = 5f, SoundInterval = 1f;
        // [Notifications] Sound.
        internal static bool Sound = true;
        private static GameObject _owner;
        private static AudioSource _source;
        private static AudioClip _alert, _message;
        private static string _text;
        private static float _until, _nextSound;
        private static GUIStyle _style;

        // Two rising tones and the line.
        internal static void Alert(string text) { Show(text); Play(true); }
        // One short tone, and the line when there is one.
        internal static void Message(string text) { if (text != null) Show(text); Play(false); }

        private static void Show(string text) { _text = text; _until = Time.unscaledTime + ShowSeconds; }

        private static void Play(bool alert)
        {
            if (!Sound || Time.unscaledTime < _nextSound) return;
            _nextSound = Time.unscaledTime + SoundInterval;
            try
            {
                if (_source == null)
                {
                    _owner = new GameObject("ZompiercerLAN.Notify") { hideFlags = HideFlags.HideInHierarchy };
                    UnityEngine.Object.DontDestroyOnLoad(_owner);
                    _source = _owner.AddComponent<AudioSource>();
                    _source.playOnAwake = false; _source.spatialBlend = 0f; _source.ignoreListenerPause = true; _source.volume = .45f;
                    _alert = Tone("ZompiercerLAN.Alert", new[] { 660f, 880f }, .16f);
                    _message = Tone("ZompiercerLAN.Message", new[] { 990f }, .12f);
                }
                _source.PlayOneShot(alert ? _alert : _message);
            }
            catch (Exception) { Sound = false; } // no audio: the notifications stay silent
        }

        // Sine tones one after another, each with a 5 ms rise, a fading tail and a 5 ms fall.
        private static AudioClip Tone(string name, float[] pitches, float seconds)
        {
            const int rate = 44100;
            int each = (int)(rate * seconds), edge = rate / 200;
            var data = new float[each * pitches.Length];
            for (int t = 0; t < pitches.Length; t++)
                for (int i = 0; i < each; i++)
                {
                    float time = i / (float)rate;
                    float envelope = Mathf.Min(1f, Mathf.Min(i, each - i) / (float)edge) * Mathf.Exp(-3f * time / seconds);
                    data[t * each + i] = .6f * envelope * Mathf.Sin(2f * Mathf.PI * pitches[t] * time);
                }
            var clip = AudioClip.Create(name, data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        internal static void Draw()
        {
            if (_text == null || Event.current.type != EventType.Repaint) return;
            float left = _until - Time.unscaledTime;
            if (left <= 0f) { _text = null; return; }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { richText = false, wordWrap = true, fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _style.normal.textColor = Color.white;
            }
            var content = new GUIContent(_text);
            float width = Mathf.Min(560f, Screen.width - 32f);
            float height = Mathf.Max(34f, _style.CalcHeight(content, width) + 8f);
            var previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(left / .5f));
            GUI.Box(new Rect((Screen.width - width) / 2f, Screen.height * .1f, width, height), content, _style);
            GUI.color = previous;
        }

        internal static void Shutdown()
        {
            if (_owner != null) UnityEngine.Object.Destroy(_owner);
            _owner = null; _source = null; _text = null;
        }
    }
}
