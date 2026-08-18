using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Issue 5 - the DLC entrance softlock.
    ///
    /// Root cause: CupheadInput.AnyPlayerInput's constructor builds a *fixed* four-element array
    ///
    ///     players = new Player[4] { GetPlayerInput(One), GetPlayerInput(Two),
    ///                               GetPlayerInput(Three), GetPlayerInput(Four) };
    ///
    /// and every accessor then does an unguarded <c>player.GetButtonDown(...)</c> over it. When the
    /// Rewired players behind slots 2/3 do not exist, GetAnyButtonDown() throws. DLCGenericCutscene
    /// advances its screens from inside a coroutine:
    ///
    ///     while ((input.GetButtonDown(Pause) || !input.GetAnyButtonDown()) &amp;&amp; !fastForwardActive)
    ///
    /// An exception there kills main_cr outright, so the cutscene never advances and never calls
    /// Skip() - the reported infinite wait. Stripping the nulls fixes the softlock at its source
    /// and, being in AnyPlayerInput, repairs every other cutscene and prompt at the same time.
    /// </summary>
    [HarmonyPatch]
    internal static class AnyPlayerInputPatch
    {
        private static FieldInfo _playersField;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Constructor(typeof(CupheadInput.AnyPlayerInput), new[] { typeof(bool) });
        }

        private static void Postfix(object __instance)
        {
            if (__instance == null) return;
            if (!Plugin.FixAnyPlayerInput.Value) return;

            try
            {
                if (_playersField == null)
                {
                    _playersField = AccessTools.Field(typeof(CupheadInput.AnyPlayerInput), "players");
                }
                if (_playersField == null) return;

                var current = _playersField.GetValue(__instance) as Player[];
                if (current == null) return;

                var kept = new List<Player>(current.Length);
                foreach (var p in current)
                {
                    if (p != null) kept.Add(p);
                }

                if (kept.Count == current.Length) return;

                _playersField.SetValue(__instance, kept.ToArray());
                Plugin.Log.LogInfo(
                    "AnyPlayerInput: dropped " + (current.Length - kept.Count) +
                    " null Rewired player(s); " + kept.Count + " remain.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("AnyPlayerInput postfix failed: " + e);
            }
        }
    }

    /// <summary>
    /// The boatman hands the Chalice charm to P1/P2 only before loading the DLC intro cutscene.
    /// Keep the extra players' inventories in step, and park their overworld avatars so nothing
    /// walks the transition with a half-torn-down controller.
    /// </summary>
    [HarmonyPatch(typeof(MapNPCBoatman), "SelectWorld")]
    internal static class MapNPCBoatmanSelectWorldPatch
    {
        private const int DlcOption = 3;

        private static void Prefix(string metadata)
        {
            try
            {
                int option;
                if (!int.TryParse(metadata, out option)) return;
                if (option != DlcOption) return;

                if (Plugin.GiftChaliceToExtras.Value)
                {
                    foreach (var id in Extras.ExtraIds)
                    {
                        try { PlayerData.Data.Gift(id, Charm.charm_chalice); }
                        catch (Exception e) { Plugin.Log.LogWarning("Chalice gift to " + id + " failed: " + e.Message); }
                    }
                    Plugin.Log.LogInfo("DLC entry: gifted Chalice charm to P3/P4 for inventory parity.");
                }

                if (Plugin.ParkExtrasForDlc.Value)
                {
                    ParkExtraMapAvatars();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Boatman prefix failed: " + e);
            }
        }

        private static void ParkExtraMapAvatars()
        {
            var map = Map.Current;
            if (map == null) return;

            var extras = map.altplayers;
            if (extras == null) return;

            var parked = 0;
            foreach (var avatar in extras)
            {
                if (avatar == null) continue;
                try
                {
                    avatar.gameObject.SetActive(false);
                    parked++;
                }
                catch (Exception)
                {
                    // Already torn down - nothing to park.
                }
            }

            if (parked > 0)
            {
                Plugin.Log.LogInfo("DLC entry: parked " + parked + " extra overworld avatar(s) for the transition.");
            }
        }
    }

    /// <summary>
    /// Map.OnPlayerJoined anchors a joining P3 on <c>players[1]</c> and a joining P4 on
    /// <c>altplayers[0]</c>, with no null check. Join as P3 without a Player 2 present (or as P4
    /// before P3 has an avatar) and the overworld throws instead of spawning the avatar. Handle
    /// exactly those two broken cases and defer to the original everywhere else.
    /// </summary>
    [HarmonyPatch(typeof(Map), "OnPlayerJoined")]
    internal static class MapOnPlayerJoinedPatch
    {
        private static bool Prefix(Map __instance, PlayerId playerId)
        {
            if (__instance == null) return true;

            try
            {
                var players = __instance.players;
                var extras = __instance.altplayers;

                if (playerId == PlayerId.PlayerThree)
                {
                    var anchorMissing = players == null || players.Length < 2 || players[1] == null;
                    if (!anchorMissing) return true;

                    var anchor = (players != null && players.Length > 0) ? players[0] : null;
                    if (anchor == null) return false; // Nothing safe to anchor to; skip rather than throw.

                    SpawnExtra(__instance, PlayerId.PlayerThree, 0, anchor);
                    return false;
                }

                if (playerId == PlayerId.PlayerFour)
                {
                    var anchorMissing = extras == null || extras.Length < 1 || extras[0] == null;
                    if (!anchorMissing) return true;

                    MapPlayerController anchor = null;
                    if (players != null && players.Length > 1 && players[1] != null) anchor = players[1];
                    else if (players != null && players.Length > 0) anchor = players[0];
                    if (anchor == null) return false;

                    SpawnExtra(__instance, PlayerId.PlayerFour, 1, anchor);
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Map.OnPlayerJoined prefix failed, deferring to original: " + e);
                return true;
            }

            return true;
        }

        private static void SpawnExtra(Map map, PlayerId id, int slot, MapPlayerController anchor)
        {
            Plugin.Log.LogWarning(
                "Map.OnPlayerJoined: " + id + " had no valid anchor avatar; spawning next to " +
                anchor.name + " instead of throwing.");

            var extras = map.altplayers;
            if (extras == null || extras.Length <= slot) return;

            var position = anchor.transform.position + new Vector3(0.05f, 0.05f, 0f);
            var created = MapPlayerController.Create(id, new MapPlayerController.InitObject(position, MapPlayerPose.Joined));
            extras[slot] = created;

            try
            {
                created.animationController.spriteRenderer.sortingOrder =
                    anchor.animationController.spriteRenderer.sortingOrder;
            }
            catch (Exception)
            {
                // Sorting order is cosmetic; never let it break the join.
            }

            try
            {
                if (LevelNewPlayerGUI.Current != null) LevelNewPlayerGUI.Current.Init();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("LevelNewPlayerGUI.Init failed: " + e.Message);
            }

            try
            {
                var checkMusic = AccessTools.Method(typeof(Map), "CheckMusic", new[] { typeof(bool) });
                checkMusic?.Invoke(map, new object[] { true });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CheckMusic failed: " + e.Message);
            }
        }
    }
}
