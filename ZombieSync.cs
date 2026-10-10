using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Main-thread only. Wire messages contain primitive state, never serialized Unity objects.
    internal sealed class ZombieSync : IDisposable
    {
        private static ZombieSync Active;
        private static Harmony Guard;
        private static bool ClientWorld; // Deliberately survives disconnect, Dispose and scene loads.
        private static bool IsClientWorld { get { if (LanSaveIsolation.Active) ClientWorld = true; return ClientWorld; } }
        private readonly bool _host;
        private readonly Action<Packet> _send;
        private readonly Action<string> _report;
        internal readonly LanCombatSync Combat;
        private readonly Dictionary<int, HostZombie> _hostIds = new Dictionary<int, HostZombie>();
        private readonly List<HostZombie> _catalog = new List<HostZombie>();
        private readonly Dictionary<uint, Replica> _replicas = new Dictionary<uint, Replica>();
        private readonly Dictionary<string, ZombieAIController> _prefabs = new Dictionary<string, ZombieAIController>(StringComparer.Ordinal);
        private readonly HashSet<string> _failedPrefabs = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _unknownPrefabs = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<Packet> _hurts = new Queue<Packet>();
        private readonly uint[] _seen = new uint[LanProtocol.MaxZombieIds + 1];
        private readonly bool[] _dead = new bool[LanProtocol.MaxZombieIds + 1];
        private readonly uint[] _corpseSeen = new uint[LanProtocol.MaxZombieIds + 1];
        private uint _corpseSequence;
        private readonly ZombieFrame[] _pendingFrames = new ZombieFrame[LanProtocol.MaxZombieIds + 1];
        private readonly uint[] _pendingSequences = new uint[LanProtocol.MaxZombieIds + 1];
        private readonly float[] _pendingTimes = new float[LanProtocol.MaxZombieIds + 1];
        // Client (1.0.7): local time minus host time, from the fastest recent batches, and the
        // average extra delay of the others; replicas are shown that far in the host's past.
        private float _clockOffset, _jitter;
        private bool _haveClock;
        private readonly float[] _pendingDamage = new float[5];
        private int _pendingCount, _applyCursor = 1;
        private string _scene;
        private uint _epoch, _sequence, _actionSequence, _lastAction, _hurtSequence, _lastHurt;
        // 1.1.6: zombie effects (spits, burst stomachs) sent by the host / last shown by the guest.
        private uint _effectSequence, _lastEffect;
        private bool _effectFailed;
        // 1.4.14, host: spawners and territories woken around the guest (WakeAroundRemote); notices logged.
        private float _wakeAt, _territoriesAt, _noticeLogAt;
        private ZombieSpawnerTerritoryActivator[] _territories;
        private bool _wakeFailed;
        // 1.1.9: the partner lies downed or dead: no zombie takes it as a target.
        internal bool RemoteDown;
        // 1.4.13: how the guest moves (crouching, running, in the air, its ground speed) and its stealth
        // (ZFLevelController.stealthFactor): the host's zombies notice it by the game's own rules.
        internal bool RemoteCrouch, RemoteRun, RemoteAir;
        internal float RemoteSpeed, RemoteStealth=1f;
        internal const float EffectRange=150f;
        private bool _ready, _remoteReady, _playerReady, _disposed;
        private float _diagnosticAt;
        private int _sentFrames, _receivedFrames, _createdReplicas;
        private Vector3 _remote;
        private float _remoteYaw=float.NaN;
        private int _remoteItem, _cursor;
        private float _scanAt, _sendAt, _hurtAt, _nextAction, _damageAt;
        private GameObject _target;
        private sealed class HostZombie { internal ZombieAIController AI; internal uint Id; internal int AttackGeneration; internal string Prefab; internal bool Remote, Explosion, ChargeHit; internal float LastAlive, NextAttack, DeadAt=-1f, NextCorpse; internal ZombieFrame Last; internal Transform[] Bones; internal float KeepRemoteUntil; internal bool Fleeing; internal float FleeAt; }
        private sealed class Replica
        {
            internal GameObject Root; internal Animator Animator; internal Vector3 Position; internal Quaternion Rotation; internal float Seen; internal string Prefab; internal Transform[] Bones; internal int[] BoneOrder; internal bool Dead;
            // 1.4.11: this client saw it alive (a body first seen dead gives no experience).
            internal bool SeenAlive;
            // Host frames by host time, oldest first (1.0.7): the copy moves between them at an even pace
            // instead of lurching toward each new one, and its animation follows the same timeline.
            internal readonly Snap[] Snaps=new Snap[8]; internal int SnapCount;
            internal FlameVisual Flame; // 1.1.8: a firefighter's flamethrower and burning hands
            internal float Interval=.05f, AnimAt=-1f, MismatchSince=-1f; internal bool ForcePlay=true;
        }
        private struct Snap { internal float T, Yaw, AnimationTime; internal Vector3 P; internal int Animation; internal uint Flags; internal byte Fx; }
        // 1.1.8: the copy's flames: particles and lights of the prefab's own flamethrower effect and
        // burning hands (no scripts), the flamethrower's sounds; switched by the host's frame bits.
        private sealed class FlameVisual
        {
            internal Transform Holder; internal Quaternion Turn; internal Transform[] Hands;
            internal ParticleSystem[] Jet; internal Light[] JetLights; internal ParticleSystem[][] HandFire; internal GameObject[] HandLights;
            internal AudioSource Audio; internal AudioClip Start, End; internal byte Shown=255;
        }
        private static string _flameFailure;

        internal ZombieSync(bool host, Action<Packet> send, Action<string> report, HostCombatInventory inventory)
        {
            if(host && IsClientWorld) throw new InvalidOperationException("Client zombie isolation requires restart before hosting.");
            _host=host; _send=send; _report=report;
            Combat=new LanCombatSync(host,inventory,send);
            InstallGuard();
            Active=this;
            // Capture trusted local assets before the menu/scene that owns the
            // library is unloaded. Assets remain valid across world epochs.
            BuildPrefabCatalog();
            if(host) { _target=new GameObject("LAN remote zombie target"); Object.DontDestroyOnLoad(_target); }
        }

        internal void Tick(string scene,uint epoch,bool ready,Vector3 remotePosition,bool remoteReady,int remoteEquippedItemId,float remoteYaw=float.NaN,bool playerReady=true)
        {
            if(_disposed) return;
            if(_epoch!=epoch || _scene!=scene) ResetWorld(scene,epoch);
            _ready=ready && epoch!=0; _remoteReady=remoteReady && _ready; _playerReady=playerReady && _ready;
            _remote=remotePosition; _remoteItem=remoteEquippedItemId;_remoteYaw=remoteYaw;
            Combat.Tick(_playerReady && (!_host || _remoteReady));
            if(!_host && !IsClientWorld) return; // Failed/cancelled pairing must not alter a solo/menu world.
            if(_target!=null) _target.transform.position=_remote;
            if(_host && _ready && _remoteReady && !RemoteDown) WakeAroundRemote();
            BuildPrefabCatalog();
            float now=Time.unscaledTime;
            if (now >= _diagnosticAt) {
                _diagnosticAt=now+15f;
                _report("sync " + (_host?"host":"client") + " epoch=" + _epoch + " ready=" + _ready +
                    " playerReady=" + _playerReady + " remoteReady=" + _remoteReady + " prefabs=" + _prefabs.Count +
                    " catalog=" + _catalog.Count + " sent=" + _sentFrames + " received=" + _receivedFrames +
                    " created=" + _createdReplicas + " replicas=" + _replicas.Count + " pending=" + _pendingCount + (_host ? "" : " xp=" + Mathf.RoundToInt(_experience)));
            }
            if(now>=_scanAt) {
                _scanAt=now+.5f;
                foreach(var ai in Object.FindObjectsOfType<ZombieAIController>()) {
                    if(!_host) { ai.StopAllCoroutines(); ai.gameObject.SetActive(false); continue; }
                    if(!_ready || _catalog.Count>=LanProtocol.MaxZombieIds || _hostIds.ContainsKey(ai.GetInstanceID())) continue;
                    ZombieAIController prefab;
                    if(string.IsNullOrEmpty(ai.ID) || !_prefabs.TryGetValue(ai.ID,out prefab)) continue;
                    var h=new HostZombie {AI=ai,Id=(uint)_catalog.Count+1,Prefab=ai.ID,LastAlive=now};
                    _catalog.Add(h); _hostIds.Add(ai.GetInstanceID(),h);
                }
            }
            if(!_host) {
                if (_ready) ApplyPending();
                var remove=new List<uint>();
                float hostNow=now-_clockOffset, smooth=1-Mathf.Exp(-Time.unscaledDeltaTime*20);
                foreach(var pair in _replicas) {
                    var r=pair.Value;
                    if(r.Root==null || now-r.Seen>12) { if(r.Root!=null) Object.Destroy(r.Root); remove.Add(pair.Key); continue; }
                    if(r.Dead || r.SnapCount==0) continue; // a corpse keeps the pose its last ragdoll frame gave it
                    float at=hostNow-Mathf.Clamp(r.Interval+_jitter*2+.03f,.08f,.6f);
                    Vector3 position; float yaw; Sample(r,at,out position,out yaw);
                    var t=r.Root.transform;
                    if((t.position-position).sqrMagnitude>25) t.SetPositionAndRotation(position,Quaternion.Euler(0,yaw,0)); // a jump: no glide
                    else { t.position=Vector3.Lerp(t.position,position,smooth); t.rotation=Quaternion.Slerp(t.rotation,Quaternion.Euler(0,yaw,0),smooth); }
                    // The newest frame the shown moment has reached sets the animation.
                    for(int i=r.SnapCount-1;i>=0;i--) if(r.Snaps[i].T<=at) { if(r.Snaps[i].T>r.AnimAt) { r.AnimAt=r.Snaps[i].T; ApplyAnimation(r,r.Snaps[i]); } break; }
                    if(r.ForcePlay && r.AnimAt<0) ApplyAnimation(r,r.Snaps[0]); // just created: pose it at once
                    if(r.Flame!=null && r.Flame.Holder!=null) r.Flame.Holder.rotation=t.rotation*r.Flame.Turn; // the jet points ahead
                }
                foreach(uint id in remove) _replicas.Remove(id);
                return;
            }
            // World visuals do not require an accepted remote player pose. Combat
            // still does: an unplaced/stale client cannot redirect AI or cause damage.
            if(_ready && now>=_sendAt) { _sendAt=now+.05f; SendBatch(now); }
            if(!_ready || !_remoteReady) { ReleaseTargets(); return; }
            if(now>=_damageAt && _hurts.Count<64) {
                _damageAt=now+.1f;
                for(byte kind=1;kind<5 && _hurts.Count<64;kind++) if(_pendingDamage[kind]>0) {
                    var hurt=Message(PacketKind.ZombieHurt,++_hurtSequence);hurt.Action=kind;
                    hurt.Damage=Mathf.Min(1000,_pendingDamage[kind]);_pendingDamage[kind]-=hurt.Damage;_hurts.Enqueue(hurt);
                }
            }
            if(_hurts.Count>0 && now>=_hurtAt) { _hurtAt=now+.2f; _send(_hurts.Peek()); }
        }

        private void BuildPrefabCatalog()
        {
            var library=GlobalZombieLibrary.global;
            if(library==null || library.zombie==null) return;
            foreach(var prefab in library.zombie) {
                if(prefab==null || string.IsNullOrEmpty(prefab.ID) || System.Text.Encoding.UTF8.GetByteCount(prefab.ID)>64 || _prefabs.ContainsKey(prefab.ID)) continue;
                if(_prefabs.Count>=256) break;
                _prefabs.Add(prefab.ID,prefab);
            }
        }

        private void SendBatch(float now)
        {
            var batch=new List<ZombieFrame>();
            var relevant=new List<HostZombie>();
            foreach(var h in _catalog) if(h.AI!=null && h.AI.gameObject.activeInHierarchy || h.Last!=null && now-h.LastAlive<12) relevant.Add(h);
            relevant.Sort((a,b)=>Distance(a).CompareTo(Distance(b)));
            if(relevant.Count>LanProtocol.MaxZombieReplicas) relevant.RemoveRange(LanProtocol.MaxZombieReplicas,relevant.Count-LanProtocol.MaxZombieReplicas);
            int visited=0;
            while(visited++<relevant.Count && batch.Count<LanProtocol.MaxZombieBatch) {
                if(_cursor>=relevant.Count) _cursor=0;
                var h=relevant[_cursor++]; var ai=h.AI;
                if(ai!=null && ai.gameObject.activeInHierarchy) {
                    var pos=ai.transform.position; var anim=VisualAnimator(ai);
                    var z=new ZombieFrame {Id=h.Id,Prefab=h.Prefab,X=pos.x,Y=pos.y,Z=pos.z,Yaw=ai.transform.eulerAngles.y,
                        Health=ai.zombieHP==null?0:Mathf.Clamp(ai.zombieHP.currentHealth,0,99999),Flags=ai.dead?(byte)1:FlameFlags(ai)};
                    if(anim!=null && anim.runtimeAnimatorController!=null) { var state=anim.GetCurrentAnimatorStateInfo(0); z.Animation=state.fullPathHash; z.AnimationTime=Mathf.Repeat(state.normalizedTime,1); z.AnimationFlags=BoolFlags(anim); }
                    h.Last=z; h.LastAlive=now; batch.Add(z);
                } else if(h.Last!=null && now-h.LastAlive<12) {
                    h.Last.Flags=(byte)(ai==null?3:2); h.Last.Health=0; batch.Add(h.Last);
                }
            }
            var packet=Message(PacketKind.ZombieState,++_sequence); packet.Zombies=batch.ToArray(); packet.HostTime=Time.unscaledTime; _send(packet);_sentFrames+=batch.Count;
            SendCorpse(relevant,now);
        }

        // One ragdoll pose per batch: often while a body is still falling, then rarely.
        private void SendCorpse(List<HostZombie> relevant,float now)
        {
            HostZombie pick=null;
            foreach(var h in relevant) {
                var ai=h.AI;
                if(ai==null || !ai.dead || !ai.gameObject.activeInHierarchy) continue;
                if(h.DeadAt<0) { h.DeadAt=now; h.NextCorpse=now; }
                if(now>=h.NextCorpse && (pick==null || h.NextCorpse<pick.NextCorpse)) pick=h;
            }
            if(pick==null) return;
            pick.NextCorpse=now+(now-pick.DeadAt<6f?.3f:4f);
            if(pick.Bones==null) {
                var detector=pick.AI.GetComponentInChildren<ZombieDamagDetector>(true);
                pick.Bones=detector==null || detector.rigidElements==null || detector.rigidElements.Length>LanProtocol.MaxCorpseBones ? new Transform[0] : detector.rigidElements;
            }
            var bones=new NetPose[pick.Bones.Length];
            for(int i=0;i<bones.Length;i++) {
                if(pick.Bones[i]==null) { bones=new NetPose[0]; break; }
                bones[i]=Pose(pick.Bones[i]);
            }
            var root=pick.AI.transform.position;
            var packet=Message(PacketKind.ZombieCorpse,++_corpseSequence);
            packet.Corpse=new CorpseFrame {Id=pick.Id,Prefab=pick.Prefab,X=root.x,Y=root.y,Z=root.z,Yaw=Mathf.Repeat(pick.AI.transform.eulerAngles.y,360f)%360f,Bones=bones};
            _send(packet);
        }
        private static NetPose Pose(Transform t)
        {
            var p=t.position; var q=t.rotation;
            return new NetPose {X=p.x,Y=p.y,Z=p.z,QX=q.x,QY=q.y,QZ=q.z,QW=q.w};
        }
        private float Distance(HostZombie h)
        {
            var position=h.AI!=null?h.AI.transform.position:new Vector3(h.Last.X,h.Last.Y,h.Last.Z);
            var player=LocalPlayer();
            float local=player==null?float.PositiveInfinity:(position-player.transform.position).sqrMagnitude;
            return _remoteReady?Mathf.Min(local,(position-_remote).sqrMagnitude):local;
        }

        private static Animator VisualAnimator(Component source)
        {
            foreach(var animator in source.GetComponentsInChildren<Animator>(true))
                if(animator.runtimeAnimatorController!=null) return animator;
            return null;
        }

        internal void Receive(Packet p)
        {
            if(_disposed || !_ready || p.WorldEpoch!=_epoch || p.Scene!=_scene) return;
            if(p.Kind==PacketKind.CombatProposal || p.Kind==PacketKind.CombatState) { Combat.Receive(p);return; }
            if(_host) {
                if(!_remoteReady) return;
                if(p.Kind==PacketKind.ZombieAction) ReceiveAction(p);
                else if(p.Kind==PacketKind.CombatReload) {
                    if(!Newer(p.Sequence,_lastAction)) return;
                    _lastAction=p.Sequence;Combat.Processed(p.Sequence);
                    if(p.EquippedItemId==_remoteItem) Combat.Reload(p);
                }
                else if(p.Kind==PacketKind.ZombieHurtAck && _hurts.Count>0 && _hurts.Peek().Sequence==p.Sequence) { _hurts.Dequeue(); _hurtAt=0; }
                return;
            }
            if(p.Kind==PacketKind.ZombieState) {
                BuildPrefabCatalog();
                _receivedFrames+=p.Zombies.Length;
                // The fastest batches set the offset; it creeps up slowly if the path gets slower,
                // and follows at once after a host restart or a long stall.
                float sample=Time.unscaledTime-p.HostTime;
                if(!_haveClock || sample<_clockOffset || sample-_clockOffset>1.5f) { _clockOffset=sample;_haveClock=true; }
                else _clockOffset+=(sample-_clockOffset)*.01f;
                _jitter+=(Mathf.Clamp(sample-_clockOffset,0,.5f)-_jitter)*.05f;
                foreach(var z in p.Zombies) QueueFrame(z,p.Sequence,p.HostTime);
            } else if(p.Kind==PacketKind.ZombieCorpse) {
                ApplyCorpse(p.Corpse,p.Sequence);
            } else if(p.Kind==PacketKind.ZombieEffect) {
                if(!Newer(p.Sequence,_lastEffect)) return;
                _lastEffect=p.Sequence; ShowEffect(p);
            } else if(p.Kind==PacketKind.ZombieHurt) {
                if(!_playerReady) return;
                if(p.Sequence==_lastHurt) { _send(Message(PacketKind.ZombieHurtAck,p.Sequence)); return; }
                if(p.Sequence!=_lastHurt+1) return; // Host sends one outstanding result, in order, until acknowledged.
                var player=LocalPlayer(); var health=player==null?null:player.zombieFighterIndicatorsBar;
                if(health==null) return;
                if(p.Action==LanProtocol.HurtMelee) MeleeEffects(player,health,p.Damage,p.Text);
                else health.CauseDamage(p.Damage,false,false,(DamageType)p.Action);
                _lastHurt=p.Sequence; _send(Message(PacketKind.ZombieHurtAck,p.Sequence));
            }
        }

        // 1.1.8: what an alive firefighter shows: its flamethrower firing (and in which hand), its burning hands.
        private static byte FlameFlags(ZombieAIController ai)
        {
            var skill=ai.flamethrower; if(skill==null) return 0;
            int flags=0; var gun=skill.flamethrower;
            if(gun!=null) {
                if(gun.Firing) flags|=LanProtocol.ZombieFlameFiring;
                if(ai.FirePosition!=null && ai.FirePosition.Length>1 && ai.FirePosition[1]!=null && gun.transform.parent==ai.FirePosition[1]) flags|=LanProtocol.ZombieFlameHand1;
            }
            var fire=skill.flameParticles;
            if(fire!=null && fire.Length>0 && fire[0]!=null && fire[0].isPlaying) flags|=LanProtocol.ZombieHandFire0;
            if(fire!=null && fire.Length>1 && fire[1]!=null && fire[1].isPlaying) flags|=LanProtocol.ZombieHandFire1;
            return (byte)flags;
        }

        private void QueueFrame(ZombieFrame frame, uint sequence, float hostTime)
        {
            uint id=frame.Id;
            if(id==0 || id>LanProtocol.MaxZombieIds || (_seen[id]!=0 && !Newer(sequence,_seen[id])) ||
                (_pendingFrames[id]!=null && !Newer(sequence,_pendingSequences[id]))) return;
            if((frame.Flags&LanProtocol.ZombieStateBits)!=0) {
                if(_pendingFrames[id]!=null) { _pendingFrames[id]=null;_pendingCount--; }
                Apply(frame,sequence,hostTime); return;
            }
            // A replica takes every frame into its timeline; only frames that would create one wait here.
            if(_pendingFrames[id]==null && _replicas.ContainsKey(id)) { Apply(frame,sequence,hostTime); return; }
            if(_pendingFrames[id]==null) {
                if(_pendingCount>=LanProtocol.MaxZombieReplicas) return;
                _pendingCount++;
            }
            _pendingFrames[id]=frame;_pendingSequences[id]=sequence;_pendingTimes[id]=hostTime;
        }
        private void ApplyPending()
        {
            var budget=Stopwatch.StartNew();int created=0;
            for(int visited=0;visited<LanProtocol.MaxZombieIds && _pendingCount>0 && budget.Elapsed.TotalMilliseconds<3;visited++) {
                if(_applyCursor>LanProtocol.MaxZombieIds) _applyCursor=1;
                uint id=(uint)_applyCursor++;var frame=_pendingFrames[id];
                if(frame==null) continue;
                if(!_replicas.ContainsKey(id) && created++>=2) break;
                _pendingFrames[id]=null;_pendingCount--;Apply(frame,_pendingSequences[id],_pendingTimes[id]);
            }
        }

        private void Apply(ZombieFrame z,uint sequence,float hostTime)
        {
            if(z.Id==0 || z.Id>LanProtocol.MaxZombieIds || (_seen[z.Id]!=0 && !Newer(sequence,_seen[z.Id]))) return;
            _seen[z.Id]=sequence;
            Replica r;
            int state=z.Flags&LanProtocol.ZombieStateBits;
            if(state!=0) {
                if((state&1)!=0) _dead[z.Id]=true;
                if(!_replicas.TryGetValue(z.Id,out r)) return;
                if((state&1)!=0) Killed(r);
                // Dead and still present at the host: keep the body (its pose comes
                // with ZombieCorpse). Deactivated or destroyed there: remove it here.
                if(state==1) { MarkDead(r); r.Seen=Time.unscaledTime; return; }
                Object.Destroy(r.Root); _replicas.Remove(z.Id);
                return;
            }
            if(_dead[z.Id]) return;
            ZombieAIController prefab;
            if(!_prefabs.TryGetValue(z.Prefab,out prefab) || prefab==null) {
                if(_unknownPrefabs.Count<256 && _unknownPrefabs.Add(z.Prefab))
                    _report("Неизвестная локальная модель зомби; zombieId=" + z.Id + "; каталог=" + _prefabs.Count);
                return;
            }
            if(_failedPrefabs.Contains(z.Prefab)) return;
            if(!_replicas.TryGetValue(z.Id,out r)) {
                if(_replicas.Count>=LanProtocol.MaxZombieReplicas) return;
                try { r=CreateVisual(prefab); }
                catch(Exception ex) { _failedPrefabs.Add(z.Prefab);_report("Не удалось создать модель зомби " + z.Prefab + ": " + ex.GetType().Name + ": " + ex.Message);return; }
                r.Prefab=z.Prefab; _replicas.Add(z.Id,r);
                if(_flameFailure!=null) { _report("Огонь пожарного не показан: "+_flameFailure); _flameFailure=null; }
                r.Root.transform.position=new Vector3(z.X,z.Y,z.Z);
                r.Root.transform.rotation=Quaternion.Euler(0,z.Yaw,0);
                _createdReplicas++;
            }
            if(r.Prefab!=z.Prefab || r.Dead) return;
            r.Position=new Vector3(z.X,z.Y,z.Z); r.Rotation=Quaternion.Euler(0,z.Yaw,0); r.Seen=Time.unscaledTime;
            r.Root.SetActive(true); r.SeenAlive=true;
            if(r.SnapCount!=0) {
                float gap=hostTime-r.Snaps[r.SnapCount-1].T;
                if(gap<=0) return; // same or older host moment
                if(gap<1) r.Interval+=(gap-r.Interval)*.2f;
                else r.SnapCount=0; // after a long gap the old frames are history
            }
            if(r.SnapCount==r.Snaps.Length) { Array.Copy(r.Snaps,1,r.Snaps,0,r.Snaps.Length-1); r.SnapCount--; }
            r.Snaps[r.SnapCount++]=new Snap {T=hostTime,P=r.Position,Yaw=z.Yaw,Animation=z.Animation,AnimationTime=z.AnimationTime,Flags=z.AnimationFlags,Fx=(byte)(z.Flags&~LanProtocol.ZombieStateBits)};
        }
        // Position and facing at host time `at`: between the two frames around it, a short
        // extrapolation past the newest one, or the oldest one before the timeline starts.
        private static void Sample(Replica r,float at,out Vector3 position,out float yaw)
        {
            var first=r.Snaps[0]; var last=r.Snaps[r.SnapCount-1];
            if(at<=first.T) { position=first.P;yaw=first.Yaw;return; }
            if(at>=last.T) {
                position=last.P;yaw=last.Yaw;
                if(r.SnapCount<2) return;
                var prev=r.Snaps[r.SnapCount-2]; float span=last.T-prev.T;
                if(span<.01f || span>.5f) return;
                var velocity=(last.P-prev.P)/span;
                if(velocity.sqrMagnitude<400) position+=velocity*Mathf.Min(at-last.T,.15f);
                return;
            }
            int i=r.SnapCount-2; while(i>0 && r.Snaps[i].T>at) i--;
            var a=r.Snaps[i]; var b=r.Snaps[i+1]; float f=Mathf.Clamp01((at-a.T)/Mathf.Max(.001f,b.T-a.T));
            position=Vector3.Lerp(a.P,b.P,f); yaw=Mathf.LerpAngle(a.Yaw,b.Yaw,f);
        }
        // 1.0.1: the host's bool parameters drive the copy's own state machine. 1.0.7: a different
        // state is forced only when it lasts (a missed transition), not while both are mid-change,
        // where forcing it snapped the copy back and forth.
        private static void ApplyAnimation(Replica r,Snap s)
        {
            if(r.Flame!=null) ApplyFlame(r.Flame,s.Fx);
            if(r.Animator==null || !r.Animator.isInitialized) return;
            ApplyBoolFlags(r.Animator,s.Flags);
            if(s.Animation==0 || !r.Animator.HasState(0,s.Animation) || InState(r.Animator,s.Animation)) { r.MismatchSince=-1f;r.ForcePlay=false;return; }
            float now=Time.unscaledTime;
            if(!r.ForcePlay) {
                if(r.MismatchSince<0) { r.MismatchSince=now;return; }
                if(now-r.MismatchSince<.4f) return;
            }
            r.Animator.Play(s.Animation,0,s.AnimationTime); r.MismatchSince=-1f;r.ForcePlay=false;
        }
        private static bool InState(Animator animator,int state)
        {
            return animator.GetCurrentAnimatorStateInfo(0).fullPathHash==state ||
                animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash==state;
        }
        // Bool parameters (name hashes) of each zombie controller, in controller order; at most 32.
        private static readonly Dictionary<RuntimeAnimatorController,int[]> _boolParameters=new Dictionary<RuntimeAnimatorController,int[]>();
        private static int[] BoolParameters(Animator animator)
        {
            int[] hashes; var controller=animator.runtimeAnimatorController;
            if(controller==null || !animator.isInitialized) return new int[0];
            if(_boolParameters.TryGetValue(controller,out hashes)) return hashes;
            var list=new List<int>();
            foreach(var parameter in animator.parameters) if(parameter.type==AnimatorControllerParameterType.Bool && list.Count<32) list.Add(parameter.nameHash);
            hashes=list.ToArray(); if(_boolParameters.Count<64) _boolParameters[controller]=hashes;
            return hashes;
        }
        private static uint BoolFlags(Animator animator)
        {
            var hashes=BoolParameters(animator); uint flags=0;
            for(int i=0;i<hashes.Length;i++) if(animator.GetBool(hashes[i])) flags|=1u<<i;
            return flags;
        }
        private static void ApplyBoolFlags(Animator animator,uint flags)
        {
            var hashes=BoolParameters(animator);
            for(int i=0;i<hashes.Length;i++) animator.SetBool(hashes[i],(flags&(1u<<i))!=0);
        }

        // Client: a host ragdoll pose. Creates the body if this client never saw the
        // zombie alive (joined later, or it died far away).
        private void ApplyCorpse(CorpseFrame c,uint sequence)
        {
            if(!_ready || c==null || c.Id==0 || c.Id>LanProtocol.MaxZombieIds || (_corpseSeen[c.Id]!=0 && !Newer(sequence,_corpseSeen[c.Id]))) return;
            _corpseSeen[c.Id]=sequence; _dead[c.Id]=true;
            Replica r;
            if(!_replicas.TryGetValue(c.Id,out r)) {
                ZombieAIController prefab;
                if(_replicas.Count>=LanProtocol.MaxZombieReplicas || _failedPrefabs.Contains(c.Prefab) || !_prefabs.TryGetValue(c.Prefab,out prefab) || prefab==null) return;
                try { r=CreateVisual(prefab); }
                catch(Exception ex) { _failedPrefabs.Add(c.Prefab);_report("Не удалось создать модель зомби " + c.Prefab + ": " + ex.GetType().Name + ": " + ex.Message);return; }
                r.Prefab=c.Prefab; _replicas.Add(c.Id,r); _createdReplicas++;
            }
            if(r.Prefab!=c.Prefab) return;
            Killed(r);
            MarkDead(r);
            r.Position=new Vector3(c.X,c.Y,c.Z); r.Rotation=Quaternion.Euler(0,c.Yaw,0); r.Seen=Time.unscaledTime;
            r.Root.transform.SetPositionAndRotation(r.Position,r.Rotation);
            r.Root.SetActive(true);
            if(c.Bones.Length!=r.Bones.Length) return;
            foreach(var bone in c.Bones)
                if((new Vector3(bone.X,bone.Y,bone.Z)-r.Position).sqrMagnitude>100f) return; // a body is not 10 m long
            foreach(int i in r.BoneOrder) {
                var b=c.Bones[i];
                if(r.Bones[i]!=null) r.Bones[i].SetPositionAndRotation(new Vector3(b.X,b.Y,b.Z),new Quaternion(b.QX,b.QY,b.QZ,b.QW));
            }
        }
        // 1.4.11: the game gives every player a zombie's experience when it dies (ZombieDamagDetector.Dead,
        // which the client's copies never run): the client takes it, as the game would, when a zombie it saw
        // alive dies at the host while it plays in the host's world. The host credits the same experience
        // to the guest's checked stats (LanGuestCredits), so the guest's levels are kept.
        private void Killed(Replica r)
        {
            if(_host || r.Dead || !r.SeenAlive || !_playerReady) return;
            ZombieAIController prefab;
            var player=LocalPlayer();
            if(!_prefabs.TryGetValue(r.Prefab,out prefab) || prefab==null || player==null || player.zFLevelController==null) return;
            try {
                var detector=prefab.GetComponentInChildren<ZombieDamagDetector>(true);
                if(detector==null || !(detector.XPReward>0) || float.IsInfinity(detector.XPReward)) return;
                player.zFLevelController.AddExperience(detector.XPReward); _experience+=detector.XPReward;
            }
            catch(Exception ex) { if(!_experienceFailed) { _experienceFailed=true; _report("Zombie experience not given: "+ex.GetType().Name+": "+ex.Message); } }
        }
        private float _experience;
        private bool _experienceFailed;
        private static void MarkDead(Replica r)
        {
            if(r.Dead) return;
            r.Dead=true;
            if(r.Flame!=null) ApplyFlame(r.Flame,0);
            if(r.Animator!=null) r.Animator.enabled=false;
            foreach(var collider in r.Root.GetComponents<Collider>()) collider.enabled=false;
        }

        // Copy only transforms, meshes and a locally trusted animator. No prefab Awake, native AI,
        // native HP, loot, dismemberment, save registration or animation-event receivers are cloned.
        private static Replica CreateVisual(ZombieAIController prefab)
        {
            var root=new GameObject("LAN zombie visual"); root.SetActive(false);
            try {
            var map=new Dictionary<Transform,Transform>();
            CopyHierarchy(prefab.transform,root.transform,map,0);
            foreach(var source in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                Transform t; if(!map.TryGetValue(source.transform,out t)) continue;
                var dest=t.gameObject.AddComponent<SkinnedMeshRenderer>(); dest.sharedMesh=source.sharedMesh; dest.sharedMaterials=source.sharedMaterials;
                var bones=source.bones; var copied=new Transform[bones.Length];
                for(int i=0;i<bones.Length;i++) if(bones[i]!=null) map.TryGetValue(bones[i],out copied[i]);
                dest.bones=copied; Transform bone;
                if(source.rootBone!=null && map.TryGetValue(source.rootBone,out bone)) dest.rootBone=bone;
                dest.localBounds=source.localBounds; dest.enabled=source.enabled; dest.updateWhenOffscreen=true;
            }
            // The prefab's own Start() picks a random material; do the same for the copy (1.0.1).
            foreach(var random in prefab.GetComponentsInChildren<RandomMaterial>(true)) {
                Transform t; if(random.targetRenderer==null || random.materials==null || random.materials.Length==0 || !map.TryGetValue(random.targetRenderer.transform,out t)) continue;
                var copy=t.GetComponent<Renderer>(); if(copy!=null) copy.sharedMaterial=random.materials[UnityEngine.Random.Range(0,random.materials.Length)];
            }
            Animator animator=null; var sourceAnimator=VisualAnimator(prefab);
            Transform animatorTransform;
            if(sourceAnimator!=null && map.TryGetValue(sourceAnimator.transform,out animatorTransform)) {
                animator=animatorTransform.gameObject.AddComponent<Animator>(); animator.avatar=sourceAnimator.avatar;
                animator.runtimeAnimatorController=sourceAnimator.runtimeAnimatorController; animator.applyRootMotion=false;
                animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            }
            // The ragdoll bones of this prefab, parents first, for corpse poses.
            var ragdoll=new List<Transform>(); var depths=new List<int>();
            var detector=prefab.GetComponentInChildren<ZombieDamagDetector>(true);
            if(detector!=null && detector.rigidElements!=null && detector.rigidElements.Length<=LanProtocol.MaxCorpseBones)
                foreach(var element in detector.rigidElements) {
                    Transform copy;
                    if(element==null || !map.TryGetValue(element,out copy)) { ragdoll.Clear(); depths.Clear(); break; }
                    ragdoll.Add(copy); int depth=0; for(var node=copy;node!=null && node!=root.transform;node=node.parent) depth++; depths.Add(depth);
                }
            var order=new int[ragdoll.Count]; for(int i=0;i<order.Length;i++) order[i]=i;
            Array.Sort(order,(a,b)=>depths[a].CompareTo(depths[b]));
            // A passive capsule provides body collision without native AI/damage scripts.
            var controller=prefab.GetComponent<CharacterController>();
            if(controller!=null) {
                var capsule=root.AddComponent<CapsuleCollider>();capsule.radius=Mathf.Clamp(controller.radius,.1f,1);
                capsule.height=Mathf.Clamp(controller.height,capsule.radius*2,3);capsule.center=controller.center;
                root.layer=2;
            }
            return new Replica {Root=root,Animator=animator,Bones=ragdoll.ToArray(),BoneOrder=order,Flame=CreateFlame(prefab,root,map)};
            } catch { Object.Destroy(root);throw; }
        }
        // 1.1.8: the firefighter's flamethrower effect and burning hands for the copy. A failure leaves
        // the copy without flames (reported once), never without the zombie.
        private static FlameVisual CreateFlame(ZombieAIController prefab,GameObject root,Dictionary<Transform,Transform> map)
        {
            var skill=prefab.flamethrower!=null?prefab.flamethrower:prefab.GetComponentInChildren<SkillFlamethrower>(true);
            if(skill==null) return null;
            try {
                var f=new FlameVisual();
                var gun=skill.flamethrower;
                if(gun!=null && gun.Effect!=null) {
                    var holder=new GameObject("LAN flamethrower"); holder.transform.SetParent(root.transform,false); f.Holder=holder.transform;
                    f.Turn=Quaternion.Inverse(prefab.transform.rotation)*gun.transform.rotation;
                    var effect=VisualPart(gun.Effect.gameObject,f.Holder);
                    effect.transform.localPosition=gun.transform.InverseTransformPoint(gun.Effect.transform.position);
                    effect.transform.localRotation=Quaternion.Inverse(gun.transform.rotation)*gun.Effect.transform.rotation;
                    effect.SetActive(true);
                    f.Jet=effect.GetComponentsInChildren<ParticleSystem>(true); f.JetLights=effect.GetComponentsInChildren<Light>(true);
                    var source=gun.GetComponent<AudioSource>(); var audio=holder.AddComponent<AudioSource>();
                    audio.playOnAwake=false; audio.loop=true; audio.clip=gun.AudioLoop; audio.spatialBlend=1f;
                    if(source!=null) {
                        audio.outputAudioMixerGroup=source.outputAudioMixerGroup; audio.volume=source.volume; audio.spatialBlend=source.spatialBlend;
                        audio.minDistance=source.minDistance; audio.maxDistance=source.maxDistance; audio.rolloffMode=source.rolloffMode;
                        audio.dopplerLevel=source.dopplerLevel; audio.spread=source.spread; audio.priority=source.priority;
                    }
                    f.Audio=audio; f.Start=gun.AudioStart; f.End=gun.AudioEnd;
                } else { f.Jet=new ParticleSystem[0]; f.JetLights=new Light[0]; }
                f.Hands=new Transform[prefab.FirePosition==null?0:prefab.FirePosition.Length];
                for(int i=0;i<f.Hands.Length;i++) { Transform hand; if(prefab.FirePosition[i]!=null && map.TryGetValue(prefab.FirePosition[i],out hand)) f.Hands[i]=hand; }
                var fire=skill.flameParticles; int count=fire==null?0:Math.Min(2,fire.Length);
                f.HandFire=new ParticleSystem[count][]; f.HandLights=new GameObject[count];
                for(int i=0;i<count;i++) {
                    Transform parent;
                    if(fire[i]==null || fire[i].transform.parent==null || !map.TryGetValue(fire[i].transform.parent,out parent)) continue;
                    var copy=VisualPart(fire[i].gameObject,parent); copy.SetActive(true);
                    f.HandFire[i]=copy.GetComponentsInChildren<ParticleSystem>(true);
                    var light=skill.flameLight!=null && i<skill.flameLight.Length?skill.flameLight[i]:null;
                    if(light==null || light.transform==fire[i].transform) continue;
                    if(light.transform.IsChildOf(fire[i].transform)) { var found=copy.transform.Find(PathBelow(fire[i].transform,light.transform)); if(found!=null) f.HandLights[i]=found.gameObject; }
                    else { Transform lightParent; if(light.transform.parent!=null && map.TryGetValue(light.transform.parent,out lightParent)) f.HandLights[i]=VisualPart(light,lightParent); }
                }
                foreach(var system in AllParticles(f)) { var main=system.main; main.playOnAwake=false; }
                ApplyFlame(f,0);
                return f;
            } catch(Exception ex) { _flameFailure=prefab.ID+": "+ex.GetType().Name+": "+ex.Message; return null; }
        }
        private static IEnumerable<ParticleSystem> AllParticles(FlameVisual f)
        {
            foreach(var system in f.Jet) if(system!=null) yield return system;
            foreach(var hand in f.HandFire) if(hand!=null) foreach(var system in hand) if(system!=null) yield return system;
        }
        // A prefab part's own particles, lights and renderers only: its scripts, colliders, bodies
        // and sounds are removed while the copy is still inactive (nothing of it ever woke up).
        private static GameObject VisualPart(GameObject source,Transform parent)
        {
            var staging=new GameObject("LAN staging"); staging.SetActive(false);
            try {
                var copy=Object.Instantiate(source,staging.transform,false);
                foreach(var script in copy.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(script);
                foreach(var collider in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                foreach(var body in copy.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
                foreach(var audio in copy.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(audio);
                copy.transform.SetParent(parent,false);
                return copy;
            } finally { Object.Destroy(staging); }
        }
        private static string PathBelow(Transform top,Transform node)
        {
            var names=new List<string>();
            for(var t=node;t!=null && t!=top;t=t.parent) names.Add(t.name);
            names.Reverse(); return string.Join("/",names.ToArray());
        }
        // Switches the copy's flames to the host's bits (only what changed).
        private static void ApplyFlame(FlameVisual f,byte fx)
        {
            if(f.Shown==fx) return;
            bool fresh=f.Shown==255, firing=(fx&LanProtocol.ZombieFlameFiring)!=0, wasFiring=!fresh && (f.Shown&LanProtocol.ZombieFlameFiring)!=0;
            if(f.Holder!=null && firing) {
                int hand=(fx&LanProtocol.ZombieFlameHand1)!=0?1:0;
                var at=hand<f.Hands.Length?f.Hands[hand]:null;
                if(at!=null && f.Holder.parent!=at) { f.Holder.SetParent(at,true); f.Holder.localPosition=Vector3.zero; } // keeps the world scale
            }
            if(fresh || firing!=wasFiring) {
                foreach(var system in f.Jet) if(system!=null) { if(firing) system.Play(true); else system.Stop(true,fresh?ParticleSystemStopBehavior.StopEmittingAndClear:ParticleSystemStopBehavior.StopEmitting); }
                foreach(var light in f.JetLights) if(light!=null) light.enabled=firing;
                if(f.Audio!=null && !fresh) {
                    float volume=GlobalSoundEffects.global==null?1f:Mathf.Clamp01(GlobalSoundEffects.global.EffectsVolume);
                    if(firing) { if(f.Start!=null) f.Audio.PlayOneShot(f.Start,volume); if(f.Audio.clip!=null) f.Audio.Play(); }
                    else { f.Audio.Stop(); if(f.End!=null) f.Audio.PlayOneShot(f.End,volume); }
                }
            }
            for(int i=0;i<f.HandFire.Length;i++) {
                int bit=i==0?LanProtocol.ZombieHandFire0:LanProtocol.ZombieHandFire1;
                bool on=(fx&bit)!=0;
                if(!fresh && on==((f.Shown&bit)!=0)) continue;
                if(f.HandFire[i]!=null) foreach(var system in f.HandFire[i]) if(system!=null) { if(on) system.Play(true); else system.Stop(true,fresh?ParticleSystemStopBehavior.StopEmittingAndClear:ParticleSystemStopBehavior.StopEmitting); }
                if(f.HandLights[i]!=null) f.HandLights[i].SetActive(on);
            }
            f.Shown=fx;
        }
        private static void CopyHierarchy(Transform source,Transform target,Dictionary<Transform,Transform> map,int depth)
        {
            if(depth>32 || map.Count>=512) return;
            map.Add(source,target); target.localPosition=source.localPosition; target.localRotation=source.localRotation; target.localScale=source.localScale;
            var filter=source.GetComponent<MeshFilter>(); var renderer=source.GetComponent<MeshRenderer>();
            if(filter!=null && renderer!=null) { target.gameObject.AddComponent<MeshFilter>().sharedMesh=filter.sharedMesh; var copy=target.gameObject.AddComponent<MeshRenderer>(); copy.sharedMaterials=renderer.sharedMaterials; copy.enabled=renderer.enabled; }
            for(int i=0;i<source.childCount && depth<32 && map.Count<512;i++) { var child=source.GetChild(i); var obj=new GameObject(child.name); obj.transform.SetParent(target,false); CopyHierarchy(child,obj.transform,map,depth+1); obj.SetActive(child.gameObject.activeSelf && NativeVisible(child)); }
        }
        // What the prefab's own Start() would hide (1.0.1): decorations of a season other than the
        // current one (SeasonalEventObject) and accessories that lose their roll (RandomActivator).
        private static bool NativeVisible(Transform source)
        {
            var seasonal=source.GetComponent<SeasonalEventObject>();
            if(seasonal!=null && !SeasonActive((int)seasonal.Event)) return false;
            var random=source.GetComponent<RandomActivator>();
            return random==null || UnityEngine.Random.Range(0,100)<random.activationChanceMAX;
        }
        private static bool SeasonActive(int season)
        {
            switch(season) {
                case 0: return SeasonalEvents.IsCurrentlyNewYear();
                case 1: return SeasonalEvents.IsCurrentlyHalloween();
                case 2: return SeasonalEvents.IsCurrentlyAprilFoolsDay();
                default: return true;
            }
        }

        private void ReceiveAction(Packet p)
        {
            if(!Newer(p.Sequence,_lastAction)) return;
            _lastAction=p.Sequence;Combat.Processed(p.Sequence);
            float now=Time.unscaledTime;
            if(now<_nextAction-(float)HostCombatInventory.Jitter || p.EquippedItemId!=_remoteItem) return;
            var origin=new Vector3(p.X,p.Y,p.Z); var aim=new Vector3(p.AimX,p.AimY,p.AimZ);
            if(Vector3.Distance(origin,_remote+Vector3.up*1.5f)>1.75f || Mathf.Abs(aim.sqrMagnitude-1)>.01f) return;
            if(float.IsNaN(_remoteYaw) || float.IsInfinity(_remoteYaw)) return;
            var flat=new Vector3(aim.x,0,aim.z);
            if(flat.sqrMagnitude>.01f && Vector3.Angle(flat,Quaternion.Euler(0,_remoteYaw,0)*Vector3.forward)>95) return;
            if(Physics.Linecast(_remote+Vector3.up*1.5f,origin,~0,QueryTriggerInteraction.Ignore)) return;
            WeaponData weapon=null; Gun gun=null;
            if(p.EquippedItemId==-1) gun=LanCombatSync.FistGun(); // 1.1.6: a swing of the bare fists
            else {
                var db=ItemsDataBase.Global; var items=db==null?null:db.itemPrefab;
                if(items==null || p.EquippedItemId<0 || p.EquippedItemId>=items.Length || items[p.EquippedItemId]==null) return;
                weapon=items[p.EquippedItemId].GetComponent<WeaponData>();
                var player=LocalPlayer(); var guns=player==null || player.zombieFighterFireArmWeapon==null?null:player.zombieFighterFireArmWeapon.gunList;
                if(weapon==null || guns==null) return;
                foreach(var candidate in guns) if(candidate!=null && candidate.ID==weapon.ID) { gun=candidate; break; }
            }
            if(gun==null) return;
            float damage,range,interval; BulletScript bullet=null; int mask;
            if(p.Action==0) {
                if(weapon==null || gun.weaponType!=0 || gun.bulletPrefab==null) return;
                bullet=gun.bulletPrefab.GetComponent<BulletScript>(); if(bullet==null) return;
                damage=Mathf.Clamp(bullet.bulletDamage,0,200); range=100; mask=bullet.targetRaycastMask.value;
                interval=Mathf.Max(.05f,gun.currentBetweenShotsTime);
            } else {
                if(gun.weaponType!=2 || gun.Hands!=(weapon==null)) return;
                // The fists' own strength (the game never replaces it with an item's). 1.4.20: a weapon's too, as the game's
                // swing (Gun.GetMeleeDamage: the gun's melee strength, 5% more a strength level) with the guest's own
                // strength level; it took the lesser of the item's listed damage and the gun's strength, with no level, so
                // the guest's blows were weaker than the host's with the same weapon.
                var book=Combat.Ledger==null?null:Combat.Ledger();
                int strength=book==null?1:book.StrengthLevel;
                damage=Mathf.Clamp(gun.meleeAttackStrength*(1f+.05f*(strength-1)),0,200);
                range=Mathf.Clamp(gun.meleeAttacRange,0,3); interval=Mathf.Max(.5f,gun.meleeStrikeTimeForAttackEnd); mask=gun.targetRaycastMask.value;
            }
            if(damage<=0) return;
            if(!Combat.Spend(p)) return;
            _nextAction=Mathf.Max(_nextAction,now)+interval; // jitter-tolerant average rate (1.0.1)
            // 1.0.8: zombies hear the guest's shot as the game's own: the gun's hearing radius, cut
            // by the silencer on the guest's weapon (the guest reports the share, at most the full radius).
            if(p.Action==0) Gun.AttractByNoise(origin,Mathf.Clamp(gun.shotHearingRadius,0,500)*p.Noise/100f);
            // 1.0.7: a firearm round is the host gun's own native bullet from the guest's eye, so hits,
            // head and limb damage, armour, blood and deaths are the game's. Arrows stay a ray here:
            // a native arrow would leave a new pickup in the host world.
            if(p.Action==0 && !bullet.arrow) { FireNative(gun,origin,aim); return; }
            // The native layer mask: with every layer the zombie's own body capsule (CharacterController)
            // was nearer than its limb hitboxes, and the ray dealt no damage.
            RaycastHit hit;
            if(!Physics.Raycast(origin,aim,out hit,range,mask) || hit.collider==null) return;
            var zombie=hit.collider.GetComponentInParent<ZombieAIController>(); HostZombie h;
            if(zombie==null || zombie.dead || zombie.zombieHP==null || !_hostIds.TryGetValue(zombie.GetInstanceID(),out h)) return;
            var limb=hit.collider.GetComponent<LimbDetector>();
            if(limb==null || limb.deadVersionZombie || limb.zombieHP!=zombie.zombieHP) return; // Shields/armor props cannot be treated as exposed flesh.
            if(p.Action==0 && limb.armored>bullet.armorPenetration) return;
            // 1.4.13: as the game's: a swing counts the difficulty's share; the weapon's stealth bonus counts for a
            // swing at a zombie that noticed neither player, for an arrow at one that does not hunt (waits or walks).
            if(p.Action!=0 && DifficultyManager.Global!=null) damage*=Mathf.Clamp(DifficultyManager.Global.ZombieHP,.1f,10f);
            if(weapon!=null && (p.Action!=0 ? !h.Remote && Time.time>=h.KeepRemoteUntil && !zombie.playerIsInSight && !zombie.aiHeardPlayer : zombie.state=="Wait" || zombie.state=="Walk"))
                damage*=Mathf.Clamp(weapon.stealthModifier,0,10);
            if(p.Action!=0) MeleeHitEffects(zombie,limb,hit,gun); // 1.4.11: before the damage, as natively
            zombie.zombieHP.CauseDamage(limb.limbID==0 ? damage*2 : damage); // a head hit counts double, as natively
        }
        // 1.4.11, host: what the game's own swing does to a zombie besides the damage (Gun.MeleeHitTarget): a
        // wound and blood where it landed, the body-hit and pain sounds and the stun that plays its hit
        // animation (the guest sees it in the zombie's frames), the push its body falls with.
        private void MeleeHitEffects(ZombieAIController zombie,LimbDetector limb,RaycastHit hit,Gun gun)
        {
            try {
                var effects=GlobalEffects.Global;
                if(effects!=null && effects.bloodHole!=null && effects.bloodHole.Length!=0) {
                    var hole=effects.bloodHole[UnityEngine.Random.Range(0,effects.bloodHole.Length)];
                    if(hole!=null) Object.Instantiate(hole,hit.point+hit.normal*.1f,Quaternion.LookRotation(-hit.normal)).parent=hit.transform;
                }
                var detector=zombie.zombieDamagDetector;
                if(detector!=null) {
                    detector.DetectionBulletHits(); detector.SetLastHittedBodyPart(limb);
                    if(gun.meleeAttacPushForceRatio>0f) detector.pushingVector=-hit.normal*gun.meleeAttacPushForce/gun.meleeAttacPushForceRatio;
                }
                if(effects!=null) effects.SpawnBloodDecal(hit.point,hit.normal,zombie.transform.position);
                if(gun.meleeHitBodySound!=null) AudioSource.PlayClipAtPoint(gun.meleeHitBodySound,hit.point);
            }
            catch(Exception ex) { if(!_swingFailed) { _swingFailed=true; _report("Swing effects failed: "+ex.GetType().Name+": "+ex.Message); } }
        }
        private bool _swingFailed;

        // 1.4.11, guest: its swing at a zombie copy, which has no colliders (the game's own hit met what was
        // behind it): the game's wound, blood and body-hit sound where it lands, at once. The host deals the
        // damage; the zombie's flinch comes with its frames.
        private static bool BeforeMeleeHit(Gun __instance)
        {
            var a=Active;
            if(!IsClientWorld || a==null || a._host || !a._playerReady || __instance==null) return true;
            try {
                var camera=Camera.main; if(camera==null) return true;
                var origin=camera.transform.TransformPoint(new Vector3(0,0,-.2f)); var aim=camera.transform.forward;
                float range=Mathf.Clamp(__instance.meleeAttacRange,0,3)-.2f;
                if(range<=0f) return true;
                RaycastHit wall;
                if(Physics.Raycast(origin,aim,out wall,range,__instance.targetRaycastMask.value)) range=wall.distance;
                Replica r; Transform bone; Vector3 point;
                if(!a.SwingHits(origin,aim,range,out r,out bone,out point)) return true;
                var effects=GlobalEffects.Global;
                if(effects!=null && effects.bloodHole!=null && effects.bloodHole.Length!=0) {
                    var hole=effects.bloodHole[UnityEngine.Random.Range(0,effects.bloodHole.Length)];
                    if(hole!=null) Object.Instantiate(hole,point-aim*.1f,camera.transform.rotation).parent=bone;
                }
                if(effects!=null) effects.SpawnBloodDecal(point,-aim,r.Root.transform.position);
                if(__instance.meleeHitBodySound!=null) AudioSource.PlayClipAtPoint(__instance.meleeHitBodySound,point);
                return false;
            }
            catch(Exception ex) { if(!a._swingFailed) { a._swingFailed=true; a._report("Swing effects failed: "+ex.GetType().Name+": "+ex.Message); } return true; }
        }
        // The nearest live zombie copy the swing meets before `range`: its ragdoll bones and the middle of
        // each bone's reach to its parent, or a column from its feet to its head when it has none.
        private bool SwingHits(Vector3 origin,Vector3 aim,float range,out Replica hit,out Transform bone,out Vector3 point)
        {
            hit=null; bone=null; point=Vector3.zero; float best=range;
            const float Radius=.22f;
            foreach(var r in _replicas.Values) {
                if(r.Root==null || !r.Root.activeInHierarchy || r.Dead || (r.Root.transform.position-origin).sqrMagnitude>(range+3f)*(range+3f)) continue;
                var samples=new List<KeyValuePair<Vector3,Transform>>();
                if(r.Bones!=null && r.Bones.Length!=0)
                    foreach(var b in r.Bones) {
                        if(b==null) continue;
                        samples.Add(new KeyValuePair<Vector3,Transform>(b.position,b));
                        if(b.parent!=null && (b.parent.position-b.position).sqrMagnitude<.64f) samples.Add(new KeyValuePair<Vector3,Transform>((b.position+b.parent.position)*.5f,b));
                    }
                else for(int i=0;i<7;i++) samples.Add(new KeyValuePair<Vector3,Transform>(r.Root.transform.position+Vector3.up*(.2f+i*.25f),r.Root.transform));
                foreach(var s in samples) {
                    float along=Vector3.Dot(s.Key-origin,aim);
                    if(along<0f || along>best) continue;
                    var near=origin+aim*along;
                    if((s.Key-near).sqrMagnitude>Radius*Radius) continue;
                    best=along; hit=r; bone=s.Value; point=near;
                }
            }
            return hit!=null;
        }

        private static void FireNative(Gun gun,Vector3 origin,Vector3 aim)
        {
            var look=Quaternion.LookRotation(aim); var right=look*Vector3.right; var up=look*Vector3.up;
            float spread=Mathf.Clamp(gun.spreadOfFractions,0,.5f), speed=Mathf.Clamp(gun.bulletSpeed,1,2000);
            for(int i=Mathf.Clamp(gun.ProjectilesInShot,1,16);i>0;i--) {
                var circle=UnityEngine.Random.insideUnitCircle*spread;
                var direction=(aim+right*circle.x+up*circle.y).normalized;
                var round=Object.Instantiate(gun.bulletPrefab,origin,Quaternion.LookRotation(direction));
                round.AddRelativeForce(Vector3.forward*speed,ForceMode.VelocityChange);
                var script=round.GetComponent<BulletScript>(); if(script!=null) script.MakeInvisible();
            }
        }

        private void CaptureAction(Gun gun,byte action)
        {
            if(_host || !_playerReady || gun==null || LocalPlayer()==null || LocalPlayer().GetSelectedGun()!=gun) return;
            int item=RemoteEquipment.CaptureLocalItemId(); if(item<0 && !(gun.Hands && action==1)) return;
            if(gun.Hands) item=-1; // 1.1.6: the bare fists
            var rays=LocalPlayer().zombieFighterRays; if(rays==null || rays.cameraPoint==null) return;
            var origin=rays.cameraPoint.position; var aim=rays.cameraPoint.forward;
            var p=Message(PacketKind.ZombieAction,++_actionSequence); p.Action=action;p.EquippedItemId=item;
            var silencer=gun.GetInstalledSilencer(); float loudness=silencer==null?1f:silencer.Value;
            p.Noise=(byte)Mathf.RoundToInt(float.IsNaN(loudness)?100f:Mathf.Clamp01(loudness)*100f);
            // 1.0.7: on a train car, in that car's own space (as the player states are), so a moving
            // train does not shift the shot on the host.
            var car=LocalPlayer().OnTrainCar; var train=GlobalManager.global.controlledTrain;
            if(car!=null && train!=null && car.transform.IsChildOf(train.transform)) {
                int index=car.GetCarIndex();
                var local=car.transform.InverseTransformPoint(origin);
                if(index>=0 && index<LanProtocol.MaxCars && Mathf.Abs(local.x)<=100 && Mathf.Abs(local.y)<=100 && Mathf.Abs(local.z)<=100) {
                    p.CarIndex=index; origin=local; aim=car.transform.InverseTransformDirection(aim);
                }
            }
            p.X=origin.x;p.Y=origin.y;p.Z=origin.z;p.AimX=aim.x;p.AimY=aim.y;p.AimZ=aim.z;Combat.Submit(p);
        }

        private void CaptureReload(Gun gun)
        {
            if(_host || !_playerReady || gun==null || LocalPlayer()==null || LocalPlayer().GetSelectedGun()!=gun) return;
            int item=RemoteEquipment.CaptureLocalItemId();if(item<0)return;
            var p=Message(PacketKind.CombatReload,++_actionSequence);p.EquippedItemId=item;Combat.Submit(p);
        }

        private static ZombieFighterController LocalPlayer() { return GlobalManager.global==null?null:GlobalManager.global.controlledChar; }
        private Packet Message(PacketKind kind,uint seq) { return new Packet {Kind=kind,WorldEpoch=_epoch,Scene=_scene,Sequence=seq}; }
        private static bool Newer(uint a,uint b) { return a!=0 && unchecked((int)(a-b))>0; }
        private void Hurt(float damage,byte kind=1)
        {
            if(!_ready || !_remoteReady || _hurts.Count>=64 || damage<=0 || float.IsNaN(damage) || float.IsInfinity(damage)) return;
            if(kind<1 || kind>4) return;
            _pendingDamage[kind]=Mathf.Min(3000,_pendingDamage[kind]+Mathf.Clamp(damage,0,1000));
        }
        // 1.4.3: a melee hit is its own hurt, so the guest gets what the game's own hit brings
        // (ZombieAIController.ZombieAttackRays): a zombie with its hand on also passes its prefab,
        // whose buffs (bleeding) the guest takes as the game would.
        private void MeleeHit(ZombieAIController ai)
        {
            float damage=ai.GetMeleeDamage();
            if(!_ready || !_remoteReady || damage<=0 || float.IsNaN(damage) || float.IsInfinity(damage)) return;
            if(_hurts.Count>=64) { Hurt(damage); return; }
            var hurt=Message(PacketKind.ZombieHurt,++_hurtSequence);
            hurt.Action=LanProtocol.HurtMelee; hurt.Damage=Mathf.Min(1000,damage);
            string prefab=ai.ID;
            hurt.Text=ai.IsHaveHandForMelee() && LanProtocol.ValidHurt(hurt.Damage,LanProtocol.HurtMelee,prefab) ? prefab : "";
            _hurts.Enqueue(hurt);
        }
        // Guest: the game's own melee hit on the local player, its damage booked by the host.
        private void MeleeEffects(ZombieFighterController player,ZombieFighterIndicatorsBar health,float damage,string prefab)
        {
            try { var fx=player.GetComponent<ZombieFighterEffects>(); if(fx!=null) { fx.RandomBodyHitSound(); fx.RandomAttackPainSound(); } }
            catch(Exception ex) { EffectFailed("Melee hit sound",ex); }
            health.CauseDamage(damage,false,false,(DamageType)1);
            try
            {
                ZombieAIController zombie=null;
                if(!string.IsNullOrEmpty(prefab)) { BuildPrefabCatalog(); _prefabs.TryGetValue(prefab,out zombie); }
                var buffs=zombie==null?null:zombie.GetComponent<Zompiercer.UI.BuffParameters>();
                var stats=Zompiercer.GlobalStatsController.Global;
                if(buffs!=null && buffs.Buffs!=null && buffs.Buffs.Length>0 && stats!=null)
                {
                    // Armor lowers the chance to bleed and wears, as the game's hit does.
                    int lower=0;
                    for(int slot=1;slot<=2;slot++)
                    {
                        var slots=player.eqipmentInventory;
                        if(slots==null || slot>=slots.Length || slots[slot]==null || slots[slot].content==null || slots[slot].content.Count==0 || slots[slot].content[0]==null) continue;
                        var armor=slots[slot].content[0].GetComponent<Eqipment>();
                        if(armor==null || armor.ArmorDurability<=0) continue;
                        lower+=armor.decreaseChanceBleeding; armor.DamageArmor(damage);
                    }
                    foreach(var source in buffs.Buffs)
                    {
                        var buff=source;
                        if((int)buff.buffType==BleedingBuff) buff.chance-=(int)(buff.chance*(lower/100f));
                        stats.AddBuff(buff);
                    }
                }
            }
            catch(Exception ex) { EffectFailed("Melee hit buffs",ex); }
            try
            {
                var gui=Zompiercer.GUI.GUIManager.Global; if(gui!=null) gui.BloodScreen();
                var shake=player.GetComponentInChildren<CameraShake>(); if(shake!=null) shake.ShakeCamera(.1f,.1f);
            }
            catch(Exception ex) { EffectFailed("Melee hit screen",ex); }
        }
        private const int BleedingBuff=22;
        private bool _meleeFailed;
        private void EffectFailed(string what,Exception ex)
        {
            if(_meleeFailed) return;
            _meleeFailed=true; _report(what+" not shown: "+ex.GetType().Name+": "+ex.Message);
        }

        // Explicit remote branch: never replace GlobalManager.controlledChar and never construct a
        // pretend player controller that native inventory/buff scripts could dereference.
        private const float KeepRemoteSeconds=3f;
        private bool RemoteUpdate(ZombieAIController ai)
        {
            HostZombie h;
            if(!_host || !_ready || !_remoteReady || ai==null || ai.dead || !_hostIds.TryGetValue(ai.GetInstanceID(),out h)) return false;
            var local=LocalPlayer(); if(local==null) return false;
            if(RemoteDown) { if(h.Remote) ReleaseTarget(h); return false; }
            // 1.1.7: a boss waiting behind open arena gates does not take the guest either.
            if(LanBossArena.Holds(ai)) { if(h.Remote) ReleaseTarget(h); return false; }
            float distance=Vector3.Distance(ai.transform.position,_remote);
            bool eligible=(ai.melee || ai.cannon!=null && ai.cannon.projectile!=null || ai.RunToExplode || ai.flamethrower!=null) && !ai.zombieNurse;
            bool busy=ai.Staggered || ai.attackRayActivatorHunterAnimation || ai.doingFireAnimation || ai.skillChargeAttack!=null && (int)ai.skillChargeAttack.CurrentPhase!=0 || ai.flamethrower!=null && ai.flamethrower.flamethrower!=null && ai.flamethrower.flamethrower.Firing;
            float nearer=Vector3.Distance(ai.transform.position,local.transform.position), sight=SightOf(ai);
            // 1.4.13: a zombie that does not hunt the guest yet notices it as the game's zombies notice the
            // player (Spots, Hears); before, it saw the guest anywhere within its sight, crouching or not.
            // 1.4.14: as far as it sees now (the game's catch radius after a sighting, a noise or a wound), and a
            // zombie already on the hunt (chasing, the host in sight, someone heard) sees the guest in its sight
            // whatever the guest does, as the game's chasing zombie keeps the player.
            bool alerted=h.Remote || ai.state=="Chase" || ai.playerIsInSight || ai.aiHeardPlayer;
            bool sees=distance<sight && distance<nearer && (alerted || Spots(ai,distance,sight)) && ClearLine(ai,_remote);
            bool hears=!sees && !h.Remote && distance<nearer && Hears(ai,distance);
            // 1.0.2: a guest once seen stays the target for a few seconds. A single blocked ray (a bush
            // zombie in foliage) used to hand it back to the native AI, which hid it again, and the next
            // frame woke it for the guest: it hid and rose over and over while still attacking.
            if(eligible && (sees || hears)) h.KeepRemoteUntil=Time.time+KeepRemoteSeconds;
            bool hostTakes=h.Remote && !busy && HostTakes(ai,local,distance,nearer,sight);
            bool keep=h.Remote && !hostTakes && Time.time<h.KeepRemoteUntil && distance<Mathf.Min(60,Mathf.Max(3,ai.sightRadius*1.25f));
            bool choose=eligible && (h.Remote && busy || sees || hears || keep);
            if(!choose) { if(h.Remote) { ReleaseTarget(h); if(hostTakes) HandToHost(ai); } return false; }
            if(!h.Remote && (sees || hears) && Time.unscaledTime>=_noticeLogAt) {
                _noticeLogAt=Time.unscaledTime+5f;
                _report("Zombie "+h.Prefab+" noticed the guest: "+(sees?(alerted?"seen on the hunt":"seen"):"heard")+" at "+distance.ToString("0.0")+" m (sight "+sight.ToString("0")+
                    " m, noise "+ai.noiseDistance.ToString("0")+" m, field "+ai.fieldOfViewAngle.ToString("0")+"°, guest "+(RemoteRun?"running":RemoteAir?"in the air":RemoteCrouch?"crouching":RemoteSpeed>.3f?"walking":"standing")+
                    ", stealth x"+Mathf.Clamp(RemoteStealth,.1f,1f).ToString("0.0")+")");
            }
            h.Remote=true;ai.target=_target.transform;ai.DistanceToPlayer=distance;ai.UpdateDistanceToTarget();
            if(!ai.Staggered && (ai.navMeshAgent==null || ai.navMeshAgent.velocity.sqrMagnitude<.1f)) {
                var facing=_remote-ai.transform.position;facing.y=0;
                if(facing.sqrMagnitude>.001f) ai.transform.rotation=Quaternion.Slerp(ai.transform.rotation,Quaternion.LookRotation(facing),Mathf.Clamp01(Time.deltaTime*10));
            }
            ai.playerIsInSight=true;
            if(ai.sleeping) { ai.SetSleeping(false); return true; }
            if(ai.StaggeredTimer>0) { ai.StaggeredTimer-=Time.deltaTime;if(ai.StaggeredTimer<=0) ai.SetStaggered(false); }
            var charge=ai.skillChargeAttack;
            if(charge!=null) {
                var area=charge.repulsiveArea;
                if(area==null || !area.gameObject.activeInHierarchy) h.ChargeHit=false;
                else if(!h.ChargeHit && OverlapsRemote(area.gameObject) && ClearLine(ai,_remote)) { h.ChargeHit=true;Hurt(area.Damage);area.HitPlayer=true; }
                if((int)charge.CurrentPhase!=0) return true;
            }
            if(ai.WakesUp || ai.Staggered || (ai.zombieDamagDetector!=null && ai.zombieDamagDetector.stunAfterHittingCounter)) return true;
            var tongue=ai.skillFrogTongue;
            if(tongue!=null && tongue.cooldown!=null && tongue.cooldown.IsReady() && tongue.frogTongue!=null && distance<Mathf.Clamp(tongue.frogTongue.MaxRange,0,30) && ClearLine(ai,_remote)) { tongue.Fire();return true; }
            var jump=ai.skillJump;
            if(jump!=null && jump.cooldown!=null && jump.cooldown.IsReady() && distance>=jump.RangeMin && distance<=jump.RangeMax && Vector3.Angle(ai.transform.forward,_remote-ai.transform.position)<20 && ClearLine(ai,_remote)) jump.Jump();
            if(charge!=null && charge.UltaCooldownTimer<=0 && distance<Mathf.Clamp(ai.ChargeAttackRange,0,50) && Vector3.Angle(ai.transform.forward,_remote-ai.transform.position)<ai.ChargeAttackAngle && ClearLine(ai,_remote)) { charge.Charge(_target.transform);return true; }
            var flame=ai.flamethrower;
            if(flame!=null && flame.cooldown!=null && flame.cooldown.IsReady() && distance<=Mathf.Clamp(ai.fireRange,0,30) && distance>=ai.fireRangeMin && ClearLine(ai,_remote)) {
                int hand=ai.GetFireFromHandIndex();
                if(hand>=0 && ai.FirePosition!=null && hand<ai.FirePosition.Length && flame.animations!=null && hand<flame.animations.Length && flame.flamethrower!=null) {
                    flame.flamethrower.transform.SetParent(ai.FirePosition[hand],false);flame.flamethrower.transform.localPosition=Vector3.zero;flame.Fire();return true;
                }
            }
            if(ai.RunToExplode && ai.explodingBlob!=null && !ai.explodingBlob.Exploded && !h.Explosion && distance<=Mathf.Clamp(ai.ExplodeOnDistance,0,10)) {
                h.Explosion=true;ai.BeginStomachExplosion();return true;
            }
            if(ai.cannon!=null && ai.cannon.projectile!=null && ai.FireAnimations!=null && ai.FireAnimations.Length>0) { RangedAttack(ai,h,distance); return true; }
            if(ai.attackRayActivatorHunterAnimation) {
                ai.attackTimer+=Time.deltaTime;
                float delay=ai.attackRayActivator==2?ai.attackHunterHitMomentTime2:ai.OverrideAttackAnimationMode && ai.OverrideAttackAnimation!=null?ai.OverrideAttackAnimation.delay:ai.attackHunterHitMomentTime;
                if(ai.attackRayActivator>0 && ai.attackTimer>=delay) {
                    if(InMelee(ai,distance)) MeleeHit(ai);
                    ai.attackRayActivator=ai.zombieBroker && ai.attackRayActivator==1?2:0;
                }
                if(ai.attackTimer>=Mathf.Max(.5f,ai.OverrideAttackAnimationMode && ai.OverrideAttackAnimation!=null?ai.OverrideAttackAnimation.duration:ai.durationOfHunterAttack)) {
                    ai.attackRayActivatorHunterAnimation=false;ai.attackTimer=0;ai.attackRayActivator=0;h.NextAttack=Time.time+.15f;
                    if(ai.zombieAnimation!=null) ai.zombieAnimation.AttackAnimationVariants();
                }
                return true;
            }
            ai.SetState("Chase"); ai.SetNavMeshStop(false,true);ai.SetNavMeshAgentSpeed(Mathf.Max(.1f,ai.chaseSpeed),false);ai.SetNavMeshDestination(_remote);
            if(ai.melee && InMelee(ai,distance) && Time.time>=h.NextAttack) {
                ai.SetNavMeshStop(true,true); ai.attackRayActivatorHunterAnimation=true;ai.attackRayActivator=1;ai.attackTimer=0;
                if(ai.zombieEffects!=null) ai.zombieEffects.ZombieAttackRoarSound();
            }
            return true;
        }
        // 1.1.8: a spitting zombie (the bush zombie) plays its own ranged game against the guest, as the
        // game's AI does against the host: it walks up while its spit is ready and spits from the hand
        // whose turn it is once it faces the guest, keeps its distance while it reloads (runs aside or
        // away, its flee thresholds), and never stops in its waiting pose. That pose is the bush zombie
        // hiding in its bush: the old stop in range made it hide and rise over and over, and it spat
        // without turning, always the first hand's animation from whichever hand held the cannon.
        private void RangedAttack(ZombieAIController ai,HostZombie h,float distance)
        {
            bool ready=ai.cannon.IsReadyToFire() && !ai.doingFireAnimation;
            if(!ai.keepDistance) h.Fleeing=false;
            else if(distance<ai.FleeThresholdMin && !(ai.priorityOnFire && ready)) h.Fleeing=true;
            else if(distance>ai.FleeThresholdMax || ai.priorityOnFire && ready) h.Fleeing=false;
            ai.SetState("Chase"); ai.SetNavMeshStop(false,false);
            if(h.Fleeing) {
                if(Time.time>=h.FleeAt) {
                    h.FleeAt=Time.time+1f;
                    ai.SetNavMeshAgentSpeed(Mathf.Max(.1f,ai.fleeSpeed>0?ai.fleeSpeed:ai.chaseSpeed),false); ai.SetNavMeshDestination(FleePoint(ai));
                }
                return;
            }
            ai.SetNavMeshAgentSpeed(Mathf.Max(.1f,ai.chaseSpeed),false);
            if(!ready) return; // reloading: on toward where the guest was, as the game keeps its last destination
            ai.SetNavMeshDestination(_remote);
            var facing=_remote-ai.transform.position; facing.y=0;
            if(facing.sqrMagnitude<.001f || Vector3.Angle(ai.transform.forward,facing)>=20f || distance>Mathf.Clamp(ai.fireRange,0,100) || distance<ai.fireRangeMin || !ClearLine(ai,_remote)) return;
            int hand=ai.GetFireFromHandIndex();
            if(hand<0) return;
            ai.doingFireAnimation=true; ai.Fire(ai.FireAnimations[Math.Min(hand,ai.FireAnimations.Length-1)]);
            if(ai.FirePosition!=null && hand<ai.FirePosition.Length && ai.FirePosition[hand]!=null) { ai.cannon.transform.parent=ai.FirePosition[hand]; ai.cannon.transform.localPosition=Vector3.zero; }
            ai.CycleNextFirePosition();
        }
        // Where a fleeing ranged zombie runs: aside (its flee type Side) or away from the guest.
        private Vector3 FleePoint(ZombieAIController ai)
        {
            var self=ai.transform.position; Vector3 goal;
            if((int)ai.fleeType==1) goal=ai.transform.TransformPoint(new Vector3(UnityEngine.Random.value<.5f?-10f:10f,0,0));
            else { var away=self-_remote; away.y=0; goal=self+(away.sqrMagnitude>.01f?away.normalized:-ai.transform.forward)*10f; }
            UnityEngine.AI.NavMeshHit hit;
            return UnityEngine.AI.NavMesh.SamplePosition(goal,out hit,8f,UnityEngine.AI.NavMesh.AllAreas)?hit.position:self;
        }
        // 1.4.13: the game's own rules for noticing the player (ZombieAIController.CheckLOS), for the guest. Seen
        // within a share of the zombie's sight (half of it sleeping) by how the guest moves: a half walking or
        // running, a third standing or sneaking, a fifth crouching still, times its stealth; and only in front
        // of the zombie (its field of view). Heard within its noise distance: all of it running or jumping,
        // otherwise the same shares and stealth.
        private float StealthShare(bool hearing)
        {
            if(RemoteRun || RemoteAir) return hearing?1f:2f;
            bool moving=RemoteSpeed>.3f;
            return (RemoteCrouch ? moving?3f:5f : moving?2f:3f)/Mathf.Clamp(RemoteStealth,.1f,1f);
        }
        private bool Spots(ZombieAIController ai,float distance,float sight)
        {
            if(distance>=sight/StealthShare(false)) return false;
            return Vector3.Angle(_remote-ai.transform.position,ai.transform.forward)<Mathf.Clamp(ai.fieldOfViewAngle*.5f,1,180);
        }
        private bool Hears(ZombieAIController ai,float distance) { return distance<Mathf.Clamp(ai.noiseDistance,0,50)/StealthShare(true); }
        // 1.4.14: how far a zombie sees now, as the game's CheckLOS uses it (currentSightRadius: its sight radius,
        // half of it asleep at first, its catch radius once it saw, heard or was hurt).
        private static AccessTools.FieldRef<ZombieAIController,float> _currentSight;
        private static bool _currentSightFailed;
        private static float SightOf(ZombieAIController ai)
        {
            float sight=ai.sightRadius;
            if(!_currentSightFailed)
                try { if(_currentSight==null) _currentSight=AccessTools.FieldRefAccess<ZombieAIController,float>("currentSightRadius"); float current=_currentSight(ai); if(current>0) sight=current; }
                catch(Exception) { _currentSightFailed=true; }
            return Mathf.Clamp(sight,2,50);
        }

        // 1.4.14, host: the game wakes its zombies around the player only: a spawner makes its zombie once the
        // player's character comes within its reach (ZombieSpawn.Update: 60, 20, 7 or 3 m by its spawnPlace), a
        // territory (ZombieSpawnerTerritoryActivator) turns its spawners on when the player's CharacterController
        // enters it. Around the guest alone the world stayed empty until the host came; the guest wakes them too.
        private static readonly float[] SpawnReach={60f,20f,7f,3f};
        private void WakeAroundRemote()
        {
            float now=Time.time;
            if(now<_wakeAt) return;
            _wakeAt=now+.5f;
            try {
                var saves=Zompiercer.SaveLoad.SaveLoadCore.global;
                var local=LocalPlayer();
                if(saves!=null && saves.LoadingNow || local==null || GlobalManager.AllZombieSpawners==null) return;
                foreach(var spawn in GlobalManager.AllZombieSpawners.ToArray()) {
                    if(spawn==null || spawn.GoingToBeDestroyed || !spawn.isActiveAndEnabled || spawn.spawnPlace<0 || spawn.spawnPlace>=SpawnReach.Length) continue;
                    float reach=SpawnReach[spawn.spawnPlace];
                    // Within the host's reach the game's own Update makes it (never twice in a frame).
                    if(Vector3.Distance(spawn.transform.position,local.transform.position)<=reach || Vector3.Distance(spawn.transform.position,_remote)>reach) continue;
                    spawn.Activator();
                }
                if(now>=_territoriesAt) { _territoriesAt=now+5f; _territories=Object.FindObjectsOfType<ZombieSpawnerTerritoryActivator>(); }
                if(_territories==null) return;
                foreach(var territory in _territories) {
                    if(territory==null || !territory.isActiveAndEnabled || territory.zombieSpawners==null || !Inside(territory,_remote)) continue;
                    foreach(var spawner in territory.zombieSpawners) if(spawner!=null && !spawner.activeSelf) spawner.SetActive(true);
                }
            }
            catch(Exception ex) { if(!_wakeFailed) { _wakeFailed=true; _report("Zombies around the guest not woken: "+ex.GetType().Name+": "+ex.Message); } }
        }
        // Whether the guest stands in a territory's trigger (its body: a point a metre above its feet).
        private static bool Inside(Component territory,Vector3 feet)
        {
            var body=feet+Vector3.up;
            foreach(var collider in territory.GetComponents<Collider>()) {
                if(collider==null || !collider.enabled || !collider.isTrigger || !collider.bounds.Contains(body)) continue;
                var mesh=collider as MeshCollider;
                if(mesh!=null && !mesh.convex || (collider.ClosestPoint(body)-body).sqrMagnitude<1e-4f) return true;
            }
            return false;
        }
        // 1.4.16: a zombie hunting a guest goes for the host at once when the host is nearer (by a metre), in its
        // sight and in a clear line, as the game's zombie goes for the player (AttackPlayer). Let go, it ran on to
        // where the guest had been and took the host only if it happened to notice it.
        private static bool HostTakes(ZombieAIController ai,ZombieFighterController local,float guestDistance,float hostDistance,float sight)
        {
            if(local==null || hostDistance+1f>=guestDistance || hostDistance>=sight) return false;
            var bar=local.zombieFighterIndicatorsBar;
            if(bar!=null && bar.deathRegister) return false;
            return ClearLine(ai,local.transform.position);
        }
        private static void HandToHost(ZombieAIController ai) { try { ai.AttackPlayer(); } catch(Exception) { } }
        private bool InMelee(ZombieAIController ai,float distance)
        {
            float range=ai.IsHaveHandForMelee()?ai.attackDistance:ai.attackDistanceTornOff;
            return distance<=Mathf.Clamp(range,0,4) && Vector3.Angle(ai.transform.forward,_remote-ai.transform.position)<Mathf.Clamp(ai.fieldOfAttackAngle*.5f,1,90) && ClearLine(ai,_remote);
        }
        private static bool ClearLine(ZombieAIController ai,Vector3 target)
        {
            var start=ai.transform.position+Vector3.up;var delta=target+Vector3.up-start;
            var hits=Physics.RaycastAll(start,delta.normalized,delta.magnitude,~0,QueryTriggerInteraction.Ignore);
            foreach(var hit in hits) if(hit.collider.GetComponentInParent<ZombieAIController>()!=ai && hit.collider.GetComponentInParent<ZombieFighterController>()==null) return false;
            return true;
        }
        private static bool BeforeProjectile(Projectile __instance)
        {
            // 1.1.6: on the guest only the host's spits fly (ShowEffect): the native flight and splash,
            // without the native damage (the host books it).
            if(IsClientWorld) { VisualProjectile(__instance); return false; }
            var a=Active;if(a==null || !a._host || !a._ready || !a._remoteReady) return true;
            // Same host projectile trajectory and wall occlusion, with an analytic remote body.
            var start=__instance.transform.TransformPoint(new Vector3(0,0,__instance.RayOffsetZ));
            float length=Mathf.Clamp(__instance.Speed*Time.fixedDeltaTime,0,30);
            if(length<=0) return true;
            var direction=__instance.transform.forward; var center=a._remote+Vector3.up;
            float along=Mathf.Clamp(Vector3.Dot(center-start,direction),0,length);
            if(Vector3.Distance(start+direction*along,center)>.5f) return true;
            RaycastHit wall;
            if(Physics.Raycast(start,direction,out wall,along,~0,QueryTriggerInteraction.Ignore)) return true;
            a.Hurt(__instance.Damage);
            // 1.1.6: the splash where it hit the guest, as the native hit leaves it.
            if(__instance.ImpactEffect!=null) Object.Instantiate(__instance.ImpactEffect,start+direction*along,__instance.transform.rotation);
            if(__instance.Trail!=null) { __instance.Trail.parent=null; Object.Destroy(__instance.Trail.gameObject,5f); }
            __instance.enabled=false;Object.Destroy(__instance.gameObject);return false;
        }
        private static void VisualProjectile(Projectile round)
        {
            float step=Mathf.Clamp(round.Speed*Time.fixedDeltaTime,0,30);
            round.transform.Translate(Vector3.forward*step);
            if(step<=0) return;
            var start=round.transform.TransformPoint(new Vector3(0,0,round.RayOffsetZ));
            int mask=GlobalManager.global==null?~0:GlobalManager.global.LayerMaskEnemyProjectiles.value;
            RaycastHit hit;
            if(!Physics.Raycast(new Ray(start,round.transform.forward),out hit,step,mask) || hit.collider==null) return;
            if(round.ImpactEffect!=null) Object.Instantiate(round.ImpactEffect,hit.point,round.transform.rotation);
            if(round.Trail!=null) { round.Trail.parent=null; Object.Destroy(round.Trail.gameObject,5f); }
            round.enabled=false; Object.Destroy(round.gameObject);
        }
        private bool OverlapsRemote(GameObject area)
        {
            foreach(var collider in area.GetComponentsInChildren<Collider>())
                if(collider.enabled && collider.gameObject.activeInHierarchy && (collider.ClosestPoint(_remote+Vector3.up)-(_remote+Vector3.up)).sqrMagnitude<=.1225f) return true;
            return false;
        }
        private static void AfterFlame(Flame __instance)
        {
            var a=Active;if(a==null || !a._host || !a._ready || !a._remoteReady || !a.OverlapsRemote(__instance.gameObject)) return;
            var start=__instance.transform.position;var delta=a._remote+Vector3.up-start;
            if(!Physics.Raycast(start,delta.normalized,delta.magnitude,~0,QueryTriggerInteraction.Ignore)) a.Hurt(__instance.Damage*__instance.DamageFactorPlayer*Time.fixedDeltaTime,2);
        }
        private static void BeforeSmooth(SmoothDirection __instance)
        {
            var a=Active;if(a==null || !a._host || !a._remoteReady) return;
            var ai=__instance.GetComponentInParent<ZombieAIController>();HostZombie h;
            if(ai!=null && a._hostIds.TryGetValue(ai.GetInstanceID(),out h) && h.Remote) __instance.Target=a._target.transform;
        }
        private static bool BeforeAim(ZombieAIController __instance,ref Vector3 __result)
        {
            var a=Active;HostZombie h;
            if(a==null || !a._host || !a._remoteReady || !a._hostIds.TryGetValue(__instance.GetInstanceID(),out h) || !h.Remote) return true;
            __result=a._remote+Vector3.up;return false;
        }
        private static bool BeforeTongueHit(ItemStealer __instance)
        {
            var a=Active;if(a==null || !a._host || !a._ready || !a._remoteReady || __instance.HitSomething) return true;
            var start=__instance.transform.position;var direction=__instance.transform.forward;
            float along=Vector3.Dot(a._remote+Vector3.up-start,direction);
            if(along<0 || along>Mathf.Clamp(__instance.RayDistance,0,10) || Vector3.Distance(start+direction*along,a._remote+Vector3.up)>.5f) return true;
            if(Physics.Raycast(start,direction,along,~0,QueryTriggerInteraction.Ignore)) return true;
            a.Hurt(__instance.Damage);__instance.HitSomething=true;
            return false; // Damage only. Remote inventory theft deliberately awaits an inventory protocol.
        }
        private static void BeforeExplosion(ExplodingBlob __instance,out bool __state) { __state=__instance.Exploded; }
        private static void AfterExplosion(ExplodingBlob __instance,bool __state)
        {
            var a=Active;if(__state || a==null || !a._host) return;
            var ai=__instance.GetComponentInParent<ZombieAIController>(); if(ai==null || !__instance.Exploded) return;
            // 1.1.6: the guest sees the stomach burst too.
            a.SendEffect(ai,LanProtocol.EffectBurst,__instance.ExplosionEffectSpawnPoint!=null?__instance.ExplosionEffectSpawnPoint.position:__instance.transform.position,Vector3.up);
            if(!a._remoteReady) return;
            if(Vector3.Distance(__instance.transform.position,a._remote)<=Mathf.Clamp(__instance.Radius,0,30) && ClearLine(ai,a._remote)) a.Hurt(__instance.Damage,3);
        }
        // 1.1.8: the nurse threw its healing smoke (ShotAndReloading: the sphere and the mucus spray).
        private static void BeforeThrow(ZombieAIController __instance,out bool __state) { __state=__instance.weaponsShot; }
        private static void AfterThrow(ZombieAIController __instance,bool __state)
        {
            var a=Active; if(__state || a==null || !a._host || !__instance.weaponsShot || __instance.bulletPoint==null) return;
            try { a.SendEffect(__instance,LanProtocol.EffectThrow,__instance.bulletPoint.position,__instance.bulletPoint.forward); }
            catch(Exception ex) { if(!a._effectFailed) { a._effectFailed=true; a._report("Zombie effect not sent: "+ex.GetType().Name+": "+ex.Message); } }
        }
        // 1.1.6: a host zombie spat (Cannon.FireOneShot): the same projectile flies for the guest.
        private static void AfterCannonShot(Cannon __instance)
        {
            var a=Active; if(a==null || !a._host || __instance==null || __instance.projectile==null || __instance.FirePoint==null) return;
            try { a.SendEffect(__instance.GetComponentInParent<ZombieAIController>(),LanProtocol.EffectShot,__instance.FirePoint.position,__instance.FirePoint.forward); }
            catch(Exception ex) { if(!a._effectFailed) { a._effectFailed=true; a._report("Zombie effect not sent: "+ex.GetType().Name+": "+ex.Message); } }
        }
        private void SendEffect(ZombieAIController ai,byte action,Vector3 at,Vector3 facing)
        {
            HostZombie h;
            if(!_host || !_ready || ai==null || !_hostIds.TryGetValue(ai.GetInstanceID(),out h) || (at-_remote).sqrMagnitude>EffectRange*EffectRange) return;
            if(facing.sqrMagnitude<.0001f) facing=Vector3.forward;
            facing.Normalize();
            var p=Message(PacketKind.ZombieEffect,++_effectSequence); p.Revision=(int)h.Id; p.Action=action;
            p.X=at.x;p.Y=at.y;p.Z=at.z;p.AimX=facing.x;p.AimY=facing.y;p.AimZ=facing.z; _send(p);
        }
        // Guest: the host zombie's spit or burst, from the local prefab of the zombie the copy shows.
        // A spit flies as a visual only (BeforeProjectile): the host books its hits on the guest.
        private void ShowEffect(Packet p)
        {
            Replica r; ZombieAIController prefab;
            var player=LocalPlayer();
            // A burst may come after the frame that killed the zombie: its copy may already be a corpse.
            if(player==null || !_replicas.TryGetValue((uint)p.Revision,out r) || r.Root==null || r.Dead && p.Action!=LanProtocol.EffectBurst || !_prefabs.TryGetValue(r.Prefab,out prefab) || prefab==null) return;
            var at=new Vector3(p.X,p.Y,p.Z);
            if((at-player.transform.position).sqrMagnitude>EffectRange*EffectRange) return;
            float volume=GlobalSoundEffects.global==null?1f:Mathf.Clamp01(GlobalSoundEffects.global.EffectsVolume);
            try {
                // 1.1.8: the nurse's healing smoke: the same sphere flies and bursts into the yellow cloud here
                // (whose debuff reaches this player when it stands in it, as the game does to the host).
                if(p.Action==LanProtocol.EffectThrow) {
                    if(prefab.bullet==null) return;
                    var rotation=Quaternion.LookRotation(new Vector3(p.AimX,p.AimY,p.AimZ));
                    Object.Instantiate(prefab.bullet,at,rotation).AddRelativeForce(Vector3.forward*prefab.bulletSpeed,ForceMode.VelocityChange);
                    var effects=prefab.zombieEffects!=null?prefab.zombieEffects:prefab.GetComponentInChildren<ZombieEffects>(true);
                    if(effects!=null && effects.zombieNurseMucusSprays!=null) Object.Instantiate(effects.zombieNurseMucusSprays,at,rotation);
                    return;
                }
                if(p.Action==LanProtocol.EffectShot) {
                    var cannon=prefab.cannon!=null?prefab.cannon:prefab.GetComponentInChildren<Cannon>(true);
                    if(cannon==null || cannon.projectile==null) return;
                    var round=Object.Instantiate(cannon.projectile,at,Quaternion.LookRotation(new Vector3(p.AimX,p.AimY,p.AimZ)));
                    if(cannon.ApplyForce>0) { var body=round.GetComponent<Rigidbody>(); if(body!=null) body.velocity=round.transform.forward*cannon.ApplyForce; }
                    var source=cannon.GetComponent<AudioSource>();
                    if(cannon.AudioShot!=null) AudioSource.PlayClipAtPoint(cannon.AudioShot,at,(source==null?1f:source.volume)*volume);
                    return;
                }
                var blob=prefab.explodingBlob!=null?prefab.explodingBlob:prefab.GetComponent<ExplodingBlob>();
                if(blob==null) return;
                if(blob.ExplosionEffect!=null) Object.Instantiate(blob.ExplosionEffect,at,Quaternion.identity);
                if(prefab.ExplosionAudio!=null) AudioSource.PlayClipAtPoint(prefab.ExplosionAudio,at,volume);
                if(blob.ExplodedPart!=null)
                    foreach(var t in r.Root.GetComponentsInChildren<Transform>(true)) if(t.name==blob.ExplodedPart.name) t.gameObject.SetActive(false);
                // The game's own splash on a player close by: dirt on the screen and a short slowdown.
                if(Vector3.Distance(r.Root.transform.position,player.transform.position)<blob.Radius) {
                    var gui=GlobalManager.global==null?null:GlobalManager.global.canvas;
                    if(gui!=null && gui.ScreenEffectZombieDirt!=null) gui.ScreenEffectZombieDirt.Show(blob.DirtDuration);
                    if(player.statusEffectsController!=null) {
                        var slow=player.gameObject.AddComponent<StatusEffect>();
                        slow.Name="Slow down"; slow.ValueFloat=blob.SlowDownFactor; slow.Duration=blob.SlowDownDuration; slow.ModifyMoveSpeed=true;
                        player.statusEffectsController.Add(slow);
                    }
                }
            }
            catch(Exception ex) { if(!_effectFailed) { _effectFailed=true; _report("Zombie effect not shown: "+ex.GetType().Name+": "+ex.Message); } }
        }
        private void ReleaseTarget(HostZombie h)
        {
            bool canceledExplosion=h.Explosion;h.AttackGeneration++;h.Explosion=false;
            if(h.AI!=null) {
                if(_target!=null && h.AI.target==_target.transform) h.AI.target=LocalPlayer()==null?null:LocalPlayer().transform;
                foreach(var direction in h.AI.GetComponentsInChildren<SmoothDirection>(true))
                    if(_target!=null && direction.Target==_target.transform) direction.Target=LocalPlayer()==null?null:LocalPlayer().transform;
                if(h.AI.skillChargeAttack!=null && (int)h.AI.skillChargeAttack.CurrentPhase!=0) h.AI.skillChargeAttack.StopCharge();
                if(h.AI.flamethrower!=null) {
                    h.AI.flamethrower.StopFire();
                    if(h.AI.zombieAnimation!=null && h.AI.flamethrower.animations!=null)
                        foreach(var animation in h.AI.flamethrower.animations)
                            if(animation!=null) h.AI.zombieAnimation.TripleAnimationOff(animation);
                }
                if(h.AI.skillFrogTongue!=null) h.AI.skillFrogTongue.StopFire();
                h.AI.doingFireAnimation=false;h.AI.WiggleOnDamage=true;
                if(canceledExplosion && h.AI.zombieAnimation!=null && h.AI.zombieAnimation.animator!=null)
                    h.AI.zombieAnimation.animator.SetBool("bellyExplosion",false);
                h.AI.attackRayActivatorHunterAnimation=false;h.AI.attackRayActivator=0;h.AI.attackTimer=0;h.AI.SetNavMeshStop(false,true);
            }
            h.Remote=false;h.Fleeing=false;
        }
        private void ReleaseTargets() { foreach(var h in _catalog) if(h.Remote) ReleaseTarget(h); }
        internal void Freeze() { _ready=false;_remoteReady=false;_playerReady=false;Combat.Freeze();ReleaseTargets();foreach(var r in _replicas.Values) if(r.Root!=null) r.Root.SetActive(false); }
        private void ResetWorld(string scene,uint epoch)
        {
            ReleaseTargets(); foreach(var r in _replicas.Values) if(r.Root!=null) Object.Destroy(r.Root);
            _replicas.Clear();_hostIds.Clear();_catalog.Clear();_failedPrefabs.Clear();_unknownPrefabs.Clear();_hurts.Clear();Array.Clear(_seen,0,_seen.Length);Array.Clear(_dead,0,_dead.Length);Array.Clear(_corpseSeen,0,_corpseSeen.Length);_corpseSequence=0;
            _sentFrames=0;_receivedFrames=0;_createdReplicas=0;_diagnosticAt=0;
            Array.Clear(_pendingFrames,0,_pendingFrames.Length);Array.Clear(_pendingSequences,0,_pendingSequences.Length);Array.Clear(_pendingDamage,0,_pendingDamage.Length);
            Array.Clear(_pendingTimes,0,_pendingTimes.Length);_haveClock=false;_clockOffset=0;_jitter=0;
            _pendingCount=0;_applyCursor=1;
            _scene=scene;_epoch=epoch;_cursor=0;_sequence=0;_lastAction=0;_actionSequence=0;_hurtSequence=0;_lastHurt=0;_scanAt=0;_sendAt=0;_nextAction=0;
            _effectSequence=0;_lastEffect=0;
            Combat.Rebind(scene,epoch);
        }
        public void Dispose() { if(_disposed)return;Freeze();ResetWorld(null,0);_disposed=true;if(_target!=null)Object.Destroy(_target);if(Active==this)Active=null; }

        private static void InstallGuard()
        {
            if(Guard!=null) return;
            var harmony=new Harmony("local.zompiercer.lan.zombies");
            try {
                Patch(harmony,typeof(ZombieAIController),"Update",nameof(AIUpdate));
                Patch(harmony,typeof(ZombieAIController),"FixedUpdate",nameof(AIFixed));
                foreach(var type in new[]{typeof(ZombieSpawn),typeof(ZombieDeadSpawn)}) Patch(harmony,type,"Activator",nameof(AllowNative));
                foreach(var method in new[]{"Awake","Start"}) Patch(harmony,typeof(ZombieAIController),method,nameof(ClientZombie));
                foreach(var type in new[]{typeof(ZombieHP),typeof(ZombieDamagDetector),typeof(ZombieTrainDetector)})
                    foreach(var method in new[]{"Update","CauseDamage","OnTriggerEnter","Dead"}) if(HasMethod(type,method)) Patch(harmony,type,method,nameof(AllowNative));
                harmony.Patch(AccessTools.Method(typeof(Gun),"Shot"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeShot)),new HarmonyMethod(typeof(ZombieSync),nameof(AfterShot)));
                harmony.Patch(AccessTools.Method(typeof(Gun),"MeleeStrike"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeMelee)),new HarmonyMethod(typeof(ZombieSync),nameof(AfterMelee)));
                harmony.Patch(AccessTools.Method(typeof(Gun),"MeleeHitTarget"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeMeleeHit)));
                harmony.Patch(AccessTools.Method(typeof(Gun),"ReloadingActivate"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeReload)));
                Patch(harmony,typeof(Gun),"RechargeSuitableRounds",nameof(BeforeRecharge));
                harmony.Patch(AccessTools.Method(typeof(Gun),"MagazineInserted"),prefix:new HarmonyMethod(typeof(ZombieSync),nameof(BeforeInserted)),
                    postfix:new HarmonyMethod(typeof(ZombieSync),nameof(AfterInserted)),finalizer:new HarmonyMethod(typeof(ZombieSync),nameof(InsertedDone)));
                harmony.Patch(AccessTools.Method(typeof(Inventory),"Extract",new[]{typeof(InventoryItem),typeof(int)}),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeExtract)));
                Patch(harmony,typeof(Projectile),"FixedUpdate",nameof(BeforeProjectile));
                harmony.Patch(AccessTools.Method(typeof(Flame),"FixedUpdate"),null,new HarmonyMethod(typeof(ZombieSync),nameof(AfterFlame)));
                Patch(harmony,typeof(SmoothDirection),"Update",nameof(BeforeSmooth));
                Patch(harmony,typeof(ZombieAIController),"GetPlayerPositionAim",nameof(BeforeAim));
                Patch(harmony,typeof(ItemStealer),"FixedUpdate",nameof(BeforeTongueHit));
                harmony.Patch(AccessTools.Method(typeof(ExplodingBlob),"Explode"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeExplosion)),new HarmonyMethod(typeof(ZombieSync),nameof(AfterExplosion)));
                harmony.Patch(AccessTools.Method(typeof(Cannon),"FireOneShot"),null,new HarmonyMethod(typeof(ZombieSync),nameof(AfterCannonShot)));
                harmony.Patch(AccessTools.Method(typeof(ZombieAIController),"ShotAndReloading"),new HarmonyMethod(typeof(ZombieSync),nameof(BeforeThrow)),new HarmonyMethod(typeof(ZombieSync),nameof(AfterThrow)));
                foreach(var type in new[]{typeof(ZombieAIController),typeof(SkillFrogTongue),typeof(SkillFlamethrower),typeof(SkillChargeAttack)})
                    foreach(var method in AccessTools.GetDeclaredMethods(type))
                        if(method.ReturnType==typeof(IEnumerator) && (method.Name=="FireIE" || method.Name=="ChargeIE" ||
                            method.Name=="DoEndHitIE" || method.Name=="ActivateRepulserIE" || method.Name=="BeginStomachExplosionIE"))
                            harmony.Patch(method,null,new HarmonyMethod(typeof(ZombieSync),nameof(OwnAttackRoutine)));
                Guard=harmony;
            } catch { harmony.UnpatchSelf();throw; }
        }
        // Optional native methods: a quiet lookup (AccessTools.Method logs a warning for each absent one).
        private static bool HasMethod(Type type,string name)
        {
            for(var t=type;t!=null;t=t.BaseType)
                foreach(var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) if(m.Name==name) return true;
            return false;
        }
        private static void Patch(Harmony h,Type type,string method,string prefix) { h.Patch(AccessTools.Method(type,method),new HarmonyMethod(typeof(ZombieSync),prefix)); }
        private static bool AllowNative() { return !IsClientWorld; }
        private static bool ClientZombie(ZombieAIController __instance) { if(!IsClientWorld)return true;__instance.gameObject.SetActive(false);return false; }
        private static bool AIUpdate(ZombieAIController __instance) { if(IsClientWorld) { __instance.gameObject.SetActive(false);return false; } return Active==null || !Active.RemoteUpdate(__instance); }
        private static bool AIFixed(ZombieAIController __instance) { if(IsClientWorld)return false;HostZombie h;return Active==null || !Active._hostIds.TryGetValue(__instance.GetInstanceID(),out h) || !h.Remote; }
        private static void OwnAttackRoutine(Component __instance,ref IEnumerator __result)
        {
            var owner=Active;HostZombie zombie;
            var ai=__instance.GetComponentInParent<ZombieAIController>();
            if(owner!=null && owner._host && ai!=null && owner._hostIds.TryGetValue(ai.GetInstanceID(),out zombie) && zombie.Remote)
                __result=GuardAttack(__result,owner,zombie,zombie.AttackGeneration);
        }
        private static IEnumerator GuardAttack(IEnumerator routine,ZombieSync owner,HostZombie zombie,int generation)
        {
            try {
                while(!owner._disposed && owner._ready && owner._remoteReady && zombie.Remote && zombie.AttackGeneration==generation && routine.MoveNext()) {
                    var nested=routine.Current as IEnumerator;
                    yield return nested==null?routine.Current:GuardAttack(nested,owner,zombie,generation);
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }
        private static bool CanClientUse(Gun gun,bool reload=false) { return !IsClientWorld || Active!=null && !Active._host && Active._playerReady && Active.Combat.CanUse(gun,reload); }
        private static bool BeforeShot(Gun __instance,out int __state) { __state=__instance.magazine; if(CanClientUse(__instance)) return true; Refused("Shot",__instance,false); return false; }
        private static void AfterShot(Gun __instance,int __state) { if(Active!=null && __instance.magazine<__state) Active.CaptureAction(__instance,0); }
        private static bool BeforeMelee(Gun __instance,out bool __state) { __state=__instance.meleeAttack; if(CanClientUse(__instance)) return true; Refused("Blow",__instance,false); return false; }
        private static void AfterMelee(Gun __instance,bool __state) { if(Active!=null && !__state && __instance.meleeAttack) Active.CaptureAction(__instance,1); }
        private static bool BeforeReload(Gun __instance)
        {
            if(!IsClientWorld) return true;
            if(__instance.reloading) return false;
            if(!CanClientUse(__instance,true)) { Refused("Reload",__instance,true); return false; }
            // 1.0.6: the host books the reload; the native one still runs for the first-person
            // animation, with its rounds transfer suppressed below (no reserve spent twice).
            Active.CaptureReload(__instance);
            return true;
        }
        // 1.4.16, guest: why a blow, a shot or a reload is refused, at most every 10 seconds (it was refused
        // without a word: no blows at all, not even with the fists, and a rifle that would not reload).
        private static float _refusedLogAt;
        private static void Refused(string what,Gun gun,bool reload)
        {
            if(!IsClientWorld || Active==null || Time.unscaledTime<_refusedLogAt) return;
            _refusedLogAt=Time.unscaledTime+10f;
            Active._report(what+" refused: "+(!Active._playerReady?"the player is not placed in the host's world yet":Active.Combat.Refusal(gun,reload)));
        }
        // Guest: animation events must not move rounds; the magazine and reserve come from the host.
        private static bool _suppressExtract;
        private static bool BeforeRecharge() { return !IsClientWorld; }
        private static bool BeforeExtract() { return !_suppressExtract; }
        private static void BeforeInserted(Gun __instance,out int __state) { __state=__instance.magazine; _suppressExtract=IsClientWorld; }
        private static void AfterInserted(Gun __instance,int __state)
        {
            if(!IsClientWorld || !__instance.IncrementalReloading) return;
            __instance.magazine=__state;
            // One cartridge per event loops until the native magazine fills, which it no longer
            // does here: end the loop once the host has finished (or refused) the reload.
            if(Active==null || !Active.Combat.ReloadPending) __instance.weaponAnimation.SetBool("reload",false);
        }
        private static Exception InsertedDone(Exception __exception) { _suppressExtract=false; return __exception; }
    }
}
