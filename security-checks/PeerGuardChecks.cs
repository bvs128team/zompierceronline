using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static void PeerGuardChecks()
        {
            var guard=new LanPeerGuard();
            for(int i=0;i<7;i++)guard.Invalid(0);
            Require(guard.Failure==null,"Invalid guard revoked early");guard.Invalid(9.99);
            Require(guard.Failure!=null,"Invalid guard failed to revoke");
            string reason=guard.Failure;guard.Invalid(100);Require(guard.Failure==reason,"Revocation changed/reset");
            guard=new LanPeerGuard();
            for(int window=0;window<4;window++)for(int i=0;i<7;i++)guard.Invalid(window*10);
            Require(guard.Failure==null,"Lifetime invalid guard revoked early");
            for(int i=0;i<4;i++)guard.Invalid(40);
            Require(guard.Failure!=null,"Slow authenticated violations bypassed lifetime cap");
            guard=new LanPeerGuard();
            for(int i=0;i<63;i++)guard.ExcessRate(0);
            Require(guard.Failure==null,"Rate guard revoked early");guard.ExcessRate(1);
            Require(guard.Failure!=null,"Rate guard failed to revoke");
            guard=new LanPeerGuard();
            for(int window=0;window<8;window++)for(int i=0;i<63;i++)guard.ExcessRate(window*10);
            Require(guard.Failure==null,"Lifetime rate guard revoked early");
            for(int i=0;i<8;i++)guard.ExcessRate(80);
            Require(guard.Failure!=null,"Slow rate violations bypassed lifetime cap");
            guard=new LanPeerGuard();guard.Invalid(double.NaN);Require(guard.Failure!=null,"Invalid guard time accepted");
            var delay=new LanReconnectDelay();double now=0;
            Require(delay.Ready(0) && !delay.Ready(double.NaN),"Invalid reconnect time");
            for(int i=0;i<20;i++) {
                double expected=Math.Min(30,.5*Math.Pow(2,i)),actual=delay.Failed(now);
                Require(actual==expected && !delay.Ready(now+actual-.001) && delay.Ready(now+actual),"Reconnect delay/cap failed");now+=actual;
            }
            delay.Healthy();Require(delay.Ready(now) && delay.Failed(now)==.5,"Healthy session failed to reset delay");
            Console.WriteLine("PASS: authenticated violation windows/lifetime caps / reconnect exponential delay and ceiling");
        }
        private static int FreeUdpPort()
        {
            using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))return ((IPEndPoint)reservation.Client.LocalEndPoint).Port;
        }
        private static byte[] TestKey(int seed)
        { var secret=new byte[32];new Random(seed).NextBytes(secret);return secret; }
        private static void Establish(LanSecureTransport host,LanSecureTransport client)
        {
            Packet packet;IPEndPoint peer;
            Require(Wait(()=>host.Session!=0 && host.Session==client.Session,15000),"Attack-check DTLS handshake failed");
            Require(Wait(()=> {
                if(!host.TryReceive(out peer,out packet) || packet.Kind!=PacketKind.Hello)return false;
                host.Send(new Packet {Kind=PacketKind.Welcome,GameVersion="checks",Session=host.Session});return true;
            },4000),"Attack-check Hello missing");
            Require(Wait(()=>client.TryReceive(out peer,out packet) && packet.Kind==PacketKind.Welcome,3000),"Attack-check Welcome missing");
        }
        private static void AssertBlocked(LanSecureTransport receiver)
        {
            Require(Wait(()=>receiver.SecurityFailure!=null,3000),"Authenticated attacker was not blocked: "+receiver.GuardCountsForChecks+"; queue="+receiver.QueuedPackets);
            Require(receiver.Session==0 && receiver.QueuedPackets==0,"Revocation retained session/queued payloads");
            long attempts=receiver.HandshakeAttempts;receiver.Restart();receiver.RetryAfterBusy();Thread.Sleep(100);
            Require(receiver.Session==0 && receiver.HandshakeAttempts==attempts,"Restart reactivated revoked admission");
            Packet packet;IPEndPoint peer;Require(!receiver.TryReceive(out peer,out packet),"Revoked queue delivered data");
        }
        private static void PeerAttackChecks()
        {
            // Authenticated invalid plaintext, forbidden direction, stale session,
            // and data before application Welcome. Each runs against BOTH roles.
            foreach(bool attackingHost in new[]{false,true})for(int mode=0;mode<5;mode++) {
                Console.WriteLine("CHECK: authenticated attacker={0}; mode={1}",attackingHost?"host":"client",mode);
                int port=FreeUdpPort();var secret=TestKey(791+mode);
                using(var host=LanSecureTransport.HostWithKey(port,IPAddress.Loopback,"checks",secret))
                using(var client=LanSecureTransport.JoinWithKey(port,IPAddress.Loopback,"checks",secret)) {
                    if(mode==3) Require(Wait(()=>host.Session!=0 && host.Session==client.Session,15000),"Pre-Welcome DTLS handshake failed");
                    else Establish(host,client);
                    var sender=attackingHost?host:client;var receiver=attackingHost?client:host;
                    byte[] invalid;
                    if(mode==0)invalid=new byte[]{1,2,3};
                    else if(mode==1) {
                        var packet=attackingHost?Kit(PacketKind.CombatProposal):Kit(PacketKind.CombatState);
                        packet.Session=sender.Session;invalid=LanProtocol.Encode(packet);
                    } else {
                        var packet=State(901);packet.Session=mode==2?sender.Session^1UL:sender.Session;invalid=LanProtocol.Encode(packet);
                    }
                    if(mode==4) {
                        // Flood valid State at a single kind's budget, below the
                        // transport's overall application ceiling. Refill depends
                        // on wall time, so paced delivery still exceeds 30/s.
                        for(int i=0;i<500 && receiver.SecurityFailure==null;i++) { sender.SendUncheckedForChecks(invalid);Thread.Sleep(6); }
                    } else {
                        for(int i=0;i<8;i++)sender.SendUncheckedForChecks(invalid,mode==3);
                    }
                    AssertBlocked(receiver);
                }
            }
            // Four strikes survive reconnect; another four revoke the same grant.
            {
                int port=FreeUdpPort();var secret=TestKey(920);
                using(var host=LanSecureTransport.HostWithKey(port,IPAddress.Loopback,"checks",secret))
                using(var client=LanSecureTransport.JoinWithKey(port,IPAddress.Loopback,"checks",secret)) {
                    Establish(host,client);for(int i=0;i<4;i++)client.SendUncheckedForChecks(new byte[]{1});
                    Thread.Sleep(250);Require(host.SecurityFailure==null,"Four strikes revoked early");ulong previous=host.Session;
                    host.Restart();client.Restart();
                    Require(Wait(()=>host.Session!=0 && host.Session!=previous && host.Session==client.Session,15000),"Strike reconnect failed");
                    Establish(host,client);for(int i=0;i<4;i++)client.SendUncheckedForChecks(new byte[]{1});AssertBlocked(host);
                }
            }
            Console.WriteLine("PASS: malicious authenticated host AND client / malformed, role, stale-session, pre-Welcome, rate / permanent revocation / strikes survive reconnect");
        }
        private static void ReconnectChecks()
        {
            // A non-responsive loopback UDP sink exercises cancellation inside
            // the actual client handshake. Restart cannot skip the wait.
            using(var sink=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            using(var client=LanSecureTransport.JoinWithKey(((IPEndPoint)sink.Client.LocalEndPoint).Port,IPAddress.Loopback,"checks",TestKey(120))) {
                Require(Wait(()=>client.HandshakeAttempts==1,1500),"First handshake missing");
                var watch=Stopwatch.StartNew();client.Restart();
                Require(Wait(()=>client.HandshakeAttempts==2,3000) && watch.ElapsedMilliseconds>=450,"First restart skipped backoff");
                watch.Restart();client.Restart();
                for(int i=0;i<10;i++) { Thread.Sleep(50);client.Restart();Require(client.HandshakeAttempts==2,"Repeated Restart bypassed backoff"); }
                Require(Wait(()=>client.HandshakeAttempts==3,2500) && watch.ElapsedMilliseconds>=950,"Second restart failed exponential backoff");
            }
            Console.WriteLine("PASS: real DTLS handshake cancellation / repeated Restart does not bypass backoff");
        }
        private static void AdmissionChecks()
        {
            // Real production TCP/J-PAKE state machine, loopback endpoints and
            // ephemeral ports only: no discovery broadcast or external service.
            using(var host=LanPairing.HostLoopbackForChecks())
            using(var client=LanPairing.JoinLoopbackForChecks(host.Code,host.ListenPortForChecks)) {
                Require(Wait(()=>host.PendingApproval,9000),"Loopback approval missing: "+host.Status+"; "+client.Status);
                byte[] key;IPAddress peer;
                Require(host.PendingPeer=="127.0.0.1","Pending peer is not the authenticated connection source");
                for(int i=0;i<20;i++) { Require(!host.TryTakeCredentials(out key,out peer) && !client.TryTakeCredentials(out key,out peer),"Credentials before host decision");Thread.Sleep(25); }
                host.Approve();byte[] hostKey=null,clientKey=null;
                Require(Wait(()=>host.TryTakeCredentials(out hostKey,out peer),1500),"Approved host credentials missing");
                Require(!host.TryTakeCredentials(out key,out peer) && !client.TryTakeCredentials(out key,out peer),"Credentials repeated or client before listener readiness");
                host.TransportReady();Require(Wait(()=>client.TryTakeCredentials(out clientKey,out peer),1500),"Approved client credentials missing");
                Require(Org.BouncyCastle.Utilities.Arrays.AreEqual(hostKey,clientKey),"Approved peer keys differ");LanPake.Clear(hostKey);LanPake.Clear(clientKey);
                Require(Wait(()=>host.Finished && client.Finished && host.Succeeded && client.Succeeded,1500),"Approved pairing incomplete");
                Require(host.Code=="" && !client.TryTakeCredentials(out key,out peer),"Consumed grant/code reused");
            }
            foreach(bool approveThenRevoke in new[]{false,true})
            using(var host=LanPairing.HostLoopbackForChecks())
            using(var client=LanPairing.JoinLoopbackForChecks(host.Code,host.ListenPortForChecks)) {
                Require(Wait(()=>host.PendingApproval,9000),"Rejection PAKE incomplete");
                if(approveThenRevoke) { host.Approve();Thread.Sleep(100); }
                host.Reject();byte[] key;IPAddress peer;
                Require(!host.TryTakeCredentials(out key,out peer) && !client.TryTakeCredentials(out key,out peer),"Rejected credentials remained available");
                Require(Wait(()=>host.Finished && client.Finished,1500) && !host.Succeeded && !client.Succeeded,"Rejected pairing did not stop");
            }
            using(var expired=LanPairing.HostLoopbackForChecks()) {
                Require(Wait(()=>expired.Expired && expired.Finished,(LanLocalText.PinSeconds+1)*1000),"Code did not expire");byte[] key;IPAddress peer;
                Require(expired.Code=="" && !expired.TryTakeCredentials(out key,out peer),"Expired invitation still grants access");
            }
            Console.WriteLine("PASS: real loopback TCP/J-PAKE / explicit host approval / listener-readiness gate / rejection and revocation / code expiry / single-use credentials");
        }
    }
}
