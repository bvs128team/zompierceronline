using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ZompiercerLAN
{
    // Restrict native selection/pickup to their original behaviour: synchronized
    // construction provides collision, but does not expose host inventory or editing.
    // Weapons and character ground probes keep the solid geometry for occlusion/support.
    internal static class TrainInteractionRays
    {
        private static bool _installed;
        private static readonly RaycastHit[] Hits = new RaycastHit[128];

        internal static void Install()
        {
            if (_installed) return;
            var harmony = new Harmony("local.zompiercer.lan.train-interaction");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(SelectionManager), "FixedUpdate"),
                    transpiler: new HarmonyMethod(typeof(TrainInteractionRays), nameof(ReplaceRays)));
                harmony.Patch(AccessTools.Method(typeof(ZombieFighterRays), "Update"),
                    transpiler: new HarmonyMethod(typeof(TrainInteractionRays), nameof(ReplaceRays)));
                _installed = true;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }

        private static IEnumerable<CodeInstruction> ReplaceRays(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var original = AccessTools.Method(typeof(Physics), nameof(Physics.Raycast),
                new[] { typeof(Ray), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });
            var replacement = AccessTools.Method(typeof(TrainInteractionRays), nameof(InteractionRay));
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, original))
                { instruction.operand = replacement; replaced++; }
                result.Add(instruction);
            }
            int expected = __originalMethod.DeclaringType == typeof(SelectionManager) ? 2 : 1;
            if (replaced != expected)
                throw new InvalidOperationException("Unsupported train interaction probes: " + __originalMethod.Name);
            return result;
        }

        private static bool InteractionRay(Ray ray, out RaycastHit hit, float distance, int mask)
        {
            bool found = Physics.Raycast(ray, out hit, distance, mask);
            if (!found || !TrainLayout.IsGroundSupport(hit.collider)) return found;
            // 1.4.16: placing a part on a surface (a picture on a wall) aims at the host's copies as the game aims
            // at its own walls (TrainConstructor: SelectionManager.raycastHit); the ray went through them, so there
            // was no preview at all, or a red one on whatever lay behind.
            if (TrainLayout.SurfacePlanning()) return found;
            int count = Physics.RaycastNonAlloc(ray, Hits, distance, mask, QueryTriggerInteraction.UseGlobal);
            hit = default(RaycastHit);
            // Saturation must not expose a native furniture interaction accidentally.
            if (count == Hits.Length) return false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
                if (!TrainLayout.IsGroundSupport(Hits[i].collider) && Hits[i].distance < nearest)
                { hit = Hits[i]; nearest = hit.distance; }
            return hit.collider != null;
        }
    }
}
