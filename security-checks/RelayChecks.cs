using System;
using System.IO;
using System.Net;
using System.Threading;
using ZompiercerLAN.Relay;

namespace ZompiercerLAN
{
    // The mod's relay path against the real ZompiercerRelay server on 127.0.0.1:
    // TLS pinning, J-PAKE through relay DATA, host approval, DTLS through relay UDP.
    internal static partial class Program
    {
        // Game-side broker against a compromised worker in relay/LAN modes.
        private static void RelayBrokerChecks()
        {
            var virtualPeer = LanRelayText.VirtualPeer;
            // LAN host: no relay room, PIN window at most LanLocalText.PinSeconds (30 s since 1.4.10).
            var lan = new LanBrokerState(LanWorkerMode.Host, "checks", null); Boot(lan);
            ExpectRejected(() => Snapshot(lan, new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456", RelayRoom = "ABCDEFGH", Seconds = 9 }), "LAN host accepted a relay room");
            lan = new LanBrokerState(LanWorkerMode.Host, "checks", null); Boot(lan);
            ExpectRejected(() => Snapshot(lan, new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456", Seconds = 120 }), "LAN host accepted a 120 s PIN");
            // Relay host: room id and 120 s window are fine; a real pending address is not.
            var host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            Snapshot(host, new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456", RelayRoom = "ABCDEFGH", Seconds = 120 });
            ExpectRejected(() => Snapshot(host, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 7, PendingAddress = IPAddress.Parse("192.168.1.5") }),
                "Relay host accepted a real peer address");
            ExpectRejected(() => LanIpc.ReadStatus(LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456", RelayRoom = "abcd-efg" })),
                "Non-canonical relay room accepted");
            host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            Snapshot(host, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 7, PendingAddress = virtualPeer });
            Require(host.PendingApproval, "Relay approval request not shown");
            // Relay joiner: may only become active with the virtual peer on the virtual port.
            var join = new LanBrokerState(LanWorkerMode.RelayJoin, "checks", virtualPeer); Boot(join);
            ExpectRejected(() => Snapshot(join, new LanWorkerStatus { State = LanWorkerState.Active, Session = 9, PeerAddress = IPAddress.Parse("10.0.0.2"), PeerPort = 27777 }),
                "Relay joiner accepted a substituted peer");
            join = new LanBrokerState(LanWorkerMode.RelayJoin, "checks", virtualPeer); Boot(join);
            ExpectRejected(() => Snapshot(join, new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456", RelayRoom = "ABCDEFGH" }), "Relay joiner accepted host-only fields");
            join = new LanBrokerState(LanWorkerMode.RelayJoin, "checks", virtualPeer); Boot(join);
            Snapshot(join, new LanWorkerStatus { State = LanWorkerState.Active, Session = 9, PeerAddress = virtualPeer, PeerPort = LanRelayText.VirtualPort });
            Require(join.TransportReady, "Relay joiner transport not ready");
            // 0.4.2: host's own password (never echoed) and the authenticated room name.
            host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            Snapshot(host, new LanWorkerStatus { State = LanWorkerState.Invitation, CustomSecret = true, RelayRoom = "ABCDEFGH", Seconds = 120 });
            ExpectRejected(() => LanIpc.ReadStatus(LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.Invitation, CustomSecret = true, Code = "123456", RelayRoom = "ABCDEFGH" })),
                "Host password status echoed a code");
            host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            ExpectRejected(() => Snapshot(host, new LanWorkerStatus { State = LanWorkerState.Invitation, RelayRoom = "ABCDEFGH" }), "Invitation without any secret accepted");
            host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            ExpectRejected(() => Snapshot(host, new LanWorkerStatus { State = LanWorkerState.Invitation, Code = "123456" }), "Relay invitation without a room accepted");
            host = new LanBrokerState(LanWorkerMode.RelayHost, "checks", null); Boot(host);
            ExpectRejected(() => Snapshot(host, new LanWorkerStatus { State = LanWorkerState.Pairing, RoomName = "Комната" }), "Relay host accepted a guest-side room name");
            lan = new LanBrokerState(LanWorkerMode.Host, "checks", null); Boot(lan);
            ExpectRejected(() => Snapshot(lan, new LanWorkerStatus { State = LanWorkerState.Invitation, CustomSecret = true, Seconds = 9 }), "LAN host accepted a custom password");
            join = new LanBrokerState(LanWorkerMode.RelayJoin, "checks", virtualPeer); Boot(join);
            ExpectRejected(() => Snapshot(join, new LanWorkerStatus { State = LanWorkerState.Pairing, RoomName = "Комната" }), "Room name shown before the proof");
            join = new LanBrokerState(LanWorkerMode.RelayJoin, "checks", virtualPeer); Boot(join);
            Snapshot(join, new LanWorkerStatus { State = LanWorkerState.WaitingApproval, RoomName = "Комната 1" });
            ExpectRejected(() => Snapshot(join, new LanWorkerStatus { State = LanWorkerState.WaitingApproval, RoomName = "Комната 2" }, now: .1), "Room name replaced after the proof");
            ExpectRejected(() => LanIpc.ReadStatus(LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.WaitingApproval, RoomName = "a/b" })), "Unclean room name accepted");
            ExpectRejected(() => LanIpc.ReadStatus(LanIpc.StatusBytes(new LanWorkerStatus { State = LanWorkerState.WaitingApproval, RoomName = new string('я', 11) })), "Oversized room name accepted");
            // Relay options cannot smuggle private/loopback destinations through IPC.
            foreach (var bad in new[] { "127.0.0.1", "10.1.2.3", "192.168.0.1", "172.16.0.1", "169.254.1.1", "224.0.0.1", "0.0.0.0" })
            {
                var bytes = LanIpc.Encode(w => { w.Write(IPAddress.Parse(bad).GetAddressBytes()); w.Write((ushort)27780); w.Write(new byte[32]); LanIpc.WriteText(w, "", 64); LanIpc.WriteText(w, "", 8); LanIpc.WriteText(w, "", 20); });
                ExpectRejected(() => LanIpc.Decode(bytes, r => LanRelayOptions.Read(r)), "Relay options accepted " + bad);
            }
            // 0.13.0: the public room list over IPC, public names and passwords of listed rooms.
            var listed = new[] { LanRoomInfo.ForRelay("ABCDEFGH", "Поезд Васи"), LanRoomInfo.ForRelay("0123ZZZZ", "Room 2") };
            var browser = new LanBrokerState(LanWorkerMode.RelayBrowse, "", null); Boot(browser);
            browser.Apply(Event(LanIpcKind.Rooms, LanIpc.RoomsBytes(listed)), 0);
            Require(browser.BrowserFinished && browser.Rooms.Length == 2 && browser.Rooms[0].RelayRoom == "ABCDEFGH" && browser.Rooms[1].Name == "Room 2", "Relay room list over IPC");
            var lanBrowser = new LanBrokerState(LanWorkerMode.Browse, "", null); Boot(lanBrowser);
            ExpectRejected(() => lanBrowser.Apply(Event(LanIpcKind.Rooms, LanIpc.RoomsBytes(listed)), 0), "LAN browser accepted relay rooms");
            browser = new LanBrokerState(LanWorkerMode.RelayBrowse, "", null); Boot(browser);
            ExpectRejected(() => browser.Apply(Event(LanIpcKind.Rooms, LanIpc.RoomsBytes(new[] { new LanRoomInfo { Address = IPAddress.Parse("192.168.1.2"), Room = new byte[16], Name = "LAN", Flags = 1 } })), 0), "Relay browser accepted a LAN room");
            Func<LanRoomInfo, bool> refusedRoom = room => { try { LanIpc.ReadRooms(LanIpc.RoomsBytes(new[] { room })); return false; } catch (InvalidDataException) { return true; } };
            var padded = LanRoomInfo.ForRelay("ABCDEFGH", "A"); padded.Room[12] = 1;
            var badId = LanRoomInfo.ForRelay("ABCDEFGH", "A"); badId.Room[0] = (byte)'I';
            var badAddress = LanRoomInfo.ForRelay("ABCDEFGH", "A"); badAddress.Address = IPAddress.Parse("10.0.0.1");
            Require(refusedRoom(padded) && refusedRoom(badId) && refusedRoom(badAddress) && refusedRoom(LanRoomInfo.ForRelay("ABCDEFGH", "two  spaces")) &&
                refusedRoom(LanRoomInfo.ForRelay("ABCDEFGH", "a<b>")), "Malformed relay room accepted over IPC");
            var many = new LanRoomInfo[LanIpc.MaxRooms];
            for (int i = 0; i < many.Length; i++) many[i] = LanRoomInfo.ForRelay("ABCDEFGH", "Room " + i);
            Require(LanIpc.ReadRooms(LanIpc.RoomsBytes(many)).Length == LanIpc.MaxRooms, "24 rooms fit over IPC");
            ExpectRejected(() => LanIpc.RoomsBytes(new LanRoomInfo[LanIpc.MaxRooms + 1]), "25 rooms encoded");
            Require(LanRelayText.PublicName("  Мой   поезд  ") == "Мой поезд" && LanRelayText.PublicName("<b>") == "b" && LanRelayText.PublicName("") == "Комната", "Public room names");
            foreach (var weak in new[] { "", "abc12", "12345678", "abcdefgh", "hgfedcba", "aaaabbbb", "ab ab ab" })
                Require(LanRelayText.PublicSecretProblem(weak) != null && !LanRelayText.StrongPublicSecret(weak), "Weak public password accepted: " + weak);
            foreach (var strong in new[] { "кот-в-сапогах-7", "Tr4in Night", "zompier42!" })
                Require(LanRelayText.PublicSecretProblem(strong) == null && LanRelayText.StrongPublicSecret(strong), "Strong public password refused: " + strong);
            var publicOptions = new LanRelayOptions { Server = IPAddress.Parse("203.0.113.10"), Fingerprint = new byte[32], PublicName = "Поезд Васи" };
            LanRelayOptions decoded = null;
            LanIpc.Decode(LanIpc.Encode(w => publicOptions.Write(w)), r => decoded = LanRelayOptions.Read(r));
            Require(decoded.PublicName == "Поезд Васи", "Public name over IPC");
            foreach (var bad in new[] { new LanRelayOptions { Server = IPAddress.Parse("203.0.113.10"), Fingerprint = new byte[32], PublicName = "a  b" },
                new LanRelayOptions { Server = IPAddress.Parse("203.0.113.10"), Fingerprint = new byte[32], PublicName = "<x>" } })
                ExpectRejected(() => LanIpc.Encode(w => bad.Write(w)), "Unclean public name encoded");
            var both = LanIpc.Encode(w => { w.Write(IPAddress.Parse("203.0.113.10").GetAddressBytes()); w.Write((ushort)27780); w.Write(new byte[32]); LanIpc.WriteText(w, "", 64); LanIpc.WriteText(w, "ABCDEFGH", 8); LanIpc.WriteText(w, "Room", 20); });
            ExpectRejected(() => LanIpc.Decode(both, r => LanRelayOptions.Read(r)), "A joiner's options carried a public name");
            Console.WriteLine("PASS: broker relay roles / virtual peer only / relay fields never in LAN modes / relay destination policy over IPC / public room list over IPC, names, passwords");
        }

        private static void RelayChecks()
        {
            RelayBrokerChecks();
            LanRelayText.AllowLoopbackForChecks = true;
            string dir = Path.Combine(Path.GetTempPath(), "zlan-relay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var config = new ZompiercerRelay.RelayConfig
            {
                ListenAddress = "127.0.0.1", TcpPort = 0, UdpPort = 0, StatsIntervalSeconds = 0, LogLevel = "error",
                TlsCertificate = Path.Combine(dir, "cert.pem"), TlsPrivateKey = Path.Combine(dir, "key.pem"),
                JoinsPerMinutePerAddress = 10000, ConnectionsPerMinutePerAddress = 10000, MaxConnectionsPerAddress = 1000, MaxRoomsPerAddress = 1000, ConnectionsPerSecond = 10000, MaxConnectionsPerSubnet = 10000, MaxRoomsPerSubnet = 10000,
                // As on the public server (relay 1.3.0): only the mod's own pairing frames and DTLS datagrams pass.
                FilterTraffic = true,
            };
            ZompiercerRelay.Certificates.Generate(config.TlsCertificate, config.TlsPrivateKey, false);
            ZompiercerRelay.Log.Configure(config);
            var server = new ZompiercerRelay.RelayServer(config);
            server.Start();
            try
            {
                byte[] pin = LanRelayText.ParseFingerprint(server.Fingerprint);
                Require(pin != null, "Relay fingerprint format");
                Func<string, byte[], LanRelayOptions> options = (room, fingerprint) => new LanRelayOptions
                    { Server = IPAddress.Loopback, Port = server.TcpPort, Fingerprint = fingerprint, AccessKey = "", Room = room };

                // A guest that leaves frees its seat only when the relay sees its connection close;
                // the next guest joining at once must not be turned away as "room busy" (a friend
                // retrying right after a wrong password).
                int busy = 0;
                for (int round = 0; round < 10; round++)
                    using (var hostLink = new LanRelayLink(options("", pin), true))
                    {
                        Require(Wait(() => hostLink.Ready && hostLink.Room.Length == 8, 10000), "Relay room for the quick rejoin not ready: " + hostLink.Failure);
                        using (var first = new LanRelayLink(options(hostLink.Room, pin), false))
                            Require(Wait(() => first.Ready, 10000), "First guest did not join: " + first.Failure);
                        using (var next = new LanRelayLink(options(hostLink.Room, pin), false))
                        {
                            Wait(() => next.Ready || next.Failure != LanFault.None, 10000);
                            if (!next.Ready) busy++;
                            Require(next.Ready || next.Failure == LanFault.RelayRoomBusy, "Quick rejoin failed otherwise: " + next.Failure);
                        }
                    }
                Console.WriteLine("relay quick rejoin: " + busy + " of 10 refused as busy");
                Require(busy == 0, "A guest joining right after another left was refused as busy (" + busy + " of 10)");

                // Full path: invitation, PIN proof, approval, DTLS over the relay, protected packets.
                using (var hostLink = new LanRelayLink(options("", pin), true))
                using (var host = LanRelayPairing.Host(hostLink, "Поезд 42"))
                {
                    Require(Wait(() => host.Code.Length == 6 && host.Room.Length == 8, 10000), "Relay room/PIN not ready: " + hostLink.Failure);
                    Require(host.InvitationOpen && !host.CustomSecret, "Random PIN invitation state");
                    Require(LanRelayText.NormalizeRoom(host.Room) == host.Room, "Relay room id is not canonical");
                    using (var guestLink = new LanRelayLink(options(host.Room, pin), false))
                    using (var guest = LanRelayPairing.Join(host.Code, guestLink))
                    {
                        Require(Wait(() => host.PendingApproval, 10000), "Relay J-PAKE did not reach approval: " + host.Failure + "/" + guest.Failure);
                        Require(Wait(() => guest.ConfirmedRoomName == "Поезд 42", 2000), "Guest did not get the authenticated room name: '" + guest.ConfirmedRoomName + "'");
                        byte[] key; IPAddress peer;
                        Require(!host.TryTakeCredentials(out key, out peer), "Relay host credentials before approval");
                        Require(!guest.TryTakeCredentials(out key, out peer), "Relay guest credentials before approval");
                        Require(host.PendingPeer == LanRelayText.VirtualPeer.ToString(), "Relay exposed a real peer address");
                        Require(host.Code == "" && host.Room == "", "Invitation was not consumed by the PIN proof");
                        host.Approve();
                        Require(Wait(() => host.TryTakeCredentials(out key, out peer), 2000), "Approved relay host credentials missing");
                        Require(LanRelayText.VirtualPeer.Equals(peer), "Relay host peer is not the virtual endpoint");
                        using (var hostTransport = LanSecureTransport.OverRelay(true, hostLink.CreateDatagramLink(), "checks", key))
                        {
                            host.TransportReady();
                            byte[] guestKey = null; IPAddress guestPeer = null;
                            Require(Wait(() => guest.TryTakeCredentials(out guestKey, out guestPeer), 5000), "Relay guest approval missing");
                            Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(key, guestKey), "Relay pairing keys differ");
                            using (var guestTransport = LanSecureTransport.OverRelay(false, guestLink.CreateDatagramLink(), "checks", guestKey))
                            {
                                Require(Wait(() => hostTransport.Session != 0 && guestTransport.Session == hostTransport.Session, 15000), "DTLS over relay failed");
                                Packet packet; IPEndPoint from;
                                Require(Wait(() =>
                                {
                                    if (!hostTransport.TryReceive(out from, out packet) || packet.Kind != PacketKind.Hello) return false;
                                    hostTransport.Send(new Packet { Kind = PacketKind.Welcome, GameVersion = "checks", Session = hostTransport.Session }); return true;
                                }, 5000), "Protected Hello over relay missing");
                                Require(Wait(() => guestTransport.TryReceive(out from, out packet) && packet.Kind == PacketKind.Welcome, 5000), "Protected Welcome over relay missing");
                                var state = State(321); state.Session = guestTransport.Session; guestTransport.Send(state);
                                Require(Wait(() => hostTransport.TryReceive(out from, out packet) && packet.Kind == PacketKind.State && packet.Sequence == 321 &&
                                    from.Address.Equals(LanRelayText.VirtualPeer) && from.Port == LanRelayText.VirtualPort, 5000), "Protected state over relay missing");
                                Require(guestTransport.Peer.Port == LanRelayText.VirtualPort, "Guest peer is not the virtual endpoint");
                                ulong oldSession = hostTransport.Session;
                                hostTransport.Restart(); guestTransport.Restart();
                                Require(Wait(() => hostTransport.Session != 0 && hostTransport.Session != oldSession && hostTransport.Session == guestTransport.Session, 15000),
                                    "DTLS reconnect over relay failed");
                            }
                            LanPake.Clear(guestKey);
                        }
                        LanPake.Clear(key);
                        Require(Wait(() => host.Finished && guest.Finished && host.Succeeded && guest.Succeeded, 2000), "Relay pairing did not complete");
                        Require(hostLink.Failure == LanFault.None && guestLink.Failure == LanFault.None, "Relay link failed after pairing");
                    }
                }

                // Wrong PIN: proof fails for the guest, the invitation survives for the right friend.
                using (var hostLink = new LanRelayLink(options("", pin), true))
                using (var host = LanRelayPairing.Host(hostLink))
                {
                    Require(Wait(() => host.Code.Length == 6 && host.Room.Length == 8, 10000), "Second relay room not ready");
                    string room = host.Room, code = host.Code;
                    using (var badLink = new LanRelayLink(options(room, pin), false))
                    using (var bad = LanRelayPairing.Join(code == "123456" ? "654321" : "123456", badLink))
                    {
                        Require(Wait(() => bad.Finished, 15000), "Wrong-PIN guest did not finish");
                        Require(!bad.Succeeded && bad.Failure == LanFault.PairingProofFailed, "Wrong PIN lost its proof-failure diagnosis: " + bad.Failure);
                        Require(!host.PendingApproval, "Wrong PIN reached host approval");
                    }
                    using (var guestLink = new LanRelayLink(options(room, pin), false))
                    using (var guest = LanRelayPairing.Join(code, guestLink))
                    {
                        Require(Wait(() => host.PendingApproval, 10000), "Right PIN after a wrong one did not reach approval: " + host.Failure + "/" + guest.Failure);
                        host.Reject();
                        Require(Wait(() => host.Finished && guest.Finished, 5000), "Rejected relay pairing did not stop");
                        byte[] key; IPAddress peer;
                        Require(!host.Succeeded && !guest.Succeeded && !host.TryTakeCredentials(out key, out peer) && !guest.TryTakeCredentials(out key, out peer),
                            "Rejected relay pairing exposed credentials");
                    }
                }

                // Host's own password: never echoed as a code; wrong password fails the
                // proof and reveals no room name; the right one passes.
                using (var hostLink = new LanRelayLink(options("", pin), true))
                using (var host = LanRelayPairing.Host(hostLink, "Мой поезд", "длинный пароль 42"))
                {
                    Require(Wait(() => host.InvitationOpen && host.Room.Length == 8, 10000), "Password room not ready: " + hostLink.Failure);
                    Require(host.CustomSecret && host.Code == "", "Host password was echoed as a code");
                    string room = host.Room;
                    using (var badLink = new LanRelayLink(options(room, pin), false))
                    using (var bad = LanRelayPairing.Join("длинный пароль 43", badLink))
                    {
                        Require(Wait(() => bad.Finished, 15000), "Wrong-password guest did not finish");
                        Require(!bad.Succeeded && bad.Failure == LanFault.PairingProofFailed && bad.ConfirmedRoomName == "", "Wrong password: " + bad.Failure + " / '" + bad.ConfirmedRoomName + "'");
                        Require(!host.PendingApproval, "Wrong password reached host approval");
                    }
                    using (var guestLink = new LanRelayLink(options(room, pin), false))
                    using (var guest = LanRelayPairing.Join(LanRelayText.NormalizeSecret("  длинный пароль 42 "), guestLink))
                    {
                        Require(Wait(() => host.PendingApproval, 10000), "Right password did not reach approval: " + host.Failure + "/" + guest.Failure);
                        Require(Wait(() => guest.ConfirmedRoomName == "Мой поезд", 2000), "Password room name missing");
                        Require(!host.InvitationOpen, "Password invitation still open after the proof");
                        host.Reject();
                        Require(Wait(() => host.Finished && guest.Finished, 5000), "Rejected password pairing did not stop");
                    }
                }
                // A guest that joins and stays silent (holding the only seat) is removed after
                // 20 s, and the room then takes the real friend.
                using (var hostLink = new LanRelayLink(options("", pin), true))
                using (var host = LanRelayPairing.Host(hostLink, "Squat"))
                {
                    Require(Wait(() => host.InvitationOpen && host.Room.Length == 8, 10000), "Room for the squatter check not ready");
                    string room = host.Room, code = host.Code;
                    var started = DateTime.UtcNow;
                    using (var squatter = new LanRelayLink(options(room, pin), false))
                    {
                        Require(Wait(() => squatter.Ready, 10000), "Squatter did not join");
                        Require(Wait(() => squatter.Failure != LanFault.None, 30000), "Silent guest was never removed");
                        Require((DateTime.UtcNow - started).TotalSeconds < 29, "Silent guest removed too late");
                    }
                    using (var guestLink = new LanRelayLink(options(room, pin), false))
                    using (var guest = LanRelayPairing.Join(code, guestLink))
                    {
                        Require(Wait(() => host.PendingApproval, 15000), "Friend after a squatter did not reach approval: " + host.Failure + "/" + guest.Failure);
                        host.Reject();
                        Require(Wait(() => host.Finished && guest.Finished, 5000), "Pairing after a squatter did not stop");
                    }
                }
                bool refusedSecret = false;
                using (var link = new LanRelayLink(options("", pin), true))
                    try { LanRelayPairing.Host(link, "x", "12345").Dispose(); } catch (ArgumentException) { refusedSecret = true; }
                Require(refusedSecret, "Five-character host password accepted");

                // 0.13.0: a listed room needs the host's own strong password; it is listed under its
                // name for this mod version; wrong passwords keep it open; the right one reaches approval.
                foreach (var weak in new[] { "", "12345678" })
                {
                    bool refusedWeak = false;
                    using (var link = new LanRelayLink(options("", pin), true))
                        try { LanRelayPairing.Host(link, "x", weak, true).Dispose(); } catch (ArgumentException) { refusedWeak = true; }
                    Require(refusedWeak, "Listed room accepted the password '" + weak + "'");
                }
                var listedOptions = options("", pin); listedOptions.PublicName = "Поезд Васи";
                using (var hostLink = new LanRelayLink(listedOptions, true))
                using (var host = LanRelayPairing.Host(hostLink, "Поезд Васи", "кот-в-сапогах-7", true))
                {
                    Require(Wait(() => host.InvitationOpen && host.Room.Length == 8, 10000), "Listed room not ready: " + hostLink.Failure);
                    string room = host.Room;
                    Func<LanRoomInfo[]> list = () =>
                    {
                        using (var reader = new LanRelayBrowser(options("", pin)))
                        {
                            Require(Wait(() => reader.Finished, 10000), "Room list did not finish");
                            Require(reader.Failure == LanFault.None, "Room list failed: " + reader.Failure);
                            return reader.Rooms;
                        }
                    };
                    var rooms = list();
                    Require(rooms.Length == 1 && rooms[0].RelayRoom == room && rooms[0].Name == "Поезд Васи", "Listed room not in the list");
                    for (int i = 0; i < 4; i++)
                        using (var badLink = new LanRelayLink(options(room, pin), false))
                        using (var bad = LanRelayPairing.Join("кот-в-сапогах-8", badLink))
                        {
                            Require(Wait(() => bad.Finished, 15000) && !bad.Succeeded, "Wrong password to a listed room succeeded");
                            // The relay allows three joins per window: the host reopens it after each try.
                            Thread.Sleep(3500);
                        }
                    Require(host.InvitationOpen && !host.Finished, "Listed room closed after four wrong passwords: " + host.Failure);
                    Require(list().Length == 1, "Listed room left the list after wrong passwords");
                    using (var guestLink = new LanRelayLink(options(room, pin), false))
                    using (var guest = LanRelayPairing.Join("кот-в-сапогах-7", guestLink))
                    {
                        Require(Wait(() => host.PendingApproval, 10000), "Right password to a listed room did not reach approval: " + host.Failure + "/" + guest.Failure);
                        Require(list().Length == 0, "Room with a guest still listed");
                        host.Reject();
                        Require(Wait(() => host.Finished && guest.Finished, 5000), "Rejected listed pairing did not stop");
                    }
                }
                using (var hostLink = new LanRelayLink(options("", pin), true))
                using (var host = LanRelayPairing.Host(hostLink, "Private", "кот-в-сапогах-7"))
                {
                    Require(Wait(() => host.InvitationOpen, 10000), "Private room not ready");
                    using (var reader = new LanRelayBrowser(options("", pin)))
                        Require(Wait(() => reader.Finished, 10000) && reader.Failure == LanFault.None && reader.Rooms.Length == 0, "Private room listed");
                }

                // Pinning: a different fingerprint never reaches the relay protocol.
                var wrongPin = (byte[])pin.Clone(); wrongPin[0] ^= 1;
                using (var link = new LanRelayLink(options("", wrongPin), true))
                    Require(Wait(() => link.Failure == LanFault.RelayCertificate, 10000) && !link.Ready, "Wrong fingerprint accepted: " + link.Failure);

                // Unknown room.
                using (var link = new LanRelayLink(options("ZZZZZZZZ", pin), false))
                    Require(Wait(() => link.Failure == LanFault.RelayRoomNotFound, 10000), "Unknown relay room not diagnosed: " + link.Failure);

                // Options are validated before any connection.
                bool refused = false;
                try { new LanRelayLink(new LanRelayOptions { Server = IPAddress.Parse("192.168.1.10"), Port = 1, Fingerprint = pin, Room = "" }, true).Dispose(); }
                catch (ArgumentException) { refused = true; }
                Require(refused, "Private relay address accepted");
                LanRelayText.AllowLoopbackForChecks = false;
                Require(!LanRelayText.ValidServer(IPAddress.Loopback) && !LanRelayText.ValidServer(IPAddress.Parse("10.0.0.1")) &&
                    LanRelayText.ValidServer(IPAddress.Parse("203.0.113.10")), "Relay address policy");
                Require(ZompiercerRelay.Log.ErrorCount == 0, "Relay server logged internal errors");
                Console.WriteLine("relay filter: udp forwarded=" + server.Stats.UdpForwarded + " filtered=" + server.Stats.UdpFiltered +
                    ", tcp frames=" + server.Stats.TcpFrames + " filtered=" + server.Stats.TcpFiltered);
                Require(server.Stats.UdpForwarded > 0 && server.Stats.TcpFrames > 0, "Relay carried pairing and DTLS traffic");
                Require(server.Stats.UdpFiltered == 0 && server.Stats.TcpFiltered == 0, "Relay traffic filter passes all of the mod's own traffic");
            }
            finally
            {
                LanRelayText.AllowLoopbackForChecks = false;
                server.StopAsync().GetAwaiter().GetResult();
                try { Directory.Delete(dir, true); } catch { }
            }
            RelayClosedChecks();
            Console.WriteLine("PASS: relay TLS pinning / J-PAKE via relay DATA / approval and rejection / wrong PIN / host password / authenticated room name / DTLS via validated relay UDP / reconnect / virtual peer only");
        }

        // A relay with an access key and a 1 s grace: wrong key is refused; when the
        // relay closes the room during a session the guest reports "room closed"
        // at once and does not try to reconnect (0.4.1 retried and then reported
        // "room not found").
        private static void RelayClosedChecks()
        {
            LanRelayText.AllowLoopbackForChecks = true;
            string dir = Path.Combine(Path.GetTempPath(), "zlan-relay-closed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            const string key = "checks-access-key-0123456789";
            var config = new ZompiercerRelay.RelayConfig
            {
                ListenAddress = "127.0.0.1", TcpPort = 0, UdpPort = 0, StatsIntervalSeconds = 0, LogLevel = "error", RejoinGraceSeconds = 1, AccessKey = key,
                TlsCertificate = Path.Combine(dir, "cert.pem"), TlsPrivateKey = Path.Combine(dir, "key.pem"),
                JoinsPerMinutePerAddress = 10000, ConnectionsPerMinutePerAddress = 10000, MaxConnectionsPerAddress = 1000, MaxRoomsPerAddress = 1000, ConnectionsPerSecond = 10000, MaxConnectionsPerSubnet = 10000, MaxRoomsPerSubnet = 10000,
            };
            ZompiercerRelay.Certificates.Generate(config.TlsCertificate, config.TlsPrivateKey, false);
            var server = new ZompiercerRelay.RelayServer(config);
            server.Start();
            try
            {
                byte[] pin = LanRelayText.ParseFingerprint(server.Fingerprint);
                Func<string, string, LanRelayOptions> options = (room, accessKey) => new LanRelayOptions
                    { Server = IPAddress.Loopback, Port = server.TcpPort, Fingerprint = pin, AccessKey = accessKey, Room = room };
                using (var wrong = new LanRelayLink(options("", "wrong-access-key-000000"), true))
                    Require(Wait(() => wrong.Failure == LanFault.RelayAccessDenied, 10000) && !wrong.Ready, "Wrong relay access key not diagnosed: " + wrong.Failure);
                // The server switches off this very build (0.9.1+: the application string names the version).
                config.BlockedApplications.Add(LanRelayLink.Application);
                using (var blocked = new LanRelayLink(options("", key), true))
                    Require(Wait(() => blocked.Failure == LanFault.RelayOutdated, 10000) && !blocked.Ready, "Blocked mod version not diagnosed: " + blocked.Failure);
                config.BlockedApplications.Clear();
                Require(LanFaultException.MessageFor(LanFault.RelayOutdated).Contains("новую версию"), "Outdated mod message");

                using (var hostLink = new LanRelayLink(options("", key), true))
                {
                    Require(Wait(() => hostLink.Ready && hostLink.Room.Length == 8, 10000), "Keyed relay room not created: " + hostLink.Failure);
                    using (var guestLink = new LanRelayLink(options(hostLink.Room, key), false))
                    {
                        Require(Wait(() => guestLink.Ready, 10000), "Guest did not join the keyed relay: " + guestLink.Failure);
                        guestLink.BeginSession();
                        long rejoins = server.Stats.Rejoins, rejected = server.Stats.JoinsRejected;
                        hostLink.Dispose(); // host gone; after the 1 s grace the relay closes the room
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        Require(Wait(() => guestLink.Failure != LanFault.None, 8000), "Closed room not reported");
                        Require(guestLink.Failure == LanFault.RelayClosed, "Closed room reported as " + guestLink.Failure);
                        Thread.Sleep(2500);
                        Require(server.Stats.Rejoins == rejoins && server.Stats.JoinsRejected == rejected, "Guest tried to rejoin a room the relay closed");
                    }
                }
                Require(ZompiercerRelay.Log.ErrorCount == 0, "Relay server logged internal errors");
            }
            finally
            {
                LanRelayText.AllowLoopbackForChecks = false;
                server.StopAsync().GetAwaiter().GetResult();
                try { Directory.Delete(dir, true); } catch { }
            }
            Console.WriteLine("PASS: relay access key / blocked mod version asks to update / room closed during a session ends it without a rejoin attempt");
        }
    }
}
