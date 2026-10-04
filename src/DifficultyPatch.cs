using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Cuphead4PFixes
{
    /// <summary>
    /// The modified Level.Awake tests (levelId - 1464969490) &lt;= 3 using a signed
    /// branch. Boss IDs below that range therefore take the Run &amp; Gun path and
    /// receive Normal, even though CurrentMode still contains Easy or Hard.
    /// The range test must be unsigned: only the four consecutive platforming
    /// IDs belong to it. The other two platforming checks remain unchanged.
    /// Fix this in Awake before PartialInit consumes mode for scoring/boss setup.
    /// A postfix that merely changes mode would be too late.
    /// </summary>
    [HarmonyPatch(typeof(Level), "Awake")]
    internal static class DifficultyLevelAwakePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!Plugin.FixDifficultySelection.Value) return codes;

            var matches = CorrectRangeCheck(codes, AccessTools.PropertySetter(typeof(Level), "mode"));
            if (matches == 1)
                Plugin.Log.LogMessage("[4P-Difficulty] PATCH APPLIED: corrected signed Run & Gun range check in Level.Awake.");
            else
                Plugin.Log.LogWarning("[4P-Difficulty] PATCH NOT APPLIED: expected one faulty range check; found " +
                    matches + ". Game may differ or already be fixed.");
            return codes;
        }

        // Match both the range and its destination so an unrelated comparison
        // cannot be patched. Ambiguous/unrecognized versions are left untouched.
        internal static int CorrectRangeCheck(List<CodeInstruction> codes, MethodInfo modeSetter)
        {
            if (modeSetter == null) return 0;
            var matches = new List<int>();
            for (var i = 0; i + 3 < codes.Count; i++)
            {
                if (!codes[i].LoadsConstant((long)Levels.Platforming_Level_1_1) ||
                    codes[i + 1].opcode != OpCodes.Sub || !codes[i + 2].LoadsConstant(3)) continue;

                var branch = codes[i + 3];
                if ((branch.opcode != OpCodes.Ble && branch.opcode != OpCodes.Ble_S) ||
                    !(branch.operand is Label)) continue;

                var destination = (Label)branch.operand;
                for (var j = 0; j + 2 < codes.Count; j++)
                {
                    if (codes[j].labels.Contains(destination) && codes[j].opcode == OpCodes.Ldarg_0 &&
                        codes[j + 1].LoadsConstant((long)Level.Mode.Normal) && codes[j + 2].Calls(modeSetter))
                    {
                        matches.Add(i + 3);
                        break;
                    }
                }
            }

            if (matches.Count == 1)
            {
                // Mutate only the opcode, preserving the target, labels and exception blocks.
                var branch = codes[matches[0]];
                branch.opcode = branch.opcode == OpCodes.Ble_S ? OpCodes.Ble_Un_S : OpCodes.Ble_Un;
            }
            return matches.Count;
        }

        private static void Postfix(Level __instance)
        {
            if (!Plugin.LogDifficulty.Value) return;
            Plugin.Log.LogMessage("[4P-Difficulty] Awake AFTER: level=" + __instance.CurrentLevel +
                " selected=" + Level.CurrentMode + " actual=" + __instance.mode +
                " joinedFlags(P2/P3/P4)=" + PlayerManager.Multiplayer + "/" +
                PlayerManager.Multiplayer2 + "/" + PlayerManager.Multiplayer3);
        }
    }
}
