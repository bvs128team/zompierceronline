using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// Copy of ZompiercerRelay/client/RelayClient.cs (protocol v3, see ZompiercerRelay/PROTOCOL.md).
// Worker-only: never compiled into the Unity assembly. Keep in sync with the relay.
// Written for C# 7.3 / .NET Framework 4.7.2 so it can be used by the mod's
// network worker. The control channel is TLS with a pinned server certificate;
// UDP datagrams are authenticated per hop. End-to-end protection of the game
// stays the mod's J-PAKE + DTLS, carried through the relay as opaque bytes.
namespace ZompiercerLAN.Relay
{
    internal static class RelayProtocol
    {
        public const uint Magic = 0x5A524C59;
        public const byte Version = 3;
        public const int FrameHeader = 3, MaxFramePayload = 4096;
        public const int RoomIdLength = 8, PeerIdLength = 8, SecretLength = 32, TicketLength = 16;
        public const int UdpHeader = 22, UdpTag = 16, UdpOverhead = UdpHeader + UdpTag, BindPayload = 25, PathTokenLength = 16;
    }

    internal static class ClientMessage
    {
        public const byte Hello = 0x01, CreateRoom = 0x02, JoinRoom = 0x03, RejoinRoom = 0x04;
        public const byte OpenRoom = 0x05, LockRoom = 0x06, KickGuest = 0x07, Data = 0x08, Ping = 0x09;
        public const byte PublishRoom = 0x0A, ListRooms = 0x0B; // relay 1.4.0
    }

    internal static class ServerMessage
    {
        public const byte Welcome = 0x81, RoomCreated = 0x82, Joined = 0x83, PeerJoined = 0x84, PeerLeft = 0x85;
        public const byte RoomClosed = 0x86, RoomState = 0x87, Data = 0x88, Pong = 0x89, Error = 0x8F;
        public const byte RoomList = 0x8A, Published = 0x8B; // relay 1.4.0
    }

    internal static class RelayError
    {
        public const byte BadFrame = 1, UnsupportedVersion = 2, BadState = 3, RoomNotFound = 4, RoomBusy = 5;
        public const byte AppMismatch = 6, RateLimited = 7, ServerFull = 8, BadTicket = 9, Timeout = 10;
        public const byte TooManyRooms = 11, AccessDenied = 12;

        public static string Describe(byte code)
        {
            switch (code)
            {
                case BadFrame: return "Сервер отклонил сообщение";
                case UnsupportedVersion: return "Версия протокола сервера не поддерживается";
                case BadState: return "Недопустимая команда";
                case RoomNotFound: return "Комната не найдена";
                case RoomBusy: return "Комната закрыта для входа или занята";
                case AppMismatch: return "Версии мода у игроков или на сервере не совпадают";
                case RateLimited: return "Слишком много попыток, подождите";
                case ServerFull: return "Сервер переполнен";
                case BadTicket: return "Повторный вход отклонён";
                case Timeout: return "Сервер закрыл соединение по тайм-ауту";
                case TooManyRooms: return "Слишком много комнат с вашего адреса";
                case AccessDenied: return "Неверный ключ доступа к серверу";
                default: return "Ошибка сервера " + code;
            }
        }
    }

    internal static class UdpKind
    {
        public const byte Data = 1, Bind = 2, BindAck = 3;
    }

    // BIND_ACK flags (protocol 3). Challenge: the relay has not validated the
    // address this datagram was sent to; echo the token in the next BIND.
    internal static class BindAckFlags
    {
        public const byte PeerBound = 1, PathValidated = 2, Challenge = 4;
    }

    internal sealed class RelayException : Exception
    {
        public byte Code { get; private set; }
        public RelayException(byte code) : base(RelayError.Describe(code)) { Code = code; }
    }

    internal sealed class RelayMessage
    {
        public byte Type;
        public byte[] Payload;
    }

    internal sealed class RelayWelcome
    {
        public int UdpPort, MaxUdpPayload, KeepaliveSeconds, JoinWindowSeconds, MaxJoinAttempts, RejoinGraceSeconds;
    }

    // The public room list (relay 1.4.0): rooms of this application open for a guest.
    internal sealed class RelayListedRoom
    {
        public string RoomId;
        public string Name;
    }

    // PUBLISHED status (relay 1.4.0).
    internal static class PublishStatus
    {
        public const byte Listed = 0, TooMany = 1, Disabled = 2;
        public const int MaxNameBytes = 32;
    }

    // One side of a room. Keep it for the whole session: a rejoin returns the
    // same PeerId/Secret, and the existing RelayUdpSession (with its counters)
    // must continue to be used.
    internal sealed class RelaySeat
    {
        public string RoomId;
        public ulong PeerId;     // public UDP routing id
        public byte[] Secret;    // UDP key material; never log or persist
        public byte[] Ticket;    // rejoin capability; replaced on every rejoin
        public bool IsHost;
        public bool Rejoined;

        public RelayUdpSession CreateUdpSession() { return new RelayUdpSession(PeerId, Secret); }

        public void Clear()
        {
            if (Secret != null) Array.Clear(Secret, 0, Secret.Length);
            if (Ticket != null) Array.Clear(Ticket, 0, Ticket.Length);
        }
    }

    // Room ids use Crockford base32 (no I, L, O, U). Shown to players as XXXX-XXXX.
    internal static class RoomCode
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        // Returns the canonical 8-symbol id or null. Accepts lower case, spaces,
        // dashes and the usual look-alikes (O->0, I/L->1).
        public static string Normalize(string input)
        {
            if (input == null) return null;
            var result = new StringBuilder(RelayProtocol.RoomIdLength);
            foreach (char raw in input)
            {
                if (raw == '-' || raw == ' ') continue;
                char c = char.ToUpperInvariant(raw);
                if (c == 'O') c = '0';
                else if (c == 'I' || c == 'L') c = '1';
                if (Alphabet.IndexOf(c) < 0 || result.Length == RelayProtocol.RoomIdLength) return null;
                result.Append(c);
            }
            return result.Length == RelayProtocol.RoomIdLength ? result.ToString() : null;
        }

        public static string Format(string id)
        {
            return id != null && id.Length == RelayProtocol.RoomIdLength ? id.Substring(0, 4) + "-" + id.Substring(4) : id;
        }
    }

    // Sliding 64-packet anti-replay window. Counters start at 1.
    internal sealed class ReplayWindow
    {
        private ulong _highest, _seen;

        public bool Accept(ulong counter)
        {
            if (counter == 0) return false;
            if (counter > _highest)
            {
                ulong shift = counter - _highest;
                _seen = shift >= 64 ? 0 : _seen << (int)shift;
                _seen |= 1;
                _highest = counter;
                return true;
            }
            ulong age = _highest - counter;
            if (age >= 64) return false;
            ulong bit = 1UL << (int)age;
            if ((_seen & bit) != 0) return false;
            _seen |= bit;
            return true;
        }
    }

    // Authenticated UDP datagrams for one seat. Seal* methods and TryOpen may run
    // on different threads (separate HMAC instances), but each one must not be
    // called concurrently with itself.
    internal sealed class RelayUdpSession : IDisposable
    {
        private readonly ulong _peerId;
        private readonly HMACSHA256 _up, _down;
        private readonly ReplayWindow _replay = new ReplayWindow();
        private ulong _counter;

        public RelayUdpSession(ulong peerId, byte[] secret)
        {
            if (secret == null || secret.Length != RelayProtocol.SecretLength) throw new ArgumentException("secret");
            _peerId = peerId;
            using (var kdf = new HMACSHA256(secret))
            {
                var up = kdf.ComputeHash(Encoding.ASCII.GetBytes("ZRLY2 client-to-relay"));
                var down = kdf.ComputeHash(Encoding.ASCII.GetBytes("ZRLY2 relay-to-client"));
                _up = new HMACSHA256(up);
                _down = new HMACSHA256(down);
                Array.Clear(up, 0, up.Length);
                Array.Clear(down, 0, down.Length);
            }
        }

        public byte[] SealData(byte[] payload, int offset, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException("count");
            var datagram = Header(UdpKind.Data, count);
            Buffer.BlockCopy(payload, offset, datagram, RelayProtocol.UdpHeader, count);
            Seal(datagram);
            return datagram;
        }

        // pathToken: the token of the latest challenge from the relay (or null).
        // Sending it from the challenged address proves the address is ours.
        public byte[] SealBind(ulong nonce, byte[] pathToken = null)
        {
            if (pathToken != null && pathToken.Length != RelayProtocol.PathTokenLength) throw new ArgumentException("pathToken");
            var datagram = Header(UdpKind.Bind, RelayProtocol.BindPayload);
            WriteU64(datagram, RelayProtocol.UdpHeader, nonce);
            if (pathToken != null) Buffer.BlockCopy(pathToken, 0, datagram, RelayProtocol.UdpHeader + 9, RelayProtocol.PathTokenLength);
            Seal(datagram);
            return datagram;
        }

        // Reads an opened BIND_ACK. When the challenge flag is set, `pathToken`
        // receives the token to echo; otherwise it is left unchanged.
        public static bool ReadBindAck(byte[] buffer, int payloadOffset, int payloadLength, out ulong nonce, out byte flags, byte[] pathToken)
        {
            nonce = 0; flags = 0;
            if (buffer == null || payloadLength != RelayProtocol.BindPayload || pathToken == null || pathToken.Length != RelayProtocol.PathTokenLength) return false;
            nonce = ReadU64(buffer, payloadOffset);
            flags = buffer[payloadOffset + 8];
            if ((flags & ~(BindAckFlags.PeerBound | BindAckFlags.PathValidated | BindAckFlags.Challenge)) != 0) return false;
            if ((flags & BindAckFlags.Challenge) != 0)
                Buffer.BlockCopy(buffer, payloadOffset + 9, pathToken, 0, RelayProtocol.PathTokenLength);
            return true;
        }

        // Accepts only datagrams sealed by the relay for this seat, each at most once.
        public bool TryOpen(byte[] buffer, int length, out byte kind, out int payloadOffset, out int payloadLength)
        {
            kind = 0; payloadOffset = RelayProtocol.UdpHeader; payloadLength = 0;
            if (buffer == null || length < RelayProtocol.UdpOverhead + 1 || length > buffer.Length) return false;
            if (RelayDatagram.ReadU32(buffer, 0) != RelayProtocol.Magic || buffer[4] != RelayProtocol.Version) return false;
            if (ReadU64(buffer, 6) != _peerId) return false;
            var mac = _down.ComputeHash(buffer, 0, length - RelayProtocol.UdpTag);
            int diff = 0;
            for (int i = 0; i < RelayProtocol.UdpTag; i++) diff |= mac[i] ^ buffer[length - RelayProtocol.UdpTag + i];
            if (diff != 0) return false;
            if (!_replay.Accept(ReadU64(buffer, 14))) return false;
            kind = buffer[5];
            payloadLength = length - RelayProtocol.UdpOverhead;
            return true;
        }

        public void Dispose()
        {
            _up.Dispose();
            _down.Dispose();
        }

        private byte[] Header(byte kind, int payload)
        {
            var datagram = new byte[RelayProtocol.UdpOverhead + payload];
            RelayDatagram.WriteU32(datagram, 0, RelayProtocol.Magic);
            datagram[4] = RelayProtocol.Version;
            datagram[5] = kind;
            WriteU64(datagram, 6, _peerId);
            WriteU64(datagram, 14, ++_counter);
            return datagram;
        }

        private void Seal(byte[] datagram)
        {
            var mac = _up.ComputeHash(datagram, 0, datagram.Length - RelayProtocol.UdpTag);
            Buffer.BlockCopy(mac, 0, datagram, datagram.Length - RelayProtocol.UdpTag, RelayProtocol.UdpTag);
        }

        internal static ulong ReadU64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = v << 8 | b[o + i];
            return v;
        }

        internal static void WriteU64(byte[] b, int o, ulong v)
        {
            for (int i = 7; i >= 0; i--) { b[o + i] = (byte)v; v >>= 8; }
        }
    }

    internal static class RelayDatagram
    {
        internal static uint ReadU32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        internal static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        // Accepts "AB:CD:..." (openssl style) or plain hex; returns 32 bytes or null.
        public static byte[] ParseFingerprint(string text)
        {
            if (text == null) return null;
            var hex = text.Replace(":", "").Replace(" ", "");
            if (hex.Length != 64) return null;
            var result = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                int hi = Nibble(hex[2 * i]), lo = Nibble(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                result[i] = (byte)(hi << 4 | lo);
            }
            return result;
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    // TLS control connection. Not thread-safe: use it from one thread, or lock.
    internal sealed class RelayConnection : IDisposable
    {
        private readonly Socket _socket;
        private readonly SslStream _tls;
        private readonly byte[] _rx = new byte[2 * (RelayProtocol.FrameHeader + RelayProtocol.MaxFramePayload)];
        private readonly byte[] _chunk = new byte[RelayProtocol.MaxFramePayload];
        private IAsyncResult _pendingRead;
        private int _rxCount;

        public RelayWelcome Welcome { get; private set; }
        public IPEndPoint Server { get; private set; }

        private RelayConnection(Socket socket, SslStream tls, IPEndPoint server) { _socket = socket; _tls = tls; Server = server; }

        // pinnedFingerprint: SHA-256 of the server certificate ("AB:CD:..." as printed
        // by the server). Strongly recommended. null = normal certificate validation
        // (only for servers with a CA-issued certificate for `host`).
        public static RelayConnection Connect(string host, int port, string application, string accessKey, string pinnedFingerprint, int timeoutMs)
        {
            byte[] pin = null;
            if (pinnedFingerprint != null && (pin = RelayDatagram.ParseFingerprint(pinnedFingerprint)) == null)
                throw new ArgumentException("Неверный отпечаток сертификата сервера");

            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                address = null;
                foreach (var candidate in Dns.GetHostAddresses(host))
                    if (candidate.AddressFamily == AddressFamily.InterNetwork) { address = candidate; break; }
                if (address == null) throw new SocketException((int)SocketError.HostNotFound);
            }
            var endpoint = new IPEndPoint(address, port);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            SslStream tls = null;
            try
            {
                var connect = socket.BeginConnect(endpoint, null, null);
                using (var wait = connect.AsyncWaitHandle)
                    if (!wait.WaitOne(timeoutMs)) throw new TimeoutException("Сервер не отвечает");
                socket.EndConnect(connect);

                socket.ReceiveTimeout = socket.SendTimeout = timeoutMs; // bounds the handshake only
                tls = new SslStream(new NetworkStream(socket, false), false,
                    (sender, certificate, chain, errors) => pin != null ? MatchesPin(certificate, pin) : errors == SslPolicyErrors.None);
                // None = the OS default protocols (TLS 1.2, and 1.3 where the OS has it).
                tls.AuthenticateAsClient(host, null, SslProtocols.None, false);
                socket.ReceiveTimeout = socket.SendTimeout = 0;

                var connection = new RelayConnection(socket, tls, endpoint);
                connection.SendHello(application, accessKey ?? "");
                var welcome = connection.Expect(ServerMessage.Welcome, timeoutMs);
                if (welcome.Length < 11 || welcome[0] != RelayProtocol.Version) throw new RelayException(RelayError.UnsupportedVersion);
                connection.Welcome = new RelayWelcome
                {
                    UdpPort = U16(welcome, 1), MaxUdpPayload = U16(welcome, 3), KeepaliveSeconds = welcome[5],
                    JoinWindowSeconds = U16(welcome, 6), MaxJoinAttempts = welcome[8], RejoinGraceSeconds = U16(welcome, 9),
                };
                return connection;
            }
            catch
            {
                if (tls != null) tls.Dispose();
                socket.Dispose();
                throw;
            }
        }

        public RelaySeat CreateRoom(int timeoutMs)
        {
            Send(ClientMessage.CreateRoom, new byte[0], 0, 0);
            var p = Expect(ServerMessage.RoomCreated, timeoutMs);
            if (p.Length != RelayProtocol.RoomIdLength + RelayProtocol.PeerIdLength + RelayProtocol.SecretLength + RelayProtocol.TicketLength)
                throw new RelayException(RelayError.BadFrame);
            var seat = ReadSeat(p, RelayProtocol.RoomIdLength);
            seat.RoomId = Encoding.ASCII.GetString(p, 0, RelayProtocol.RoomIdLength);
            seat.IsHost = true;
            Array.Clear(p, 0, p.Length);
            return seat;
        }

        public RelaySeat JoinRoom(string roomId, int timeoutMs)
        {
            string id = RoomCode.Normalize(roomId);
            if (id == null) throw new ArgumentException("Неверный код комнаты сервера");
            var payload = Encoding.ASCII.GetBytes(id);
            Send(ClientMessage.JoinRoom, payload, 0, payload.Length);
            return ReadJoined(id, timeoutMs);
        }

        public RelaySeat RejoinRoom(string roomId, byte[] ticket, int timeoutMs)
        {
            string id = RoomCode.Normalize(roomId);
            if (id == null || ticket == null || ticket.Length != RelayProtocol.TicketLength) throw new ArgumentException("Неверные данные повторного входа");
            var payload = new byte[RelayProtocol.RoomIdLength + RelayProtocol.TicketLength];
            Encoding.ASCII.GetBytes(id, 0, id.Length, payload, 0);
            Buffer.BlockCopy(ticket, 0, payload, RelayProtocol.RoomIdLength, RelayProtocol.TicketLength);
            Send(ClientMessage.RejoinRoom, payload, 0, payload.Length);
            Array.Clear(payload, 0, payload.Length);
            return ReadJoined(id, timeoutMs);
        }

        // Host in a room: list it under `name` (1..32 UTF-8 bytes; letters, digits, single
        // spaces, '-', '_'); an empty name unlists it. Returns a PublishStatus value.
        public byte PublishRoom(string name, int timeoutMs)
        {
            var bytes = Encoding.UTF8.GetBytes(name ?? "");
            if (bytes.Length > PublishStatus.MaxNameBytes) throw new ArgumentException("Public room name too long");
            var payload = new byte[1 + bytes.Length];
            payload[0] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, payload, 1, bytes.Length);
            Send(ClientMessage.PublishRoom, payload, 0, payload.Length);
            var p = Expect(ServerMessage.Published, timeoutMs);
            if (p.Length != 1 || p[0] > PublishStatus.Disabled) throw new RelayException(RelayError.BadFrame);
            return p[0];
        }

        // Before entering a room: one page (20 rooms) of the public list, newest first.
        // Names are returned as sent by the server; callers validate them before showing.
        public RelayListedRoom[] ListRooms(int page, int timeoutMs, out int total)
        {
            if (page < 0 || page > 255) throw new ArgumentOutOfRangeException("page");
            Send(ClientMessage.ListRooms, new[] { (byte)page }, 0, 1);
            var p = Expect(ServerMessage.RoomList, timeoutMs);
            if (p.Length < 3) throw new RelayException(RelayError.BadFrame);
            total = U16(p, 0);
            int count = p[2], at = 3;
            if (count > 20) throw new RelayException(RelayError.BadFrame);
            var rooms = new RelayListedRoom[count];
            var strict = new UTF8Encoding(false, true);
            for (int i = 0; i < count; i++)
            {
                if (at + RelayProtocol.RoomIdLength + 1 > p.Length) throw new RelayException(RelayError.BadFrame);
                string id = Encoding.ASCII.GetString(p, at, RelayProtocol.RoomIdLength);
                int length = p[at + RelayProtocol.RoomIdLength];
                at += RelayProtocol.RoomIdLength + 1;
                if (RoomCode.Normalize(id) != id || length < 1 || length > PublishStatus.MaxNameBytes || at + length > p.Length) throw new RelayException(RelayError.BadFrame);
                string name;
                try { name = strict.GetString(p, at, length); }
                catch (ArgumentException) { throw new RelayException(RelayError.BadFrame); }
                at += length;
                rooms[i] = new RelayListedRoom { RoomId = id, Name = name };
            }
            if (at != p.Length) throw new RelayException(RelayError.BadFrame);
            return rooms;
        }

        public void SendData(byte[] data, int offset, int count)
        {
            if (count < 1 || count > RelayProtocol.MaxFramePayload) throw new ArgumentOutOfRangeException("count");
            Send(ClientMessage.Data, data, offset, count);
        }

        public void Ping(uint nonce)
        {
            var p = new byte[4];
            RelayDatagram.WriteU32(p, 0, nonce);
            Send(ClientMessage.Ping, p, 0, 4);
        }

        public void Send(byte type, byte[] payload, int offset, int count)
        {
            if (count < 0 || count > RelayProtocol.MaxFramePayload) throw new ArgumentOutOfRangeException("count");
            var frame = new byte[RelayProtocol.FrameHeader + count];
            frame[0] = (byte)(count >> 8); frame[1] = (byte)count; frame[2] = type;
            if (count > 0) Buffer.BlockCopy(payload, offset, frame, RelayProtocol.FrameHeader, count);
            _tls.Write(frame, 0, frame.Length);
            Array.Clear(frame, 0, frame.Length);
        }

        // Next frame, or null on timeout (0 = check without waiting). Throws when
        // the server closed the connection. One TLS read stays outstanding across
        // calls, so a timeout never corrupts the TLS stream.
        public RelayMessage Receive(int timeoutMs)
        {
            int deadline = Environment.TickCount + Math.Max(0, timeoutMs);
            while (true)
            {
                var message = TryTakeFrame();
                if (message != null) return message;
                if (_pendingRead == null) _pendingRead = _tls.BeginRead(_chunk, 0, _chunk.Length, null, null);
                int left = Math.Max(0, deadline - Environment.TickCount);
                if (!_pendingRead.IsCompleted && !_pendingRead.AsyncWaitHandle.WaitOne(left)) return null;
                var completed = _pendingRead;
                _pendingRead = null;
                int read = _tls.EndRead(completed);
                if (read == 0) throw new System.IO.EndOfStreamException("Сервер закрыл соединение");
                Buffer.BlockCopy(_chunk, 0, _rx, _rxCount, read);
                _rxCount += read;
            }
        }

        public void Dispose()
        {
            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            _tls.Dispose();
            _socket.Dispose();
            Array.Clear(_rx, 0, _rx.Length);
            Array.Clear(_chunk, 0, _chunk.Length);
        }

        private static bool MatchesPin(X509Certificate certificate, byte[] pin)
        {
            if (certificate == null) return false;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(certificate.GetRawCertData());
            int diff = 0;
            for (int i = 0; i < hash.Length; i++) diff |= hash[i] ^ pin[i];
            return diff == 0;
        }

        private RelaySeat ReadJoined(string id, int timeoutMs)
        {
            var p = Expect(ServerMessage.Joined, timeoutMs);
            if (p.Length != RelayProtocol.PeerIdLength + RelayProtocol.SecretLength + RelayProtocol.TicketLength + 1)
                throw new RelayException(RelayError.BadFrame);
            var seat = ReadSeat(p, 0);
            seat.RoomId = id;
            seat.Rejoined = (p[p.Length - 1] & 1) != 0;
            seat.IsHost = (p[p.Length - 1] & 2) != 0;
            Array.Clear(p, 0, p.Length);
            return seat;
        }

        private static RelaySeat ReadSeat(byte[] p, int o)
        {
            return new RelaySeat
            {
                PeerId = RelayUdpSession.ReadU64(p, o),
                Secret = Slice(p, o + RelayProtocol.PeerIdLength, RelayProtocol.SecretLength),
                Ticket = Slice(p, o + RelayProtocol.PeerIdLength + RelayProtocol.SecretLength, RelayProtocol.TicketLength),
            };
        }

        private void SendHello(string application, string accessKey)
        {
            var app = Encoding.ASCII.GetBytes(application ?? "");
            var key = Encoding.UTF8.GetBytes(accessKey);
            if (app.Length < 1 || app.Length > 64 || key.Length > 64) throw new ArgumentException("application/accessKey length");
            var p = new byte[7 + app.Length + key.Length];
            RelayDatagram.WriteU32(p, 0, RelayProtocol.Magic);
            p[4] = RelayProtocol.Version;
            p[5] = (byte)app.Length;
            Buffer.BlockCopy(app, 0, p, 6, app.Length);
            p[6 + app.Length] = (byte)key.Length;
            Buffer.BlockCopy(key, 0, p, 7 + app.Length, key.Length);
            Send(ClientMessage.Hello, p, 0, p.Length);
            Array.Clear(p, 0, p.Length);
            Array.Clear(key, 0, key.Length);
        }

        private byte[] Expect(byte type, int timeoutMs)
        {
            var message = Receive(timeoutMs);
            if (message == null) throw new TimeoutException("Сервер не ответил вовремя");
            if (message.Type == ServerMessage.Error) throw new RelayException(message.Payload.Length > 0 ? message.Payload[0] : (byte)0);
            if (message.Type != type) throw new RelayException(RelayError.BadState);
            return message.Payload;
        }

        private RelayMessage TryTakeFrame()
        {
            if (_rxCount < RelayProtocol.FrameHeader) return null;
            int length = _rx[0] << 8 | _rx[1];
            if (length > RelayProtocol.MaxFramePayload) throw new RelayException(RelayError.BadFrame);
            int total = RelayProtocol.FrameHeader + length;
            if (_rxCount < total) return null;
            var message = new RelayMessage { Type = _rx[2], Payload = Slice(_rx, RelayProtocol.FrameHeader, length) };
            Buffer.BlockCopy(_rx, total, _rx, 0, _rxCount - total);
            _rxCount -= total;
            Array.Clear(_rx, _rxCount, total);
            return message;
        }

        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }
        private static byte[] Slice(byte[] b, int o, int n) { var r = new byte[n]; Buffer.BlockCopy(b, o, r, 0, n); return r; }
    }
}
