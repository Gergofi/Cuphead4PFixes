using System;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Configurable player damage scaling.
    ///
    /// The game already funnels all player damage through one chokepoint:
    ///
    ///     public static float DamageMultiplier =&gt; 1f / (float)Count;
    ///
    /// Every consumer is a player weapon or super - AbstractProjectile.DamageMultiplier,
    /// PlayerSuperBeam, PlayerSuperGhost, PlayerSuperChaliceVerticalBeam, PlaneSuperBomb,
    /// PlaneSuperChalice, WeaponBoomerang and RetroArcadeWormRocketBrokenPiece. Nothing on the
    /// enemy-to-player path reads it, so overriding this one getter scales exactly player output and
    /// nothing else. That is far safer and more complete than intercepting individual DealDamage
    /// call sites, which would miss supers and the boomerang's maxDamage.
    ///
    /// Note PlayerManager.Count counts LIVING players, so the multiplier naturally rises again as
    /// players go down - the same behaviour vanilla has.
    /// </summary>
    internal static class DamageScaling
    {
        private static float _lastLogged = -1f;
        private static int _lastCount = -1;

        internal static bool DynamicMode
        {
            get
            {
                try { return Plugin.ScaleByAlivePlayers.Value; }
                catch (Exception) { return true; }
            }
        }

        internal static string ModeName
        {
            get { return DynamicMode ? "Dynamic Alive" : "Static Connected"; }
        }

        /// <summary>
        /// The player count the multiplier is chosen from, clamped to the 1-4 range the config
        /// covers.
        ///
        /// Dynamic mode counts only players present AND alive. Static mode counts everyone present
        /// in the level regardless of death.
        ///
        /// Two things worth knowing:
        ///
        ///   * PlayerStatsManager has no `isDead` member. The correct test is
        ///     AbstractPlayerController.IsDead, which is `_isReviving ? false : stats.Health &lt;= 0` -
        ///     exactly the wanted behaviour, since a downed player in ghost form is not counted and
        ///     the instant a parry starts the revive they count again.
        ///   * `GetAllPlayers().Count` cannot be used for the static count. PlayerManager.Awake
        ///     seeds the dictionary with all four keys set to null, so that Count is ALWAYS 4 -
        ///     using it would pin even a solo game to the 4P multiplier. Non-null entries are
        ///     counted instead.
        /// </summary>
        internal static int ScalingPlayerCount()
        {
            var count = 0;
            var dynamicMode = DynamicMode;

            try
            {
                foreach (var player in PlayerManager.GetAllPlayers())
                {
                    if (player == null) continue;          // slot never joined
                    if (player.stats == null) continue;    // mid-teardown
                    if (dynamicMode && player.IsDead) continue;
                    count++;
                }
            }
            catch (Exception)
            {
                return 1;
            }

            if (count < 1) return 1;
            if (count > 4) return 4;
            return count;
        }

        internal static float MultiplierFor(int count)
        {
            float configured;
            switch (count)
            {
                case 1: configured = Plugin.DamageMultiplier1P.Value; break;
                case 2: configured = Plugin.DamageMultiplier2P.Value; break;
                case 3: configured = Plugin.DamageMultiplier3P.Value; break;
                default: configured = Plugin.DamageMultiplier4P.Value; break;
            }

            // "Unset or invalid" - a non-positive or non-finite entry - falls back to 1/N when
            // dynamic defaulting is on, otherwise to vanilla's own 1/N anyway.
            var usable = configured > 0f && !float.IsNaN(configured) && !float.IsInfinity(configured);
            if (usable) return configured;

            // Unset/invalid. With dynamic defaulting on we compute 1/N; with it off we still fall
            // back to 1/N, because that is vanilla's own formula and the safest thing to apply.
            return 1f / count;
        }

        internal static float Current()
        {
            var count = ScalingPlayerCount();
            var multiplier = MultiplierFor(count);
            NoteChange(count, multiplier);
            return multiplier;
        }

        private static void NoteChange(int count, float multiplier)
        {
            if (!Plugin.LogDamageScaling.Value) return;
            if (count == _lastCount && Mathf.Approximately(multiplier, _lastLogged)) return;

            _lastCount = count;
            _lastLogged = multiplier;

            Plugin.Log.LogInfo("[4P-Fix] Damage scaling active: Mode=" + ModeName +
                               ", Players=" + count +
                               ", Multiplier=" + multiplier.ToString("P0"));
        }

        /// <summary>Forces the next evaluation to log, so each level entry reports afresh.</summary>
        internal static void ResetLogState()
        {
            _lastCount = -1;
            _lastLogged = -1f;
        }

        internal static void ReportForLevel()
        {
            if (!Plugin.LogDamageScaling.Value) return;

            ResetLogState();
            var count = ScalingPlayerCount();
            var multiplier = MultiplierFor(count);
            _lastCount = count;
            _lastLogged = multiplier;

            Plugin.Log.LogInfo("[4P-Fix] Damage scaling active: Mode=" + ModeName +
                               ", Multiplier=" + multiplier.ToString("P0") +
                               " (players counted: " + count + ")");
        }

        /// <summary>
        /// Re-evaluates and logs immediately on a death or revive, rather than waiting for the next
        /// projectile to read the multiplier. The stats manager updates health before these fire.
        /// </summary>
        internal static void ReportChange(string reason)
        {
            if (!Plugin.LogDamageScaling.Value) return;

            var count = ScalingPlayerCount();
            var multiplier = MultiplierFor(count);
            if (count == _lastCount && Mathf.Approximately(multiplier, _lastLogged)) return;

            _lastCount = count;
            _lastLogged = multiplier;

            Plugin.Log.LogInfo("[4P-Fix] Alive player count changed to " + count +
                               ". Damage multiplier updated to " + multiplier.ToString("P0") +
                               ". (" + reason + ", Mode=" + ModeName + ")");
        }
    }

    /// <summary>Replaces vanilla's hardcoded 1/Count with the configured curve.</summary>
    [HarmonyPatch(typeof(PlayerManager), "get_DamageMultiplier")]
    internal static class PlayerManagerDamageMultiplierPatch
    {
        private static bool Prefix(ref float __result)
        {
            if (!Plugin.EnableDamageScaling.Value) return true;

            try
            {
                __result = DamageScaling.Current();
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Damage scaling failed, using vanilla: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Immediate re-evaluation the moment a player goes down or is parried back, so the multiplier
    /// change is logged at the event rather than on the next shot.
    /// </summary>
    [HarmonyPatch(typeof(Level), "OnPlayerDeath")]
    internal static class LevelOnPlayerDeathPatch
    {
        private static void Postfix()
        {
            try { DamageScaling.ReportChange("player died"); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Level), "OnPlayerRevive")]
    internal static class LevelOnPlayerRevivePatch
    {
        private static void Postfix()
        {
            try { DamageScaling.ReportChange("player revived"); }
            catch (Exception) { }
        }
    }

    /// <summary>Reports the scaling in force as each level begins.</summary>
    [HarmonyPatch(typeof(Level), "Start")]
    internal static class LevelStartDamageReportPatch
    {
        private static void Postfix(Level __instance)
        {
            if (__instance == null) return;
            try
            {
                DamageScaling.ReportForLevel();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Damage scaling report failed: " + e.Message);
            }
        }
    }
}
