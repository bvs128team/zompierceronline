using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ZompiercerLAN
{
    // Keeps the newest few LAN save backups, inventory checkpoint runs and client
    // session folders under BepInEx; older ones are deleted at game start.
    // Only folders whose names match the mod's own timestamped pattern are touched.
    // Deletion never follows links: a folder containing a junction/symlink, or a
    // linked parent, is left alone and reported. No Unity or game APIs.
    internal static class LanRetention
    {
        internal const int Keep = 5;
        private const int MaxEntries = 20000, MaxDepth = 64;
        private static readonly Regex Stamped = new Regex(@"^\d{8}-\d{6}-\d{7}-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex Session = new Regex(@"^\d{8}-\d{6}-\d{3}(-[0-9a-f]{32})?$", RegexOptions.CultureInvariant);
        internal const string BackupMarker = "LAN-backup-complete.txt";

        // protectedPath: a folder that must survive (the active client save folder), or null.
        internal static int Run(string bepInExRoot, string protectedPath, Action<string> info, Action<string> warn)
        {
            int removed = 0;
            removed += Prune(Path.Combine(bepInExRoot, "LAN-backups"), Stamped, true, protectedPath, info, warn);
            removed += Prune(Path.Combine(bepInExRoot, "LAN-inventory-checkpoints"), Stamped, false, protectedPath, info, warn);
            removed += Prune(Path.Combine(bepInExRoot, "LAN-sessions"), Session, false, protectedPath, info, warn);
            return removed;
        }

        // Backups: keep the newest `Keep` complete copies; an incomplete copy (no
        // marker) older than the newest complete one is a failed attempt and goes too.
        internal static int Prune(string root, Regex pattern, bool backups, string protectedPath, Action<string> info, Action<string> warn)
        {
            try
            {
                if (!Directory.Exists(root)) return 0;
                if (Linked(root)) { warn("LAN retention skipped: linked folder " + root); return 0; }
                var names = new List<string>();
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (pattern.IsMatch(name)) names.Add(name);
                }
                names.Sort(StringComparer.Ordinal); names.Reverse(); // newest first: names start with a UTC timestamp
                string guarded = protectedPath == null ? null : Path.GetFullPath(protectedPath).TrimEnd(Path.DirectorySeparatorChar);
                int kept = 0, removed = 0; bool newestComplete = false;
                foreach (string name in names)
                {
                    string dir = Path.Combine(root, name);
                    bool complete = !backups || File.Exists(Path.Combine(dir, BackupMarker));
                    bool keep;
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), guarded, StringComparison.OrdinalIgnoreCase)) keep = true;
                    else if (complete) keep = kept < Keep;
                    else keep = !newestComplete; // incomplete and newer than every complete copy: leave it
                    if (complete) { if (kept < Keep) kept++; newestComplete = true; }
                    if (keep) continue;
                    string reason;
                    if (DeleteTree(dir, out reason)) removed++;
                    else warn("LAN retention kept " + dir + ": " + reason);
                }
                if (removed != 0) info("LAN retention: removed " + removed + " old folder(s) from " + root + "; kept the newest " + Keep);
                return removed;
            }
            catch (Exception ex) { warn("LAN retention failed for " + root + ": " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // Two passes: first prove the tree has no links and is bounded, then delete
        // files and folders bottom-up. Nothing is deleted if the first pass fails.
        internal static bool DeleteTree(string dir, out string reason)
        {
            reason = null;
            var files = new List<string>(); var dirs = new List<string>();
            if (!Collect(dir, files, dirs, 0, ref reason)) return false;
            try
            {
                foreach (string file in files)
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { reason = "link appeared during deletion"; return false; }
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(file);
                }
                for (int i = dirs.Count - 1; i >= 0; i--) Directory.Delete(dirs[i], false); // empty by now; never recursive
                return true;
            }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        private static bool Collect(string dir, List<string> files, List<string> dirs, int depth, ref string reason)
        {
            if (depth > MaxDepth || files.Count + dirs.Count > MaxEntries) { reason = "folder tree exceeds limits"; return false; }
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { reason = "contains a link: " + dir; return false; }
            dirs.Add(dir);
            foreach (string file in Directory.GetFiles(dir))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { reason = "contains a link: " + file; return false; }
                files.Add(file);
            }
            foreach (string child in Directory.GetDirectories(dir))
                if (!Collect(child, files, dirs, depth + 1, ref reason)) return false;
            return true;
        }

        private static bool Linked(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
    }
}
