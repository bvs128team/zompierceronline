using System;
using System.Linq;

namespace ZompiercerLAN
{
    // 1.4.4: location objects of the host's world and the host's difficulty (packet schema 34).
    internal static partial class Program
    {
        private static void SceneChecks()
        {
            Packet decoded;
            Func<Packet, Packet> world = p => { p.GameVersion = "checks"; p.Session = 42; p.WorldEpoch = 3; p.Sequence = 9; p.Scene = new string('S', 96); return p; };
            var states = world(new Packet { Kind = PacketKind.SceneStates, SceneObjects = Enumerable.Range(0, LanProtocol.MaxSceneBatch).Select(i => new SceneFrame {
                Id = i % 2 == 0 ? i + 1 : -(i + 1), Kind = (byte)(LanProtocol.SceneBomb + i % LanProtocol.MaxSceneKind), Flags = (byte)(i % 2),
                Value = LanProtocol.SceneBomb + i % LanProtocol.MaxSceneKind == LanProtocol.SceneDrainer ? 12.5f : 0f }).ToArray() });
            var use = world(new Packet { Kind = PacketKind.SceneUse, Revision = int.MinValue, Action = LanProtocol.SceneButton });
            foreach (var p in new[] { states, use })
            {
                var bytes = LanProtocol.Encode(p);
                Require(bytes.Length <= 1200 && LanProtocol.TryDecode(bytes, out decoded) && decoded.Kind == p.Kind && decoded.Sequence == 9, "Valid scene packet rejected: " + p.Kind);
                for (int size = 0; size < bytes.Length; size++) { var t = new byte[size]; Array.Copy(bytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated scene packet accepted"); }
                var tail = new byte[bytes.Length + 1]; Array.Copy(bytes, tail, bytes.Length); Require(!LanProtocol.TryDecode(tail, out decoded), "Scene packet trailing bytes accepted");
            }
            LanProtocol.TryDecode(LanProtocol.Encode(states), out decoded);
            var drainer = decoded.SceneObjects.First(s => s.Kind == LanProtocol.SceneDrainer);
            Require(decoded.SceneObjects.Length == LanProtocol.MaxSceneBatch && decoded.SceneObjects[1].Id == -2 && decoded.SceneObjects[1].Flags == 1 && drainer.Value == 12.5f, "Scene object fields");
            LanProtocol.TryDecode(LanProtocol.Encode(use), out decoded);
            Require(decoded.Revision == int.MinValue && decoded.Action == LanProtocol.SceneButton, "Scene use fields");
            foreach (byte kind in new[] { LanProtocol.SceneTrap, LanProtocol.SceneCollapse, LanProtocol.SceneActivator, LanProtocol.SceneTrigger })
            {
                use.Action = kind;
                Require(LanProtocol.TryDecode(LanProtocol.Encode(use), out decoded) && decoded.Action == kind, "Guest scene use rejected: " + kind);
            }
            use.Action = LanProtocol.SceneButton;
            Func<Action<Packet>, Packet, bool> rejected = (change, source) =>
            {
                Packet copy; LanProtocol.TryDecode(LanProtocol.Encode(source), out copy);
                copy.GameVersion = "checks"; change(copy);
                try { return !LanProtocol.TryDecode(LanProtocol.Encode(copy), out decoded); }
                catch (System.IO.InvalidDataException) { return true; }
            };
            var bad = new[]
            {
                rejected(p => p.SceneObjects[0].Id = 0, states), rejected(p => p.SceneObjects[0].Kind = 0, states), rejected(p => p.SceneObjects[0].Kind = LanProtocol.MaxSceneKind + 1, states),
                rejected(p => p.SceneObjects[0].Flags = 2, states), rejected(p => p.SceneObjects[0].Value = 1f, states), rejected(p => p.SceneObjects[4].Value = -1f, states),
                rejected(p => p.SceneObjects[4].Value = float.NaN, states), rejected(p => p.SceneObjects[4].Value = 100000f, states),
                rejected(p => p.SceneObjects = new SceneFrame[LanProtocol.MaxSceneBatch + 1], states), rejected(p => p.SceneObjects = null, states),
                rejected(p => p.Sequence = 0, states), rejected(p => p.WorldEpoch = 0, states), rejected(p => p.Scene = "", states),
                rejected(p => p.Revision = 0, use), rejected(p => p.Action = LanProtocol.SceneBomb, use), rejected(p => p.Action = LanProtocol.SceneDrainer, use),
                rejected(p => p.Action = LanProtocol.SceneResource, use), rejected(p => p.Action = 0, use), rejected(p => p.Action = LanProtocol.MaxSceneKind + 1, use),
                rejected(p => p.Action = LanProtocol.SceneParsed, use),
                rejected(p => p.WorldEpoch = 0, use), rejected(p => p.Sequence = 0, use),
            };
            Require(bad.All(x => x), "Malformed scene packet accepted: #" + Array.IndexOf(bad, false));
            // The same on the wire: a drainer's time is the last float, a guest's use ends with what it is.
            var wire = LanProtocol.Encode(world(new Packet { Kind = PacketKind.SceneStates, SceneObjects = new[] { new SceneFrame { Id = 5, Kind = LanProtocol.SceneBomb, Flags = 1 } } }));
            Require(LanProtocol.TryDecode(wire, out decoded), "One-object scene batch rejected");
            foreach (var change in new[] { new[] { 6, LanProtocol.MaxSceneKind + 1 }, new[] { 5, 2 } }) // an unknown kind, Flags 2
            { var copy = (byte[])wire.Clone(); copy[copy.Length - change[0]] = (byte)change[1]; Require(!LanProtocol.TryDecode(copy, out decoded), "Invalid scene bytes accepted"); }
            var nonzero = (byte[])wire.Clone(); BitConverter.GetBytes(3f).CopyTo(nonzero, nonzero.Length - 4);
            Require(!LanProtocol.TryDecode(nonzero, out decoded), "A wall with a drained time accepted");
            var useWire = LanProtocol.Encode(use); useWire[useWire.Length - 1] = LanProtocol.SceneBomb;
            Require(!LanProtocol.TryDecode(useWire, out decoded), "A guest blowing a wall up accepted");
            // Directions and bursts.
            Require(LanNetworkPolicy.Allowed(true, PacketKind.SceneUse, true) && !LanNetworkPolicy.Allowed(false, PacketKind.SceneUse, true) && !LanNetworkPolicy.Allowed(true, PacketKind.SceneUse, false) &&
                !LanNetworkPolicy.Allowed(true, PacketKind.SceneStates, true) && LanNetworkPolicy.Allowed(false, PacketKind.SceneStates, true) && !LanNetworkPolicy.Allowed(false, PacketKind.SceneStates, false) &&
                LanProtocol.WorldBound(PacketKind.SceneStates) && LanProtocol.WorldBound(PacketKind.SceneUse), "Scene packet directions");
            var limits = new LanMessageLimits(); int batches = 0, uses = 0;
            for (int i = 0; i < 1000; i++) { if (limits.Take(PacketKind.SceneStates, 0)) batches++; if (limits.Take(PacketKind.SceneUse, 0)) uses++; }
            Require(batches == 10 && uses == 4, "Scene packet bursts not bounded");

            // The host's difficulty in WorldState.
            var state = new Packet { Kind = PacketKind.WorldState, GameVersion = "checks", Session = 42, WorldEpoch = 1, Sequence = 2, Scene = "Terrain1", Location = 2, RailsId = 5, TrainPosition = 10,
                Clock = new ClockSnapshot { Year = 1, Day = 3, Hour = 12, HoursPerRealSecond = .01f }, Difficulty = new DifficultySnapshot { ZombieDamage = 1.5f, Recoil = .25f, Wobble = 10f, ShowOnMap = true } };
            var stateBytes = LanProtocol.Encode(state);
            Require(LanProtocol.TryDecode(stateBytes, out decoded) && decoded.Difficulty != null && decoded.Difficulty.ZombieDamage == 1.5f && decoded.Difficulty.Wobble == 10f && decoded.Difficulty.ShowOnMap, "Difficulty roundtrip");
            for (int size = 0; size < stateBytes.Length; size++) { var t = new byte[size]; Array.Copy(stateBytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated world state accepted"); }
            state.Difficulty = null;
            Require(LanProtocol.TryDecode(LanProtocol.Encode(state), out decoded) && decoded.Difficulty == null, "World state without difficulty rejected");
            foreach (var d in new[] { new DifficultySnapshot { ZombieDamage = -1 }, new DifficultySnapshot { ZombieDamage = 10.5f }, new DifficultySnapshot { Recoil = float.NaN }, new DifficultySnapshot { Wobble = 11 } })
            {
                state.Difficulty = d;
                ExpectRejected(() => LanProtocol.Encode(state), "Invalid difficulty encoded");
            }
            state.Difficulty = new DifficultySnapshot { ZombieDamage = 1f, Recoil = 1f, Wobble = 1f, ShowOnMap = false };
            var flagBytes = LanProtocol.Encode(state); flagBytes[flagBytes.Length - 1] = 2; // ShowOnMap is 0 or 1
            Require(!LanProtocol.TryDecode(flagBytes, out decoded), "Map flag 2 accepted");
            var damageBytes = LanProtocol.Encode(state); BitConverter.GetBytes(50f).CopyTo(damageBytes, damageBytes.Length - 13);
            Require(!LanProtocol.TryDecode(damageBytes, out decoded), "Zombie damage x50 accepted");
            var hasBytes = LanProtocol.Encode(state); hasBytes[hasBytes.Length - 14] = 2;
            Require(!LanProtocol.TryDecode(hasBytes, out decoded), "Difficulty presence 2 accepted");
            // 1.4.5: Leave is the header only, either way once a session stands, a few at once.
            var leave = new Packet { Kind = PacketKind.Leave, GameVersion = "checks", Session = 42 };
            var leaveBytes = LanProtocol.Encode(leave);
            Require(LanProtocol.TryDecode(leaveBytes, out decoded) && decoded.Kind == PacketKind.Leave && decoded.Session == 42, "Leave rejected");
            for (int size = 0; size < leaveBytes.Length; size++) { var t = new byte[size]; Array.Copy(leaveBytes, t, size); Require(!LanProtocol.TryDecode(t, out decoded), "Truncated leave accepted"); }
            var leaveTail = new byte[leaveBytes.Length + 1]; Array.Copy(leaveBytes, leaveTail, leaveBytes.Length);
            Require(!LanProtocol.TryDecode(leaveTail, out decoded), "Leave with a payload accepted");
            var unknown = (byte[])leaveBytes.Clone(); unknown[5] = (byte)PacketKind.Leave + 1;
            Require(!LanProtocol.TryDecode(unknown, out decoded), "A packet kind beyond Leave accepted");
            Require(LanNetworkPolicy.Allowed(true, PacketKind.Leave, true) && LanNetworkPolicy.Allowed(false, PacketKind.Leave, true) &&
                !LanNetworkPolicy.Allowed(true, PacketKind.Leave, false) && !LanNetworkPolicy.Allowed(false, PacketKind.Leave, false) && !LanProtocol.WorldBound(PacketKind.Leave), "Leave directions");
            int leaves = 0; for (int i = 0; i < 1000; i++) if (limits.Take(PacketKind.Leave, 0)) leaves++;
            Require(leaves == 4, "Leave bursts not bounded");
            // A taken-apart object only comes from the host.
            var parsed = world(new Packet { Kind = PacketKind.SceneStates, SceneObjects = new[] { new SceneFrame { Id = -77, Kind = LanProtocol.SceneParsed, Flags = 1 }, new SceneFrame { Id = 5, Kind = LanProtocol.SceneTrigger, Flags = 1 } } });
            Require(LanProtocol.TryDecode(LanProtocol.Encode(parsed), out decoded) && decoded.SceneObjects[0].Kind == LanProtocol.SceneParsed && decoded.SceneObjects[1].Kind == LanProtocol.SceneTrigger, "Taken-apart object or trigger rejected");
            Console.WriteLine("PASS: location objects (scene batch and guest use shapes and bounds, byte tampering, a guest cannot blow a wall up or drain a swamp, directions, bursts) and the host's difficulty in WorldState");
        }
    }
}
