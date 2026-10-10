using System;
using System.Collections.Generic;
using Zompiercer.Inventory;

// Narrow test doubles for the Unity/resource boundary. The ledger and sync under
// test are the actual production files. This does not execute Harmony or Unity.
namespace UnityEngine
{
    internal static class Time { internal static float unscaledTime; }
    internal static class Mathf { internal static float Clamp(float x,float min,float max) { return Math.Max(min,Math.Min(max,x)); } internal static int Clamp(int x,int min,int max) { return Math.Max(min,Math.Min(max,x)); } }
    internal sealed class GameObject
    {
        internal string name="test resource";
        internal WeaponData Data;
        internal T GetComponent<T>() where T:class { return Data as T; }
    }
}
internal sealed class WeaponData { internal string ID="gun"; internal float weaponDurability=100,weaponDurabilityMax=100; }
internal sealed class Gun
{
    internal string ID="gun";
    internal bool Hands=false;
    internal int weaponType=0,ammoID=10,currentCapacityMagazine=2,magazine=2;
    internal float currentBetweenShotsTime=.5f,meleeStrikeTimeForAttackEnd=.5f,attackStaminaCost=0;
    internal object bulletPrefab=new object();
}
internal sealed class Firearms { internal List<Gun> gunList=new List<Gun>(); }
internal sealed class ZombieFighterController { internal Inventory inventory=new Inventory(); internal Inventory[] beltInventory; internal Firearms zombieFighterFireArmWeapon=new Firearms(); }
internal sealed class GlobalManager { internal static GlobalManager global; internal ZombieFighterController controlledChar; }
internal sealed class ItemsDataBase { internal static ItemsDataBase Global; internal UnityEngine.GameObject[] itemPrefab; }
namespace Zompiercer.Inventory
{
    internal sealed class InventoryItem { internal int itemID,amount=1; internal WeaponData weaponData; }
    internal sealed class Inventory
    {
        internal List<InventoryItem> content=new List<InventoryItem>();
        internal void Extract(InventoryItem item,int amount) { if(amount<0 || amount>item.amount)throw new Exception("Invalid local extraction");item.amount-=amount;if(item.amount==0)content.Remove(item); }
    }
}
namespace ZompiercerLAN
{
    internal static class LanSaveIsolation { internal static bool Active; }
    internal static class RemoteEquipment { internal static int Held=1; internal static int CaptureLocalItemId() { return Held; } }
    internal static class CombatSyncChecks
    {
        private sealed class Rules : ILedgerRules
        {
            public LedgerItemRule Item(int id) { return id == 1 ? new LedgerItemRule { Slot0 = LedgerSlot.Wear } : id == 10 ? new LedgerItemRule() : null; }
        }
        private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
        private static Packet Last(List<Packet> list) { return list[list.Count-1]; }
        private static Packet Request(PacketKind kind,uint sequence) { return new Packet {Kind=kind,WorldEpoch=1,Scene="Terrain1",Sequence=sequence,Revision=1,EquippedItemId=1}; }
        internal static void Run()
        {
            UnityEngine.Time.unscaledTime=0;LanSaveIsolation.Active=true;
            var gun=new Gun();var data=new WeaponData();var player=new ZombieFighterController {beltInventory=new[]{new Inventory()}};
            player.beltInventory[0].content.Add(new InventoryItem {itemID=1,weaponData=data});player.inventory.content.Add(new InventoryItem {itemID=10,amount=3});
            player.zombieFighterFireArmWeapon.gunList.Add(gun);
            GlobalManager.global=new GlobalManager {controlledChar=player};ItemsDataBase.Global=new ItemsDataBase {itemPrefab=new UnityEngine.GameObject[11]};
            ItemsDataBase.Global.itemPrefab[1]=new UnityEngine.GameObject {Data=new WeaponData()};
            ItemsDataBase.Global.itemPrefab[10]=new UnityEngine.GameObject();
            // The host's ledger of the guest: 3 rounds in the backpack, the gun on the belt with 2 loaded.
            var book=new LanGuestLedger(new Rules());
            book.Reset(Program.LedgerProfile(new object[] { Program.L(10,3) }, new object[] { Program.L(1,1,100f) }, new[] { 2 }),0);
            var ledger=new HostCombatInventory();var hostOut=new List<Packet>();var clientOut=new List<Packet>();
            var host=new LanCombatSync(true,ledger,hostOut.Add) {Ledger=()=>book};var client=new LanCombatSync(false,null,clientOut.Add);
            host.Rebind("Terrain1",1);client.Rebind("Terrain1",1);client.Tick(true);
            Check(!client.CanUse(gun),"Client shot before the host's allowance");
            Check(clientOut.Count==0,"Client still proposes a kit");
            host.Tick(true);client.Receive(Last(hostOut));
            Check(ledger.Approved && client.CanUse(gun) && client.AppliedState==Last(hostOut).Sequence,"Ledger allowance did not reach the client");
            var shot=Request(PacketKind.ZombieAction,1);client.Submit(shot);gun.magazine--;
            Check(!client.CanUse(gun),"Client fired faster than the weapon interval");
            UnityEngine.Time.unscaledTime=.1f;host.Tick(true);client.Receive(Last(hostOut));
            Check(!client.CanUse(gun) && gun.magazine==1,"Unacknowledged snapshot restored predicted magazine");
            UnityEngine.Time.unscaledTime=.21f;client.Tick(true);Check(Last(clientOut)==shot,"Lost request was not retried identically");
            host.Processed(shot.Sequence);Check(host.Spend(shot),"Host rejected valid shot");Check(!host.Spend(shot),"Retry spent ammunition twice");
            Check(book.Magazine(0)==1 && Math.Abs(book.BestWear(1)-99f)<.01f,"Shot not booked to the ledger");
            UnityEngine.Time.unscaledTime=.5f;host.Tick(true);var ack=Last(hostOut);client.Receive(ack);Check(client.CanUse(gun) && gun.magazine==1,"Acknowledgement did not unblock/correct client");
            client.Receive(ack);Check(gun.magazine==1,"Duplicate host state changed magazine");
            UnityEngine.Time.unscaledTime=1;var reload=Request(PacketKind.CombatReload,2);client.Submit(reload);host.Processed(2);host.Reload(reload);host.Tick(true);client.Receive(Last(hostOut));
            Check(!client.CanUse(gun),"Client fired during host reload");
            UnityEngine.Time.unscaledTime=3.99f;host.Tick(true);Check(ledger.Weapons()[0].Magazine==1,"Integration reload completed early");
            UnityEngine.Time.unscaledTime=4;host.Tick(true);
            Check(book.Magazine(0)==2 && book.Count(10)==2 && book.PendingCount==1,"Reload not booked (rounds out of the reserve, magazine pending)");
            // Completion and the next periodic snapshot can be separate frames.
            UnityEngine.Time.unscaledTime=4.11f;host.Tick(true);client.Receive(Last(hostOut));
            Check(ledger.Weapons()[0].Magazine==2 && ledger.Ammo()[0].Amount==2 && gun.magazine==2 && player.inventory.content[0].amount==2,"Reload/local reserve failed conservation");
            // A packet may not touch a solo inventory even if delivered directly.
            gun.magazine=0;LanSaveIsolation.Active=false;UnityEngine.Time.unscaledTime=4.2f;host.Tick(true);client.Receive(Last(hostOut));
            Check(gun.magazine==0,"Combat correction modified non-isolated inventory");LanSaveIsolation.Active=true;gun.magazine=2;
            var proposal=new Packet {Kind=PacketKind.CombatProposal,WorldEpoch=2,Scene="Terrain2",Sequence=1,CombatWeapons=new[]{new CombatWeapon {ItemId=1,Magazine=500,Durability=100}},CombatAmmo=new[]{new CombatAmmo {ItemId=10,Amount=10000}}};
            host.Rebind("Terrain2",2);client.Rebind("Terrain2",2);client.Tick(true);client.Receive(ack);host.Receive(proposal);
            Check(!client.CanUse(gun) && book.PendingCount==0,"Old world message crossed epoch, or the world change left pending magazine counts");
            UnityEngine.Time.unscaledTime=4.4f;host.Tick(true);
            Check(ledger.Weapons()[0].Magazine==2 && ledger.Ammo()[0].Amount==2,"Client proposal imported ammunition");
            client.Receive(Last(hostOut));Check(client.CanUse(gun),"Allowance failed after world change");
            // Recreated sync objects share the room ledger, but no pending request/ack.
            host=new LanCombatSync(true,ledger,hostOut.Add) {Ledger=()=>book};client=new LanCombatSync(false,null,clientOut.Add);
            host.Rebind("Terrain2",2);client.Rebind("Terrain2",2);UnityEngine.Time.unscaledTime=4.6f;host.Tick(true);client.Tick(true);client.Receive(Last(hostOut));
            Check(client.CanUse(gun) && ledger.Ammo()[0].Amount==2,"Reconnect reset the allowance");
            // Ammo taken from a host storage reaches combat through the ledger.
            book.Give(LanGuestLedger.RequestChannel,1,Program.L(10,5));UnityEngine.Time.unscaledTime=4.8f;host.Tick(true);
            Check(ledger.Ammo()[0].Amount==7,"Ledger hand-out not visible to combat");
            host.DenyOrRevoke();UnityEngine.Time.unscaledTime=5f;host.Tick(true);client.Receive(Last(hostOut));Check(!client.CanUse(gun) && !ledger.Approved,"Revocation did not block client");
            client.Freeze();Check(!client.CanUse(gun),"Disconnected isolated client resumed native combat");
            // 1.0.1: several actions may be in flight; a jittered burst is still booked one by one.
            gun.currentBetweenShotsTime=.1f;gun.currentCapacityMagazine=30;gun.magazine=30;
            book=new LanGuestLedger(new Rules());
            book.Reset(Program.LedgerProfile(new object[] { Program.L(10,3) }, new object[] { Program.L(1,1,100f) }, new[] { 30 }),0);
            ledger=new HostCombatInventory();host=new LanCombatSync(true,ledger,hostOut.Add) {Ledger=()=>book};client=new LanCombatSync(false,null,clientOut.Add);
            host.Rebind("Terrain3",3);client.Rebind("Terrain3",3);
            UnityEngine.Time.unscaledTime=10f;client.Tick(true);host.Tick(true);client.Receive(Last(hostOut));
            var shots=new List<Packet>();
            for(uint i=1;i<=3;i++) {
                UnityEngine.Time.unscaledTime=10f+.1f*i;Check(client.CanUse(gun),"In-flight shot "+i+" refused");
                var s=Request(PacketKind.ZombieAction,i);client.Submit(s);gun.magazine--;shots.Add(s);
            }
            Check(!client.CanUse(gun),"Shot faster than the weapon interval while others are in flight");
            UnityEngine.Time.unscaledTime=10.32f;host.Tick(true);client.Receive(Last(hostOut));
            Check(gun.magazine==27,"A snapshot before the host booked the shots restored them");
            UnityEngine.Time.unscaledTime=10.35f;
            foreach(var s in shots) { host.Processed(s.Sequence);Check(host.Spend(s),"Jittered in-flight shot "+s.Sequence+" rejected"); }
            int extra=0;
            for(uint i=4;i<40;i++) { var x=Request(PacketKind.ZombieAction,i);host.Processed(i);if(host.Spend(x)) extra++; else break; }
            Check(extra>0 && extra<10,"The jitter allowance did not bound a burst ("+extra+")");
            Check(book.Magazine(0)==27-extra,"In-flight shots not booked one by one");
            UnityEngine.Time.unscaledTime=10.5f;host.Tick(true);client.Receive(Last(hostOut));
            Check(gun.magazine==27-extra && client.CanUse(gun),"Acknowledged burst did not settle the client");
            // 1.1.6: bare fists (item -1): no ledger entry or wear, the same pace and stamina limits.
            var fists=new Gun {ID="Hands",Hands=true,weaponType=2,meleeStrikeTimeForAttackEnd=.6f,attackStaminaCost=30};
            player.zombieFighterFireArmWeapon.gunList.Add(fists);
            UnityEngine.Time.unscaledTime=20f;host.Tick(true);client.Receive(Last(hostOut));
            RemoteEquipment.Held=-1;
            Check(client.CanUse(fists) && !client.CanUse(gun),"Fists refused, or a gun used with nothing in hand");
            Check(!client.CanUse(fists,true),"Fists reloaded");
            var punch=Request(PacketKind.ZombieAction,50);punch.EquippedItemId=-1;punch.Action=1;client.Submit(punch);
            Check(!client.CanUse(fists),"Fists faster than their swing");
            host.Processed(50);int wearBefore=(int)book.BestWear(1);Check(host.Spend(punch),"Host rejected a fist swing");
            Check((int)book.BestWear(1)==wearBefore && Math.Abs(ledger.Stamina-70f)<.01f,"Fist swing wore an item or skipped stamina");
            // As any weapon: one early swing within the network jitter, never a burst.
            var quick=Request(PacketKind.ZombieAction,51);quick.EquippedItemId=-1;quick.Action=1;host.Processed(51);
            Check(host.Spend(quick) && Math.Abs(ledger.Stamina-40f)<.01f,"Fist swing within the jitter allowance rejected");
            var third=Request(PacketKind.ZombieAction,52);third.EquippedItemId=-1;third.Action=1;host.Processed(52);
            Check(!host.Spend(third),"Host accepted fists faster than their swing");
            UnityEngine.Time.unscaledTime=23f;var shotWithFist=Request(PacketKind.ZombieAction,53);shotWithFist.EquippedItemId=-1;shotWithFist.Action=0;host.Processed(53);
            Check(!host.Spend(shotWithFist),"Host accepted a shot of the bare fists");
            var tired=new HostCombatInventory();tired.Sync(new CombatWeapon[0],new CombatAmmo[0],new CombatRule[0],0);
            int swings=0;for(uint i=1;i<=5;i++) if(tired.TryFist(i,tired.Revision,.5f,30,i)) swings++;
            // 1.4.1: 100 stamina, 30 a swing, 10 back a second: four swings a second apart, not a fifth.
            Check(swings==4 && !tired.TryFist(20,tired.Revision,.5f,-1,100),"Fist stamina or rule not bounded ("+swings+")");
            RemoteEquipment.Held=1;
            Console.WriteLine("PASS: production combat sync on the host's ledger / booking / retry / ack / epoch / local corrections / revocation / in-flight actions and jitter / bare fists");
        }
    }
}
