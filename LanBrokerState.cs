using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace ZompiercerLAN
{
    // Game-side security boundary, also exercised without Windows/Unity in checks.
    // Callers serialize access; the child can never approve itself or load code.
    internal sealed class LanBrokerState
    {
        private readonly LanWorkerMode _mode;
        private readonly IPAddress _selectedAddress;
        private readonly string _gameVersion;
        private readonly Queue<Packet> _received=new Queue<Packet>();
        private readonly LanRateLimit _frames=new LanRateLimit(400,256),_bytes=new LanRateLimit(512*1024,512*1024),_statuses=new LanRateLimit(10,16),_sessions=new LanRateLimit(.5,4);
        private readonly LanMessageLimits _messages=new LanMessageLimits();
        private readonly LanPeerGuard _guard=new LanPeerGuard();
        private bool _boot,_established,_rejected,_roomsReceived;
        private ulong _seenRequest,_approvedRequest;
        private IPAddress _seenAddress,_approvedAddress,_fixedPeer;
        private string _roomName;
        internal uint Context { get; private set; }=1;
        internal LanWorkerStatus Snapshot { get; private set; }=new LanWorkerStatus();
        internal LanRoomInfo[] Rooms { get; private set; }=new LanRoomInfo[0];
        internal bool PendingApproval { get { return !_rejected && _approvedRequest==0 && Snapshot.State==LanWorkerState.PendingApproval; } }
        internal bool TransportReady { get { return !_rejected && Snapshot.TransportReady; } }
        internal int QueuedPackets { get { return _received.Count; } }
        internal bool BrowserFinished { get { return _roomsReceived; } }
        internal bool Booted { get { return _boot; } }
        internal IPEndPoint Peer { get { return Snapshot.Session!=0 && Snapshot.PeerAddress!=null?new IPEndPoint(Snapshot.PeerAddress,Snapshot.PeerPort):null; } }
        internal LanBrokerState(LanWorkerMode mode,string gameVersion,IPAddress selectedAddress)
        { _mode=mode;_gameVersion=gameVersion;_selectedAddress=selectedAddress; }
        internal void Apply(LanIpcFrame frame,double now)
        {
            if(!_frames.Take(1,now) || !_bytes.Take(frame.Payload.Length+20,now))throw new InvalidDataException("IPC receive budget");
            if(frame.Context<Context)return;
            if(frame.Context!=Context)throw new InvalidDataException("Unexpected IPC context");
            if(frame.Kind==LanIpcKind.Boot) {
                if(_boot || frame.Payload.Length!=1 || frame.Payload[0]!=1 || Context!=1)throw new InvalidDataException("IPC boot");_boot=true;return;
            }
            if(!_boot)throw new InvalidDataException("IPC before boot");
            switch(frame.Kind) {
                case LanIpcKind.Status:
                    if(!_statuses.Take(1,now))throw new InvalidDataException("IPC status flood");
                    Status(LanIpc.ReadStatus(frame.Payload),now);break;
                case LanIpcKind.Packet:
                    Packet packet;if(LanModes.IsBrowse(_mode) || !TransportReady || Snapshot.Session==0 || frame.Payload.Length>1200 || !LanProtocol.TryDecode(frame.Payload,out packet))throw new InvalidDataException("IPC application format/stage");
                    if(packet.Session!=Snapshot.Session)throw new InvalidDataException("IPC application session");
                    if(!LanNetworkPolicy.Allowed(LanModes.IsHost(_mode),packet.Kind,_established))throw new InvalidDataException("IPC application direction/stage");
                    if(!_messages.Take(packet.Kind,now)) { _guard.ExcessRate(now);if(_guard.Failure!=null)throw new InvalidDataException("IPC peer rate");return; }
                    if(packet.GameVersion!=_gameVersion && !(LanModes.IsHost(_mode) && packet.Kind==PacketKind.Hello || LanModes.IsJoin(_mode) && packet.Kind==PacketKind.VersionMismatch))return;
                    if(LanModes.IsJoin(_mode) && packet.Kind==PacketKind.Welcome && packet.GameVersion==_gameVersion)_established=true;
                    if(_received.Count<128)_received.Enqueue(packet);break;
                case LanIpcKind.Rooms:
                    if(!LanModes.IsBrowse(_mode) || _roomsReceived)throw new InvalidDataException("IPC rooms stage");
                    var rooms=LanIpc.ReadRooms(frame.Payload);
                    // A LAN browser reports LAN rooms only, a relay browser relay rooms only.
                    foreach(var room in rooms)if((room.Flags==LanRoomInfo.RelayFlag)!=(_mode==LanWorkerMode.RelayBrowse))throw new InvalidDataException("IPC room kind");
                    Rooms=rooms;_roomsReceived=true;break;
                case LanIpcKind.Fault:
                    throw LanFaultException.Read(frame.Payload);
                default:throw new InvalidDataException("IPC event direction");
            }
        }
        private void Status(LanWorkerStatus s,double now)
        {
            if(LanModes.IsBrowse(_mode)) {
                if(s.State!=LanWorkerState.Browsing && s.State!=LanWorkerState.BrowseComplete || s.Session!=0 || s.Request!=0 || s.Code.Length!=0 || s.RelayRoom.Length!=0 ||
                    s.RoomName.Length!=0 || s.CustomSecret || s.Seconds!=0 || s.PeerAddress!=null || s.PendingAddress!=null)
                    throw new InvalidDataException("IPC browser privilege");Snapshot=s;return;
            }
            if(s.State==LanWorkerState.Browsing || s.State==LanWorkerState.BrowseComplete || s.State==LanWorkerState.Starting)throw new InvalidDataException("IPC state role");
            // LAN invitations live LanLocalText.PinSeconds and never carry a relay room; relay peers are only the virtual endpoint.
            if(!LanModes.IsRelay(_mode) && (s.Seconds>LanLocalText.PinSeconds || s.RelayRoom.Length!=0) || _mode!=LanWorkerMode.RelayHost && s.RelayRoom.Length!=0)throw new InvalidDataException("IPC relay fields");
            if(LanModes.IsRelay(_mode) && (s.PendingAddress!=null && !s.PendingAddress.Equals(LanRelayText.VirtualPeer) || s.PeerAddress!=null && !s.PeerAddress.Equals(LanRelayText.VirtualPeer)))throw new InvalidDataException("IPC relay peer");
            if(LanModes.IsJoin(_mode) && (s.Code.Length!=0 || s.State==LanWorkerState.PendingApproval || s.State==LanWorkerState.Invitation || s.Request!=0 || s.PendingAddress!=null))throw new InvalidDataException("IPC host-only state");
            // Room name: relay join only, after the proof, never replaced. Host password flag: relay host only.
            if(_mode!=LanWorkerMode.RelayJoin && s.RoomName.Length!=0 || _mode!=LanWorkerMode.RelayHost && s.CustomSecret)throw new InvalidDataException("IPC relay fields");
            if(s.RoomName.Length!=0) {
                if(s.State!=LanWorkerState.WaitingApproval && !s.TransportReady || _roomName!=null && _roomName!=s.RoomName)throw new InvalidDataException("IPC room name");
                _roomName=s.RoomName;
            }
            // An open invitation shows exactly one secret kind: the random PIN, or the host's own password (never echoed).
            if(s.State==LanWorkerState.Invitation && ((s.Code.Length!=0)==s.CustomSecret || LanModes.IsRelay(_mode)!=(s.RelayRoom.Length!=0)))
                throw new InvalidDataException("IPC invitation fields");
            if(s.State==LanWorkerState.PendingApproval) {
                // Snapshot is untrusted, replaceable display state. Bind the first
                // request independently so an intermediate status cannot change
                // the peer that the user is being asked to authorize.
                if(_seenRequest!=0 && (_seenRequest!=s.Request || !s.PendingAddress.Equals(_seenAddress)))throw new InvalidDataException("IPC request substitution");
                _seenRequest=s.Request;_seenAddress=s.PendingAddress;
            }
            if(s.TransportReady) {
                if(_rejected || LanModes.IsHost(_mode) && (_approvedRequest==0 || _approvedRequest!=s.Request || !s.PeerAddress.Equals(_approvedAddress)))throw new InvalidDataException("IPC unapproved host access");
                if(_selectedAddress!=null && !_selectedAddress.Equals(s.PeerAddress) || _fixedPeer!=null && !_fixedPeer.Equals(s.PeerAddress))throw new InvalidDataException("IPC peer substitution");
                if(LanModes.IsJoin(_mode) && s.Session!=0 && s.PeerPort!=27777)throw new InvalidDataException("IPC host port");_fixedPeer=s.PeerAddress;
            } else if(TransportReady)throw new InvalidDataException("IPC admission revoked");
            if(s.Session!=Snapshot.Session) {
                if(s.Session!=0 && !_sessions.Take(1,now))throw new InvalidDataException("IPC session churn");_received.Clear();_established=false;
            }
            Snapshot=s;
        }
        internal byte[] Approve()
        {
            if(!LanModes.IsHost(_mode) || !PendingApproval)throw new InvalidOperationException("No approval request");
            _approvedRequest=_seenRequest;_approvedAddress=_seenAddress;
            return LanIpc.Encode(w=>{w.Write(_approvedRequest);LanIpc.WriteAddress(w,_approvedAddress);});
        }
        internal void Reject() { _rejected=true;ClearPackets(); }
        internal void Restart()
        {
            if(Context==uint.MaxValue || !TransportReady)throw new InvalidOperationException("Cannot restart IPC");Context++;ClearPackets();Snapshot.Session=0;Snapshot.State=LanWorkerState.Connecting;
        }
        internal bool AllowSend(Packet packet)
        {
            if(!TransportReady || Snapshot.Session==0 || packet.Session!=Snapshot.Session || !LanNetworkPolicy.Allowed(!LanModes.IsHost(_mode),packet.Kind,true))return false;
            if(LanModes.IsHost(_mode) && packet.Kind==PacketKind.Welcome)_established=true;return true;
        }
        internal bool TryReceive(out IPEndPoint peer,out Packet packet)
        { peer=null;packet=null;if(_received.Count==0)return false;packet=_received.Dequeue();peer=Peer;return peer!=null && packet.Session==Snapshot.Session; }
        internal void ClearPackets() { _received.Clear();_established=false; }
    }
}
