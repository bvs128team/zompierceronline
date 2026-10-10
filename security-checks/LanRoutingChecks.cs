using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static void LanRoutingChecks()
        {
            var ethernet = new LanRoute(IPAddress.Parse("192.168.50.10"), IPAddress.Parse("255.255.255.0"), 8);
            var vpn = new LanRoute(IPAddress.Parse("10.200.0.1"), IPAddress.Parse("255.255.255.0"), 42);
            var peer = IPAddress.Parse("192.168.50.20");
            // The VPN's injected route for 192.168.50/24 is intentionally not an
            // input: only addresses actually assigned to adapters are eligible.
            var routes = new[] { vpn, ethernet };
            Require(LanRoute.Select(routes, peer) == ethernet, "VPN route displaced directly attached LAN");
            Require(LanRoute.Select(routes, peer, 42) == null, "Reply crossed incoming interface");
            Require(LanRoute.Select(routes, peer, 8) == ethernet, "LAN incoming interface rejected");
            Require(ethernet.InterfaceOptionValue == IPAddress.HostToNetworkOrder(8) && LanRoute.UnicastInterfaceOption == 31,
                "Windows interface option byte order changed");
            foreach (var invalid in new[] { "192.168.50.0", "192.168.50.255", "192.168.51.20", "8.8.8.8", "127.0.0.1" })
                Require(LanRoute.Select(routes, IPAddress.Parse(invalid)) == null, "Non-peer accepted: " + invalid);
            foreach (var mask in new[] { "0.0.0.0", "255.0.255.0", "255.255.255.255" })
            {
                bool rejected = false;
                try { new LanRoute(ethernet.Address, IPAddress.Parse(mask), 8); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "Unsafe discovery mask accepted: " + mask);
            }
            var own = new HashSet<IPAddress> { ethernet.Address, vpn.Address };
            Require(LanDiscovery.IsRemote(peer, own), "Other LAN host hidden");
            foreach (var self in new[] { ethernet.Address, vpn.Address, IPAddress.Loopback, IPAddress.Parse("127.0.0.2") })
                Require(!LanDiscovery.IsRemote(self, own), "Own room exposed in production discovery");
            using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                LanRoute.BindPeer(udp, IPAddress.Loopback, 0); LanRoute.BindPeer(tcp, IPAddress.Loopback, 0);
                Require(((IPEndPoint)udp.LocalEndPoint).Address.Equals(IPAddress.Loopback) &&
                    ((IPEndPoint)tcp.LocalEndPoint).Address.Equals(IPAddress.Loopback), "Transport source was not bound");
            }
        }
        private static void DiscoveryChecks()
        {
            using (var host = new LanDiscovery(true, true))
            using (var client = new LanDiscovery(false, true))
            {
                bool stop = false; Exception error = null;
                var room = LanPake.Nonce();
                var worker = new Thread(() => { try { while (!Volatile.Read(ref stop)) { host.Advertise(room, 1, "checks"); Thread.Sleep(5); } } catch (Exception ex) { error = ex; } });
                worker.Start();
                try
                {
                    var found = client.Find(() => true, IPAddress.Loopback, true);
                    Require(found.Count == 1 && found[0].Name == "checks", "Explicit loopback discovery failed");
                }
                finally { Volatile.Write(ref stop, true); worker.Join(); }
                Require(error == null, "Loopback advertiser failed: " + error);
            }
            HashSet<IPAddress> own; var routes = LanRoute.Read(out own);
            using (var production = new LanDiscovery(false))
            {
                Require(!production.Local(IPAddress.Loopback), "Production loopback allowed");
                foreach (var address in own) Require(!production.Local(address), "Production self address allowed");
            }
            foreach (var route in routes)
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    route.PinUdp(socket);
                    Require((int)socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31) == route.InterfaceIndex,
                        "OS did not retain selected IPv4 interface");
                    socket.Bind(new IPEndPoint(route.Address, 0));
                    Require(((IPEndPoint)socket.LocalEndPoint).Address.Equals(route.Address), "LAN source bind failed");
                }
            // Exercise the exact production binding path without sending to a peer.
            foreach (var route in routes)
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                using (var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    var expected = LanRoute.Select(routes, route.Address);
                    LanRoute.BindPeer(udp, route.Address, 0); LanRoute.BindPeer(tcp, route.Address, 0);
                    Require(((IPEndPoint)udp.LocalEndPoint).Address.Equals(expected.Address) &&
                        ((IPEndPoint)tcp.LocalEndPoint).Address.Equals(expected.Address), "Pairing/DTLS source selection differs");
                    Require((int)udp.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31) == expected.InterfaceIndex,
                        "DTLS interface selection differs");
                }
            Console.WriteLine("PASS: self exclusion, explicit loopback discovery, adapter routing and OS interface options");
        }
    }
}
