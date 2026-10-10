using System;
using System.IO;
using System.Net;
using System.Text;

namespace ZompiercerLAN
{
    // Explicit bounded data only. No paths, URLs, type names or executable commands.
    // Inherited anonymous handles bind this channel to the launched child, not TCP.
    internal enum LanIpcKind : byte { Start=1, Approve=2, Reject=3, Restart=4, Send=5, Ping=6, Stop=7,
        Boot=128, Status=129, Packet=130, Rooms=131, Fault=132 }
    // Relay modes reach one configured internet relay instead of the LAN. Each
    // mode gets exactly one AppContainer network capability (never both).
    // RelayBrowse (0.13.0): read the relay's public room list, then finish.
    internal enum LanWorkerMode : byte { Browse=1, Host=2, Join=3, RelayHost=4, RelayJoin=5, RelayBrowse=6 }
    internal static class LanModes
    {
        internal static bool Valid(LanWorkerMode mode) { return mode>=LanWorkerMode.Browse && mode<=LanWorkerMode.RelayBrowse; }
        internal static bool IsBrowse(LanWorkerMode mode) { return mode==LanWorkerMode.Browse || mode==LanWorkerMode.RelayBrowse; }
        // The worker gets the internet capability (and never the LAN one).
        internal static bool IsInternet(LanWorkerMode mode) { return IsRelay(mode) || mode==LanWorkerMode.RelayBrowse; }
        internal static bool IsHost(LanWorkerMode mode) { return mode==LanWorkerMode.Host || mode==LanWorkerMode.RelayHost; }
        internal static bool IsJoin(LanWorkerMode mode) { return mode==LanWorkerMode.Join || mode==LanWorkerMode.RelayJoin; }
        internal static bool IsRelay(LanWorkerMode mode) { return mode==LanWorkerMode.RelayHost || mode==LanWorkerMode.RelayJoin; }
    }
    // Local diagnostic vocabulary only: never transmit exception text from the worker.
    internal enum LanFault : byte { None=0, Security=1, Runtime=2, SocketAccessDenied=3,
        NoLanAdapter=4, NoHostResponse=5, ConnectionRefused=6, DiscoverySendFailed=7,
        PairingProofFailed=8, PairingExchangeFailed=9, NoRooms=10,
        RelayUnreachable=11, RelayCertificate=12, RelayAccessDenied=13, RelayRoomNotFound=14,
        RelayRoomBusy=15, RelayVersion=16, RelayLimited=17, RelayClosed=18, RelayOutdated=19, RelayNoPublic=20 }
    internal sealed class LanFaultException : IOException
    {
        internal readonly LanFault Fault;
        internal LanFaultException(LanFault fault) : base(MessageFor(fault)) { Fault=fault; }
        internal static string MessageFor(LanFault fault)
        {
            switch(fault) {
                case LanFault.Security:return "Участник нарушил правила защищённого соединения";
                case LanFault.Runtime:return "Сетевой процесс завершил обмен с ошибкой";
                case LanFault.SocketAccessDenied:return "Windows запретила сетевую операцию. Проверьте разрешение LAN и брандмауэр";
                case LanFault.NoLanAdapter:return "Не найден доступный адаптер с адресом локальной IPv4-сети";
                case LanFault.NoHostResponse:return "Хост не ответил вовремя. Проверьте IP, сеть и активный код комнаты";
                case LanFault.ConnectionRefused:return "Хост отклонил TCP-подключение. Создайте новый код на хосте и повторите";
                case LanFault.DiscoverySendFailed:return "Не удалось отправить запросы поиска LAN. Проверьте сеть или укажите IP";
                case LanFault.PairingProofFailed:return "Криптографическое подтверждение кода не прошло. Получите новый код хоста";
                case LanFault.PairingExchangeFailed:return "Обмен с комнатой прерван или не прошёл проверку. Получите новый код хоста";
                case LanFault.NoRooms:return "Комната с активным кодом не найдена. Обновите код или укажите IP";
                case LanFault.RelayUnreachable:return "Сервер не отвечает. Проверьте адрес сервера и интернет";
                case LanFault.RelayCertificate:return "Сертификат сервера не совпадает с отпечатком. Проверьте отпечаток; возможна подмена сервера";
                case LanFault.RelayAccessDenied:return "Сервер отказал в доступе: неверный ключ сервера или слишком много попыток";
                case LanFault.RelayRoomNotFound:return "Комната на сервере не найдена. Проверьте код комнаты";
                case LanFault.RelayRoomBusy:return "Комната на сервере закрыта для входа или занята. Попросите хоста создать новую";
                case LanFault.RelayVersion:return "У хоста другая версия мода. Установите обоим одну и ту же версию";
                case LanFault.RelayOutdated:return "Сервер больше не принимает эту версию мода. Скачайте новую версию ZompiercerLAN";
                case LanFault.RelayLimited:return "Сервер временно ограничил подключения. Подождите минуту";
                case LanFault.RelayClosed:return "Сервер закрыл комнату или связь с ним потеряна";
                case LanFault.RelayNoPublic:return "Сервер не взял комнату в общий список: с вашего адреса уже открыто две такие комнаты или список выключен";
                default:throw new InvalidDataException("IPC fault format");
            }
        }
        internal static LanFaultException Read(byte[] payload)
        {
            if(payload==null || payload.Length!=1)throw new InvalidDataException("IPC fault format");
            return new LanFaultException((LanFault)payload[0]);
        }
    }
    internal enum LanWorkerState : byte { Starting=0, Invitation=1, Pairing=2, PendingApproval=3, WaitingApproval=4,
        Connecting=5, Active=6, Expired=7, Rejected=8, Failed=9, Browsing=10, BrowseComplete=11 }
    internal sealed class LanIpcFrame { internal LanIpcKind Kind;internal uint Context;internal byte[] Payload; }
    internal sealed class LanWorkerStatus
    {
        internal LanWorkerState State;
        internal string Code="";
        internal string RelayRoom=""; // relay host only: the server's public room id
        internal string RoomName="";  // relay join only: host's room name, authenticated by J-PAKE
        internal bool CustomSecret;   // relay host only: the host chose the password (never echoed)
        internal byte Seconds;
        internal ulong Request,Session;
        internal IPAddress PendingAddress,PeerAddress;
        internal int PeerPort;
        internal bool TransportReady { get { return State==LanWorkerState.Connecting || State==LanWorkerState.Active; } }
    }
    // LAN: a room seen on the local network (Flags 0..2). Relay (Flags 3, 0.13.0): a room of the
    // relay's public list; Address is the virtual peer and Room holds the 8-symbol room id.
    internal sealed class LanRoomInfo
    {
        internal const byte RelayFlag=3;
        internal IPAddress Address;internal byte[] Room;internal string Name;internal byte Flags;
        internal bool PinActive { get { return Flags==1; } }internal bool Busy { get { return Flags==2; } }
        internal string RelayRoom { get { return Flags==RelayFlag?Encoding.ASCII.GetString(Room,0,8):null; } }
        internal static LanRoomInfo ForRelay(string room,string name)
        { var bytes=new byte[16];Encoding.ASCII.GetBytes(room,0,8,bytes,0);return new LanRoomInfo {Address=LanRelayText.VirtualPeer,Room=bytes,Name=name,Flags=RelayFlag}; }
    }
    internal static class LanIpc
    {
        internal const int Maximum=2048,MaxRooms=24;
        private const uint Magic=0x3949505A;
        private static readonly UTF8Encoding Text=new UTF8Encoding(false,true);
        internal static LanIpcFrame Read(Stream stream,ref ulong previous)
        {
            byte[] header=Exact(stream,20);int size=BitConverter.ToUInt16(header,6);ulong sequence=BitConverter.ToUInt64(header,8);
            if(BitConverter.ToUInt32(header,0)!=Magic || header[4]!=1 || size>Maximum || previous==ulong.MaxValue || sequence!=previous+1)
                throw new InvalidDataException("IPC header");
            byte kind=header[5];if(!(kind>=1 && kind<=7 || kind>=128 && kind<=132))throw new InvalidDataException("IPC kind");
            uint context=BitConverter.ToUInt32(header,16);if(context==0)throw new InvalidDataException("IPC context");
            var payload=Exact(stream,size);previous=sequence;
            return new LanIpcFrame {Kind=(LanIpcKind)kind,Context=context,Payload=payload};
        }
        internal static void Write(Stream stream,ref ulong previous,LanIpcFrame frame)
        {
            if(frame==null || frame.Payload==null || frame.Payload.Length>Maximum || frame.Context==0 || previous==ulong.MaxValue)throw new InvalidDataException("IPC output");
            byte[] bytes=new byte[20+frame.Payload.Length];Buffer.BlockCopy(BitConverter.GetBytes(Magic),0,bytes,0,4);bytes[4]=1;bytes[5]=(byte)frame.Kind;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)frame.Payload.Length),0,bytes,6,2);Buffer.BlockCopy(BitConverter.GetBytes(++previous),0,bytes,8,8);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.Context),0,bytes,16,4);Buffer.BlockCopy(frame.Payload,0,bytes,20,frame.Payload.Length);
            try { stream.Write(bytes,0,bytes.Length);stream.Flush(); } finally { Array.Clear(bytes,0,bytes.Length); }
        }
        internal static byte[] Encode(Action<BinaryWriter> write)
        { using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Text)) { write(writer);writer.Flush();if(stream.Length>Maximum)throw new InvalidDataException("IPC payload");return stream.ToArray(); } }
        internal static void Decode(byte[] bytes,Action<BinaryReader> read)
        {
            if(bytes==null || bytes.Length>Maximum)throw new InvalidDataException("IPC payload");
            using(var stream=new MemoryStream(bytes,false))using(var reader=new BinaryReader(stream,Text)) { read(reader);if(stream.Position!=stream.Length)throw new InvalidDataException("IPC trailing bytes"); }
        }
        internal static void WriteText(BinaryWriter writer,string value,int maximum)
        { var bytes=Text.GetBytes(value??"");if(bytes.Length>maximum)throw new InvalidDataException("IPC text");writer.Write((ushort)bytes.Length);writer.Write(bytes); }
        internal static string ReadText(BinaryReader reader,int maximum)
        { int size=reader.ReadUInt16();if(size>maximum)throw new InvalidDataException("IPC text");return Text.GetString(ReadBytes(reader,size)); }
        internal static void WriteAddress(BinaryWriter writer,IPAddress address)
        { if(address!=null && !LanNetworkPolicy.PrivatePeer(address))throw new InvalidDataException("IPC address");writer.Write(address==null?new byte[4]:address.GetAddressBytes()); }
        internal static IPAddress ReadAddress(BinaryReader reader)
        { var bytes=ReadBytes(reader,4);if((bytes[0]|bytes[1]|bytes[2]|bytes[3])==0)return null;var address=new IPAddress(bytes);if(!LanNetworkPolicy.PrivatePeer(address))throw new InvalidDataException("IPC address");return address; }
        internal static byte[] ReadBytes(BinaryReader reader,int count)
        { var bytes=reader.ReadBytes(count);if(bytes.Length!=count)throw new EndOfStreamException();return bytes; }
        internal static byte[] StatusBytes(LanWorkerStatus s)
        { return Encode(w=>{w.Write((byte)s.State);WriteText(w,s.Code,6);WriteText(w,s.RelayRoom,8);WriteText(w,s.RoomName,20);w.Write((byte)(s.CustomSecret?1:0));w.Write(s.Seconds);w.Write(s.Request);WriteAddress(w,s.PendingAddress);w.Write(s.Session);WriteAddress(w,s.PeerAddress);w.Write((ushort)s.PeerPort);}); }
        internal static LanWorkerStatus ReadStatus(byte[] bytes)
        {
            byte flags=0;
            var s=new LanWorkerStatus();Decode(bytes,r=>{s.State=(LanWorkerState)r.ReadByte();s.Code=ReadText(r,6);s.RelayRoom=ReadText(r,8);s.RoomName=ReadText(r,20);flags=r.ReadByte();s.Seconds=r.ReadByte();s.Request=r.ReadUInt64();s.PendingAddress=ReadAddress(r);s.Session=r.ReadUInt64();s.PeerAddress=ReadAddress(r);s.PeerPort=r.ReadUInt16();});
            s.CustomSecret=flags==1;
            if(flags>1 || s.CustomSecret && s.Code.Length!=0 || s.RoomName.Length!=0 && LanLocalText.CleanName(s.RoomName)!=s.RoomName)throw new InvalidDataException("IPC status fields");
            if(s.State>LanWorkerState.BrowseComplete || s.Seconds>LanRelayText.PinSeconds || s.Code.Length!=0 && !LanLocalText.ValidPin(s.Code) ||
                s.RelayRoom.Length!=0 && LanRelayText.NormalizeRoom(s.RelayRoom)!=s.RelayRoom ||
                s.State==LanWorkerState.PendingApproval && (s.Request==0 || s.PendingAddress==null) ||
                s.State==LanWorkerState.Active && (s.Session==0 || s.PeerAddress==null || s.PeerPort<1024) ||
                s.State!=LanWorkerState.Active && s.Session!=0 || s.TransportReady && s.PeerAddress==null ||
                s.PeerPort!=0 && s.PeerPort<1024)throw new InvalidDataException("IPC status fields");return s;
        }
        internal static byte[] RoomsBytes(LanRoomInfo[] rooms)
        {
            if(rooms==null || rooms.Length>MaxRooms)throw new InvalidDataException("IPC rooms");
            return Encode(w=>{w.Write((byte)rooms.Length);foreach(var room in rooms){WriteAddress(w,room.Address);if(room.Room==null || room.Room.Length!=16)throw new InvalidDataException("IPC room id");w.Write(room.Room);WriteText(w,room.Name,20);w.Write(room.Flags);}});
        }
        internal static LanRoomInfo[] ReadRooms(byte[] bytes)
        {
            LanRoomInfo[] rooms=null;Decode(bytes,r=>{int count=r.ReadByte();if(count>MaxRooms)throw new InvalidDataException("IPC room count");rooms=new LanRoomInfo[count];
                for(int i=0;i<count;i++){var room=new LanRoomInfo {Address=ReadAddress(r),Room=ReadBytes(r,16),Name=ReadText(r,20),Flags=r.ReadByte()};
                    if(room.Address==null || room.Flags>LanRoomInfo.RelayFlag || LanLocalText.CleanName(room.Name)!=room.Name)throw new InvalidDataException("IPC room fields");
                    if(room.Flags==LanRoomInfo.RelayFlag && !RelayRoomBytes(room))throw new InvalidDataException("IPC relay room");rooms[i]=room;}});return rooms;
        }
        // A relay room: the virtual peer, a canonical room id, zero padding, a public name.
        private static bool RelayRoomBytes(LanRoomInfo room)
        {
            if(!room.Address.Equals(LanRelayText.VirtualPeer) || LanRelayText.PublicName(room.Name)!=room.Name)return false;
            for(int i=8;i<16;i++)if(room.Room[i]!=0)return false;
            for(int i=0;i<8;i++)if(LanRelayText.RoomAlphabet.IndexOf((char)room.Room[i])<0)return false;
            return true;
        }
        private static byte[] Exact(Stream stream,int count)
        { var bytes=new byte[count];int offset=0;while(offset<count){int read=stream.Read(bytes,offset,count-offset);if(read<=0)throw new EndOfStreamException();offset+=read;}return bytes; }
    }
    // Relay settings chosen by the local player. The worker accepts no relay
    // address from the network: only this one, validated on both sides.
    internal sealed class LanRelayOptions
    {
        internal IPAddress Server;
        internal int Port=LanRelayText.DefaultPort;
        internal byte[] Fingerprint;  // SHA-256 of the server certificate (pinned)
        internal string AccessKey="";
        internal string Room="";      // relay join only: canonical 8-symbol room id
        internal string PublicName="";// relay host only (0.13.0): list the room under this name; empty = private
        internal void Write(BinaryWriter w)
        {
            if(!LanRelayText.ValidServer(Server) || Port<1 || Port>65535 || Fingerprint==null || Fingerprint.Length!=32 ||
                PublicName.Length!=0 && LanRelayText.PublicName(PublicName)!=PublicName)throw new InvalidDataException("IPC relay options");
            w.Write(Server.GetAddressBytes());w.Write((ushort)Port);w.Write(Fingerprint);LanIpc.WriteText(w,AccessKey,64);LanIpc.WriteText(w,Room,8);LanIpc.WriteText(w,PublicName,20);
        }
        internal static LanRelayOptions Read(BinaryReader r)
        {
            var o=new LanRelayOptions {Server=new IPAddress(LanIpc.ReadBytes(r,4)),Port=r.ReadUInt16(),Fingerprint=LanIpc.ReadBytes(r,32),AccessKey=LanIpc.ReadText(r,64),Room=LanIpc.ReadText(r,8),PublicName=LanIpc.ReadText(r,20)};
            if(!LanRelayText.ValidServer(o.Server) || o.Port==0 || o.Room.Length!=0 && LanRelayText.NormalizeRoom(o.Room)!=o.Room ||
                o.PublicName.Length!=0 && (o.Room.Length!=0 || LanRelayText.PublicName(o.PublicName)!=o.PublicName))throw new InvalidDataException("IPC relay options");
            return o;
        }
    }
    internal static class LanRelayText
    {
        internal const int DefaultPort=27780;
        // Over the internet the PIN travels by messenger together with the room id,
        // so the LAN's 30 seconds are impractical. J-PAKE still allows at most three online
        // guesses per invitation (1 in 333 333), and the PIN dies on first use.
        internal const int PinSeconds=120;
        // The relay hop is a virtual link: the game sees this fixed loopback peer
        // and never learns or handles the friend's real address.
        internal static readonly IPAddress VirtualPeer=IPAddress.Loopback;
        internal const int VirtualPort=27777;
        internal const string RoomAlphabet="0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        // Internet relays only: an internetClient AppContainer cannot reach private
        // ranges, and loopback is reserved for the virtual peer.
#if SECURITY_CHECKS
        // Checks run a real relay on 127.0.0.1. This bypass is absent from the shipping assembly.
        internal static bool AllowLoopbackForChecks;
#endif
        internal static bool ValidServer(IPAddress address)
        {
            if(address==null || address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return false;
#if SECURITY_CHECKS
            if(AllowLoopbackForChecks && IPAddress.IsLoopback(address))return true;
#endif
            var b=address.GetAddressBytes();
            return !(b[0]==0 || b[0]==10 || b[0]==127 || b[0]>=224 || b[0]==169 && b[1]==254 || b[0]==172 && b[1]>=16 && b[1]<=31 || b[0]==192 && b[1]==168);
        }
        internal static string NormalizeRoom(string input)
        {
            if(input==null)return null;
            var result=new StringBuilder(8);
            foreach(char raw in input) {
                if(raw=='-' || raw==' ')continue;
                char c=char.ToUpperInvariant(raw);
                if(c=='O')c='0';else if(c=='I' || c=='L')c='1';
                if(RoomAlphabet.IndexOf(c)<0 || result.Length==8)return null;
                result.Append(c);
            }
            return result.Length==8?result.ToString():null;
        }
        internal static string FormatRoom(string room) { return room!=null && room.Length==8?room.Substring(0,4)+"-"+room.Substring(4):room??""; }

        // Room secret for relay rooms: the random six-digit PIN or a password the
        // host chose. Both sides normalize the same way before J-PAKE.
        internal const int MinSecret=6, MaxSecret=64, MaxSecretBytes=256;
        internal static string NormalizeSecret(string value)
        {
            if(value==null)return "";
            try { return value.Trim().Normalize(NormalizationForm.FormC); }
            catch(ArgumentException) { return ""; } // invalid UTF-16 (lone surrogates)
        }
        internal static bool ValidSecret(string value)
        {
            if(value==null || value.Length<MinSecret || value.Length>MaxSecret || NormalizeSecret(value)!=value)return false;
            foreach(char c in value)if(char.IsControl(c))return false;
            try { return new UTF8Encoding(false,true).GetByteCount(value)<=MaxSecretBytes; }
            catch(ArgumentException) { return false; }
        }
        // J-PAKE allows only three online guesses per room, but a guessable
        // password ("123456", "qwerty12") still falls to those few guesses.
        internal static string SecretWarning(string value)
        {
            value=NormalizeSecret(value);
            if(value.Length==0)return null;
            if(value.Length<MinSecret)return "Пароль короче 6 символов не принимается";
            if(!ValidSecret(value))return "Пароль: от 6 до 64 символов, без управляющих символов";
            bool digits=true;var distinct=new System.Collections.Generic.HashSet<char>();bool ascending=true,descending=true;
            for(int i=0;i<value.Length;i++) {
                char c=char.ToLowerInvariant(value[i]);distinct.Add(c);
                if(!char.IsDigit(c))digits=false;
                if(i>0 && c!=char.ToLowerInvariant(value[i-1])+1)ascending=false;
                if(i>0 && c!=char.ToLowerInvariant(value[i-1])-1)descending=false;
            }
            if(distinct.Count<4 || ascending || descending)return "Слабый пароль: его легко угадать. Лучше случайный PIN или 10+ разных символов";
            if(value.Length<10 || digits)return "Короткий пароль: тот, кто узнает код комнаты, получит три попытки его угадать. Надёжнее 10+ символов или случайный PIN";
            return null;
        }
        // A room in the relay's public list (0.13.0): the room name as LanLocalText.CleanName makes
        // it, with single spaces (the relay refuses anything else), at most 20 UTF-8 bytes.
        internal static string PublicName(string value)
        {
            var clean=LanLocalText.CleanName(value);
            while(clean.Contains("  "))clean=clean.Replace("  "," ");
            return clean;
        }
        // A listed room is joined with the host's own password only (no PIN): anyone may try
        // it, so it must not be guessable in the few attempts J-PAKE gives per room.
        internal const int MinPublicSecret=8;
        internal static string PublicSecretProblem(string value)
        {
            value=NormalizeSecret(value);
            if(value.Length==0)return "Для общего списка придумайте свой пароль комнаты";
            if(value.Length<MinPublicSecret)return "Пароль для общего списка — не короче "+MinPublicSecret+" символов";
            if(!ValidSecret(value))return "Пароль: до 64 символов, без управляющих символов";
            bool digits=true,ascending=true,descending=true;var distinct=new System.Collections.Generic.HashSet<char>();
            for(int i=0;i<value.Length;i++) {
                char c=char.ToLowerInvariant(value[i]);distinct.Add(c);
                if(!char.IsDigit(c))digits=false;
                if(i>0 && c!=char.ToLowerInvariant(value[i-1])+1)ascending=false;
                if(i>0 && c!=char.ToLowerInvariant(value[i-1])-1)descending=false;
            }
            if(digits)return "Пароль из одних цифр слишком легко угадать: добавьте буквы";
            if(distinct.Count<5 || ascending || descending)return "Слишком простой пароль: его легко угадать";
            return null;
        }
        internal static bool StrongPublicSecret(string value) { return value!=null && NormalizeSecret(value)==value && PublicSecretProblem(value)==null; }
        // "AB:CD:..." as printed by the relay installer, or plain hex.
        internal static byte[] ParseFingerprint(string text)
        {
            if(text==null)return null;
            var hex=text.Replace(":","").Replace(" ","").Trim();
            if(hex.Length!=64)return null;
            var result=new byte[32];
            for(int i=0;i<32;i++) {
                int hi=Nibble(hex[2*i]),lo=Nibble(hex[2*i+1]);
                if(hi<0 || lo<0)return null;
                result[i]=(byte)(hi<<4|lo);
            }
            return result;
        }
        private static int Nibble(char c) { return c>='0' && c<='9'?c-'0':c>='a' && c<='f'?c-'a'+10:c>='A' && c<='F'?c-'A'+10:-1; }
    }
    internal static class LanLocalText
    {
        // 1.4.10: how long a LAN code lets a friend in (it was 10 s); still three tries per code.
        internal const int PinSeconds=30;
        internal static bool ValidPin(string value) { if(value==null || value.Length!=6)return false;foreach(char c in value)if(c<'0'||c>'9')return false;return true; }
        internal static string CleanName(string value)
        {
            var clean=new StringBuilder();foreach(char c in value??"")if(clean.Length<20 && (char.IsLetterOrDigit(c)||c==' '||c=='-'||c=='_'))clean.Append(c);
            string result=clean.ToString().Trim();while(Encoding.UTF8.GetByteCount(result)>20)result=result.Substring(0,result.Length-1);return result.Length==0?"Комната":result;
        }
    }
}
