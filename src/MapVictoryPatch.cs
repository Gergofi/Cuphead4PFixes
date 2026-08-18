using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Fixes the Chalice victory softlock at its source, inside Map.start_cr.
    ///
    /// Two defects, both verified against the IL of Map+&lt;start_cr&gt;d__0::MoveNext (that is the
    /// enumerator Map.start_cr() actually instantiates - &lt;start_cr&gt;d__31 is dead code):
    ///
    /// 1. The victory branch is chosen by
    ///        (!playerWasChalice[0] &amp;&amp; (!Multiplayer || !playerWasChalice[1])) || Multiplayer2
    ///    That trailing `|| Multiplayer2` only means "P3 has joined" and has nothing to do with
    ///    Chalice, so any party containing P3 always takes the non-Chalice path - including when
    ///    P1 or P2 genuinely is Chalice. In IL this is the FIRST of four
    ///    `ldsfld PlayerManager::Multiplayer2` in the method, at IL_02cb: it sits directly after the
    ///    `playerWasChalice[1]` test and branches to the Chalice path at IL_034e. The other three
    ///    are the `if (Multiplayer2)` guards that follow `players[1].OnWinComplete()`. We replace
    ///    only the first with `ldc.i4.0`, restoring the correct condition.
    ///
    /// 2. Every OnWinComplete call site dereferences an avatar without a null check - e.g.
    ///    `if (Multiplayer2 &amp;&amp; playerWasChalice[2]) altplayers[0].OnWinComplete();`. If that avatar
    ///    does not exist (P3 present in the level but with no map avatar yet) the coroutine throws
    ///    and dies mid-sequence, so every player after that point never receives OnWinComplete,
    ///    never gets the OnJumpAnimationComplete animation event, and never calls Enable(). They
    ///    stay in State.Stationary, cheering, with input dead - and because the coroutine died
    ///    before its tail, CurrentState never becomes Ready, so the pause menu stays blocked too.
    ///    All 12 call sites are rewritten to a null-safe helper.
    ///
    /// Completion is then confirmed event-driven off the game's own signal (CurrentState = Ready)
    /// rather than by polling.
    /// </summary>
    internal static class MapWinFix
    {
        private static bool _loggedNullAvatar;

        /// <summary>Null-safe stand-in for MapPlayerController.OnWinComplete() inside start_cr.</summary>
        public static void SafeWinComplete(MapPlayerController player)
        {
            if (player == null)
            {
                if (!_loggedNullAvatar)
                {
                    _loggedNullAvatar = true;
                    Plugin.Log.LogWarning("[4P-Fix] Map victory: an avatar was null at OnWinComplete - swallowed so start_cr cannot die mid-sequence.");
                }
                return;
            }

            try
            {
                player.OnWinComplete();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Map victory: OnWinComplete failed for " + player.id + ": " + e.Message);
            }
        }

        internal static void ResetForNewMap()
        {
            _loggedNullAvatar = false;
        }

        /// <summary>
        /// Map.start_cr sets CurrentState = Ready as its final act, so this is the sequence's own
        /// completion signal. Guarantee every slot is walking and the pause system is released.
        /// </summary>
        internal static void FinishVictory(Map map)
        {
            if (map == null) return;
            if (!Plugin.ForceVictoryCleanup.Value) return;

            var freed = 0;
            freed += FreePool(map.players);
            freed += FreePool(map.altplayers);

            var unpaused = false;
            if (PauseManager.state == PauseManager.State.Paused && NoMenuOpen())
            {
                PauseManager.Unpause();
                unpaused = true;
            }

            if (freed > 0 || unpaused)
            {
                Plugin.Log.LogInfo(string.Format(
                    "[4P-Fix] Map victory sequence completed. Restored input for {0} player(s){1}. Chalice player: {2}",
                    freed, unpaused ? " and unpaused" : string.Empty, MapWatchdog.DescribeChalice()));
            }
        }

        private static int FreePool(MapPlayerController[] pool)
        {
            if (pool == null) return 0;

            var freed = 0;
            foreach (var player in pool)
            {
                if (player == null) continue;
                if (player.state != MapPlayerController.State.Stationary) continue;

                SafeWinComplete(player);
                player.Enable();
                freed++;
            }
            return freed;
        }

        private static bool NoMenuOpen()
        {
            try
            {
                if (AbstractEquipUI.Current != null &&
                    AbstractEquipUI.Current.CurrentState != AbstractEquipUI.ActiveState.Inactive) return false;

                var pauseUIs = UnityEngine.Object.FindObjectsOfType<AbstractPauseGUI>();
                if (pauseUIs != null)
                {
                    foreach (var gui in pauseUIs)
                    {
                        if (gui != null && gui.state != AbstractPauseGUI.State.Unpaused) return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// THE Chalice victory softlock.
    ///
    /// MapPlayerAnimationController.Init, on the MapPlayerPose.Won path, runs:
    ///
    ///     if (playerWasChalice[(int)player.id]) {
    ///         if (player1IsMugman) ghostInPortal[(int)player.id].enabled = false;
    ///         else                 ghostInPortal[1 - (int)player.id].enabled = false;
    ///     }
    ///
    /// `ghostInPortal` is a [SerializeField] SpriteRenderer[] authored in the map-player prefab for
    /// two players. For Player Three (id 2) the first branch indexes [2] - past the end - and the
    /// second computes 1 - 2 = [-1]. Either throws IndexOutOfRangeException, and the guard means it
    /// only happens when that player WAS Chalice, which is exactly the reported repro.
    ///
    /// The throw propagates Init -> MapPlayerController.Create -> Map.CreatePlayers -> Map.Awake,
    /// so the Map never finishes waking: players/altplayers are left half-built, Map.Start never
    /// runs, start_cr never starts, and CurrentState never reaches Ready. Everything downstream
    /// follows from that one exception - P1/P2 frozen mid-cheer, CupheadMapCamera.get_playerCenter
    /// and LevelPauseGUI.Update throwing every frame (the blocked pause menu), and no victory
    /// sequence at all.
    ///
    /// Note resizing the array alone is not enough - it cannot fix the negative index - so the
    /// element reads are clamped as well.
    /// </summary>
    internal static class MapGhostFix
    {
        private const int Slots = 4;

        /// <summary>Clamped element read; replaces `ldelem.ref` on ghostInPortal.</summary>
        public static SpriteRenderer SafeGhost(SpriteRenderer[] array, int index)
        {
            if (array == null || array.Length == 0) return null;
            if (index < 0) index = 0;
            else if (index >= array.Length) index = array.Length - 1;
            return array[index];
        }

        /// <summary>
        /// Grows the prefab's 2-entry array so slots 2 and 3 exist, reusing the authored renderers
        /// so the extras still have a real ghost sprite rather than a null.
        /// </summary>
        public static void EnsureGhostSlots(MapPlayerAnimationController controller)
        {
            if (controller == null) return;

            try
            {
                if (_ghostField == null)
                {
                    _ghostField = AccessTools.Field(typeof(MapPlayerAnimationController), "ghostInPortal");
                    if (_ghostField == null) return;
                }

                var current = _ghostField.GetValue(controller) as SpriteRenderer[];
                if (current == null || current.Length == 0) return;
                if (current.Length >= Slots) return;

                var grown = new SpriteRenderer[Slots];
                for (var i = 0; i < Slots; i++)
                {
                    grown[i] = current[i % current.Length];
                }
                _ghostField.SetValue(controller, grown);

                if (!_loggedGrow)
                {
                    _loggedGrow = true;
                    Plugin.Log.LogInfo("[4P-Fix] ghostInPortal grown from " + current.Length +
                                       " to " + Slots + " entries so P3/P4 Chalice can complete the map win pose.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] EnsureGhostSlots failed: " + e.Message);
            }
        }

        private static FieldInfo _ghostField;
        private static bool _loggedGrow;
    }

    [HarmonyPatch(typeof(MapPlayerAnimationController), "Init")]
    internal static class MapPlayerAnimationControllerInitPatch
    {
        private static void Prefix(MapPlayerAnimationController __instance)
        {
            if (!Plugin.FixChaliceGhostArray.Value) return;
            MapGhostFix.EnsureGhostSlots(__instance);
        }

        /// <summary>Clamp every ghostInPortal element read - handles the negative `1 - id` index.</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var ghostField = AccessTools.Field(typeof(MapPlayerAnimationController), "ghostInPortal");
            var safeGhost = AccessTools.Method(typeof(MapGhostFix), "SafeGhost");

            var output = new List<CodeInstruction>();
            var pendingGhost = false;
            var clamped = 0;

            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldfld && ghostField != null && ghostField.Equals(ins.operand))
                {
                    pendingGhost = true;
                    output.Add(ins);
                    continue;
                }

                if (pendingGhost && ins.opcode == OpCodes.Ldelem_Ref && safeGhost != null)
                {
                    pendingGhost = false;
                    clamped++;
                    var replacement = new CodeInstruction(OpCodes.Call, safeGhost);
                    replacement.labels.AddRange(ins.labels);
                    replacement.blocks.AddRange(ins.blocks);
                    output.Add(replacement);
                    continue;
                }

                output.Add(ins);
            }

            Plugin.Log.LogMessage("[4P-Fix] MapPlayerAnimationController.Init patched: ghostInPortal reads clamped=" +
                                  clamped + " (expected 4).");
            return output;
        }
    }

    /// <summary>Rewrites the two defects inside the start_cr state machine.</summary>
    [HarmonyPatch]
    internal static class MapStartCrTranspilerPatch
    {
        private static MethodBase TargetMethod()
        {
            var enumerator = AccessTools.Inner(typeof(Map), "<start_cr>d__0");
            if (enumerator == null)
            {
                Plugin.Log.LogError("[4P-Fix] Map+<start_cr>d__0 not found - Chalice victory fix cannot be applied.");
                return null;
            }

            var moveNext = AccessTools.Method(enumerator, "MoveNext");
            if (moveNext == null)
            {
                Plugin.Log.LogError("[4P-Fix] <start_cr>d__0.MoveNext not found - Chalice victory fix cannot be applied.");
            }
            return moveNext;
        }

        private static bool Prepare(MethodBase original)
        {
            if (!Plugin.FixChaliceVictoryBranch.Value) return false;

            // Refuse rather than let PatchAll throw on a null TargetMethod.
            var enumerator = AccessTools.Inner(typeof(Map), "<start_cr>d__0");
            if (enumerator == null || AccessTools.Method(enumerator, "MoveNext") == null)
            {
                Plugin.Log.LogError("[4P-Fix] Map+<start_cr>d__0.MoveNext unavailable - Chalice victory fix skipped.");
                return false;
            }
            return true;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var multiplayer2 = AccessTools.Field(typeof(PlayerManager), "Multiplayer2");
            var onWinComplete = AccessTools.Method(typeof(MapPlayerController), "OnWinComplete");
            var safeWinComplete = AccessTools.Method(typeof(MapWinFix), "SafeWinComplete");

            var output = new List<CodeInstruction>();
            var conditionFixed = false;
            var wrapped = 0;

            foreach (var ins in instructions)
            {
                // Defect 1: neutralise ONLY the first Multiplayer2 load - the branch condition.
                if (!conditionFixed
                    && ins.opcode == OpCodes.Ldsfld
                    && multiplayer2 != null
                    && multiplayer2.Equals(ins.operand))
                {
                    conditionFixed = true;
                    var replacement = new CodeInstruction(OpCodes.Ldc_I4_0);
                    replacement.labels.AddRange(ins.labels);
                    replacement.blocks.AddRange(ins.blocks);
                    output.Add(replacement);
                    continue;
                }

                // Defect 2: make every OnWinComplete call site null-safe.
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
                    && onWinComplete != null
                    && safeWinComplete != null
                    && onWinComplete.Equals(ins.operand))
                {
                    wrapped++;
                    var replacement = new CodeInstruction(OpCodes.Call, safeWinComplete);
                    replacement.labels.AddRange(ins.labels);
                    replacement.blocks.AddRange(ins.blocks);
                    output.Add(replacement);
                    continue;
                }

                output.Add(ins);
            }

            if (!conditionFixed)
            {
                Plugin.Log.LogError("[4P-Fix] start_cr transpiler: the '|| Multiplayer2' branch load was NOT found - Chalice branch unchanged.");
            }

            Plugin.Log.LogMessage("[4P-Fix] start_cr patched: Chalice branch override removed=" + conditionFixed +
                                  ", OnWinComplete call sites made null-safe=" + wrapped + " (expected 12).");

            return output;
        }
    }

    /// <summary>
    /// CurrentState = Ready is the last thing start_cr does, so it is the sequence's own completion
    /// signal - no polling required.
    /// </summary>
    [HarmonyPatch(typeof(Map), "set_CurrentState")]
    internal static class MapCurrentStatePatch
    {
        private static void Postfix(Map __instance, Map.State value)
        {
            if (__instance == null) return;
            if (value != Map.State.Ready) return;

            try
            {
                MapWinFix.FinishVictory(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] FinishVictory failed: " + e.Message);
            }
        }
    }
}
