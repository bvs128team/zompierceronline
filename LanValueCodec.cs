using System;
using System.IO;
using System.Text;

namespace ZompiercerLAN
{
    // Deliberately closed primitive grammar. Never uses CLR names, reflection or a general serializer.
    internal static class LanValueCodec
    {
        internal const int MaxBytes = 256 * 1024;
        internal const int MaxDepth = 12;
        internal const int MaxNodes = 8192;
        internal const int MaxArray = 2048;
        internal const int MaxStringBytes = 128;
        internal const int MaxBlobBytes = 64 * 1024; // byte[] leaves: one stored guest profile each
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(object[] value)
        {
            if (value == null) throw new InvalidDataException("A root array is required.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                int nodes = 0;
                Write(writer, value, 0, ref nodes);
                return stream.ToArray();
            }
        }

        internal static object[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes)
                throw new InvalidDataException("Invalid encoded length.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                int nodes = 0;
                object value = Read(reader, 0, ref nodes);
                if (!(value is object[]) || stream.Position != stream.Length)
                    throw new InvalidDataException("Root array or end of message is invalid.");
                return (object[])value;
            }
        }

        private static void Count(int depth, ref int nodes)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes) throw new InvalidDataException("Value tree is too large.");
        }

        private static void Float(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100000000f)
                throw new InvalidDataException("Invalid floating point value.");
        }

        private static void Write(BinaryWriter writer, object value, int depth, ref int nodes)
        {
            Count(depth, ref nodes);
            if (value == null) writer.Write((byte)0);
            else if (value.GetType() == typeof(int)) { writer.Write((byte)1); writer.Write((int)value); }
            else if (value.GetType() == typeof(float)) { Float((float)value); writer.Write((byte)2); writer.Write((float)value); }
            else if (value.GetType() == typeof(bool)) { writer.Write((byte)3); writer.Write((byte)((bool)value ? 1 : 0)); }
            else if (value.GetType() == typeof(string))
            {
                var s = (string)value;
                if (s.Length > MaxStringBytes) throw new InvalidDataException("String is too long.");
                byte[] data = Utf8.GetBytes(s);
                if (data.Length > MaxStringBytes) throw new InvalidDataException("String is too long.");
                writer.Write((byte)4); writer.Write(data.Length); writer.Write(data);
            }
            else if (value.GetType() == typeof(byte[]))
            {
                var data = (byte[])value;
                if (data.Length > MaxBlobBytes) throw new InvalidDataException("Blob is too long.");
                writer.Write((byte)6); writer.Write(data.Length); writer.Write(data);
            }
            // Any reference-element array: the game's Inventory.Save() returns object[][].
            // Decoding always yields plain object[].
            else if (value is object[])
            {
                var array = (object[])value;
                if (array.Length > MaxArray) throw new InvalidDataException("Array is too long.");
                writer.Write((byte)5); writer.Write(array.Length);
                foreach (object child in array) Write(writer, child, depth + 1, ref nodes);
            }
            else throw new InvalidDataException("Unsupported value type.");
            if (writer.BaseStream.Length > MaxBytes) throw new InvalidDataException("Encoded value is too large.");
        }

        private static object Read(BinaryReader reader, int depth, ref int nodes)
        {
            Count(depth, ref nodes);
            switch (reader.ReadByte())
            {
                case 0: return null;
                case 1: return reader.ReadInt32();
                case 2: float f = reader.ReadSingle(); Float(f); return f;
                case 3:
                    byte b = reader.ReadByte();
                    if (b > 1) throw new InvalidDataException("Invalid boolean.");
                    return b == 1;
                case 4:
                    int n = reader.ReadInt32();
                    if (n < 0 || n > MaxStringBytes || n > reader.BaseStream.Length - reader.BaseStream.Position)
                        throw new InvalidDataException("Invalid string length.");
                    return Utf8.GetString(reader.ReadBytes(n));
                case 5:
                    int count = reader.ReadInt32();
                    if (count < 0 || count > MaxArray || count > MaxNodes - nodes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                        throw new InvalidDataException("Invalid array length.");
                    var values = new object[count];
                    for (int i = 0; i < count; i++) values[i] = Read(reader, depth + 1, ref nodes);
                    return values;
                case 6:
                    int size = reader.ReadInt32();
                    if (size < 0 || size > MaxBlobBytes || size > reader.BaseStream.Length - reader.BaseStream.Position)
                        throw new InvalidDataException("Invalid blob length.");
                    return reader.ReadBytes(size);
                default: throw new InvalidDataException("Unknown value tag.");
            }
        }
    }
}
