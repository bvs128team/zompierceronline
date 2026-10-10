using System;
using System.Net;
using System.Net.Sockets;

namespace ZompiercerLAN
{
    // No endpoint, URL, path or executable supplied by an application packet can
    // create a new connection. These are the only peer address classes for LAN.
    internal static class LanNetworkPolicy
    {
        internal static bool PrivatePeer(IPAddress address)
        {
            if(address==null || address.AddressFamily!=AddressFamily.InterNetwork) return false;
            if(IPAddress.IsLoopback(address)) return true;
            var b=address.GetAddressBytes();
            return b[0]==10 || b[0]==172 && b[1]>=16 && b[1]<=31 || b[0]==192 && b[1]==168 ||
                b[0]==169 && b[1]==254 && b[2]>0 && b[2]<255;
        }
        internal static bool Allowed(bool receivingHost,PacketKind kind,bool established)
        {
            if(receivingHost ? kind==PacketKind.Hello : kind==PacketKind.Welcome || kind==PacketKind.VersionMismatch) return true;
            if(!established) return false;
            if(kind==PacketKind.Heartbeat || kind==PacketKind.State || kind==PacketKind.Mark || kind==PacketKind.Chat || kind==PacketKind.Revive || kind==PacketKind.Sleep || kind==PacketKind.Leave) return true;
            return receivingHost ? kind==PacketKind.TrainLayoutAck || kind==PacketKind.ZombieAction || kind==PacketKind.ZombieHurtAck ||
                kind==PacketKind.CombatProposal || kind==PacketKind.CombatReload ||
                kind==PacketKind.IdentityProof || kind==PacketKind.GuestStateChunk || kind==PacketKind.GuestRestoreAck ||
                kind==PacketKind.StorageRequest || kind==PacketKind.StorageViewAck || kind==PacketKind.DoorToggle || kind==PacketKind.SceneUse :
                kind==PacketKind.Busy || kind==PacketKind.WorldState || kind==PacketKind.TrainMotion || kind==PacketKind.TrainLayoutChunk ||
                kind==PacketKind.ZombieState || kind==PacketKind.ZombieHurt || kind==PacketKind.CombatState ||
                kind==PacketKind.IdentityRequest || kind==PacketKind.GuestStatus || kind==PacketKind.GuestStateAck || kind==PacketKind.GuestRestoreChunk ||
                kind==PacketKind.StorageResult || kind==PacketKind.StorageViewChunk || kind==PacketKind.ZombieCorpse || kind==PacketKind.DoorStates || kind==PacketKind.WorldItems ||
                kind==PacketKind.ZombieEffect || kind==PacketKind.QuestStates || kind==PacketKind.Explosion || kind==PacketKind.SceneStates;
        }
    }

    // Owned by one network worker. Reconnect never recreates/refills these buckets.
    internal sealed class LanRateLimit
    {
        private readonly double _rate,_capacity;
        private double _tokens,_last;
        internal LanRateLimit(double rate,double capacity)
        {
            if(!Finite(rate) || !Finite(capacity) || rate<=0 || capacity<=0) throw new ArgumentException("Invalid network budget");
            _rate=rate;_capacity=capacity;_tokens=capacity;
        }
        internal bool Take(double amount,double now)
        {
            if(!Finite(amount) || amount<=0 || !Finite(now) || now<0) return false;
            _tokens=Math.Min(_capacity,_tokens+Math.Max(0,now-_last)*_rate);_last=Math.Max(_last,now);
            if(amount>_tokens)return false;
            _tokens-=amount;return true;
        }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }
    internal sealed class LanMessageLimits
    {
        private readonly LanRateLimit[] _limits=new LanRateLimit[(int)PacketKind.Leave+1];
        internal LanMessageLimits()
        {
            Add(PacketKind.Hello,4,4);Add(PacketKind.Welcome,4,4);Add(PacketKind.Busy,1,2);Add(PacketKind.VersionMismatch,1,2);
            Add(PacketKind.State,40,48); // 20 a second since 1.4.6
            Add(PacketKind.Heartbeat,20,32);Add(PacketKind.WorldState,8,8);
            Add(PacketKind.TrainMotion,30,32);Add(PacketKind.TrainLayoutChunk,160,128);Add(PacketKind.TrainLayoutAck,8,8);
            // Guest actions (1.0.2): up to 20 shots a second plus retries, and a magazine-long burst.
            Add(PacketKind.ZombieState,40,32);Add(PacketKind.ZombieAction,30,48);Add(PacketKind.ZombieHurt,20,16);Add(PacketKind.ZombieHurtAck,20,16);
            Add(PacketKind.CombatProposal,1,2);Add(PacketKind.CombatState,20,16);Add(PacketKind.CombatReload,8,4);
            // Guest profile: a 64 KiB blob is 73 chunks; senders pace 2 per frame and retry every 2 s.
            Add(PacketKind.IdentityRequest,1,4);Add(PacketKind.IdentityProof,1,4);Add(PacketKind.GuestStatus,2,4);
            Add(PacketKind.GuestStateChunk,80,80);Add(PacketKind.GuestStateAck,8,8);Add(PacketKind.GuestRestoreChunk,80,80);Add(PacketKind.GuestRestoreAck,8,8);
            // Shared storages: one request in flight (retried each second), views of at most 64 KiB.
            Add(PacketKind.StorageRequest,10,20);Add(PacketKind.StorageResult,10,20);
            Add(PacketKind.StorageViewChunk,80,80);Add(PacketKind.StorageViewAck,16,16);
            // World: ragdoll poses (one per 50 ms), door batches (five per second), door requests.
            Add(PacketKind.ZombieCorpse,24,24);Add(PacketKind.DoorStates,10,10);Add(PacketKind.DoorToggle,4,4);
            Add(PacketKind.WorldItems,10,10);
            // Location objects (1.4.4): batches four a second, the guest's levers and traps.
            Add(PacketKind.SceneStates,10,10);Add(PacketKind.SceneUse,4,4);
            // Leaving (1.4.5): sent a few times right before the sender closes its session.
            Add(PacketKind.Leave,2,4);
            // Marks: a sender places at most one a second (1.1.6; 0.3 s before).
            Add(PacketKind.Mark,4,8);
            // Chat: messages, repeats and acknowledgements; a sender keeps to 4 a second (8 at once).
            Add(PacketKind.Chat,8,16);
            // Zombie effects (1.1.6): spits and burst stomachs, a few a second even in a crowd.
            Add(PacketKind.ZombieEffect,10,20);
            // Revive (1.1.9): the reviver repeats its hold every quarter second.
            Add(PacketKind.Revive,6,12);
            // Quests (1.2.0): the host sends its quests about once a second, at once after a change.
            Add(PacketKind.QuestStates,4,8);
            // Explosions (1.2.2): a chain of barrels at once; sleep: lying down and getting up.
            Add(PacketKind.Explosion,10,20);Add(PacketKind.Sleep,4,8);
        }
        private void Add(PacketKind kind,double rate,double capacity) { _limits[(int)kind]=new LanRateLimit(rate,capacity); }
        internal bool Take(PacketKind kind,double now)
        {
            int i=(int)kind;return i>0 && i<_limits.Length && _limits[i]!=null && _limits[i].Take(1,now);
        }
    }
    // Shared by all sync objects of a room: a DTLS reconnect/epoch reset cannot
    // restore the initial allowance for expensive Unity work.
    internal sealed class LanPeerWorkLimits
    {
        private readonly LanRateLimit _worlds=new LanRateLimit(.1,2),_layouts=new LanRateLimit(2,4);
        internal string Failure { get; private set; }
        internal bool WorldChange(double now) { return Take(_worlds,now,"Хост слишком часто меняет мир"); }
        internal bool LayoutChange(double now) { return Take(_layouts,now,"Хост слишком часто заменяет состав поезда"); }
        private bool Take(LanRateLimit limit,double now,string reason)
        {
            if(Failure!=null)return false;
            if(limit.Take(1,now))return true;
            Failure=reason;return false;
        }
    }
}
