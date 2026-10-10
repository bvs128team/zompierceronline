using System;
using System.IO;
using System.Net;
using System.Text;

namespace ZompiercerLAN
{
    internal static partial class Program
    {
        private static LanIpcFrame Event(LanIpcKind kind,byte[] bytes,uint context=1) { return new LanIpcFrame {Kind=kind,Payload=bytes,Context=context}; }
        private static void Boot(LanBrokerState state) { state.Apply(Event(LanIpcKind.Boot,new byte[]{1}),0); }
        private static LanWorkerStatus Active(ulong session=42,int port=27777) { return new LanWorkerStatus {State=LanWorkerState.Active,Session=session,PeerAddress=IPAddress.Loopback,PeerPort=port}; }
        private static void Snapshot(LanBrokerState state,LanWorkerStatus value,uint context=1,double now=0) { state.Apply(Event(LanIpcKind.Status,LanIpc.StatusBytes(value),context),now); }
        private static LanBrokerState ClientBroker()
        {
            var state=new LanBrokerState(LanWorkerMode.Join,"checks",IPAddress.Loopback);Boot(state);Snapshot(state,Active());
            var welcome=Control(PacketKind.Welcome);state.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(welcome)),0);return state;
        }
        private static void IpcChecks()
        {
            IpcApprovalBindingChecks();
            byte[] frame;
            using(var stream=new MemoryStream()) { ulong sequence=0;LanIpc.Write(stream,ref sequence,Event(LanIpcKind.Status,new byte[LanIpc.Maximum]));frame=stream.ToArray();stream.Position=0;sequence=0;
                Require(LanIpc.Read(stream,ref sequence).Payload.Length==LanIpc.Maximum && sequence==1,"IPC maximum frame roundtrip"); }
            for(int i=0;i<frame.Length;i++) { var shortFrame=new byte[i];Array.Copy(frame,shortFrame,i);ExpectRejected(()=>{ulong seq=0;using(var stream=new MemoryStream(shortFrame))LanIpc.Read(stream,ref seq);},"IPC truncated frame accepted"); }
            foreach(int offset in new[]{0,4,5,6,7,8,16}) { var bad=(byte[])frame.Clone();bad[offset]=(byte)(offset==16?0:255);ExpectRejected(()=>{ulong seq=0;using(var stream=new MemoryStream(bad))LanIpc.Read(stream,ref seq);},"IPC bad header accepted: "+offset); }
            ExpectRejected(()=>{ulong seq=1;using(var stream=new MemoryStream(frame))LanIpc.Read(stream,ref seq);},"IPC repeated sequence accepted");
            var random=new Random(77445);
            for(int i=0;i<20000;i++) {
                var raw=new byte[random.Next(0,2200)];random.NextBytes(raw);
                try { ulong seq=0;using(var stream=new MemoryStream(raw))LanIpc.Read(stream,ref seq); }catch(InvalidDataException){}catch(EndOfStreamException){}
                try { LanIpc.ReadStatus(raw); }catch(InvalidDataException){}catch(EndOfStreamException){}catch(DecoderFallbackException){}
                try { LanIpc.ReadRooms(raw); }catch(InvalidDataException){}catch(EndOfStreamException){}catch(DecoderFallbackException){}
            }
            // 1.4.10: a LAN code lasts 30 s: the host's broker shows that much and refuses a second more.
            var thirty=new LanBrokerState(LanWorkerMode.Host,"checks",null);Boot(thirty);
            Snapshot(thirty,new LanWorkerStatus {State=LanWorkerState.Invitation,Code="123456",Seconds=LanLocalText.PinSeconds});
            Require(LanLocalText.PinSeconds==30 && thirty.Snapshot.Seconds==30,"LAN code of 30 s refused");
            var longer=new LanBrokerState(LanWorkerMode.Host,"checks",null);Boot(longer);
            ExpectRejected(()=>Snapshot(longer,new LanWorkerStatus {State=LanWorkerState.Invitation,Code="123456",Seconds=LanLocalText.PinSeconds+1}),"LAN host accepted a PIN past 30 s");
            var host=new LanBrokerState(LanWorkerMode.Host,"checks",null);Boot(host);
            ExpectRejected(()=>Snapshot(host,Active(42,55555)),"Helper approved itself on host");
            var pending=new LanWorkerStatus {State=LanWorkerState.PendingApproval,Request=11,PendingAddress=IPAddress.Loopback};Snapshot(host,pending);
            Require(host.PendingApproval,"Host request missing");byte[] approval=host.Approve();ulong approved=0;IPAddress approvedPeer=null;
            LanIpc.Decode(approval,r=>{approved=r.ReadUInt64();approvedPeer=LanIpc.ReadAddress(r);});Require(approved==11 && IPAddress.Loopback.Equals(approvedPeer),"Approval not bound to shown request/peer");
            var active=Active(42,55555);active.Request=12;
            ExpectRejected(()=>Snapshot(host,active),"Helper changed approved request");
            active.Request=11;active.PeerAddress=IPAddress.Parse("192.168.1.8");ExpectRejected(()=>Snapshot(host,active),"Helper substituted approved peer");
            active.PeerAddress=IPAddress.Loopback;Snapshot(host,active);
            host.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(Control(PacketKind.Hello))),0);
            ExpectRejected(()=>host.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(State(1))),0),"Hello granted game access without parent Welcome");
            Require(host.AllowSend(Control(PacketKind.Welcome)),"Parent Welcome rejected");host.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(State(1))),0);
            host.Restart();Require(host.Context==2 && host.QueuedPackets==0,"IPC restart retained queued state");
            Snapshot(host,active,1,.1);Require(host.Snapshot.Session==0,"Old IPC context revived session");active.Session=99;Snapshot(host,active,2,1);
            ExpectRejected(()=>host.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(State(2)),2),1),"Old application session crossed IPC restart");
            host.Reject();ExpectRejected(()=>Snapshot(host,active,2,2),"Rejected host approval reactivated");
            var client=new LanBrokerState(LanWorkerMode.Join,"checks",IPAddress.Loopback);Boot(client);
            ExpectRejected(()=>Snapshot(client,pending),"Client helper issued a host approval dialog");
            active=Active();active.PeerAddress=IPAddress.Parse("192.168.1.8");ExpectRejected(()=>Snapshot(client,active),"Manual destination substituted");
            active=Active(42,12345);ExpectRejected(()=>Snapshot(client,active),"Host port substituted");
            client=ClientBroker();var forbidden=Kit(PacketKind.CombatProposal);
            ExpectRejected(()=>client.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(forbidden)),1),"Helper bypassed packet direction");
            ExpectRejected(()=>client.Apply(Event(LanIpcKind.Packet,new byte[]{1}),1),"Helper bypassed packet decoder");
            ExpectRejected(()=>client.Apply(Event(LanIpcKind.Boot,new byte[]{1}),1),"Helper repeated boot");
            ExpectRejected(()=>client.Apply(Event(LanIpcKind.Status,LanIpc.StatusBytes(Active()),2),1),"Helper advanced local context");
            // Consumer stalls do not grow the parent's queue or revoke valid data.
            client=ClientBroker();IPEndPoint from;Packet packet;while(client.TryReceive(out from,out packet)){}
            for(uint i=1;i<=200;i++)client.Apply(Event(LanIpcKind.Packet,LanProtocol.Encode(State(i))),i*.05);
            Require(client.QueuedPackets==128,"Game-side receive queue is not bounded");int count=0;while(client.TryReceive(out from,out packet))count++;Require(count==128,"Game queue corrupted under pressure");
            var browser=new LanBrokerState(LanWorkerMode.Browse,"",null);Boot(browser);
            ExpectRejected(()=>Snapshot(browser,Active()),"Browser helper obtained gameplay privilege");
            var rooms=new[]{new LanRoomInfo {Address=IPAddress.Loopback,Room=new byte[16],Name="Комната",Flags=1}};
            browser.Apply(Event(LanIpcKind.Rooms,LanIpc.RoomsBytes(rooms)),0);Require(browser.BrowserFinished && browser.Rooms.Length==1,"IPC room result missing");
            ExpectRejected(()=>browser.Apply(Event(LanIpcKind.Rooms,LanIpc.RoomsBytes(rooms)),1),"Repeated room result accepted");
            Console.WriteLine("PASS: IPC framing / 20000 malformed frames-status-rooms / parent-owned approval / compromised helper roles, sessions, context / bounded game queue");
        }

        private static void IpcApprovalBindingChecks()
        {
            var shown = IPAddress.Parse("192.168.1.20");
            var substituted = IPAddress.Parse("192.168.1.21");
            var state = new LanBrokerState(LanWorkerMode.Host, "checks", null);
            Boot(state);
            Snapshot(state, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 71, PendingAddress = shown });
            // A compromised helper must not replace the identity shown for an
            // existing request by laundering it through an intermediate status.
            ExpectRejected(() => {
                Snapshot(state, new LanWorkerStatus { State = LanWorkerState.Pairing, Request = 71, PendingAddress = substituted }, now: .1);
                Snapshot(state, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 71, PendingAddress = substituted }, now: .2);
                state.Approve();
            }, "Helper laundered an approval address through an intermediate status");

            state = new LanBrokerState(LanWorkerMode.Host, "checks", null);
            Boot(state);
            Snapshot(state, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 71, PendingAddress = shown });
            Snapshot(state, new LanWorkerStatus { State = LanWorkerState.Pairing }, now: .1);
            Snapshot(state, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 71, PendingAddress = shown }, now: .2);
            ulong request = 0; IPAddress peer = null;
            LanIpc.Decode(state.Approve(), r => { request = r.ReadUInt64(); peer = LanIpc.ReadAddress(r); });
            Require(request == 71 && shown.Equals(peer), "Unchanged approval identity lost after intermediate status");
            ExpectRejected(() => Snapshot(state, new LanWorkerStatus { State = LanWorkerState.PendingApproval, Request = 72, PendingAddress = shown }, now: .3),
                "Helper changed a fixed request ID");
            Console.WriteLine("PASS: immutable approval identity across untrusted intermediate statuses");
        }
    }
}
