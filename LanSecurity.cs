using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.Utilities;

namespace ZompiercerLAN
{
    // DTLS/socket owner: one background thread. Unity receives bounded explicit
    // packet queues; no game API runs on the worker. Cipher state is never shared.
    internal sealed class LanSecureTransport : IDisposable
    {
        private const uint Magic = 0x33444C5A;
        private const int MaxDatagram = 1300, PrefixBytes = 6, TagBytes = 32;
        private const int WireLimit = MaxDatagram - PrefixBytes - TagBytes, QueueLimit = 128;
        private static readonly byte[] Identity = Encoding.ASCII.GetBytes("ZompiercerLAN paired DTLS v3");
        private static readonly object LibraryGate = new object();
        private static bool _libraryVerified;
        private readonly object _gate = new object();
        private readonly ILanDatagramLink _link;
        private readonly Thread _worker;
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private readonly bool _hosting;
        private readonly string _gameVersion;
        private readonly IPEndPoint _joinPeer;
        private readonly IPAddress _allowedPeer;
        private readonly byte[] _secret, _admissionKey;
        private readonly Queue<Received> _received = new Queue<Received>();
        private readonly Queue<Outgoing> _outgoing = new Queue<Outgoing>(), _controls = new Queue<Outgoing>();
        // Token buckets are worker-owned and use monotonic time.
        private readonly LanRateLimit _packets = new LanRateLimit(400, 256), _bytes = new LanRateLimit(384 * 1024, 384 * 1024);
        private readonly LanRateLimit _handshakes = new LanRateLimit(10, 10), _application = new LanRateLimit(200, 160), _sending = new LanRateLimit(200, 160);
        private readonly LanRateLimit _wireSending = new LanRateLimit(400, 256), _wireBytes = new LanRateLimit(384 * 1024, 384 * 1024);
        private readonly LanMessageLimits _messages = new LanMessageLimits();
        private readonly LanPeerGuard _peerGuard = new LanPeerGuard();
        private readonly LanReconnectDelay _reconnectDelay = new LanReconnectDelay();
        private volatile bool _stopping;
        private volatile bool _blocked;
        private string _securityFailure;
        private long _handshakeAttempts;
        private int _generation;
        private ulong _session;
        private IPEndPoint _peer;
        private string _problem;
        private sealed class Received { internal IPEndPoint Peer; internal Packet Packet; internal ulong Session; }
        private sealed class Outgoing { internal byte[] Bytes; internal ulong Session; internal PacketKind Kind; }
        internal IPEndPoint Peer { get { lock (_gate) return _peer; } }
        internal ulong Session { get { lock (_gate) return _session; } }
        internal bool Available { get { lock (_gate) return _received.Count != 0; } }
        internal string SecurityFailure { get { lock (_gate) return _securityFailure; } }
        internal int QueuedPackets { get { lock (_gate) return _received.Count; } }
        internal long HandshakeAttempts { get { return Interlocked.Read(ref _handshakeAttempts); } }

        internal static LanSecureTransport HostWithKey(int port, IPAddress pairedClient, string gameVersion, byte[] secret)
        {
            return new LanSecureTransport(true, port, pairedClient, gameVersion, secret);
        }
        internal static LanSecureTransport JoinWithKey(int port, IPAddress address, string gameVersion, byte[] secret)
        {
            return new LanSecureTransport(false, port, address, gameVersion, secret, null);
        }
        // Same DTLS session, admission MAC and budgets, carried through the relay's
        // authenticated UDP. The peer is the fixed virtual endpoint, never a real address.
        internal static LanSecureTransport OverRelay(bool hosting, ILanDatagramLink link, string gameVersion, byte[] secret)
        {
            if (link == null) throw new ArgumentNullException("link");
            return new LanSecureTransport(hosting, LanRelayText.VirtualPort, LanRelayText.VirtualPeer, gameVersion, secret, link);
        }
        private LanSecureTransport(bool hosting, int port, IPAddress address, string gameVersion, byte[] secret)
            : this(hosting, port, address, gameVersion, secret, null) { }
        private LanSecureTransport(bool hosting, int port, IPAddress address, string gameVersion, byte[] secret, ILanDatagramLink relay)
        {
            VerifyLibrary();
            bool validPeer = relay == null ? LanNetworkPolicy.PrivatePeer(address) && port >= 1024 && port <= 65535
                : LanRelayText.VirtualPeer.Equals(address) && port == LanRelayText.VirtualPort;
            if (secret == null || secret.Length != 32 || !validPeer)
            {
                relay?.Dispose();
                throw new ArgumentException("A completed pairing and 256-bit transport key are required");
            }
            _hosting = hosting; _gameVersion = gameVersion; _secret = (byte[])secret.Clone();
            _allowedPeer = hosting ? address : null;
            // This domain-separated HMAC is admission filtering before DTLS parsing,
            // not a custom key exchange/cipher. DTLS PSK+ECDHE/AEAD are library-owned.
            using (var hmac = new HMACSHA256(_secret))
                _admissionKey = hmac.ComputeHash(Encoding.ASCII.GetBytes("ZompiercerLAN paired DTLS admission v3"));
            _joinPeer = hosting ? null : new IPEndPoint(address, port); _peer = _joinPeer;
            Socket socket = null;
            try
            {
                if (relay != null) _link = relay;
                else
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.ExclusiveAddressUse = true; socket.ReceiveBufferSize = 262144;
                    LanRoute.BindPeer(socket, address, hosting ? port : 0); socket.Blocking = false;
                    _link = new LanSocketLink(socket);
                }
                _worker = new Thread(Run) { IsBackground = true, Name = "Zompiercer LAN DTLS" }; _worker.Start();
            }
            catch
            {
                if (_link != null) _link.Dispose(); else socket?.Close();
                Clear(_secret); Clear(_admissionKey); throw;
            }
        }
        internal static void VerifyLibrary()
        {
            lock (LibraryGate)
            {
                if (_libraryVerified) return;
                // Also fail closed if another mod resolves a different BC assembly.
                using (var stream = File.OpenRead(typeof(DtlsTransport).Assembly.Location))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") !=
                        "4F96977E9C67334742C683410B3A361258219F0D3084A5E0BC10FBA96CF23A0D")
                        throw new IOException("Требуется проверенная Bouncy Castle 2.7.0 из архива мода");
                _libraryVerified = true;
            }
        }
        internal void Tick()
        {
            string problem; lock (_gate) { problem = _problem; _problem = null; }
            if (problem != null) throw new IOException(problem);
        }
        internal void Restart()
        {
            lock (_gate) { if(_blocked || _stopping)return;Interlocked.Increment(ref _generation); ResetQueues(); _session = 0; _peer = _joinPeer; }
        }
        internal void RetryAfterBusy() { Restart(); }
        internal bool TryReceive(out IPEndPoint from, out Packet packet)
        {
            lock (_gate)
            {
                from = null; packet = null;
                if (_received.Count == 0) return false;
                var received = _received.Dequeue();
                if (received.Session == 0 || received.Session != _session) return false;
                from = received.Peer; packet = received.Packet; return true;
            }
        }
        internal void Send(Packet packet)
        {
            lock (_gate)
            {
                if (_stopping || _blocked || _session == 0 || packet.Session != _session) return;
                if (!LanNetworkPolicy.Allowed(!_hosting,packet.Kind,true)) return;
                var bytes = LanProtocol.Encode(packet);
                bool control = packet.Kind == PacketKind.Welcome || packet.Kind == PacketKind.VersionMismatch;
                var queue = control ? _controls : _outgoing;
                if (queue.Count >= (control ? 8 : QueueLimit)) { Clear(bytes); return; }
                queue.Enqueue(new Outgoing { Bytes = bytes, Session = _session, Kind = packet.Kind });
            }
        }
#if SECURITY_CHECKS
        internal string GuardCountsForChecks { get { return _peerGuard.CountsForChecks; } }
        // Model a compromised peer sending validly encrypted but invalid plaintext.
        // This bypass is absent from the shipping assembly.
        internal void SendUncheckedForChecks(byte[] bytes,bool control=false)
        {
            lock(_gate) {
                if(_stopping || _blocked || _session==0 || bytes==null || bytes.Length>1200)return;
                var queue=control?_controls:_outgoing;if(queue.Count>=QueueLimit)return;
                queue.Enqueue(new Outgoing {Bytes=(byte[])bytes.Clone(),Session=_session,Kind=bytes.Length>5?(PacketKind)bytes[5]:0});
            }
        }
#endif
        private void Run()
        {
            try
            {
                while (!_stopping && !_blocked)
                {
                    int generation = Volatile.Read(ref _generation); DtlsTransport dtls = null;
                    while(Current(generation) && !_reconnectDelay.Ready(_time.Elapsed.TotalSeconds)) Thread.Sleep(10);
                    if(!Current(generation))continue;
                    bool attempted=false;
                    try
                    {
                        var crypto = new BcTlsCrypto(new SecureRandom()); ulong session; IPEndPoint peer;
                        if (_hosting)
                        {
                            var listener = new SocketDatagrams(this, generation, null);
                            var verifier = new DtlsVerifier(crypto); double rotateAt = _time.Elapsed.TotalSeconds + 10;
                            var hello = new byte[WireLimit]; DtlsRequest request = null;
                            while (Current(generation) && request == null)
                            {
                                int size = listener.Receive(hello, 0, hello.Length, 50);
                                if (size < 0) continue;
                                double now = _time.Elapsed.TotalSeconds;
                                if (!_handshakes.Take(1, now)) continue;
                                if (now >= rotateAt) { verifier = new DtlsVerifier(crypto); rotateAt = now + 10; }
                                peer = listener.LastSender; var clientId = new byte[6];
                                Buffer.BlockCopy(peer.Address.GetAddressBytes(), 0, clientId, 0, 4);
                                clientId[4] = (byte)peer.Port; clientId[5] = (byte)(peer.Port >> 8);
                                request = verifier.VerifyRequest(clientId, hello, 0, size, listener);
                            }
                            Check(generation); peer = listener.LastSender;
                            var profile = new RoomServer(crypto, new RoomIdentity(_secret));
                            attempted=true;Interlocked.Increment(ref _handshakeAttempts);
                            dtls = new DtlsServerProtocol().Accept(profile, new SocketDatagrams(this, generation, peer), request);
                            session = profile.ConnectionSession;
                        }
                        else
                        {
                            peer = _joinPeer; var profile = new RoomClient(crypto, new RoomIdentity(_secret));
                            attempted=true;Interlocked.Increment(ref _handshakeAttempts);
                            dtls = new DtlsClientProtocol().Connect(profile, new SocketDatagrams(this, generation, peer));
                            session = profile.ConnectionSession;
                        }
                        Check(generation);
                        if (session == 0 || dtls.GetSendLimit() < 1200 || dtls.GetReceiveLimit() < 1200)
                            throw new IOException("DTLS profile or payload size is incompatible");
                        lock (_gate) { Check(generation); ResetQueues(); _session = session; _peer = peer; }
                        Serve(dtls, generation, peer, session);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        if (Current(generation)) lock (_gate)
                            _problem = "DTLS: " + ex.GetType().Name + ". Проверьте доступность друга в локальной сети.";
                    }
                    finally
                    {
                        try { dtls?.Close(); } catch { }
                        lock (_gate) if (generation == Volatile.Read(ref _generation))
                        { ResetQueues(); _session = 0; _peer = _joinPeer; }
                        if(attempted && !_stopping && !_blocked) _reconnectDelay.Failed(_time.Elapsed.TotalSeconds);
                    }
                }
            }
            finally { _link.Dispose();Clear(_secret); Clear(_admissionKey); }
        }
        private void Serve(DtlsTransport dtls, int generation, IPEndPoint peer, ulong session)
        {
            var input = new byte[1201];
            double started = _time.Elapsed.TotalSeconds, lastValid = started, establishedAt = 0, nextHello = 0, nextHeartbeat = 0;
            bool established = false, helloSeen=false, healthy=false;
            while (Current(generation))
            {
                double now = _time.Elapsed.TotalSeconds;
                if(!healthy && established && now-establishedAt>=10 && now-lastValid<5) { healthy=true;_reconnectDelay.Healthy(); }
                if (now - started > 24 * 60 * 60 || now - lastValid > (established ? 30 : 15)) return;
                if (!_hosting && !established && now >= nextHello)
                {
                    SendControl(dtls, new Packet { Kind = PacketKind.Hello, Session = session, GameVersion = _gameVersion }); nextHello = now + 1;
                }
                if (established && now >= nextHeartbeat)
                {
                    SendControl(dtls, new Packet { Kind = PacketKind.Heartbeat, Session = session, GameVersion = _gameVersion }); nextHeartbeat = now + 1;
                }
                for (int i = 0; i < 32; i++)
                {
                    Outgoing send;
                    lock (_gate)
                    {
                        if (_controls.Count != 0) send = _controls.Dequeue();
                        else if (established && _outgoing.Count != 0) send = _outgoing.Dequeue();
                        else break;
                    }
                    try
                    {
                        Check(generation);
                        if (send.Session == session && _sending.Take(1, _time.Elapsed.TotalSeconds)) {
                            dtls.Send(send.Bytes, 0, send.Bytes.Length);
                            // Hello alone grants no application access on the host.
                            // Legitimate clients wait for this Welcome before sending.
                            if(_hosting && helloSeen && send.Kind==PacketKind.Welcome && !established) { established=true;establishedAt=_time.Elapsed.TotalSeconds; }
                        }
                    }
                    finally { Clear(send.Bytes); }
                }
                int size = dtls.Receive(input, 0, input.Length, 15);
                if(size<0)continue;
                if(size==0 || size>1200) { Violation(false);continue; }
                if(!_application.Take(1,_time.Elapsed.TotalSeconds)) { Violation(true);continue; }
                var bytes = new byte[size]; Buffer.BlockCopy(input, 0, bytes, 0, size); Packet packet;
                try { if (!LanProtocol.TryDecode(bytes, out packet) || packet.Session != session) { Violation(false);continue; } }
                finally { Clear(bytes); }
                if (!LanNetworkPolicy.Allowed(_hosting,packet.Kind,established)) { Violation(false);continue; }
                if (!_messages.Take(packet.Kind,_time.Elapsed.TotalSeconds)) { Violation(true);continue; }
                if (packet.GameVersion == _gameVersion)
                {
                    lastValid = _time.Elapsed.TotalSeconds;
                    if(_hosting && packet.Kind==PacketKind.Hello) helloSeen=true;
                    if(!_hosting && packet.Kind==PacketKind.Welcome && !established) { established=true;establishedAt=lastValid; }
                }
                else if(!(_hosting && packet.Kind==PacketKind.Hello || !_hosting && packet.Kind==PacketKind.VersionMismatch)) continue;
                lock (_gate)
                {
                    Check(generation);
                    if (_received.Count < QueueLimit) _received.Enqueue(new Received { Peer = peer, Packet = packet, Session = session });
                }
            }
        }
        private void Violation(bool rate)
        {
            if(rate)_peerGuard.ExcessRate(_time.Elapsed.TotalSeconds);else _peerGuard.Invalid(_time.Elapsed.TotalSeconds);
            if(_peerGuard.Failure==null)return;
            lock(_gate) {
                if(_blocked)return;
                _securityFailure=_peerGuard.Failure;_blocked=true;
                Interlocked.Increment(ref _generation);ResetQueues();_session=0;_peer=null;
            }
        }
        private static void SendControl(DtlsTransport dtls, Packet packet)
        { var bytes = LanProtocol.Encode(packet); try { dtls.Send(bytes, 0, bytes.Length); } finally { Clear(bytes); } }
        private bool Current(int generation) { return !_stopping && !_blocked && generation == Volatile.Read(ref _generation); }
        private void Check(int generation) { if (!Current(generation)) throw new OperationCanceledException(); }
        private void ResetQueues()
        {
            _received.Clear(); while (_outgoing.Count != 0) Clear(_outgoing.Dequeue().Bytes); while (_controls.Count != 0) Clear(_controls.Dequeue().Bytes);
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true; Interlocked.Increment(ref _generation); ResetQueues(); _session = 0; _peer = null;
            }
            _link.Dispose(); if (_worker != null && Thread.CurrentThread != _worker) _worker.Join(100);
        }
        private static void Clear(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        private sealed class SocketDatagrams : DatagramTransport
        {
            private readonly LanSecureTransport _owner; private readonly int _generation; private readonly IPEndPoint _peer;
            private readonly byte[] _buffer = new byte[MaxDatagram+1];
            internal IPEndPoint LastSender { get; private set; }
            internal SocketDatagrams(LanSecureTransport owner, int generation, IPEndPoint peer) { _owner = owner; _generation = generation; _peer = peer; }
            public int GetReceiveLimit() { return WireLimit; }
            public int GetSendLimit() { return WireLimit; }
            public int Receive(byte[] buffer, int offset, int length, int waitMillis)
            {
                double deadline = _owner._time.Elapsed.TotalMilliseconds + Math.Max(0, waitMillis);
                do
                {
                    _owner.Check(_generation);
                    if (!_owner._link.Poll(1000))
                    {
                        if (_owner._time.Elapsed.TotalMilliseconds >= deadline) return -1;
                        Thread.Sleep(1); continue;
                    }
                    // Charge before reading, including oversized datagrams which
                    // throw MessageSize. A hostile queue must not busy-spin a core.
                    if (!_owner._packets.Take(1,_owner._time.Elapsed.TotalSeconds)) { Thread.Sleep(5);return -1; }
                    IPEndPoint peer;
                    int size = _owner._link.Receive(_buffer, 0, _buffer.Length, out peer);
                    if (size < 0 || peer == null) continue;
                    double now = _owner._time.Elapsed.TotalSeconds;
                    if (!_owner._bytes.Take(size, now)) { Thread.Sleep(5);return -1; }
                    if (size > MaxDatagram || size < PrefixBytes + TagBytes + 1 ||
                        BitConverter.ToUInt32(_buffer, 0) != Magic || _buffer[4] != 3 || _buffer[5] != 1) continue;
                    if (peer.Port<1024 || _peer != null && !peer.Equals(_peer)) continue;
                    if (_owner._allowedPeer != null && !peer.Address.Equals(_owner._allowedPeer)) continue;
                    using (var hmac = new HMACSHA256(_owner._admissionKey))
                    {
                        var tag = hmac.ComputeHash(_buffer, 0, size - TagBytes);
                        bool valid = Arrays.FixedTimeEquals(tag, Slice(_buffer, size - TagBytes, TagBytes)); Clear(tag); if (!valid) continue;
                    }
                    int payload = size - PrefixBytes - TagBytes; if (payload > length) continue;
                    Buffer.BlockCopy(_buffer, PrefixBytes, buffer, offset, payload); LastSender = peer; return payload;
                } while (_owner._time.Elapsed.TotalMilliseconds < deadline);
                return -1;
            }
            public void Send(byte[] buffer, int offset, int length)
            {
                _owner.Check(_generation); var peer = _peer ?? LastSender;
                if (peer == null || length < 1 || length > WireLimit) throw new IOException("Invalid DTLS datagram");
                var expectedAddress=_owner._hosting ? _owner._allowedPeer : _owner._joinPeer.Address;
                if (!peer.Address.Equals(expectedAddress) || !_owner._hosting && !peer.Equals(_owner._joinPeer))
                    throw new IOException("DTLS destination differs from paired peer");
                double now=_owner._time.Elapsed.TotalSeconds;
                if (!_owner._wireSending.Take(1,now) || !_owner._wireBytes.Take(PrefixBytes+length+TagBytes,now)) return;
                var bytes = new byte[PrefixBytes + length + TagBytes];
                Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, bytes, 0, 4); bytes[4] = 3; bytes[5] = 1;
                Buffer.BlockCopy(buffer, offset, bytes, PrefixBytes, length);
                using (var hmac = new HMACSHA256(_owner._admissionKey))
                {
                    var tag = hmac.ComputeHash(bytes, 0, bytes.Length - TagBytes); Buffer.BlockCopy(tag, 0, bytes, bytes.Length - TagBytes, TagBytes); Clear(tag);
                }
                _owner._link.Send(bytes, bytes.Length, peer);
            }
            public void Close() { /* Adapter lifetime does not close shared listener. */ }
            private static byte[] Slice(byte[] data, int offset, int count) { var bytes = new byte[count]; Buffer.BlockCopy(data, offset, bytes, 0, count); return bytes; }
        }
        private sealed class RoomIdentity : TlsPskIdentity, TlsPskIdentityManager
        {
            private readonly byte[] _secret; internal RoomIdentity(byte[] secret) { _secret = secret; }
            public void SkipIdentityHint() { } public void NotifyIdentityHint(byte[] hint) { }
            public byte[] GetPskIdentity() { return (byte[])Identity.Clone(); } public byte[] GetPsk() { return (byte[])_secret.Clone(); }
            public byte[] GetHint() { return null; } public byte[] GetPsk(byte[] identity) { return Arrays.FixedTimeEquals(identity, Identity) ? GetPsk() : null; }
        }
        private static ulong ExportSession(TlsContext context)
        {
            var bytes = context.ExportKeyingMaterial("EXPORTER-ZompiercerLAN-session-v2", null, 8);
            try { ulong session = BitConverter.ToUInt64(bytes, 0); return session == 0 ? 1 : session; } finally { Clear(bytes); }
        }
        private sealed class RoomClient : PskTlsClient
        {
            internal ulong ConnectionSession; internal RoomClient(TlsCrypto crypto, RoomIdentity identity) : base(crypto, identity) { }
            protected override ProtocolVersion[] GetSupportedVersions() { return ProtocolVersion.DTLSv12.Only(); }
            protected override int[] GetSupportedCipherSuites() { return new[] { CipherSuite.TLS_ECDHE_PSK_WITH_CHACHA20_POLY1305_SHA256 }; }
            protected override IList<int> GetSupportedGroups(IList<int> roles) { return new[] { NamedGroup.x25519 }; }
            public override int GetHandshakeTimeoutMillis() { return 10000; } public override int GetMaxHandshakeMessageSize() { return 4096; }
            public override bool RequiresExtendedMasterSecret() { return true; } public override bool IgnoreCorruptDtlsRecords { get { return true; } }
            public override void NotifyHandshakeComplete() { base.NotifyHandshakeComplete(); ConnectionSession = ExportSession(m_context); }
        }
        private sealed class RoomServer : PskTlsServer
        {
            internal ulong ConnectionSession; internal RoomServer(TlsCrypto crypto, RoomIdentity identity) : base(crypto, identity) { }
            protected override ProtocolVersion[] GetSupportedVersions() { return ProtocolVersion.DTLSv12.Only(); }
            protected override int[] GetSupportedCipherSuites() { return new[] { CipherSuite.TLS_ECDHE_PSK_WITH_CHACHA20_POLY1305_SHA256 }; }
            public override int[] GetSupportedGroups() { return new[] { NamedGroup.x25519 }; }
            public override int GetHandshakeTimeoutMillis() { return 10000; } public override int GetMaxHandshakeMessageSize() { return 4096; }
            public override bool RequiresExtendedMasterSecret() { return true; } public override bool IgnoreCorruptDtlsRecords { get { return true; } }
            public override void NotifyHandshakeComplete() { base.NotifyHandshakeComplete(); ConnectionSession = ExportSession(m_context); }
        }
    }

    // Raw datagram carrier under the DTLS admission layer. Used only from the
    // transport's own worker thread; Dispose may race and must be idempotent.
    internal interface ILanDatagramLink : IDisposable
    {
        bool Poll(int microseconds);
        // Returns the datagram size, or -1 for a transient/foreign datagram to skip.
        int Receive(byte[] buffer, int offset, int count, out IPEndPoint from);
        // Transient send failures are dropped (UDP semantics); others throw.
        void Send(byte[] datagram, int count, IPEndPoint to);
    }

    // The original LAN behaviour: one unconnected UDP socket on the selected interface.
    internal sealed class LanSocketLink : ILanDatagramLink
    {
        private readonly Socket _socket;
        internal LanSocketLink(Socket socket) { _socket = socket; }
        public bool Poll(int microseconds) { return _socket.Poll(microseconds, SelectMode.SelectRead); }
        public int Receive(byte[] buffer, int offset, int count, out IPEndPoint from)
        {
            from = null; EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0); int size;
            try { size = _socket.ReceiveFrom(buffer, offset, count, SocketFlags.None, ref endpoint); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.ConnectionReset) return -1;
                throw;
            }
            from = (IPEndPoint)endpoint; return size;
        }
        public void Send(byte[] datagram, int count, IPEndPoint to)
        {
            try { _socket.SendTo(datagram, 0, count, SocketFlags.None, to); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.NoBufferSpaceAvailable && ex.SocketErrorCode != SocketError.ConnectionReset) throw;
            }
        }
        public void Dispose() { _socket.Close(); }
    }
}
