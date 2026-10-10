using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Zompiercer.GUI;
using Zompiercer.Inventory;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Quests (1.2.0). The host's quests are the shared ones: the host sends their state
    // (accepted, ready to hand in, completed, stage) and the guest's copies show it, with the
    // game's own quest logic turned off there. The guest accepts and hands in quests through
    // its own NPCs as usual; each becomes a request (storage request 14) that the host carries
    // out on its own quest: gifts and rewards go onto the guest's ledger, what handing in costs
    // comes off it, and the experience is the guest's. On the host, a quest's goals count for
    // either player: a point the guest reaches, items the guest's ledger holds. Zombies and time
    // are the host's world anyway. A quest is known by its place in the scene (several quests
    // share an ID). 1.4.5: a reward is for both players: the guest's hand-in gives the host the same
    // reward, and a quest the host hands in while the guest plays keeps the same reward for the guest
    // (QuestShared), which it claims once (storage quest request 3). Main thread only.
    internal sealed class LanQuests : ILanQuestWorld
    {
        private const float CheckEvery = .25f, ResendEvery = 2f;
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _hookLogged;
        internal static string Failure { get; private set; }

        // Host: where the guest stands in the host's world and how many of an item its ledger
        // holds (null: no guest).
        internal static Func<Vector3?> GuestPosition;
        internal static Func<int, long> GuestCount;
        // Host (1.4.5): the attached guest's profile id (null: none), and the quests the host handed in
        // while that guest played, by scene and key: whose reward is still waiting.
        internal static Func<string> GuestProfile;
        private static readonly Dictionary<string, string> _rewardFor = new Dictionary<string, string>();
        // Guest (1.4.5): when it last claimed a shared reward, per quest.
        private static readonly Dictionary<uint, float> _claimedAt = new Dictionary<uint, float>();
        // Guest: attached to a host (the game's quest logic stays off), and the way to ask it
        // (null: not now, Unavailable says why).
        internal static bool Guest;
        internal static Func<int, uint, string, bool> Request;
        internal static string Unavailable = "";

        // The host's gifts for a quest the guest accepts, kept from the player's inventory.
        private static List<InventoryItem> _diverted;

        // Keys of the current scene's quests.
        private static Quest[] _keyedFrom;
        private static string _keyedScene;
        private static readonly Dictionary<uint, Quest> _byKey = new Dictionary<uint, Quest>();
        private static readonly Dictionary<Quest, uint> _keyOf = new Dictionary<Quest, uint>();
        // Guest: quests this player handed in itself (no "partner handed in" notice for those).
        private static readonly HashSet<uint> _handedIn = new HashSet<uint>();

        private readonly bool _host;
        private readonly Action<Packet> _send;
        private string _scene, _lastSent;
        private uint _epoch, _sequence, _received;
        private bool _ready, _synced;
        private float _checkAt, _sentAt = -100f;

        internal LanQuests(bool host, Action<Packet> send) { _host = host; _send = send; }

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.quests");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Quest), "Update"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(GuestSkip)));
                _harmony.Patch(AccessTools.Method(typeof(QuestStage), "Update"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(GuestSkip)));
                _harmony.Patch(AccessTools.Method(typeof(Quest), "ForceComplete"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(GuestSkip)));
                _harmony.Patch(AccessTools.Method(typeof(Quest), "Accept"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(AcceptPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Quest), "Complete"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(CompletePrefix)), postfix: new HarmonyMethod(typeof(LanQuests), nameof(CompletePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryController), "PutItemInPlayer"), prefix: new HarmonyMethod(typeof(LanQuests), nameof(GiftPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(ETPlayerAtPoint), "Update"), postfix: new HarmonyMethod(typeof(LanQuests), nameof(AtPointPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(ETPlayerHaveItem), "Update"), postfix: new HarmonyMethod(typeof(LanQuests), nameof(HaveItemPostfix)));
            }
            catch (Exception ex)
            {
                // Without every hook the guest keeps its own quests, as before 1.2.0.
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Quest hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf(); _harmony = null;
            Guest = false; Request = null; GuestPosition = null; GuestCount = null; GuestProfile = null;
        }

        // ---- Session: the host sends, the guest shows ----

        internal void Tick(string scene, uint epoch, bool ready)
        {
            if (scene != _scene || epoch != _epoch)
            {
                _scene = scene; _epoch = epoch; _sequence = 0; _received = 0; _synced = false; _claimedAt.Clear();
                _lastSent = null; _sentAt = -100f; _checkAt = 0f;
            }
            _ready = ready && epoch != 0 && !string.IsNullOrEmpty(scene) && Failure == null;
            if (!_host || !_ready) return;
            float now = Time.unscaledTime;
            if (now < _checkAt) return;
            _checkAt = now + CheckEvery;
            QuestFrame[] frames;
            try { frames = Frames(scene); }
            catch (Exception ex) { Once("Quest states not read: " + ex.GetType().Name + ": " + ex.Message); return; }
            if (frames == null) return;
            string signature = Signature(frames);
            if (signature == _lastSent && now - _sentAt < ResendEvery) return;
            _lastSent = signature; _sentAt = now;
            _send(new Packet { Kind = PacketKind.QuestStates, WorldEpoch = _epoch, Scene = _scene, Sequence = ++_sequence, Quests = frames });
        }

        // Host: a guest request changed a quest; its state goes out at once.
        internal void SendSoon() { _checkAt = 0f; _lastSent = null; }

        internal void Receive(Packet p)
        {
            if (_host || !_ready || p.Kind != PacketKind.QuestStates || p.WorldEpoch != _epoch || p.Scene != _scene) return;
            if (_received != 0 && unchecked((int)(p.Sequence - _received)) <= 0) return;
            _received = p.Sequence;
            try
            {
                // The first state of a scene replaces whatever the guest's own copy had, silently.
                if (Apply(p.Quests, p.Scene, _synced)) _synced = true;
            }
            catch (Exception ex) { Once("Quest states not applied: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---- Keys ----

        private static string CurrentScene()
        {
            var scenes = GlobalSceneManager.global;
            return scenes == null || scenes.SceneCurrentlyLoading ? null : scenes.CurrentSceneName;
        }

        private static Quest[] SceneQuests()
        {
            var manager = QuestsManager.Global;
            return manager == null ? null : manager.Quests;
        }

        // FNV-1a of the scene and the quest's place in it (never 0).
        internal static uint Key(string scene, string path)
        {
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(scene + "\n" + path)) { hash ^= b; hash *= 16777619; }
            return hash == 0 ? 1u : hash;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static void Rekey(string scene)
        {
            var quests = SceneQuests();
            if (quests == _keyedFrom && scene == _keyedScene) return;
            _keyedFrom = quests; _keyedScene = scene; _byKey.Clear(); _keyOf.Clear();
            if (quests == null || scene == null) return;
            var paths = new Dictionary<Quest, string>();
            var seen = new Dictionary<string, int>();
            foreach (var q in quests)
            {
                if (q == null) continue;
                string path = PathOf(q.transform);
                paths[q] = path;
                int n; seen.TryGetValue(path, out n); seen[path] = n + 1;
            }
            foreach (var pair in paths)
            {
                // Same names: the sibling index tells them apart (both sides load the same scene).
                string path = seen[pair.Value] > 1 ? pair.Value + "#" + pair.Key.transform.GetSiblingIndex() : pair.Value;
                uint key = Key(scene, path);
                if (_byKey.ContainsKey(key)) { _byKey[key] = null; continue; } // a collision: neither is shared
                _byKey[key] = pair.Key; _keyOf[pair.Key] = key;
            }
        }

        private static uint KeyOf(Quest q, string scene)
        {
            Rekey(scene);
            uint key;
            return q != null && _keyOf.TryGetValue(q, out key) && _byKey[key] == q ? key : 0u;
        }

        private static Quest Find(uint key, string scene)
        {
            Rekey(scene);
            Quest q;
            return _byKey.TryGetValue(key, out q) ? q : null;
        }

        // ---- Host: the states it sends ----

        private static QuestFrame[] Frames(string scene)
        {
            if (scene != CurrentScene() || SceneQuests() == null) return null;
            Rekey(scene);
            var frames = new List<QuestFrame>();
            foreach (var pair in _byKey)
            {
                var q = pair.Value;
                if (q == null) continue;
                bool accepted = q.Accepted || q.PreCompleted || q.Completed;
                byte flags = (byte)((accepted ? LanProtocol.QuestAccepted : 0) | (q.PreCompleted ? LanProtocol.QuestReady : 0) | (q.Completed ? LanProtocol.QuestCompleted : 0));
                if (q.Completed && accepted && RewardWaits(scene, pair.Key)) flags |= LanProtocol.QuestShared;
                int stage = accepted ? Mathf.Clamp(q.CurrentStage, -1, LanProtocol.MaxQuestStage) : -1;
                frames.Add(new QuestFrame { Key = pair.Key, Flags = flags, Stage = (sbyte)stage });
                if (frames.Count == LanProtocol.MaxQuestBatch) break;
            }
            frames.Sort((a, b) => a.Key.CompareTo(b.Key));
            return frames.ToArray();
        }

        // Host: the reward of this quest waits for the attached guest.
        private static bool RewardWaits(string scene, uint key)
        {
            string me = GuestProfile?.Invoke(), owner;
            return !string.IsNullOrEmpty(me) && _rewardFor.TryGetValue(scene + "|" + key, out owner) && owner == me;
        }

        private static string Signature(QuestFrame[] frames)
        {
            var text = new StringBuilder();
            foreach (var f in frames) text.Append(f.Key).Append(':').Append(f.Flags).Append(':').Append(f.Stage).Append(';');
            return text.ToString();
        }

        // ---- Guest: the host's states on its own copies ----

        private bool Apply(QuestFrame[] frames, string scene, bool notify)
        {
            if (frames == null || scene != CurrentScene() || SceneQuests() == null) return false;
            Rekey(scene);
            bool changed = false;
            foreach (var f in frames)
            {
                var q = Find(f.Key, scene);
                if (q == null) continue;
                bool accepted = (f.Flags & LanProtocol.QuestAccepted) != 0, ready = (f.Flags & LanProtocol.QuestReady) != 0,
                    completed = (f.Flags & LanProtocol.QuestCompleted) != 0;
                string title = Title(q);
                if (q.Accepted != accepted)
                {
                    q.Accepted = accepted; changed = true;
                    if (notify && accepted && !completed) Notify(Localized("Quest accepted") + ": " + title, false);
                }
                if (q.CurrentStage != f.Stage)
                {
                    // As ActivateStage: the stage's object comes on (its goals show their progress).
                    q.CurrentStage = f.Stage; changed = true;
                    var stages = q.Stages;
                    if (f.Stage >= 0 && stages != null && f.Stage < stages.Count && stages[f.Stage] != null) stages[f.Stage].gameObject.SetActive(true);
                }
                if (q.PreCompleted != ready)
                {
                    q.PreCompleted = ready; changed = true;
                    if (notify && ready && accepted && !completed) Notify(Localized("Quest completed") + ": " + title, true);
                }
                if (q.Completed != completed)
                {
                    changed = true;
                    try { q.SetCompleted(completed); } // OnCompleted runs in the guest's copy too
                    catch (Exception ex) { Once("Quest completion effects failed: " + ex.GetType().Name + ": " + ex.Message); q.Completed = completed; }
                    if (notify && completed && !_handedIn.Remove(f.Key)) Notify(LanSkinPicker.PartnerLabel(false) + " сдал квест: " + title, true);
                }
                // 1.4.5: the host handed it in while this player played: its reward is this player's too.
                if (completed && (f.Flags & LanProtocol.QuestShared) != 0) Claim(f.Key, scene);
            }
            if (changed) Regenerate();
            return true;
        }

        private static void Claim(uint key, string scene)
        {
            float now = Time.unscaledTime, last;
            if (_claimedAt.TryGetValue(key, out last) && now - last < 10f) return;
            var request = Request;
            if (request == null || !request(LanStorage.QuestShare, key, scene)) return; // asked again with the next state
            _claimedAt[key] = now;
        }

        // ---- Guest: the game's quest calls become requests ----

        private static bool GuestSkip() { return !Guest; }

        private static bool AcceptPrefix(Quest __instance)
        {
            if (!Guest) return true;
            Ask(LanStorage.QuestAccept, __instance);
            return false;
        }

        private static bool CompletePrefix(Quest __instance, out bool __state)
        {
            __state = __instance != null && __instance.Completed;
            if (!Guest) return true;
            try
            {
                var need = __instance.RequiresItem;
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (need != null && need.ItemAmount > 0 && (player == null || player.HowMuchHave(need.ItemID) < need.ItemAmount))
                {
                    Notify("Нужно: " + LanStorageGame.ItemName(need.ItemID) + " ×" + need.ItemAmount, false, true);
                    return false;
                }
            }
            catch (Exception ex) { Once("Quest items not counted: " + ex.GetType().Name + ": " + ex.Message); }
            Ask(LanStorage.QuestComplete, __instance);
            return false;
        }

        // Host (1.4.5): the host handed a quest in while the guest plays: the guest's reward waits.
        private static void CompletePostfix(Quest __instance, bool __state)
        {
            if (Guest || __state || __instance == null || !__instance.Completed) return;
            try
            {
                string me = GuestProfile?.Invoke(), scene = CurrentScene();
                if (string.IsNullOrEmpty(me) || scene == null || GuestPosition?.Invoke() == null) return;
                uint key = KeyOf(__instance, scene);
                if (key == 0) return;
                _rewardFor[scene + "|" + key] = me;
                LanNotify.Message("Награда за квест «" + Title(__instance) + "» достанется и другу");
            }
            catch (Exception ex) { Once("Shared quest reward not kept: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Ask(int kind, Quest q)
        {
            string scene = CurrentScene();
            uint key = scene == null ? 0u : KeyOf(q, scene);
            var request = Request;
            if (key == 0 || request == null || !request(kind, key, scene))
                Notify(key == 0 ? "Этот квест у хоста не найден" : Unavailable.Length != 0 ? Unavailable : "Квесты сейчас недоступны", false, true);
        }

        // The host's answer to this guest's request (its items already in the backpack).
        internal static void Answered(int kind, uint key, byte status, int received)
        {
            string scene = CurrentScene();
            var q = scene == null ? null : Find(key, scene);
            if (status != LanStorage.Ok && kind == LanStorage.QuestShare)
            {
                if (status == LanStorage.TooLarge) Notify("Награда за квест ждёт: освободите место в рюкзаке", false, true);
                return; // NotReady: already taken
            }
            if (status != LanStorage.Ok)
            {
                string why = status == LanStorage.NotOwned && kind == LanStorage.QuestComplete && q != null && q.RequiresItem != null
                    ? "По учёту хоста у вас нет: " + LanStorageGame.ItemName(q.RequiresItem.ItemID) + " ×" + q.RequiresItem.ItemAmount
                    : LanStorage.Describe(status);
                Notify("Хост не принял квест: " + why, false, true);
                return;
            }
            if (q == null) return;
            if (kind == LanStorage.QuestShare)
            {
                // 1.4.5: the reward of a quest the host handed in (nothing to spend, not handed in here).
                try
                {
                    var player = GlobalManager.global.controlledChar;
                    if (player != null && player.zFLevelController != null) player.zFLevelController.AddExperience(q.RewardExperience);
                }
                catch (Exception ex) { Once("Quest experience not added: " + ex.GetType().Name + ": " + ex.Message); }
                if (received > 0 && q.RewardItems != null)
                    foreach (var reward in q.RewardItems) if (reward != null && reward.amount > 0) Received(reward.ID, reward.amount);
                Notify("Награда за квест «" + Title(q) + "»: " + LanSkinPicker.PartnerLabel(false) + " сдал его", true);
                return;
            }
            if (kind == LanStorage.QuestAccept)
            {
                if (received > 0)
                {
                    var gifts = new List<InventoryItem>();
                    if (q.GiveItemOnAccept != null) gifts.Add(q.GiveItemOnAccept);
                    if (q.GiveItemsOnAccept != null) foreach (var g in q.GiveItemsOnAccept) if (g != null) gifts.Add(g);
                    foreach (var g in gifts) Received(g.itemID, Math.Max(1, g.amount));
                }
                return;
            }
            // Handed in: what the game takes and gives, as Quest.Complete does.
            _handedIn.Add(key);
            try { Spend(q); }
            catch (Exception ex) { Once("Quest items not taken: " + ex.GetType().Name + ": " + ex.Message); }
            try
            {
                var player = GlobalManager.global.controlledChar;
                if (player != null && player.zFLevelController != null) player.zFLevelController.AddExperience(q.RewardExperience);
            }
            catch (Exception ex) { Once("Quest experience not added: " + ex.GetType().Name + ": " + ex.Message); }
            if (received > 0 && q.RewardItems != null)
                foreach (var reward in q.RewardItems) if (reward != null && reward.amount > 0) Received(reward.ID, reward.amount);
            Regenerate();
        }

        private static void Spend(Quest q)
        {
            var need = q.RequiresItem;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            if (need == null || player == null) return;
            int left = need.ItemAmount;
            for (int guard = 0; left > 0 && guard < 64; guard++)
            {
                var item = player.GetItemByID(need.ItemID);
                if (item == null || item.inInventory == null) break;
                int take = Math.Min(left, item.amount);
                if (take < 1) break;
                item.inInventory.ExtractUnsafe(item.itemID, take);
                left -= take;
            }
        }

        // ---- Host: the guest's requests on the host's own quests ----

        public byte Accept(uint key, string scene, bool room, out object[] gifts)
        {
            gifts = new object[0];
            byte status;
            var q = HostQuest(key, scene, out status);
            if (q == null) return status;
            if (q.Accepted || q.Completed) return LanStorage.Ok;
            bool hasGifts = q.GiveItemOnAccept != null;
            if (q.GiveItemsOnAccept != null) foreach (var g in q.GiveItemsOnAccept) hasGifts |= g != null;
            if (hasGifts && !room) return LanStorage.TooLarge;
            var diverted = new List<InventoryItem>();
            _diverted = diverted;
            try { q.Accept(); }
            catch (Exception ex) { Once("Quest not accepted natively: " + ex.GetType().Name + ": " + ex.Message); }
            finally { _diverted = null; }
            if (!q.Accepted) return LanStorage.Invalid;
            var given = new List<object>();
            foreach (var item in diverted)
            {
                if (item == null) continue;
                try
                {
                    var entry = LanStorage.Normalize(new object[] { item.itemID, Math.Max(1, item.amount), item.Save() });
                    if (!LanStorage.ItemFits(entry)) throw new InvalidDataException("too large");
                    given.Add(entry);
                    item.gameObject.SetActive(false); Object.Destroy(item.gameObject);
                }
                catch (Exception ex) { Once("Quest gift kept by the host: " + ex.GetType().Name + ": " + ex.Message); }
                if (given.Count == LanStorage.MaxQuestItems) break;
            }
            gifts = given.ToArray();
            LanNotify.Message(LanSkinPicker.PartnerLabel(true) + " взял квест «" + Title(q) + "»");
            return LanStorage.Ok;
        }

        public byte Prepare(uint key, string scene, out int[] need, out object[] rewards)
        {
            need = null; rewards = new object[0];
            byte status;
            var q = HostQuest(key, scene, out status);
            if (q == null) return status;
            if (!q.Accepted || !q.PreCompleted || q.Completed) return LanStorage.NotReady;
            if (q.RequiresItem != null && q.RequiresItem.ItemAmount > 0) need = new[] { q.RequiresItem.ItemID, q.RequiresItem.ItemAmount };
            var list = new List<object>();
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            if (q.RewardItems != null)
                foreach (var reward in q.RewardItems)
                {
                    if (reward == null || reward.amount <= 0) continue;
                    if (prefabs == null || reward.ID < 0 || reward.ID >= prefabs.Length || prefabs[reward.ID] == null) return LanStorage.Invalid;
                    list.Add(new object[] { reward.ID, Math.Min(reward.amount, LanStorage.MaxAmount), new object[4] });
                }
            rewards = list.ToArray();
            return LanStorage.Ok;
        }

        public void Complete(uint key, string scene)
        {
            byte status;
            var q = HostQuest(key, scene, out status);
            if (q == null) return;
            try { q.SetCompleted(true); }
            catch (Exception ex) { Once("Quest completion effects failed: " + ex.GetType().Name + ": " + ex.Message); q.Completed = true; }
            Regenerate();
            LanNotify.Message(LanSkinPicker.PartnerLabel(true) + " сдал квест «" + Title(q) + "»");
            LanGuestCredits.Xp(q.RewardExperience); // 1.3.0: the guest's experience for it
            // 1.4.5: the host gets the same reward, as Quest.Complete gives its player.
            try
            {
                var player = GlobalManager.global.controlledChar;
                if (player != null && player.zFLevelController != null) player.zFLevelController.AddExperience(q.RewardExperience);
                if (player != null && player.inventory != null && q.RewardItems != null)
                    foreach (var reward in q.RewardItems)
                        if (reward != null && reward.amount > 0) { player.inventory.Add(reward.ID, reward.amount, false, true); Received(reward.ID, reward.amount); }
            }
            catch (Exception ex) { Once("Host quest reward not given: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public byte Share(uint key, string scene, bool room, out object[] rewards)
        {
            rewards = new object[0];
            byte status;
            var q = HostQuest(key, scene, out status);
            if (q == null) return status;
            string me = GuestProfile?.Invoke(), owner, id = scene + "|" + key;
            if (string.IsNullOrEmpty(me) || !q.Completed || !_rewardFor.TryGetValue(id, out owner) || owner != me) return LanStorage.NotReady;
            var list = new List<object>();
            var prefabs = ItemsDataBase.Global == null ? null : ItemsDataBase.Global.itemPrefab;
            if (q.RewardItems != null)
                foreach (var reward in q.RewardItems)
                {
                    if (reward == null || reward.amount <= 0) continue;
                    if (prefabs == null || reward.ID < 0 || reward.ID >= prefabs.Length || prefabs[reward.ID] == null) return LanStorage.Invalid;
                    list.Add(new object[] { reward.ID, Math.Min(reward.amount, LanStorage.MaxAmount), new object[4] });
                }
            if (list.Count > LanStorage.MaxQuestItems) return LanStorage.TooLarge;
            if (list.Count > 0 && !room) return LanStorage.TooLarge; // kept until there is room
            _rewardFor.Remove(id);
            LanGuestCredits.Xp(q.RewardExperience);
            LanNotify.Message(LanSkinPicker.PartnerLabel(true) + " получил награду за квест «" + Title(q) + "»");
            rewards = list.ToArray();
            return LanStorage.Ok;
        }

        private static Quest HostQuest(uint key, string scene, out byte status)
        {
            status = LanStorage.Ok;
            if (scene != CurrentScene() || SceneQuests() == null) { status = LanStorage.Gone; return null; }
            var q = Find(key, scene);
            if (q == null) status = LanStorage.NotFound;
            return q;
        }

        // Host: the game gives the accepting player its gifts; for the guest they are kept aside.
        private static bool GiftPrefix(InventoryItem item)
        {
            var diverted = _diverted;
            if (diverted == null) return true;
            if (item != null) diverted.Add(item);
            return false;
        }

        // Host: a goal reached by the guest counts too.
        private static void AtPointPostfix(ETPlayerAtPoint __instance)
        {
            try
            {
                var guest = GuestPosition == null ? null : GuestPosition();
                if (guest == null || __instance.Done || __instance.Position == null) return;
                if (Vector3.Distance(__instance.Position.position, guest.Value) < __instance.Radius) __instance.SetDone(true);
            }
            catch (Exception ex) { Once("Quest point not checked for the guest: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void HaveItemPostfix(ETPlayerHaveItem __instance)
        {
            try
            {
                var count = GuestCount;
                if (count == null || __instance.Done || __instance.ItemAmount < 1) return;
                if (count(__instance.ItemID) >= __instance.ItemAmount) __instance.SetDone(true);
            }
            catch (Exception ex) { Once("Quest items not checked for the guest: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---- Display ----

        private static string Localized(string key)
        {
            try { return LocalizationCore.GetTextByKey(key); } catch (Exception) { return key; }
        }
        private static string Title(Quest q) { return Localized(q.Title); }

        private static void Received(int id, int amount)
        {
            string name;
            try { name = ItemsDataBase.Global.GetItemNameByID(id); } catch (Exception) { name = LanStorageGame.ItemName(id); }
            Notify(Localized("Received item") + string.Format(": {0} {1}", amount, name), false);
        }

        private static void Notify(string text, bool green, bool red = false)
        {
            try
            {
                var n = NotificationManager.Global;
                if (n == null) { LanNotify.Message(text); return; }
                if (red) n.SetColorRed(); else if (green) n.SetColorGreen();
                n.MakeNotification(text);
            }
            catch (Exception) { LanNotify.Message(text); }
        }

        private static void Regenerate()
        {
            try { var gui = Zompiercer.Singleton<GuiCore>.Global; if (gui != null && gui.questsWindow != null) gui.questsWindow.Regenerate(); }
            catch (Exception) { }
        }

        private static void Once(string text)
        {
            if (_hookLogged) return;
            _hookLogged = true; _log?.Invoke(text);
        }
    }
}
