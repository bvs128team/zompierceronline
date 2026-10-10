using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZompiercerLAN
{
    // Text chat (0.11.0): wire format, cleaning, delivery, budgets and fuzzing.
    internal static partial class Program
    {
        private static Packet ChatPacket(uint sequence, byte action, string text)
        { return new Packet { Kind = PacketKind.Chat, GameVersion = "checks", Session = 42, Sequence = sequence, Action = action, Text = text }; }

        private static void ChatChecks()
        {
            Packet parsed;
            // Wire format.
            string longest = new string('ж', LanProtocol.MaxChatChars);
            foreach (var good in new[] { ChatPacket(1, 0, "привет"), ChatPacket(uint.MaxValue, 0, longest), ChatPacket(7, 1, ""),
                ChatPacket(2, 0, "<size=999><color=red>Система</color></size>"), ChatPacket(3, 0, "😀 ok") })
            {
                var bytes = LanProtocol.Encode(good);
                Require(bytes.Length <= 1200, "Chat packet too large");
                Require(LanProtocol.TryDecode(bytes, out parsed) && parsed.Kind == PacketKind.Chat && parsed.Sequence == good.Sequence &&
                    parsed.Action == good.Action && parsed.Text == good.Text, "Valid chat packet rejected");
                for (int size = 0; size < bytes.Length; size++)
                { var truncated = new byte[size]; Array.Copy(bytes, truncated, size); Require(!LanProtocol.TryDecode(truncated, out parsed), "Truncated chat accepted"); }
                var trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length);
                Require(!LanProtocol.TryDecode(trailing, out parsed), "Chat trailing data accepted");
            }
            foreach (var bad in new[] { ChatPacket(0, 0, "x"), ChatPacket(1, 2, "x"), ChatPacket(1, 1, "x"), ChatPacket(1, 0, ""), ChatPacket(1, 0, null),
                ChatPacket(1, 0, longest + "ж"), ChatPacket(1, 0, "a\nb"), ChatPacket(1, 0, "a\u0007"), ChatPacket(1, 0, "a\u0085"), ChatPacket(1, 0, "\u007F") })
                ExpectRejected(() => LanProtocol.Encode(bad), "Invalid chat encoded");
            // Hand-made bodies the encoder refuses: control characters, overlong, invalid UTF-8.
            foreach (var body in new[] { Encoding.UTF8.GetBytes("a\rb"), Encoding.UTF8.GetBytes(longest + "a"), new byte[] { 0x61, 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 }, new byte[0] })
                Require(!LanProtocol.TryDecode(RawChat(1, 0, body), out parsed), "Malformed chat body accepted");
            Require(!LanProtocol.TryDecode(RawChat(1, 1, Encoding.UTF8.GetBytes("x")), out parsed), "Acknowledgement with text accepted");
            var huge = RawChat(1, 0, Encoding.UTF8.GetBytes("abc")); huge[huge.Length - 5] = 0xFF; huge[huge.Length - 4] = 0xFF;
            Require(!LanProtocol.TryDecode(huge, out parsed), "Chat length field over the limit accepted");
            Require(LanProtocol.TryDecode(RawChat(5, 0, Encoding.UTF8.GetBytes("ok")), out parsed) && parsed.Text == "ok", "Raw chat encoder out of step");
            Require(!LanProtocol.WorldBound(PacketKind.Chat), "Chat bound to a world");
            foreach (bool host in new[] { true, false })
                Require(LanNetworkPolicy.Allowed(host, PacketKind.Chat, true) && !LanNetworkPolicy.Allowed(host, PacketKind.Chat, false), "Chat direction policy");

            // Cleaning.
            Require(LanChatChannel.Clean("  привет   мир  ") == "привет мир", "Spaces not collapsed");
            Require(LanChatChannel.Clean("a\r\nb\tc\u2028d\u2029e\u00A0f") == "a b c d e f", "Line breaks not flattened");
            Require(LanChatChannel.Clean("ab\u202Ecd\u202Ae\u2066f\u200B\u200D\uFEFF\u00AD") == "abcdef", "Format characters kept");
            Require(LanChatChannel.Clean("x\uE000\uF8FFy") == "xy", "Private-use characters kept");
            Require(LanChatChannel.Clean("a\uD800b\uDC00c") == "abc", "Lone surrogates kept");
            Require(LanChatChannel.Clean("ok 😀") == "ok 😀", "Emoji dropped");
            Require(LanChatChannel.Clean("e\u0301\u0302\u0303\u0304x") == "e\u0301\u0302x", "Combining run not limited");
            Require(LanChatChannel.Clean("\u0301\u0302a") == "a" && LanChatChannel.Clean("a \u0301b") == "a b", "Leading combining mark kept");
            Require(LanChatChannel.Clean("<b>жирно</b>") == "<b>жирно</b>", "Markup altered (it is shown as text)");
            Require(LanChatChannel.Clean(new string('w', 500)).Length == LanChatChannel.MaxChars, "Long line not cut");
            string pairs = LanChatChannel.Clean(new string('a', LanChatChannel.MaxChars - 1) + "😀");
            Require(pairs.Length == LanChatChannel.MaxChars - 1, "Surrogate pair split at the limit");
            Require(LanChatChannel.Clean("\u0000\u0001 \u202E ") == "" && LanChatChannel.Clean(null) == "", "Nothing-but-invisible line kept");

            // Delivery: in order, exactly once, through loss, duplicates and reordering.
            var aToB = new List<Packet>(); var bToA = new List<Packet>();
            var a = new LanChatChannel(p => aToB.Add(Wire(p))); var b = new LanChatChannel(p => bToA.Add(Wire(p)));
            string text; int id;
            Require(a.Send("один", 0, out text, out id) == LanChatChannel.Submit.Sent && text == "один" && a.IsPending(id), "Send failed");
            Require(a.Send("два", 0, out text, out id) == LanChatChannel.Submit.Sent, "Second send failed");
            Require(aToB.Count == 2, "Messages not sent at once");
            aToB.RemoveAt(0); // the first is lost
            Require(b.Receive(aToB[0]) == null, "Message after a gap delivered");
            aToB.Clear(); b.Tick(0);
            Require(bToA.Count == 0, "Acknowledged nothing delivered");
            a.Tick(1); Require(aToB.Count == 0, "Repeated before the resend time");
            a.Tick(1.6); Require(aToB.Count == 2, "Not repeated");
            var delivered = new List<string>();
            foreach (var p in aToB) { var t = b.Receive(p); if (t != null) delivered.Add(t); }
            foreach (var p in aToB) Require(b.Receive(p) == null, "Duplicate delivered twice");
            Require(delivered.Count == 2 && delivered[0] == "один" && delivered[1] == "два", "Order or content wrong");
            aToB.Clear(); b.Tick(2);
            Require(bToA.Count == 1 && bToA[0].Action == LanChatChannel.Ack && bToA[0].Sequence == 2, "Cumulative acknowledgement missing");
            a.Receive(bToA[0]); bToA.Clear();
            Require(a.Pending == 0 && !a.IsPending(id), "Acknowledged message still pending");
            Require(a.Receive(ChatPacket(99, LanChatChannel.Ack, "")) == null && a.Pending == 0, "Acknowledgement of unsent messages");
            a.Tick(10); Require(aToB.Count == 0, "Delivered message repeated");
            // Typing limit and the pending cap.
            var c = new LanChatChannel(p => { });
            int sent = 0;
            for (int i = 0; i < 20; i++) if (c.Send("x" + i, 100, out text, out id) == LanChatChannel.Submit.Sent) sent++;
            Require(sent == 5, "Typing burst not limited");
            Require(c.Send("   ", 200, out text, out id) == LanChatChannel.Submit.Empty, "Empty line sent");
            for (int i = 0; i < 10; i++) c.Send("y", 200 + i * 2, out text, out id);
            Require(c.Pending == LanChatChannel.MaxPending && c.Send("z", 300, out text, out id) == LanChatChannel.Submit.Full, "Pending queue not capped");
            // A new session numbers from 1 and keeps undelivered lines.
            c.Reset(); var resent = new List<Packet>(); var d = new LanChatChannel(p => resent.Add(p));
            d.Send("после", 0, out text, out id); d.Reset(); resent.Clear(); d.Tick(5);
            Require(resent.Count == 1 && resent[0].Sequence == 1 && resent[0].Text == "после", "Reset lost or misnumbered a line");

            // The sender never exceeds the receiver's network budget, even when typing as fast as possible.
            var limits = new LanMessageLimits(); int packets = 0, refused = 0;
            var fast = new LanChatChannel(p => { packets++; if (!limits.Take(PacketKind.Chat, _chatNow)) refused++; });
            for (int step = 0; step < 6000; step++)
            {
                _chatNow = step * 0.01;
                fast.Send("flood " + step, _chatNow, out text, out id);
                fast.Receive(ChatPacket((uint)step + 1, 0, "in")); // the partner floods too: acknowledgements
                fast.Tick(_chatNow);
            }
            Require(packets > 100 && refused == 0, "Chat sender exceeded the peer's limit (" + refused + " of " + packets + ")");

            int fuzzed = FuzzChat(new Random(55021));
            Console.WriteLine("PASS: chat (wire format, cleaning, in-order delivery through loss/duplicates, acknowledgements, typing and wire budgets, " + fuzzed + " fuzzed inputs)");
        }
        private static double _chatNow;

        private static Packet Wire(Packet p)
        {
            p.GameVersion = "checks"; p.Session = 42;
            Packet parsed;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(p), out parsed), "Channel produced an invalid packet");
            return parsed;
        }

        private static byte[] RawChat(uint sequence, byte action, byte[] body)
        {
            var template = LanProtocol.Encode(ChatPacket(sequence, 1, ""));
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write(template, 0, template.Length - 7);
                w.Write(sequence); w.Write(action); w.Write((ushort)body.Length); w.Write(body);
                return s.ToArray();
            }
        }

        private static int FuzzChat(Random random)
        {
            int inputs = 0;
            var valid = LanProtocol.Encode(ChatPacket(3, 0, "проверка связи"));
            Packet parsed;
            for (int i = 0; i < 40000; i++, inputs++)
            {
                var bytes = (byte[])valid.Clone();
                int flips = random.Next(1, 6);
                for (int f = 0; f < flips; f++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                if (LanProtocol.TryDecode(bytes, out parsed) && parsed.Kind == PacketKind.Chat)
                    Require(LanProtocol.ValidChat(parsed.Sequence, parsed.Action, parsed.Text), "Decoded chat outside its limits");
            }
            for (int i = 0; i < 40000; i++, inputs++)
            {
                var chars = new char[random.Next(0, 400)];
                for (int k = 0; k < chars.Length; k++)
                    chars[k] = random.Next(4) == 0 ? (char)random.Next(0x10000) : random.Next(3) == 0 ? (char)random.Next(0xD800, 0xE000) : (char)random.Next(0x20, 0x500);
                string clean = LanChatChannel.Clean(new string(chars));
                Require(clean.Length <= LanChatChannel.MaxChars && LanChatChannel.Clean(clean) == clean, "Clean not bounded or not stable");
                Require(clean.Length == 0 || LanProtocol.ValidChat(1, 0, clean), "Clean produced a line the wire rejects");
                for (int k = 0; k < clean.Length; k++)
                {
                    if (char.IsHighSurrogate(clean[k])) { Require(k + 1 < clean.Length && char.IsLowSurrogate(clean[k + 1]), "Lone surrogate after Clean"); k++; continue; }
                    Require(!char.IsLowSurrogate(clean[k]), "Lone surrogate after Clean");
                    var category = CharUnicodeInfo.GetUnicodeCategory(clean[k]);
                    Require(category != UnicodeCategory.Format && category != UnicodeCategory.Control && category != UnicodeCategory.PrivateUse &&
                        category != UnicodeCategory.OtherNotAssigned && category != UnicodeCategory.LineSeparator && category != UnicodeCategory.ParagraphSeparator, "Invisible character after Clean");
                }
                if (clean.Length != 0) Require(LanProtocol.TryDecode(LanProtocol.Encode(ChatPacket(1, 0, clean)), out parsed) && parsed.Text == clean, "Clean line does not survive the wire");
            }
            return inputs;
        }
    }
}
