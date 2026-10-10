using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZompiercerLAN
{
    // Paint, wires and sign text of one object of the host's train (1.4.0), as the train layout
    // carries it: painted surfaces (the object's Paintables by index) with their material and
    // colour, the sign's text, and the inputs fed by the switch of a part (by its host id).
    // No Unity APIs in this file.
    internal sealed class TrainDecor
    {
        internal const int MaxPaint = 32, MaxWires = 16, MaxMaterialBytes = 4 * LanStorage.MaxMaterialChars, MaxSignBytes = 4 * LanStorage.MaxSignChars;
        // 1.4.1: the most one encoded record may take (the layout gives each its length).
        internal const int MaxBytes = 1 + 1 + MaxPaint * (5 + 2 + MaxMaterialBytes) + 2 + MaxSignBytes + 1 + MaxWires * 5;
        private const byte HasPaint = 1, HasSign = 2, HasWires = 4;
        internal sealed class Paint { internal int Index; internal byte R, G, B, A; internal string Material; }
        internal sealed class Wire { internal int Input; internal int Output; }
        internal readonly List<Paint> Paints = new List<Paint>();
        internal string Sign; // null: the object has no sign
        internal readonly List<Wire> Wires = new List<Wire>();
        internal bool Empty { get { return Paints.Count == 0 && Sign == null && Wires.Count == 0; } }
        internal Paint PaintOf(int index) { foreach (var p in Paints) if (p.Index == index) return p; return null; }
        internal Wire WireOf(int input) { foreach (var w in Wires) if (w.Input == input) return w; return null; }

        internal byte[] Encode()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                Write(writer);
                return stream.ToArray();
            }
        }

        internal void Write(BinaryWriter writer)
        {
            if (Paints.Count > MaxPaint || Wires.Count > MaxWires) throw new InvalidOperationException("Train decor too large");
            writer.Write((byte)((Paints.Count > 0 ? HasPaint : 0) | (Sign != null ? HasSign : 0) | (Wires.Count > 0 ? HasWires : 0)));
            if (Paints.Count > 0)
            {
                writer.Write((byte)Paints.Count);
                foreach (var p in Paints)
                {
                    writer.Write((byte)p.Index); writer.Write(p.R); writer.Write(p.G); writer.Write(p.B); writer.Write(p.A);
                    WriteText(writer, p.Material, MaxMaterialBytes);
                }
            }
            if (Sign != null) WriteText(writer, Sign, MaxSignBytes);
            if (Wires.Count > 0)
            {
                writer.Write((byte)Wires.Count);
                foreach (var w in Wires) { writer.Write((byte)w.Input); writer.Write(w.Output); }
            }
        }

        // Everything as the host writes it: indices in range and once each, material names and sign
        // texts as LanStorage cleans them, switches by a non-zero host id.
        internal static TrainDecor Read(BinaryReader reader)
        {
            var d = new TrainDecor();
            byte flags = reader.ReadByte();
            if (flags == 0 || flags > (HasPaint | HasSign | HasWires)) throw new InvalidDataException("Train decor flags");
            if ((flags & HasPaint) != 0)
            {
                int count = reader.ReadByte();
                if (count < 1 || count > MaxPaint) throw new InvalidDataException("Train decor paint count");
                var seen = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    var p = new Paint { Index = reader.ReadByte(), R = reader.ReadByte(), G = reader.ReadByte(), B = reader.ReadByte(), A = reader.ReadByte(), Material = ReadText(reader, MaxMaterialBytes) };
                    if (p.Index > LanStorage.MaxDecorIndex || !seen.Add(p.Index)) throw new InvalidDataException("Train decor paint index");
                    if (p.Material.Length == 0 || p.Material.Length > LanStorage.MaxMaterialChars || p.Material != LanStorage.CleanMaterial(p.Material)) throw new InvalidDataException("Train decor material");
                    d.Paints.Add(p);
                }
            }
            if ((flags & HasSign) != 0)
            {
                d.Sign = ReadText(reader, MaxSignBytes);
                if (d.Sign != LanStorage.CleanSign(d.Sign)) throw new InvalidDataException("Train decor sign");
            }
            if ((flags & HasWires) != 0)
            {
                int count = reader.ReadByte();
                if (count < 1 || count > MaxWires) throw new InvalidDataException("Train decor wire count");
                var seen = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    var w = new Wire { Input = reader.ReadByte(), Output = reader.ReadInt32() };
                    if (w.Input > LanStorage.MaxDecorIndex || !seen.Add(w.Input) || w.Output == 0) throw new InvalidDataException("Train decor wire");
                    d.Wires.Add(w);
                }
            }
            return d;
        }
        internal static TrainDecor Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new InvalidDataException("Train decor size");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                TrainDecor d;
                try { d = Read(reader); }
                catch (EndOfStreamException) { throw new InvalidDataException("Train decor truncated"); }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing train decor data");
                return d;
            }
        }

        private static void WriteText(BinaryWriter writer, string text, int max)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            if (bytes.Length > max) throw new InvalidOperationException("Train decor text too long");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
        private static string ReadText(BinaryReader reader, int max)
        {
            int length = reader.ReadUInt16();
            if (length > max) throw new InvalidDataException("Train decor text length");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            try { return Strict.GetString(bytes); }
            catch (ArgumentException) { throw new InvalidDataException("Train decor text"); }
        }
    }
}
