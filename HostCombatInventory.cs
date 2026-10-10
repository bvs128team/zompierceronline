using System;
using System.Collections.Generic;

namespace ZompiercerLAN
{
    internal sealed class CombatWeapon { internal int ItemId, Magazine; internal float Durability; }
    internal sealed class CombatAmmo { internal int ItemId, Amount; }
    // All rules originate on the host. No wire-supplied capacity, timing or stamina cost.
    internal sealed class CombatRule
    {
        internal int ItemId, AmmoId = -1, Capacity;
        internal byte Action;
        internal float Interval, ReloadSeconds, StaminaCost, MaxDurability;
    }

    // Main-thread combat allowance of the remote player. Since 0.7.0 its weapons,
    // magazines, durability and reserve come from the host's ledger of the guest's
    // belongings (Sync); every accepted swing, shot and completed reload is reported
    // back (Spent, Reloaded) so the ledger stays the single account. Rebinding a
    // world/DTLS session cancels work, but never restores stamina or the cooldown.
    internal sealed class HostCombatInventory
    {
        private readonly Dictionary<int, CombatWeapon> _weapons = new Dictionary<int, CombatWeapon>();
        private readonly Dictionary<int, CombatAmmo> _ammo = new Dictionary<int, CombatAmmo>();
        private readonly Dictionary<int, CombatRule> _rules = new Dictionary<int, CombatRule>();
        private uint _lastRequest;
        private int _reloadItem = -1;
        private double _reloadEnd, _nextAttack, _staminaAt;
        // Network jitter (1.0.1): actions may arrive this much early as long as the average
        // rate stays at one per interval (a theoretical-arrival-time limiter).
        internal const double Jitter = .75; // 1.0.2: relay paths bunch a long burst
        private bool _denied;
        internal bool Approved { get; private set; }
        internal bool Denied { get { return _denied; } }
        internal int Revision { get; private set; }
        internal float Stamina { get; private set; } = 100;
        // 1.4.1: as the game: the maximum follows the stamina skill (100 + 10 per level, never less), and stamina
        // comes back at a tenth of the maximum a second, all the time (ZombieFighterIndicatorsBar).
        internal const float StaminaRecovery = .1f, MinMaxStamina = 100f, MaxMaxStamina = 1000f;
        internal float MaxStamina { get; private set; } = 100;
        internal void SetMaxStamina(float value)
        {
            if (!Finite(value)) return;
            MaxStamina = Math.Max(MinMaxStamina, Math.Min(MaxMaxStamina, value));
            if (Stamina > MaxStamina) Stamina = MaxStamina;
        }
        internal int ReloadItem { get { return _reloadItem; } }
        // item id, action (0 shot, 1 swing)
        internal Action<int, byte> Spent { get; set; }
        // weapon item id, ammo item id, rounds moved from the reserve into the magazine
        internal Action<int, int, int> Reloaded { get; set; }

        // Replaces the allowance with the ledger's view. Invalid entries are skipped.
        internal void Sync(CombatWeapon[] weapons, CombatAmmo[] ammo, CombatRule[] rules, double now)
        {
            if (_denied || !ValidTime(now) || weapons == null || ammo == null || rules == null || rules.Length != weapons.Length) return;
            var w = new List<CombatWeapon>(); var r = new List<CombatRule>(); var ids = new HashSet<int>();
            for (int i = 0; i < weapons.Length && w.Count < 16; i++)
                if (weapons[i] != null && rules[i] != null && rules[i].ItemId == weapons[i].ItemId && ValidRule(rules[i], weapons[i]) && ids.Add(weapons[i].ItemId))
                { w.Add(weapons[i]); r.Add(rules[i]); }
            var a = new List<CombatAmmo>(); var ammoIds = new HashSet<int>();
            foreach (var x in ammo)
            {
                if (x == null || a.Count >= 16 || !ammoIds.Add(x.ItemId)) continue;
                bool used = false; foreach (var rule in r) if (rule.Action == 0 && rule.AmmoId == x.ItemId) used = true;
                if (used) a.Add(new CombatAmmo { ItemId = x.ItemId, Amount = Math.Max(0, Math.Min(10000, x.Amount)) });
            }
            if (!ValidLoadout(w.ToArray(), a.ToArray())) return;
            _weapons.Clear(); _rules.Clear(); _ammo.Clear();
            for (int i = 0; i < w.Count; i++)
            {
                _weapons.Add(w[i].ItemId, new CombatWeapon { ItemId = w[i].ItemId, Magazine = w[i].Magazine, Durability = w[i].Durability });
                _rules.Add(r[i].ItemId, Copy(r[i]));
            }
            foreach (var x in a) _ammo.Add(x.ItemId, x);
            if (_reloadItem != -1 && !_weapons.ContainsKey(_reloadItem)) _reloadItem = -1;
            if (!Approved) { Approved = true; Revision++; _staminaAt = now; }
        }
        // The host forbids combat for this room.
        internal void DenyOrRevoke()
        {
            _denied = true; Approved = false; Revision++; _reloadItem = -1;
            _weapons.Clear(); _ammo.Clear(); _rules.Clear();
        }
        internal void Rebind()
        {
            _lastRequest=0; _reloadItem=-1;
        }
        internal void CancelReload() { _reloadItem=-1; }

        private static bool ValidRule(CombatRule r, CombatWeapon w)
        {
            if (r.Action > 1 || !Finite(r.Interval) || r.Interval < .05f || r.Interval > 60 ||
                !Finite(r.StaminaCost) || r.StaminaCost < 0 || r.StaminaCost > 100 || !Finite(r.MaxDurability) ||
                r.MaxDurability <= 0 || r.MaxDurability > 99999 || !Finite(w.Durability) || w.Durability < 0 || w.Durability > r.MaxDurability) return false;
            if (r.Action == 0 && (r.AmmoId < 0 || r.AmmoId > 65535 || r.Capacity < 1 || r.Capacity > 500 || w.Magazine < 0 || w.Magazine > r.Capacity ||
                !Finite(r.ReloadSeconds) || r.ReloadSeconds < .5f || r.ReloadSeconds > 60)) return false;
            if (r.Action == 1 && (w.Magazine != 0 || r.Capacity != 0 || r.AmmoId != -1)) return false;
            return true;
        }
        private static CombatRule Copy(CombatRule r)
        {
            return new CombatRule { ItemId=r.ItemId, AmmoId=r.AmmoId, Capacity=r.Capacity, Action=r.Action, Interval=r.Interval,
                ReloadSeconds=r.ReloadSeconds, StaminaCost=r.StaminaCost, MaxDurability=r.MaxDurability };
        }

        internal bool TryAttack(uint sequence, int revision, int item, byte action, double now)
        {
            if (!Request(sequence, revision, now)) return false;
            Tick(now);
            CombatWeapon w; CombatRule rule;
            if (!Approved || _reloadItem!=-1 || now<_nextAttack-Jitter || !_weapons.TryGetValue(item,out w) ||
                !_rules.TryGetValue(item,out rule) || rule.Action!=action || w.Durability<=0 || action==1 && Stamina<rule.StaminaCost ||
                action==0 && w.Magazine<=0) return false;
            if (action==0) w.Magazine--;
            // Charge every accepted swing, even a miss. Conservative until native hit-phase sync exists.
            w.Durability=Math.Max(0,w.Durability-1);
            // 1.0.7: stamina limits swings only. The native game never stops a firearm for want of
            // stamina (it only adds recoil), and it capped a guest burst at about ten rounds.
            if (action==1) { Stamina-=rule.StaminaCost; _staminaAt=now; }
            _nextAttack=Math.Max(_nextAttack,now)+rule.Interval;
            Spent?.Invoke(item, action);
            return true;
        }
        // 1.1.6: a swing of the bare fists: nothing to book or wear out, but the same pace and
        // stamina limits as any swing (the host's own fists give the interval and the cost).
        internal bool TryFist(uint sequence, int revision, float interval, float staminaCost, double now)
        {
            if (!Request(sequence, revision, now)) return false;
            Tick(now);
            if (!Approved || _reloadItem!=-1 || now<_nextAttack-Jitter || !Finite(interval) || interval<.05f || interval>60 ||
                !Finite(staminaCost) || staminaCost<0 || staminaCost>100 || Stamina<staminaCost) return false;
            Stamina-=staminaCost; _staminaAt=now;
            _nextAttack=Math.Max(_nextAttack,now)+interval;
            return true;
        }
        internal bool TryReload(uint sequence, int revision, int item, double now)
        {
            if (!Request(sequence, revision, now)) return false;
            Tick(now);
            CombatWeapon w; CombatRule r; CombatAmmo a;
            if (!Approved || _reloadItem!=-1 || now<_nextAttack-Jitter || !_weapons.TryGetValue(item,out w) || !_rules.TryGetValue(item,out r) ||
                r.Action!=0 || w.Durability<=0 || w.Magazine>=r.Capacity || !_ammo.TryGetValue(r.AmmoId,out a) || a.Amount<=0) return false;
            _reloadItem=item; _reloadEnd=now+r.ReloadSeconds; return true;
        }
        private bool Request(uint sequence, int revision, double now)
        {
            if (!ValidTime(now) || revision!=Revision || sequence==0 || unchecked((int)(sequence-_lastRequest))<=0) return false;
            _lastRequest=sequence; return true;
        }
        internal void Tick(double now)
        {
            if (!ValidTime(now) || now<_staminaAt) return;
            Stamina=(float)Math.Min(MaxStamina,Stamina+Math.Max(0,now-_staminaAt)*MaxStamina*StaminaRecovery); _staminaAt=now;
            if (_reloadItem!=-1 && now>=_reloadEnd) {
                var w=_weapons[_reloadItem]; var r=_rules[_reloadItem]; var a=_ammo[r.AmmoId];
                int count=Math.Min(r.Capacity-w.Magazine,a.Amount); a.Amount-=count; w.Magazine+=count; _reloadItem=-1;
                if (count>0) Reloaded?.Invoke(w.ItemId, r.AmmoId, count);
            }
        }
        internal bool HasWeapon(int item, byte action) { CombatRule r; return Approved && _rules.TryGetValue(item,out r) && r.Action==action; }
        internal CombatWeapon[] Weapons() { return Copy(new List<CombatWeapon>(_weapons.Values).ToArray()); }
        internal CombatAmmo[] Ammo() { return Copy(new List<CombatAmmo>(_ammo.Values).ToArray()); }
        internal static bool ValidLoadout(CombatWeapon[] weapons, CombatAmmo[] ammo)
        {
            if (weapons==null || ammo==null || weapons.Length>16 || ammo.Length>16) return false;
            var ids=new HashSet<int>();
            foreach (var w in weapons) if (w==null || w.ItemId<0 || w.ItemId>65535 || w.Magazine<0 || w.Magazine>500 ||
                !Finite(w.Durability) || w.Durability<0 || w.Durability>99999 || !ids.Add(w.ItemId)) return false;
            ids.Clear();
            foreach (var a in ammo) if (a==null || a.ItemId<0 || a.ItemId>65535 || a.Amount<0 || a.Amount>10000 || !ids.Add(a.ItemId)) return false;
            return true;
        }
        private static CombatWeapon[] Copy(CombatWeapon[] src) { var dst=new CombatWeapon[src.Length]; for(int i=0;i<src.Length;i++) dst[i]=new CombatWeapon {ItemId=src[i].ItemId,Magazine=src[i].Magazine,Durability=src[i].Durability}; return dst; }
        private static CombatAmmo[] Copy(CombatAmmo[] src) { var dst=new CombatAmmo[src.Length]; for(int i=0;i<src.Length;i++) dst[i]=new CombatAmmo {ItemId=src[i].ItemId,Amount=src[i].Amount}; return dst; }
        internal static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        private static bool ValidTime(double x) { return !double.IsNaN(x) && !double.IsInfinity(x) && x>=0; }
    }
}
