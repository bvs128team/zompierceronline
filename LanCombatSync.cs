using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Zompiercer.Inventory;

namespace ZompiercerLAN
{
    // Combat of the remote player. Since 0.7.0 the host takes the allowance from its
    // ledger of the guest's belongings and books every shot, swing and reload back
    // into it; the guest no longer proposes a kit.
    internal sealed class LanCombatSync
    {
        private readonly bool _host;
        private readonly HostCombatInventory _ledger;
        private readonly Action<Packet> _send;
        private string _scene;
        private uint _epoch, _stateSequence, _lastState, _processed, _appliedState;
        private float _sendAt, _retryAt, _lastAttack=-1000f;
        private Packet _state;
        // Guest (1.0.1): actions sent but not yet processed by the host, oldest first. A few may
        // be in flight at once, so an automatic weapon is not limited to one shot per round trip;
        // the host still books every one of them against its ledger and rate.
        private readonly List<Packet> _inflight=new List<Packet>();
        internal const int MaxInFlight=40; // a full magazine of an automatic weapon (1.0.2)
        private bool _ready;
        // Host: the guest's ledger while it is usable, otherwise null.
        internal Func<LanGuestLedger> Ledger;
        // Guest: the last combat state applied locally (acknowledged in uploaded states).
        internal uint AppliedState { get { return _appliedState; } }
        internal LanCombatSync(bool host, HostCombatInventory ledger, Action<Packet> send)
        {
            _host=host;_ledger=ledger;_send=send;
            if(!host) return;
            ledger.Spent=(item,action)=>{
                var book=Ledger==null?null:Ledger();if(book==null)return;
                if(action==0) book.SpendRound(GunIndex(item));
                book.Wear(item,1f);
            };
            ledger.Reloaded=(item,ammo,count)=>{
                var book=Ledger==null?null:Ledger();if(book==null)return;
                // The rounds leave the reserve now; the magazine is pending until the
                // guest has applied the next combat state, which carries it.
                book.Remove(ammo,count);book.GiveMagazine(LanGuestLedger.CombatChannel,_stateSequence+1,GunIndex(item),count);
            };
        }
        internal int Revision { get { return _host ? _ledger.Revision : _state==null ? 0 : _state.Revision; } }
        internal void Rebind(string scene, uint epoch)
        {
            _scene=scene;_epoch=epoch;_stateSequence=0;_lastState=0;_processed=0;_sendAt=0;
            _state=null;_inflight.Clear();_ready=false;_appliedState=0;_lastAttack=-1000f;
            if(_host) { _ledger.Rebind();var book=Ledger==null?null:Ledger();if(book!=null) book.AckAll(LanGuestLedger.CombatChannel); }
        }
        internal void Freeze() { _ready=false;_inflight.Clear(); if(_host) _ledger.Rebind(); }
        internal void Tick(bool ready)
        {
            bool wasReady=_ready;
            _ready=ready && _epoch!=0;
            if(!_ready) { if(wasReady && _host) _ledger.CancelReload();return; }
            float now=Time.unscaledTime;
            if(_host) {
                _ledger.Tick(now);
                if(now>=_sendAt) { _sendAt=now+.1f;SyncFromLedger(now);SendState(); }
            } else {
                // One retry per period (the oldest): the host acknowledges cumulatively, and
                // retries stay within the action budget next to the fire rate.
                if(_inflight.Count!=0 && now>=_retryAt) { _retryAt=now+.2f;_send(_inflight[0]); }
            }
        }
        internal void Receive(Packet p)
        {
            if(!_ready || p.WorldEpoch!=_epoch || p.Scene!=_scene) return;
            if(!_host && p.Kind==PacketKind.CombatState) {
                if(!Newer(p.Sequence,_lastState)) return;
                _lastState=p.Sequence;
                // Processed actions leave (an action the host skipped is covered by a later one);
                // a new allowance or a refusal drops them all.
                int before=_inflight.Count;
                _inflight.RemoveAll(a => p.Action!=1 || a.Revision!=p.Revision || !Newer(a.Sequence,p.ShotSequence));
                if(_inflight.Count!=before) _retryAt=Time.unscaledTime+.2f;
                _state=p;
                if(p.Action==1) { ApplyLocal(p,_inflight);_appliedState=p.Sequence; }
            }
        }
        internal void Processed(uint sequence) { _processed=sequence;_sendAt=0; }
        internal bool Spend(Packet p)
        {
            if(p.EquippedItemId==-1) { var fist=FistRule(FistGun()); return p.Action==1 && fist!=null && _ledger.TryFist(p.Sequence,p.Revision,fist.Interval,fist.StaminaCost,Time.unscaledTime); }
            return _ledger.TryAttack(p.Sequence,p.Revision,p.EquippedItemId,p.Action,Time.unscaledTime);
        }
        internal void Reload(Packet p) { _ledger.TryReload(p.Sequence,p.Revision,p.EquippedItemId,Time.unscaledTime); }
        // 1.4.16, guest: why CanUse refuses a blow, a shot or a reload (for the log; it refused silently).
        internal string Refusal(Gun gun, bool reload)
        {
            if(!_ready || _state==null) return "no combat state from the host yet";
            if(_state.Action!=1 || _state.EquippedItemId!=-1) return "combat is not allowed by the host";
            if(gun==null) return "no weapon in hand";
            if(_inflight.Count>=MaxInFlight) return _inflight.Count+" actions still being booked by the host (the most it takes)";
            foreach(var a in _inflight) if(a.Kind==PacketKind.CombatReload) return "the last reload is still being booked by the host";
            if(reload && _inflight.Count!=0) return _inflight.Count+" action(s) still being booked by the host";
            int id=RemoteEquipment.CaptureLocalItemId();
            if(gun.Hands) {
                var fist=FistRule(gun);
                if(reload) return "bare hands";
                if(id!=-1) return "bare hands while item "+id+" is in hand";
                if(fist==null) return "no rule for the bare hands";
                if(Time.unscaledTime-_lastAttack<fist.Interval*.9f) return "too soon after the last blow";
                return "not enough stamina on the host's record ("+_state.Stamina.ToString("0")+", a blow takes "+fist.StaminaCost.ToString("0")+")";
            }
            var rule=Rule(id);
            if(rule==null) return "no combat rule for item "+id;
            if(rule.Action==0 && gun.weaponType!=0 || rule.Action==1 && gun.weaponType!=2) return "item "+id+" does not match the weapon in hand";
            var ammo=new List<string>(); foreach(var a in _state.CombatAmmo) ammo.Add(a.ItemId+" x"+a.Amount);
            foreach(var w in _state.CombatWeapons) if(w.ItemId==id) {
                if(w.Durability<=0) return "item "+id+" is broken on the host's record";
                if(reload) {
                    if(rule.Action!=0) return "item "+id+" is not a firearm";
                    if(w.Magazine>=rule.Capacity) return "the magazine is full on the host's record ("+w.Magazine+"/"+rule.Capacity+")";
                    return "no rounds of item "+rule.AmmoId+" on the host's record (it has: "+(ammo.Count==0?"none":string.Join(", ",ammo.ToArray()))+")";
                }
                if(Time.unscaledTime-_lastAttack<rule.Interval*.9f) return "too soon after the last attack";
                return rule.Action==0 ? "the magazine is empty on the host's record" : "not enough stamina on the host's record ("+_state.Stamina.ToString("0")+")";
            }
            var weapons=new List<string>(); foreach(var w in _state.CombatWeapons) weapons.Add(w.ItemId.ToString());
            return "item "+id+" is not among this player's weapons on the host's record ("+(weapons.Count==0?"none":string.Join(", ",weapons.ToArray()))+")";
        }

        internal bool CanUse(Gun gun, bool reload=false)
        {
            if(_host) return true;
            if(!_ready || _state==null || _state.Action!=1 || _state.EquippedItemId!=-1 || gun==null || _inflight.Count>=MaxInFlight) return false;
            // A reload waits for every action before it, and nothing overtakes a reload.
            foreach(var a in _inflight) if(a.Kind==PacketKind.CombatReload) return false;
            if(reload && _inflight.Count!=0) return false;
            int id=RemoteEquipment.CaptureLocalItemId();
            // 1.1.6: the bare fists: no item, so no ledger entry; the pace and stamina still count.
            if(gun.Hands) {
                var fist=FistRule(gun);
                if(reload || id!=-1 || fist==null || Time.unscaledTime-_lastAttack<fist.Interval*.9f) return false;
                return _state.Stamina-_inflight.Count*fist.StaminaCost>=fist.StaminaCost;
            }
            var rule=Rule(id);
            if(rule==null || rule.Action==0 && gun.weaponType!=0 || rule.Action==1 && gun.weaponType!=2) return false;
            foreach(var w in _state.CombatWeapons) if(w.ItemId==id) {
                // What the host will have left once it has booked the actions still in flight.
                int uses=0; foreach(var a in _inflight) if(a.Kind==PacketKind.ZombieAction && a.EquippedItemId==id) uses++;
                if(w.Durability-uses<=0) return false;
                if(reload) {
                    if(rule.Action!=0 || w.Magazine>=rule.Capacity) return false;
                    foreach(var a in _state.CombatAmmo) if(a.ItemId==rule.AmmoId && a.Amount>0) return true;
                    return false;
                }
                // The host accepts at most one action per rule interval on average.
                if(Time.unscaledTime-_lastAttack<rule.Interval*.9f) return false;
                // Firearms do not use stamina (1.0.7), as in the native game; swings still do.
                return rule.Action==0 ? w.Magazine-uses>0 : _state.Stamina-_inflight.Count*rule.StaminaCost>=rule.StaminaCost;
            }
            return false;
        }
        internal void Submit(Packet packet)
        {
            if(_host || !_ready || _inflight.Count>=MaxInFlight || Revision<=0) return;
            packet.Revision=Revision;
            if(_inflight.Count==0) _retryAt=Time.unscaledTime+.2f;
            _inflight.Add(packet);
            if(packet.Kind==PacketKind.ZombieAction) _lastAttack=Time.unscaledTime;
            _send(packet);
        }
        // Guest: a reload is still waiting for the host or running there.
        internal bool ReloadPending
        {
            get {
                if(_host || !_ready || _state==null) return false;
                foreach(var a in _inflight) if(a.Kind==PacketKind.CombatReload) return true;
                return _state.EquippedItemId!=-1;
            }
        }
        internal void DenyOrRevoke() { if(_host) { _ledger.DenyOrRevoke();_sendAt=0; } }
        internal bool Approved { get { return _host && _ledger.Approved; } }
        internal string Description
        {
            get {
                if(!_host) return _state==null || _state.Action==0 ? "Бой: ждём, пока хост поставит ваши вещи на учёт" : _state.Action==2 ? "Бой запрещён хостом до новой комнаты" : _state.EquippedItemId!=-1 ? "Перезарядка у хоста: 3 секунды" : "Бой: оружие и патроны по учёту хоста";
                if(!_ledger.Approved) return _ledger.Denied ? "Бой друга запрещён до новой комнаты" : "Бой друга: ждёт учёта его вещей";
                var text=new StringBuilder("Оружие друга по учёту хоста:");
                foreach(var w in _ledger.Weapons()) text.Append("\n").Append(ItemLabel(w.ItemId)).Append(": магазин ").Append(w.Magazine).Append(", прочность ").Append(w.Durability.ToString("0"));
                foreach(var a in _ledger.Ammo()) text.Append("\n").Append(ItemLabel(a.ItemId)).Append(": ").Append(a.Amount);
                return text.ToString();
            }
        }
        private void SendState()
        {
            var p=Message(PacketKind.CombatState,++_stateSequence);p.Revision=_ledger.Revision;p.Action=(byte)(_ledger.Approved?1:_ledger.Denied?2:0);
            p.CombatWeapons=_ledger.Weapons();p.CombatAmmo=_ledger.Ammo();p.Stamina=_ledger.Stamina;p.ShotSequence=_processed;p.EquippedItemId=_ledger.ReloadItem;_send(p);
        }
        // Host: weapons the guest owns per the ledger, their magazines (by gun slot),
        // best durability, and the reserve of every ammunition they use.
        private void SyncFromLedger(float now)
        {
            var book=Ledger==null?null:Ledger();
            if(book==null || !book.Ready) return;
            var weapons=new List<CombatWeapon>();var rules=new List<CombatRule>();var ammoIds=new HashSet<int>();var gunIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(int id in book.TopLevelIds()) {
                var rule=Rule(id);var gun=GunFor(id);
                if(rule==null || gun==null || weapons.Count>=16 || !gunIds.Add(gun.ID)) continue;
                float wear=book.BestWear(id);if(wear<0) continue;
                weapons.Add(new CombatWeapon {ItemId=id,Magazine=rule.Action==0?Mathf.Clamp(book.Magazine(GunIndex(id)),0,rule.Capacity):0,Durability=Mathf.Clamp(wear,0,rule.MaxDurability)});
                rules.Add(rule);
                if(rule.Action==0) ammoIds.Add(rule.AmmoId);
            }
            var ammo=new List<CombatAmmo>();
            foreach(int id in ammoIds) ammo.Add(new CombatAmmo {ItemId=id,Amount=(int)Math.Min(10000,book.Count(id))});
            _ledger.SetMaxStamina(book.MaxStamina); // 1.4.1: its own maximum, as the host checks it (LanGuestStats)
            _ledger.Sync(weapons.ToArray(),ammo.ToArray(),rules.ToArray(),now);
        }
        private static int GunIndex(int itemId)
        {
            var gun=GunFor(itemId);var player=Player();
            return gun==null || player==null ? -1 : player.zombieFighterFireArmWeapon.gunList.IndexOf(gun);
        }
        private static void ApplyLocal(Packet state, List<Packet> inflight)
        {
            if(!LanSaveIsolation.Active) return;
            // Only the disposable client's combat presentation is corrected. No saves or host inventory writes.
            // Shots still in flight were already taken from the local magazine.
            foreach(var w in state.CombatWeapons) {
                var gun=GunFor(w.ItemId);if(gun==null) continue;
                int shots=0; foreach(var a in inflight) if(a.Kind==PacketKind.ZombieAction && a.EquippedItemId==w.ItemId) shots++;
                gun.magazine=Math.Max(0,w.Magazine-shots);
            }
            var player=Player();if(player==null)return;
            if(player.beltInventory!=null) foreach(var cell in player.beltInventory) {
                if(cell==null || cell.content==null)continue;
                foreach(var item in cell.content) if(item!=null && item.weaponData!=null)
                    foreach(var w in state.CombatWeapons) if(w.ItemId==item.itemID) item.weaponData.weaponDurability=Math.Min(item.weaponData.weaponDurability,w.Durability); // never raise
            }
            // A host reload replaces native reserve extraction. Remove only the excess
            // from existing backpack stacks; never create or restore an item from a packet.
            var inventory=player.inventory;if(inventory==null || inventory.content==null)return;
            foreach(var a in state.CombatAmmo) {
                var stacks=new List<InventoryItem>();long total=0;
                foreach(var item in inventory.content) if(item!=null && item.itemID==a.ItemId && item.amount>0) { stacks.Add(item);total+=item.amount; }
                long excess=Math.Max(0,total-a.Amount);
                foreach(var item in stacks) {
                    if(excess==0)break;
                    int take=(int)Math.Min(excess,item.amount);inventory.Extract(item,take);excess-=take;
                }
            }
        }
        internal static CombatRule Rule(int id)
        {
            var db=ItemsDataBase.Global;var prefabs=db==null?null:db.itemPrefab;
            if(prefabs==null || id<0 || id>=prefabs.Length || prefabs[id]==null) return null;
            var data=prefabs[id].GetComponent<WeaponData>();var gun=GunFor(id);
            if(data==null || gun==null || gun.Hands || gun.weaponType!=0 && gun.weaponType!=2) return null;
            if(!HostCombatInventory.Finite(data.weaponDurabilityMax) || data.weaponDurabilityMax<=0 || data.weaponDurabilityMax>99999 ||
                !HostCombatInventory.Finite(gun.attackStaminaCost) || gun.attackStaminaCost<0 || gun.attackStaminaCost>100) return null;
            bool firearm=gun.weaponType==0;
            float interval=firearm?gun.currentBetweenShotsTime:gun.meleeStrikeTimeForAttackEnd;
            // Base prefab capacity works for inactive guns and does not borrow the
            // local host's installed magazine extension for the remote player.
            if(!HostCombatInventory.Finite(interval) || interval<0 || interval>60 || firearm && (gun.currentCapacityMagazine<1 || gun.currentCapacityMagazine>500 || gun.ammoID<0 || gun.ammoID>65535 || gun.bulletPrefab==null)) return null;
            return new CombatRule {ItemId=id,Action=(byte)(firearm?0:1),AmmoId=firearm?gun.ammoID:-1,Capacity=firearm?gun.currentCapacityMagazine:0,
                Interval=Math.Max(firearm ? .05f : .5f,interval),ReloadSeconds=3,StaminaCost=gun.attackStaminaCost,MaxDurability=data.weaponDurabilityMax};
        }
        // 1.1.6: the rule of a swing of the bare fists (item -1), from the fists' own gun.
        internal static CombatRule FistRule(Gun gun)
        {
            if(gun==null || !gun.Hands || gun.weaponType!=2) return null;
            float interval=gun.meleeStrikeTimeForAttackEnd;
            if(!HostCombatInventory.Finite(interval) || interval<0 || interval>60 ||
                !HostCombatInventory.Finite(gun.attackStaminaCost) || gun.attackStaminaCost<0 || gun.attackStaminaCost>100) return null;
            return new CombatRule {ItemId=-1,Action=1,AmmoId=-1,Interval=Math.Max(.5f,interval),StaminaCost=gun.attackStaminaCost,MaxDurability=1};
        }
        // The local player's bare fists.
        internal static Gun FistGun()
        {
            var player=Player(); var guns=player==null || player.zombieFighterFireArmWeapon==null?null:player.zombieFighterFireArmWeapon.gunList;
            if(guns==null) return null;
            foreach(var gun in guns) if(gun!=null && gun.Hands && gun.weaponType==2) return gun;
            return null;
        }
        private static Gun GunFor(int id)
        {
            var db=ItemsDataBase.Global;var prefabs=db==null?null:db.itemPrefab;var player=Player();
            if(prefabs==null || id<0 || id>=prefabs.Length || prefabs[id]==null || player==null || player.zombieFighterFireArmWeapon==null) return null;
            var data=prefabs[id].GetComponent<WeaponData>();var guns=player.zombieFighterFireArmWeapon.gunList;
            if(data==null || guns==null) return null;
            foreach(var gun in guns) if(gun!=null && gun.ID==data.ID) return gun;
            return null;
        }
        private static ZombieFighterController Player() { return GlobalManager.global==null?null:GlobalManager.global.controlledChar; }
        private static string ItemLabel(int id)
        {
            var db=ItemsDataBase.Global;var items=db==null?null:db.itemPrefab;
            return (items!=null && id>=0 && id<items.Length && items[id]!=null ? items[id].name : "Предмет")+" (ID "+id+")";
        }
        private Packet Message(PacketKind kind,uint seq) { return new Packet {Kind=kind,Scene=_scene,WorldEpoch=_epoch,Sequence=seq}; }
        private static bool Newer(uint a,uint b) { return a!=0 && unchecked((int)(a-b))>0; }
    }
}
