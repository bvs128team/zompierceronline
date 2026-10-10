using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZompiercerLAN
{
    // Text chat (0.11.0), without Unity: text cleaning and delivery. Messages are numbered
    // per sender and delivered in order exactly once; the receiver acknowledges the last
    // message it delivered and the sender repeats what was not acknowledged. Text is only
    // ever shown as plain text: never executed, parsed as a command, opened as a link,
    // rendered as markup or written to the log.
    internal sealed class LanChatChannel
    {
        internal const int MaxChars = LanProtocol.MaxChatChars, MaxPending = 8, ResendBatch = 4, MaxCombiningRun = 2;
        internal const double ResendSeconds = 1.5;
        internal const byte Message = 0, Ack = 1;
        internal enum Submit { Sent, Empty, TooFast, Full }

        private sealed class Outgoing { internal int Id; internal uint Sequence; internal string Text; internal double Next; }
        private readonly Action<Packet> _send;
        private readonly List<Outgoing> _pending = new List<Outgoing>();
        // Typing: one message a second with a burst of five. Wire: every chat packet this side
        // sends (messages, repeats, acknowledgements), well inside the peer's limit (8/s, 16).
        private readonly LanRateLimit _typing = new LanRateLimit(1, 5), _wire = new LanRateLimit(4, 8);
        private uint _sequence, _received;
        private int _nextId;
        private bool _ackDue;

        internal LanChatChannel(Action<Packet> send) { _send = send; }

        internal int Pending { get { return _pending.Count; } }
        internal bool IsPending(int id) { foreach (var o in _pending) if (o.Id == id) return true; return false; }

        // Cleans and queues one line; id names it for IsPending.
        internal Submit Send(string raw, double now, out string text, out int id)
        {
            text = Clean(raw); id = 0;
            if (text.Length == 0) return Submit.Empty;
            if (_pending.Count >= MaxPending) return Submit.Full;
            if (!_typing.Take(1, now)) return Submit.TooFast;
            id = ++_nextId;
            _pending.Add(new Outgoing { Id = id, Sequence = ++_sequence, Text = text, Next = 0 });
            Tick(now);
            return Submit.Sent;
        }

        // Returns the cleaned text of a newly delivered message, or null.
        internal string Receive(Packet p)
        {
            if (p == null || p.Kind != PacketKind.Chat) return null;
            if (p.Action == Ack)
            {
                if (p.Sequence > _sequence) return null; // never sent: ignore
                _pending.RemoveAll(o => o.Sequence <= p.Sequence);
                return null;
            }
            if (p.Sequence <= _received) { _ackDue = true; return null; } // a repeat
            if (p.Sequence != _received + 1) return null;                  // a gap: wait for the repeat
            _received = p.Sequence; _ackDue = true;
            string text = Clean(p.Text);
            return text.Length == 0 ? null : text;
        }

        internal void Tick(double now)
        {
            if (_ackDue && _received != 0 && _wire.Take(1, now))
            {
                _ackDue = false;
                _send(new Packet { Kind = PacketKind.Chat, Sequence = _received, Action = Ack, Text = "" });
            }
            for (int i = 0; i < _pending.Count && i < ResendBatch; i++)
            {
                var o = _pending[i];
                if (now < o.Next) continue;
                if (!_wire.Take(1, now)) break;
                o.Next = now + ResendSeconds;
                _send(new Packet { Kind = PacketKind.Chat, Sequence = o.Sequence, Action = Message, Text = o.Text });
            }
        }

        // A new secure session: both sides number from 1 again; undelivered lines are kept.
        internal void Reset()
        {
            _sequence = 0; _received = 0; _ackDue = false;
            foreach (var o in _pending) { o.Sequence = ++_sequence; o.Next = 0; }
        }

        // One line of plain text: no control or format characters (bidi overrides, zero-width,
        // BOM), no private-use, unassigned or lone surrogate characters, no more than two
        // combining marks in a row; every kind of space or line break becomes one space.
        internal static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var b = new StringBuilder(Math.Min(raw.Length, MaxChars));
            int marks = 0;
            for (int i = 0; i < raw.Length && b.Length < MaxChars; i++)
            {
                char c = raw[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= raw.Length || !char.IsLowSurrogate(raw[i + 1])) continue;
                    var pair = CharUnicodeInfo.GetUnicodeCategory(raw, i);
                    i++;
                    if (Dropped(pair) || b.Length + 2 > MaxChars) continue;
                    if (Combining(pair)) { if (marks >= MaxCombiningRun || b.Length == 0) continue; marks++; }
                    else marks = 0;
                    b.Append(c).Append(raw[i]);
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.SpaceSeparator ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
                {
                    if (b.Length != 0 && b[b.Length - 1] != ' ') b.Append(' ');
                    marks = 0;
                    continue;
                }
                if (Dropped(category)) continue;
                if (Combining(category)) { if (marks >= MaxCombiningRun || b.Length == 0 || b[b.Length - 1] == ' ') continue; marks++; }
                else marks = 0;
                b.Append(c);
            }
            while (b.Length != 0 && b[b.Length - 1] == ' ') b.Length--;
            return b.ToString();
        }
        private static bool Dropped(UnicodeCategory c)
        {
            return c == UnicodeCategory.Format || c == UnicodeCategory.PrivateUse || c == UnicodeCategory.OtherNotAssigned ||
                c == UnicodeCategory.Surrogate || c == UnicodeCategory.Control;
        }
        private static bool Combining(UnicodeCategory c)
        {
            return c == UnicodeCategory.NonSpacingMark || c == UnicodeCategory.EnclosingMark || c == UnicodeCategory.SpacingCombiningMark;
        }
    }
}
