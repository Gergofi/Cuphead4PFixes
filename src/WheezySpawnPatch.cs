using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Issue 2 - Mr. Wheezy (DicePalaceCigar) spawn collisions.
    ///
    /// Level.CreatePlayers derives the extra players' spawns purely arithmetically:
    ///     gap     = |spawns.playerOne.x - spawns.playerTwo.x|
    ///     P3      = spawns.playerOne + (gap, 0)
    ///     P4      = spawns.playerOne + (gap * 2, 0)
    /// Nothing consults the level layout, so in the cigar fight those offsets land P3/P4 straight
    /// on the flame/cigar spawn transforms and they eat an unavoidable hit on frame one.
    ///
    /// We re-place them after creation: mirrored to the *left* of the authored pair (away from the
    /// cigar, which enters from the right), then pushed further until every detected hazard spawn
    /// point is at least HazardClearance away.
    /// </summary>
    internal static class SafeSpawn
    {
        private static FieldInfo _playersField;

        private static AbstractPlayerController[] GetPlayers(Level level)
        {
            if (_playersField == null)
            {
                _playersField = AccessTools.Field(typeof(Level), "players");
            }
            return _playersField?.GetValue(level) as AbstractPlayerController[];
        }

        internal static bool ShouldApply(Level level)
        {
            if (level == null) return false;
            if (!Plugin.FixWheezySpawns.Value) return false;

            if (Plugin.WheezyFixAllDicePalace.Value && Level.IsDicePalace) return true;

            try
            {
                return level.CurrentLevel == Levels.DicePalaceCigar;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Collects world positions that an extra player must not spawn on: every Transform /
        /// GameObject the cigar boss holds as a serialized spawn point, plus the boss itself.
        /// Read reflectively so a renamed field cannot break the fix.
        /// </summary>
        private static List<Vector2> CollectHazards()
        {
            var hazards = new List<Vector2>();
            try
            {
                var cigars = UnityEngine.Object.FindObjectsOfType<DicePalaceCigarLevelCigar>();
                if (cigars == null) return hazards;

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (var cigar in cigars)
                {
                    if (cigar == null) continue;
                    hazards.Add(cigar.transform.position);

                    foreach (var f in cigar.GetType().GetFields(flags))
                    {
                        object value;
                        try { value = f.GetValue(cigar); }
                        catch (Exception) { continue; }
                        if (value == null) continue;

                        var t = value as Transform;
                        if (t != null) { hazards.Add(t.position); continue; }

                        var go = value as GameObject;
                        if (go != null) hazards.Add(go.transform.position);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Hazard scan failed: " + e.Message);
            }
            return hazards;
        }

        private static Vector2 PushClear(Vector2 desired, List<Vector2> hazards, float clearance)
        {
            if (hazards.Count == 0) return desired;

            var result = desired;
            // Step away to the left; 24 steps at one clearance each is far more room than any
            // Dice Palace arena has, so this always terminates well before running out.
            for (var step = 0; step < 24; step++)
            {
                var blocked = false;
                foreach (var h in hazards)
                {
                    if (Vector2.Distance(result, h) < clearance)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (!blocked) return result;
                result.x -= clearance * 0.5f;
            }
            return result;
        }

        internal static void Reposition(Level level)
        {
            try
            {
                var players = GetPlayers(level);
                if (players == null || players.Length < 4) return;

                var p3 = players[2];
                var p4 = players[3];
                if (p3 == null && p4 == null) return;

                var p1 = players[0];
                if (p1 == null) return;

                var basePos = (Vector2)p1.transform.position;
                var leftMost = basePos.x;
                if (players[1] != null)
                {
                    leftMost = Mathf.Min(leftMost, players[1].transform.position.x);
                }

                var spacing = Mathf.Max(40f, Plugin.ExtraSpawnSpacing.Value);
                var clearance = Mathf.Max(10f, Plugin.HazardClearance.Value);
                var hazards = CollectHazards();

                // Keep everyone inside the playfield.
                float minX = level.Left + 60f;
                float maxX = level.Right - 60f;

                if (p3 != null)
                {
                    var want = new Vector2(leftMost - spacing, basePos.y);
                    want = PushClear(want, hazards, clearance);
                    want.x = Mathf.Clamp(want.x, minX, maxX);
                    p3.transform.position = new Vector3(want.x, want.y, p3.transform.position.z);
                    Plugin.Log.LogInfo("Wheezy fix: P3 spawn -> " + want);
                }

                if (p4 != null)
                {
                    var want = new Vector2(leftMost - spacing * 2f, basePos.y);
                    want = PushClear(want, hazards, clearance);
                    want.x = Mathf.Clamp(want.x, minX, maxX);
                    p4.transform.position = new Vector3(want.x, want.y, p4.transform.position.z);
                    Plugin.Log.LogInfo("Wheezy fix: P4 spawn -> " + want);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wheezy spawn reposition failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Level), "CreatePlayers")]
    internal static class LevelCreatePlayersPatch
    {
        private static void Postfix(Level __instance)
        {
            if (__instance == null) return;
            if (!SafeSpawn.ShouldApply(__instance)) return;
            SafeSpawn.Reposition(__instance);
        }
    }

    [HarmonyPatch(typeof(Level), "CreatePlayerThreeOnJoin")]
    internal static class LevelCreatePlayerThreePatch
    {
        private static void Postfix(Level __instance)
        {
            if (__instance == null) return;
            if (!SafeSpawn.ShouldApply(__instance)) return;
            SafeSpawn.Reposition(__instance);
        }
    }

    [HarmonyPatch(typeof(Level), "CreatePlayerFourOnJoin")]
    internal static class LevelCreatePlayerFourPatch
    {
        private static void Postfix(Level __instance)
        {
            if (__instance == null) return;
            if (!SafeSpawn.ShouldApply(__instance)) return;
            SafeSpawn.Reposition(__instance);
        }
    }
}
