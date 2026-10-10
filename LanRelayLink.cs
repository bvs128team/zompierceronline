using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using ZompiercerLAN.Relay;

namespace ZompiercerLAN
{
    internal enum LanRelayEventKind : byte { PeerJoined = 1, PeerLeft = 2, Data = 3 }
    internal sealed class LanRelayEvent { internal LanRelayEventKind Kind; internal byte[] Data; }

    // Worker-only. One thread owns the TLS control connection to the configured
    // relay. Before pairing completes it carries the J-PAKE frames (DATA); after
    // that it only keeps the seat alive and reclaims it with the ticket after a
    // TCP outage. Nothing received here can choose a destination or run code:
    // the relay address comes from the local player, frames are size-bounded.
    internal sealed class LanRelayLink : IDisposable
    {
        // Relay "application" string: host and guest must match (protocol of this mod over the relay).
        // Since 0.9.1 it ends with the mod version (equal to LanPlugin.PluginVersion; package-release
        // checks it), so the server can admit "ZompiercerLAN/relay-8/*" and block one flawed version.
        // relay-9 (0.10.0): schema 16 (marks, shared train controls);
        // relay-10 (0.11.0): schema 17 (text chat);
        // relay-11 (0.12.0): schema 18 (guest death, the host's death flag); 0.13.0 adds the public room list (relay 1.4.0);
        // 0.13.2: native-loaded beaver figurines stay visible (same wire protocol as 0.13.x);
        // 1.0.0: the guest profile follows the host ledger at once (same wire protocol as 0.13.x);
        // relay-12 (1.0.1): schema 19 (zombie animator parameters), several combat actions in flight;
        // 1.0.2 (relay-12, same schema): a magazine of actions in flight and wider action budgets;
        // 1.0.3-1.0.6 (relay-12, same schema): panel layout, guest reload animation; 1.0.7 (schema 20): zombie timeline, car-relative shots;
        // 1.0.8 (relay-12, schema 21): partner health, guest shots heard by zombies, notifications;
        // 1.0.9 (relay-12, schema 21): the other player's armed pose and weapon in hand;
        // 1.1.0 (relay-12, schema 22): the other player crouches, looks up and down, swings melee weapons;
        // 1.1.1 (relay-12, schema 23): ladders of built parts for the guest, the partner aims and fires visibly;
        // 1.1.5 (relay-12, schema 24): the guest crafts in the game's own craft window;
        // 1.1.6 (relay-12, schema 25): train doors, fists, zombie spits, robot dogs and barrels, chosen looks;
        // 1.1.7 (relay-12, schema 25): the boss arena gates close only once both players are inside;
        // 1.1.8 (relay-12, schema 26): firefighter flames, the nurse's healing smoke, the bush zombie's ranged attack;
        // 1.1.9 (relay-12, schema 27): revive (downed players), partner flashlights, steps, reload, lying pose, nicknames;
        // 1.2.0 (relay-12, schema 28): the host's quests are shared, the guest accepts and hands them in through the host; keys;
        // 1.2.1 (relay-12, schema 29): robot dogs belong to whoever takes them first, barrels are carried by either player;
        // 1.2.2 (relay-12, schema 30): explosions for the guest, rainwater through the host, sleeping together, map marker;
        // 1.3.0 (relay-12, schema 30): the host checks the guest's level, experience, health and needs;
        // 1.4.0 (relay-12, schema 31): the guest's grenades through the host; paint, wires and signs of the host's train;
        // 1.4.1 (relay-12, schema 31, train layout 7): fixes (downed guest seen by the host, robot window, fists, decor);
        // 1.4.2 (relay-12, schema 32): the guest's furniture kits through the host; the guest takes host parts apart and repairs them;
        // 1.4.3 (relay-12, schema 33): zombie hits make the guest bleed; the host's fuel and fuel stations for the guest;
        // 1.4.4 (relay-12, schema 34): location objects (blown walls, swamp, levers) and difficulty for two;
        // 1.4.5 (relay-12, schema 35): leaving at once, shared quest and boss rewards, taken-apart objects;
        // 1.4.6 (relay-12, schema 36): the partner's state 20 a second on the sender's clock, its dash (a roll), hands on the weapon;
        // 1.4.7 (relay-12, schema 37): the partner runs, jumps, swims, climbs, throws, eats, drinks, bandages and flinches visibly;
        // 1.4.8 (relay-12, schema 38): strikes by weapon, jabs with fists, reaching and bending down with the use key;
        // 1.4.9 (relay-12, schema 38): heartbeats and world states have their budgets again (1.4.6-1.4.8 lost them);
        // 1.4.10 (relay-12, schema 38): a LAN code lasts 30 seconds;
        // 1.4.11 (relay-12, schema 38): the guest gets zombie experience, its swings show, items on shelves, blueprints aboard;
        // 1.4.12 (relay-12, schema 38): blueprints are seen and solid only with a tool in hand, as the game's own;
        // 1.4.13 (relay-12, schema 39): the host's zombies notice the guest by the game's stealth rules, robot dogs show their devices;
        // 1.4.14 (relay-12, schema 39): guests keep their profiles when the host's slot was saved again without them, zombies wake around the guest;
        // 1.4.15 (relay-12, schema 39): a beaver figurine keeps its look after a load or a location change;
        // 1.4.16 (relay-12, schema 39): ladders, pictures on host walls, zombies turn to a nearer host, combat refusals logged;
        // 1.4.17 (relay-12, schema 39): the guest sees barrels, canisters and items the host's camera does not see;
        // 1.4.18 (relay-12, schema 39): the guest's combat no longer stalls above 100 stamina; barrels and canisters in holders;
        // 1.4.19 (relay-12, schema 39): emptied loot bags go, poisoning ends with its buff, the guest's throws logged;
        // 1.4.20 (relay-12, schema 39): zombie health for two counts for every blow, the guest's swing as strong as the host's;
        // relay-8 (0.8.1): schema 15 (the guest places blueprints on the host's train);
        // relay-7 was 0.8.0 (schema 14), relay-6 0.7.x (schema 13), relay-5 0.6.1, relay-4 0.6.0,
        // relay-3 0.5.0, relay-2 0.4.2-0.4.3.
        internal const string Application = "ZompiercerLAN/relay-12/1.4.20";
        private const int ConnectTimeout = 8000, MaxEvents = 32;
        private readonly LanRelayOptions _options;
        private readonly bool _host;
        private readonly string _fingerprint;
        private readonly object _gate = new object();
        private readonly Queue<LanRelayEvent> _events = new Queue<LanRelayEvent>();
        private readonly Queue<KeyValuePair<byte, byte[]>> _outbound = new Queue<KeyValuePair<byte, byte[]>>();
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private readonly Thread _thread;
        private volatile bool _stopping, _ready, _session;
        private LanFault _failure;
        private RelayConnection _connection;
        private RelaySeat _seat;
        private string _room = "";
        private int _udpPort;

        internal bool Ready { get { return _ready; } }
        internal LanFault Failure { get { lock (_gate) return _failure; } }
        internal string Room { get { lock (_gate) return _room; } }
        internal LanRelayLink(LanRelayOptions options, bool host)
        {
            if (options == null || !LanRelayText.ValidServer(options.Server) || options.Fingerprint == null || options.Fingerprint.Length != 32 ||
                host != (options.Room.Length == 0))
                throw new ArgumentException("Relay options");
            _options = options; _host = host;
            _fingerprint = BitConverter.ToString(options.Fingerprint).Replace("-", "");
            _thread = new Thread(Run) { IsBackground = true, Name = "Zompiercer relay control" };
            _thread.Start();
        }

        // UDP carrier for the DTLS transport; available once the seat exists.
        internal ILanDatagramLink CreateDatagramLink()
        {
            lock (_gate)
            {
                if (!_ready || _seat == null) throw new InvalidOperationException("Relay seat not ready");
                return new LanRelayDatagramLink(new IPEndPoint(_options.Server, _udpPort), _seat.PeerId, _seat.Secret);
            }
        }

        // Pairing is over: ignore further DATA and allow automatic rejoin.
        internal void BeginSession() { _session = true; lock (_gate) _events.Clear(); }
        internal void SendData(byte[] frame) { Enqueue(ClientMessage.Data, frame); }
        internal void Kick() { Enqueue(ClientMessage.KickGuest, new byte[0]); }
        internal void Lock() { Enqueue(ClientMessage.LockRoom, new byte[0]); }
        // A listed room (0.13.0) stays open for guests: a new join window and attempts. The relay
        // answers ROOM_STATE, or a non-fatal RATE_LIMITED when asked again too soon.
        internal void Reopen() { Interlocked.Increment(ref _opens); Enqueue(ClientMessage.OpenRoom, new byte[0]); }
        private int _opens;

        internal LanRelayEvent NextEvent(int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (!_stopping)
            {
                lock (_gate) { if (_events.Count != 0) return _events.Dequeue(); if (_failure != LanFault.None) return null; }
                if (Environment.TickCount - deadline >= 0) return null;
                Thread.Sleep(5);
            }
            return null;
        }

        private void Enqueue(byte type, byte[] payload)
        {
            if (payload == null || payload.Length > RelayProtocol.MaxFramePayload) throw new ArgumentException("Relay frame");
            lock (_gate)
            {
                if (_outbound.Count >= 16) throw new IOException("Relay send queue full");
                _outbound.Enqueue(new KeyValuePair<byte, byte[]>(type, (byte[])payload.Clone()));
            }
        }

        private void Run()
        {
            try
            {
                _connection = OpenFirst();
                lock (_gate) _ready = true;
                double nextPing = 0, lostAt = -1;
                while (!_stopping)
                {
                    try
                    {
                        Pump(ref nextPing);
                        lostAt = -1;
                    }
                    // LanFaultException derives from IOException but is a decision of the
                    // relay (ROOM_CLOSED, unknown message): never reconnect after it.
                    catch (Exception ex) when (!(ex is LanFaultException) &&
                        (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is TimeoutException))
                    {
                        // A TCP outage does not end the game: the relay keeps the seat
                        // (and its UDP) for the grace period; reclaim it with the ticket.
                        if (!_session || _stopping) throw;
                        if (lostAt < 0) lostAt = _time.Elapsed.TotalSeconds;
                        Close();
                        int grace = _connectionGrace;
                        while (!_stopping && _connection == null)
                        {
                            if (_time.Elapsed.TotalSeconds - lostAt > Math.Max(5, grace - 2)) throw new LanFaultException(LanFault.RelayClosed);
                            Thread.Sleep(2000);
                            try { _connection = Open(_seat.Ticket); }
                            catch (Exception retry) when (!(retry is LanFaultException) &&
                                (retry is IOException || retry is SocketException || retry is TimeoutException)) { }
                        }
                        nextPing = 0;
                    }
                }
            }
            catch (Exception ex) { if (!_stopping) Fail(Map(ex)); }
            finally { Close(); }
        }

        private int _connectionGrace = 60;

        // A guest that just left the room (a wrong password, a cancelled try) frees its seat
        // only when the relay sees its connection close, and a join that overtakes that close
        // is refused as busy. Such a refusal costs no join attempt: retry for about 3 s.
        private RelayConnection OpenFirst()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return Open(null); }
                catch (RelayException ex) when (!_host && ex.Code == RelayError.RoomBusy && attempt < 5 && !_stopping) { Thread.Sleep(600); }
            }
        }

        private RelayConnection Open(byte[] rejoinTicket)
        {
            RelayConnection connection;
            try { connection = RelayConnection.Connect(_options.Server.ToString(), _options.Port, Application, _options.AccessKey, _fingerprint, ConnectTimeout); }
            catch (RelayException ex) when (ex.Code == RelayError.AppMismatch || ex.Code == RelayError.UnsupportedVersion)
            {
                // Refused at HELLO: the server does not take this build at all (old or switched off).
                throw new LanFaultException(LanFault.RelayOutdated);
            }
            try
            {
                if (connection.Welcome.UdpPort == 0) throw new RelayException(RelayError.BadFrame);
                _connectionGrace = connection.Welcome.RejoinGraceSeconds;
                RelaySeat seat;
                string room;
                lock (_gate) room = _room;
                if (rejoinTicket != null) seat = connection.RejoinRoom(room, rejoinTicket, ConnectTimeout);
                else seat = _host ? connection.CreateRoom(ConnectTimeout) : connection.JoinRoom(_options.Room, ConnectTimeout);
                // The relay keeps the listing across a rejoin; list it once, right after creating it.
                if (rejoinTicket == null && _host && _options.PublicName.Length != 0 &&
                    connection.PublishRoom(_options.PublicName, ConnectTimeout) != PublishStatus.Listed)
                    throw new LanFaultException(LanFault.RelayNoPublic);
                if (seat.IsHost != _host || _seat != null && (seat.PeerId != _seat.PeerId || !Same(seat.Secret, _seat.Secret)))
                    throw new RelayException(RelayError.BadState);
                lock (_gate)
                {
                    if (_seat != null) _seat.Clear();
                    _seat = seat; _room = seat.RoomId; _udpPort = connection.Welcome.UdpPort;
                }
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private void Pump(ref double nextPing)
        {
            for (int i = 0; i < 8; i++)
            {
                KeyValuePair<byte, byte[]> item;
                lock (_gate) { if (_outbound.Count == 0) break; item = _outbound.Dequeue(); }
                try { _connection.Send(item.Key, item.Value, 0, item.Value.Length); }
                finally { Array.Clear(item.Value, 0, item.Value.Length); }
            }
            double now = _time.Elapsed.TotalSeconds;
            if (now >= nextPing)
            {
                _connection.Ping((uint)Environment.TickCount);
                nextPing = now + Math.Max(1, _connection.Welcome.KeepaliveSeconds / 2.0);
            }
            var message = _connection.Receive(20);
            if (message == null) return;
            switch (message.Type)
            {
                case ServerMessage.Pong:
                    break;
                case ServerMessage.PeerJoined:
                    if (!_session) Event(LanRelayEventKind.PeerJoined, null);
                    break;
                case ServerMessage.PeerLeft:
                    if (!_session) Event(LanRelayEventKind.PeerLeft, null);
                    break;
                case ServerMessage.Data:
                    // Only pairing frames travel over TCP; bounded like LanPairing frames.
                    if (_session || message.Payload.Length < 9 || message.Payload.Length > 8 + LanPake.MaximumFrame) { Array.Clear(message.Payload, 0, message.Payload.Length); break; }
                    Event(LanRelayEventKind.Data, message.Payload);
                    break;
                case ServerMessage.RoomState:
                    if (Volatile.Read(ref _opens) > 0) Interlocked.Decrement(ref _opens);
                    break;
                case ServerMessage.RoomClosed:
                    throw new LanFaultException(LanFault.RelayClosed);
                case ServerMessage.Error:
                    // Reopening too soon is refused without closing the room.
                    if (message.Payload.Length == 1 && message.Payload[0] == RelayError.RateLimited && Volatile.Read(ref _opens) > 0)
                    { Interlocked.Decrement(ref _opens); break; }
                    throw new RelayException(message.Payload.Length == 1 ? message.Payload[0] : RelayError.BadFrame);
                default:
                    throw new LanFaultException(LanFault.RelayClosed);
            }
        }

        private void Event(LanRelayEventKind kind, byte[] data)
        {
            lock (_gate)
            {
                // A peer flooding the pairing channel ends this invitation.
                if (_events.Count >= MaxEvents) throw new LanFaultException(LanFault.PairingExchangeFailed);
                _events.Enqueue(new LanRelayEvent { Kind = kind, Data = data });
            }
        }

        private void Fail(LanFault fault) { lock (_gate) if (_failure == LanFault.None) _failure = fault == LanFault.None ? LanFault.RelayClosed : fault; }

        internal static LanFault Map(Exception ex)
        {
            var fault = ex as LanFaultException;
            if (fault != null) return fault.Fault;
            if (ex is AuthenticationException) return LanFault.RelayCertificate;
            var relay = ex as RelayException;
            if (relay != null)
                switch (relay.Code)
                {
                    case RelayError.AccessDenied: return LanFault.RelayAccessDenied;
                    case RelayError.RoomNotFound: return LanFault.RelayRoomNotFound;
                    case RelayError.RoomBusy: return LanFault.RelayRoomBusy;
                    case RelayError.AppMismatch: case RelayError.UnsupportedVersion: return LanFault.RelayVersion;
                    case RelayError.RateLimited: case RelayError.ServerFull: case RelayError.TooManyRooms: return LanFault.RelayLimited;
                    default: return LanFault.RelayClosed;
                }
            var socket = ex as SocketException;
            if (socket != null && socket.SocketErrorCode == SocketError.AccessDenied) return LanFault.SocketAccessDenied;
            if (ex is IOException || ex is SocketException || ex is TimeoutException) return LanFault.RelayUnreachable;
            return LanFault.Runtime;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }

        private void Close()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection != null) try { connection.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _stopping = true;
            Close();
            if (Thread.CurrentThread != _thread) _thread.Join(500);
            lock (_gate)
            {
                while (_events.Count != 0) { var e = _events.Dequeue(); if (e.Data != null) Array.Clear(e.Data, 0, e.Data.Length); }
                while (_outbound.Count != 0) { var o = _outbound.Dequeue(); Array.Clear(o.Value, 0, o.Value.Length); }
                if (_seat != null) _seat.Clear();
            }
        }
    }

    // Worker-only (0.13.0): one page of the relay's public room list for this mod version.
    // Names are shown in the game, so only those already in the mod's own room-name form
    // are kept; the room ids are only offered for the player to join with a password.
    internal sealed class LanRelayBrowser : IDisposable
    {
        private const int Timeout = 8000;
        private readonly LanRelayOptions _options;
        private readonly Thread _thread;
        private volatile bool _finished;
        private LanRoomInfo[] _rooms = new LanRoomInfo[0];
        private LanFault _failure;
        internal bool Finished { get { return _finished; } }
        internal LanFault Failure { get { return _failure; } }
        internal LanRoomInfo[] Rooms { get { return _rooms; } }

        internal LanRelayBrowser(LanRelayOptions options)
        {
            if (options == null || !LanRelayText.ValidServer(options.Server) || options.Fingerprint == null || options.Fingerprint.Length != 32 || options.Room.Length != 0)
                throw new ArgumentException("Relay options");
            _options = options;
            _thread = new Thread(Run) { IsBackground = true, Name = "Zompiercer relay room list" };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                RelayConnection connection;
                try { connection = RelayConnection.Connect(_options.Server.ToString(), _options.Port, LanRelayLink.Application, _options.AccessKey, BitConverter.ToString(_options.Fingerprint).Replace("-", ""), Timeout); }
                catch (RelayException ex) when (ex.Code == RelayError.AppMismatch || ex.Code == RelayError.UnsupportedVersion) { throw new LanFaultException(LanFault.RelayOutdated); }
                using (connection)
                {
                    int total;
                    var listed = connection.ListRooms(0, Timeout, out total);
                    var rooms = new System.Collections.Generic.List<LanRoomInfo>();
                    foreach (var room in listed)
                    {
                        if (rooms.Count >= LanIpc.MaxRooms) break;
                        if (LanRelayText.NormalizeRoom(room.RoomId) != room.RoomId || LanRelayText.PublicName(room.Name) != room.Name) continue;
                        rooms.Add(LanRoomInfo.ForRelay(room.RoomId, room.Name));
                    }
                    _rooms = rooms.ToArray();
                }
            }
            catch (Exception ex) { _failure = LanRelayLink.Map(ex); }
            finally { _finished = true; }
        }

        public void Dispose() { if (Thread.CurrentThread != _thread) _thread.Join(200); }
    }

    // Relay UDP for the DTLS transport: one connected socket to the relay, every
    // datagram sealed/opened with this seat's keys (HMAC + replay window). The
    // DTLS layer sees a single fixed virtual peer.
    internal sealed class LanRelayDatagramLink : ILanDatagramLink
    {
        private readonly Socket _socket;
        private readonly RelayUdpSession _udp;
        private readonly IPEndPoint _virtual = new IPEndPoint(LanRelayText.VirtualPeer, LanRelayText.VirtualPort);
        private readonly byte[] _buffer = new byte[2048];
        private readonly Stopwatch _time = Stopwatch.StartNew();
        // Latest path challenge token (protocol 3). The relay sends our traffic only
        // to an address that echoed its token; every BIND carries the latest one.
        private readonly byte[] _pathToken = new byte[RelayProtocol.PathTokenLength];
        private double _nextBind;
        private ulong _nonce;
        private bool _pathValidated;
        private int _disposed;

        internal LanRelayDatagramLink(IPEndPoint server, ulong peerId, byte[] secret)
        {
            _udp = new RelayUdpSession(peerId, secret);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // ICMP "port unreachable" must not surface as a receive error (SIO_UDP_CONNRESET).
                _socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _socket.Connect(server); // the OS drops datagrams from any other source
                _socket.Blocking = false;
                _socket.ReceiveBufferSize = 262144;
            }
            catch { _socket.Close(); _udp.Dispose(); throw; }
        }

        public bool Poll(int microseconds)
        {
            // BIND tells the relay our current NAT mapping; repeat to keep it alive.
            // Until the relay has validated this address, retry every second.
            double now = _time.Elapsed.TotalSeconds;
            if (now >= _nextBind) { _nextBind = now + (_pathValidated ? 10 : 1); SendBind(); }
            return _socket.Poll(microseconds, SelectMode.SelectRead);
        }

        public int Receive(byte[] buffer, int offset, int count, out IPEndPoint from)
        {
            from = null; int size;
            try { size = _socket.Receive(_buffer); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.ConnectionReset) return -1;
                throw;
            }
            byte kind; int payloadOffset, payloadLength;
            if (!_udp.TryOpen(_buffer, size, out kind, out payloadOffset, out payloadLength)) return -1;
            if (kind == UdpKind.BindAck)
            {
                ulong nonce; byte flags;
                if (!RelayUdpSession.ReadBindAck(_buffer, payloadOffset, payloadLength, out nonce, out flags, _pathToken)) return -1;
                if ((flags & BindAckFlags.Challenge) != 0) { _pathValidated = false; SendBind(); } // echo at once from this address
                else if ((flags & BindAckFlags.PathValidated) != 0) _pathValidated = true;
                return -1;
            }
            if (kind != UdpKind.Data || payloadLength > count) return -1;
            Buffer.BlockCopy(_buffer, payloadOffset, buffer, offset, payloadLength);
            from = _virtual; return payloadLength;
        }

        private void SendBind() { Transmit(_udp.SealBind(++_nonce, _pathToken)); }

        public void Send(byte[] datagram, int count, IPEndPoint to)
        {
            if (to == null || !to.Equals(_virtual)) throw new IOException("Relay destination differs from the paired peer");
            Transmit(_udp.SealData(datagram, 0, count));
        }

        private void Transmit(byte[] sealedDatagram)
        {
            try { _socket.Send(sealedDatagram); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.NoBufferSpaceAvailable && ex.SocketErrorCode != SocketError.ConnectionReset) throw;
            }
            finally { Array.Clear(sealedDatagram, 0, sealedDatagram.Length); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _socket.Close();
            _udp.Dispose();
            Array.Clear(_pathToken, 0, _pathToken.Length);
        }
    }
}
