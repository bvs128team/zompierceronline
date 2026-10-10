using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ZompiercerLAN
{
    // What the game's items restore and how far skills go (1.3.0), from the item prefabs: heal,
    // hunger, thirst and sleep of LootObjectValue, and whether an item gives buffs.
    internal interface IStatRules
    {
        float[] Restores(int id);   // [heal, hunger, thirst, sleep] or null
        bool HasBuffs(int id);
        int SkillMax(int skill);    // 0 health, 1 stamina, 2 strength, 3 stealth
    }

    // The host's check of the guest's level, experience, health and needs (1.3.0). No Unity: the
    // host's game credits what it can explain, and every uploaded profile is held against the last
    // accepted one plus those credits.
    //  - Experience comes only from zombies dying in the host's world while the guest plays there and
    //    from quests the guest hands in through the host (with up to MaxXpBuff of the game's buff).
    //    Level, skill points and skills must follow the game's own level-up (ZFLevelController:
    //    maxXP = 100 * level * factor, one point per level, one level per point spent). Too much:
    //    the last accepted level advanced by the credited experience, as the game would.
    //  - Maximum health and stamina follow their skills (100 + 10 per level above 1); maximum hunger,
    //    thirst and sleep never grow; nothing is above its maximum.
    //  - Health, hunger, thirst and sleep grow only by what the guest's consumed items restore (the
    //    ledger sees them go), a buffed item, getting up after being downed, respawning, sleeping
    //    together, a health skill, and small rates over time for the game's buffs. Too much: the
    //    last accepted value plus what is credited.
    // A corrected profile goes back to the guest as the ledger's corrections do.
    internal sealed class LanGuestStats
    {
        internal const float MaxXpBuff = 3f, HealthRate = 2f, NeedRate = .3f, SleepRate = .5f, Slack = 3f, BuffCredit = 30f, MaxStep = 600f,
            MaxHealthCredit = 1000f, MaxNeedCredit = 500f, MaxXpCredit = 10000000f, ReviveShare = .35f, HealthOver = 1.5f;
        // Stat slots (LanGuestSchema: character[1]) and level slots (character[6], ZFLevelController.Save).
        private const int MaxHealth = 0, Health = 1, MaxStamina = 2, Stamina = 3, MaxHunger = 4, Hunger = 5, MaxThirst = 6, Thirst = 7,
            MaxSleep = 8, Sleep = 9, Level = 10, MaxXp = 11, Xp = 12, Perks = 13, Skill0 = 14, StrengthFactor = 18, StealthFactor = 19;
        private readonly IStatRules _rules;
        private float _xp, _health, _hunger, _thirst, _sleep;
        // Getting up, respawning and sleeping together count for a while (the guest's next uploads).
        internal const double EventWindow = 60;
        private double _reviveUntil = -1, _respawnUntil = -1, _sleptUntil = -1;
        private bool _revive, _respawn, _slept;
        private double _last = -1;
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        internal Func<double> Clock = () => Watch.Elapsed.TotalSeconds;
        internal LanGuestStats(IStatRules rules) { _rules = rules; }

        internal void Reset() { _xp = _health = _hunger = _thirst = _sleep = 0f; _reviveUntil = _respawnUntil = _sleptUntil = -1; _last = -1; }

        internal void CreditXp(float xp) { if (xp > 0f && !float.IsNaN(xp)) _xp = Math.Min(MaxXpCredit, _xp + xp); }
        internal void CreditRevive() { _reviveUntil = Clock() + EventWindow; }
        // Healing the host handed out (the guest's robot dog's medkit).
        internal void CreditHealth(float amount) { if (amount > 0f && !float.IsNaN(amount)) _health = Math.Min(MaxHealthCredit, _health + amount); }
        internal void CreditRespawn() { _respawnUntil = Clock() + EventWindow; }
        internal void CreditSleep(int hours = 0) { _sleptUntil = Clock() + EventWindow + Math.Max(0, hours); }
        internal float XpCredit { get { return _xp; } }

        // Items the guest's ledger saw it consume (id -> count).
        internal void Consumed(Dictionary<int, long> consumed)
        {
            if (consumed == null || _rules == null) return;
            foreach (var pair in consumed)
            {
                if (pair.Value <= 0) continue;
                float n = Math.Min(pair.Value, 1000);
                var r = _rules.Restores(pair.Key);
                if (r != null && r.Length == 4)
                {
                    _health = Math.Min(MaxHealthCredit, _health + Math.Max(0f, r[0]) * n);
                    _hunger = Math.Min(MaxNeedCredit, _hunger + Math.Max(0f, r[1]) * n);
                    _thirst = Math.Min(MaxNeedCredit, _thirst + Math.Max(0f, r[2]) * n);
                    _sleep = Math.Min(MaxNeedCredit, _sleep + Math.Max(0f, r[3]) * n);
                }
                if (_rules.HasBuffs(pair.Key))
                {
                    _health = Math.Min(MaxHealthCredit, _health + BuffCredit * n);
                    _hunger = Math.Min(MaxNeedCredit, _hunger + BuffCredit * n);
                    _thirst = Math.Min(MaxNeedCredit, _thirst + BuffCredit * n);
                    _sleep = Math.Min(MaxNeedCredit, _sleep + BuffCredit * n);
                }
            }
        }

        private static float F(object v) { return v is float ? (float)v : v is int ? (int)v : 0f; }
        private static int I(object v) { return v is int ? (int)v : 0; }

        // Holds `next` (a validated profile root) against `previous`, correcting `next`'s character in
        // place. False when anything was corrected (`reasons` says what).
        internal bool Check(object[] previous, object[] next, List<string> reasons)
        {
            double now = Clock();
            float dt = _last < 0 ? 0f : (float)Math.Max(0, Math.Min(MaxStep, now - _last));
            _last = now;
            _revive = now < _reviveUntil; _respawn = now < _respawnUntil; _slept = now < _sleptUntil;
            var oc = (object[])previous[12]; var nc = (object[])next[12];
            var os = (object[])oc[1]; var ns = (object[])nc[1];
            var ol = oc[6] as object[]; var nl = nc[6] as object[];
            int before = reasons.Count;

            // ---- Level, experience, skills ----
            float factor = ol != null ? F(ol[3]) : nl != null ? F(nl[3]) : 1f;
            if (factor <= 0f || float.IsNaN(factor)) factor = 1f;
            int l0 = I(os[Level]), p0 = I(os[Perks]); float x0 = F(os[Xp]), m0 = F(os[MaxXp]);
            int l1 = I(ns[Level]), p1 = I(ns[Perks]); float x1 = F(ns[Xp]), m1 = F(ns[MaxXp]);
            var s0 = new int[4]; var s1 = new int[4];
            for (int i = 0; i < 4; i++) { s0[i] = I(os[Skill0 + i]); s1[i] = I(ns[Skill0 + i]); }
            bool ok = l1 >= l0 && l1 <= l0 + 100 && p1 >= 0 && x1 >= 0f;
            int spent0 = p0, spent1 = p1;
            for (int i = 0; i < 4; i++)
            {
                ok &= s1[i] >= s0[i] && s1[i] >= 1 && s1[i] <= Math.Max(s0[i], _rules == null ? 100 : _rules.SkillMax(i));
                spent0 += s0[i] - 1; spent1 += s1[i] - 1;
            }
            ok &= spent1 - spent0 == l1 - l0;
            ok &= l1 == l0 ? Math.Abs(m1 - m0) < .5f : Math.Abs(m1 - 100f * l1 * factor) < .5f;
            if (nl != null)
            {
                ok &= I(nl[0]) == l1 && Math.Abs(F(nl[1]) - x1) < .01f && Math.Abs(F(nl[2]) - m1) < .01f && Math.Abs(F(nl[3]) - factor) < .0001f && I(nl[4]) == p1;
                for (int i = 0; i < 4; i++) ok &= I(nl[5 + i]) == s1[i];
            }
            float gain = 0f;
            if (ok)
            {
                if (l1 == l0) gain = x1 - x0;
                else
                {
                    gain = (m0 - x0) + x1;
                    for (int l = l0 + 1; l < l1; l++) gain += 100f * l * factor;
                }
                ok = gain <= _xp * MaxXpBuff + 1f;
            }
            if (ok) _xp = Math.Max(0f, _xp - Math.Max(0f, gain));
            else
            {
                // The last accepted level, advanced by what the host credited, as the game levels up.
                int level = l0, perks = p0; float xp = x0 + _xp, max = m0 > 0f ? m0 : 100f * Math.Max(1, l0) * factor;
                for (int guard = 0; xp >= max && guard < 100; guard++) { level++; perks++; xp -= max; max = 100f * level * factor; }
                _xp = 0f;
                l1 = level; p1 = perks; x1 = xp; m1 = max; Array.Copy(s0, s1, 4);
                ns[Level] = l1; ns[Perks] = p1; ns[Xp] = x1; ns[MaxXp] = m1;
                for (int i = 0; i < 4; i++) ns[Skill0 + i] = s1[i];
                ns[StrengthFactor] = os[StrengthFactor]; ns[StealthFactor] = os[StealthFactor];
                if (nl != null)
                {
                    nl[0] = l1; nl[1] = x1; nl[2] = m1; nl[3] = factor; nl[4] = p1;
                    for (int i = 0; i < 4; i++) nl[5 + i] = s1[i];
                }
                reasons.Add("level/experience");
            }

            // ---- Maximums ----
            float maxHealth = 100f + 10f * (s1[0] - 1), maxStamina = 100f + 10f * (s1[1] - 1);
            if (F(ns[MaxHealth]) > maxHealth + .5f) { ns[MaxHealth] = maxHealth; reasons.Add("max health"); }
            if (F(ns[MaxStamina]) > maxStamina + .5f) { ns[MaxStamina] = maxStamina; reasons.Add("max stamina"); }
            foreach (int k in new[] { MaxHunger, MaxThirst, MaxSleep })
                if (F(ns[k]) > Math.Max(F(os[k]), 100f) + .01f) { ns[k] = Math.Max(F(os[k]), 100f); reasons.Add("max need " + k); }
            if (F(ns[Stamina]) > F(ns[MaxStamina]) * HealthOver + .5f) { ns[Stamina] = F(ns[MaxStamina]); reasons.Add("stamina"); }

            // ---- Health and needs ----
            float healthCap = F(ns[MaxHealth]) * HealthOver;
            float allowed = _health + HealthRate * dt + Slack;
            if (s1[0] > s0[0] || _respawn) allowed += healthCap;     // a health skill fills it; so does respawning
            if (_revive) allowed += F(ns[MaxHealth]) * ReviveShare;
            float h0 = F(os[Health]), h1 = Math.Min(F(ns[Health]), healthCap);
            if (h1 < F(ns[Health]) - .01f) reasons.Add("health above maximum");
            if (h1 - h0 > allowed) { h1 = h0 + allowed; reasons.Add("health"); }
            _health = Math.Max(0f, _health - Math.Max(0f, h1 - h0 - HealthRate * dt - Slack));
            ns[Health] = h1; nc[7] = h1;
            _hunger = Need(os, ns, Hunger, MaxHunger, _hunger, NeedRate * dt, "hunger", reasons);
            _thirst = Need(os, ns, Thirst, MaxThirst, _thirst, NeedRate * dt, "thirst", reasons);
            float sleep = _sleep;
            float left = Need(os, ns, Sleep, MaxSleep, sleep + (_slept ? MaxNeedCredit : 0f), SleepRate * dt, "sleep", reasons);
            _sleep = _slept ? Math.Min(left, sleep) : left; // sleeping together fills it, but is not kept for later
            return reasons.Count == before;
        }

        // One need: at most its maximum, grown by at most the credit; returns what credit is left.
        private float Need(object[] os, object[] ns, int slot, int maxSlot, float credit, float rate, string name, List<string> reasons)
        {
            float cap = F(ns[maxSlot]) + .5f, v0 = F(os[slot]), v1 = F(ns[slot]);
            float allowed = credit + rate + Slack + (_respawn ? cap : 0f);
            if (v1 > cap) { v1 = cap; reasons.Add(name + " above maximum"); }
            if (v1 - v0 > allowed) { v1 = v0 + allowed; reasons.Add(name); }
            ns[slot] = v1;
            return Math.Min(MaxNeedCredit, Math.Max(0f, credit - Math.Max(0f, v1 - v0 - rate - Slack)));
        }
    }
}
