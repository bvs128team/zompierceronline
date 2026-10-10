using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ZompiercerLAN
{
    // Host-side guest profiles of the current world, and their sidecar file inside a
    // native save slot. The sidecar records SHA-256 of the three native save files:
    // it is used only with exactly the checkpoint it was written for, so loading an
    // older save also restores the guests' older state (no item duplication through
    // rollback). A missing or mismatching sidecar starts a new world lineage.
    // No Unity APIs; the caller decides when native saves complete.
    internal sealed class LanGuestStore
    {
        internal const string FileName = "ZompiercerLAN-guests.dat";
        internal const int MaxProfiles = 16, MaxTotalBytes = 200 * 1024;
        internal static readonly string[] NativeFiles = { "SceneData.scene", "SaveData.sdt", "TrainAndChar.tac" };
        private const string Magic = "ZLANGUESTS";
        private const int Version = 2;
        internal const int MaxWelcomeReceipts = 512;
        private readonly HashSet<string> _welcomed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _profiles = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long _order;
        private sealed class Entry { internal byte[] State; internal string Utc; internal long Order; }

        internal long Generation { get; private set; }
        internal Guid WorldId { get; private set; }
        // True once this lineage has a sidecar; later saves keep writing it even
        // when no guest is currently stored, so the lineage id survives.
        internal bool Persistent { get; private set; }
        internal int Count { get { return _profiles.Count; } }
        // The live guest's batched ledger changes, stored before a sidecar is written.
        internal Action BeforeWrite;

        internal Guid EnsureWorld() { if (WorldId == Guid.Empty) NewWorld(); return WorldId; }
        internal void NewWorld() { Generation++; WorldId = Guid.NewGuid(); Persistent = false; _profiles.Clear(); _welcomed.Clear(); }

        internal bool WasWelcomed(string profileId) { return _welcomed.Contains(profileId); }
        internal bool CanWelcome(string profileId)
        { return ValidProfileId(profileId) && !_welcomed.Contains(profileId) && _welcomed.Count < MaxWelcomeReceipts; }
        // Never evict receipts: losing an ownership profile must not mint another gift.
        internal bool RecordWelcome(string profileId)
        {
            if (!CanWelcome(profileId)) return false;
            EnsureWorld(); _welcomed.Add(profileId); return true;
        }

        internal byte[] Get(string profileId)
        {
            Entry entry;
            return profileId != null && _profiles.TryGetValue(profileId, out entry) ? (byte[])entry.State.Clone() : null;
        }

        // `state` must already be a validated guest blob. The oldest profiles are
        // dropped when the per-world limits are exceeded.
        internal void Put(string profileId, byte[] state, DateTime utc)
        {
            if (!ValidProfileId(profileId) || state == null || state.Length < 1 || state.Length > LanProtocol.MaxGuestBytes)
                throw new ArgumentException("Guest profile entry");
            EnsureWorld();
            _profiles[profileId] = new Entry { State = (byte[])state.Clone(), Utc = utc.ToUniversalTime().ToString("o"), Order = ++_order };
            while (_profiles.Count > MaxProfiles || Total() > MaxTotalBytes)
            {
                string oldest = null; long order = long.MaxValue;
                foreach (var pair in _profiles) if (pair.Value.Order < order && pair.Key != profileId) { order = pair.Value.Order; oldest = pair.Key; }
                if (oldest == null) break;
                _profiles.Remove(oldest);
            }
        }

        // Profile key for a guest world token (LanPlayerIdentity.WorldToken): the host
        // never stores the token itself.
        internal static string ProfileIdFor(byte[] token)
        {
            if (token == null || token.Length != LanProtocol.ProfileTokenBytes) throw new ArgumentException("Invalid profile token.");
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(token)).Replace("-", "");
        }

        internal static bool ValidProfileId(string id)
        {
            if (id == null || id.Length != 64) return false;
            foreach (char c in id) if (!(c >= '0' && c <= '9' || c >= 'A' && c <= 'F')) return false;
            return true;
        }

        // Writes the sidecar after a completed native save. Returns false (and keeps
        // any previous sidecar) when there is nothing to keep or a file is unsuitable.
        internal bool Write(string slot, out string result)
        {
            result = null;
            var flush = BeforeWrite;
            if (flush != null) flush();
            if (WorldId == Guid.Empty || _profiles.Count == 0 && _welcomed.Count == 0 && !Persistent) { result = "nothing to store"; return false; }
            string[] hashes;
            if (!NativeHashes(slot, out hashes, out result)) return false;
            var profiles = new List<object>();
            foreach (var pair in _profiles) profiles.Add(new object[] { pair.Key, pair.Value.Utc, pair.Value.State });
            var root = new object[] { Magic, Version, WorldId.ToString("N"), hashes[0], hashes[1], hashes[2], profiles.ToArray(), new List<string>(_welcomed).ConvertAll<object>(id => id).ToArray() };
            byte[] bytes = LanValueCodec.Encode(root);
            string target = Path.Combine(slot, FileName);
            string temporary = Path.Combine(slot, ".ZompiercerLAN-guests-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                RejectLinks(target);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                if (File.Exists(target))
                {
                    try { File.Replace(temporary, target, null); }
                    catch (PlatformNotSupportedException) { File.Delete(target); File.Move(temporary, target); }
                }
                else File.Move(temporary, target);
                Persistent = true;
                result = _profiles.Count + " guest profile(s), " + bytes.Length + " bytes";
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // Called after a native load completed. Returns true when the slot's sidecar was loaded (1.4.14: also when
        // the game's files changed after it was written); otherwise the store starts a new, empty lineage.
        internal bool Load(string slot, out string result)
        {
            Generation++; _profiles.Clear(); _welcomed.Clear(); WorldId = Guid.Empty; Persistent = false;
            string path = Path.Combine(slot ?? "", FileName);
            try
            {
                if (slot == null || !File.Exists(path)) { NewWorld(); result = "no guest sidecar; new world lineage"; return false; }
                RejectLinks(path);
                var info = new FileInfo(path);
                if (info.Length < 1 || info.Length > LanValueCodec.MaxBytes) throw new InvalidDataException("sidecar size");
                object[] root = LanValueCodec.Decode(File.ReadAllBytes(path));
                if (root.Length < 2 || !Magic.Equals(root[0]) || !(root[1] is int)) throw new InvalidDataException("sidecar header");
                int version = (int)root[1];
                if (!(version == 1 && root.Length == 7 || version == Version && root.Length == 8)) throw new InvalidDataException("sidecar version");
                Guid world;
                if (!(root[2] is string) || !Guid.TryParseExact((string)root[2], "N", out world) || world == Guid.Empty) throw new InvalidDataException("sidecar world id");
                string[] hashes;
                if (!NativeHashes(slot, out hashes, out result)) throw new InvalidDataException(result);
                // 1.4.14: the game's files changed after the sidecar was written. That is still this slot's world: the
                // slot was saved again while this mod knew no guest in the game's run (a run that loads a slot without
                // a sidecar, or this very case before 1.4.14, writes nothing until a guest joins), or copied over by
                // hand. Its guests keep their profiles, their identity at this host (the world id) and their welcome
                // receipts; the next save writes the sidecar anew. Before, they started fresh and the stale sidecar
                // stayed, so every later load reset them again: a new profile (and a new gift) every session.
                bool same = true;
                for (int i = 0; i < 3; i++) if (!hashes[i].Equals(root[3 + i])) same = false;
                var profiles = root[6] as object[];
                if (profiles == null || profiles.Length > MaxProfiles) throw new InvalidDataException("sidecar profiles");
                var loaded = new Dictionary<string, Entry>(StringComparer.Ordinal);
                int total = 0;
                foreach (var p in profiles)
                {
                    var e = p as object[];
                    if (e == null || e.Length != 3 || !ValidProfileId(e[0] as string) || !(e[1] is string) || !(e[2] is byte[])) throw new InvalidDataException("sidecar entry");
                    var state = (byte[])e[2];
                    LanGuestSchema.Decode(state); // a stored profile is re-validated before use
                    total += state.Length;
                    if (total > MaxTotalBytes || loaded.ContainsKey((string)e[0])) throw new InvalidDataException("sidecar entries");
                    loaded[(string)e[0]] = new Entry { State = state, Utc = (string)e[1], Order = ++_order };
                }
                var receipts = new HashSet<string>(StringComparer.Ordinal);
                if (version == 1)
                {
                    // Existing guests are not new visitors on upgrade.
                    foreach (var id in loaded.Keys) receipts.Add(id);
                }
                else
                {
                    var entries = root[7] as object[];
                    if (entries == null || entries.Length > MaxWelcomeReceipts) throw new InvalidDataException("welcome receipts");
                    foreach (var id in entries)
                        if (!ValidProfileId(id as string) || !receipts.Add((string)id)) throw new InvalidDataException("welcome receipt");
                }
                WorldId = world; Persistent = true;
                foreach (var id in receipts) _welcomed.Add(id);
                foreach (var pair in loaded) _profiles.Add(pair.Key, pair.Value);
                result = same ? "guest sidecar verified: " + _profiles.Count + " profile(s)"
                    : "guest sidecar kept although the game files changed after it was written: " + _profiles.Count + " profile(s)";
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is EndOfStreamException)
            {
                NewWorld();
                result = "guest sidecar rejected (" + ex.GetType().Name + ": " + ex.Message + "); guests start fresh";
                return false;
            }
        }

        private static bool NativeHashes(string slot, out string[] hashes, out string error)
        {
            hashes = new string[NativeFiles.Length]; error = null;
            if (string.IsNullOrEmpty(slot) || !Directory.Exists(slot)) { error = "save slot missing"; return false; }
            RejectLinks(slot);
            for (int i = 0; i < NativeFiles.Length; i++)
            {
                string file = Path.Combine(slot, NativeFiles[i]);
                if (!File.Exists(file)) { error = "native save file missing: " + NativeFiles[i]; return false; }
                RejectLinks(file);
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create()) hashes[i] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            return true;
        }

        private int Total() { int total = 0; foreach (var e in _profiles.Values) total += e.State.Length; return total; }

        private static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked save path: " + current);
        }
    }
}
