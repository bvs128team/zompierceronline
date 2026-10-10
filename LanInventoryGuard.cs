using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Zompiercer.Inventory;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    // Local evidence only. Never restores items or invokes the game's save/load methods.
    internal static class LanInventoryGuard
    {
        private const int MaxFiles = 10000;
        private const long MaxBytes = 512L * 1024 * 1024;
        private static string _backedUpSource;
        private static string _checkpointDirectory;
        private static int _checkpointCount;
        private static Snapshot _previous;
        private static Snapshot _lastNonempty;

        internal static void BackupSavesBeforeSession(ManualLogSource logger)
        {
            var saves = SaveLoadCore.global;
            if (saves == null || string.IsNullOrEmpty(saves.SavesPath))
                throw new InvalidOperationException("Save directory is not ready; LAN session was not started.");
            string source = Path.GetFullPath(saves.SavesPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(source, _backedUpSource, StringComparison.OrdinalIgnoreCase)) return;
            RejectLinks(source);
            string root = Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, "LAN-backups"));
            if (root.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root, source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Backup destination must be outside the save directory.");
            RejectLinks(root);
            var files = new List<string>();
            long total = 0;
            int directories = 0;
            if (Directory.Exists(source)) Collect(source, files, ref total, ref directories, 0);
            string destination = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            // A missing completion marker identifies an interrupted/failed backup.
            foreach (string file in files)
            {
                RejectLinks(file);
                string target = Path.Combine(destination, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total -= read;
                        if (total < 0) throw new IOException("Save files grew during backup; LAN session was not started.");
                        output.Write(buffer, 0, read);
                    }
                }
            }
            if (total != 0) throw new IOException("Save files changed size during backup; LAN session was not started.");
            File.WriteAllText(Path.Combine(destination, "LAN-backup-complete.txt"),
                "Source: " + source + Environment.NewLine + "Files: " + files.Count + Environment.NewLine +
                "UTC: " + DateTime.UtcNow.ToString("O") + Environment.NewLine, Encoding.UTF8);
            _backedUpSource = source;
            logger.LogInfo("Save backup completed before LAN session: " + destination + " (" + files.Count + " files)");
        }

        private static void Collect(string directory, List<string> files, ref long total, ref int directories, int depth)
        {
            if (++directories > MaxFiles || depth > 64) throw new IOException("Save directory structure exceeds backup limits.");
            RejectLinks(directory);
            foreach (string file in Directory.GetFiles(directory))
            {
                RejectLinks(file);
                files.Add(file);
                total += new FileInfo(file).Length;
                if (files.Count > MaxFiles || total > MaxBytes) throw new IOException("Save backup exceeds 10000 files or 512 MiB; LAN session was not started.");
            }
            foreach (string child in Directory.GetDirectories(directory)) Collect(child, files, ref total, ref directories, depth + 1);
        }

        internal static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked save/backup path: " + current);
        }

        internal static void CaptureCheckpoint(string reason, ManualLogSource logger)
        {
            try
            {
                var character = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                if (character == null) return;
                var snapshot = new Snapshot { Reason = reason, CharacterId = character.GetInstanceID(), Utc = DateTime.UtcNow.ToString("O") };
                AddInventory(snapshot, "backpack", character.inventory);
                if (character.beltInventory != null)
                    for (int i = 0; i < character.beltInventory.Length; i++) AddInventory(snapshot, "belt[" + i + "]", character.beltInventory[i]);
                if (character.eqipmentInventory != null)
                    for (int i = 0; i < character.eqipmentInventory.Length; i++) AddInventory(snapshot, "equipment[" + i + "]", character.eqipmentInventory[i]);
                snapshot.SelectedBelt = InventoryController.global == null ? -1 : InventoryController.global.selectedBeltIcon;
                var weapons = character.zombieFighterFireArmWeapon;
                if (weapons != null && weapons.gunList != null)
                    for (int i = 0; i < weapons.gunList.Count; i++)
                        if (weapons.gunList[i] != null) snapshot.Guns.Add("gun[" + i + "] magazine=" + weapons.gunList[i].magazine);
                if (_previous != null && (_previous.CharacterId != snapshot.CharacterId || _previous.Items.Count > snapshot.Items.Count))
                    logger.LogWarning("Inventory checkpoint changed: " + _previous.Reason + " -> " + reason + "; character " + _previous.CharacterId + " -> " + snapshot.CharacterId + "; stacks " + _previous.Items.Count + " -> " + snapshot.Items.Count + ". Evidence only; no automatic restore.");
                _previous = snapshot;
                if (snapshot.Items.Count > 0) _lastNonempty = snapshot;
                if (_checkpointDirectory == null)
                    _checkpointDirectory = Path.Combine(Paths.BepInExRootPath, "LAN-inventory-checkpoints", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N"));
                RejectLinks(_checkpointDirectory);
                Directory.CreateDirectory(_checkpointDirectory);
                File.WriteAllText(Path.Combine(_checkpointDirectory, "latest.txt"), snapshot.Render(), Encoding.UTF8);
                if (_checkpointCount < 128)
                    File.WriteAllText(Path.Combine(_checkpointDirectory, (++_checkpointCount).ToString("D3") + ".txt"), snapshot.Render(), Encoding.UTF8);
                if (snapshot.Items.Count == 0 && _lastNonempty != null)
                    File.WriteAllText(Path.Combine(_checkpointDirectory, "last-nonempty-before-empty.txt"), _lastNonempty.Render(), Encoding.UTF8);
                logger.LogInfo("Inventory checkpoint " + reason + ": character=" + snapshot.CharacterId + " stacks=" + snapshot.Items.Count + " directory=" + _checkpointDirectory);
            }
            catch (Exception ex) { logger.LogWarning("Inventory checkpoint could not be captured: " + ex.Message); }
        }

        private static void AddInventory(Snapshot snapshot, string slot, Inventory inventory)
        {
            snapshot.Slots.Add(slot + " instance=" + (inventory == null ? "missing" : inventory.GetInstanceID().ToString()));
            if (inventory == null || inventory.content == null) return;
            foreach (var item in inventory.content)
                if (item != null)
                    snapshot.Items.Add(new ItemRecord { Slot = slot, Id = item.itemID, Amount = item.amount,
                        Instance = item.GetInstanceID(), Owner = item.inInventory == null ? 0 : item.inInventory.GetInstanceID(),
                        WeaponDurability = item.weaponData == null ? (float?)null : item.weaponData.weaponDurability,
                        ShoesDurability = item.eqipment == null ? (float?)null : item.eqipment.shoesDurability });
        }

        private sealed class ItemRecord
        {
            internal string Slot;
            internal int Id, Amount, Instance, Owner;
            internal float? WeaponDurability, ShoesDurability;
        }

        private sealed class Snapshot
        {
            internal string Reason, Utc;
            internal int CharacterId, SelectedBelt;
            internal readonly List<string> Slots = new List<string>();
            internal readonly List<string> Guns = new List<string>();
            internal readonly List<ItemRecord> Items = new List<ItemRecord>();
            internal string Render()
            {
                var text = new StringBuilder("LAN inventory evidence v1; diagnostic only, not a restorable save\n");
                text.AppendLine("UTC=" + Utc).AppendLine("reason=" + Reason).AppendLine("character=" + CharacterId).AppendLine("selectedBelt=" + SelectedBelt);
                foreach (string slot in Slots) text.AppendLine(slot);
                foreach (var item in Items)
                    text.AppendLine(item.Slot + " id=" + item.Id + " amount=" + item.Amount + " instance=" + item.Instance + " owner=" + item.Owner +
                        " weaponDurability=" + (item.WeaponDurability.HasValue ? item.WeaponDurability.Value.ToString("R", CultureInfo.InvariantCulture) : "n/a") +
                        " shoesDurability=" + (item.ShoesDurability.HasValue ? item.ShoesDurability.Value.ToString("R", CultureInfo.InvariantCulture) : "n/a"));
                foreach (string gun in Guns) text.AppendLine(gun);
                return text.ToString();
            }
        }
    }
}
