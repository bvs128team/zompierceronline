using System;
using System.IO;
using System.Text;

namespace ZompiercerLAN
{
    internal enum PacketKind : byte { Hello=1, Welcome=2, State=3, Busy=4, VersionMismatch=5, Heartbeat=6, TrainMotion=7, TrainLayoutChunk=8, TrainLayoutAck=9, WorldState=10, ZombieState=11, ZombieAction=12, ZombieHurt=13, ZombieHurtAck=14, CombatProposal=15, CombatState=16, CombatReload=17,
        // Schema 10 (0.5.0): guest profile persistence on the host.
        IdentityRequest=18, IdentityProof=19, GuestStatus=20, GuestStateChunk=21, GuestStateAck=22, GuestRestoreChunk=23, GuestRestoreAck=24,
        // Schema 11 (0.6.0): host-authoritative shared storages.
        StorageRequest=25, StorageResult=26, StorageViewChunk=27, StorageViewAck=28,
        // Schema 12 (0.6.1): zombie corpses, world doors.
        ZombieCorpse=29, DoorStates=30, DoorToggle=31,
        // Schema 13 (0.7.0): loose items and loot bags of the host's world.
        // Schema 14 (0.8.0): no new kinds; storage request 9 (build) and work kind 5 (refuel).
        // Schema 15 (0.8.1): storage request 10 (place a blueprint), result statuses up to 20.
        WorldItems=32,
        // Schema 16 (0.10.0): marks of either player; storage request 11 (train controls);
        // the train frame carries the cab's state and whether the host lets the guest drive.
        Mark=33,
        // Schema 17 (0.11.0): text chat (LanChatChannel), not bound to a world.
        // Schema 18 (0.12.0): no new kinds; storage request 12 (the guest died), train flag 64 (the host is dead).
        // Schema 19 (1.0.1): a zombie frame also carries its animator's bool parameters, so the
        // guest's copy follows the host's state machine instead of falling back to idle.
        // Schema 20 (1.0.7): zombie batches carry the host clock; car-relative zombie actions.
        // Schema 21 (1.0.8): a player state carries its health; a zombie action its loudness.
        // Schema 22 (1.1.0): a player state carries where it looks up or down and whether it crouches;
        // its shot counter also counts melee swings.
        // Schema 23 (1.1.1): the player state's pose also says whether it aims down the sights.
        // Schema 24 (1.1.5): a workbench's storage view carries its craft queue and progress;
        // storage request 13 cancels one queued craft (the guest uses the game's craft window).
        Chat=34,
        // Schema 25 (1.1.6): a player state carries its health points and chosen look; a guest swing
        // may be a bare fist (item -1); control 6 opens or closes a train door; world items of kind 3
        // are the host's carryable objects (barrels, robot dogs). A zombie's visible effect
        // (a spit, a burst stomach) reaches the guest.
        // Schema 26 (1.1.8): a zombie frame's Flags also carry the firefighter's flamethrower (firing,
        // in which hand) and its burning hands; ZombieEffect 3 is the nurse's thrown healing smoke.
        ZombieEffect=35,
        // Schema 27 (1.1.9): a player state says whether the player lies downed or dead, its
        // flashlights and a reload, and carries its nickname; Revive: the partner is reviving
        // (ReviveHold, while the use key is held) or has revived (ReviveDone) a downed player.
        Revive=36,
        // Schema 28 (1.2.0): the host's quests (QuestStates) reach the guest; storage request 14 asks
        // the host to accept or hand in a quest; a door frame says whether the door is locked and a
        // door toggle may unlock it with the guest's key (DoorUnlock, Revision: the key item).
        // Schema 29 (1.2.1): a carryable object's frame says who holds it and whose robot dog it is;
        // storage request 15 carries objects and drives the guest's own robot dog; storage target 4
        // is a slot of that dog.
        QuestStates=37,
        // Schema 30 (1.2.2): Explosion: a host barrel catches fire or a host explosive goes off
        // (Text: its prefab, X/Y/Z: where); Sleep: a player lies down to sleep, gets up, or the host
        // starts the shared sleep (Revision: hours); work kinds 6-8 fill bottles at, pour bottles into
        // or read the rainwater tank of the host's train.
        Explosion=38, Sleep=39,
        // Schema 31 (1.4.0): no new kinds; storage request 16 throws the guest's grenade, dynamite
        // or C4 in the host's world, storage request 17 paints, wires or letters the host's train
        // (and the train layout carries paint, wires and sign texts).
        // Schema 32 (1.4.2): no new kinds; storage request 18 places the guest's furniture kit on the
        // host's train, storage request 19 takes a part or furniture apart (or a blueprint away);
        // result status 23: the object still holds something.
        // Schema 33 (1.4.3): no new kinds; a zombie's melee hit on the guest is its own ZombieHurt
        // (Action HurtMelee, Text: the zombie's prefab when its hit carries the prefab's effects),
        // the train frame carries the main tank's fuel and the fuel stations near the guest
        // (StationFrame), takable op 6 (StationUse) works a station's lever, pump and fuel gun.
        // Schema 34 (1.4.4): SceneStates: how the host's location objects stand (walls blown up, trap
        // walls, collapsed floors, bridges, drained swamps, world levers and buttons, resources picked
        // clean: SceneFrame by the game's ObjectID); SceneUse: the guest's lever, trap or bridge in the
        // host's world (Revision: ObjectID, Action: what). WorldState carries the host's difficulty.
        SceneStates=40, SceneUse=41,
        // Schema 35 (1.4.5): Leave: the player leaves the game (no waiting 30 s for silence); a quest
        // frame may say the guest has the reward of a quest the host handed in (QuestShared); scene
        // objects 8 (taken apart for resources, by place) and 9 (presence triggers, the guest may set
        // one off); storage quest request 3 claims that reward.
        Leave=42
        }
    // A dead zombie's ragdoll: root pose and the world pose of each rigid bone,
    // in the order of the shared prefab's ZombieDamagDetector.rigidElements.
    internal sealed class CorpseFrame
    {
        internal uint Id;
        internal string Prefab;
        internal float X,Y,Z,Yaw;
        internal NetPose[] Bones;
    }
    // A scene door (DoorController) identified by its position; Flags: 1 open, 2 broken, 4 locked (schema 28).
    internal sealed class DoorFrame { internal float X,Y,Z; internal byte Flags; }
    // A location object of the host's scene (schema 34): the game's ObjectID, what it is (SceneBomb ..
    // SceneResource), Flags 1 when it is done (blown up, closed, collapsed, raised, draining, switched on,
    // picked clean), Value the drained time of a swamp (0 otherwise).
    internal sealed class SceneFrame { internal int Id; internal byte Kind,Flags; internal float Value; }
    // The host's difficulty the guest plays by (schema 34): zombie damage, weapon recoil and aim wobble,
    // whether players are shown on the map.
    internal sealed class DifficultySnapshot { internal float ZombieDamage,Recoil,Wobble; internal bool ShowOnMap; }
    // A quest of the host's scene (schema 28): Key is LanQuests.Key of its place in the scene,
    // Flags QuestAccepted/QuestReady/QuestCompleted, Stage its current stage (-1: none).
    internal sealed class QuestFrame { internal uint Key; internal byte Flags; internal sbyte Stage; }
    // A loose item (Kind 0) or loot bag (Kind 1) of the host's world near the guest,
    // or the removal of one (Kind 2). HostId is the host's instance id of the object.
    // Devices (schema 39, 1.4.13): a robot dog's frame (kind 3, the dog's prefab) also says what its device slots
    // hold, slot by slot up to the last one in use: the RobotDevice id the game spawns there, NoDevice: empty.
    internal sealed class WorldItemFrame { internal int HostId,ItemId,Amount; internal byte Kind; internal NetPose Pose; internal byte[] Devices; }
    internal sealed class ZombieFrame
    {
        internal uint Id;
        internal string Prefab;
        internal float X,Y,Z,Yaw,Health,AnimationTime;
        internal int Animation;
        internal uint AnimationFlags; // bool parameters of the zombie's animator, in controller order (schema 19)
        // ZombieStateBits: 0 alive, 1 dead, 2 deactivated, 3 destroyed; an alive zombie's other bits
        // are what it shows (ZombieFlameFiring .. ZombieHandFire1, schema 26).
        internal byte Flags;
    }
    internal sealed class NetPose { internal float X,Y,Z,QX,QY,QZ,QW; }
    internal sealed class CarPose { internal NetPose Root,Body,Front,Back; }
    internal sealed class TrainFrame
    {
        internal uint Sequence;
        internal int Revision;
        internal string Scene;
        internal float Speed,Thrust;
        // 1 engine, 2 brake, 4 reverse, 8 horn, 16 spotlight, 32 the guest may drive (0.10.0),
        // 64 the host's player is dead (0.12.0).
        internal byte Flags;
        // The train's own controls (LanTrainControl order): button states, broken buttons,
        // cab light switches, lever values.
        internal uint Buttons,Broken;
        internal ushort Lights;
        internal float[] Levers=new float[0];
        internal CarPose[] Cars;
        // Schema 33 (1.4.3): the main tank's fuel and capacity (capacity 0: no tank), and the
        // fuel stations near the guest.
        internal float Fuel,FuelMax;
        internal StationFrame[] Stations=new StationFrame[0];
    }
    // A fuel station of the host's scene (schema 33). Key: LanFuelStation.Key of its place,
    // Flags StationActive/StationLever/StationPump/StationPumping, Gun where its fuel gun is
    // (GunAtStation .. GunLoose), GunPose its world pose when a player holds it or it hangs loose.
    internal sealed class StationFrame { internal int Key; internal float Fuel; internal byte Flags,Gun; internal NetPose GunPose; }
    internal sealed class Packet
    {
        internal PacketKind Kind;
        internal string GameVersion;
        internal ulong Session;
        internal uint WorldEpoch;
        internal uint Sequence;
        internal string Scene;
        internal float X,Y,Z,Yaw,TrainPosition;
        internal int CarIndex=-1;
        internal float LocalX,LocalY,LocalZ,LocalYaw;
        internal int EquippedItemId=-1, ShotItemId=-1;
        internal uint ShotSequence;
        internal TrainFrame Train;
        internal int Revision,ChunkIndex,ChunkCount;
        internal byte[] Chunk;
        internal int Location, RailsId=-1;
        internal ClockSnapshot Clock;
        // WorldState (schema 34): the host's difficulty, or null.
        internal DifficultySnapshot Difficulty;
        // SceneStates (schema 34).
        internal SceneFrame[] SceneObjects;
        internal ZombieFrame[] Zombies;
        internal float AimX,AimY,AimZ,Damage;
        // ZombieState (schema 20): the host clock when the batch was taken, for client interpolation.
        internal float HostTime;
        // State (schema 21): the player's health in percent, or NoHealth. ZombieAction: the shot's
        // loudness in percent of the gun's hearing radius (less with a silencer).
        internal byte Health=LanProtocol.NoHealth, Noise=100;
        // State (schema 22): the view's pitch in degrees (up positive); pose flags PoseCrouch, PoseAim (23).
        internal float Pitch;
        internal byte Pose;
        // State (schema 25): health points as the game's own bar shows them (max 0: unknown), and
        // the look the player chose (NPCManager.ModelName, NoSkin: the default).
        internal ushort HealthNow, HealthMax;
        internal byte Skin=LanProtocol.NoSkin;
        // State (schema 27): the nickname the player typed (LanProtocol.ValidName; empty: none).
        internal string Name="";
        // State (schema 36): the sender's clock in milliseconds when it was sent (positions are shown
        // on it), and its dash (the game's dodge on Space): DashCount times 8 plus the direction
        // (DashForward .. clockwise in eighths); a new count is a new dash, 0 none yet.
        internal uint SentAt;
        internal byte Dash;
        // State (schema 37): how the player moves (MotionRun, MotionAir, MotionSwim, MotionCock and
        // what it uses, the use kind times 16) and a count of its throws (wraps; a new count is a throw).
        internal byte Motion, Throws;
        // State (schema 38): a count of the player's presses of the use key in play (wraps; a new count
        // is a reach for a door, a lever, a button or an item on the ground).
        internal byte Interacts;
        internal byte Action;
        internal CombatWeapon[] CombatWeapons;
        internal CombatAmmo[] CombatAmmo;
        internal float Stamina;
        internal int Amount;
        internal CorpseFrame Corpse;
        internal DoorFrame[] Doors;
        internal WorldItemFrame[] Items;
        internal QuestFrame[] Quests;
        internal string Text;
    }
    internal static class LanProtocol
    {
        private const uint Magic=0x4C504D5A;
        private const byte Version=39;
        internal const byte NoHealth=255;
        internal const byte NoSkin=255, MaxSkin=35;
        // ZombieEffect: Revision is the zombie's id, Action what it did, X/Y/Z where, Aim which way.
        internal const byte EffectShot=1, EffectBurst=2, EffectThrow=3;
        // ZombieFrame.Flags: the state, and for an alive firefighter its flamethrower (firing; held in
        // FirePosition 1 rather than 0) and its burning hands (flameParticles 0 and 1).
        internal const byte ZombieStateBits=3, ZombieFlameFiring=4, ZombieFlameHand1=8, ZombieHandFire0=16, ZombieHandFire1=32, MaxZombieFlags=63;
        // WorldItems kind 3 (a carryable object): Amount is its car + 1 (0: in the world, its pose
        // in the world; otherwise in that car's own space) plus TakableActive/Walk/Run times 256.
        internal const int TakableActive=1, TakableWalk=2, TakableRun=4, MaxTakablePrefab=4095;
        // Schema 29: held by the host's player or by the guest; a robot dog the receiving guest owns,
        // or one with an owner at all.
        internal const int TakableHeldByHost=8, TakableHeldByGuest=16, TakableYours=32, TakableClaimed=64, MaxTakableState=127;
        internal const byte PoseCrouch=1, PoseAim=2, PoseDowned=4, PoseDead=8, PoseHelmetLight=16, PoseGunLight=32, PoseReload=64, MaxPose=127;
        // State.Dash (schema 36): the low three bits the direction in eighths of a turn clockwise
        // from the player's facing, the high five a count (1..31, then 1 again) of its dashes.
        internal const int DashDirections=8, MaxDashCount=31;
        internal static bool ValidDash(byte dash) { return (dash>>3)<=MaxDashCount && ((dash>>3)!=0 || (dash&7)==0); }
        // State.Motion (schema 37): running, in the air (a jump or a fall), swimming, a throw held
        // back (the throwing phase before the release); bits 4..6 the item being used.
        internal const byte MotionRun=1, MotionAir=2, MotionSwim=4, MotionCock=8, UseShift=4;
        internal const byte UseNone=0, UseEat=1, UseDrink=2, UseBandage=3, UseMedicine=4, MaxUse=4;
        internal static bool ValidMotion(byte motion) { return (motion&0x80)==0 && (motion>>UseShift)<=MaxUse; }
        internal const byte ReviveHold=1, ReviveDone=2;
        // DoorToggle (schema 28): 0 close, 1 open, DoorUnlock unlock with key item Revision.
        internal const byte DoorUnlock=2, DoorOpenFlag=1, DoorBrokenFlag=2, DoorLockedFlag=4, MaxDoorFlags=7;
        // SceneStates and SceneUse (schema 34). A guest may use a button, set off a trap wall or a
        // collapsing floor, or raise a bridge; the rest only the host's world does.
        internal const int MaxSceneBatch=32;
        internal const byte SceneBomb=1, SceneTrap=2, SceneCollapse=3, SceneActivator=4, SceneDrainer=5, SceneButton=6, SceneResource=7,
            SceneParsed=8, SceneTrigger=9, MaxSceneKind=9;
        internal static bool ValidSceneFrame(SceneFrame s)
        { return s!=null && s.Id!=0 && s.Kind>=SceneBomb && s.Kind<=MaxSceneKind && s.Flags<=1 && !float.IsNaN(s.Value) && s.Value>=0 && s.Value<100000f && (s.Kind==SceneDrainer || s.Value==0); }
        internal static bool ValidSceneUse(int id,byte kind)
        { return id!=0 && (kind==SceneTrap || kind==SceneCollapse || kind==SceneActivator || kind==SceneButton || kind==SceneTrigger); }
        internal static bool ValidDifficulty(DifficultySnapshot d)
        {
            if(d==null) return true;
            foreach(float v in new[] {d.ZombieDamage,d.Recoil,d.Wobble}) if(float.IsNaN(v) || v<0 || v>10) return false;
            return true;
        }
        internal const int MaxKeyItem=65535;
        // QuestStates (schema 28).
        internal const byte QuestAccepted=1, QuestReady=2, QuestCompleted=4, QuestShared=8, MaxQuestFlags=15;
        // Explosion and Sleep (schema 30).
        internal const byte ExplosionFire=1, ExplosionBlast=2, SleepWait=1, SleepCancel=2, SleepStart=3;
        internal const int MaxSleepHours=24, MaxExplosiveName=64;
        internal const int MaxQuestBatch=64, MaxQuestStage=63;
        internal const int MaxNameChars=16, MaxNameBytes=64;
        internal const float MaxPitch=90f;
        internal const int MaxCorpseBones=24, MaxDoorBatch=64, MaxItemBatch=24;
        // 1.4.13: a robot dog's device slots in its frame (NoDevice: empty); a batch's frames take at most
        // MaxItemBytes (with the longest version and scene names the packet stays within 1200 bytes).
        internal const int MaxDogDevices=16, MaxItemBytes=1000;
        internal const byte NoDevice=255;
        internal static bool DogFrame(WorldItemFrame it) { return it.Kind==3 && it.ItemId==MaxTakablePrefab; }
        internal static int ItemBytes(WorldItemFrame it) { return 41+(DogFrame(it)?1+(it.Devices==null?0:it.Devices.Length):0); }
        internal const int MaxZombieBatch=8, MaxZombieIds=4096, MaxZombieReplicas=256;
        internal const int MaxCars=8, ChunkSize=900, MaxLayoutBytes=256*1024;
        internal const int MaxChunks=(MaxLayoutBytes+ChunkSize-1)/ChunkSize;
        internal const int MaxGuestBytes=64*1024, MaxGuestChunks=(MaxGuestBytes+ChunkSize-1)/ChunkSize;
        internal const int WorldIdBytes=16, ProfileTokenBytes=32, MaxStoragePayload=1000;
        internal const int MaxLevers=4;
        // Fuel stations in a train frame (schema 33).
        internal const int MaxStations=2;
        internal const byte StationActive=1, StationLever=2, StationPump=4, StationPumping=8, MaxStationFlags=15;
        internal const byte GunAtStation=0, GunHost=1, GunGuest=2, GunTank=3, GunLoose=4;
        internal static bool GunPosed(byte gun) { return gun==GunHost || gun==GunGuest || gun==GunLoose; }
        // ZombieHurt (schema 33): a zombie's melee hit, with the hit's sounds, blood and shake on the guest.
        internal const byte HurtMelee=5;
        // Chat: Action 0 a message (Sequence = its number), 1 acknowledges every message up to Sequence.
        internal const int MaxChatChars=200, MaxChatBytes=MaxChatChars*3;
        // Marks: 0 removes the sender's mark Revision, 1 enemy, 2 find, 3 point / direction.
        internal const byte MarkClear=0, MarkEnemy=1, MarkFind=2, MarkPoint=3;
        private static readonly Encoding Text=new UTF8Encoding(false,true);
        internal static byte[] Encode(Packet p)
        {
            using(var s=new MemoryStream(1200))
            using(var w=new BinaryWriter(s))
            {
                w.Write(Magic); w.Write(Version); w.Write((byte)p.Kind); WriteText(w,p.GameVersion,40); w.Write(p.Session);
                w.Write(p.WorldEpoch);
                switch(p.Kind)
                {
                    case PacketKind.ZombieState:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.HostTime);
                        if(p.Zombies==null || p.Zombies.Length>MaxZombieBatch) throw new InvalidDataException("Zombie batch");
                        w.Write((byte)p.Zombies.Length);
                        foreach(var z in p.Zombies) {
                            w.Write(z.Id); WriteText(w,z.Prefab,64); w.Write(z.X);w.Write(z.Y);w.Write(z.Z);w.Write(z.Yaw);
                            w.Write(z.Health);w.Write(z.Flags);w.Write(z.Animation);w.Write(z.AnimationTime);w.Write(z.AnimationFlags);
                        }
                        break;
                    case PacketKind.ZombieAction:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.EquippedItemId); w.Write(p.Action);
                        w.Write(p.X);w.Write(p.Y);w.Write(p.Z);w.Write(p.AimX);w.Write(p.AimY);w.Write(p.AimZ); w.Write(p.Revision);
                        // Schema 20: origin and aim in car CarIndex's own space, or in the world (-1).
                        w.Write((sbyte)p.CarIndex); w.Write(p.Noise); break;
                    case PacketKind.CombatReload:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.EquippedItemId); w.Write(p.Revision); break;
                    case PacketKind.CombatProposal:
                    case PacketKind.CombatState:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Revision); w.Write(p.Action); w.Write(p.Stamina);
                        w.Write(p.ShotSequence); w.Write(p.EquippedItemId);
                        if(!HostCombatInventory.ValidLoadout(p.CombatWeapons,p.CombatAmmo)) throw new InvalidDataException("Combat loadout");
                        w.Write((byte)p.CombatWeapons.Length);
                        foreach(var cw in p.CombatWeapons) { w.Write(cw.ItemId);w.Write(cw.Magazine);w.Write(cw.Durability); }
                        w.Write((byte)p.CombatAmmo.Length);
                        foreach(var ca in p.CombatAmmo) { w.Write(ca.ItemId);w.Write(ca.Amount); } break;
                    case PacketKind.ZombieHurt:
                        if(!ValidHurt(p.Damage,p.Action,p.Text)) throw new InvalidDataException("Zombie hurt");
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Damage); w.Write(p.Action); WriteText(w,p.Text,MaxExplosiveName); break;
                    case PacketKind.ZombieHurtAck:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); break;
                    case PacketKind.WorldState:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Location); w.Write(p.RailsId); w.Write(p.TrainPosition);
                        w.Write(p.Clock != null);
                        if(p.Clock != null) { w.Write(p.Clock.Year); w.Write(p.Clock.Day); w.Write(p.Clock.Hour); w.Write(p.Clock.HoursPerRealSecond); }
                        if(!ValidDifficulty(p.Difficulty)) throw new InvalidDataException("Difficulty");
                        w.Write(p.Difficulty != null);
                        if(p.Difficulty != null) { w.Write(p.Difficulty.ZombieDamage); w.Write(p.Difficulty.Recoil); w.Write(p.Difficulty.Wobble); w.Write(p.Difficulty.ShowOnMap); }
                        break;
                    case PacketKind.State:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96);
                        w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.Yaw); w.Write(p.TrainPosition);
                        w.Write((sbyte)p.CarIndex); w.Write(p.LocalX); w.Write(p.LocalY); w.Write(p.LocalZ); w.Write(p.LocalYaw);
                        w.Write(p.EquippedItemId); w.Write(p.ShotSequence); w.Write(p.ShotItemId); w.Write(p.Health);
                        w.Write(p.Pitch); w.Write(p.Pose);
                        w.Write(p.HealthNow); w.Write(p.HealthMax); w.Write(p.Skin);
                        if(!ValidName(p.Name??"")) throw new InvalidDataException("Player name");
                        WriteText(w,p.Name??"",MaxNameBytes);
                        if(!ValidDash(p.Dash)) throw new InvalidDataException("Dash");
                        if(!ValidMotion(p.Motion)) throw new InvalidDataException("Motion");
                        w.Write(p.SentAt); w.Write(p.Dash); w.Write(p.Motion); w.Write(p.Throws); w.Write(p.Interacts);
                        break;
                    case PacketKind.TrainMotion:
                        var f=p.Train;
                        w.Write(f.Sequence); w.Write(f.Revision); WriteText(w,f.Scene,96);
                        w.Write(f.Speed); w.Write(f.Thrust); w.Write(f.Flags);
                        w.Write(f.Buttons); w.Write(f.Broken); w.Write(f.Lights);
                        if(f.Levers==null || f.Levers.Length>MaxLevers) throw new InvalidDataException("Train levers");
                        w.Write((byte)f.Levers.Length); foreach(var lever in f.Levers) w.Write(lever);
                        if(f.Cars==null || f.Cars.Length==0 || f.Cars.Length>MaxCars) throw new InvalidDataException("Train car count");
                        w.Write((byte)f.Cars.Length);
                        foreach(var c in f.Cars) { WritePose(w,c.Root); WritePose(w,c.Body); WritePose(w,c.Front); WritePose(w,c.Back); }
                        if(!ValidFuel(f.Fuel,f.FuelMax)) throw new InvalidDataException("Train fuel");
                        w.Write(f.Fuel); w.Write(f.FuelMax);
                        if(f.Stations==null || f.Stations.Length>MaxStations) throw new InvalidDataException("Train stations");
                        w.Write((byte)f.Stations.Length);
                        foreach(var st in f.Stations)
                        {
                            if(!ValidStation(st)) throw new InvalidDataException("Fuel station");
                            w.Write(st.Key); w.Write(st.Fuel); w.Write(st.Flags); w.Write(st.Gun);
                            if(GunPosed(st.Gun)) WritePose(w,st.GunPose);
                        }
                        break;
                    case PacketKind.TrainLayoutChunk:
                        w.Write(p.Revision); w.Write((ushort)p.ChunkCount); w.Write((ushort)p.ChunkIndex);
                        if(p.Chunk==null || p.Chunk.Length>ChunkSize) throw new InvalidDataException("Chunk size");
                        w.Write((ushort)p.Chunk.Length); w.Write(p.Chunk); break;
                    case PacketKind.TrainLayoutAck: w.Write(p.Revision); break;
                    case PacketKind.IdentityRequest:
                    case PacketKind.IdentityProof:
                        int fixedSize=p.Kind==PacketKind.IdentityRequest?WorldIdBytes:ProfileTokenBytes;
                        if(p.Chunk==null || p.Chunk.Length!=fixedSize) throw new InvalidDataException("Identity field size");
                        w.Write(p.Chunk); break;
                    case PacketKind.GuestStatus: w.Write(p.Action); w.Write(p.Revision); break;
                    case PacketKind.GuestStateChunk:
                    case PacketKind.GuestRestoreChunk:
                    case PacketKind.StorageViewChunk:
                        w.Write(p.Revision); w.Write((ushort)p.ChunkCount); w.Write((ushort)p.ChunkIndex);
                        if(p.Chunk==null || p.Chunk.Length>ChunkSize) throw new InvalidDataException("Chunk size");
                        w.Write((ushort)p.Chunk.Length); w.Write(p.Chunk); break;
                    case PacketKind.GuestStateAck:
                    case PacketKind.GuestRestoreAck:
                    case PacketKind.StorageViewAck: w.Write(p.Revision); break;
                    case PacketKind.StorageRequest:
                    case PacketKind.StorageResult:
                        w.Write(p.Sequence); w.Write(p.Action); w.Write(p.Revision);
                        if(p.Kind==PacketKind.StorageResult) w.Write(p.Amount);
                        int payload=p.Chunk==null?0:p.Chunk.Length;
                        if(payload>MaxStoragePayload) throw new InvalidDataException("Storage payload");
                        w.Write((ushort)payload); if(payload>0) w.Write(p.Chunk); break;
                    case PacketKind.ZombieCorpse:
                        var cf=p.Corpse;
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(cf.Id); WriteText(w,cf.Prefab,64);
                        w.Write(cf.X); w.Write(cf.Y); w.Write(cf.Z); w.Write(cf.Yaw);
                        if(cf.Bones==null || cf.Bones.Length>MaxCorpseBones) throw new InvalidDataException("Corpse bones");
                        w.Write((byte)cf.Bones.Length); foreach(var bone in cf.Bones) WritePose(w,bone); break;
                    case PacketKind.DoorStates:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96);
                        if(p.Doors==null || p.Doors.Length>MaxDoorBatch) throw new InvalidDataException("Door batch");
                        w.Write((byte)p.Doors.Length);
                        foreach(var d in p.Doors) { w.Write(d.X); w.Write(d.Y); w.Write(d.Z); w.Write(d.Flags); } break;
                    case PacketKind.DoorToggle:
                        if(!ValidDoorToggle(p.Action,p.Revision)) throw new InvalidDataException("Door toggle");
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.Action); w.Write(p.Revision); break;
                    case PacketKind.SceneStates:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96);
                        if(p.SceneObjects==null || p.SceneObjects.Length>MaxSceneBatch) throw new InvalidDataException("Scene batch");
                        w.Write((byte)p.SceneObjects.Length);
                        foreach(var so in p.SceneObjects)
                        {
                            if(!ValidSceneFrame(so)) throw new InvalidDataException("Scene object");
                            w.Write(so.Id); w.Write(so.Kind); w.Write(so.Flags); w.Write(so.Value);
                        }
                        break;
                    case PacketKind.SceneUse:
                        if(!ValidSceneUse(p.Revision,p.Action)) throw new InvalidDataException("Scene use");
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Revision); w.Write(p.Action); break;
                    case PacketKind.Explosion:
                        if(p.Action<ExplosionFire || p.Action>ExplosionBlast || !ValidExplosive(p.Text)) throw new InvalidDataException("Explosion");
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Action); WriteText(w,p.Text,MaxExplosiveName); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); break;
                    case PacketKind.Sleep:
                        if(!ValidSleep(p.Action,p.Revision)) throw new InvalidDataException("Sleep");
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Action); w.Write((byte)p.Revision); break;
                    case PacketKind.QuestStates:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96);
                        if(p.Quests==null || p.Quests.Length>MaxQuestBatch) throw new InvalidDataException("Quest batch");
                        w.Write((byte)p.Quests.Length);
                        for(int i=0;i<p.Quests.Length;i++) {
                            var q=p.Quests[i];
                            if(!ValidQuest(q)) throw new InvalidDataException("Quest frame");
                            for(int j=0;j<i;j++) if(p.Quests[j].Key==q.Key) throw new InvalidDataException("Quest frame");
                            w.Write(q.Key); w.Write(q.Flags); w.Write(q.Stage);
                        }
                        break;
                    case PacketKind.WorldItems:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96);
                        if(p.Items==null || p.Items.Length>MaxItemBatch) throw new InvalidDataException("Item batch");
                        w.Write((byte)p.Items.Length);
                        foreach(var it in p.Items) {
                            w.Write(it.HostId); w.Write(it.ItemId); w.Write(it.Amount); w.Write(it.Kind); WritePose(w,it.Pose);
                            if(!DogFrame(it)) continue;
                            var devices=it.Devices??new byte[0];
                            if(devices.Length>MaxDogDevices) throw new InvalidDataException("Robot dog devices");
                            w.Write((byte)devices.Length); w.Write(devices);
                        }
                        break;
                    case PacketKind.Mark:
                        // A position in the world, or in car CarIndex's own space.
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Action); w.Write(p.Revision);
                        w.Write((sbyte)p.CarIndex); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); break;
                    case PacketKind.ZombieEffect:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Revision); w.Write(p.Action);
                        w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.AimX); w.Write(p.AimY); w.Write(p.AimZ); break;
                    case PacketKind.Revive:
                        w.Write(p.Sequence); WriteText(w,p.Scene,96); w.Write(p.Action); break;
                    case PacketKind.Chat:
                        if(!ValidChat(p.Sequence,p.Action,p.Text)) throw new InvalidDataException("Chat message");
                        var chat=Text.GetBytes(p.Text??"");
                        if(chat.Length>MaxChatBytes) throw new InvalidDataException("Chat message");
                        w.Write(p.Sequence); w.Write(p.Action); w.Write((ushort)chat.Length); w.Write(chat); break;
                }
                if(s.Length>1200) throw new InvalidDataException("LAN packet too large");
                return s.ToArray();
            }
        }
        internal static bool TryDecode(byte[] data,out Packet packet)
        {
            packet=null;
            if(data==null || data.Length<19 || data.Length>1200) return false;
            try
            {
                using(var s=new MemoryStream(data,false))
                using(var r=new BinaryReader(s))
                {
                    if(r.ReadUInt32()!=Magic || r.ReadByte()!=Version) return false;
                    var kind=(PacketKind)r.ReadByte();
                    if(kind<PacketKind.Hello || kind>PacketKind.Leave) return false;
                    var p=new Packet {Kind=kind,GameVersion=ReadText(r,40),Session=r.ReadUInt64(),WorldEpoch=r.ReadUInt32()};
                    switch(kind)
                    {
                        case PacketKind.ZombieState:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.HostTime=r.ReadSingle();
                            if(float.IsNaN(p.HostTime) || float.IsInfinity(p.HostTime) || p.HostTime<0) return false;
                            int zombies=r.ReadByte(); if(zombies>MaxZombieBatch) return false;
                            p.Zombies=new ZombieFrame[zombies];
                            for(int i=0;i<zombies;i++) {
                                var z=new ZombieFrame {Id=r.ReadUInt32(),Prefab=ReadText(r,64),X=ReadFloat(r),Y=ReadFloat(r),Z=ReadFloat(r),Yaw=ReadFloat(r),Health=ReadFloat(r),Flags=r.ReadByte(),Animation=r.ReadInt32(),AnimationTime=ReadFloat(r),AnimationFlags=r.ReadUInt32()};
                                if(z.Id==0 || z.Id>MaxZombieIds || string.IsNullOrEmpty(z.Prefab) || z.Yaw<0 || z.Yaw>360 || z.Health<0 || z.Flags>MaxZombieFlags || (z.Flags&ZombieStateBits)!=0 && z.Flags>ZombieStateBits || z.AnimationTime<0 || z.AnimationTime>1) return false;
                                for(int j=0;j<i;j++) if(p.Zombies[j].Id==z.Id) return false;
                                p.Zombies[i]=z;
                            }
                            break;
                        case PacketKind.ZombieAction:
                            p.Sequence=r.ReadUInt32();p.Scene=ReadText(r,96);p.EquippedItemId=r.ReadInt32();p.Action=r.ReadByte();
                            p.X=ReadFloat(r);p.Y=ReadFloat(r);p.Z=ReadFloat(r);p.AimX=ReadFloat(r);p.AimY=ReadFloat(r);p.AimZ=ReadFloat(r);
                            p.Revision=r.ReadInt32(); if(p.Revision<=0) return false;
                            float aim=p.AimX*p.AimX+p.AimY*p.AimY+p.AimZ*p.AimZ;
                            // Schema 25: item -1 is a swing of the bare fists.
                            if(p.EquippedItemId < -1 || p.EquippedItemId>65535 || p.Action>1 || p.EquippedItemId==-1 && p.Action!=1 || aim<.99f || aim>1.01f) return false;
                            p.CarIndex=r.ReadSByte();
                            if(p.CarIndex < -1 || p.CarIndex>=MaxCars || p.CarIndex>=0 && (Math.Abs(p.X)>100 || Math.Abs(p.Y)>100 || Math.Abs(p.Z)>100)) return false;
                            p.Noise=r.ReadByte(); if(p.Noise>100) return false;
                            break;
                        case PacketKind.CombatReload:
                            p.Sequence=r.ReadUInt32();p.Scene=ReadText(r,96);p.EquippedItemId=r.ReadInt32();p.Revision=r.ReadInt32();
                            if(p.EquippedItemId<0 || p.EquippedItemId>65535 || p.Revision<=0) return false; break;
                        case PacketKind.CombatProposal:
                        case PacketKind.CombatState:
                            p.Sequence=r.ReadUInt32();p.Scene=ReadText(r,96);p.Revision=r.ReadInt32();p.Action=r.ReadByte();p.Stamina=ReadFloat(r);
                            p.ShotSequence=r.ReadUInt32();p.EquippedItemId=r.ReadInt32();
                            // 1.4.18: up to the host's own bound (HostCombatInventory.MaxMaxStamina): since 1.4.1 the guest's
                            // maximum follows its stamina level (110 at level 2), and a state above 100 was dropped here, so
                            // the guest's combat stalled (no blow, shot or reload confirmed; the ledger kept correcting it).
                            if(p.Revision<0 || p.Action>2 || p.Stamina<0 || p.Stamina>HostCombatInventory.MaxMaxStamina ||
                                p.EquippedItemId < -1 || p.EquippedItemId > 65535 ||
                                kind==PacketKind.CombatProposal && (p.Revision!=0 || p.Action!=0 || p.Stamina!=0 || p.ShotSequence!=0 || p.EquippedItemId!=-1) ||
                                kind==PacketKind.CombatState && ((p.Action==0)!=(p.Revision==0))) return false;
                            int wc=r.ReadByte();if(wc>16) return false;p.CombatWeapons=new CombatWeapon[wc];
                            for(int i=0;i<wc;i++) p.CombatWeapons[i]=new CombatWeapon {ItemId=r.ReadInt32(),Magazine=r.ReadInt32(),Durability=ReadFloat(r)};
                            int ac=r.ReadByte();if(ac>16) return false;p.CombatAmmo=new CombatAmmo[ac];
                            for(int i=0;i<ac;i++) p.CombatAmmo[i]=new CombatAmmo {ItemId=r.ReadInt32(),Amount=r.ReadInt32()};
                            if(!HostCombatInventory.ValidLoadout(p.CombatWeapons,p.CombatAmmo) || kind==PacketKind.CombatState && p.Action!=1 && (wc!=0 || ac!=0)) return false;
                            break;
                        case PacketKind.ZombieHurt:
                            p.Sequence=r.ReadUInt32();p.Scene=ReadText(r,96);p.Damage=ReadFloat(r);p.Action=r.ReadByte();p.Text=ReadText(r,MaxExplosiveName);
                            if(!ValidHurt(p.Damage,p.Action,p.Text)) return false; break;
                        case PacketKind.ZombieHurtAck:
                            p.Sequence=r.ReadUInt32();p.Scene=ReadText(r,96); break;
                        case PacketKind.WorldState:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Location=r.ReadInt32(); p.RailsId=r.ReadInt32(); p.TrainPosition=ReadFloat(r);
                            if(p.WorldEpoch==0 || string.IsNullOrEmpty(p.Scene) || p.Location<0 || p.Location>100000 || p.RailsId < -1 || p.TrainPosition<0) return false;
                            byte hasClock=r.ReadByte(); if(hasClock>1) return false;
                            if(hasClock!=0)
                            {
                                p.Clock=new ClockSnapshot {Year=r.ReadInt32(),Day=r.ReadInt32(),Hour=ReadFloat(r),HoursPerRealSecond=ReadFloat(r)};
                                if(p.Clock.Year<1 || p.Clock.Year>9999 || p.Clock.Day<0 || p.Clock.Day>366 || p.Clock.Hour<0 || p.Clock.Hour>24 || p.Clock.HoursPerRealSecond<0 || p.Clock.HoursPerRealSecond>24) return false;
                            }
                            byte hasDifficulty=r.ReadByte(); if(hasDifficulty>1) return false;
                            if(hasDifficulty!=0)
                            {
                                p.Difficulty=new DifficultySnapshot {ZombieDamage=ReadFloat(r),Recoil=ReadFloat(r),Wobble=ReadFloat(r)};
                                byte map=r.ReadByte(); if(map>1) return false;
                                p.Difficulty.ShowOnMap=map==1;
                                if(!ValidDifficulty(p.Difficulty)) return false;
                            }
                            break;
                        case PacketKind.State:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            p.X=ReadFloat(r); p.Y=ReadFloat(r); p.Z=ReadFloat(r); p.Yaw=ReadFloat(r); p.TrainPosition=ReadFloat(r);
                            if(string.IsNullOrEmpty(p.Scene) || p.Yaw < 0 || p.Yaw > 360 || p.TrainPosition < 0) return false;
                            p.CarIndex=r.ReadSByte();
                            if(p.CarIndex < -1 || p.CarIndex>=MaxCars) return false;
                            p.LocalX=ReadFloat(r); p.LocalY=ReadFloat(r); p.LocalZ=ReadFloat(r); p.LocalYaw=ReadFloat(r);
                            if(Math.Abs(p.LocalX)>100 || Math.Abs(p.LocalY)>100 || Math.Abs(p.LocalZ)>100) return false;
                            if(p.LocalYaw < 0 || p.LocalYaw > 360) return false;
                            p.EquippedItemId=r.ReadInt32(); p.ShotSequence=r.ReadUInt32(); p.ShotItemId=r.ReadInt32();
                            if(p.EquippedItemId < -1 || p.EquippedItemId > 65535 || p.ShotItemId < -1 || p.ShotItemId > 65535) return false;
                            p.Health=r.ReadByte(); if(p.Health>100 && p.Health!=NoHealth) return false;
                            p.Pitch=ReadFloat(r); p.Pose=r.ReadByte();
                            if(p.Pitch < -MaxPitch || p.Pitch > MaxPitch || p.Pose>MaxPose) return false;
                            p.HealthNow=r.ReadUInt16(); p.HealthMax=r.ReadUInt16(); p.Skin=r.ReadByte();
                            if(p.HealthNow>p.HealthMax || p.Skin>MaxSkin && p.Skin!=NoSkin) return false;
                            p.Name=ReadText(r,MaxNameBytes); if(!ValidName(p.Name)) return false;
                            p.SentAt=r.ReadUInt32(); p.Dash=r.ReadByte(); if(!ValidDash(p.Dash)) return false;
                            p.Motion=r.ReadByte(); p.Throws=r.ReadByte(); if(!ValidMotion(p.Motion)) return false;
                            p.Interacts=r.ReadByte();
                            break;
                        case PacketKind.TrainMotion:
                            var f=new TrainFrame {Sequence=r.ReadUInt32(),Revision=r.ReadInt32(),Scene=ReadText(r,96),Speed=ReadFloat(r),Thrust=ReadFloat(r),Flags=r.ReadByte()};
                            f.Buttons=r.ReadUInt32(); f.Broken=r.ReadUInt32(); f.Lights=r.ReadUInt16();
                            int levers=r.ReadByte(); if(levers>MaxLevers) return false;
                            f.Levers=new float[levers];
                            for(int i=0;i<levers;i++) { f.Levers[i]=ReadFloat(r); if(Math.Abs(f.Levers[i])>1000) return false; }
                            int count=r.ReadByte();
                            if(f.Revision<=0 || string.IsNullOrEmpty(f.Scene) || count==0 || count>MaxCars || Math.Abs(f.Speed)>500 || Math.Abs(f.Thrust)>100 || f.Flags>127) return false;
                            f.Cars=new CarPose[count];
                            for(int i=0;i<count;i++) f.Cars[i]=new CarPose {Root=ReadPose(r),Body=ReadPose(r),Front=ReadPose(r),Back=ReadPose(r)};
                            f.Fuel=ReadFloat(r); f.FuelMax=ReadFloat(r);
                            if(!ValidFuel(f.Fuel,f.FuelMax)) return false;
                            int stations=r.ReadByte(); if(stations>MaxStations) return false;
                            f.Stations=new StationFrame[stations];
                            for(int i=0;i<stations;i++)
                            {
                                var st=new StationFrame {Key=r.ReadInt32(),Fuel=ReadFloat(r),Flags=r.ReadByte(),Gun=r.ReadByte()};
                                if(st.Gun>GunLoose) return false;
                                if(GunPosed(st.Gun)) st.GunPose=ReadPose(r);
                                if(!ValidStation(st)) return false;
                                f.Stations[i]=st;
                            }
                            p.Train=f; break;
                        case PacketKind.TrainLayoutChunk:
                            p.Revision=r.ReadInt32(); p.ChunkCount=r.ReadUInt16(); p.ChunkIndex=r.ReadUInt16();
                            int size=r.ReadUInt16();
                            if(p.Revision<=0 || p.ChunkCount<1 || p.ChunkCount>MaxChunks || p.ChunkIndex>=p.ChunkCount || size<1 || size>ChunkSize) return false;
                            if(p.ChunkIndex<p.ChunkCount-1 && size!=ChunkSize) return false;
                            if((p.ChunkCount-1)*ChunkSize+(p.ChunkIndex==p.ChunkCount-1?size:1)>MaxLayoutBytes) return false;
                            p.Chunk=r.ReadBytes(size); if(p.Chunk.Length!=size) return false;
                            break;
                        case PacketKind.TrainLayoutAck:
                            p.Revision=r.ReadInt32(); if(p.Revision<=0) return false; break;
                        case PacketKind.IdentityRequest:
                        case PacketKind.IdentityProof:
                            int fixedSize=kind==PacketKind.IdentityRequest?WorldIdBytes:ProfileTokenBytes;
                            p.Chunk=r.ReadBytes(fixedSize); if(p.Chunk.Length!=fixedSize) return false;
                            if(kind==PacketKind.IdentityRequest && new Guid(p.Chunk)==Guid.Empty) return false;
                            break;
                        case PacketKind.GuestStatus:
                            p.Action=r.ReadByte(); p.Revision=r.ReadInt32();
                            if(p.Action>1 || (p.Action==0)!=(p.Revision==0) || p.Revision<0) return false; break;
                        case PacketKind.GuestStateChunk:
                        case PacketKind.GuestRestoreChunk:
                        case PacketKind.StorageViewChunk:
                            p.Revision=r.ReadInt32(); p.ChunkCount=r.ReadUInt16(); p.ChunkIndex=r.ReadUInt16();
                            int blobSize=r.ReadUInt16();
                            if(p.Revision<=0 || p.ChunkCount<1 || p.ChunkCount>MaxGuestChunks || p.ChunkIndex>=p.ChunkCount || blobSize<1 || blobSize>ChunkSize) return false;
                            if(p.ChunkIndex<p.ChunkCount-1 && blobSize!=ChunkSize) return false;
                            if((p.ChunkCount-1)*ChunkSize+(p.ChunkIndex==p.ChunkCount-1?blobSize:1)>MaxGuestBytes) return false;
                            p.Chunk=r.ReadBytes(blobSize); if(p.Chunk.Length!=blobSize) return false;
                            break;
                        case PacketKind.GuestStateAck:
                        case PacketKind.GuestRestoreAck:
                        case PacketKind.StorageViewAck:
                            p.Revision=r.ReadInt32(); if(p.Revision<=0) return false; break;
                        case PacketKind.StorageRequest:
                        case PacketKind.StorageResult:
                            p.Sequence=r.ReadUInt32(); p.Action=r.ReadByte(); p.Revision=r.ReadInt32();
                            if(kind==PacketKind.StorageResult) p.Amount=r.ReadInt32();
                            int payloadSize=r.ReadUInt16();
                            if(payloadSize>MaxStoragePayload || p.Sequence==0 || p.Revision<0) return false;
                            p.Chunk=r.ReadBytes(payloadSize); if(p.Chunk.Length!=payloadSize) return false;
                            if(kind==PacketKind.StorageRequest ? !ValidStorageRequest(p.Action,p.Revision,payloadSize) :
                                p.Action>MaxStorageStatus || p.Amount<0 || p.Amount>1000000) return false;
                            break;
                        case PacketKind.ZombieCorpse:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            var corpse=new CorpseFrame {Id=r.ReadUInt32(),Prefab=ReadText(r,64),X=ReadFloat(r),Y=ReadFloat(r),Z=ReadFloat(r),Yaw=ReadFloat(r)};
                            int bones=r.ReadByte();
                            if(corpse.Id==0 || corpse.Id>MaxZombieIds || string.IsNullOrEmpty(corpse.Prefab) || corpse.Yaw<0 || corpse.Yaw>360 || bones>MaxCorpseBones) return false;
                            corpse.Bones=new NetPose[bones];
                            for(int i=0;i<bones;i++) corpse.Bones[i]=ReadPose(r);
                            p.Corpse=corpse; break;
                        case PacketKind.DoorStates:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            int doors=r.ReadByte(); if(doors>MaxDoorBatch) return false;
                            p.Doors=new DoorFrame[doors];
                            for(int i=0;i<doors;i++) {
                                p.Doors[i]=new DoorFrame {X=ReadFloat(r),Y=ReadFloat(r),Z=ReadFloat(r),Flags=r.ReadByte()};
                                if(p.Doors[i].Flags>MaxDoorFlags) return false;
                            }
                            break;
                        case PacketKind.DoorToggle:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.X=ReadFloat(r); p.Y=ReadFloat(r); p.Z=ReadFloat(r); p.Action=r.ReadByte(); p.Revision=r.ReadInt32();
                            if(!ValidDoorToggle(p.Action,p.Revision)) return false; break;
                        case PacketKind.SceneStates:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            int sceneCount=r.ReadByte(); if(sceneCount>MaxSceneBatch) return false;
                            p.SceneObjects=new SceneFrame[sceneCount];
                            for(int i=0;i<sceneCount;i++)
                            {
                                var so=new SceneFrame {Id=r.ReadInt32(),Kind=r.ReadByte(),Flags=r.ReadByte(),Value=ReadFloat(r)};
                                if(!ValidSceneFrame(so)) return false;
                                p.SceneObjects[i]=so;
                            }
                            break;
                        case PacketKind.SceneUse:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Revision=r.ReadInt32(); p.Action=r.ReadByte();
                            if(!ValidSceneUse(p.Revision,p.Action)) return false; break;
                        case PacketKind.Explosion:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Action=r.ReadByte(); p.Text=ReadText(r,MaxExplosiveName);
                            p.X=ReadFloat(r); p.Y=ReadFloat(r); p.Z=ReadFloat(r);
                            if(p.Action<ExplosionFire || p.Action>ExplosionBlast || !ValidExplosive(p.Text)) return false;
                            break;
                        case PacketKind.Sleep:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Action=r.ReadByte(); p.Revision=r.ReadByte();
                            if(!ValidSleep(p.Action,p.Revision)) return false;
                            break;
                        case PacketKind.QuestStates:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            int quests=r.ReadByte(); if(quests>MaxQuestBatch) return false;
                            p.Quests=new QuestFrame[quests];
                            for(int i=0;i<quests;i++) {
                                var q=new QuestFrame {Key=r.ReadUInt32(),Flags=r.ReadByte(),Stage=r.ReadSByte()};
                                if(!ValidQuest(q)) return false;
                                for(int j=0;j<i;j++) if(p.Quests[j].Key==q.Key) return false;
                                p.Quests[i]=q;
                            }
                            break;
                        case PacketKind.WorldItems:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96);
                            int items=r.ReadByte(); if(items>MaxItemBatch) return false;
                            p.Items=new WorldItemFrame[items];
                            for(int i=0;i<items;i++) {
                                var it=new WorldItemFrame {HostId=r.ReadInt32(),ItemId=r.ReadInt32(),Amount=r.ReadInt32(),Kind=r.ReadByte(),Pose=ReadPose(r)};
                                if(DogFrame(it)) {
                                    int devices=r.ReadByte(); if(devices>MaxDogDevices) return false;
                                    it.Devices=r.ReadBytes(devices); if(it.Devices.Length!=devices) return false;
                                }
                                if(it.HostId==0 || it.Kind>3 || it.Kind==0 && (it.ItemId<0 || it.ItemId>65535 || it.Amount<1 || it.Amount>1000000) ||
                                    (it.Kind==1 || it.Kind==2) && (it.ItemId!=-1 || it.Amount!=0)) return false;
                                if(it.Kind==3 && (it.ItemId<0 || it.ItemId>MaxTakablePrefab || it.Amount<0 || (it.Amount&255)>MaxCars || it.Amount>>8>MaxTakableState ||
                                    (it.Amount&255)!=0 && (Math.Abs(it.Pose.X)>256 || Math.Abs(it.Pose.Y)>256 || Math.Abs(it.Pose.Z)>256))) return false;
                                p.Items[i]=it;
                            }
                            break;
                        case PacketKind.Mark:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Action=r.ReadByte(); p.Revision=r.ReadInt32();
                            p.CarIndex=r.ReadSByte(); p.X=ReadFloat(r); p.Y=ReadFloat(r); p.Z=ReadFloat(r);
                            if(p.Action>MarkPoint || p.Revision<1 || p.Revision>65535 || p.CarIndex < -1 || p.CarIndex>=MaxCars) return false;
                            if(p.CarIndex>=0 && (Math.Abs(p.X)>256 || Math.Abs(p.Y)>256 || Math.Abs(p.Z)>256)) return false;
                            break;
                        case PacketKind.ZombieEffect:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Revision=r.ReadInt32(); p.Action=r.ReadByte();
                            p.X=ReadFloat(r); p.Y=ReadFloat(r); p.Z=ReadFloat(r); p.AimX=ReadFloat(r); p.AimY=ReadFloat(r); p.AimZ=ReadFloat(r);
                            float facing=p.AimX*p.AimX+p.AimY*p.AimY+p.AimZ*p.AimZ;
                            if(p.Revision<1 || p.Revision>MaxZombieIds || p.Action<EffectShot || p.Action>EffectThrow || facing<.99f || facing>1.01f) return false;
                            break;
                        case PacketKind.Revive:
                            p.Sequence=r.ReadUInt32(); p.Scene=ReadText(r,96); p.Action=r.ReadByte();
                            if(p.Action<ReviveHold || p.Action>ReviveDone) return false;
                            break;
                        case PacketKind.Chat:
                            p.Sequence=r.ReadUInt32(); p.Action=r.ReadByte();
                            int chatBytes=r.ReadUInt16(); if(chatBytes>MaxChatBytes) return false;
                            var chat=r.ReadBytes(chatBytes); if(chat.Length!=chatBytes) return false;
                            p.Text=Text.GetString(chat);
                            if(!ValidChat(p.Sequence,p.Action,p.Text)) return false;
                            break;
                    }
                    if(WorldBound(kind) && (p.WorldEpoch==0 || p.Sequence==0 || string.IsNullOrEmpty(p.Scene))) return false;
                    if(s.Position!=s.Length) return false;
                    packet=p; return true;
                }
            }
            catch(Exception) { return false; }
        }
        // Messages about one world epoch and scene, numbered by their sender.
        internal static bool WorldBound(PacketKind kind)
        { return kind>=PacketKind.ZombieState && kind<=PacketKind.CombatReload || kind>=PacketKind.ZombieCorpse && kind<=PacketKind.Mark || kind==PacketKind.ZombieEffect || kind==PacketKind.Revive || kind==PacketKind.QuestStates || kind==PacketKind.Explosion || kind==PacketKind.Sleep || kind==PacketKind.SceneStates || kind==PacketKind.SceneUse; }
        // An explosive's prefab name: letters, digits, '_', '-' and spaces.
        internal static bool ValidExplosive(string name)
        {
            if(string.IsNullOrEmpty(name) || name.Length>MaxExplosiveName) return false;
            foreach(char c in name) if(!(c>='a' && c<='z' || c>='A' && c<='Z' || c>='0' && c<='9' || c=='_' || c=='-' || c==' ')) return false;
            return true;
        }
        // A zombie hurt (schema 33): damage kinds 1..4, or a melee hit (HurtMelee) that alone may name
        // the zombie's prefab (no control characters).
        internal static bool ValidHurt(float damage,byte action,string prefab)
        {
            if(!(damage>0 && damage<=1000) || action<1 || action>HurtMelee) return false;
            if(string.IsNullOrEmpty(prefab)) return true;
            if(action!=HurtMelee || Text.GetByteCount(prefab)>MaxExplosiveName) return false;
            foreach(char c in prefab) if(c<0x20 || c==0x7F) return false;
            return true;
        }
        // The main tank (schema 33): no tank (both 0), or a level within a capacity.
        internal static bool ValidFuel(float fuel,float max)
        {
            if(float.IsNaN(fuel) || float.IsNaN(max) || max<0 || max>=100000f || fuel<0) return false;
            return max==0 ? fuel==0 : fuel<=max+1f;
        }
        internal static bool ValidStation(StationFrame s)
        {
            if(s==null || s.Key==0 || float.IsNaN(s.Fuel) || s.Fuel<0 || s.Fuel>=100000f || s.Flags>MaxStationFlags || s.Gun>GunLoose) return false;
            if(!GunPosed(s.Gun)) return s.GunPose==null;
            var g=s.GunPose;
            if(g==null) return false;
            foreach(float v in new[] {g.X,g.Y,g.Z,g.QX,g.QY,g.QZ,g.QW}) if(float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v)>=100000f) return false;
            float n=g.QX*g.QX+g.QY*g.QY+g.QZ*g.QZ+g.QW*g.QW;
            return n>=0.9f && n<=1.1f;
        }
        // Lying down and starting carry the hours; getting up carries none.
        internal static bool ValidSleep(byte action,int hours)
        { return action==SleepCancel ? hours==0 : (action==SleepWait || action==SleepStart) && hours>=1 && hours<=MaxSleepHours; }
        // A door toggle: close or open (no key), or unlock with a key item (schema 28).
        internal static bool ValidDoorToggle(byte action,int key)
        { return action<=1 ? key==0 : action==DoorUnlock && key>=0 && key<=MaxKeyItem; }
        // A quest frame: known flags, completed or ready only once accepted, a stage only once accepted.
        internal static bool ValidQuest(QuestFrame q)
        {
            if(q==null || q.Key==0 || q.Flags>MaxQuestFlags || q.Stage<-1 || q.Stage>MaxQuestStage) return false;
            if((q.Flags&QuestShared)!=0 && (q.Flags&QuestCompleted)==0) return false; // a reward only for a quest handed in
            return (q.Flags&QuestAccepted)!=0 || q.Flags==0 && q.Stage==-1;
        }
        // The wire form of a chat packet: a message is 1..MaxChatChars UTF-16 units without
        // C0/C1 control characters (strict UTF-8 already excludes lone surrogates); an
        // acknowledgement has no text. Display always cleans again (LanChatChannel.Clean).
        // A nickname as typed (1.1.9): up to MaxNameChars characters, no control characters, no
        // markup brackets, no leading or trailing spaces; empty means none.
        internal static bool ValidName(string name)
        {
            if(name==null || name.Length>MaxNameChars || name!=name.Trim()) return false;
            foreach(char c in name) if(c<0x20 || c>=0x7F && c<=0x9F || c=='<' || c=='>' || char.IsSurrogate(c) || c=='\uFFFD') return false;
            return true;
        }
        // What the player typed, made valid (characters ValidName refuses are dropped).
        internal static string CleanName(string typed)
        {
            var b=new StringBuilder();
            foreach(char c in typed??"") if(!(c<0x20 || c>=0x7F && c<=0x9F || c=='<' || c=='>' || char.IsSurrogate(c) || c=='\uFFFD')) b.Append(c);
            var name=b.ToString().Trim();
            if(name.Length>MaxNameChars) name=name.Substring(0,MaxNameChars).Trim();
            return name;
        }
        internal static bool ValidChat(uint sequence,byte action,string text)
        {
            if(sequence==0 || action>1 || text==null) return false;
            if(action==1) return text.Length==0;
            if(text.Length==0 || text.Length>MaxChatChars) return false;
            foreach(char c in text) if(c<0x20 || c>=0x7F && c<=0x9F) return false;
            return true;
        }
        // Storage operations (LanStorage): 1 open (target, no handle), 2 take and 3 put
        // (handle and payload), 4 close (handle, no payload).
        // 0.7.0: 5 pick up a loose item, 6 drop an item, 7 work on the world, 8 craft at a host
        // workbench (no handle, payload). 0.8.0: 9 build on the host's train (no handle, payload).
        // 0.8.1: 10 place a blueprint on the host's train (no handle, payload).
        // 0.10.0: 11 use a control of the host's train (no handle, payload).
        // 0.12.0: 12 the guest died: everything it owns goes into a bag where it fell (no handle, payload).
        // 1.1.5: 13 cancel one queued craft of the open workbench (handle, payload).
        // 1.2.0: 14 accept or hand in a quest of the host's scene (no handle, payload).
        // 1.2.1: 15 carry a host object, or drive the guest's own robot dog (no handle, payload).
        internal const byte StorageOpen=1, StorageTake=2, StoragePut=3, StorageClose=4, StoragePickup=5, StorageDrop=6, StorageWork=7, StorageCraft=8, StorageBuild=9, StoragePlace=10, StorageControl=11, StorageDeath=12, StorageCraftCancel=13, StorageQuest=14, StorageTakable=15, StorageThrow=16, StorageDecor=17, StorageFurniture=18, StorageDismantle=19, MaxStorageStatus=23;
        internal static bool ValidStorageRequest(byte op,int handle,int payload)
        {
            switch(op)
            {
                case StorageOpen: case StoragePickup: case StorageDrop: case StorageWork: case StorageCraft: case StorageBuild: case StoragePlace: case StorageControl: case StorageDeath: case StorageQuest: case StorageTakable: case StorageThrow: case StorageDecor: case StorageFurniture: case StorageDismantle: return handle==0 && payload>0;
                case StorageTake: case StoragePut: case StorageCraftCancel: return handle>0 && payload>0;
                case StorageClose: return handle>0 && payload==0;
                default: return false;
            }
        }
        private static float ReadFloat(BinaryReader r)
        {
            float v=r.ReadSingle();
            if(float.IsNaN(v)||float.IsInfinity(v)||Math.Abs(v)>=100000f) throw new InvalidDataException("Invalid coordinate");
            return v;
        }
        private static void WritePose(BinaryWriter w,NetPose p) { w.Write(p.X);w.Write(p.Y);w.Write(p.Z);w.Write(p.QX);w.Write(p.QY);w.Write(p.QZ);w.Write(p.QW); }
        private static NetPose ReadPose(BinaryReader r)
        {
            var p=new NetPose {X=ReadFloat(r),Y=ReadFloat(r),Z=ReadFloat(r),QX=ReadFloat(r),QY=ReadFloat(r),QZ=ReadFloat(r),QW=ReadFloat(r)};
            float n=p.QX*p.QX+p.QY*p.QY+p.QZ*p.QZ+p.QW*p.QW;
            if(n<0.9f || n>1.1f) throw new InvalidDataException("Invalid rotation");
            return p;
        }
        private static void WriteText(BinaryWriter w,string value,int maximum)
        {
            var b=Text.GetBytes(value??""); if(b.Length>maximum) throw new ArgumentException("LAN text field too long");
            w.Write((byte)b.Length); w.Write(b);
        }
        private static string ReadText(BinaryReader r,int maximum)
        {
            int length=r.ReadByte(); if(length>maximum) throw new InvalidDataException("LAN text field too long");
            var b=r.ReadBytes(length); if(b.Length!=length) throw new EndOfStreamException(); return Text.GetString(b);
        }
    }
}
