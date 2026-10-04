using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    internal static class SaltbakerTransition
    {
        internal static Vector3 PositionForPlayer(int player, int positionCount, Func<int, Vector3> readPosition)
        {
            // Preserve authored positions, including a future four-player prefab.
            if (player >= 0 && player < positionCount) return readPosition(player);
            if ((player == (int)PlayerId.PlayerThree || player == (int)PlayerId.PlayerFour) && positionCount >= 2)
            {
                // Stay within the existing landing area, with separate positions
                // for P3/P4 between P1 and P2. Read live transforms at each event.
                return Vector3.Lerp(readPosition(0), readPosition(1), (player - 1) / 3f);
            }
            throw new ArgumentOutOfRangeException("player", "Missing Saltbaker transition position.");
        }

        internal static Vector3 GetDefrostPosition(Transform[] positions, int player)
        {
            if (!Plugin.FixSaltbakerTransitions.Value) return positions[player].position;
            return PositionForPlayer(player, positions.Length, index => positions[index].position);
        }
    }

    [HarmonyPatch]
    internal static class SaltbakerTransitionPositionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SaltbakerLevelSaltbaker), "AniEvent_HandsClosed");
            yield return AccessTools.Method(typeof(SaltbakerLevelSaltbaker), "AniEvent_HandsOpen");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (PatchPositionRead(codes))
                Plugin.Log.LogMessage("[4P-Saltbaker] PATCH APPLIED: four-player transition positions in " +
                    __originalMethod.Name + ".");
            else
                Plugin.Log.LogError("[4P-Saltbaker] PATCH NOT APPLIED: expected one transition-position read in " +
                    __originalMethod.Name + ".");
            return codes;
        }

        internal static bool PatchPositionRead(List<CodeInstruction> codes)
        {
            var positions = AccessTools.Field(typeof(SaltbakerLevelSaltbaker), "playerDefrostPositions");
            var getId = AccessTools.PropertyGetter(typeof(AbstractPlayerController), "id");
            var getPosition = AccessTools.PropertyGetter(typeof(Transform), "position");
            var matches = new List<int>();
            for (var i = 0; i + 4 < codes.Count; i++)
            {
                if (codes[i].LoadsField(positions) && codes[i + 1].IsLdloc() && codes[i + 2].Calls(getId) &&
                    codes[i + 3].opcode == OpCodes.Ldelem_Ref && codes[i + 4].Calls(getPosition) &&
                    codes[i + 4].labels.Count == 0 && codes[i + 4].blocks.Count == 0)
                    matches.Add(i + 3);
            }
            if (matches.Count != 1) return false;
            var index = matches[0];
            // Stack before: Transform[], player ID. Stack after: Vector3. Keep
            // the game's hidden -10000 offset, animation timing, bounds changes,
            // input restoration, and final +10000 relocation exactly in place.
            codes[index].opcode = OpCodes.Call;
            codes[index].operand = AccessTools.Method(typeof(SaltbakerTransition), "GetDefrostPosition");
            codes[index + 1].opcode = OpCodes.Nop;
            codes[index + 1].operand = null;
            return true;
        }
    }
}
