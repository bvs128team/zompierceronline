using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static void NetworkFailureChecks()
        {
            for(int value=0;value<256;value++) {
                var broker=new LanBrokerState(LanWorkerMode.Browse,"",null);Boot(broker);
                bool known=value>=1 && value<=20, accepted=false; // 11..18: relay diagnostics (0.4.0); 19: outdated mod (0.9.1); 20: no public list (0.13.0)
                try { broker.Apply(Event(LanIpcKind.Fault,new byte[]{(byte)value}),0); }
                catch(LanFaultException ex) { accepted=true;Require((int)ex.Fault==value && ex.Message.Length>0,"Fault code changed across IPC"); }
                catch(InvalidDataException) { Require(!known,"Known fault rejected"); }
                Require(accepted==known,"Unknown diagnostic accepted");
                Require(!broker.BrowserFinished,"Network failure became an empty completed browse");
            }
            foreach(var payload in new[]{new byte[0],new byte[]{3,0},new byte[64]})
                ExpectRejected(()=>LanFaultException.Read(payload),"Noncanonical fault payload accepted");
            Require((byte)LanFault.Security==1 && (byte)LanFault.Runtime==2,"Legacy fault values changed");
            Require((byte)LanFault.RelayUnreachable==11 && (byte)LanFault.RelayCertificate==12 && (byte)LanFault.RelayClosed==18,"Relay fault values changed");
            Require(LanNetworkFailure.From(new SocketException((int)SocketError.AccessDenied))==LanFault.SocketAccessDenied,"Access denied was hidden");
            Require(LanNetworkFailure.From(new SocketException((int)SocketError.ConnectionRefused))==LanFault.ConnectionRefused,"Refused connection was hidden");
            Require(LanNetworkFailure.From(new TimeoutException())==LanFault.NoHostResponse,"Timeout was hidden");
            Require(LanNetworkFailure.From(new IOException("untrusted network text"))==LanFault.Runtime,"Exception text escaped diagnostic vocabulary");
            Require(LanNetworkFailure.DiscoveryResult(false,0,0,false)==LanFault.NoLanAdapter,"Missing adapter was hidden");
            Require(LanNetworkFailure.DiscoveryResult(true,0,3,true)==LanFault.SocketAccessDenied,"Blocked discovery became no rooms");
            Require(LanNetworkFailure.DiscoveryResult(true,0,3,false)==LanFault.DiscoverySendFailed,"Failed discovery became no rooms");
            Require(LanNetworkFailure.DiscoveryResult(true,1,3,true)==LanFault.None,"One failed adapter overrode successful discovery send");
            Console.WriteLine("PASS: bounded network diagnostics and strict IPC fault vocabulary");
        }
        private static void PairingFailureChecks()
        {
            int unusedPort;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            unusedPort=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            using(var client=LanPairing.JoinLoopbackForChecks("123456",unusedPort)) {
                Require(Wait(()=>client.Finished,3000),"Refused client did not finish");
                Require(!client.Succeeded && (client.Failure==LanFault.ConnectionRefused || client.Failure==LanFault.NoHostResponse),"Closed port lost its refusal/timeout diagnosis: "+client.Failure);
            }
            using(var host=LanPairing.HostLoopbackForChecks())
            using(var client=LanPairing.JoinLoopbackForChecks(host.Code=="123456"?"654321":"123456",host.ListenPortForChecks)) {
                Require(Wait(()=>client.Finished,5000),"Wrong-code exchange did not finish");
                Require(!client.Succeeded && client.Failure==LanFault.PairingProofFailed,"Wrong code lost proof-failure diagnosis");
            }
            using(var host=LanPairing.HostLoopbackForChecks()) {
                var client=LanPairing.JoinLoopbackForChecks(host.Code,host.ListenPortForChecks);
                client.Dispose();Require(Wait(()=>client.Finished,1000),"Cancelled client did not finish");
                Require(client.Failure==LanFault.None,"Cancellation became a network fault");
            }
            Console.WriteLine("PASS: refused connection, wrong proof, normal expiry and cancellation diagnostics");
        }
    }
}
