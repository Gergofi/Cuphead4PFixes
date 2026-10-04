using System;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Issue 4, second half. Lighting transitions call ResetColor() to put a player back to plain
    /// white. For P3/P4 that is never what we want - re-assert their tint immediately so the swap
    /// survives leaving a lit zone instead of waiting a frame for FixRunner to notice.
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerAnimationController), nameof(LevelPlayerAnimationController.ResetColor))]
    internal static class LevelPlayerAnimationControllerResetColorPatch
    {
        private static void Postfix(LevelPlayerAnimationController __instance)
        {
            if (__instance == null) return;
            if (!Plugin.PersistExtraTints.Value) return;

            try
            {
                // The animation controller lives on the same GameObject as the player controller.
                var player = __instance.GetComponent<AbstractPlayerController>();
                if (player == null) return;

                var id = player.id;
                if (id != PlayerId.PlayerThree && id != PlayerId.PlayerFour) return;

                var stats = player.stats;
                if (stats != null && stats.isChalice) return;

                var color = (id == PlayerId.PlayerThree)
                    ? Extras.ParseColor(Plugin.Player3Color.Value, new Color(0.06f, 1f, 0f))
                    : Extras.ParseColor(Plugin.Player4Color.Value, new Color(1f, 1f, 0f));

                __instance.SetColor(color);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ResetColor postfix failed: " + e.Message);
            }
        }
    }
}
