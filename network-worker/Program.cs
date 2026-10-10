using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ZompiercerLAN
{
    // DTO only; the worker never loads Unity or any assembly of the game.
    internal sealed class ClockSnapshot { internal int Year,Day;internal float Hour,HoursPerRealSecond; }
    internal static class NetworkWorkerProgram
    {
        private static int Main(string[] args)
        {
            try {
                bool internet=LanSandboxProcess.RequireSandbox();
                long read,write;
                if(args.Length!=2 || !long.TryParse(args[0],NumberStyles.None,CultureInfo.InvariantCulture,out read) ||
                    !long.TryParse(args[1],NumberStyles.None,CultureInfo.InvariantCulture,out write) || read<=0 || write<=0 || read==write)return 2;
                using(var input=new FileStream(new SafeFileHandle(new IntPtr(read),true),FileAccess.Read,4096,false))
                using(var output=new FileStream(new SafeFileHandle(new IntPtr(write),true),FileAccess.Write,4096,false))
                using(var runtime=new LanWorkerRuntime(input,output,internet))return runtime.Run();
            } catch { return 3; }
        }
    }
    internal sealed class LanWorkerRuntime : IDisposable
    {
        private readonly Stream _input,_output;
        private readonly bool _internet;
        private readonly Stopwatch _time=Stopwatch.StartNew();
        private readonly object _gate=new object();
        private readonly Queue<LanIpcFrame> _commands=new Queue<LanIpcFrame>();
        private volatile bool _stopping,_readFailed;
        private long _lastCommandTicks;
        private ulong _readSequence,_writeSequence,_request;
        private uint _context=1;
        private LanWorkerMode _mode;
        private string _gameVersion,_roomName="";
        private ILanPairing _pairing;
        private LanRelayPairing _relayPairing;
        private LanRelayLink _relayLink;
        private LanRoomAdvertisement _advertisement;
        private LanRoomBrowser _browser;
        private LanRelayBrowser _relayBrowser;
        private LanSecureTransport _transport;
        private IPAddress _pendingAddress,_pairedAddress;
        private bool _approved,_rejected,_roomsSent;
        private LanWorkerState _ended=LanWorkerState.Failed;
        private byte[] _previousStatus;
        private ulong _publishedSession;
        private double _nextStatus;
        internal LanWorkerRuntime(Stream input,Stream output,bool internet) { _input=input;_output=output;_internet=internet; }
        internal int Run()
        {
            try {
                Emit(LanIpcKind.Boot,new byte[]{1});
                var first=LanIpc.Read(_input,ref _readSequence);
                if(first.Kind!=LanIpcKind.Start || first.Context!=1)throw new InvalidDataException("IPC initialization");
                string name=null,pin=null;IPAddress address=null;LanRelayOptions relay=null;
                try { LanIpc.Decode(first.Payload,r=>{_mode=(LanWorkerMode)r.ReadByte();_gameVersion=LanIpc.ReadText(r,40);name=LanIpc.ReadText(r,20);pin=LanIpc.ReadText(r,LanRelayText.MaxSecretBytes);address=LanIpc.ReadAddress(r);
                    if(LanModes.IsInternet(_mode))relay=LanRelayOptions.Read(r);}); }
                finally { Array.Clear(first.Payload,0,first.Payload.Length); }
                // LAN: six-digit PIN only. Relay join: PIN or the host's password.
                // Relay host: empty for a random PIN, or the host's own password.
                bool secret=_mode==LanWorkerMode.Join?LanLocalText.ValidPin(pin):_mode==LanWorkerMode.RelayJoin?LanRelayText.ValidSecret(pin):
                    _mode==LanWorkerMode.RelayHost?pin.Length==0 || LanRelayText.ValidSecret(pin):pin.Length==0;
                // A listed room (0.13.0) only with the host's own strong password.
                if(_mode==LanWorkerMode.RelayHost && relay.PublicName.Length!=0 && !LanRelayText.StrongPublicSecret(pin) ||
                    _mode!=LanWorkerMode.RelayHost && relay!=null && relay.PublicName.Length!=0 || _mode==LanWorkerMode.RelayBrowse && relay.Room.Length!=0)throw new InvalidDataException("IPC options");
                if(!LanModes.Valid(_mode) || !LanModes.IsBrowse(_mode) && string.IsNullOrEmpty(_gameVersion) ||
                    name!=LanLocalText.CleanName(name) || !secret || _mode!=LanWorkerMode.Join && address!=null ||
                    _mode==LanWorkerMode.RelayHost && relay.Room.Length!=0 || _mode==LanWorkerMode.RelayJoin && relay.Room.Length==0)throw new InvalidDataException("IPC options");
                // The AppContainer capability chosen by the game must match the mode:
                // a LAN worker cannot reach the internet and a relay worker cannot reach the LAN.
                if(LanModes.IsInternet(_mode)!=_internet)throw new InvalidDataException("IPC capability");
                LanSecureTransport.VerifyLibrary();
                if(_mode==LanWorkerMode.Host) { var lan=LanPairing.Host(name);_advertisement=lan.DetachAdvertisement();_pairing=lan; }
                else if(_mode==LanWorkerMode.Join)_pairing=LanPairing.Join(pin,address);
                else if(LanModes.IsRelay(_mode)) {
                    _relayLink=new LanRelayLink(relay,_mode==LanWorkerMode.RelayHost);
                    _relayPairing=_mode==LanWorkerMode.RelayHost?LanRelayPairing.Host(_relayLink,name,pin,relay.PublicName.Length!=0):LanRelayPairing.Join(pin,_relayLink);
                    _pairing=_relayPairing;
                }
                else if(_mode==LanWorkerMode.RelayBrowse)_relayBrowser=new LanRelayBrowser(relay);
                else _browser=new LanRoomBrowser();
                pin=null;Interlocked.Exchange(ref _lastCommandTicks,_time.ElapsedTicks);
                var reader=new Thread(ReadCommands) {IsBackground=true,Name="LAN worker IPC reader"};reader.Start();
                while(!_stopping) {
                    if(_readFailed || (_time.ElapsedTicks-Interlocked.Read(ref _lastCommandTicks))/(double)Stopwatch.Frequency>8)throw new IOException("IPC parent unavailable");
                    for(int i=0;i<32;i++) { LanIpcFrame command;lock(_gate){if(_commands.Count==0)break;command=_commands.Dequeue();}try{Command(command);}finally{Array.Clear(command.Payload,0,command.Payload.Length);} }
                    if(_stopping)break;
                    PollPairing();
                    // After pairing, a lost relay room ends the session explicitly.
                    if(_pairing==null && _relayLink!=null && _relayLink.Failure!=LanFault.None)throw new LanFaultException(_relayLink.Failure);
                    if(_transport!=null && _transport.SecurityFailure!=null) { Emit(LanIpcKind.Fault,new byte[]{1});return 4; }
                    if(_transport!=null)try{_transport.Tick();}catch(IOException){/* The bounded reconnect worker reports Connecting via snapshots. */}
                    PublishStatus();
                    if(_browser!=null && _browser.Finished && !_roomsSent) {
                        if(_browser.Failure!=LanFault.None)throw new LanFaultException(_browser.Failure);
                        var found=_browser.Rooms;var rooms=new LanRoomInfo[found.Length];
                        for(int i=0;i<found.Length;i++)rooms[i]=new LanRoomInfo {Address=found[i].Address,Room=found[i].Room,Name=found[i].Name,Flags=found[i].Flags};
                        Emit(LanIpcKind.Rooms,LanIpc.RoomsBytes(rooms));_roomsSent=true;_browser.Dispose();_browser=null;
                    }
                    if(_relayBrowser!=null && _relayBrowser.Finished && !_roomsSent) {
                        if(_relayBrowser.Failure!=LanFault.None)throw new LanFaultException(_relayBrowser.Failure);
                        Emit(LanIpcKind.Rooms,LanIpc.RoomsBytes(_relayBrowser.Rooms));_roomsSent=true;_relayBrowser.Dispose();_relayBrowser=null;
                    }
                    if(_transport!=null)for(int i=0;i<32;i++) {
                        IPEndPoint peer;Packet packet;if(!_transport.TryReceive(out peer,out packet))break;
                        if(packet.Session!=_publishedSession || packet.Session!=_transport.Session)continue;
                        Emit(LanIpcKind.Packet,LanProtocol.Encode(packet));
                    }
                    Thread.Sleep(10);
                }
                return 0;
            } catch(Exception ex) {
                var fault=ex as LanFaultException;
                try{Emit(LanIpcKind.Fault,new byte[]{(byte)(fault!=null?fault.Fault:LanNetworkFailure.From(ex))});}catch{}return 5;
            }
        }
        private void ReadCommands()
        {
            var messages=new LanRateLimit(400,256);var bytes=new LanRateLimit(512*1024,512*1024);
            try { while(!_stopping) {
                var frame=LanIpc.Read(_input,ref _readSequence);
                if(frame.Kind>=LanIpcKind.Boot || !messages.Take(1,_time.Elapsed.TotalSeconds) || !bytes.Take(frame.Payload.Length+20,_time.Elapsed.TotalSeconds))throw new InvalidDataException("IPC command budget");
                lock(_gate) {if(_commands.Count>=128)throw new InvalidDataException("IPC command queue");_commands.Enqueue(frame);}
                Interlocked.Exchange(ref _lastCommandTicks,_time.ElapsedTicks);
            }}catch{_readFailed=true;}
        }
        private void Command(LanIpcFrame frame)
        {
            if(frame.Kind==LanIpcKind.Restart) {
                if(_context==uint.MaxValue || frame.Context!=_context+1 || frame.Payload.Length!=0 || _transport==null)throw new InvalidDataException("IPC restart");
                _context=frame.Context;_publishedSession=0;_previousStatus=null;_transport.Restart();return;
            }
            if(frame.Context!=_context)throw new InvalidDataException("IPC command context");
            switch(frame.Kind) {
                case LanIpcKind.Ping:if(frame.Payload.Length!=0)throw new InvalidDataException();break;
                case LanIpcKind.Stop:if(frame.Payload.Length!=0)throw new InvalidDataException();_stopping=true;break;
                case LanIpcKind.Approve:
                    ulong request=0;IPAddress peer=null;LanIpc.Decode(frame.Payload,r=>{request=r.ReadUInt64();peer=LanIpc.ReadAddress(r);});
                    if(!LanModes.IsHost(_mode) || _pairing==null || !_pairing.PendingApproval || request==0 || request!=_request || peer==null || !peer.Equals(_pendingAddress))throw new InvalidDataException("IPC approval");
                    _approved=true;_pairing.Approve();break;
                case LanIpcKind.Reject:
                    if(frame.Payload.Length!=0 || _pairing==null)throw new InvalidDataException("IPC reject");_rejected=true;_pairing.Reject();break;
                case LanIpcKind.Send:
                    Packet packet;
                    if(frame.Payload.Length>1200 || !LanProtocol.TryDecode(frame.Payload,out packet) || !LanNetworkPolicy.Allowed(!LanModes.IsHost(_mode),packet.Kind,true))throw new InvalidDataException("IPC packet");
                    if(_transport!=null && packet.Session==_transport.Session)_transport.Send(packet);break;
                default:throw new InvalidDataException("IPC command kind");
            }
        }
        private void PollPairing()
        {
            if(_pairing==null)return;
            // Known only after mutual J-PAKE confirmation; kept for the whole session.
            if(_mode==LanWorkerMode.RelayJoin && _roomName.Length==0)_roomName=_pairing.ConfirmedRoomName;
            if(_pairing.PendingApproval && _request==0) {
                if(!IPAddress.TryParse(_pairing.PendingPeer,out _pendingAddress) || !LanNetworkPolicy.PrivatePeer(_pendingAddress) ||
                    LanModes.IsRelay(_mode) && !_pendingAddress.Equals(LanRelayText.VirtualPeer))throw new InvalidDataException("Pairing peer");
                var nonce=LanPake.Nonce();_request=BitConverter.ToUInt64(nonce,0);if(_request==0)_request=1;LanPake.Clear(nonce);
            }
            byte[] secret;IPAddress peer;
            if(_pairing.TryTakeCredentials(out secret,out peer)) {
                try {
                    if(LanModes.IsHost(_mode) && (!_approved || _request==0 || !peer.Equals(_pendingAddress)))throw new InvalidDataException("Unapproved admission");
                    _pairedAddress=peer;
                    if(LanModes.IsRelay(_mode)) {
                        if(!peer.Equals(LanRelayText.VirtualPeer))throw new InvalidDataException("Relay peer");
                        _transport=LanSecureTransport.OverRelay(_mode==LanWorkerMode.RelayHost,_relayLink.CreateDatagramLink(),_gameVersion,secret);
                    }
                    else _transport=_mode==LanWorkerMode.Host?LanSecureTransport.HostWithKey(LanDiscovery.GamePort,peer,_gameVersion,secret):LanSecureTransport.JoinWithKey(LanDiscovery.GamePort,peer,_gameVersion,secret);
                    _pairing.TransportReady();
                } finally { LanPake.Clear(secret); }
            }
            if(_pairing.Finished) {
                if(!_pairing.Succeeded && _pairing.Failure!=LanFault.None)
                    throw new LanFaultException(_pairing.Failure);
                if(!_pairing.Succeeded) { _transport?.Dispose();_transport=null;_ended=_rejected?LanWorkerState.Rejected:_pairing.Expired?LanWorkerState.Expired:LanWorkerState.Failed; }
                _pairing.Dispose();_pairing=null;_relayPairing=null;
            }
        }
        private void PublishStatus()
        {
            var s=new LanWorkerStatus {Request=_request,PendingAddress=_pendingAddress,RoomName=_roomName};
            if(LanModes.IsBrowse(_mode))s.State=_roomsSent?LanWorkerState.BrowseComplete:LanWorkerState.Browsing;
            else if(_transport!=null) {
                ulong session=_transport.Session;IPEndPoint peer=_transport.Peer;
                if(session!=_transport.Session || session!=0 && peer==null)return;
                s.Session=session;s.PeerAddress=session==0?_pairedAddress:peer.Address;s.PeerPort=session==0?0:peer.Port;
                s.State=session==0?LanWorkerState.Connecting:LanWorkerState.Active;
            } else if(_pairing!=null) {
                string code=_pairing.Code;bool open=_pairing.InvitationOpen;
                if(LanModes.IsHost(_mode)) {
                    // The host's own password is never echoed back to the game.
                    s.Code=_pairing.CustomSecret?"":code;s.CustomSecret=_pairing.CustomSecret;
                    s.Seconds=(byte)Math.Min(LanRelayText.PinSeconds,_pairing.SecondsLeft);
                    if(_relayPairing!=null && open)s.RelayRoom=_relayPairing.Room;
                }
                s.State=_pairing.PendingApproval?LanWorkerState.PendingApproval:LanModes.IsHost(_mode) && open?LanWorkerState.Invitation:
                    LanModes.IsJoin(_mode) && code.Length==0?LanWorkerState.WaitingApproval:LanWorkerState.Pairing;
            } else s.State=_ended;
            var bytes=LanIpc.StatusBytes(s);bool changed=_previousStatus==null || !Equal(bytes,_previousStatus);
            if(changed || _time.Elapsed.TotalSeconds>=_nextStatus) { _previousStatus=(byte[])bytes.Clone();Emit(LanIpcKind.Status,bytes);_publishedSession=s.Session;_nextStatus=_time.Elapsed.TotalSeconds+.2; }
        }
        private static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private void Emit(LanIpcKind kind,byte[] payload) { try{LanIpc.Write(_output,ref _writeSequence,new LanIpcFrame {Kind=kind,Context=_context,Payload=payload});}finally{Array.Clear(payload,0,payload.Length);} }
        public void Dispose()
        {
            _stopping=true;_transport?.Dispose();_pairing?.Dispose();_relayLink?.Dispose();_advertisement?.Dispose();_browser?.Dispose();_relayBrowser?.Dispose();
            lock(_gate){while(_commands.Count!=0){var frame=_commands.Dequeue();Array.Clear(frame.Payload,0,frame.Payload.Length);}}
        }
    }
}
