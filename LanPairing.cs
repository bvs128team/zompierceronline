using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Org.BouncyCastle.Utilities;

namespace ZompiercerLAN
{
    // Short-lived invitation/PAKE worker. No Unity APIs, plaintext PIN transmission,
    // automatic PIN renewal, or persistent pairing credentials.
    internal sealed class LanPairing : ILanPairing
    {
        private const uint Magic = 0x33504C5A;
        private readonly object _gate = new object();
        private readonly bool _host;
        private readonly LanDiscovery _discovery;
        private readonly TcpListener _listener;
        private readonly LanRoomAdvertisement _advertisement;
        private readonly IPAddress _requestedAddress;
        private readonly int _pairingPort;
        private string _pendingPeer;
        private bool _ownsAdvertisement;
        private readonly Thread _worker;
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private readonly byte[] _room;
        private readonly double _expires;
        private string _pin, _status;
        private Socket _connection;
        private volatile bool _stopping, _finished, _consumed, _pending, _succeeded;
        private int _decision, _transportReady;
        private bool _credentialsReady;
        private byte[] _secret;
        private IPAddress _peer;
        private LanFault _failure;
        public LanFault Failure { get { return _failure; } }

        public bool PendingApproval { get { return _pending && !_finished && Volatile.Read(ref _decision) == 0; } }
        public string PendingPeer { get { return Volatile.Read(ref _pendingPeer) ?? ""; } }
        public bool Finished { get { return _finished; } }
        public bool Succeeded { get { return _succeeded; } }
        public bool Expired { get { return _time.Elapsed.TotalSeconds >= _expires; } }
        internal string Status { get { return Volatile.Read(ref _status); } }
        public int SecondsLeft { get { return _consumed ? 0 : Math.Max(0, (int)Math.Ceiling(_expires - _time.Elapsed.TotalSeconds)); } }
        public string Code { get { return SecondsLeft > 0 ? Volatile.Read(ref _pin) : ""; } }
        public bool InvitationOpen { get { return _host && Code.Length != 0; } }
        public bool CustomSecret { get { return false; } }    // LAN uses the random 10-second PIN only
        public string ConfirmedRoomName { get { return ""; } } // LAN shows names from discovery
        internal static LanPairing Host(string name = "Комната") { return new LanPairing(true, LanPake.CreatePin(), null, name); }
        internal static LanPairing Join(string code, IPAddress address = null)
        {
            code = (code ?? "").Trim();
            if (!LanPake.ValidPin(code)) throw new ArgumentException("Введите шесть цифр кода комнаты");
            return new LanPairing(false, code, address, null);
        }
        internal LanRoomAdvertisement DetachAdvertisement()
        {
            lock (_gate) { _ownsAdvertisement = false; return _advertisement; }
        }
#if SECURITY_CHECKS
        internal int ListenPortForChecks { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }
        internal static LanPairing HostLoopbackForChecks() { return new LanPairing(true,LanPake.CreatePin(),null,"checks",true,0); }
        internal static LanPairing JoinLoopbackForChecks(string code,int port) {
            if(!LanPake.ValidPin(code) || port<1024 || port>65535)throw new ArgumentException("Invalid test invitation");
            return new LanPairing(false,code,IPAddress.Loopback,null,true,port);
        }
#endif
        private LanPairing(bool host, string pin, IPAddress address, string name, bool loopbackOnly = false, int pairingPort = LanDiscovery.PairingPort)
        {
            LanSecureTransport.VerifyLibrary();
            _host = host; _pin = pin; _room = host ? LanPake.Nonce() : null;
            _discovery = new LanDiscovery(false,loopbackOnly); _requestedAddress = address; _pairingPort=pairingPort;
            try
            {
                if (address != null && !_discovery.Local(address)) throw new ArgumentException("Нужен IPv4 адрес из вашей локальной подсети");
                if (host)
                {
                    _listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, pairingPort);
                    _listener.Server.ExclusiveAddressUse = true; _listener.Start(8);
                    if(!loopbackOnly) { _advertisement = new LanRoomAdvertisement(_room, name); _ownsAdvertisement = true; }
                }
                _expires = _time.Elapsed.TotalSeconds + LanLocalText.PinSeconds;
                SetStatus(host ? "Код готов; передайте другу в течение " + LanLocalText.PinSeconds + " секунд" : "Поиск комнаты в локальной сети");
                _worker = new Thread(Run) { IsBackground = true, Name = "Zompiercer LAN pairing" }; _worker.Start();
            }
            catch { _listener?.Stop(); _advertisement?.Dispose(); _discovery.Dispose(); _pin = ""; throw; }
        }
        public void Approve() { if (PendingApproval) Interlocked.CompareExchange(ref _decision, 1, 0); }
        public void Reject() {
            lock(_gate) { Interlocked.Exchange(ref _decision,2);LanPake.Clear(_secret);_secret=null;_credentialsReady=false; }
        }
        public void TransportReady() { Interlocked.Exchange(ref _transportReady, 1); }
        public bool TryTakeCredentials(out byte[] secret, out IPAddress peer)
        {
            lock (_gate)
            {
                secret = null; peer = null;
                if (_stopping || !_credentialsReady || _host && Volatile.Read(ref _decision)!=1) return false;
                secret = (byte[])_secret.Clone(); peer = _peer;
                LanPake.Clear(_secret); _secret = null; _credentialsReady = false;
                return true;
            }
        }
        private void Credentials(byte[] secret, IPAddress peer)
        {
            lock (_gate)
            {
                if (_stopping || _host && Volatile.Read(ref _decision)!=1) throw new OperationCanceledException();
                _secret = (byte[])secret.Clone(); _peer = peer; _credentialsReady = true;
            }
        }
        private void SetStatus(string text) { Volatile.Write(ref _status, text); }
        private void Run()
        {
            try { if (_host) RunHost(); else RunClient(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_stopping) { _failure=LanNetworkFailure.From(ex,LanFault.PairingExchangeFailed);SetStatus(LanFaultException.MessageFor(_failure)); }
            }
            finally
            {
                if (_stopping) _failure = LanFault.None;
                _pin = ""; _pending = false; _consumed = true;
                lock(_gate) if(!_succeeded) { LanPake.Clear(_secret);_secret=null;_credentialsReady=false; }
                _advertisement?.SetFlags(_succeeded ? (byte)2 : (byte)0);
                CloseConnection(); _listener?.Stop(); _discovery.Dispose(); _finished = true;
            }
        }
        private void RunHost()
        {
            int attempts = 0;
            while (!_stopping && !Expired && attempts < 3)
            {
                if (!_listener.Pending()) { Thread.Sleep(5); continue; }
                var socket = _listener.AcceptSocket();
                var peer = ((IPEndPoint)socket.RemoteEndPoint).Address;
                if (!_discovery.Local(peer)) { socket.Dispose(); continue; }
                // Count even malformed/time-out exchanges. No per-address table an
                // attacker can grow; at most three executions per invitation.
                attempts++;
                SetConnection(socket);
                try
                {
                    Configure(socket); SetStatus("Проверка кода друга");
                    // Public invitation context over TCP also supports manual IP
                    // when broadcast/discovery UDP is blocked. This proves no identity.
                    WriteFrame(socket, 5, _room, _expires);
                    var hello = ReadFrame(socket, 0, _expires);
                    if (hello.Length != 32) throw new InvalidDataException("Pairing hello size");
                    var room = Slice(hello, 0, 16); var nonce = Slice(hello, 16, 16);
                    if (!Arrays.AreEqual(room, _room)) throw new InvalidDataException("Stale invitation");
                    using (var pake = new LanPake(true, room, nonce, _pin))
                    using (var keys = Exchange(pake, socket, _expires))
                    {
                        Check(_expires); // PIN proof must finish inside its 10-second window.
                        _consumed = true; _pin = ""; Volatile.Write(ref _pendingPeer,peer.ToString()); _pending = true;
                        _advertisement?.SetFlags(2);
                        SetStatus("Друг подтвердил код. Разрешите подключение");
                        double decisionDeadline = _time.Elapsed.TotalSeconds + 30;
                        while (Volatile.Read(ref _decision) == 0) { Check(decisionDeadline); Thread.Sleep(10); }
                        _pending = false;
                        if (Volatile.Read(ref _decision) != 1)
                        { SetStatus("Подключение отклонено хостом; создайте новый код"); return; }
                        Credentials(keys.Secret, peer);
                        // Main thread opens the actual DTLS listener before the
                        // authenticated approval is delivered to the client.
                        double readyDeadline = _time.Elapsed.TotalSeconds + 10;
                        while (Volatile.Read(ref _transportReady) == 0)
                        {
                            Check(readyDeadline);
                            if (Volatile.Read(ref _decision) != 1) throw new OperationCanceledException();
                            Thread.Sleep(10);
                        }
                        WriteFrame(socket, 4, keys.ApprovalTag(), readyDeadline);
                        _failure = LanFault.None; _succeeded = true;
                        SetStatus("Подключение разрешено; устанавливается защищённая связь");
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (_consumed) throw;
                    SetStatus(attempts < 3 ? "Код не подтверждён; ожидание друга" : "Лимит попыток исчерпан; создайте новый код");
                }
                finally { CloseConnection(); }
            }
            _pin = "";
            SetStatus(attempts >= 3 ? "Лимит попыток исчерпан; создайте новый код" : "Код истёк. Нажмите «Новый код»");
        }
        private void RunClient()
        {
            var candidates = _requestedAddress == null ? _discovery.Find(() => !_stopping && !Expired) :
                new System.Collections.Generic.List<LanDiscovery.Candidate> { new LanDiscovery.Candidate { Address = _requestedAddress } };
            if (_stopping) throw new OperationCanceledException();
            if (candidates.Count == 0)
            { _failure=LanFault.NoRooms;SetStatus(LanFaultException.MessageFor(_failure)); return; }
            int attempts = 0;
            foreach (var candidate in candidates)
            {
                if (++attempts > 3 || Expired || _stopping) break;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                SetConnection(socket);
                try
                {
                    LanRoute.BindPeer(socket, candidate.Address, 0);
                    Configure(socket); SetStatus("Проверка найденной комнаты");
                    var connect = socket.BeginConnect(new IPEndPoint(candidate.Address, _pairingPort), null, null);
                    using (var wait = connect.AsyncWaitHandle)
                    {
                        double connectDeadline = Math.Min(_expires, _time.Elapsed.TotalSeconds + 1);
                        while (!wait.WaitOne(20)) Check(connectDeadline);
                    }
                    socket.EndConnect(connect); Check(_expires);
                    byte[] room = ReadFrame(socket, 5, _expires);
                    if (room.Length != 16 || (candidate.Room != null && !Arrays.AreEqual(room, candidate.Room)))
                        throw new InvalidDataException("Invitation context changed");
                    var nonce = LanPake.Nonce(); var hello = new byte[32];
                    Buffer.BlockCopy(room, 0, hello, 0, 16); Buffer.BlockCopy(nonce, 0, hello, 16, 16);
                    WriteFrame(socket, 0, hello, _expires);
                    using (var pake = new LanPake(false, room, nonce, _pin))
                    using (var keys = Exchange(pake, socket, Math.Min(_expires, _time.Elapsed.TotalSeconds + 3)))
                    {
                        _consumed = true; _pin = "";
                        SetStatus("Код подтверждён. Ожидание разрешения хоста");
                        byte[] approval = ReadFrame(socket, 4, _time.Elapsed.TotalSeconds + 30);
                        byte[] expected = keys.ApprovalTag();
                        try { if (!Arrays.FixedTimeEquals(expected, approval)) throw new InvalidDataException("Host approval invalid"); }
                        finally { LanPake.Clear(expected); }
                        Credentials(keys.Secret, candidate.Address);
                        _failure = LanFault.None; _succeeded = true;
                        SetStatus("Комната найдена; устанавливается защищённая связь"); return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (_consumed) throw;_failure=LanNetworkFailure.From(ex,LanFault.PairingExchangeFailed); }
                finally { CloseConnection(); }
            }
            if (_stopping) throw new OperationCanceledException();
            if(_failure==LanFault.None)_failure=LanFault.NoHostResponse;
            SetStatus(LanFaultException.MessageFor(_failure));
        }
        private LanPake.Keys Exchange(LanPake pake, Socket socket, double deadline)
        {
            // Alternating TCP messages avoid unbounded UDP fragmentation and retry state.
            var local1 = pake.Round1();
            byte[] remote1;
            if (_host) { WriteFrame(socket, 1, local1, deadline); remote1 = ReadFrame(socket, 1, deadline); }
            else { remote1 = ReadFrame(socket, 1, deadline); WriteFrame(socket, 1, local1, deadline); }
            pake.AcceptRound1(remote1); Check(deadline);
            var local2 = pake.Round2(); byte[] remote2;
            if (_host) { WriteFrame(socket, 2, local2, deadline); remote2 = ReadFrame(socket, 2, deadline); }
            else { remote2 = ReadFrame(socket, 2, deadline); WriteFrame(socket, 2, local2, deadline); }
            pake.AcceptRound2(remote2); Check(deadline);
            var local3 = pake.Round3(); byte[] remote3;
            if (_host) { WriteFrame(socket, 3, local3, deadline); remote3 = ReadFrame(socket, 3, deadline); }
            else { remote3 = ReadFrame(socket, 3, deadline); WriteFrame(socket, 3, local3, deadline); }
            try { pake.AcceptRound3(remote3); }
            catch (Org.BouncyCastle.Crypto.CryptoException) { throw new LanFaultException(LanFault.PairingProofFailed); }
            Check(deadline);
            return pake.DeriveKeys();
        }
        private void Check(double deadline)
        {
            if (_stopping) throw new OperationCanceledException();
            if (_time.Elapsed.TotalSeconds >= deadline) throw new TimeoutException();
        }
        private byte[] ReadFrame(Socket socket, byte kind, double deadline)
        {
            var header = ReadExact(socket, 8, deadline);
            int size = BitConverter.ToUInt16(header, 6);
            if (BitConverter.ToUInt32(header, 0) != Magic || header[4] != 1 || header[5] != kind || size < 1 || size > LanPake.MaximumFrame)
                throw new InvalidDataException("Pairing frame rejected");
            return ReadExact(socket, size, deadline);
        }
        private byte[] ReadExact(Socket socket, int size, double deadline)
        {
            var bytes = new byte[size]; int offset = 0;
            while (offset < size)
            {
                Check(deadline);
                if (!socket.Poll(5000, SelectMode.SelectRead)) continue;
                int count;
                try { count = socket.Receive(bytes, offset, size - offset, SocketFlags.None); }
                catch (SocketException ex) { if (ex.SocketErrorCode == SocketError.WouldBlock) continue; throw; }
                if (count == 0) throw new EndOfStreamException(); offset += count;
            }
            Check(deadline); return bytes;
        }
        private void WriteFrame(Socket socket, byte kind, byte[] bytes, double deadline)
        {
            if (bytes.Length < 1 || bytes.Length > LanPake.MaximumFrame) throw new InvalidDataException("Pairing frame size");
            var frame = new byte[bytes.Length + 8];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, frame, 0, 4); frame[4] = 1; frame[5] = kind;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)bytes.Length), 0, frame, 6, 2); Buffer.BlockCopy(bytes, 0, frame, 8, bytes.Length);
            int offset = 0;
            while (offset < frame.Length)
            {
                Check(deadline);
                if (!socket.Poll(5000, SelectMode.SelectWrite)) continue;
                try
                {
                    int count = socket.Send(frame, offset, frame.Length - offset, SocketFlags.None);
                    if (count == 0) throw new EndOfStreamException(); offset += count;
                }
                catch (SocketException ex) { if (ex.SocketErrorCode != SocketError.WouldBlock) throw; }
            }
            Check(deadline);
        }
        private static void Configure(Socket socket) { socket.NoDelay = true; socket.Blocking = false; }
        private void SetConnection(Socket socket)
        {
            lock (_gate)
            {
                if (_stopping) { socket.Dispose(); throw new OperationCanceledException(); }
                _connection = socket;
            }
        }
        private void CloseConnection()
        { lock (_gate) { try { _connection?.Dispose(); } catch { } _connection = null; } }
        private static byte[] Slice(byte[] bytes, int offset, int count)
        { var result = new byte[count]; Buffer.BlockCopy(bytes, offset, result, 0, count); return result; }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true; _pin = ""; _pending = false; _credentialsReady = false;
                LanPake.Clear(_secret); _secret = null;
            }
            _listener?.Stop(); _discovery.Dispose(); CloseConnection();
            if (_ownsAdvertisement) _advertisement?.Dispose();
            if (_worker != null && Thread.CurrentThread != _worker) _worker.Join(100);
        }
    }
}
