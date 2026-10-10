using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ZompiercerLAN
{
    // Shared storages (0.6.0). The host owns every container: the guest sees a
    // snapshot ("view") of the one container it opened and asks the host to move
    // items; the host applies each request natively and answers with the result.
    // No Unity APIs in this file: LanStorageGame supplies the world, the offline
    // checks a fake one.
    //
    // Items travel in the game's own Inventory.Save() entry shape, object[3] =
    // [itemID, amount, InventoryItem.Save() object[4]], validated by LanGuestSchema.
    // Slot 2 of the extras (the train car an item lies on) is cleared on the wire.
    internal static class LanStorage
    {
        internal const int ViewFormat = 2, MaxName = 64, FingerprintBytes = 32, MaxAmount = 1000000;
        internal const int SceneTarget = 1, TrainTarget = 2, BagTarget = 3, RobotTarget = 4;
        // A slot of a robot dog (1.2.1): a device, an upgrade, or the storage of the device in a slot.
        internal const int RobotSlotDevice = 1, RobotSlotUpgrade = 2, RobotSlotStorage = 3, MaxRobotSlots = 16;
        internal const int TrainFurniture = 1, TrainPart = 2;
        // Result and view statuses (Packet.Action of StorageResult).
        internal const byte Ok = 0, NotFound = 1, TooFar = 2, NoSpace = 3, NotAllowed = 4, Busy = 5, Gone = 6,
            Invalid = 7, Unavailable = 8, TooLarge = 9, Closed = 10, NotOwned = 11, Empty = 12, Manual = 13,
            Full = 14, Missing = 15, Occupied = 16, Lost = 17, Broken = 18, Locked = 19, NotReady = 20, NotYours = 21, DeviceInUse = 22, NotEmpty = 23;

        internal static string Describe(byte status)
        {
            switch (status)
            {
                case Ok: return "Готово";
                case NotFound: return "У хоста нет такого хранилища или предмета";
                case TooFar: return "Слишком далеко от хранилища";
                case NoSpace: return "Не хватает места";
                case NotAllowed: return "Этот предмет сюда нельзя положить";
                case Busy: return "Хост занят этим хранилищем; попробуйте ещё раз";
                case Gone: return "Хранилище закрыто";
                case Invalid: return "Хост отклонил запрос";
                case Unavailable: return "Хост ещё не поставил ваши вещи на учёт (или ваш профиль не сохраняется)";
                case TooLarge: return "Слишком сложный предмет для передачи";
                case Closed: return "Хранилище закрыто";
                case NotOwned: return "По учёту хоста у вас нет такой вещи";
                case Empty: return "Здесь ничего не осталось";
                case Manual: return "Для ручного крафта откройте окно этого верстака и оставайтесь рядом";
                case Full: return "Бак поезда уже полон";
                case Missing: return "Не хватает ресурсов для постройки";
                case Occupied: return "Это место у хоста уже занято";
                case Lost: return "Хост сейчас не видит, где вы; подождите пару секунд";
                case Broken: return "Сломано: вставьте запасной рычаг";
                case Locked: return "Хост не разрешил вам управлять поездом";
                case NotReady: return "У хоста этот квест ещё не готов к сдаче или уже сдан";
                case NotYours: return "Это не ваш робопёс";
                case DeviceInUse: return "Сначала освободите хранилище этого устройства";
                case NotEmpty: return "Сначала выньте всё, что внутри";
                default: return "Ошибка хранилища " + status;
            }
        }
        // The host's answer about a train door (1.1.6).
        internal static string DescribeDoor(byte status)
        {
            switch (status)
            {
                case NotFound: return "У хоста нет такой двери";
                case TooFar: return "Подойдите к двери ближе";
                case NotAllowed: return "Эту дверь нельзя открыть";
                case Broken: return "Дверь сломана";
                case Closed: return "Дверь заперта";
                default: return Describe(status);
            }
        }

        // ---- Targets: what the guest is looking at, in terms both worlds share ----
        // Scene: [1, scene, x, y, z, inventory name]: positions are the game's own
        // identity of scene inventories in its saves. Train: [2, car, kind, host id].
        internal static byte[] ScenePayload(string scene, float x, float y, float z, string name)
        { return Payload(new object[] { SceneTarget, scene, x, y, z, name }); }
        internal static byte[] TrainPayload(int car, int kind, int identity)
        { return Payload(new object[] { TrainTarget, car, kind, identity }); }

        // Bag: [3, host id] — a loot bag that exists only in the host's world.
        internal static byte[] BagPayload(int hostId) { return Payload(new object[] { BagTarget, hostId }); }
        // Robot (1.2.1): [4, host id of the dog, RobotSlotDevice/RobotSlotUpgrade/RobotSlotStorage, slot].
        internal static byte[] RobotPayload(int hostId, int kind, int slot) { return Payload(new object[] { RobotTarget, hostId, kind, slot }); }

        internal static object[] DecodeTarget(byte[] payload) { return ValidTarget(Decode(payload, "target")); }
        internal static object[] ValidTarget(object[] t)
        {
            Require(t != null && t.Length >= 1 && t[0] is int, "target type");
            if ((int)t[0] == BagTarget) Require(t.Length == 2 && t[1] is int && (int)t[1] != 0, "target bag");
            else if ((int)t[0] == RobotTarget)
            {
                Require(t.Length == 4 && t[1] is int && (int)t[1] != 0 && t[2] is int && t[3] is int, "target robot");
                Require((int)t[2] >= RobotSlotDevice && (int)t[2] <= RobotSlotStorage && (int)t[3] >= 0 && (int)t[3] < MaxRobotSlots, "target robot slot");
            }
            else if ((int)t[0] == SceneTarget)
            {
                Require(t.Length == 6 && t[1] is string && ((string)t[1]).Length > 0 && ((string)t[1]).Length <= 96, "target scene");
                for (int i = 2; i <= 4; i++) Require(t[i] is float && Math.Abs((float)t[i]) < 100000f, "target position");
                Require(t[5] is string && ((string)t[5]).Length > 0 && ((string)t[5]).Length <= MaxName, "target name");
            }
            else
            {
                Require((int)t[0] == TrainTarget && t.Length == 4 && t[1] is int && t[2] is int && t[3] is int, "target train");
                Require((int)t[1] >= 0 && (int)t[1] < LanProtocol.MaxCars, "target car");
                Require((int)t[2] == TrainFurniture || (int)t[2] == TrainPart, "target kind");
                Require((int)t[3] != 0, "target identity");
            }
            return t;
        }

        // ---- Items ----
        internal static object[] Normalize(object[] item)
        {
            ValidateItem(item);
            var extra = (object[])((object[])item[2]).Clone();
            extra[2] = null;
            return new object[] { item[0], item[1], extra };
        }
        internal static void ValidateItem(object[] item)
        {
            try { LanGuestSchema.Inventory(new object[] { item }, 0); }
            catch (InvalidDataException ex) { throw new InvalidDataException("Storage item: " + ex.Message); }
            Require((int)item[1] <= MaxAmount, "item amount");
        }
        internal static byte[] EncodeItem(object[] item) { return Payload(Normalize(item)); }
        internal static object[] DecodeItem(byte[] payload) { return Normalize(Decode(payload, "item")); }
        internal static bool ItemFits(object[] item)
        {
            try { EncodeItem(item); return true; } catch (InvalidDataException) { return false; }
        }
        internal static object[] WithAmount(object[] item, int amount)
        { var copy = Normalize(item); copy[1] = amount; return copy; }

        // Identity of an item regardless of stack size and train-car parent.
        internal static byte[] Fingerprint(object[] item)
        {
            var n = Normalize(item);
            using (var sha = SHA256.Create()) return sha.ComputeHash(LanValueCodec.Encode(new object[] { n[0], n[2] }));
        }
        internal static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        internal static bool SameFingerprint(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != FingerprintBytes || b.Length != FingerprintBytes) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // Take: [index in the view, amount, fingerprint of that item].
        internal static byte[] TakePayload(int index, int amount, byte[] fingerprint)
        { return Payload(new object[] { index, amount, fingerprint }); }
        internal static void DecodeTake(byte[] payload, out int index, out int amount, out byte[] fingerprint)
        {
            var t = Decode(payload, "take");
            Require(t.Length == 3 && t[0] is int && t[1] is int && t[2] is byte[], "take fields");
            index = (int)t[0]; amount = (int)t[1]; fingerprint = (byte[])t[2];
            Require(index >= 0 && index < LanGuestSchema.MaxItems && amount >= 1 && amount <= MaxAmount && fingerprint.Length == FingerprintBytes, "take values");
        }

        // Pick up: [host id of the loose item, its item id, amount wanted].
        internal static byte[] PickupPayload(int hostId, int itemId, int amount) { return Payload(new object[] { hostId, itemId, amount }); }
        internal static void DecodePickup(byte[] payload, out int hostId, out int itemId, out int amount)
        {
            var t = Decode(payload, "pickup");
            Require(t.Length == 3 && t[0] is int && t[1] is int && t[2] is int, "pickup fields");
            hostId = (int)t[0]; itemId = (int)t[1]; amount = (int)t[2];
            Require(hostId != 0 && itemId >= 0 && itemId <= 65535 && amount >= 1 && amount <= MaxAmount, "pickup values");
        }
        // Drop: [item, x, y, z] — where the guest lets it fall.
        internal static byte[] DropPayload(object[] item, float x, float y, float z) { return Payload(new object[] { Normalize(item), x, y, z }); }
        internal static byte[] DropPayload(object[] item, int car, float[] pose)
        { return Payload(new object[] { Normalize(item), pose[0], pose[1], pose[2], car, pose[3], pose[4], pose[5], pose[6] }); }
        internal static object[] DecodeDrop(byte[] payload, out float x, out float y, out float z)
        { int car; float[] rotation; return DecodeDrop(payload, out x, out y, out z, out car, out rotation); }
        internal static object[] DecodeDrop(byte[] payload, out float x, out float y, out float z, out int car, out float[] rotation)
        {
            var t = Decode(payload, "drop");
            Require((t.Length == 4 || t.Length == 9) && t[0] is object[] && t[1] is float && t[2] is float && t[3] is float, "drop fields");
            x = (float)t[1]; y = (float)t[2]; z = (float)t[3];
            Require(Math.Abs(x) < 100000f && Math.Abs(y) < 100000f && Math.Abs(z) < 100000f, "drop position");
            car = -1; rotation = null;
            if (t.Length == 9)
            {
                Require(t[4] is int && (int)t[4] >= -1 && (int)t[4] < LanProtocol.MaxCars, "drop car");
                car = (int)t[4]; rotation = new float[4];
                for (int i = 0; i < 4; i++) { Require(t[5 + i] is float, "drop rotation"); rotation[i] = (float)t[5 + i]; }
                float norm = 0f; foreach (float q in rotation) norm += q * q;
                Require(norm > .96f && norm < 1.04f, "drop rotation norm");
                Require(car < 0 || Math.Abs(x) <= MaxCarLocal && Math.Abs(y) <= MaxCarLocal && Math.Abs(z) <= MaxCarLocal, "drop car position");
            }
            return Normalize((object[])t[0]);
        }

        // Work on the world through the host: [kind, x, y, z, tool item id, extra, escrowed item or null].
        // Hit: melee on a ResourceContent (what falls out lies in the host's world).
        // Gather: knife on a CollectedResource. Dig: shovel, extra = 1 sand / 2 soil.
        // Fuel: an empty canister (escrowed) filled at a gas station.
        // Refuel (0.8.0): a full canister (escrowed) poured into a fuel tank of the host's train;
        // extra = car index, x/y/z = the point on the tank in that car's local space.
        internal const int WorkHit = 1, WorkGather = 2, WorkDig = 3, WorkFuel = 4, WorkRefuel = 5;
        // 1.2.2: the rainwater tank of the host's train, in car `extra`'s space: fill empty bottles
        // (escrow), pour dirty water back (escrow), or read its level.
        internal const int WorkWater = 6, WorkPourWater = 7, WorkWaterLevel = 8, MaxWaterBottles = 50;
        internal static byte[] WorkPayload(int kind, float x, float y, float z, int tool, int extra, object[] escrow)
        { return Payload(new object[] { kind, x, y, z, tool, extra, escrow == null ? null : Normalize(escrow) }); }
        internal static int DecodeWork(byte[] payload, out float x, out float y, out float z, out int tool, out int extra, out object[] escrow)
        {
            var t = Decode(payload, "work");
            Require(t.Length == 7 && t[0] is int && t[1] is float && t[2] is float && t[3] is float && t[4] is int && t[5] is int, "work fields");
            int kind = (int)t[0]; x = (float)t[1]; y = (float)t[2]; z = (float)t[3]; tool = (int)t[4]; extra = (int)t[5];
            Require(kind >= WorkHit && kind <= WorkWaterLevel && Math.Abs(x) < 100000f && Math.Abs(y) < 100000f && Math.Abs(z) < 100000f, "work kind or position");
            bool canister = kind == WorkFuel || kind == WorkRefuel, water = kind >= WorkWater, onCar = kind == WorkRefuel || water;
            Require(canister || water ? tool == -1 : tool >= 0 && tool <= 65535, "work tool");
            Require(kind == WorkDig ? extra == 1 || extra == 2 : onCar ? extra >= 0 && extra < LanProtocol.MaxCars : extra == 0, "work extra");
            Require(!onCar || Math.Abs(x) <= 256f && Math.Abs(y) <= 256f && Math.Abs(z) <= 256f, "work car-local position");
            Require(t[6] == null || t[6] is object[], "work escrow item");
            escrow = t[6] == null ? null : Normalize((object[])t[6]);
            Require((canister || kind == WorkWater || kind == WorkPourWater) == (escrow != null), "work escrow");
            Require(escrow == null || !water || (int)escrow[1] <= MaxWaterBottles, "work bottles");
            return kind;
        }

        // Build on the host's train (0.8.0): [car, kind (1 furniture, 2 part), host id, tool item id].
        // One hit of the guest's tool on a blueprint part or an unassembled furniture;
        // the hit that completes a part pays its recipe from the guest's ledger.
        internal static byte[] BuildPayload(int car, int kind, int identity, int tool)
        { return Payload(new object[] { car, kind, identity, tool }); }
        internal static void DecodeBuild(byte[] payload, out int car, out int kind, out int identity, out int tool)
        {
            var t = Decode(payload, "build");
            Require(t.Length == 4 && t[0] is int && t[1] is int && t[2] is int && t[3] is int, "build fields");
            car = (int)t[0]; kind = (int)t[1]; identity = (int)t[2]; tool = (int)t[3];
            Require(car >= 0 && car < LanProtocol.MaxCars && (kind == TrainFurniture || kind == TrainPart) && tool >= 0 && tool <= 65535, "build target");
        }

        // Place a blueprint on the host's train (0.8.1): [part prefab id, car, mount point type
        // (-1: on a surface), point x, y, z, x, y, z, qx, qy, qz, qw], all in that car's space.
        internal const float MaxCarLocal = 256f;
        internal static byte[] PlacePayload(int part, int car, int pointType, float[] point, float[] pose)
        { return Payload(new object[] { part, car, pointType, point[0], point[1], point[2], pose[0], pose[1], pose[2], pose[3], pose[4], pose[5], pose[6] }); }
        internal static void DecodePlace(byte[] payload, out int part, out int car, out int pointType, out float[] point, out float[] pose)
        {
            var t = Decode(payload, "place");
            Require(t.Length == 13 && t[0] is int && t[1] is int && t[2] is int, "place fields");
            for (int i = 3; i < 13; i++) Require(t[i] is float, "place numbers");
            part = (int)t[0]; car = (int)t[1]; pointType = (int)t[2];
            Require(part >= 0 && part <= 65535 && car >= 0 && car < LanProtocol.MaxCars && pointType >= -1 && pointType <= 1023, "place part");
            point = new[] { (float)t[3], (float)t[4], (float)t[5] };
            pose = new[] { (float)t[6], (float)t[7], (float)t[8], (float)t[9], (float)t[10], (float)t[11], (float)t[12] };
            for (int i = 0; i < 3; i++) Require(Math.Abs(point[i]) <= MaxCarLocal && Math.Abs(pose[i]) <= MaxCarLocal, "place position");
            float norm = pose[3] * pose[3] + pose[4] * pose[4] + pose[5] * pose[5] + pose[6] * pose[6];
            Require(norm > .96f && norm < 1.04f, "place rotation");
        }

        // A control of the host's train (0.10.0): [kind, index, check, value, escrowed item or null].
        // Button/cab light/repair: index in LanTrainControl order (< 32), value 0; repair escrows one
        // spare lever (item 66). Lever: index < MaxLevers, value = the lever's new value x1000.
        // Light of a built part: index = car, check = host id, value = 1 furniture / 2 part.
        // `check` is a hash of the control's place in the train, so different trains never match.
        // A door (1.1.6, allowed without the driving permission): index = car, check = host id of its
        // furniture or part (0 for a door of the car or of the train itself), value = DoorValue.
        internal const int ControlButton = 1, ControlLever = 2, ControlCabLight = 3, ControlRepair = 4, ControlPartLight = 5, ControlDoor = 6;
        // A door's owner (furniture, part, the car itself, the train itself), DoorOpen when it should
        // open, and 23 bits of the hash of its path under the owner (TrainLayout.DoorHash).
        internal const int DoorFurniture = 1, DoorPart = 2, DoorCar = 3, DoorTrain = 4, DoorOpen = 8, DoorHashMask = 0x7FFFFF;
        internal static int DoorValue(int owner, bool open, int hash) { return owner | (open ? DoorOpen : 0) | (hash & DoorHashMask) << 8; }
        internal static void DoorFields(int value, out int owner, out bool open, out int hash)
        { owner = value & 7; open = (value & DoorOpen) != 0; hash = value >> 8 & DoorHashMask; }
        internal const int LeverItem = 66, MaxControlIndex = 32;
        internal static byte[] ControlPayload(int kind, int index, int check, int value, object[] escrow)
        { return Payload(new object[] { kind, index, check, value, escrow == null ? null : Normalize(escrow) }); }
        internal static void DecodeControl(byte[] payload, out int kind, out int index, out int check, out int value, out object[] escrow)
        {
            var t = Decode(payload, "control");
            Require(t.Length == 5 && t[0] is int && t[1] is int && t[2] is int && t[3] is int && (t[4] == null || t[4] is object[]), "control fields");
            kind = (int)t[0]; index = (int)t[1]; check = (int)t[2]; value = (int)t[3];
            escrow = t[4] == null ? null : Normalize((object[])t[4]);
            switch (kind)
            {
                case ControlButton: case ControlCabLight:
                    Require(index >= 0 && index < MaxControlIndex && value == 0 && escrow == null, "control button"); break;
                case ControlRepair:
                    Require(index >= 0 && index < MaxControlIndex && value == 0 && escrow != null && (int)escrow[0] == LeverItem && (int)escrow[1] == 1, "control repair"); break;
                case ControlLever:
                    Require(index >= 0 && index < LanProtocol.MaxLevers && value >= -1000000 && value <= 1000000 && escrow == null, "control lever"); break;
                case ControlPartLight:
                    Require(index >= 0 && index < LanProtocol.MaxCars && check != 0 && (value == TrainFurniture || value == TrainPart) && escrow == null, "control part light"); break;
                case ControlDoor:
                    int owner = value & 7;
                    Require(index >= 0 && index < LanProtocol.MaxCars && value >= 0 && (value & 0xF0) == 0 && owner >= DoorFurniture && owner <= DoorTrain &&
                        (check != 0) == (owner <= DoorPart) && (owner != DoorTrain || index == 0) && escrow == null, "control door"); break;
                default: throw new InvalidDataException("Storage: control kind");
            }
        }

        // The guest died (0.12.0): [x, y, z, car] where it fell, in that car's space when car >= 0.
        internal static byte[] DeathPayload(float x, float y, float z, int car) { return Payload(new object[] { x, y, z, car }); }
        internal static void DecodeDeath(byte[] payload, out float x, out float y, out float z, out int car)
        {
            var t = Decode(payload, "death");
            Require(t.Length == 4 && t[0] is float && t[1] is float && t[2] is float && t[3] is int, "death fields");
            x = (float)t[0]; y = (float)t[1]; z = (float)t[2]; car = (int)t[3];
            Require(car >= -1 && car < LanProtocol.MaxCars, "death car");
            float limit = car >= 0 ? MaxCarLocal : 100000f;
            Require(Math.Abs(x) < limit && Math.Abs(y) < limit && Math.Abs(z) < limit, "death position");
        }

        // A throw of the guest's grenade, dynamite or C4 (1.4.0): [the escrowed item (one), car, x, y, z,
        // qx, qy, qz, qw, type]: where its hand let go and how its camera was turned, in car `car`'s
        // space (-1: the world), and the game's standard (0) or alternative (1) throw. The host takes
        // the speed of the throw from its own copy of the weapon.
        internal const int ThrowStandard = 0, ThrowAlternative = 1;
        internal static byte[] ThrowPayload(object[] escrow, int car, float[] at, float[] turn, int type)
        { return Payload(new object[] { Normalize(escrow), car, at[0], at[1], at[2], turn[0], turn[1], turn[2], turn[3], type }); }
        internal static object[] DecodeThrow(byte[] payload, out int car, out float[] at, out float[] turn, out int type)
        {
            var t = Decode(payload, "throw");
            Require(t.Length == 10 && t[0] is object[] && t[1] is int && t[9] is int, "throw fields");
            for (int i = 2; i < 9; i++) Require(t[i] is float, "throw numbers");
            var escrow = Normalize((object[])t[0]);
            Require((int)escrow[1] == 1, "throw amount");
            car = (int)t[1]; type = (int)t[9];
            Require(car >= -1 && car < LanProtocol.MaxCars && (type == ThrowStandard || type == ThrowAlternative), "throw car or type");
            at = new[] { (float)t[2], (float)t[3], (float)t[4] };
            turn = new[] { (float)t[5], (float)t[6], (float)t[7], (float)t[8] };
            float limit = car >= 0 ? MaxCarLocal : 100000f;
            Require(Math.Abs(at[0]) < limit && Math.Abs(at[1]) < limit && Math.Abs(at[2]) < limit, "throw position");
            float norm = turn[0] * turn[0] + turn[1] * turn[1] + turn[2] * turn[2] + turn[3] * turn[3];
            Require(norm > .96f && norm < 1.04f, "throw rotation");
            return escrow;
        }

        // Paint, wires and sign text of the host's train (1.4.0): [kind, car, owner (TrainFurniture or
        // TrainPart), host id, index, text, r, g, b, a, apply colour, output car, output host id].
        // Paint: surface `index` of the object (its Paintables) gets material `text` and, when apply
        // colour, that colour; Restore: its own material again. Sign: the object's sign reads `text`.
        // Wire: input `index` of the object takes its signal from the switch of part (output car,
        // output host id); Unwire: input `index` is cut; UnwireAll: every input fed by the switch of
        // this part is cut. Unused fields are "", 0 and false.
        internal const int DecorPaint = 1, DecorRestore = 2, DecorSign = 3, DecorWire = 4, DecorUnwire = 5, DecorUnwireAll = 6;
        internal const int MaxDecorIndex = 63, MaxMaterialChars = 64, MaxSignChars = 100;
        internal sealed class DecorRequest
        {
            internal int Kind, Car, Owner, Identity, Index, OutCar, OutIdentity;
            internal string Text = "";
            internal float[] Color = new float[4];
            internal bool ApplyColor;
        }
        internal static byte[] DecorPayload(DecorRequest d)
        {
            Require(d != null && d.Color != null && d.Color.Length == 4, "decor request");
            return Payload(new object[] { d.Kind, d.Car, d.Owner, d.Identity, d.Index, d.Text ?? "", d.Color[0], d.Color[1], d.Color[2], d.Color[3], d.ApplyColor, d.OutCar, d.OutIdentity });
        }
        internal static DecorRequest DecodeDecor(byte[] payload)
        {
            var t = Decode(payload, "decor");
            Require(t.Length == 13 && t[0] is int && t[1] is int && t[2] is int && t[3] is int && t[4] is int && t[5] is string &&
                t[10] is bool && t[11] is int && t[12] is int, "decor fields");
            for (int i = 6; i < 10; i++) Require(t[i] is float, "decor colour");
            var d = new DecorRequest { Kind = (int)t[0], Car = (int)t[1], Owner = (int)t[2], Identity = (int)t[3], Index = (int)t[4], Text = (string)t[5],
                Color = new[] { (float)t[6], (float)t[7], (float)t[8], (float)t[9] }, ApplyColor = (bool)t[10], OutCar = (int)t[11], OutIdentity = (int)t[12] };
            Require(d.Kind >= DecorPaint && d.Kind <= DecorUnwireAll, "decor kind");
            Require(d.Car >= 0 && d.Car < LanProtocol.MaxCars && (d.Owner == TrainFurniture || d.Owner == TrainPart) && d.Identity != 0 &&
                d.Index >= 0 && d.Index <= MaxDecorIndex, "decor target");
            bool paint = d.Kind == DecorPaint, wire = d.Kind == DecorWire;
            if (paint)
            {
                Require(d.Text.Length > 0 && d.Text.Length <= MaxMaterialChars && d.Text == CleanMaterial(d.Text), "decor material");
                foreach (float c in d.Color) Require(c >= 0f && c <= 1f, "decor colour range");
            }
            else
            {
                Require(d.Kind == DecorSign ? d.Text == CleanSign(d.Text) : d.Text.Length == 0, "decor text");
                foreach (float c in d.Color) Require(c == 0f, "decor unused colour");
                Require(!d.ApplyColor, "decor unused flag");
            }
            if (wire) Require(d.OutCar >= 0 && d.OutCar < LanProtocol.MaxCars && d.OutIdentity != 0, "decor output");
            else Require(d.OutCar == 0 && d.OutIdentity == 0, "decor unused output");
            Require(d.Kind != DecorSign && d.Kind != DecorUnwireAll || d.Index == 0, "decor index");
            Require(d.Kind != DecorUnwireAll || d.Owner == TrainPart, "decor switch owner");
            return d;
        }
        // A material's name as the game's material database knows it (no " (Instance)").
        internal static string CleanMaterial(string name)
        {
            if (name == null) return "";
            var text = new StringBuilder(name.Length);
            foreach (char c in name) if (c >= ' ' && c != '\x7f') text.Append(c);
            string clean = StripInstance(text.ToString());
            if (clean.Length > MaxMaterialChars)
            {
                int cut = char.IsHighSurrogate(clean[MaxMaterialChars - 1]) ? MaxMaterialChars - 1 : MaxMaterialChars;
                clean = StripInstance(clean.Substring(0, cut));
            }
            return clean;
        }
        private static string StripInstance(string name)
        {
            const string instance = " (Instance)";
            while (name.EndsWith(instance, StringComparison.Ordinal)) name = name.Substring(0, name.Length - instance.Length);
            return name;
        }
        // A sign's text as either side shows it: no control characters, no rich-text tags, at most
        // MaxSignChars characters (a line break stays).
        internal static string CleanSign(string value)
        {
            if (value == null) return "";
            var text = new StringBuilder(Math.Min(value.Length, MaxSignChars));
            foreach (char c in value)
            {
                if (text.Length >= MaxSignChars) break;
                if (c == '\n' || c >= ' ' && c != '\x7f' && c != '<' && c != '>' && !char.IsSurrogate(c) && (c < '\u2028' || c > '\u202e')) text.Append(c);
            }
            return text.ToString();
        }

        // A furniture kit placed on the host's train (1.4.2): [the escrowed kit (one), car, x, y, z, qx, qy,
        // qz, qw] in that car's space; the host puts the unassembled furniture of the kit there.
        internal static byte[] FurniturePayload(object[] escrow, int car, float[] pose)
        { return Payload(new object[] { Normalize(escrow), car, pose[0], pose[1], pose[2], pose[3], pose[4], pose[5], pose[6] }); }
        internal static object[] DecodeFurniture(byte[] payload, out int car, out float[] pose)
        {
            var t = Decode(payload, "furniture");
            Require(t.Length == 9 && t[0] is object[] && t[1] is int, "furniture fields");
            for (int i = 2; i < 9; i++) Require(t[i] is float, "furniture numbers");
            var escrow = Normalize((object[])t[0]);
            Require((int)escrow[1] == 1, "furniture kit amount");
            car = (int)t[1];
            Require(car >= 0 && car < LanProtocol.MaxCars, "furniture car");
            pose = new[] { (float)t[2], (float)t[3], (float)t[4], (float)t[5], (float)t[6], (float)t[7], (float)t[8] };
            for (int i = 0; i < 3; i++) Require(Math.Abs(pose[i]) <= MaxCarLocal, "furniture position");
            float norm = pose[3] * pose[3] + pose[4] * pose[4] + pose[5] * pose[5] + pose[6] * pose[6];
            Require(norm > .96f && norm < 1.04f, "furniture rotation");
            return escrow;
        }

        // Taking the host's train apart (1.4.2): [mode, car, owner (TrainFurniture or TrainPart), host id,
        // tool item id]. DismantleHit: one hit of the guest's tool on a built part or furniture; the hit
        // that takes it apart gives the guest the part's recipe or the furniture's kit, as the game gives
        // them to whoever takes it apart. DismantleBlueprint: a blueprint part goes (tool -1, nothing given).
        internal const int DismantleHit = 1, DismantleBlueprint = 2, MaxRefundItems = 16;
        internal static byte[] DismantlePayload(int mode, int car, int owner, int identity, int tool)
        { return Payload(new object[] { mode, car, owner, identity, tool }); }
        internal static int DecodeDismantle(byte[] payload, out int car, out int owner, out int identity, out int tool)
        {
            var t = Decode(payload, "dismantle");
            Require(t.Length == 5 && t[0] is int && t[1] is int && t[2] is int && t[3] is int && t[4] is int, "dismantle fields");
            int mode = (int)t[0]; car = (int)t[1]; owner = (int)t[2]; identity = (int)t[3]; tool = (int)t[4];
            Require(mode == DismantleHit || mode == DismantleBlueprint, "dismantle mode");
            Require(car >= 0 && car < LanProtocol.MaxCars && (owner == TrainFurniture || owner == TrainPart) && identity != 0, "dismantle target");
            Require(mode == DismantleHit ? tool >= 0 && tool <= 65535 : tool == -1 && owner == TrainPart, "dismantle tool");
            return mode;
        }
        // What taking it apart gave the guest (a part's recipe, a furniture's kit).
        internal static byte[] RefundPayload(object[] items)
        {
            Require(items != null && items.Length >= 1 && items.Length <= MaxRefundItems, "refund items");
            var all = new object[items.Length];
            for (int i = 0; i < items.Length; i++) all[i] = Normalize((object[])items[i]);
            return Payload(all);
        }
        internal static object[] DecodeRefund(byte[] payload)
        {
            var t = Decode(payload, "refund");
            Require(t.Length >= 1 && t.Length <= MaxRefundItems, "refund items");
            var all = new object[t.Length];
            for (int i = 0; i < t.Length; i++) { Require(t[i] is object[], "refund item"); all[i] = Normalize((object[])t[i]); }
            return all;
        }

        // A quest of the host's scene (1.2.0): [QuestAccept or QuestComplete, its key, the scene].
        // 1.4.5: QuestShare claims the reward of a quest the host handed in while the guest played.
        internal const int QuestAccept = 1, QuestComplete = 2, QuestShare = 3, MaxQuestItems = 8;
        internal static byte[] QuestPayload(int kind, uint key, string scene) { return Payload(new object[] { kind, unchecked((int)key), scene }); }
        internal static int DecodeQuest(byte[] payload, out uint key, out string scene)
        {
            var t = Decode(payload, "quest");
            Require(t.Length == 3 && t[0] is int && t[1] is int && t[2] is string, "quest fields");
            int kind = (int)t[0];
            Require(kind == QuestAccept || kind == QuestComplete || kind == QuestShare, "quest kind");
            key = unchecked((uint)(int)t[1]); scene = (string)t[2];
            Require(key != 0, "quest key");
            Require(scene.Length > 0 && scene.Length <= 96, "quest scene");
            return kind;
        }
        // What the host hands out for a quest (gifts on accepting, rewards on handing in).
        internal static byte[] QuestItemsPayload(object[] items)
        {
            Require(items != null && items.Length <= MaxQuestItems, "quest items");
            var all = new object[items.Length];
            for (int i = 0; i < items.Length; i++) all[i] = Normalize((object[])items[i]);
            return Payload(all);
        }
        internal static object[] DecodeQuestItems(byte[] payload)
        {
            var t = Decode(payload, "quest items");
            Require(t.Length >= 1 && t.Length <= MaxQuestItems, "quest items");
            var all = new object[t.Length];
            for (int i = 0; i < t.Length; i++) { Require(t[i] is object[], "quest item"); all[i] = Normalize((object[])t[i]); }
            return all;
        }

        // ---- Carryable objects and the guest's robot dog (1.2.1) ----
        // [TakableTake, host id]: the guest picks up a host object (a barrel, its own robot dog).
        // [TakableRelease, host id, mode, car, x, y, z, qx, qy, qz, qw, vx, vy, vz]: it puts it down
        // (drop, throw, into a holder on the train, or a dog placed), in car `car`'s space (-1: world).
        // [RobotInfo, host id], [RobotSet, host id, field, value], [RobotMedkit, host id, slot].
        internal const int TakableTake = 1, TakableRelease = 2, RobotInfo = 3, RobotSet = 4, RobotMedkit = 5;
        internal const int ReleaseDrop = 0, ReleaseThrow = 1, ReleaseHolder = 2, ReleasePlace = 3;
        internal const int RobotActive = 1, RobotBehavior = 2, RobotAttack = 3, RobotDoors = 4;
        internal const float MaxThrowSpeed = 50f;
        internal static byte[] CarryPayload(int hostId) { return Payload(new object[] { TakableTake, hostId }); }
        internal static byte[] ReleasePayload(int hostId, int mode, int car, float[] pose, float[] velocity)
        {
            Require(pose != null && pose.Length == 7 && velocity != null && velocity.Length == 3, "release fields");
            return Payload(new object[] { TakableRelease, hostId, mode, car, pose[0], pose[1], pose[2], pose[3], pose[4], pose[5], pose[6], velocity[0], velocity[1], velocity[2] });
        }
        internal static byte[] RobotInfoPayload(int hostId) { return Payload(new object[] { RobotInfo, hostId }); }
        internal static byte[] RobotSetPayload(int hostId, int field, int value) { return Payload(new object[] { RobotSet, hostId, field, value }); }
        internal static byte[] RobotMedkitPayload(int hostId, int slot) { return Payload(new object[] { RobotMedkit, hostId, slot }); }
        // 1.4.3: [StationUse, station key, action]: a fuel station of the host's scene (LanFuelStation.Key):
        // its switchboard lever, its pump button, or its fuel gun (taken, into the main tank of the
        // host's train, hung back on the station, let go).
        internal const int StationUse = 6;
        internal const int StationLever = 1, StationPump = 2, StationTake = 3, StationTank = 4, StationBack = 5, StationDrop = 6;
        internal static byte[] StationPayload(int key, int action)
        {
            Require(key != 0 && action >= StationLever && action <= StationDrop, "station fields");
            return Payload(new object[] { StationUse, key, action });
        }
        // A validated request: [op, host id, ...] with typed fields (release: mode, car, float[7] pose, float[3] velocity).
        internal static object[] DecodeTakable(byte[] payload)
        {
            var t = Decode(payload, "takable");
            Require(t.Length >= 2 && t[0] is int && t[1] is int && (int)t[1] != 0, "takable request");
            int op = (int)t[0];
            switch (op)
            {
                case TakableTake: case RobotInfo:
                    Require(t.Length == 2, "takable fields"); return t;
                case RobotSet:
                    Require(t.Length == 4 && t[2] is int && t[3] is int, "robot setting");
                    int field = (int)t[2], value = (int)t[3];
                    Require(field >= RobotActive && field <= RobotDoors && value >= 0 && value <= (field == RobotAttack ? 3 : 1), "robot setting value");
                    return t;
                case RobotMedkit:
                    Require(t.Length == 3 && t[2] is int && (int)t[2] >= 0 && (int)t[2] < MaxRobotSlots, "robot medkit");
                    return t;
                case StationUse:
                    Require(t.Length == 3 && t[2] is int && (int)t[2] >= StationLever && (int)t[2] <= StationDrop, "station use");
                    return t;
                case TakableRelease:
                    Require(t.Length == 14 && t[2] is int && t[3] is int, "release fields");
                    int mode = (int)t[2], car = (int)t[3];
                    Require(mode >= ReleaseDrop && mode <= ReleasePlace && car >= -1 && car < LanProtocol.MaxCars, "release mode");
                    var pose = new float[7]; var velocity = new float[3];
                    for (int i = 0; i < 7; i++) { Require(t[4 + i] is float && !float.IsNaN((float)t[4 + i]) && !float.IsInfinity((float)t[4 + i]), "release pose"); pose[i] = (float)t[4 + i]; }
                    for (int i = 0; i < 3; i++) { Require(t[11 + i] is float && !float.IsNaN((float)t[11 + i]) && Math.Abs((float)t[11 + i]) <= MaxThrowSpeed, "release velocity"); velocity[i] = (float)t[11 + i]; }
                    float limit = car >= 0 ? MaxCarLocal : 100000f;
                    Require(Math.Abs(pose[0]) < limit && Math.Abs(pose[1]) < limit && Math.Abs(pose[2]) < limit, "release position");
                    float norm = pose[3] * pose[3] + pose[4] * pose[4] + pose[5] * pose[5] + pose[6] * pose[6];
                    Require(norm > .9f && norm < 1.1f, "release rotation");
                    return new object[] { op, t[1], mode, car, pose, velocity };
            }
            throw new InvalidDataException("Storage: takable op");
        }

        // The guest's robot dog as the host's window shows it.
        internal sealed class RobotState
        {
            internal bool Active, Broken, OnTrain;
            internal int Behavior, Attack, Doors, Battery, Durability;
            internal int[] Devices = new int[0], Upgrades = new int[0];
            internal bool[] Storage = new bool[0];
            internal int[] Medkit = new int[0]; // -1 none, 0 not ready, 1 ready
        }
        internal static byte[] EncodeRobot(RobotState r)
        {
            Require(r != null && r.Devices.Length <= MaxRobotSlots && r.Upgrades.Length <= MaxRobotSlots &&
                r.Storage.Length == r.Devices.Length && r.Medkit.Length == r.Devices.Length, "robot state");
            var devices = new object[r.Devices.Length];
            for (int i = 0; i < devices.Length; i++) devices[i] = new object[] { r.Devices[i], r.Storage[i] ? 1 : 0, r.Medkit[i] };
            var upgrades = new object[r.Upgrades.Length];
            for (int i = 0; i < upgrades.Length; i++) upgrades[i] = r.Upgrades[i];
            return Payload(new object[] { r.Active ? 1 : 0, r.Broken ? 1 : 0, r.OnTrain ? 1 : 0, r.Behavior, r.Attack, r.Doors,
                Math.Max(0, Math.Min(100, r.Battery)), Math.Max(0, Math.Min(100, r.Durability)), devices, upgrades });
        }
        internal static RobotState DecodeRobot(byte[] payload)
        {
            var t = Decode(payload, "robot state");
            Require(t.Length == 10 && t[8] is object[] && t[9] is object[], "robot state");
            for (int i = 0; i < 8; i++) Require(t[i] is int, "robot state");
            var r = new RobotState { Active = (int)t[0] == 1, Broken = (int)t[1] == 1, OnTrain = (int)t[2] == 1, Behavior = (int)t[3], Attack = (int)t[4], Doors = (int)t[5], Battery = (int)t[6], Durability = (int)t[7] };
            for (int i = 0; i < 3; i++) Require((int)t[i] == 0 || (int)t[i] == 1, "robot flags");
            Require(r.Behavior >= 0 && r.Behavior <= 1 && r.Attack >= 0 && r.Attack <= 3 && r.Doors >= 0 && r.Doors <= 1 &&
                r.Battery >= 0 && r.Battery <= 100 && r.Durability >= 0 && r.Durability <= 100, "robot modes");
            var devices = (object[])t[8]; var upgrades = (object[])t[9];
            Require(devices.Length <= MaxRobotSlots && upgrades.Length <= MaxRobotSlots, "robot slots");
            r.Devices = new int[devices.Length]; r.Storage = new bool[devices.Length]; r.Medkit = new int[devices.Length];
            for (int i = 0; i < devices.Length; i++)
            {
                var d = devices[i] as object[];
                Require(d != null && d.Length == 3 && d[0] is int && d[1] is int && d[2] is int, "robot device");
                Require((int)d[0] >= -1 && (int)d[0] <= 65535 && ((int)d[1] == 0 || (int)d[1] == 1) && (int)d[2] >= -1 && (int)d[2] <= 1, "robot device");
                r.Devices[i] = (int)d[0]; r.Storage[i] = (int)d[1] == 1; r.Medkit[i] = (int)d[2];
            }
            r.Upgrades = new int[upgrades.Length];
            for (int i = 0; i < upgrades.Length; i++) { Require(upgrades[i] is int && (int)upgrades[i] >= -1 && (int)upgrades[i] <= 65535, "robot upgrade"); r.Upgrades[i] = (int)upgrades[i]; }
            return r;
        }

        // Craft at a host workbench: [target, recipe item id, count, ingredients escrowed by the guest].
        internal const int MaxCraft = 20;
        internal const int MaxCraftIngredients = 16;
        internal static byte[] CraftPayload(byte[] target, int recipe, int count, object[] ingredients)
        {
            Require(ingredients != null && ingredients.Length >= 1 && ingredients.Length <= MaxCraftIngredients, "craft ingredients");
            Require(recipe >= 0 && recipe <= 65535 && count >= 1 && count <= MaxCraft, "craft recipe");
            var items = new object[ingredients.Length];
            for (int i = 0; i < items.Length; i++) items[i] = Normalize((object[])ingredients[i]);
            return Payload(new object[] { LanValueCodec.Decode(target), recipe, count, items });
        }
        // `a` and `b` as one list: equal items (same id and data) become one entry.
        internal static object[] MergeItems(object[] a, object[] b)
        {
            var merged = new List<object[]>();
            var prints = new List<byte[]>();
            foreach (var list in new[] { a, b })
                foreach (object[] raw in list)
                {
                    var item = Normalize(raw);
                    var print = Fingerprint(item);
                    int at = prints.FindIndex(x => SameFingerprint(x, print));
                    if (at < 0) { merged.Add(item); prints.Add(print); continue; }
                    long sum = (long)(int)merged[at][1] + (int)item[1];
                    Require(sum <= MaxAmount, "merged amount");
                    merged[at][1] = (int)sum;
                }
            return merged.ToArray();
        }
        internal static object[] DecodeCraft(byte[] payload, out int recipe, out int count, out object[] ingredients)
        {
            var t = Decode(payload, "craft");
            Require(t.Length == 4 && t[0] is object[] && t[1] is int && t[2] is int && t[3] is object[], "craft fields");
            var target = ValidTarget((object[])t[0]);
            recipe = (int)t[1]; count = (int)t[2];
            Require(recipe >= 0 && recipe <= 65535 && count >= 1 && count <= MaxCraft, "craft recipe");
            var raw = (object[])t[3];
            Require(raw.Length >= 1 && raw.Length <= MaxCraftIngredients, "craft ingredients");
            ingredients = new object[raw.Length];
            for (int i = 0; i < raw.Length; i++) ingredients[i] = Normalize(raw[i] as object[]);
            return target;
        }

        // Cancel one queued craft of the open workbench (1.1.5): [queue index, recipe item id].
        // The host does what the game's queue button does: the ingredients of one craft go
        // into the workbench's own inventory.
        internal static byte[] CraftCancelPayload(int index, int recipe) { return Payload(new object[] { index, recipe }); }
        internal static void DecodeCraftCancel(byte[] payload, out int index, out int recipe)
        {
            var t = Decode(payload, "craft cancel");
            Require(t.Length == 2 && t[0] is int && t[1] is int, "craft cancel fields");
            index = (int)t[0]; recipe = (int)t[1];
            Require(index >= 0 && index < MaxCraftQueue && recipe >= 0 && recipe <= 65535, "craft cancel values");
        }

        // ---- Views: [format, handle, status, name, weight, max weight, items(, craft)] ----
        // A workbench adds its craft state (1.1.5): [item being made or -1, whole seconds of
        // work done on it, 1 while the work advances, queue [[item id, amount], ...]].
        internal const int MaxCraftQueue = 32, MaxCraftSeconds = 100000000;
        internal sealed class CraftState
        {
            internal int WorkingOn = -1, Elapsed;
            internal bool Working;
            internal int[][] Queue = new int[0][];
        }
        internal sealed class View
        {
            internal int Handle;
            internal byte Status;
            internal string Name = "";
            internal float Weight, MaxWeight;
            internal object[] Items = new object[0];
            internal CraftState Craft;
        }
        internal static byte[] EncodeView(View v)
        {
            var items = new object[v.Items.Length];
            for (int i = 0; i < items.Length; i++) items[i] = Normalize(v.Items[i] as object[]);
            string name = v.Name ?? "";
            if (name.Length > MaxName) name = name.Substring(0, MaxName);
            object[] root;
            if (v.Craft == null) root = new object[] { ViewFormat, v.Handle, (int)v.Status, name, v.Weight, v.MaxWeight, items };
            else
            {
                var queue = new object[v.Craft.Queue.Length];
                for (int i = 0; i < queue.Length; i++) queue[i] = new object[] { v.Craft.Queue[i][0], v.Craft.Queue[i][1] };
                var craft = new object[] { v.Craft.WorkingOn, v.Craft.Elapsed, v.Craft.Working ? 1 : 0, queue };
                root = new object[] { ViewFormat, v.Handle, (int)v.Status, name, v.Weight, v.MaxWeight, items, craft };
            }
            CheckView(root);
            byte[] blob = LanValueCodec.Encode(root);
            Require(blob.Length <= LanProtocol.MaxGuestBytes, "view size");
            return blob;
        }
        internal static View DecodeView(byte[] blob)
        {
            object[] root;
            try { root = LanValueCodec.Decode(blob); }
            catch (ArgumentException) { throw new InvalidDataException("Storage view text"); }
            catch (EndOfStreamException) { throw new InvalidDataException("Storage view truncated"); }
            CheckView(root);
            var items = (object[])root[6];
            for (int i = 0; i < items.Length; i++) items[i] = Normalize((object[])items[i]);
            CraftState craft = null;
            if (root.Length == 8)
            {
                var c = (object[])root[7]; var queue = (object[])c[3];
                craft = new CraftState { WorkingOn = (int)c[0], Elapsed = (int)c[1], Working = (int)c[2] == 1, Queue = new int[queue.Length][] };
                for (int i = 0; i < queue.Length; i++) { var q = (object[])queue[i]; craft.Queue[i] = new[] { (int)q[0], (int)q[1] }; }
            }
            return new View { Handle = (int)root[1], Status = (byte)(int)root[2], Name = (string)root[3], Weight = (float)root[4], MaxWeight = (float)root[5], Items = items, Craft = craft };
        }
        private static void CheckView(object[] root)
        {
            Require(root != null && (root.Length == 7 || root.Length == 8) && root[0] is int && (int)root[0] == ViewFormat, "view header");
            Require(root[1] is int && (int)root[1] > 0, "view handle");
            Require(root[2] is int && (int)root[2] >= 0 && (int)root[2] <= LanProtocol.MaxStorageStatus, "view status");
            Require(root[3] is string && ((string)root[3]).Length <= MaxName, "view name");
            Require(root[4] is float && root[5] is float && Math.Abs((float)root[4]) <= 1000000f && (float)root[5] >= 0 && (float)root[5] <= 1000000f, "view weight");
            var items = root[6] as object[];
            Require(items != null && items.Length <= LanGuestSchema.MaxItems, "view items");
            foreach (var item in items) ValidateItem(item as object[]);
            Require((int)root[2] == Ok || items.Length == 0, "closed view items");
            if (root.Length == 8)
            {
                Require((int)root[2] == Ok, "closed view craft");
                var c = root[7] as object[];
                Require(c != null && c.Length == 4 && c[0] is int && c[1] is int && c[2] is int && c[3] is object[], "view craft");
                Require((int)c[0] >= -1 && (int)c[0] <= 65535 && (int)c[1] >= 0 && (int)c[1] <= MaxCraftSeconds && ((int)c[2] == 0 || (int)c[2] == 1), "view craft work");
                var queue = (object[])c[3];
                Require(queue.Length <= MaxCraftQueue, "view craft queue");
                foreach (var entry in queue)
                {
                    var q = entry as object[];
                    Require(q != null && q.Length == 2 && q[0] is int && q[1] is int && (int)q[0] >= 0 && (int)q[0] <= 65535 && (int)q[1] >= 1 && (int)q[1] <= MaxAmount, "view craft entry");
                }
            }
        }

        private static byte[] Payload(object[] value)
        {
            byte[] bytes = LanValueCodec.Encode(value);
            Require(bytes.Length <= LanProtocol.MaxStoragePayload, "payload size");
            return bytes;
        }
        private static object[] Decode(byte[] payload, string what)
        {
            Require(payload != null && payload.Length > 0 && payload.Length <= LanProtocol.MaxStoragePayload, what + " size");
            try { return LanValueCodec.Decode(payload); }
            catch (ArgumentException) { throw new InvalidDataException("Storage " + what + " text"); }
            catch (EndOfStreamException) { throw new InvalidDataException("Storage " + what + " truncated"); }
        }
        private static void Require(bool condition, string what) { if (!condition) throw new InvalidDataException("Storage: " + what); }
    }

    // 1.4.0: the guest's throw of explosive item `item` from its hand (in car `car`'s space, or the
    // world for -1) with its camera turned `turn`, the game's standard or alternative throw.
    internal interface ILanThrowWorld
    {
        byte Throw(int item, int car, float[] at, float[] turn, int type);
    }
    // 1.4.2: the guest's furniture kits placed on the host's train, and its train taken apart.
    internal interface ILanBuildWorld
    {
        byte PlaceFurniture(int kit, int car, float[] pose);
        // `given`: what the guest receives (null: nothing yet); `done`: the object is gone.
        byte Dismantle(int mode, int car, int owner, int identity, int tool, out object[] given, out bool done);
    }
    // 1.4.0: paint, wires and sign text of the host's train for the guest.
    internal interface ILanDecorWorld
    {
        byte Decor(LanStorage.DecorRequest request);
    }

    // The host's train controls (0.10.0): a button, lever, cab light, repair or light of a
    // built part, used for the guest as the host's own hand would (LanTrainControl).
    internal interface ILanTrainControlWorld
    {
        byte Control(int kind, int index, int check, int value, object[] escrow);
    }

    // The guest died (0.12.0): its belongings go into bags at the place it fell.
    internal interface ILanDeathWorld
    {
        // Ok when every item lies in the host's world (bags, or loose where a bag had no room).
        byte Death(object[] items, float x, float y, float z, int car);
    }

    // Quests of the host's scene (1.2.0), answered with the host's own quest objects.
    internal interface ILanQuestWorld
    {
        // Accepts quest `key` of `scene` for the guest. `gifts` are the items the game hands the
        // player who accepts it; they leave the host's world, so with `room` false (the guest's
        // ledger is full) a quest with gifts is refused. Ok and no gifts when it was accepted already.
        byte Accept(uint key, string scene, bool room, out object[] gifts);
        // While the quest is ready to be handed in: what that costs ([item, amount] or null) and
        // what it gives. NotReady otherwise.
        byte Prepare(uint key, string scene, out int[] need, out object[] rewards);
        // The quest is handed in: the host's copy is completed (its OnCompleted runs in the host's world).
        void Complete(uint key, string scene);
        // 1.4.5: the reward of a quest the host handed in while the attached guest played, once; with
        // `room` false (the guest's ledger is full) a reward with items is refused and kept. NotReady
        // when there is none for this guest.
        byte Share(uint key, string scene, bool room, out object[] rewards);
    }

    // Carryable objects and the guest's robot dog (1.2.1), answered by the host's world. `request`
    // comes from LanStorage.DecodeTakable; `answer` goes back to the guest as it is (a robot state:
    // LanStorage.EncodeRobot), `amount` too (a medkit: the healing x100).
    internal interface ILanTakableWorld
    {
        byte Takable(object[] request, out byte[] answer, out int amount);
    }

    // What the host side needs from the game. Every container is an opaque token.
    internal interface ILanStoragePlacementWorld
    {
        byte PlaceItem(object[] item, int car, float[] pose);
    }

    internal interface ILanStorageHostWorld
    {
        // A container matching a validated target that the guest may use now.
        object Resolve(object[] target, out byte status);
        // Ok while the container exists, the world is unchanged and the guest is near.
        byte Check(object container);
        // Name, weights and items (Inventory.Save() entries); null when it cannot be shown.
        LanStorage.View Snapshot(object container);
        // Removes up to `amount` of the matching item; `taken` is what the guest receives.
        byte Take(object container, int index, byte[] fingerprint, int amount, out object[] taken);
        // Adds up to the item's amount; `accepted` is how many were stored.
        byte Put(object container, object[] item, out int accepted);
        // Removes up to `amount` of a loose item of the world near the guest.
        byte Pickup(int hostId, int itemId, int amount, out object[] taken);
        // Places an item into the world at a point near the guest.
        byte Drop(object[] item, float x, float y, float z);
        // Works on the world with the guest's tool; `given` is what the guest receives (or null),
        // `detail` a non-negative number for the guest (refuel: the tank's fuel x100).
        byte Work(int kind, float x, float y, float z, int tool, int extra, object[] escrow, out object[] given, out int detail);
        // One hit of the guest's tool on a host blueprint part or unassembled furniture.
        // `pay` takes a part's recipe from the guest's ledger (false: not enough) and is
        // called only for the hit that completes it; `completed` says whether it was.
        byte Build(int car, int kind, int identity, int tool, Func<int[][], bool> pay, out bool completed);
        // Places a blueprint of part `part` on the host's train as the guest previewed it.
        byte Place(int part, int car, int pointType, float[] point, float[] pose);
        // The ingredients one craft of `recipe` at this workbench needs, or null (not offered, manual, gone, far).
        int[][] Recipe(object[] target, int recipe, out byte status);
        // Queues `count` of `recipe` at the workbench (ingredients already paid).
        byte Queue(object[] target, int recipe, int count);
        // The game's queue button on the open workbench: one craft of entry `index` (which must
        // still be `recipe`) leaves the queue and its ingredients go into the workbench.
        byte CancelCraft(object container, int index, int recipe);
    }

    // Optional game-world notification: manual work follows the one open guest view.
    internal interface ILanStorageViewWorld
    {
        void SetGuestView(object container);
    }

    // Host half for one paired guest: request deduplication, at most one open
    // container, its view pushed (chunked) whenever its contents change.
    internal sealed class LanStorageHost
    {
        private const double PollEvery = 0.25, ChunksPerSecond = 40, Retry = 1;
        private readonly ILanStorageHostWorld _world;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly Func<bool> _guestReady;
        private readonly LanBlobSender _view = new LanBlobSender();
        // The guest's ledger (0.7.0): takes are handed out on it, puts must come from it.
        internal Func<LanGuestLedger> Ledger;
        // Quests of the host's scene (1.2.0); null: refused.
        internal ILanQuestWorld Quests;
        // A quest request of the guest was carried out (1.2.0): its kind and key.
        internal Action<int, uint> QuestChanged;
        // Carryable objects and the guest's robot dog (1.2.1); null: refused.
        internal ILanTakableWorld Takables;
        private object _container;
        private int _handle, _nextHandle;
        private byte[] _lastView;
        private double _nextPoll, _nextOpen, _nextWork, _nextPlace, _nextDeath, _nextThrow, _nextDecor;
        private uint _lastTx;
        private Packet _lastResult;
        private bool _active;
        internal bool Open { get { return _container != null; } }

        internal LanStorageHost(ILanStorageHostWorld world, Action<Packet> send, Action<string> log, Func<bool> guestReady)
        { _world = world; _send = send; _log = log; _guestReady = guestReady; }

        internal void Begin() { SetGuestView(null); _active = true; _container = null; _handle = 0; _lastView = null; _lastTx = 0; _lastResult = null; _view.Cancel(); }
        internal void End() { SetGuestView(null); _active = false; _container = null; _view.Cancel(); }

        internal void Tick(double now)
        {
            if (!_active) return;
            if (!_guestReady()) { CloseView(LanStorage.Unavailable); return; }
            if (_container != null && now >= _nextPoll)
            {
                _nextPoll = now + PollEvery;
                byte status = _world.Check(_container);
                if (status != LanStorage.Ok) CloseView(status);
                else
                {
                    var view = _world.Snapshot(_container);
                    if (view == null) CloseView(LanStorage.TooLarge);
                    else
                    {
                        view.Handle = _handle; view.Status = LanStorage.Ok;
                        byte[] blob;
                        try { blob = LanStorage.EncodeView(view); }
                        catch (InvalidDataException ex) { _log("Storage view not sent: " + ex.Message); CloseView(LanStorage.TooLarge); blob = null; }
                        if (blob != null && (_lastView == null || !Same(blob, _lastView))) { _lastView = blob; _view.Start(blob); }
                    }
                }
            }
            _view.Tick(now, ChunksPerSecond, Retry, _send, PacketKind.StorageViewChunk);
        }

        internal void Receive(Packet p, double now)
        {
            if (!_active) return;
            if (p.Kind == PacketKind.StorageViewAck) { _view.Acknowledge(p.Revision); return; }
            if (p.Kind != PacketKind.StorageRequest) return;
            if (p.Sequence < _lastTx) return;
            if (p.Sequence == _lastTx) { if (_lastResult != null) _send(_lastResult); return; } // lost answer
            var book = Ledger == null ? null : Ledger();
            if (book != null) book.BeginChange();
            Packet result;
            try { result = Process(p, now); }
            catch { if (book != null) book.EndChange(false); throw; }
            if (book != null) book.EndChange(result.Action == LanStorage.Ok);
            result.Kind = PacketKind.StorageResult; result.Sequence = p.Sequence;
            _lastTx = p.Sequence; _lastResult = result;
            _send(result);
        }

        private Packet Process(Packet p, double now)
        {
            if (!_guestReady()) return Result(LanStorage.Unavailable);
            switch (p.Action)
            {
                case LanProtocol.StorageOpen:
                    // Resolving searches the scene: at most two opens per second.
                    if (now < _nextOpen) return Result(LanStorage.Busy);
                    _nextOpen = now + 0.5;
                    object[] target;
                    try { target = LanStorage.DecodeTarget(p.Chunk); }
                    catch (InvalidDataException ex) { _log("Storage target rejected: " + ex.Message); return Result(LanStorage.Invalid); }
                    byte found;
                    var container = _world.Resolve(target, out found);
                    if (container == null) return Result(found == LanStorage.Ok ? LanStorage.NotFound : found);
                    _container = container; SetGuestView(container); _handle = ++_nextHandle; _lastView = null; _nextPoll = 0;
                    return new Packet { Action = LanStorage.Ok, Revision = _handle };
                case LanProtocol.StorageClose:
                    if (p.Revision == _handle) { SetGuestView(null); _container = null; _view.Cancel(); }
                    return Result(LanStorage.Ok);
                case LanProtocol.StoragePickup: return Pickup(p);
                case LanProtocol.StorageDrop: return Drop(p);
                case LanProtocol.StorageCraft: return Report("Craft", Craft(p));
                case LanProtocol.StorageQuest: return Report("Quest", Quest(p));
                case LanProtocol.StorageTakable: return Report("Takable", Takable(p));
                case LanProtocol.StorageControl: return Report("Train control", Control(p));
                case LanProtocol.StorageDeath:
                    if (now < _nextDeath) return Result(LanStorage.Busy);
                    _nextDeath = now + 5;
                    return Death(p);
                case LanProtocol.StorageThrow:
                    // The game's own throw takes over a second (SetThrowingPhase).
                    if (now < _nextThrow) return Result(LanStorage.Busy);
                    _nextThrow = now + 0.5;
                    return Report("Throw", Throw(p));
                case LanProtocol.StorageDecor:
                    if (now < _nextDecor) return Result(LanStorage.Busy);
                    _nextDecor = now + 0.1;
                    return Report("Train decor", Decor(p));
                case LanProtocol.StoragePlace:
                    if (now < _nextPlace) return Result(LanStorage.Busy);
                    _nextPlace = now + 0.4;
                    return Place(p);
                case LanProtocol.StorageFurniture:
                    if (now < _nextPlace) return Result(LanStorage.Busy);
                    _nextPlace = now + 0.4;
                    return Report("Furniture placement", Furniture(p));
                case LanProtocol.StorageDismantle:
                    // A melee swing is not faster than this (as building).
                    if (now < _nextWork) return Result(LanStorage.Busy);
                    _nextWork = now + 0.3;
                    return Report("Dismantling", Dismantle(p));
                case LanProtocol.StorageWork:
                case LanProtocol.StorageBuild:
                    // A melee swing is not faster than this.
                    if (now < _nextWork) return Result(LanStorage.Busy);
                    _nextWork = now + 0.3;
                    return p.Action == LanProtocol.StorageWork ? Work(p) : Build(p);
            }
            if (_container == null || p.Revision != _handle) return Result(LanStorage.Gone);
            byte status = _world.Check(_container);
            if (status != LanStorage.Ok) { CloseView(status); return Result(status); }
            _nextPoll = 0; // show the outcome at once
            if (p.Action == LanProtocol.StorageCraftCancel)
            {
                int index, recipe;
                try { LanStorage.DecodeCraftCancel(p.Chunk, out index, out recipe); }
                catch (InvalidDataException ex) { _log("Craft cancel rejected: " + ex.Message); return Result(LanStorage.Invalid); }
                return Report("Craft cancel", Result(_world.CancelCraft(_container, index, recipe)));
            }
            if (p.Action == LanProtocol.StorageTake)
            {
                int index, amount; byte[] fingerprint;
                try { LanStorage.DecodeTake(p.Chunk, out index, out amount, out fingerprint); }
                catch (InvalidDataException ex) { _log("Storage take rejected: " + ex.Message); return Result(LanStorage.Invalid); }
                object[] taken;
                var book = Ledger == null ? null : Ledger();
                if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
                if (book != null && !book.CanReceive()) return Result(LanStorage.TooLarge);
                status = _world.Take(_container, index, fingerprint, amount, out taken);
                if (status != LanStorage.Ok) return Result(status);
                if (book != null) book.Give(LanGuestLedger.RequestChannel, p.Sequence, taken);
                return new Packet { Action = LanStorage.Ok, Revision = _handle, Amount = (int)taken[1], Chunk = LanStorage.EncodeItem(taken) };
            }
            object[] item;
            try { item = LanStorage.DecodeItem(p.Chunk); }
            catch (InvalidDataException ex) { _log("Storage put rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var owner = Ledger == null ? null : Ledger();
            if (Ledger != null && (owner == null || !owner.Take(item))) return Result(owner == null ? LanStorage.Unavailable : LanStorage.NotOwned);
            int accepted;
            status = _world.Put(_container, item, out accepted);
            if (status != LanStorage.Ok) accepted = 0;
            if (owner != null && accepted < (int)item[1]) owner.Refund(LanStorage.WithAmount(item, (int)item[1] - accepted));
            if (status != LanStorage.Ok) return Result(status);
            return new Packet { Action = LanStorage.Ok, Revision = _handle, Amount = accepted };
        }

        private Packet Pickup(Packet p)
        {
            int hostId, itemId, amount;
            try { LanStorage.DecodePickup(p.Chunk, out hostId, out itemId, out amount); }
            catch (InvalidDataException ex) { _log("Pickup rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            object[] taken;
            if (book != null && !book.CanReceive()) return Result(LanStorage.TooLarge);
            byte status = _world.Pickup(hostId, itemId, amount, out taken);
            if (status != LanStorage.Ok) return Result(status);
            if (book != null) book.Give(LanGuestLedger.RequestChannel, p.Sequence, taken);
            return new Packet { Action = LanStorage.Ok, Amount = (int)taken[1], Chunk = LanStorage.EncodeItem(taken) };
        }

        private Packet Drop(Packet p)
        {
            object[] item; float x, y, z; int car; float[] rotation;
            try { item = LanStorage.DecodeDrop(p.Chunk, out x, out y, out z, out car, out rotation); }
            catch (InvalidDataException ex) { _log("Drop rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var owner = Ledger == null ? null : Ledger();
            if (Ledger != null && (owner == null || !owner.Take(item))) return Result(owner == null ? LanStorage.Unavailable : LanStorage.NotOwned);
            var placement = _world as ILanStoragePlacementWorld;
            byte status = rotation == null ? _world.Drop(item, x, y, z) : placement == null ? LanStorage.NotAllowed :
                placement.PlaceItem(item, car, new[] { x, y, z, rotation[0], rotation[1], rotation[2], rotation[3] });
            if (status != LanStorage.Ok) { if (owner != null) owner.Refund(item); return Result(status); }
            return new Packet { Action = LanStorage.Ok, Amount = (int)item[1] };
        }

        private Packet Work(Packet p)
        {
            int kind, tool, extra; float x, y, z; object[] escrow;
            try { kind = LanStorage.DecodeWork(p.Chunk, out x, out y, out z, out tool, out extra, out escrow); }
            catch (InvalidDataException ex) { _log("Work rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            // The tool must be the guest's, and not broken.
            if (book != null && tool >= 0 && (book.Count(tool) < 1 || book.BestWear(tool) == 0f)) return Result(LanStorage.NotOwned);
            if (book != null && escrow != null && !book.Take(escrow)) return Result(LanStorage.NotOwned);
            object[] given; int detail;
            byte status = _world.Work(kind, x, y, z, tool, extra, escrow, out given, out detail);
            if (status != LanStorage.Ok) { if (book != null && escrow != null) book.Refund(escrow); return Result(status); }
            if (book != null && tool >= 0 && book.BestWear(tool) > 0f) book.Wear(tool, 1f);
            detail = Math.Max(0, detail);
            if (given == null) return new Packet { Action = LanStorage.Ok, Revision = detail };
            if (book != null) book.Give(LanGuestLedger.RequestChannel, p.Sequence, given);
            return new Packet { Action = LanStorage.Ok, Revision = detail, Amount = (int)given[1], Chunk = LanStorage.EncodeItem(given) };
        }

        private Packet Build(Packet p)
        {
            int car, kind, identity, tool;
            try { LanStorage.DecodeBuild(p.Chunk, out car, out kind, out identity, out tool); }
            catch (InvalidDataException ex) { _log("Build rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            if (book != null && (book.Count(tool) < 1 || book.BestWear(tool) == 0f)) return Result(LanStorage.NotOwned);
            bool completed;
            byte status = _world.Build(car, kind, identity, tool, needs => Pay(book, needs), out completed);
            if (status != LanStorage.Ok) return Result(status);
            if (book != null && book.BestWear(tool) > 0f) book.Wear(tool, 1f);
            return new Packet { Action = LanStorage.Ok, Amount = completed ? 1 : 0 };
        }
        // 1.4.0: the guest's explosive leaves its ledger and flies in the host's world.
        private Packet Throw(Packet p)
        {
            object[] escrow; int car, type; float[] at, turn;
            try { escrow = LanStorage.DecodeThrow(p.Chunk, out car, out at, out turn, out type); }
            catch (InvalidDataException ex) { _log("Throw rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var throws = _world as ILanThrowWorld;
            if (throws == null) return Result(LanStorage.NotAllowed);
            var owner = Ledger == null ? null : Ledger();
            if (Ledger != null && (owner == null || !owner.Take(escrow))) return Result(owner == null ? LanStorage.Unavailable : LanStorage.NotOwned);
            byte status = throws.Throw((int)escrow[0], car, at, turn, type);
            if (status != LanStorage.Ok) { if (owner != null) owner.Refund(escrow); return Result(status); }
            return Result(LanStorage.Ok);
        }
        // 1.4.0: paint, wires and sign text of the host's train (nothing is spent).
        private Packet Decor(Packet p)
        {
            LanStorage.DecorRequest request;
            try { request = LanStorage.DecodeDecor(p.Chunk); }
            catch (InvalidDataException ex) { _log("Train decor rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var decor = _world as ILanDecorWorld;
            return Result(decor == null ? LanStorage.NotAllowed : decor.Decor(request));
        }

        // 1.4.2: the guest's furniture kit leaves its ledger; the host places the unassembled furniture.
        private Packet Furniture(Packet p)
        {
            object[] escrow; int car; float[] pose;
            try { escrow = LanStorage.DecodeFurniture(p.Chunk, out car, out pose); }
            catch (InvalidDataException ex) { _log("Furniture placement rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var build = _world as ILanBuildWorld;
            if (build == null) return Result(LanStorage.NotAllowed);
            var owner = Ledger == null ? null : Ledger();
            if (Ledger != null && (owner == null || !owner.Take(escrow))) return Result(owner == null ? LanStorage.Unavailable : LanStorage.NotOwned);
            byte status = build.PlaceFurniture((int)escrow[0], car, pose);
            if (status != LanStorage.Ok) { if (owner != null) owner.Refund(escrow); return Result(status); }
            return Result(LanStorage.Ok);
        }
        // 1.4.2: one hit of the guest's tool taking a part or furniture apart, or a blueprint taken away;
        // what it gives goes onto the guest's ledger.
        private Packet Dismantle(Packet p)
        {
            int car, owner, identity, tool;
            int mode;
            try { mode = LanStorage.DecodeDismantle(p.Chunk, out car, out owner, out identity, out tool); }
            catch (InvalidDataException ex) { _log("Dismantling rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var build = _world as ILanBuildWorld;
            if (build == null) return Result(LanStorage.NotAllowed);
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            if (book != null && mode == LanStorage.DismantleHit && (book.Count(tool) < 1 || book.BestWear(tool) == 0f)) return Result(LanStorage.NotOwned);
            if (book != null && !book.CanReceive()) return Result(LanStorage.TooLarge);
            object[] given; bool done;
            byte status = build.Dismantle(mode, car, owner, identity, tool, out given, out done);
            if (status != LanStorage.Ok) return Result(status);
            if (book != null && mode == LanStorage.DismantleHit && book.BestWear(tool) > 0f) book.Wear(tool, 1f);
            if (given == null || given.Length == 0) return new Packet { Action = LanStorage.Ok, Amount = done ? 1 : 0 };
            if (given.Length > LanStorage.MaxRefundItems) { var cut = new object[LanStorage.MaxRefundItems]; Array.Copy(given, cut, cut.Length); given = cut; }
            for (int i = 0; i < given.Length; i++) given[i] = LanStorage.Normalize((object[])given[i]);
            if (book != null) foreach (object[] item in given) book.Give(LanGuestLedger.RequestChannel, p.Sequence, item);
            return new Packet { Action = LanStorage.Ok, Amount = done ? 1 : 0, Chunk = LanStorage.RefundPayload(given) };
        }

        private Packet Place(Packet p)
        {
            int part, car, pointType; float[] point, pose;
            try { LanStorage.DecodePlace(p.Chunk, out part, out car, out pointType, out point, out pose); }
            catch (InvalidDataException ex) { _log("Placement rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            byte status = _world.Place(part, car, pointType, point, pose);
            return Result(status);
        }

        // 1.1.1: a refusal of the guest's request goes to the log (what was refused and why).
        private Packet Report(string what, Packet result)
        {
            if (result != null && result.Action != LanStorage.Ok && result.Action != LanStorage.Busy)
                _log(what + " of the guest refused: status " + result.Action + " (" + LanStorage.Describe(result.Action) + ")");
            return result;
        }

        private Packet Control(Packet p)
        {
            int kind, index, check, value; object[] escrow;
            try { LanStorage.DecodeControl(p.Chunk, out kind, out index, out check, out value, out escrow); }
            catch (InvalidDataException ex) { _log("Control rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var controls = _world as ILanTrainControlWorld;
            if (controls == null) return Result(LanStorage.NotAllowed);
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            // The spare lever of a repair must be the guest's.
            if (book != null && escrow != null && !book.Take(escrow)) return Result(LanStorage.NotOwned);
            byte status = controls.Control(kind, index, check, value, escrow);
            if (status != LanStorage.Ok && book != null && escrow != null) book.Refund(escrow);
            return Result(status);
        }

        private Packet Death(Packet p)
        {
            float x, y, z; int car;
            try { LanStorage.DecodeDeath(p.Chunk, out x, out y, out z, out car); }
            catch (InvalidDataException ex) { _log("Death rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var deaths = _world as ILanDeathWorld;
            if (deaths == null) return Result(LanStorage.NotAllowed);
            var book = Ledger == null ? null : Ledger();
            if (book == null) return Result(LanStorage.Unavailable);
            var items = book.TakeAll();
            byte status = items.Length == 0 ? LanStorage.Ok : deaths.Death(items, x, y, z, car);
            if (status != LanStorage.Ok) { foreach (object[] item in items) book.Refund(item); return Result(status); }
            _log("Guest died; " + items.Length + " item stack(s) left where it fell");
            return new Packet { Action = LanStorage.Ok, Amount = items.Length };
        }

        // 1.2.0: the guest accepts or hands in a quest of the host's scene. Gifts and rewards go
        // onto its ledger; what handing in costs comes off it, all of it or nothing.
        private Packet Quest(Packet p)
        {
            int kind; uint key; string scene;
            try { kind = LanStorage.DecodeQuest(p.Chunk, out key, out scene); }
            catch (InvalidDataException ex) { _log("Quest request rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var quests = Quests;
            if (quests == null) return Result(LanStorage.NotAllowed);
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            object[] given; byte status;
            if (kind == LanStorage.QuestAccept)
            {
                status = quests.Accept(key, scene, book == null || book.CanReceive(), out given);
                if (status != LanStorage.Ok) return Result(status);
            }
            else if (kind == LanStorage.QuestShare)
            {
                status = quests.Share(key, scene, book == null || book.CanReceive(), out given);
                if (status != LanStorage.Ok) return Result(status);
                if (given != null && given.Length > LanStorage.MaxQuestItems) return Result(LanStorage.TooLarge);
            }
            else
            {
                int[] need;
                status = quests.Prepare(key, scene, out need, out given);
                if (status != LanStorage.Ok) return Result(status);
                if (given.Length > LanStorage.MaxQuestItems) return Result(LanStorage.TooLarge);
                if (book != null && given.Length > 0 && !book.CanReceive()) return Result(LanStorage.TooLarge);
                if (book != null && need != null && need[1] > 0 && !book.Remove(need[0], need[1])) return Result(LanStorage.NotOwned);
                quests.Complete(key, scene);
            }
            given = given ?? new object[0];
            byte[] chunk = null;
            if (given.Length > 0)
            {
                try { chunk = LanStorage.QuestItemsPayload(given); }
                catch (InvalidDataException ex) { _log("Quest items not sent: " + ex.Message); given = new object[0]; }
            }
            if (book != null && chunk != null) foreach (object[] item in given) book.Give(LanGuestLedger.RequestChannel, p.Sequence, item);
            QuestChanged?.Invoke(kind, key);
            return new Packet { Action = LanStorage.Ok, Amount = chunk == null ? 0 : given.Length, Chunk = chunk };
        }

        // 1.2.1: a host object the guest carries, or the guest's robot dog.
        private Packet Takable(Packet p)
        {
            object[] request;
            try { request = LanStorage.DecodeTakable(p.Chunk); }
            catch (InvalidDataException ex) { _log("Takable request rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            var world = Takables;
            if (world == null) return Result(LanStorage.NotAllowed);
            byte[] answer; int amount;
            byte status = world.Takable(request, out answer, out amount);
            if (status != LanStorage.Ok) return Result(status);
            if (answer != null && answer.Length > LanProtocol.MaxStoragePayload) { _log("Takable answer too large"); answer = null; }
            return new Packet { Action = LanStorage.Ok, Amount = Math.Max(0, Math.Min(amount, 1000000)), Chunk = answer };
        }

        // All of a recipe or nothing.
        private static bool Pay(LanGuestLedger book, int[][] needs)
        {
            var wanted = new Dictionary<int, long>();
            foreach (var need in needs) { if (need[1] <= 0) continue; long v; wanted.TryGetValue(need[0], out v); wanted[need[0]] = v + need[1]; }
            if (book == null) return true;
            foreach (var pair in wanted) if (book.Count(pair.Key) < pair.Value) return false;
            foreach (var pair in wanted) book.Remove(pair.Key, (int)pair.Value);
            return true;
        }

        private Packet Craft(Packet p)
        {
            object[] target, ingredients; int recipe, count;
            try { target = LanStorage.DecodeCraft(p.Chunk, out recipe, out count, out ingredients); }
            catch (InvalidDataException ex) { _log("Craft rejected: " + ex.Message); return Result(LanStorage.Invalid); }
            byte status;
            var needs = _world.Recipe(target, recipe, out status);
            if (needs == null) return Result(status == LanStorage.Ok ? LanStorage.NotAllowed : status);
            // The escrow must be exactly the recipe's ingredients for `count` crafts.
            var wanted = new Dictionary<int, long>(); var given = new Dictionary<int, long>();
            foreach (var need in needs) { long v; wanted.TryGetValue(need[0], out v); wanted[need[0]] = v + (long)need[1] * count; }
            foreach (object[] item in ingredients) { long v; given.TryGetValue((int)item[0], out v); given[(int)item[0]] = v + (int)item[1]; }
            if (wanted.Count != given.Count) return Result(LanStorage.Invalid);
            foreach (var pair in wanted) { long v; if (!given.TryGetValue(pair.Key, out v) || v != pair.Value) return Result(LanStorage.Invalid); }
            var book = Ledger == null ? null : Ledger();
            if (Ledger != null && book == null) return Result(LanStorage.Unavailable);
            if (book != null)
            {
                foreach (var pair in wanted) if (book.Count(pair.Key) < pair.Value) return Result(LanStorage.NotOwned);
                foreach (var pair in wanted) book.Remove(pair.Key, (int)pair.Value);
            }
            status = _world.Queue(target, recipe, count);
            if (status != LanStorage.Ok)
            {
                if (book != null) foreach (object[] item in ingredients) book.Refund(item);
                return Result(status);
            }
            return new Packet { Action = LanStorage.Ok, Amount = count };
        }

        private void SetGuestView(object container) { (_world as ILanStorageViewWorld)?.SetGuestView(container); }

        private void CloseView(byte status)
        {
            if (_container == null) return;
            SetGuestView(null); _container = null; _lastView = null;
            _view.Start(LanStorage.EncodeView(new LanStorage.View { Handle = _handle, Status = status == LanStorage.Ok ? LanStorage.Closed : status }));
        }
        private static Packet Result(byte status) { return new Packet { Action = status }; }
        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    // What the guest side needs from the game.
    internal interface ILanStorageGuestWorld
    {
        // How many of this item fit into the guest's backpack right now.
        int Room(object[] item);
        // Adds an item (received, or returned from escrow) to the guest's backpack.
        void Give(object[] item);
        // Removes what the host took from the guest's ledger for a construction ([id, amount]).
        void Spend(int[][] needs);
    }

    // Guest half: one request in flight (retried until answered), takes queued
    // behind it. A put is escrowed: the game removes the item before the request
    // and the client gives back whatever the host did not store.
    internal sealed class LanStorageClient
    {
        private const double RetryEvery = 1;
        private sealed class Op
        {
            internal byte Kind;
            internal uint Tx;
            internal int Handle;
            internal byte[] Payload;
            internal object[] Item; // take: the view entry; put/drop: the escrowed item; pickup: id and amount
            internal int HostId;    // pickup: the host's loose item
            internal object[] Items; // craft: the escrowed ingredients
            internal int Work;       // work: its kind
            internal int[][] Needs;  // build: what completing the target costs
            internal int Recipe, Count; // craft: recipe item id and how many
            internal byte[] Target;     // craft: the workbench
            internal uint Quest;        // quest: its key
            internal Action<byte, int, byte[]> Answer; // takable: the host's status, amount and answer
            internal Action<object[]> Return;          // throw (1.4.1): puts the escrow back where it was taken from
        }
        private readonly ILanStorageGuestWorld _world;
        private readonly Action<Packet> _send;
        private readonly Action<string> _log;
        private readonly Action _changed;
        private readonly LanBlobReceiver _views = new LanBlobReceiver();
        private readonly Queue<Op> _queue = new Queue<Op>();
        private Op _pending;
        private uint _tx;
        private int _handle;
        private bool _wanted;
        private LanStorage.View _early;
        private double _nextSend;
        internal LanStorage.View Current { get; private set; }
        internal string Message { get; private set; }
        // Open() was called and neither Close() nor the host ended it.
        internal bool IsOpen { get { return _wanted; } }
        internal bool Busy { get { return _pending != null || _queue.Count != 0; } }
        internal int PendingRequests { get { return (_pending != null ? 1 : 0) + _queue.Count; } }
        // The last request whose result this guest has applied.
        internal uint AppliedTx { get; private set; }
        internal Action<int, int> PickedUp;

        internal LanStorageClient(ILanStorageGuestWorld world, Action<Packet> send, Action<string> log, Action changed)
        { _world = world; _send = send; _log = log; _changed = changed; Message = ""; }

        // A new secure session: the host forgot every request; escrow comes back.
        internal void Begin() { Reset(); _tx = 0; AppliedTx = 0; _views.Reset(); }
        internal void End() { Reset(); }

        private void Reset()
        {
            if (_pending != null && Escrowed(_pending)) ReturnEscrow(_pending);
            foreach (var op in _queue) if (Escrowed(op)) ReturnEscrow(op);
            _pending = null; _queue.Clear(); _handle = 0; _wanted = false; Current = null; _early = null;
        }

        internal void Open(byte[] target)
        {
            Close();
            _wanted = true; Message = "Открытие…";
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageOpen, Payload = target });
        }

        // An open still in flight is closed when its answer arrives.
        internal void Close()
        {
            if (_handle != 0) _queue.Enqueue(new Op { Kind = LanProtocol.StorageClose, Handle = _handle });
            _handle = 0; _wanted = false; Current = null; Message = "";
            Keep(op => op.Kind != LanProtocol.StorageTake && op.Kind != LanProtocol.StorageOpen && op.Kind != LanProtocol.StorageCraftCancel);
        }

        internal void Take(int index, int amount)
        {
            if (Current == null || index < 0 || index >= Current.Items.Length || amount < 1) return;
            var item = (object[])Current.Items[index];
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageTake, Handle = _handle, Item = LanStorage.WithAmount(item, Math.Min(amount, (int)item[1])) });
        }

        internal void TakeAll()
        {
            if (Current == null) return;
            for (int i = 0; i < Current.Items.Length; i++) Take(i, (int)((object[])Current.Items[i])[1]);
        }

        // The caller has already removed `item` from the backpack (escrow).
        internal bool Put(object[] item)
        {
            if (_handle == 0 || Busy || !LanStorage.ItemFits(item)) return false;
            _queue.Enqueue(new Op { Kind = LanProtocol.StoragePut, Handle = _handle, Item = LanStorage.Normalize(item) });
            return true;
        }

        // Ask the host for a loose item of its world (up to what fits).
        internal void Pickup(int hostId, int itemId, int amount)
        {
            if (Busy || hostId == 0 || amount < 1) return;
            _queue.Enqueue(new Op { Kind = LanProtocol.StoragePickup, HostId = hostId, Item = new object[] { itemId, Math.Min(amount, LanStorage.MaxAmount), new object[4] } });
        }
        // The caller has already removed `item` from the backpack (escrow).
        internal bool Drop(object[] item, float x, float y, float z)
        {
            if (Busy || !LanStorage.ItemFits(item)) return false;
            byte[] payload;
            try { payload = LanStorage.DropPayload(item, x, y, z); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageDrop, Item = LanStorage.Normalize(item), Payload = payload });
            return true;
        }
        internal bool Drop(object[] item, int car, float[] pose)
        {
            if (Busy || !LanStorage.ItemFits(item) || pose == null || pose.Length != 7) return false;
            byte[] payload;
            try
            {
                payload = LanStorage.DropPayload(item, car, pose);
                float x, y, z; int decodedCar; float[] rotation;
                LanStorage.DecodeDrop(payload, out x, out y, out z, out decodedCar, out rotation);
            }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageDrop, Item = LanStorage.Normalize(item), Payload = payload });
            return true;
        }
        // Work on the world; an escrowed item (fuel: the empty canister) is already out of the backpack.
        internal bool Work(int kind, float x, float y, float z, int tool, int extra, object[] escrow)
        {
            if (escrow != null && Busy) return false;
            if (PendingRequests >= 4) return false; // swings faster than the host answers are dropped
            byte[] payload;
            try { payload = LanStorage.WorkPayload(kind, x, y, z, tool, extra, escrow); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageWork, Work = kind, Item = escrow == null ? null : LanStorage.Normalize(escrow), Payload = payload });
            return true;
        }
        // Requests a guest may line up before the host answers (crafts and cancels in the craft window).
        private const int MaxQueuedCraftRequests = 6;
        // Cancel one queued craft of the open workbench (no escrow: the ingredients stay with the host).
        internal bool CancelCraft(int index, int recipe)
        {
            if (_handle == 0 || PendingRequests >= MaxQueuedCraftRequests) return false;
            byte[] payload;
            try { payload = LanStorage.CraftCancelPayload(index, recipe); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageCraftCancel, Handle = _handle, Payload = payload });
            return true;
        }
        // Craft at a host workbench; `ingredients` are already out of the backpack. Crafts of one
        // recipe made before the host answers travel as one request (the game's craft amount).
        internal bool Craft(byte[] target, int recipe, int count, object[] ingredients)
        {
            if (target == null || ingredients == null || ingredients.Length == 0 || count < 1) return false;
            Op last = null;
            foreach (var queued in _queue) last = queued;
            if (last != null && last.Kind == LanProtocol.StorageCraft && last.Recipe == recipe && last.Target != null &&
                LanStorage.SameBytes(last.Target, target) && last.Count + count <= LanStorage.MaxCraft)
            {
                try
                {
                    var merged = LanStorage.MergeItems(last.Items, ingredients);
                    last.Payload = LanStorage.CraftPayload(target, recipe, last.Count + count, merged);
                    last.Items = merged; last.Count += count;
                    return true;
                }
                catch (InvalidDataException) { } // too many different ingredients: a request of its own
            }
            if (PendingRequests >= MaxQueuedCraftRequests) return false;
            byte[] payload;
            try { payload = LanStorage.CraftPayload(target, recipe, count, ingredients); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageCraft, Items = ingredients, Payload = payload, Recipe = recipe, Count = count, Target = target });
            return true;
        }
        // One hit on a host blueprint or unassembled furniture; `needs` is spent here if the host completes it.
        internal bool Build(int car, int kind, int identity, int tool, int[][] needs)
        {
            if (PendingRequests >= 2) return false; // swings faster than the host answers are dropped
            byte[] payload;
            try { payload = LanStorage.BuildPayload(car, kind, identity, tool); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageBuild, Payload = payload, Needs = needs });
            return true;
        }
        // A blueprint as the guest's construction mode previewed it; the host places it.
        internal bool Place(int part, int car, int pointType, float[] point, float[] pose)
        {
            if (PendingRequests >= 2) return false;
            byte[] payload;
            try { payload = LanStorage.PlacePayload(part, car, pointType, point, pose); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StoragePlace, Payload = payload });
            return true;
        }
        // A control of the host's train; a repair's spare lever is already out of the backpack.
        internal bool Control(int kind, int index, int check, int value, object[] escrow)
        {
            if (PendingRequests >= 3) return false; // presses faster than the host answers are dropped
            byte[] payload;
            try
            {
                payload = LanStorage.ControlPayload(kind, index, check, value, escrow);
                int k, i, c, v; object[] e;
                LanStorage.DecodeControl(payload, out k, out i, out c, out v, out e);
            }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageControl, Work = kind, Item = escrow == null ? null : LanStorage.Normalize(escrow), Payload = payload });
            return true;
        }
        // Successful work: its kind and the host's detail (refuel: the tank's fuel x100).
        internal Action<int, int> WorkDone;
        // A door the host opened or closed (1.1.6): the request's payload (ControlPayload).
        internal Action<byte[]> DoorDone;

        // The guest died where it stands (car-local when car >= 0). Pending requests stay queued.
        internal bool Death(float x, float y, float z, int car)
        {
            byte[] payload;
            try
            {
                payload = LanStorage.DeathPayload(x, y, z, car);
                float a, b, c; int d; LanStorage.DecodeDeath(payload, out a, out b, out c, out d);
            }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageDeath, Payload = payload });
            return true;
        }
        // The answer to Death: the host's status and how many item stacks it left there.
        internal Action<byte, int> DeathDone;

        // 1.2.0: accept or hand in quest `key` of `scene` at the host.
        internal bool Quest(int kind, uint key, string scene)
        {
            if (PendingRequests >= 4) return false;
            byte[] payload;
            try
            {
                payload = LanStorage.QuestPayload(kind, key, scene);
                uint k; string s; LanStorage.DecodeQuest(payload, out k, out s);
            }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageQuest, Work = kind, Quest = key, Payload = payload });
            return true;
        }
        // The answer to Quest: its kind, key, the host's status and how many items it handed out
        // (already in the backpack).
        internal Action<int, uint, byte, int> QuestDone;

        // 1.2.1: a request about a host object or the guest's robot dog (LanStorage payloads);
        // `answer` gets the host's status, amount and answer bytes.
        internal bool Takable(byte[] payload, Action<byte, int, byte[]> answer)
        {
            if (payload == null || PendingRequests >= 6) return false;
            try { LanStorage.DecodeTakable(payload); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageTakable, Payload = payload, Answer = answer });
            return true;
        }

        // 1.4.0: the guest's grenade, dynamite or C4, already out of the backpack (escrow), thrown in
        // the host's world; `answer` gets the host's status.
        internal bool Throw(object[] escrow, int car, float[] at, float[] turn, int type, Action<byte> answer, Action<object[]> giveBack = null)
        {
            if (escrow == null || PendingRequests >= 4) return false;
            byte[] payload;
            try
            {
                payload = LanStorage.ThrowPayload(escrow, car, at, turn, type);
                int c, k; float[] a, r; LanStorage.DecodeThrow(payload, out c, out a, out r, out k);
            }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageThrow, Item = LanStorage.Normalize(escrow), Payload = payload, Return = giveBack, Answer = (status, amount, chunk) => { if (answer != null) answer(status); } });
            return true;
        }
        // 1.4.0: paint, wires or sign text of the host's train; `answer` gets the host's status.
        internal bool Decor(LanStorage.DecorRequest request, Action<byte> answer)
        {
            if (request == null || PendingRequests >= 6) return false;
            byte[] payload;
            try { payload = LanStorage.DecorPayload(request); LanStorage.DecodeDecor(payload); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageDecor, Work = request.Kind, Payload = payload, Answer = (status, amount, chunk) => { if (answer != null) answer(status); } });
            return true;
        }

        // 1.4.2: a furniture kit, already out of the belt (escrow), placed on the host's train.
        internal bool Furniture(object[] escrow, int car, float[] pose, Action<byte> answer, Action<object[]> giveBack)
        {
            if (escrow == null || pose == null || pose.Length != 7 || PendingRequests >= 3) return false;
            byte[] payload;
            try { payload = LanStorage.FurniturePayload(escrow, car, pose); int c; float[] q; LanStorage.DecodeFurniture(payload, out c, out q); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageFurniture, Item = LanStorage.Normalize(escrow), Payload = payload, Return = giveBack, Answer = (status, amount, chunk) => { if (answer != null) answer(status); } });
            return true;
        }
        // 1.4.2: one hit taking a host part or furniture apart, or a blueprint away; `answer` gets the
        // host's status and whether the object is gone (what it gave is already in the backpack).
        internal bool Dismantle(int mode, int car, int owner, int identity, int tool, Action<byte, bool> answer)
        {
            if (PendingRequests >= 2) return false; // swings faster than the host answers are dropped
            byte[] payload;
            try { payload = LanStorage.DismantlePayload(mode, car, owner, identity, tool); int c, o, i, t; LanStorage.DecodeDismantle(payload, out c, out o, out i, out t); }
            catch (InvalidDataException) { return false; }
            _queue.Enqueue(new Op { Kind = LanProtocol.StorageDismantle, Work = mode, Payload = payload, Answer = (status, amount, chunk) => { if (answer != null) answer(status, amount == 1); } });
            return true;
        }

        private static bool Escrowed(Op op) { return (op.Kind == LanProtocol.StoragePut || op.Kind == LanProtocol.StorageDrop || op.Kind == LanProtocol.StorageWork || op.Kind == LanProtocol.StorageControl || op.Kind == LanProtocol.StorageThrow || op.Kind == LanProtocol.StorageFurniture) && op.Item != null || op.Items != null; }

        internal void Tick(double now)
        {
            if (_pending == null && _queue.Count != 0)
            {
                var op = _queue.Dequeue();
                if (op.Kind == LanProtocol.StorageTake)
                {
                    // The view may have changed while queued: ask for what fits now.
                    int room = _world.Room(op.Item);
                    int want = Math.Min(room, (int)op.Item[1]);
                    if (want < 1) { Message = "Рюкзак полон"; DropTakes(); return; }
                    int index = IndexOf(op.Item);
                    op.Payload = LanStorage.TakePayload(index < 0 ? 0 : index, want, LanStorage.Fingerprint(op.Item));
                }
                else if (op.Kind == LanProtocol.StoragePickup)
                {
                    int want = Math.Min(_world.Room(op.Item), (int)op.Item[1]);
                    if (want < 1) { Message = "Рюкзак полон"; return; }
                    op.Payload = LanStorage.PickupPayload(op.HostId, (int)op.Item[0], want);
                }
                else if (op.Kind == LanProtocol.StoragePut)
                {
                    try { op.Payload = LanStorage.EncodeItem(op.Item); }
                    catch (InvalidDataException) { ReturnEscrow(op.Item); Message = LanStorage.Describe(LanStorage.TooLarge); return; }
                }
                op.Tx = ++_tx; _pending = op; _nextSend = 0;
            }
            if (_pending != null && now >= _nextSend)
            {
                _nextSend = now + RetryEvery;
                _send(new Packet { Kind = PacketKind.StorageRequest, Sequence = _pending.Tx, Action = _pending.Kind, Revision = _pending.Handle, Chunk = _pending.Payload });
            }
        }

        internal void Receive(Packet p)
        {
            if (p.Kind == PacketKind.StorageViewChunk)
            {
                bool ack;
                byte[] blob = _views.Accept(p, out ack);
                if (ack) _send(new Packet { Kind = PacketKind.StorageViewAck, Revision = p.Revision });
                if (blob == null) return;
                LanStorage.View view;
                try { view = LanStorage.DecodeView(blob); }
                catch (InvalidDataException ex) { _log("Storage view rejected: " + ex.Message); return; }
                // A view may overtake the answer to its own open: keep the newest one.
                if (_handle == 0 || view.Handle != _handle) { _early = view; return; }
                ApplyView(view);
                return;
            }
            if (p.Kind != PacketKind.StorageResult || _pending == null || p.Sequence != _pending.Tx) return;
            var op = _pending; _pending = null; AppliedTx = op.Tx;
            byte status = p.Action;
            switch (op.Kind)
            {
                case LanProtocol.StorageOpen:
                    bool current = _wanted && _handle == 0 && !HasQueuedOpen();
                    if (status == LanStorage.Ok && p.Revision > 0)
                    {
                        if (current)
                        {
                            _handle = p.Revision;
                            if (_early != null && _early.Handle == _handle) ApplyView(_early);
                        }
                        else _queue.Enqueue(new Op { Kind = LanProtocol.StorageClose, Handle = p.Revision });
                    }
                    else if (current) { _wanted = false; Message = LanStorage.Describe(status == LanStorage.Ok ? LanStorage.Invalid : status); }
                    break;
                case LanProtocol.StorageTake:
                    if (status != LanStorage.Ok) { Message = LanStorage.Describe(status); DropTakes(); break; }
                    object[] item;
                    try { item = LanStorage.DecodeItem(p.Chunk); }
                    catch (InvalidDataException ex) { _log("Storage item from host rejected: " + ex.Message); Message = "Хост прислал повреждённый предмет"; DropTakes(); break; }
                    try { _world.Give(item); Message = ""; }
                    catch (Exception ex) { _log("Storage item not added: " + ex.GetType().Name + ": " + ex.Message); Message = "Предмет не удалось положить в рюкзак"; }
                    _changed();
                    break;
                case LanProtocol.StoragePickup:
                    if (status != LanStorage.Ok) { Message = LanStorage.Describe(status); break; }
                    object[] picked;
                    try { picked = LanStorage.DecodeItem(p.Chunk); }
                    catch (InvalidDataException ex) { _log("Picked item from host rejected: " + ex.Message); Message = "Хост прислал повреждённый предмет"; break; }
                    try { _world.Give(picked); PickedUp?.Invoke(op.HostId, (int)picked[1]); Message = ""; }
                    catch (Exception ex) { _log("Picked item not added: " + ex.GetType().Name + ": " + ex.Message); Message = "Предмет не удалось положить в рюкзак"; }
                    _changed();
                    break;
                case LanProtocol.StorageDrop:
                    if (status != LanStorage.Ok) { ReturnEscrow(op.Item); Message = LanStorage.Describe(status); break; }
                    Message = ""; _changed();
                    break;
                case LanProtocol.StorageCraft:
                    _log(status == LanStorage.Ok ? "Craft queued at the host's workbench" : "Craft refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    if (status != LanStorage.Ok) { ReturnEscrow(op); Message = LanStorage.Describe(status); break; }
                    Message = ""; _changed(); // the queue shows in the game's craft window
                    break;
                case LanProtocol.StorageCraftCancel:
                    _log(status == LanStorage.Ok ? "Craft cancelled at the host's workbench" : "Craft cancel refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    Message = status == LanStorage.Ok ? "" : LanStorage.Describe(status);
                    break;
                case LanProtocol.StorageWork:
                    if (status != LanStorage.Ok) { if (op.Item != null) ReturnEscrow(op.Item); if (status != LanStorage.Busy) Message = LanStorage.Describe(status); break; }
                    if (p.Chunk != null && p.Chunk.Length > 0)
                    {
                        object[] made;
                        try { made = LanStorage.DecodeItem(p.Chunk); }
                        catch (InvalidDataException ex) { _log("Work result from host rejected: " + ex.Message); break; }
                        try { _world.Give(made); }
                        catch (Exception ex) { _log("Work result not added: " + ex.GetType().Name + ": " + ex.Message); }
                    }
                    try { WorkDone?.Invoke(op.Work, p.Revision); }
                    catch (Exception ex) { _log("Work result not shown: " + ex.Message); }
                    _changed();
                    break;
                case LanProtocol.StorageDeath:
                    try { DeathDone?.Invoke(status, p.Amount); }
                    catch (Exception ex) { _log("Death result not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    break;
                case LanProtocol.StoragePlace:
                    Message = status == LanStorage.Ok ? "Чертёж поставлен у хоста" : LanStorage.Describe(status);
                    _changed();
                    break;
                case LanProtocol.StorageThrow:
                    if (status != LanStorage.Ok)
                    {
                        ReturnEscrow(op);
                        _log("Throw refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    }
                    try { op.Answer?.Invoke(status, p.Amount, p.Chunk); }
                    catch (Exception ex) { _log("Throw answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    _changed();
                    break;
                case LanProtocol.StorageFurniture:
                    if (status != LanStorage.Ok)
                    {
                        ReturnEscrow(op);
                        _log("Furniture placement refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    }
                    try { op.Answer?.Invoke(status, p.Amount, p.Chunk); }
                    catch (Exception ex) { _log("Furniture answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    _changed();
                    break;
                case LanProtocol.StorageDismantle:
                    if (status == LanStorage.Ok && p.Chunk != null && p.Chunk.Length > 0)
                    {
                        object[] refund;
                        try { refund = LanStorage.DecodeRefund(p.Chunk); }
                        catch (InvalidDataException ex) { _log("Dismantled items from host rejected: " + ex.Message); refund = new object[0]; }
                        foreach (object[] back in refund)
                        {
                            try { _world.Give(back); }
                            catch (Exception ex) { _log("Dismantled item not added: " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                    else if (status != LanStorage.Ok && status != LanStorage.Busy) _log("Dismantling refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    try { op.Answer?.Invoke(status, p.Amount, p.Chunk); }
                    catch (Exception ex) { _log("Dismantling answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    _changed();
                    break;
                case LanProtocol.StorageDecor:
                    if (status != LanStorage.Ok && status != LanStorage.Busy) _log("Train decor " + op.Work + " refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    try { op.Answer?.Invoke(status, p.Amount, p.Chunk); }
                    catch (Exception ex) { _log("Train decor answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    break;
                case LanProtocol.StorageTakable:
                    if (status != LanStorage.Ok && status != LanStorage.Busy) _log("Takable request refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    try { op.Answer?.Invoke(status, p.Amount, p.Chunk); }
                    catch (Exception ex) { _log("Takable answer not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    break;
                case LanProtocol.StorageQuest:
                    int received = 0;
                    if (status == LanStorage.Ok && p.Chunk != null && p.Chunk.Length > 0)
                    {
                        object[] items;
                        try { items = LanStorage.DecodeQuestItems(p.Chunk); }
                        catch (InvalidDataException ex) { _log("Quest items from host rejected: " + ex.Message); items = new object[0]; }
                        foreach (object[] gift in items)
                        {
                            try { _world.Give(gift); received++; }
                            catch (Exception ex) { _log("Quest item not added: " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                    if (status != LanStorage.Ok) _log("Quest " + op.Work + " refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")");
                    try { QuestDone?.Invoke(op.Work, op.Quest, status, received); }
                    catch (Exception ex) { _log("Quest result not applied: " + ex.GetType().Name + ": " + ex.Message); }
                    _changed();
                    break;
                case LanProtocol.StorageControl:
                    if (status != LanStorage.Ok || op.Work != LanStorage.ControlLever)
                        _log("Train control " + op.Work + (status == LanStorage.Ok ? " done by the host" : " refused by the host: status " + status + " (" + LanStorage.Describe(status) + ")"));
                    if (status != LanStorage.Ok)
                    {
                        if (op.Item != null) ReturnEscrow(op.Item);
                        Message = op.Work == LanStorage.ControlDoor ? LanStorage.DescribeDoor(status) : LanStorage.Describe(status);
                        break;
                    }
                    Message = op.Work == LanStorage.ControlRepair ? "Починено" : "";
                    if (op.Work == LanStorage.ControlDoor) DoorDone?.Invoke(op.Payload);
                    if (op.Item != null) _changed();
                    break;
                case LanProtocol.StorageBuild:
                    if (status != LanStorage.Ok) { if (status != LanStorage.Busy) Message = LanStorage.Describe(status); break; }
                    if (p.Amount == 1 && op.Needs != null)
                    {
                        try { _world.Spend(op.Needs); }
                        catch (Exception ex) { _log("Construction cost not spent: " + ex.GetType().Name + ": " + ex.Message); }
                        Message = "Построено";
                    }
                    _changed();
                    break;
                case LanProtocol.StoragePut:
                    int stored = status == LanStorage.Ok ? Math.Max(0, Math.Min(p.Amount, (int)op.Item[1])) : 0;
                    if (stored < (int)op.Item[1]) ReturnEscrow(LanStorage.WithAmount(op.Item, (int)op.Item[1] - stored));
                    Message = status == LanStorage.Ok ? (stored < (int)op.Item[1] ? "Поместилось " + stored : "") : LanStorage.Describe(status);
                    if (stored > 0) _changed();
                    break;
            }
        }

        private void ApplyView(LanStorage.View view)
        {
            _early = null;
            if (view.Status != LanStorage.Ok)
            { _handle = 0; _wanted = false; Current = null; DropTakes(); Message = LanStorage.Describe(view.Status); return; }
            Current = view;
            if (Message == "Открытие…") Message = "";
        }

        private void ReturnEscrow(Op op)
        {
            if (op.Items != null) foreach (object[] item in op.Items) ReturnEscrow(item);
            else if (op.Item != null && op.Return != null)
            {
                try { op.Return(op.Item); }
                catch (Exception ex) { _log("Escrow not put back where it was (" + ex.GetType().Name + ": " + ex.Message + "); into the backpack"); ReturnEscrow(op.Item); }
            }
            else if (op.Item != null) ReturnEscrow(op.Item);
        }
        private void ReturnEscrow(object[] item)
        {
            try { _world.Give(item); }
            catch (Exception ex) { _log("Storage escrow return failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private int IndexOf(object[] item)
        {
            if (Current == null) return -1;
            byte[] fingerprint = LanStorage.Fingerprint(item);
            for (int i = 0; i < Current.Items.Length; i++)
                if (LanStorage.SameFingerprint(LanStorage.Fingerprint((object[])Current.Items[i]), fingerprint)) return i;
            return -1;
        }
        private void DropTakes() { Keep(op => op.Kind != LanProtocol.StorageTake); }
        private void Keep(Func<Op, bool> keep)
        {
            var all = new List<Op>(_queue); _queue.Clear();
            foreach (var op in all) if (keep(op)) _queue.Enqueue(op);
        }
        private bool HasQueuedOpen() { foreach (var op in _queue) if (op.Kind == LanProtocol.StorageOpen) return true; return false; }
    }
}
