using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using Org.BouncyCastle.Utilities;

namespace ZompiercerLAN
{
    internal static class LanNetworkFailure
    {
        internal static LanFault From(Exception error, LanFault fallback=LanFault.Runtime)
        {
            var known=error as LanFaultException;if(known!=null)return known.Fault;
            var socket=error as SocketException;
            if(socket!=null) {
                if(socket.SocketErrorCode==SocketError.AccessDenied)return LanFault.SocketAccessDenied;
                if(socket.SocketErrorCode==SocketError.ConnectionRefused)return LanFault.ConnectionRefused;
                if(socket.SocketErrorCode==SocketError.TimedOut || socket.SocketErrorCode==SocketError.HostUnreachable ||
                    socket.SocketErrorCode==SocketError.NetworkUnreachable)return LanFault.NoHostResponse;
            }
            return error is TimeoutException?LanFault.NoHostResponse:fallback;
        }
        internal static LanFault DiscoveryResult(bool hasAdapter,int sent,int failed,bool denied)
        {
            if(!hasAdapter)return LanFault.NoLanAdapter;
            return sent==0 && failed>0?(denied?LanFault.SocketAccessDenied:LanFault.DiscoverySendFailed):LanFault.None;
        }
    }
    // Adapter addresses, not the system route table: VPN-injected prefixes must
    // not choose the interface used to contact a directly attached LAN peer.
    internal sealed class LanRoute
    {
        internal readonly IPAddress Address, Mask, Broadcast;
        internal readonly int InterfaceIndex, PrefixLength;
        internal LanRoute(IPAddress address, IPAddress mask, int index)
        {
            Address = address; Mask = mask; InterfaceIndex = index;
            var ip = address.GetAddressBytes(); var bits = mask.GetAddressBytes(); var broadcast = new byte[4];
            bool zero = false; int prefix = 0;
            for (int i = 0; i < 4; i++)
            {
                broadcast[i] = (byte)(ip[i] | ~bits[i]);
                for (int bit = 7; bit >= 0; bit--)
                    if ((bits[i] & (1 << bit)) == 0) zero = true;
                    else { if (zero) throw new ArgumentException("Noncontiguous IPv4 mask"); prefix++; }
            }
            if (prefix == 0 || prefix > 30 || index <= 0 || !LanNetworkPolicy.PrivatePeer(address))
                throw new ArgumentException("Not a usable private IPv4 LAN interface");
            PrefixLength = prefix; Broadcast = new IPAddress(broadcast);
        }
        internal bool Contains(IPAddress peer)
        {
            if (!LanNetworkPolicy.PrivatePeer(peer) || IPAddress.IsLoopback(peer) || peer.Equals(Broadcast)) return false;
            var ip = Address.GetAddressBytes(); var mask = Mask.GetAddressBytes(); var bytes = peer.GetAddressBytes();
            bool network = true;
            for (int i = 0; i < 4; i++)
            {
                if ((bytes[i] & mask[i]) != (ip[i] & mask[i])) return false;
                if (bytes[i] != (ip[i] & mask[i])) network = false;
            }
            return !network;
        }
        internal static List<LanRoute> Read(out HashSet<IPAddress> own)
        {
            own = new HashSet<IPAddress>(); var routes = new List<LanRoute>(16);
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                var properties = adapter.GetIPProperties(); var ipv4 = properties.GetIPv4Properties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    own.Add(unicast.Address);
                    if (routes.Count == 16 || ipv4 == null || IPAddress.IsLoopback(unicast.Address) || unicast.IPv4Mask == null) continue;
                    try { routes.Add(new LanRoute(unicast.Address, unicast.IPv4Mask, ipv4.Index)); }
                    catch (ArgumentException) { }
                }
            }
            return routes;
        }
        internal static LanRoute Select(IEnumerable<LanRoute> routes, IPAddress peer, int incomingInterface = 0)
        {
            LanRoute best = null;
            foreach (var route in routes)
                if ((incomingInterface == 0 || incomingInterface == route.InterfaceIndex) && route.Contains(peer) &&
                    (best == null || route.PrefixLength > best.PrefixLength)) best = route;
            return best;
        }
        internal const int UnicastInterfaceOption = 31;
        internal int InterfaceOptionValue { get { return IPAddress.HostToNetworkOrder(InterfaceIndex); } }
        internal void PinUdp(Socket socket)
        {
            // Windows IP_UNICAST_IF requires the interface index in network order.
            // Failure is fatal for this send; never silently retry through a VPN.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)UnicastInterfaceOption, InterfaceOptionValue);
        }
        internal static IPAddress BindPeer(Socket socket, IPAddress peer, int port)
        {
            IPAddress source;
            if (IPAddress.IsLoopback(peer)) source = IPAddress.Loopback;
            else
            {
                HashSet<IPAddress> own; var route = Select(Read(out own), peer);
                if (route == null) throw new ArgumentException("Peer is not on a private local subnet");
                source = route.Address;
                if (socket.SocketType == SocketType.Dgram) route.PinUdp(socket);
            }
            socket.Bind(new IPEndPoint(source, port)); return source;
        }
    }
    // Public LAN discovery: no PIN, password hash or password-derived verifier.
    // Responses are untrusted hints; only J-PAKE authenticates the selected host.
    internal sealed class LanDiscovery : IDisposable
    {
        internal const int DiscoveryPort = 27776, PairingPort = 27778, GamePort = 27777;
        private const uint Magic = 0x504D4C5A;
        private readonly Socket _socket;
        private readonly List<LanRoute> _routes;
        private readonly HashSet<IPAddress> _own;
        private readonly bool _loopbackOnly;
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private double _tokens = 32, _last, _responses = 10, _lastResponse;
        private int _sent,_failed;
        private bool _sendDenied;
        internal sealed class Candidate
        {
            internal IPAddress Address;
            internal byte[] Room;
            internal string Name;
            internal byte Flags;
            internal bool PinActive { get { return Flags == 1; } }
            internal bool Busy { get { return Flags == 2; } }
        }
        private static readonly UTF8Encoding TextEncoding = new UTF8Encoding(false, true);
        internal static string CleanName(string name)
        {
            var clean = new StringBuilder();
            foreach (char c in name ?? "")
                if (clean.Length < 20 && (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')) clean.Append(c);
            string result = clean.ToString().Trim();
            while (TextEncoding.GetByteCount(result) > 20) result = result.Substring(0, result.Length - 1);
            return result.Length == 0 ? "Комната" : result;
        }

        internal LanDiscovery(bool host, bool loopbackOnly = false)
        {
            _loopbackOnly = loopbackOnly;
            if (loopbackOnly) { _routes = new List<LanRoute>(); _own = new HashSet<IPAddress>(); }
            else _routes = LanRoute.Read(out _own);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                _socket.ExclusiveAddressUse = true; _socket.EnableBroadcast = true; _socket.ReceiveBufferSize = 32768;
                _socket.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, host ? DiscoveryPort : 0)); _socket.Blocking = false;
            }
            catch { _socket.Dispose(); throw; }
        }
        internal bool Local(IPAddress address)
        {
            return _loopbackOnly ? address != null && address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address) :
                IsRemote(address, _own) && LanRoute.Select(_routes, address) != null;
        }
        internal static bool IsRemote(IPAddress address, HashSet<IPAddress> own)
        { return LanNetworkPolicy.PrivatePeer(address) && !IPAddress.IsLoopback(address) && !own.Contains(address); }
        private bool Receive(out IPEndPoint peer, out byte[] packet, out LanRoute route)
        {
            peer = null; packet = null; route = null;
            if (!_socket.Poll(1000, SelectMode.SelectRead)) return false;
            // One spare byte detects oversized datagrams even on runtimes that
            // truncate ReceiveFrom instead of reporting SocketError.MessageSize.
            var bytes = new byte[65]; EndPoint from = new IPEndPoint(IPAddress.Any, 0); int size;
            try
            {
                SocketFlags flags = SocketFlags.None; IPPacketInformation info;
                size = _socket.ReceiveMessageFrom(bytes, 0, bytes.Length, ref flags, ref from, out info);
                if ((flags & SocketFlags.Truncated) != 0) return false;
                if (!_loopbackOnly)
                {
                    route = LanRoute.Select(_routes, ((IPEndPoint)from).Address, info.Interface);
                    if (info.Interface == 0 || route == null) return false;
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.ConnectionReset) return false;
                throw;
            }
            double now = _time.Elapsed.TotalSeconds;
            _tokens = Math.Min(32, _tokens + Math.Max(0, now - _last) * 50); _last = now;
            if (_tokens < 1) return false; _tokens--;
            if (size != 64 || !Local(((IPEndPoint)from).Address) || BitConverter.ToUInt32(bytes, 0) != Magic || bytes[4] != 2) return false;
            Array.Resize(ref bytes, 64);
            peer = (IPEndPoint)from; packet = bytes; return true;
        }
        internal void Advertise(byte[] room, byte flags, string name)
        {
            for (int i = 0; i < 16; i++)
            {
                IPEndPoint from; byte[] query; LanRoute route;
                if (!Receive(out from, out query, out route)) break;
                if(from.Port<1024) continue; // Do not reflect discovery replies into privileged UDP services.
                if (query[5] != 1) continue;
                bool padding = true;
                for (int j = 22; j < 64; j++) if (query[j] != 0) { padding = false; break; }
                if (!padding) continue;
                double now = _time.Elapsed.TotalSeconds;
                _responses = Math.Min(10, _responses + Math.Max(0, now - _lastResponse) * 10); _lastResponse = now;
                if (_responses < 1) continue; _responses--;
                var reply = Header(2); Buffer.BlockCopy(query, 6, reply, 6, 16); Buffer.BlockCopy(room, 0, reply, 22, 16);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)PairingPort), 0, reply, 38, 2);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)GamePort), 0, reply, 40, 2);
                reply[42] = flags;
                var label = TextEncoding.GetBytes(CleanName(name)); reply[43] = (byte)label.Length;
                Buffer.BlockCopy(label, 0, reply, 44, label.Length);
                Send(reply, from, route);
            }
        }
        internal List<Candidate> Find(Func<bool> active, IPAddress target = null, bool browse = false)
        {
            _sent=0;_failed=0;_sendDenied=false;
            if(!_loopbackOnly && _routes.Count==0)throw new LanFaultException(LanFault.NoLanAdapter);
            if (target != null && !Local(target)) throw new ArgumentException("Нужен IPv4 адрес из вашей локальной подсети");
            var found = new List<Candidate>(8); var nonce = LanPake.Nonce(); var query = Header(1);
            Buffer.BlockCopy(nonce, 0, query, 6, 16);
            var watch = Stopwatch.StartNew(); double nextSend = 0;
            while (active() && watch.Elapsed.TotalSeconds < (browse || found.Count == 0 ? 1.2 : .35))
            {
                if (watch.Elapsed.TotalSeconds >= nextSend)
                {
                    if (_loopbackOnly) Send(query, new IPEndPoint(IPAddress.Loopback, DiscoveryPort), null);
                    else if (target != null) Send(query, new IPEndPoint(target, DiscoveryPort), LanRoute.Select(_routes, target));
                    else foreach (var route in _routes) Send(query, new IPEndPoint(route.Broadcast, DiscoveryPort), route);
                    nextSend = watch.Elapsed.TotalSeconds + .4;
                }
                IPEndPoint from; byte[] reply; LanRoute incoming;
                if (!Receive(out from, out reply, out incoming)) { Thread.Sleep(5); continue; }
                if (reply[5] != 2 || from.Port != DiscoveryPort || BitConverter.ToUInt16(reply, 38) != PairingPort || BitConverter.ToUInt16(reply, 40) != GamePort) continue;
                if (target != null && !target.Equals(from.Address)) continue;
                int labelSize = reply[43];
                if (reply[42] > 2 || labelSize < 1 || labelSize > 20 || (!browse && reply[42] != 1)) continue;
                bool same = true;
                for (int i = 0; i < 16; i++) if (reply[6 + i] != nonce[i]) { same = false; break; }
                for (int i = 44 + labelSize; i < 64; i++) if (reply[i] != 0) same = false;
                if (!same) continue;
                string label;
                try { label = TextEncoding.GetString(reply, 44, labelSize); }
                catch (DecoderFallbackException) { continue; }
                if (CleanName(label) != label) continue;
                var room = new byte[16]; Buffer.BlockCopy(reply, 22, room, 0, 16);
                bool duplicate = false;
                foreach (var item in found) if (item.Address.Equals(from.Address) && Arrays.AreEqual(item.Room, room)) duplicate = true;
                if (!duplicate && found.Count < 8) found.Add(new Candidate { Address = from.Address, Room = room, Name = label, Flags = reply[42] });
            }
            var failure=LanNetworkFailure.DiscoveryResult(_loopbackOnly || _routes.Count!=0,_sent,_failed,_sendDenied);
            if(failure!=LanFault.None)throw new LanFaultException(failure);
            return found;
        }
        private static byte[] Header(byte kind)
        {
            var bytes = new byte[64]; Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, bytes, 0, 4);
            bytes[4] = 2; bytes[5] = kind; return bytes;
        }
        private void Send(byte[] bytes, IPEndPoint peer, LanRoute route)
        {
            try { if (!_loopbackOnly) { if (route == null) { if(_failed<int.MaxValue)_failed++;return; } route.PinUdp(_socket); } _socket.SendTo(bytes, peer);if(_sent<int.MaxValue)_sent++; }
            catch (SocketException ex)
            {
                if(_failed<int.MaxValue)_failed++;if(ex.SocketErrorCode==SocketError.AccessDenied)_sendDenied=true;
                if (ex.SocketErrorCode != SocketError.NetworkUnreachable && ex.SocketErrorCode != SocketError.HostUnreachable &&
                    ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.NoBufferSpaceAvailable && ex.SocketErrorCode != SocketError.AccessDenied &&
                    ex.SocketErrorCode != SocketError.ConnectionReset) throw;
            }
        }
        public void Dispose() { _socket.Dispose(); }
    }
}
