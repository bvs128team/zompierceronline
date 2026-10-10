using System;
using System.IO;

namespace ZompiercerLAN
{
    // Host half of guest persistence for one paired guest. Identity first: the
    // guest proves which profile it owns with a per-world token. Then the host says
    // whether it has a stored profile and sends it; only after the guest has the
    // profile (acknowledged restore, or "none") does the host accept new states,
    // so a fresh character can never overwrite a stored one.
    // 0.7.0: every state is reconciled with the host's ledger of what the guest may
    // own (LanGuestLedger). A new guest's first belongings wait for the host's
    // decision; a guest that keeps claiming more than the ledger gets a correction
    // through the restore channel. No Unity APIs.
    internal sealed class LanGuestHost
    {
        private const double RequestEvery = 2, StatusEvery = 2, ChunksPerSecond = 40, Retry = 2, CorrectionEvery = 10, SaveEvery = 1;
        private readonly LanGuestStore _store;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly LanGuestLedger _ledger;
        private readonly Func<int> _backpack;
        private readonly Func<object[]> _welcomeGift;
        private readonly ILedgerRules _rules;
        private readonly LanBlobSender _restore = new LanBlobSender();
        private readonly LanBlobReceiver _states = new LanBlobReceiver();
        private Guid _world;
        private string _profile;
        private int _restoreRevision, _suspect;
        private bool _active, _confirmed, _unsaved;
        private double _nextRequest, _nextStatus, _nextCorrection, _nextSave;
        private object[] _candidate; // a new guest's first state, waiting for the host's decision
        internal string Status { get; private set; }
        internal string ProfileId { get { return _profile; } }
        internal LanGuestLedger Ledger { get { return _ledger; } }
        // The guest is identified, its states are kept and its belongings are on the
        // ledger: storages and combat may move items to and from it.
        private long _generation;
        internal bool Confirmed { get { return _generation == _store.Generation && _active && _profile != null && _confirmed && _ledger.Ready && _restore.Done; } }
        // A new guest's first belongings, shown to the host for a decision.
        internal object[] PendingKit { get { return _active && !_ledger.Ready ? _candidate : null; } }

        internal LanGuestHost(LanGuestStore store, Action<Packet> send, Action<string> log, ILedgerRules rules, Func<int> backpack, Func<object[]> welcomeGift = null)
        {
            _store = store; _send = send; _log = log; _ledger = new LanGuestLedger(rules); _backpack = backpack; _welcomeGift = welcomeGift; _rules = rules;
            // 1.0.0: the stored profile follows the ledger, so a reconnect or a game save
            // between two guest uploads neither loses a taken item nor duplicates a put one.
            _ledger.Changed = LedgerChanged; _store.BeforeWrite = Flush;
            Status = "Профиль друга: ожидание подключения";
        }

        // A new secure session: identity is asked again (same token, same profile).
        internal void Begin()
        {
            Flush(); _unsaved = false;
            _active = true; _world = _store.EnsureWorld(); _generation = _store.Generation; _profile = null; _confirmed = false; _restoreRevision = 0;
            _nextRequest = 0; _restore.Cancel(); _states.Reset(); _ledger.Clear(); _candidate = null; _suspect = 0;
            Status = "Профиль друга: проверка";
        }
        internal void End() { Flush(); _unsaved = false; _active = false; _restore.Cancel(); _ledger.Clear(); _candidate = null; }

        internal void Tick(double now)
        {
            if (!_active) return;
            if (_store.WorldId != _world || _store.Generation != _generation) Begin(); // the host loaded another world lineage
            if (_unsaved && now >= _nextSave) { _nextSave = now + SaveEvery; SaveLedger(); }
            if (_profile == null)
            {
                if (now >= _nextRequest) { _nextRequest = now + RequestEvery; _send(new Packet { Kind = PacketKind.IdentityRequest, Chunk = _world.ToByteArray() }); }
                return;
            }
            if (now >= _nextStatus && (!_confirmed || !_restore.Done)) { _nextStatus = now + StatusEvery; SendStatus(); }
            _restore.Tick(now, ChunksPerSecond, Retry, _send, PacketKind.GuestRestoreChunk);
        }

        internal void Receive(Packet p, double now)
        {
            if (!_active) return;
            if (_store.Generation != _generation || _store.WorldId != _world) { Begin(); return; }
            switch (p.Kind)
            {
                case PacketKind.IdentityProof:
                    string id = LanGuestStore.ProfileIdFor(p.Chunk);
                    if (_profile != null) { if (id == _profile && !_confirmed) SendStatus(); return; } // repeated proof
                    _profile = id;
                    var stored = _store.Get(id);
                    if (stored != null)
                    {
                        try { _ledger.Reset(LanGuestSchema.Decode(stored), _backpack()); }
                        catch (InvalidDataException ex) { _log("Stored guest profile unusable: " + ex.Message); stored = null; }
                    }
                    if (stored != null) { _restoreRevision = _restore.Start(stored); Status = "Профиль друга найден; отправка"; }
                    else { _restoreRevision = 0; Status = "Новый профиль друга"; }
                    _nextStatus = now + StatusEvery; SendStatus();
                    _log("Guest identified (profile " + id.Substring(0, 8) + "…); stored profile: " + (stored != null ? stored.Length + " bytes" : "none"));
                    break;
                case PacketKind.GuestRestoreAck:
                    if (_profile == null || p.Revision != _restoreRevision || _restoreRevision == 0) return;
                    _restore.Acknowledge(p.Revision);
                    if (!_confirmed) { _confirmed = true; Status = "Профиль друга восстановлен; вещи на учёте у хоста"; }
                    break;
                case PacketKind.GuestStateChunk:
                    if (_profile == null) return;
                    if (!_confirmed)
                    {
                        // States before the guest has its stored profile would overwrite it.
                        if (_restoreRevision != 0) return;
                        _confirmed = true; Status = "Новый друг: проверьте его вещи в панели";
                    }
                    if (!_restore.Done) return; // correction must be applied before accepting inventories
                    bool ack;
                    byte[] blob = _states.Accept(p, out ack);
                    if (blob != null) State(blob, now);
                    if (ack) _send(new Packet { Kind = PacketKind.GuestStateAck, Revision = p.Revision });
                    break;
            }
        }

        // Item moves are stored at once; per-shot rounds and wear at most once per
        // SaveEvery, and always before the ledger is cleared or the store is written.
        private void LedgerChanged(bool now) { _unsaved = true; if (now) SaveLedger(); }
        private void Flush() { if (_unsaved) SaveLedger(); }

        private void SaveLedger()
        {
            // A ledger of another world lineage is never stored over the loaded one.
            if (!_active || _profile == null || !_ledger.Ready ||
                _world != _store.WorldId || _generation != _store.Generation) return;
            try
            {
                var state = LanValueCodec.Encode(_ledger.Profile);
                LanGuestSchema.Decode(state);
                _store.Put(_profile, state, DateTime.UtcNow);
                _unsaved = false;
            }
            catch (Exception ex) { _log("Guest profile not stored: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void State(byte[] blob, double now)
        {
            object[] state;
            // Chunk rate limits bound this to about one 64 KiB state per second.
            try { state = LanGuestSchema.Decode(blob); }
            catch (InvalidDataException ex) { _log("Guest state rejected: " + ex.Message); return; }
            if (!_ledger.Ready) { _candidate = state; return; }
            var fixes = new System.Collections.Generic.List<LedgerFix>();
            object[] accepted;
            try { accepted = _ledger.Reconcile(state, LanGuestSchema.Acks(state), fixes); }
            catch (InvalidDataException ex) { _log("Guest state rejected: " + ex.Message); return; }

            if (fixes.Count == 0) { _suspect = 0; Status = "Профиль друга сохранён в памяти (" + DateTime.Now.ToString("HH:mm:ss") + "); на диск — при сохранении игры"; return; }
            // One stale state can legitimately overlap a hand-out; the same excess twice cannot.
            if (++_suspect < 2 || now < _nextCorrection) return;
            _suspect = 0; _nextCorrection = now + CorrectionEvery;
            var stats = _ledger.LastStatReasons;
            int items = fixes.Count - (stats == null ? 0 : 1);
            _log("Guest claimed " + (items > 0 ? items + " item change(s) beyond the host's ledger" : "") + (items > 0 && stats != null ? " and " : "") +
                (stats != null ? "more than the host can explain: " + string.Join(", ", stats.ToArray()) : "") + "; correction sent");
            Status = stats != null ? "У друга были уровень, здоровье или потребности сверх объяснимого; отправлена коррекция"
                : "У друга были вещи сверх учёта хоста; отправлена коррекция";
            SendCorrection(accepted);
        }

        // The host's decision on a new guest's first belongings.
        internal void AcceptKit(bool keep)
        {
            var kit = PendingKit;
            if (kit == null) return;
            if (!keep)
            {
                var character = (object[])((object[])kit[12]).Clone();
                var empty = new object[((object[])character[0]).Length];
                for (int i = 0; i < empty.Length; i++) empty[i] = new object[0];
                character[0] = empty;
                var guns = new object[((object[])character[5]).Length];
                for (int i = 0; i < guns.Length; i++) guns[i] = new object[] { 0 };
                character[5] = guns;
                kit = (object[])kit.Clone(); kit[12] = character;
            }
            // The welcome item is host-issued only, even if hidden in a mod tree.
            kit = LanGuestSchema.Decode(LanValueCodec.Encode(kit));
            var inventories = (object[])((object[])kit[12])[0];
            bool changed = !keep;
            for (int i = 0; i < inventories.Length; i++) inventories[i] = StripWelcome((object[])inventories[i], ref changed);
            bool gifted = false;
            if (_welcomeGift != null && _store.CanWelcome(_profile))
            {
                try
                {
                    var gift = _welcomeGift();
                    if (gift != null)
                    {
                        LanGuestSchema.Inventory(new object[] { gift }, 0);
                        int backpack = _backpack();
                        if (backpack < 0 || backpack >= inventories.Length) backpack = 0;
                        var items = (object[])inventories[backpack];
                        if ((int)gift[1] != 1 || _rules.Item((int)gift[0]) == null) throw new InvalidDataException("welcome gift unavailable");
                        if (items.Length < LanGuestSchema.MaxItems)
                        {
                            var next = new object[items.Length + 1]; Array.Copy(items, next, items.Length); next[items.Length] = gift;
                            inventories[backpack] = next;
                            // Check the full profile's encoded size before committing its receipt.
                            try { gifted = LanValueCodec.Encode(kit).Length <= LanProtocol.MaxGuestBytes; }
                            finally { if (!gifted) inventories[backpack] = items; }
                        }
                    }
                }
                catch (Exception ex) { _log("Welcome gift unavailable: " + ex.Message); }
            }
            _ledger.Reset(kit, _backpack());
            if (gifted && !_store.RecordWelcome(_profile)) throw new InvalidOperationException("Welcome receipt unavailable");
            _candidate = null;
            var profile = _ledger.Profile;
            _store.Put(_profile, LanValueCodec.Encode(profile), DateTime.UtcNow);
            Status = keep ? "Вещи друга приняты; теперь они на учёте у хоста" : "Друг начинает без вещей";
            _log("New guest's belongings " + (keep ? "accepted" : "refused") + " by the host");
            if (gifted) { Status += "; приветственный подарок добавлен в рюкзак"; _log("Guest welcome gift recorded"); }
            if (changed || gifted) SendCorrection(profile);
        }

        private static object[] StripWelcome(object[] items, ref bool changed)
        {
            var kept = new System.Collections.Generic.List<object>();
            foreach (object[] item in items)
            {
                if ((int)item[0] == 4095) { changed = true; continue; }
                var extra = (object[])item[2];
                var mods = extra[3] as object[];
                if (mods != null) for (int i = 0; i < mods.Length; i++)
                    if (mods[i] != null) mods[i] = StripWelcome((object[])mods[i], ref changed);
                kept.Add(item);
            }
            return kept.ToArray();
        }

        private void SendCorrection(object[] profile)
        {
            _restoreRevision = _restore.Start(LanValueCodec.Encode(profile));
            _nextStatus = 0; SendStatus();
        }

        private void SendStatus()
        { _send(new Packet { Kind = PacketKind.GuestStatus, Action = (byte)(_restoreRevision != 0 ? 1 : 0), Revision = _restoreRevision }); }
    }

    // Guest half. Answers identity requests, receives an optional stored profile,
    // hands it to the game exactly once per pairing, then uploads its own state.
    internal sealed class LanGuestClient
    {
        private const double ProfileTimeout = 30, CaptureEvery = 10, ChunksPerSecond = 40, Retry = 2;
        private readonly Func<Guid, byte[]> _token;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly LanBlobSender _upload = new LanBlobSender();
        private readonly LanBlobReceiver _restore = new LanBlobReceiver();
        private bool _statusKnown, _applied, _blocked, _identityFailed, _captureSoon, _late;
        private int _expected, _appliedRevision;
        private object[] _pending;
        private byte[] _lastSent;
        private double _began, _nextCapture;
        internal string Status { get; private set; }
        internal bool Blocked { get { return _blocked; } }
        // This character's progress is uploaded to the host (profile applied or new).
        internal bool Saving { get { return _statusKnown && _applied && !_blocked && !_identityFailed && (_expected == 0 || _appliedRevision == _expected); } }
        // After an item moved between the guest and a host storage: upload promptly.
        internal void CaptureSoon() { _captureSoon = true; }

        internal LanGuestClient(Func<Guid, byte[]> token, Action<Packet> send, Action<string> log)
        { _token = token; _send = send; _log = log; Status = "Профиль: ожидание хоста"; }

        // New secure session; a profile applied earlier in this pairing stays applied.
        internal void Begin(double now)
        {
            _statusKnown = false; _expected = 0; _appliedRevision = 0; _restore.Reset(); _upload.Cancel(); _began = now;
            _lastSent = null; _nextCapture = 0; // the new session gets a fresh upload
        }

        // While true the game should hold the first placement (bounded by a timeout).
        internal bool Waiting(double now)
        {
            if (_applied || _blocked || _identityFailed || _late) return false;
            if (now - _began > ProfileTimeout)
            {
                // A long scene load or a lossy start: the player goes in, the profile is
                // still awaited and applied in play when it arrives (CorrectionPending).
                _late = true; Status = "Хост ещё не прислал профиль; ждём его в игре";
                _log("Guest profile: no answer from the host yet; the player goes in and the profile is applied when it arrives");
                return false;
            }
            return !_statusKnown || _expected != 0 && _pending == null;
        }

        internal void Receive(Packet p, double now)
        {
            switch (p.Kind)
            {
                case PacketKind.IdentityRequest:
                    byte[] token = null;
                    try { token = _token(new Guid(p.Chunk)); } catch (Exception ex) { _log("Guest identity unavailable: " + ex.GetType().Name); }
                    if (token == null || token.Length != LanProtocol.ProfileTokenBytes)
                    {
                        _identityFailed = true; Status = "Ключ игрока недоступен; профиль не сохраняется";
                        return;
                    }
                    _send(new Packet { Kind = PacketKind.IdentityProof, Chunk = token });
                    break;
                case PacketKind.GuestStatus:
                    if (_statusKnown && p.Revision == _expected) return;
                    _statusKnown = true; _expected = p.Revision;
                    if (_expected != 0) { _upload.Cancel(); _lastSent = null; }
                    if (_expected == 0 && !_applied) Status = "Хост создаёт новый профиль";
                    else if (_expected != 0 && !_applied) Status = "Получение профиля от хоста";
                    else if (_expected != 0) Status = "Хост присылает вещи по своему учёту";
                    break;
                case PacketKind.GuestRestoreChunk:
                    if (!_statusKnown || p.Revision != _expected) return;
                    bool ack;
                    byte[] blob = _restore.Accept(p, out ack);
                    // After the first restore, a new revision is the host's correction.
                    if (blob != null && _pending == null)
                    {
                        try { _pending = LanGuestSchema.Decode(blob); }
                        catch (InvalidDataException ex)
                        {
                            _blocked = true; Status = "Профиль от хоста повреждён; прогресс этого входа не сохранится";
                            _log("Guest profile rejected: " + ex.Message);
                        }
                    }
                    if (ack && _appliedRevision == p.Revision) _send(new Packet { Kind = PacketKind.GuestRestoreAck, Revision = p.Revision });
                    break;
                case PacketKind.GuestStateAck:
                    _upload.Acknowledge(p.Revision);
                    break;
            }
        }

        // The validated stored profile, once; null when there is none to apply.
        // After the first restore: the host's ledger, to replace the local belongings.
        internal object[] Pending { get { return _pending; } }
        // A host correction, or a stored profile that arrived after the player went in.
        internal bool CorrectionPending { get { return (_applied || _late) && _pending != null; } }
        internal object[] TakePending() { var p = _pending; _pending = null; return p; }

        internal void Applied(bool success, string detail)
        {
            _applied = true;
            if (!success) { _blocked = true; Status = "Не удалось восстановить персонажа (" + detail + "); прогресс этого входа не сохранится"; }
            else
            {
                Status = detail;
                if (_expected != 0)
                {
                    _appliedRevision = _expected;
                    _send(new Packet { Kind = PacketKind.GuestRestoreAck, Revision = _expected });
                    _lastSent = null; _nextCapture = 0;
                }
            }
        }

        // canCapture: the guest is placed in the host's world. capture returns the
        // encoded profile or null. Unchanged states are not resent.
        internal void Tick(double now, bool canCapture, Func<byte[]> capture)
        {
            if (!_statusKnown || _blocked || _identityFailed || _expected != 0 && _appliedRevision != _expected) return;
            if (!_applied)
            {
                if (_expected != 0) return; // a stored profile is pending: never overwrite it
                if (!canCapture) return;
                _applied = true; Status = "Новый профиль; прогресс сохраняется у хоста";
            }
            if (_captureSoon) { _captureSoon = false; _nextCapture = Math.Min(_nextCapture, now + 0.5); }
            // A state captured before a pending correction is applied would only be corrected again.
            if (canCapture && _pending == null && now >= _nextCapture)
            {
                _nextCapture = now + CaptureEvery;
                byte[] state = null;
                try { state = capture(); } catch (Exception ex) { _log("Guest state capture failed: " + ex.GetType().Name + ": " + ex.Message); }
                if (state != null && (_lastSent == null || !Same(state, _lastSent)))
                {
                    _lastSent = state; _upload.Start(state);
                }
            }
            _upload.Tick(now, ChunksPerSecond, Retry, _send, PacketKind.GuestStateChunk);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
