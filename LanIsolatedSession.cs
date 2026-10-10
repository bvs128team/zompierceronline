using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;

namespace ZompiercerLAN
{
    // No sockets or crypto keys in the Unity process. Anonymous inherited IPC only.
    internal sealed class LanIsolatedSession : IDisposable
    {
        private readonly object _gate=new object();
        private readonly Stopwatch _time=Stopwatch.StartNew();
        private readonly Queue<LanIpcFrame> _outgoing=new Queue<LanIpcFrame>(),_controls=new Queue<LanIpcFrame>();
        private readonly LanBrokerState _state;
        private readonly LanWorkerMode _mode;
        private readonly LanSandboxProcess _process;
        private readonly Thread _reader,_writer,_watchdog;
        private volatile bool _stopping;
        private string _failure;
        private long _lastStatus,_lastWrite;
        private ulong _readSequence,_writeSequence;
        private bool _readyTaken;
        internal bool PendingApproval { get { lock(_gate)return !_stopping && _state.PendingApproval; } }
        internal string PendingPeer { get { lock(_gate)return _state.Snapshot.PendingAddress?.ToString()??""; } }
        internal string Code { get { lock(_gate)return _stopping?"":_state.Snapshot.Code; } }
        internal string RelayRoom { get { lock(_gate)return _stopping?"":_state.Snapshot.RelayRoom; } }
        internal string RoomName { get { lock(_gate)return _state.Snapshot.RoomName; } }
        internal bool CustomSecret { get { lock(_gate)return !_stopping && _state.Snapshot.CustomSecret; } }
        internal bool InvitationOpen { get { lock(_gate)return !_stopping && _state.Snapshot.State==LanWorkerState.Invitation; } }
        internal bool Relay { get { return LanModes.IsRelay(_mode); } }
        internal int SecondsLeft { get { lock(_gate)return _stopping?0:_state.Snapshot.Seconds; } }
        internal ulong Session { get { lock(_gate)return _stopping?0:_state.Snapshot.Session; } }
        internal IPEndPoint Peer { get { lock(_gate)return _stopping?null:_state.Peer; } }
        internal bool Available { get { lock(_gate)return !_stopping && _state.QueuedPackets!=0; } }
        internal string SecurityFailure { get { lock(_gate)return _failure; } }
        internal bool Finished { get { lock(_gate)return _stopping || LanModes.IsBrowse(_mode) && _state.BrowserFinished || _state.Snapshot.State==LanWorkerState.Expired || _state.Snapshot.State==LanWorkerState.Rejected || _state.Snapshot.State==LanWorkerState.Failed; } }
        internal LanRoomInfo[] Rooms { get { lock(_gate)return (LanRoomInfo[])_state.Rooms.Clone(); } }
        internal string Status { get { lock(_gate) {
            if(_failure!=null)return _failure;
            switch(_state.Snapshot.State) {
                case LanWorkerState.Invitation:return Relay?(_state.Snapshot.CustomSecret?"Комната на сервере готова; передайте другу код комнаты и ваш пароль":"Комната на сервере готова; передайте другу код комнаты и PIN"):"Код готов; передайте другу в течение " + LanLocalText.PinSeconds + " секунд";
                case LanWorkerState.Pairing:return Relay?(LanModes.IsHost(_mode)?"Подключение к серверу…":"Подключение к серверу и проверка пароля комнаты…"):"Проверка кода и комнаты";
                case LanWorkerState.PendingApproval:return Relay?"Друг подтвердил пароль комнаты. Разрешите подключение":"Код подтверждён. Разрешите подключение";
                case LanWorkerState.WaitingApproval:return _state.Snapshot.RoomName.Length!=0?"Комната «"+_state.Snapshot.RoomName+"» подтвердила пароль. Ожидание разрешения хоста":"Код подтверждён. Ожидание разрешения хоста";
                case LanWorkerState.Connecting:return "Устанавливается защищённая связь";
                case LanWorkerState.Active:return _state.Snapshot.RoomName.Length!=0?"Защищённый транспорт готов: комната «"+_state.Snapshot.RoomName+"»":"Защищённый транспорт готов";
                case LanWorkerState.Expired:return Relay?"PIN истёк. Создайте новую комнату":"Код истёк. Создайте новый код";
                case LanWorkerState.Rejected:return "Подключение отклонено; нужен новый код";
                case LanWorkerState.Failed:return Relay?"Подключение не завершено; создайте новую комнату":"Подключение не завершено; проверьте сеть и создайте новый код";
                case LanWorkerState.Browsing:return "Поиск комнат…";
                case LanWorkerState.BrowseComplete:
                    if(_mode==LanWorkerMode.RelayBrowse)return _state.Rooms.Length==0?"Открытых комнат сейчас нет":"Выберите комнату и введите пароль, который сообщил хост";
                    return _state.Rooms.Length==0?"Комнаты не найдены; обновите список или укажите IP":"Выберите комнату и введите свежий код хоста";
                default:return "Запускается защищённый сетевой процесс";
            }
        } } }
        internal static LanIsolatedSession Host(string name,string version) { return new LanIsolatedSession(LanWorkerMode.Host,version,LanLocalText.CleanName(name),"",null); }
        internal static LanIsolatedSession Join(string pin,IPAddress address,string version) {
            if(!LanLocalText.ValidPin(pin) || address!=null && !LanNetworkPolicy.PrivatePeer(address))throw new ArgumentException("Нужны шесть цифр и адрес локальной сети");
            return new LanIsolatedSession(LanWorkerMode.Join,version,"Комната",pin,address);
        }
        internal static LanIsolatedSession Browse() { return new LanIsolatedSession(LanWorkerMode.Browse,"","Комната","",null); }
        // secret: empty for a random PIN, otherwise the host's own room password.
        internal static LanIsolatedSession RelayHost(LanRelayOptions relay,string version,string name="Комната",string secret="")
        {
            secret=secret??"";
            if(relay==null || relay.Room.Length!=0 || secret.Length!=0 && !LanRelayText.ValidSecret(secret) ||
                relay.PublicName.Length!=0 && !LanRelayText.StrongPublicSecret(secret))throw new ArgumentException("Настройки сервера или пароль комнаты");
            return new LanIsolatedSession(LanWorkerMode.RelayHost,version,LanLocalText.CleanName(name),secret,null,relay);
        }
        // The relay's public room list (0.13.0): rooms of this mod version open for a guest.
        internal static LanIsolatedSession RelayBrowse(LanRelayOptions relay)
        {
            if(relay==null || relay.Room.Length!=0 || relay.PublicName.Length!=0)throw new ArgumentException("Настройки сервера");
            return new LanIsolatedSession(LanWorkerMode.RelayBrowse,"","Комната","",null,relay);
        }
        internal static LanIsolatedSession RelayJoin(string secret,LanRelayOptions relay,string version)
        {
            if(!LanRelayText.ValidSecret(secret) || relay==null || LanRelayText.NormalizeRoom(relay.Room)!=relay.Room)throw new ArgumentException("Нужны код комнаты сервера и PIN или пароль комнаты");
            return new LanIsolatedSession(LanWorkerMode.RelayJoin,version,"Комната",secret,null,relay);
        }
        private LanIsolatedSession(LanWorkerMode mode,string version,string name,string pin,IPAddress address,LanRelayOptions relay=null)
        {
            // A relay joiner may only ever be paired with the virtual relay peer.
            _mode=mode;_state=new LanBrokerState(mode,version,mode==LanWorkerMode.RelayJoin?LanRelayText.VirtualPeer:address);
            var start=LanIpc.Encode(w=>{w.Write((byte)mode);LanIpc.WriteText(w,version,40);LanIpc.WriteText(w,name,20);LanIpc.WriteText(w,pin,LanRelayText.MaxSecretBytes);LanIpc.WriteAddress(w,address);
                if(LanModes.IsInternet(mode))relay.Write(w);});
            try {
                string directory=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"ZompiercerLAN.Network");
                _process=LanSandboxProcess.Start(directory,LanWorkerIntegrity.Executable,LanWorkerIntegrity.Crypto,LanWorkerIntegrity.Configuration,LanModes.IsInternet(mode));
                _controls.Enqueue(new LanIpcFrame {Kind=LanIpcKind.Start,Context=1,Payload=start});start=null;
                _reader=new Thread(Read) {IsBackground=true,Name="LAN isolated IPC reader"};
                _writer=new Thread(Write) {IsBackground=true,Name="LAN isolated IPC writer"};
                _watchdog=new Thread(Watch) {IsBackground=true,Name="LAN isolated watchdog"};
                _reader.Start();_writer.Start();_watchdog.Start();
            } catch { _process?.Dispose();throw; }
            finally { if(start!=null)Array.Clear(start,0,start.Length); }
        }
        internal bool TryTakeReady(out IPAddress peer)
        { lock(_gate){peer=null;if(_stopping || _readyTaken || !_state.TransportReady)return false;_readyTaken=true;peer=_state.Snapshot.PeerAddress;return true;} }
        internal void Approve() { lock(_gate){if(_stopping || !_state.PendingApproval)return;Control(LanIpcKind.Approve,_state.Approve());} }
        internal void Reject() { lock(_gate){if(_stopping)return;_state.Reject();Control(LanIpcKind.Reject,new byte[0]);} }
        internal void Restart()
        { lock(_gate){if(_stopping || !_state.TransportReady)return;_state.Restart();ClearQueue(_outgoing);ClearQueue(_controls);Control(LanIpcKind.Restart,new byte[0]);} }
        internal void RetryAfterBusy() { Restart(); }
        internal void Tick() { /* Watchdog and bounded validation run off the Unity thread. */ }
        internal bool TryReceive(out IPEndPoint peer,out Packet packet)
        { lock(_gate){peer=null;packet=null;return !_stopping && _state.TryReceive(out peer,out packet);} }
        internal void Send(Packet packet)
        {
            lock(_gate){if(_stopping || !_state.AllowSend(packet))return;var bytes=LanProtocol.Encode(packet);Packet checkedPacket;
                if(bytes.Length>1200 || !LanProtocol.TryDecode(bytes,out checkedPacket)){Array.Clear(bytes,0,bytes.Length);return;}
                bool control=packet.Kind==PacketKind.Welcome || packet.Kind==PacketKind.VersionMismatch;
                var queue=control?_controls:_outgoing;if(queue.Count>=(control?8:128)){Array.Clear(bytes,0,bytes.Length);return;}
                queue.Enqueue(new LanIpcFrame {Kind=LanIpcKind.Send,Context=_state.Context,Payload=bytes});}
        }
        // 1.4.5: waits (at most `milliseconds`) until what was sent reached the network process, and a
        // moment more for it to go out, before the session is closed.
        internal void Flush(int milliseconds)
        {
            double until=_time.Elapsed.TotalMilliseconds+milliseconds;
            while(_time.Elapsed.TotalMilliseconds<until){lock(_gate){if(_stopping || _outgoing.Count==0 && _controls.Count==0)break;}Thread.Sleep(5);}
            Thread.Sleep(Math.Max(0,Math.Min(100,(int)(until-_time.Elapsed.TotalMilliseconds))));
        }
        private void Control(LanIpcKind kind,byte[] payload)
        {
            if(_controls.Count>=8){Array.Clear(payload,0,payload.Length);throw new IOException("Очередь управления сетевым процессом заполнена");}
            _controls.Enqueue(new LanIpcFrame {Kind=kind,Context=_state.Context,Payload=payload});
        }
        private void Read()
        {
            try {while(!_stopping){var frame=LanIpc.Read(_process.Output,ref _readSequence);try{lock(_gate){if(_stopping)return;_state.Apply(frame,_time.Elapsed.TotalSeconds);if(frame.Kind==LanIpcKind.Status || frame.Kind==LanIpcKind.Boot)Interlocked.Exchange(ref _lastStatus,_time.ElapsedTicks);}}finally{Array.Clear(frame.Payload,0,frame.Payload.Length);}}}
            catch(LanFaultException ex){Fail(LanFaultException.MessageFor(ex.Fault));}
            catch(Exception ex){Fail(ex is EndOfStreamException?"Сетевой процесс завершился; подключение закрыто":ex is InvalidDataException?"Сетевой процесс прислал недопустимые данные; подключение закрыто":"Ошибка защищённого сетевого процесса; подключение закрыто");}
        }
        private void Write()
        {
            double nextPing=1;
            try {while(!_stopping){LanIpcFrame frame=null;lock(_gate){if(_controls.Count!=0)frame=_controls.Dequeue();else if(_outgoing.Count!=0)frame=_outgoing.Dequeue();else if(_time.Elapsed.TotalSeconds>=nextPing){frame=new LanIpcFrame {Kind=LanIpcKind.Ping,Context=_state.Context,Payload=new byte[0]};nextPing=_time.Elapsed.TotalSeconds+1;}}
                if(frame==null){Thread.Sleep(5);continue;}try{LanIpc.Write(_process.Input,ref _writeSequence,frame);Interlocked.Exchange(ref _lastWrite,_time.ElapsedTicks);}finally{Array.Clear(frame.Payload,0,frame.Payload.Length);}}
            }catch{DrainThenFail("Обмен с сетевым процессом остановлен; подключение закрыто");}
        }
        private void Watch()
        {
            try {while(!_stopping){if(_process.HasExited){DrainThenFail("Сетевой процесс недоступен; подключение закрыто");return;}if((_time.ElapsedTicks-Interlocked.Read(ref _lastStatus))/(double)Stopwatch.Frequency>10 ||
                    (_time.ElapsedTicks-Interlocked.Read(ref _lastWrite))/(double)Stopwatch.Frequency>10){Fail("Сетевой процесс недоступен; подключение закрыто");return;}Thread.Sleep(100);}}
            catch{Fail("Проверка сетевого процесса завершилась ошибкой");}
        }
        // A worker can exit immediately after writing its terminal fault. Let the
        // sole reader consume the bounded pipe before a competing monitor kills it.
        private void DrainThenFail(string reason)
        { if(_reader!=null && Thread.CurrentThread!=_reader)_reader.Join(500);Fail(reason); }
        private void Fail(string reason)
        { lock(_gate){if(_stopping)return;_failure=reason;_stopping=true;_state.ClearPackets();ClearQueue(_outgoing);ClearQueue(_controls);}try{_process.Kill();}catch{} }
        private static void ClearQueue(Queue<LanIpcFrame> queue){while(queue.Count!=0){var frame=queue.Dequeue();Array.Clear(frame.Payload,0,frame.Payload.Length);}}
        public void Dispose()
        {
            lock(_gate){_stopping=true;_state.ClearPackets();ClearQueue(_outgoing);ClearQueue(_controls);}
            _process.Dispose();
            foreach(var thread in new[]{_reader,_writer,_watchdog})if(thread!=null && thread!=Thread.CurrentThread)thread.Join(200);
        }
    }
}
