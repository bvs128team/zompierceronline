using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace ZompiercerLAN
{
    // Library-owned J-PAKE. This adapter only bounds/encodes messages and derives
    // distinct transport/approval keys. Each instance belongs to one worker/exchange.
    internal sealed class LanPake : IDisposable
    {
        internal const int MaximumFrame = 2048;
        private readonly JPakeParticipant _participant;
        private readonly string _remoteId;
        private readonly bool _host;
        private readonly byte[] _context;
        private byte[] _local1, _remote1, _local2, _remote2;
        private BigInteger _material;
        private bool _confirmed;

        // binding: null for LAN (v1 context, six-digit PIN only). Relay rooms pass a
        // 32-byte digest of public room data (the room name), so a relay that
        // alters it breaks the mutual confirmation instead of misleading a player.
        internal LanPake(bool host, byte[] room, byte[] clientNonce, string pin, byte[] binding = null)
        {
            bool secret = binding == null ? ValidPin(pin) : LanRelayText.ValidSecret(pin);
            if (!secret || room == null || room.Length != 16 || clientNonce == null || clientNonce.Length != 16 ||
                binding != null && binding.Length != 32)
                throw new ArgumentException("Invalid pairing context");
            _host = host;
            string context = binding == null ? "ZompiercerLAN JPAKE v1:" + Hex(room) + ":" + Hex(clientNonce)
                : "ZompiercerLAN JPAKE v2 relay:" + Hex(room) + ":" + Hex(clientNonce) + ":" + Hex(binding);
            _context = Encoding.ASCII.GetBytes(context);
            _remoteId = context + (host ? ":client" : ":host");
            char[] password = pin.ToCharArray();
            try { _participant = new JPakeParticipant(context + (host ? ":host" : ":client"), password); }
            finally { Array.Clear(password, 0, password.Length); }
        }
        internal static bool ValidPin(string pin)
        {
            if (pin == null || pin.Length != 6) return false;
            foreach (char c in pin) if (c < '0' || c > '9') return false;
            return true;
        }
        internal static string CreatePin()
        {
            var bytes = new byte[4];
            try
            {
                using (var random = RandomNumberGenerator.Create())
                    while (true)
                    {
                        random.GetBytes(bytes); uint value = BitConverter.ToUInt32(bytes, 0);
                        // Rejection sampling, including six-digit codes with leading zeros.
                        if (value < 4294000000U) return (value % 1000000U).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                    }
            }
            finally { Clear(bytes); }
        }
        internal byte[] Round1()
        {
            var p = _participant.CreateRound1PayloadToSend();
            var proof1 = p.KnowledgeProofForX1; var proof2 = p.KnowledgeProofForX2;
            _local1 = Encode(p.Gx1, p.Gx2, proof1[0], proof1[1], proof2[0], proof2[1]);
            return (byte[])_local1.Clone();
        }
        internal void AcceptRound1(byte[] bytes)
        {
            var values = Decode(bytes, false, false, false, true, false, true);
            _participant.ValidateRound1PayloadReceived(new JPakeRound1Payload(_remoteId, values[0], values[1],
                new[] { values[2], values[3] }, new[] { values[4], values[5] }));
            _remote1 = (byte[])bytes.Clone();
        }
        internal byte[] Round2()
        {
            var p = _participant.CreateRound2PayloadToSend(); var proof = p.KnowledgeProofForX2s;
            _local2 = Encode(p.A, proof[0], proof[1]); return (byte[])_local2.Clone();
        }
        internal void AcceptRound2(byte[] bytes)
        {
            var values = Decode(bytes, false, false, true);
            _participant.ValidateRound2PayloadReceived(new JPakeRound2Payload(_remoteId, values[0], new[] { values[1], values[2] }));
            _remote2 = (byte[])bytes.Clone();
            _material = _participant.CalculateKeyingMaterial();
        }
        internal byte[] Round3()
        {
            return _participant.CreateRound3PayloadToSend(_material).MacTag.ToByteArray();
        }
        internal void AcceptRound3(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > 32) throw new InvalidDataException("Invalid PAKE confirmation size");
            var value = new BigInteger(bytes);
            if (!Arrays.AreEqual(value.ToByteArray(), bytes)) throw new InvalidDataException("Noncanonical PAKE confirmation");
            _participant.ValidateRound3PayloadReceived(new JPakeRound3Payload(_remoteId, value), _material);
            _confirmed = true;
        }
        internal Keys DeriveKeys()
        {
            if (!_confirmed) throw new InvalidOperationException("PAKE is not mutually confirmed");
            byte[] salt, material = _material.ToByteArrayUnsigned(), output = new byte[64];
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var bytes in new[] { _context, _host ? _local1 : _remote1, _host ? _remote1 : _local1,
                    _host ? _local2 : _remote2, _host ? _remote2 : _local2 })
                { writer.Write(bytes.Length); writer.Write(bytes); }
                using (var sha = SHA256.Create()) salt = sha.ComputeHash(stream.ToArray());
            }
            try
            {
                var hkdf = new HkdfBytesGenerator(new Sha256Digest());
                hkdf.Init(new HkdfParameters(material, salt, Encoding.ASCII.GetBytes("ZompiercerLAN paired DTLS and approval v1")));
                hkdf.GenerateBytes(output, 0, output.Length);
                return new Keys(output);
            }
            finally { Clear(material); Clear(salt); Clear(output); _material = null; }
        }
        private static byte[] Encode(params BigInteger[] values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var value in values)
                {
                    var bytes = value.ToByteArrayUnsigned();
                    if (bytes.Length == 0) bytes = new byte[1];
                    writer.Write((ushort)bytes.Length); writer.Write(bytes);
                }
                if (stream.Length > MaximumFrame) throw new InvalidDataException("PAKE frame too large");
                return stream.ToArray();
            }
        }
        private static BigInteger[] Decode(byte[] bytes, params bool[] scalar)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > MaximumFrame) throw new InvalidDataException("PAKE frame size");
            var values = new BigInteger[scalar.Length];
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                for (int i = 0; i < values.Length; i++)
                {
                    int size = reader.ReadUInt16();
                    if (size < 1 || size > (scalar[i] ? 32 : 384) || size > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid PAKE integer size");
                    var encoded = reader.ReadBytes(size);
                    if (size > 1 && encoded[0] == 0) throw new InvalidDataException("Noncanonical PAKE integer");
                    var value = new BigInteger(1, encoded);
                    var limit = scalar[i] ? JPakePrimeOrderGroups.NIST_3072.Q : JPakePrimeOrderGroups.NIST_3072.P;
                    if (value.CompareTo(limit) >= 0 || (!scalar[i] && value.SignValue <= 0))
                        throw new InvalidDataException("PAKE integer out of range");
                    values[i] = value;
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing PAKE data");
            }
            return values;
        }
        internal sealed class Keys : IDisposable
        {
            internal readonly byte[] Secret = new byte[32], Approval = new byte[32];
            internal Keys(byte[] bytes) { Buffer.BlockCopy(bytes, 0, Secret, 0, 32); Buffer.BlockCopy(bytes, 32, Approval, 0, 32); }
            internal byte[] ApprovalTag()
            { using (var hmac = new HMACSHA256(Approval)) return hmac.ComputeHash(Encoding.ASCII.GetBytes("ZompiercerLAN host approved and DTLS ready v1")); }
            public void Dispose() { Clear(Secret); Clear(Approval); }
        }
        internal static byte[] Nonce()
        { var bytes = new byte[16]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return bytes; }
        internal static byte[] RoomNameBinding(string name)
        {
            var label = Encoding.ASCII.GetBytes("ZompiercerLAN relay room name v1\0");
            var text = new UTF8Encoding(false, true).GetBytes(name ?? "");
            var input = new byte[label.Length + text.Length];
            Buffer.BlockCopy(label, 0, input, 0, label.Length); Buffer.BlockCopy(text, 0, input, label.Length, text.Length);
            using (var sha = SHA256.Create()) return sha.ComputeHash(input);
        }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
        internal static void Clear(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        public void Dispose()
        { Clear(_context); Clear(_local1); Clear(_remote1); Clear(_local2); Clear(_remote2); _material = null; }
    }
}
