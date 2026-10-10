using System;
using System.Collections.Generic;
using UnityEngine;
using Zompiercer.Inputs;

namespace ZompiercerLAN
{
    // Text chat (0.11.0): Enter opens a line at the bottom left, Enter sends, Esc closes.
    // While it is open the game is in its own text-input state (EStateInput.InputWindow, as in
    // the game's WindowWithInputField), so typing does not move, shoot or use anything.
    // Text from the partner is shown as plain text only (rich text off), under the trusted
    // name of the connection ("Хост"/"Друг"), never logged. Main thread only; drawn in OnGUI.
    internal sealed class LanChat
    {
        internal const int MaxLines = 50, ShownClosed = 6, ShownOpen = 12;
        internal const float ShowSeconds = 15f, FadeSeconds = 3f;
        private const string InputName = "ZompiercerLANChat";
        private enum Who { Own, Partner, Notice }
        private sealed class Line { internal string Text; internal Who From; internal float At; internal int Id; }

        // [Chat] Enabled / Key / HidePartner.
        internal static bool Enabled = true, HidePartner;
        internal static KeyCode Key = KeyCode.Return;

        private readonly LanChatChannel _channel;
        private readonly bool _hosting;
        private readonly List<Line> _lines = new List<Line>();
        private string _draft = "";
        private bool _focus, _ready;
        private int _openedFrame = -1, _hidden;
        // Static: the player's control is given back even after this chat (the session) is gone.
        private static ZombieFighterController _held;
        private static bool _heldControl, _restore;
        private bool _unfocus;
        private GUIStyle _style, _shadow, _field;

        internal LanChat(bool hosting, Action<Packet> send) { _hosting = hosting; _channel = new LanChatChannel(send); }

        internal bool Open { get; private set; }
        private string PartnerName { get { return LanSkinPicker.PartnerLabel(_hosting); } } // 1.1.9: its nickname, if any

        internal void Reset() { _channel.Reset(); }

        internal void Tick(bool ready)
        {
            _ready = ready && Enabled;
            _channel.Tick((double)Time.unscaledTime);
            if (!Open) return;
            // The game took the input state (menu, death, loading) or the session ended.
            var input = InputController.Global;
            if (!_ready || input == null || (int)input.CurrentState != (int)EStateInput.InputWindow) Close();
        }

        // The partner's new message, when it is shown (1.0.8: for the notification), else null.
        internal string Receive(Packet p)
        {
            string text = _channel.Receive(p);
            if (text == null || !Enabled) return null;
            if (HidePartner) { _hidden++; return null; }
            Add(new Line { Text = text, From = Who.Partner, At = Time.unscaledTime });
            return text;
        }

        private void Add(Line line)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        private void Notice(string text) { Add(new Line { Text = text, From = Who.Notice, At = Time.unscaledTime }); }

        // OnGUI, before Draw: the open/send/close keys.
        internal void KeyEvent(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            if (!Open)
            {
                if (!_ready || _restore || LanGuestDeath.Dead || e.keyCode != Key || e.keyCode == KeyCode.None) return;
                var input = InputController.Global;
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (input == null || player == null || (int)input.CurrentState != (int)EStateInput.Game) return;
                _held = player; _heldControl = player.controlEnabled;
                player.controlEnabled = false;
                input.SetState(EStateInput.InputWindow);
                Open = true; _focus = true; _openedFrame = Time.frameCount;
                e.Use();
                return;
            }
            // The character of the key that opened the line is not typed into it.
            if (e.keyCode == KeyCode.None && Time.frameCount == _openedFrame) { e.Use(); return; }
            if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                SendDraft();
                Close(); e.Use();
            }
        }

        private void SendDraft()
        {
            string text; int id;
            switch (_channel.Send(_draft, (double)Time.unscaledTime, out text, out id))
            {
                case LanChatChannel.Submit.Sent:
                    _draft = "";
                    Add(new Line { Text = text, From = Who.Own, At = Time.unscaledTime, Id = id });
                    break;
                case LanChatChannel.Submit.TooFast: Notice("Не так быстро: сообщение не отправлено"); break;
                case LanChatChannel.Submit.Full: Notice("Связь медленная: дождитесь доставки прошлых сообщений"); break;
                default: _draft = ""; break;
            }
        }

        internal void Close()
        {
            if (!Open) return;
            Open = false; _focus = false; _unfocus = true;
            var input = InputController.Global;
            if (input != null && (int)input.CurrentState == (int)EStateInput.InputWindow) input.SetState(EStateInput.Game);
            _restore = true;
            RestoreControl();
        }

        // The player's control comes back once the game is in its play state again: at once,
        // or after a menu the game opened over the chat line closes. Called first in Update.
        internal static void RestoreControl()
        {
            if (!_restore) return; // a chat line cannot open while this is pending
            var input = InputController.Global;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (player == null || player != _held) { _restore = false; _held = null; return; }
            if (input == null || (int)input.CurrentState != (int)EStateInput.Game) return;
            player.controlEnabled = _heldControl;
            _restore = false; _held = null;
        }

        internal string Hint
        {
            get
            {
                if (!Enabled) return "Чат выключен в конфиге ([Chat] Enabled)";
                return "Чат: " + (Key == KeyCode.Return ? "Enter" : Key.ToString()) + " — написать, Enter — отправить, Esc — закрыть" +
                    (HidePartner && _hidden > 0 ? " · скрыто сообщений: " + _hidden : "");
            }
        }

        // OnGUI: recent lines (all of the last ShownOpen while typing) and the input line.
        internal void Draw()
        {
            if (_unfocus && !Open) { _unfocus = false; if (GUI.GetNameOfFocusedControl() == InputName) GUIUtility.keyboardControl = 0; }
            if (!Enabled || !_ready && !Open) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true, fontSize = 15, alignment = TextAnchor.UpperLeft };
                _style.normal.textColor = Color.white;
                _shadow = new GUIStyle(_style); _shadow.normal.textColor = new Color(0f, 0f, 0f, .85f);
                _field = new GUIStyle(GUI.skin.textField) { richText = false, wordWrap = false, fontSize = 15 };
            }
            float now = Time.unscaledTime;
            float width = Mathf.Min(520f, Screen.width * .45f), x = 12f;
            float bottom = Screen.height - 230f; // 1.0.0: above the game's bottom HUD
            if (Open)
            {
                var field = new Rect(x, bottom, width, 26f);
                GUI.Box(new Rect(x - 4f, field.y - 4f, width + 8f, 52f), GUIContent.none);
                GUI.SetNextControlName(InputName);
                _draft = GUI.TextField(field, _draft ?? "", LanChatChannel.MaxChars, _field);
                if (_focus) { GUI.FocusControl(InputName); if (Event.current.type == EventType.Repaint) _focus = false; }
                GUI.Label(new Rect(x, field.yMax + 2f, width, 20f), "Enter — отправить · Esc — закрыть · " + (_draft ?? "").Length + "/" + LanChatChannel.MaxChars, _style);
                bottom -= 8f;
            }
            float y = bottom;
            int shown = 0;
            for (int i = _lines.Count - 1; i >= 0 && shown < (Open ? ShownOpen : ShownClosed); i--)
            {
                var line = _lines[i];
                float age = now - line.At;
                if (!Open && age > ShowSeconds) break;
                float alpha = Open ? 1f : Mathf.Clamp01((ShowSeconds - age) / FadeSeconds);
                string text = Prefix(line) + line.Text + (line.From == Who.Own && _channel.IsPending(line.Id) ? "  …" : "");
                var content = new GUIContent(text);
                float height = _style.CalcHeight(content, width);
                y -= height + 2f;
                var rect = new Rect(x, y, width, height);
                var previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), content, _shadow);
                GUI.color = ColorOf(line.From, alpha);
                GUI.Label(rect, content, _style);
                GUI.color = previous;
                shown++;
            }
        }

        private string Prefix(Line line)
        {
            switch (line.From)
            {
                case Who.Own: return "Вы: ";
                case Who.Partner: return PartnerName + ": ";
                default: return "• ";
            }
        }
        private static Color ColorOf(Who who, float alpha)
        {
            switch (who)
            {
                case Who.Own: return new Color(.85f, .9f, 1f, alpha);
                case Who.Partner: return new Color(.55f, 1f, .6f, alpha);
                default: return new Color(1f, .8f, .4f, alpha);
            }
        }
    }
}
