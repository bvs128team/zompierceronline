using System;
using System.IO;

namespace ZompiercerLAN
{
    // Guest profile blob (LanValueCodec root). Structural rules only; no Unity or
    // game assembly, so the host and the offline checks use the same validator.
    //   [0] int format = 1          [1] string scene       [2] int location
    //   [3] int car index (-1..7)   [4..7] float x, y, z, yaw
    //   [8..11] float car-local x, y, z, yaw                [12] object[18] character
    //   [13] (format 2, 0.7.0) int[2] what the guest had applied when it captured
    //        the state: last storage/world request result, last combat state.
    // The character array mirrors ZombieFighterController.Save(): index 0 the
    // inventories (Inventory.Save() each), 1 the 27 indicator/level slots, 5 gun
    // magazines, 6 ZFLevelController.Save(), 7..16 health/belt/flags. Positions (3, 4)
    // and global buffs (17) are never stored: they are restored separately or not at all.
    internal static class LanGuestSchema
    {
        internal const int Format = 1, Format2 = 2, CharacterSlots = 18, StatSlots = 27;
        internal const int MaxInventories = 32, MaxItems = 512, MaxMods = 8, MaxGuns = 64;

        internal static object[] Decode(byte[] blob)
        {
            object[] root;
            try { root = LanValueCodec.Decode(blob); }
            catch (ArgumentException) { throw new InvalidDataException("Guest profile text encoding"); }
            catch (EndOfStreamException) { throw new InvalidDataException("Guest profile truncated"); }
            Validate(root);
            return root;
        }

        internal static void Validate(object[] root)
        {
            Require(root != null && (root.Length == 13 && Int(root[0]) == Format || root.Length == 14 && Int(root[0]) == Format2), "profile header");
            if (root.Length == 14)
            {
                var acks = root[13] as object[];
                Require(acks != null && acks.Length == 2 && acks[0] is int && acks[1] is int, "profile acknowledgements");
            }
            var scene = root[1] as string;
            Require(scene != null && scene.Length > 0 && scene.Length <= 96, "profile scene");
            int location = Int(root[2]), car = Int(root[3]);
            Require(location >= 0 && location <= 100000 && car >= -1 && car < LanProtocol.MaxCars, "profile place");
            for (int i = 4; i <= 11; i++) Require(root[i] is float && Math.Abs((float)root[i]) < 100000f, "profile position");
            Require((float)root[7] >= 0 && (float)root[7] <= 360 && (float)root[11] >= 0 && (float)root[11] <= 360, "profile yaw");
            if (car >= 0) for (int i = 8; i <= 10; i++) Require(Math.Abs((float)root[i]) <= 100f, "profile car-local position");
            Character(root[12] as object[]);
        }

        private static void Character(object[] c)
        {
            Require(c != null && c.Length == CharacterSlots, "character slots");
            var inventories = c[0] as object[];
            Require(inventories != null && inventories.Length >= 1 && inventories.Length <= MaxInventories, "inventory count");
            foreach (var inventory in inventories) Inventory(inventory as object[], 0);
            var stats = c[1] as object[];
            Require(stats != null && stats.Length == StatSlots, "indicator slots");
            for (int i = 0; i < StatSlots; i++)
            {
                if (i == 20 || i == 21) { Require(stats[i] == null, "unused indicator slot"); continue; }
                bool integer = i == 10 || (i >= 13 && i <= 17) || i >= 22;
                Require(integer ? stats[i] is int && (int)stats[i] >= 0 && (int)stats[i] <= 100000 : NonNegative(stats[i], 1000000f), "indicator " + i);
            }
            Require(c[2] == null && c[3] == null && c[4] == null && c[17] == null, "positions and buffs are not stored");
            var guns = c[5] as object[];
            Require(guns != null && guns.Length <= MaxGuns, "gun count");
            foreach (var gun in guns)
            {
                var g = gun as object[];
                Require(g != null && g.Length == 1 && g[0] is int && (int)g[0] >= 0 && (int)g[0] <= 10000, "magazine");
            }
            var level = c[6] as object[];
            if (level != null)
            {
                Require(level.Length == 9, "level slots");
                for (int i = 0; i < 9; i++)
                    Require(i >= 1 && i <= 3 ? NonNegative(level[i], 100000000f) : level[i] is int && (int)level[i] >= 0 && (int)level[i] <= 100000, "level " + i);
            }
            else Require(c[6] == null, "level type");
            Require(NonNegative(c[7], 1000000f), "health");
            Require(c[8] is int && (int)c[8] >= 0 && (int)c[8] <= 16, "selected belt");
            for (int i = 9; i <= 16; i++)
                Require(i == 10 || i == 12 || i == 14 ? NonNegative(c[i], 1000000f) : c[i] is bool, "status " + i);
        }

        // Inventory.Save(): object[3] = [itemID, amount, InventoryItem.Save() object[4]].
        internal static void Inventory(object[] items, int depth)
        {
            Require(items != null && items.Length <= MaxItems && depth <= 2, "inventory");
            foreach (var entry in items)
            {
                var item = entry as object[];
                Require(item != null && item.Length == 3, "item");
                Require(item[0] is int && (int)item[0] >= 0 && (int)item[0] <= 65535, "item id");
                Require(item[1] is int && (int)item[1] >= 1 && (int)item[1] <= 1000000, "item amount");
                var extra = item[2] as object[];
                Require(extra != null && extra.Length == 4, "item extra");
                Require(extra[0] == null || extra[0] is int || extra[0] is float, "item extra 0");
                Require(extra[1] == null || extra[1] is float, "item extra 1");
                Require(extra[2] == null || extra[2] is int && (int)extra[2] >= -1 && (int)extra[2] < 64, "item extra 2");
                if (extra[3] != null)
                {
                    var mods = extra[3] as object[];
                    Require(mods != null && mods.Length <= MaxMods, "item mods");
                    foreach (var mod in mods) if (mod != null) Inventory(mod as object[], depth + 1);
                }
            }
        }

        // The acknowledgements of a format 2 state (zeros for format 1).
        internal static uint[] Acks(object[] root)
        {
            var acks = root.Length == 14 ? (object[])root[13] : null;
            return acks == null ? new uint[2] : new[] { unchecked((uint)(int)acks[0]), unchecked((uint)(int)acks[1]) };
        }

        private static int Int(object value) { Require(value is int, "integer field"); return (int)value; }
        // Any finite value within +-max: native counters may legitimately dip below zero.
        private static bool NonNegative(object value, float max) { return value is float && Math.Abs((float)value) <= max; }
        private static void Require(bool condition, string what) { if (!condition) throw new InvalidDataException("Guest profile: " + what); }
    }
}
