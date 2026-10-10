using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static void DrainStress(LanSecureTransport transport,HashSet<ulong> seen,ref int received)
        {
            Packet packet;IPEndPoint peer;
            for(int i=0;i<128 && transport.TryReceive(out peer,out packet);i++) {
                uint sequence=packet.Kind==PacketKind.TrainMotion?packet.Train.Sequence:packet.Sequence;
                if(sequence==0)continue;
                ulong id=((ulong)packet.Kind<<32)|sequence;
                Require(seen.Add(id),"Encrypted replay reached stress application queue twice");received++;
            }
        }
        private static void StressChecks(string[] args)
        {
            int seconds=60,index=Array.IndexOf(args,"--seconds");
            if(index>=0 && (index+1>=args.Length || !int.TryParse(args[index+1],out seconds) || seconds<30 || seconds>300))
                throw new ArgumentException("Stress seconds must be 30..300");
            int port=FreeUdpPort();var secret=TestKey(5539);
            using(var host=LanSecureTransport.HostWithKey(port,IPAddress.Loopback,"checks",secret))
            using(var proxy=new LoopbackDatagramProxy(port,secret))
            using(var client=LanSecureTransport.JoinWithKey(proxy.Port,IPAddress.Loopback,"checks",secret))
            using(var process=Process.GetCurrentProcess()) {
                Establish(host,client);ulong session=host.Session;
                proxy.Capture(true);proxy.DropEvery(17);
                var toHost=new HashSet<ulong>();var toClient=new HashSet<ulong>();int receivedHost=0,receivedClient=0,maxQueue=0,ticks=0,noise=0;
                long baseline=GC.GetTotalMemory(true),peakManaged=baseline;process.Refresh();long baselinePrivate=process.PrivateMemorySize64,peakPrivate=baselinePrivate;
                TimeSpan cpu=process.TotalProcessorTime;var watch=Stopwatch.StartNew();double nextTick=0,nextNoise=1,nextReplay=4,nextReport=10;
                while(watch.Elapsed.TotalSeconds<seconds) {
                    double now=watch.Elapsed.TotalSeconds;
                    Require(host.SecurityFailure==null && client.SecurityFailure==null,"Noise/loss/queue pressure revoked innocent admission");
                    Require(host.Session==session && client.Session==session,"Stress lost established session");
                    // At most one tick after a stall: never 'catch up' with a flood.
                    if(now>=nextTick) {
                        nextTick=now+.05;ticks++;uint sequence=(uint)ticks;
                        var state=State(sequence);state.Session=session;host.Send(state);client.Send(state);
                        host.Send(new Packet {Kind=PacketKind.TrainMotion,GameVersion="checks",Session=session,
                            Train=new TrainFrame {Sequence=sequence,Revision=1,Scene="Terrain1",Cars=new[]{new CarPose {Root=Pose(),Body=Pose(),Front=Pose(),Back=Pose()}}}});
                        host.Send(new Packet {Kind=PacketKind.ZombieState,GameVersion="checks",Session=session,WorldEpoch=1,Scene="Terrain1",Sequence=sequence,
                            Zombies=new[]{new ZombieFrame {Id=1,Prefab="checks",Health=100}}});
                        if(ticks%2==0) {
                            client.Send(new Packet {Kind=PacketKind.ZombieAction,GameVersion="checks",Session=session,WorldEpoch=1,Scene="Terrain1",Sequence=sequence,Revision=1,EquippedItemId=1,AimZ=1});
                            client.Send(new Packet {Kind=PacketKind.ZombieHurtAck,GameVersion="checks",Session=session,WorldEpoch=1,Scene="Terrain1",Sequence=sequence});
                        }
                        if(ticks%4==0)client.Send(new Packet {Kind=PacketKind.TrainLayoutAck,GameVersion="checks",Session=session,Revision=1});
                    }
                    // Continuous finite invalid raw UDP in both directions.
                    // This test injects < 210 KiB/s total; it does not saturate LAN.
                    if(now>=nextNoise) { nextNoise=now+1;proxy.InvalidBurst(true,80);noise+=160; }
                    if(now>=nextReplay) { nextReplay=now+5;Require(proxy.Replay()>0,"Stress capture empty");proxy.Capture(false); }
                    proxy.Tamper(now%6<.12,true); // Valid admission tag, invalid AEAD.
                    // Simulate a temporarily blocked game consumer. A full local
                    // queue must remain bounded and must not accuse the peer.
                    bool paused=now>=12 && now<19;
                    maxQueue=Math.Max(maxQueue,Math.Max(host.QueuedPackets,client.QueuedPackets));Require(maxQueue<=128,"Receive queue grew above bound");
                    if(!paused) { DrainStress(host,toHost,ref receivedHost);DrainStress(client,toClient,ref receivedClient); }
                    if(now>=nextReport) {
                        nextReport=now+10;long managed=GC.GetTotalMemory(true);peakManaged=Math.Max(peakManaged,managed);process.Refresh();peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);
                        Console.WriteLine("STRESS: {0:F0}s; host={1}, client={2}, queue peak={3}, managed={4:F1}MiB",now,receivedHost,receivedClient,maxQueue,managed/1048576.0);
                    }
                    Thread.Sleep(2);
                }
                proxy.Tamper(false,true);proxy.DropEvery(0);Thread.Sleep(250);
                DrainStress(host,toHost,ref receivedHost);DrainStress(client,toClient,ref receivedClient);
                Require(receivedHost>seconds*10 && receivedClient>seconds*20,"Stress valid delivery insufficient");
                Require(maxQueue==128,"Consumer-pause test did not exercise full queue");
                Require(host.SecurityFailure==null && client.SecurityFailure==null,"Stress guard tripped after noise");
                var finalState=State((uint)ticks+100);finalState.Session=session;host.Send(finalState);client.Send(finalState);
                Packet packet;IPEndPoint peer;
                Require(Wait(()=>host.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==finalState.Sequence,4000),"Host did not recover after stress");
                Require(Wait(()=>client.TryReceive(out peer,out packet) && packet.Kind==PacketKind.State && packet.Sequence==finalState.Sequence,4000),"Client did not recover after stress");
                peakManaged=Math.Max(peakManaged,GC.GetTotalMemory(true));process.Refresh();peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);
                Require(peakManaged-baseline<64*1048576L,"Managed memory growth exceeds bounded stress allowance");
                Require(peakPrivate-baselinePrivate<256*1048576L,"Private memory growth exceeds bounded stress allowance");
                Console.WriteLine("PASS: {0}s loopback stress both roles; host={1}, client={2}, raw noise={3}, peak queue={4}; CPU={5:F2}s; managed delta={6:F2}MiB; private delta={7:F2}MiB",
                    seconds,receivedHost,receivedClient,noise,maxQueue,(process.TotalProcessorTime-cpu).TotalSeconds,(peakManaged-baseline)/1048576.0,(peakPrivate-baselinePrivate)/1048576.0);
            }
        }
    }
}
