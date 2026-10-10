using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Utilities;

namespace ZompiercerLAN
{
    // The worker's view of an invitation, LAN or relay.
    internal interface ILanPairing : IDisposable
    {
        bool PendingApproval { get; }
        string PendingPeer { get; }
        bool Finished { get; }
        bool Succeeded { get; }
        bool Expired { get; }
        string Code { get; }
        int SecondsLeft { get; }
        LanFault Failure { get; }
        // Host: the invitation can still be used (PIN or host password not yet consumed).
        bool InvitationOpen { get; }
        // Host: the password was chosen by the host; it is never echoed in statuses.
        bool CustomSecret { get; }
        // Guest: the host's room name, known only after mutual J-PAKE confirmation.
        string ConfirmedRoomName { get; }
        void Approve();
        void Reject();
        void TransportReady();
        bool TryTakeCredentials(out byte[] secret, out IPAddress peer);
    }

    // J-PAKE invitation through the relay. Same frames, attempt cap and host
    // approval as LanPairing; only the carrier differs (relay DATA instead of a
    // direct TCP socket). The secret (random PIN or the host's own password)
    // never reaches the relay: it only forwards J-PAKE messages it cannot use.
    // The room context frame also carries the host's room name; it is bound into
    // the J-PAKE context, so the guest shows it only after mutual confirmation.
    internal sealed class LanRelayPairing : ILanPairing
    {
        private const uint Magic = 0x33504C5A; // same frame format as LanPairing
        private static readonly UTF8Encoding Text = new UTF8Encoding(false, true);
        private readonly object _gate = new object();
        private readonly bool _host, _custom, _listed;
        // A listed room (0.13.0) waits longer for a guest and takes more tries (its password is
        // the host's own and strong); the relay's join window is reopened as it runs out.
        internal const int PublicSeconds = 1700, PublicAttempts = 10;
        // One guest gets this long to prove the secret; one that joins and stays silent
        // (holding the only guest seat) is removed instead of blocking the room.
        private const double AttemptSeconds = 20;
        private const double ReopenEvery = 240;
        private readonly LanRelayLink _link;
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private readonly Thread _worker;
        private readonly byte[] _room;
        private readonly string _name;
        private readonly double _expires;
        private string _pin, _confirmedName = "";
        private volatile bool _stopping, _finished, _consumed, _pending, _succeeded;
        private int _decision, _transportReady;
        private bool _credentialsReady;
        private byte[] _secret;
        private LanFault _failure;

        public LanFault Failure { get { return _failure; } }
        public bool PendingApproval { get { return _pending && !_finished && Volatile.Read(ref _decision) == 0; } }
        // The relay hides the friend's address; the game shows "via relay" instead.
        public string PendingPeer { get { return _pending ? LanRelayText.VirtualPeer.ToString() : ""; } }
        public bool Finished { get { return _finished; } }
        public bool Succeeded { get { return _succeeded; } }
        public bool Expired { get { return _time.Elapsed.TotalSeconds >= _expires; } }
        public int SecondsLeft { get { return _consumed || !_host ? 0 : Math.Max(0, (int)Math.Ceiling(_expires - _time.Elapsed.TotalSeconds)); } }
        // The invitation shows only once the relay room exists, so the room id and
        // the secret are handed out together.
        public bool InvitationOpen { get { return _host && !_consumed && SecondsLeft > 0 && _link.Ready; } }
        public bool CustomSecret { get { return _custom; } }
        public string ConfirmedRoomName { get { return Volatile.Read(ref _confirmedName); } }
        // Host: only a random PIN is shown back; the host's own password never is.
        // Guest: non-empty until the secret is consumed by the proof (worker-internal).
        public string Code
        {
            get
            {
                if (!_host) return _consumed ? "" : Volatile.Read(ref _pin);
                return InvitationOpen && !_custom ? Volatile.Read(ref _pin) : "";
            }
        }
        internal string Room { get { return InvitationOpen ? _link.Room : ""; } }

        // secret: empty for a random six-digit PIN, otherwise the host's own password.
        internal static LanRelayPairing Host(LanRelayLink link, string name = "Комната", string secret = "", bool listed = false)
        {
            secret = secret ?? "";
            if (secret.Length != 0 && !LanRelayText.ValidSecret(secret) || listed && !LanRelayText.StrongPublicSecret(secret)) throw new ArgumentException("Room password");
            return new LanRelayPairing(true, secret.Length != 0 ? secret : LanPake.CreatePin(), secret.Length != 0, LanLocalText.CleanName(name), link, listed);
        }
        internal static LanRelayPairing Join(string secret, LanRelayLink link)
        {
            if (!LanRelayText.ValidSecret(secret)) throw new ArgumentException("Room secret");
            return new LanRelayPairing(false, secret, false, "", link);
        }
        private LanRelayPairing(bool host, string pin, bool custom, string name, LanRelayLink link, bool listed = false)
        {
            LanSecureTransport.VerifyLibrary();
            _host = host; _pin = pin; _custom = custom; _name = name; _link = link; _listed = listed;
            _room = host ? LanPake.Nonce() : null;
            // Host: PIN lifetime (a listed room: longer). Guest: time to reach the relay, join and prove the PIN.
            _expires = host ? (listed ? PublicSeconds : LanRelayText.PinSeconds) : 45;
            _worker = new Thread(Run) { IsBackground = true, Name = "Zompiercer relay pairing" };
            _worker.Start();
        }

        public void Approve() { if (PendingApproval) Interlocked.CompareExchange(ref _decision, 1, 0); }
        public void Reject()
        {
            lock (_gate) { Interlocked.Exchange(ref _decision, 2); LanPake.Clear(_secret); _secret = null; _credentialsReady = false; }
        }
        public void TransportReady() { Interlocked.Exchange(ref _transportReady, 1); }
        public bool TryTakeCredentials(out byte[] secret, out IPAddress peer)
        {
            lock (_gate)
            {
                secret = null; peer = null;
                if (_stopping || !_credentialsReady || _host && Volatile.Read(ref _decision) != 1) return false;
                secret = (byte[])_secret.Clone(); peer = LanRelayText.VirtualPeer;
                LanPake.Clear(_secret); _secret = null; _credentialsReady = false;
                return true;
            }
        }
        private void Credentials(byte[] secret)
        {
            lock (_gate)
            {
                if (_stopping || _host && Volatile.Read(ref _decision) != 1) throw new OperationCanceledException();
                _secret = (byte[])secret.Clone(); _credentialsReady = true;
            }
        }

        private void Run()
        {
            try
            {
                WaitForRelay();
                if (_host) RunHost(); else RunClient();
            }
            catch (OperationCanceledException) { }
            catch (CryptoProofException) { if (!_stopping) _failure = LanFault.PairingProofFailed; }
            catch (LanFaultException ex) { if (!_stopping) _failure = ex.Fault; }
            catch (Exception) { if (!_stopping) _failure = _link.Failure != LanFault.None ? _link.Failure : LanFault.PairingExchangeFailed; }
            finally
            {
                if (_stopping) _failure = LanFault.None;
                _pin = ""; _pending = false; _consumed = true;
                lock (_gate) if (!_succeeded) { LanPake.Clear(_secret); _secret = null; _credentialsReady = false; }
                _finished = true;
            }
        }

        private void WaitForRelay()
        {
            while (!_link.Ready)
            {
                if (_link.Failure != LanFault.None) throw new LanFaultException(_link.Failure);
                Check(_expires);
                Thread.Sleep(10);
            }
        }

        private void RunHost()
        {
            int attempts = 0;
            double reopenAt = _time.Elapsed.TotalSeconds + ReopenEvery;
            while (!Expired && attempts < (_listed ? PublicAttempts : 3))
            {
                Check(double.MaxValue);
                if (_listed && _time.Elapsed.TotalSeconds >= reopenAt) { _link.Reopen(); reopenAt = _time.Elapsed.TotalSeconds + ReopenEvery; }
                var joined = _link.NextEvent(50);
                if (joined == null) { RelayCheck(); continue; }
                // After each try the relay's own three attempts may be spent: reopen a listed room.
                if (_listed) reopenAt = Math.Min(reopenAt, _time.Elapsed.TotalSeconds + 3);
                if (joined.Kind != LanRelayEventKind.PeerJoined) { Wipe(joined); continue; }
                // Count even malformed/time-out exchanges: at most three per invitation.
                attempts++;
                try
                {
                    var name = Text.GetBytes(_name);
                    var context = new byte[16 + name.Length];
                    Buffer.BlockCopy(_room, 0, context, 0, 16); Buffer.BlockCopy(name, 0, context, 16, name.Length);
                    WriteFrame(5, context);
                    double attemptDeadline = Math.Min(_expires, _time.Elapsed.TotalSeconds + AttemptSeconds);
                    var hello = ReadFrame(0, attemptDeadline);
                    if (hello.Length != 32) throw new InvalidDataException("Pairing hello size");
                    var room = Slice(hello, 0, 16); var nonce = Slice(hello, 16, 16);
                    if (!Arrays.AreEqual(room, _room)) throw new InvalidDataException("Stale invitation");
                    using (var pake = new LanPake(true, room, nonce, _pin, LanPake.RoomNameBinding(_name)))
                    using (var keys = Exchange(pake, attemptDeadline))
                    {
                        Check(attemptDeadline); // the proof must finish inside this attempt and the invitation window
                        _consumed = true; _pin = ""; _pending = true;
                        double decisionDeadline = _time.Elapsed.TotalSeconds + 30;
                        while (Volatile.Read(ref _decision) == 0) { Check(decisionDeadline); RelayCheck(); Thread.Sleep(10); }
                        _pending = false;
                        if (Volatile.Read(ref _decision) != 1) { _link.Kick(); return; }
                        Credentials(keys.Secret);
                        // The worker opens the relay DTLS listener before approval reaches the guest.
                        double readyDeadline = _time.Elapsed.TotalSeconds + 10;
                        while (Volatile.Read(ref _transportReady) == 0)
                        {
                            Check(readyDeadline);
                            if (Volatile.Read(ref _decision) != 1) throw new OperationCanceledException();
                            Thread.Sleep(10);
                        }
                        WriteFrame(4, keys.ApprovalTag());
                        _link.Lock();          // no further JOIN_ROOM for this room
                        _link.BeginSession();
                        _failure = LanFault.None; _succeeded = true;
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (PeerLeftException) { if (_consumed) throw; /* slot already free */ }
                catch (Exception)
                {
                    if (_consumed) throw;
                    // KICK_GUEST names no guest: sent late, it could remove the next,
                    // legitimate friend. A failed guest normally leaves by itself; only
                    // one that lingers (stuck or hostile) is removed.
                    if (!WaitForPeerLeft(5)) _link.Kick();
                }
            }
        }

        private void RunClient()
        {
            // Room context: 16-byte room nonce + the host's room name (1..20 UTF-8 bytes).
            byte[] context = ReadFrame(5, _expires);
            if (context.Length < 17 || context.Length > 16 + 20) throw new InvalidDataException("Invitation context");
            var room = Slice(context, 0, 16);
            string name = Text.GetString(context, 16, context.Length - 16);
            if (LanLocalText.CleanName(name) != name) throw new InvalidDataException("Invitation room name");
            var nonce = LanPake.Nonce(); var hello = new byte[32];
            Buffer.BlockCopy(room, 0, hello, 0, 16); Buffer.BlockCopy(nonce, 0, hello, 16, 16);
            WriteFrame(0, hello);
            using (var pake = new LanPake(false, room, nonce, _pin, LanPake.RoomNameBinding(name)))
            using (var keys = Exchange(pake, Math.Min(_expires, _time.Elapsed.TotalSeconds + 15)))
            {
                // Mutual confirmation passed: the name came from the holder of the secret.
                Volatile.Write(ref _confirmedName, name);
                _consumed = true; _pin = "";
                byte[] approval = ReadFrame(4, _time.Elapsed.TotalSeconds + 45);
                byte[] expected = keys.ApprovalTag();
                try { if (!Arrays.FixedTimeEquals(expected, approval)) throw new InvalidDataException("Host approval invalid"); }
                finally { LanPake.Clear(expected); }
                Credentials(keys.Secret);
                _link.BeginSession();
                _failure = LanFault.None; _succeeded = true;
            }
        }

        private LanPake.Keys Exchange(LanPake pake, double deadline)
        {
            var local1 = pake.Round1(); byte[] remote1;
            if (_host) { WriteFrame(1, local1); remote1 = ReadFrame(1, deadline); }
            else { remote1 = ReadFrame(1, deadline); WriteFrame(1, local1); }
            pake.AcceptRound1(remote1); Check(deadline);
            var local2 = pake.Round2(); byte[] remote2;
            if (_host) { WriteFrame(2, local2); remote2 = ReadFrame(2, deadline); }
            else { remote2 = ReadFrame(2, deadline); WriteFrame(2, local2); }
            pake.AcceptRound2(remote2); Check(deadline);
            var local3 = pake.Round3(); byte[] remote3;
            if (_host) { WriteFrame(3, local3); remote3 = ReadFrame(3, deadline); }
            else { remote3 = ReadFrame(3, deadline); WriteFrame(3, local3); }
            try { pake.AcceptRound3(remote3); }
            catch (Org.BouncyCastle.Crypto.CryptoException) { throw new CryptoProofException(); }
            Check(deadline);
            return pake.DeriveKeys();
        }

        private void WriteFrame(byte kind, byte[] bytes)
        {
            if (bytes.Length < 1 || bytes.Length > LanPake.MaximumFrame) throw new InvalidDataException("Pairing frame size");
            var frame = new byte[bytes.Length + 8];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, frame, 0, 4); frame[4] = 1; frame[5] = kind;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)bytes.Length), 0, frame, 6, 2); Buffer.BlockCopy(bytes, 0, frame, 8, bytes.Length);
            try { _link.SendData(frame); } finally { Array.Clear(frame, 0, frame.Length); }
        }

        // One relay DATA message carries exactly one pairing frame.
        private byte[] ReadFrame(byte kind, double deadline)
        {
            while (true)
            {
                Check(deadline);
                var e = _link.NextEvent(20);
                if (e == null) { RelayCheck(); continue; }
                if (e.Kind == LanRelayEventKind.PeerLeft) throw new PeerLeftException();
                if (e.Kind != LanRelayEventKind.Data) continue;
                try
                {
                    var data = e.Data;
                    int size = BitConverter.ToUInt16(data, 6);
                    if (BitConverter.ToUInt32(data, 0) != Magic || data[4] != 1 || data[5] != kind || size < 1 || size > LanPake.MaximumFrame || data.Length != 8 + size)
                        throw new InvalidDataException("Pairing frame rejected");
                    return Slice(data, 8, size);
                }
                finally { Wipe(e); }
            }
        }

        // The relay orders PEER_LEFT before any later PEER_JOINED, so waiting here
        // cannot swallow the next guest's arrival.
        private bool WaitForPeerLeft(double seconds)
        {
            double deadline = _time.Elapsed.TotalSeconds + seconds;
            while (_time.Elapsed.TotalSeconds < deadline)
            {
                Check(double.MaxValue);
                var e = _link.NextEvent(20);
                if (e == null) { RelayCheck(); continue; }
                Wipe(e);
                if (e.Kind == LanRelayEventKind.PeerLeft) return true;
            }
            return false;
        }

        private void RelayCheck() { if (_link.Failure != LanFault.None) throw new LanFaultException(_link.Failure); }
        private void Check(double deadline)
        {
            if (_stopping) throw new OperationCanceledException();
            if (_time.Elapsed.TotalSeconds >= deadline) throw new TimeoutException();
        }
        private static void Wipe(LanRelayEvent e) { if (e != null && e.Data != null) Array.Clear(e.Data, 0, e.Data.Length); }
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
            if (_worker != null && Thread.CurrentThread != _worker) _worker.Join(200);
        }

        private sealed class CryptoProofException : Exception { }
        private sealed class PeerLeftException : IOException { }
    }
}
