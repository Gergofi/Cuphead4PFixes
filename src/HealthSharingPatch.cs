using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace Cuphead4PFixes
{
    internal static class HealthSharing
    {
        // A missing, dead or inactive player is reported as zero available HP.
        internal static int SelectDonor(int recipient, Func<int, int> availableHealth)
        {
            if (recipient < 0 || recipient >= 4) return -1;
            // Keep the original two-player pairing when that partner can donate.
            var partner = recipient == 0 ? 1 : recipient == 1 ? 0 : -1;
            if (partner >= 0 && availableHealth(partner) > 1) return partner;

            var donor = -1;
            var mostHealth = 1;
            for (var id = 0; id < 4; id++)
            {
                if (id == recipient) continue;
                var health = availableHealth(id);
                if (health <= mostHealth) continue;
                donor = id;
                mostHealth = health;
            }
            return donor;
        }

        internal static bool HasMultiplayer()
        {
            return PlayerManager.Multiplayer || PlayerManager.Multiplayer2 || PlayerManager.Multiplayer3;
        }

        internal static AbstractPlayerController FindDonor(PlayerId originalPartner, PlayerId recipient)
        {
            // The original partner remains on the IL stack, but the recipient's
            // actual ID is essential: P2/P3/P4 all resolved to P1 in the old code.
            var player = Extras.GetPlayer(recipient);
            if (player == null || !player.IsDead) return null;
            var donor = SelectDonor((int)recipient, AvailableHealth);
            return donor < 0 ? null : Extras.GetLivePlayer((PlayerId)donor);
        }

        private static int AvailableHealth(int id)
        {
            var player = Extras.GetLivePlayer((PlayerId)id);
            return player == null || !player.stats.PartnerCanSteal ? 0 : player.stats.Health;
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "ReviveOutOfFrame")]
    internal static class HealthSharingRevivePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!Plugin.FixHealthSharing.Value) return codes;
            if (PatchRevive(codes))
                Plugin.Log.LogMessage("[4P-Revival] PATCH APPLIED: all four players can share HP; unsuccessful attempts remain retryable.");
            else
                Plugin.Log.LogError("[4P-Revival] PATCH NOT APPLIED: unexpected ReviveOutOfFrame layout.");
            return codes;
        }

        internal static bool PatchRevive(List<CodeInstruction> codes)
        {
            var multiplayer = AccessTools.Field(typeof(PlayerManager), "Multiplayer");
            var getPlayer = ((Func<PlayerId, AbstractPlayerController>)PlayerManager.GetPlayer).Method;
            var exiting = AccessTools.Field(typeof(PlayerDeathEffect), "exiting");
            var playerId = AccessTools.Field(typeof(PlayerDeathEffect), "playerId");
            var steal = AccessTools.Method(typeof(PlayerStatsManager), "OnPartnerStealHealth");
            var multiplayerIndex = -1;
            var getPlayerIndex = -1;
            var exitingIndex = -1;
            var stealIndex = -1;
            var multiplayerCount = 0;
            var getPlayerCount = 0;
            var exitingCount = 0;
            var stealCount = 0;
            for (var i = 0; i < codes.Count; i++)
            {
                if (codes[i].LoadsField(multiplayer)) { multiplayerIndex = i; multiplayerCount++; }
                if (codes[i].Calls(getPlayer)) { getPlayerIndex = i; getPlayerCount++; }
                if (codes[i].opcode == OpCodes.Stfld && Equals(codes[i].operand, exiting))
                { exitingIndex = i; exitingCount++; }
                if (codes[i].Calls(steal)) { stealIndex = i; stealCount++; }
            }
            // Validate everything before mutating anything. Never apply half a fix.
            if (multiplayerCount != 1 || getPlayerCount != 1 || exitingCount != 1 || stealCount != 1 ||
                !(multiplayerIndex < exitingIndex && exitingIndex < getPlayerIndex && getPlayerIndex < stealIndex) ||
                exitingIndex < 2 || codes[exitingIndex - 2].opcode != OpCodes.Ldarg_0 ||
                !codes[exitingIndex - 1].LoadsConstant(1) ||
                codes[exitingIndex - 1].labels.Count != 0 || codes[exitingIndex].labels.Count != 0 ||
                codes[exitingIndex - 2].blocks.Count != 0 || codes[exitingIndex - 1].blocks.Count != 0 ||
                codes[exitingIndex].blocks.Count != 0 || codes[getPlayerIndex].blocks.Count != 0 ||
                codes[stealIndex].blocks.Count != 0)
                return false;

            codes[multiplayerIndex].opcode = OpCodes.Call;
            codes[multiplayerIndex].operand = AccessTools.Method(typeof(HealthSharing), "HasMultiplayer");
            // A failed attempt must not latch the ghost into its exiting state.
            for (var i = exitingIndex - 2; i <= exitingIndex; i++)
            {
                codes[i].opcode = OpCodes.Nop;
                codes[i].operand = null;
            }

            // Move the latch after all the original eligibility guards, directly
            // before deduction. Keep the stats object already on the stack.
            var latch = new CodeInstruction(OpCodes.Ldarg_0);
            latch.labels.AddRange(codes[stealIndex].labels);
            codes[stealIndex].labels.Clear();
            codes.InsertRange(stealIndex, new[] { latch,
                new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Stfld, exiting) });

            // Preserve the rest of the method, including Tower of Power's ban,
            // health-change events, pre-revive/animation events and positioning.
            var recipient = new CodeInstruction(OpCodes.Ldarg_0);
            recipient.labels.AddRange(codes[getPlayerIndex].labels);
            codes[getPlayerIndex].labels.Clear();
            codes[getPlayerIndex].opcode = OpCodes.Call;
            codes[getPlayerIndex].operand = AccessTools.Method(typeof(HealthSharing), "FindDonor");
            codes.InsertRange(getPlayerIndex, new[] { recipient, new CodeInstruction(OpCodes.Ldfld, playerId) });
            return true;
        }
    }
}
