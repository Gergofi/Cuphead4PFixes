using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Fixes boss target selection.
    ///
    /// There is no AbstractLevelEntity.GetTarget / GetNearestPlayer / GetRandomPlayer in this
    /// assembly. Bosses pick targets through three PlayerManager helpers - GetRandom(), GetNext()
    /// and GetFirst() - and GetRandom() alone has 226 call sites across 136 files.
    ///
    /// GetNext(), GetFirst() and DoesPlayerExist() are already four-player aware, and
    /// DoesPlayerExist already filters the dead (`return !players[(int)player].IsDead`).
    ///
    /// GetRandom() is broken:
    ///
    ///     do { playerId = EnumUtils.Random&lt;PlayerId&gt;(); } while (!DoesPlayerExist(playerId));
    ///
    /// EnumUtils.Random returns a uniformly random member of the WHOLE enum, and PlayerId here is
    /// { PlayerOne=0, PlayerTwo=1, Any=2147483646, None=int.MaxValue, PlayerThree=2, PlayerFour=3 }.
    /// Two of those six are Any/None, and DoesPlayerExist indexes
    /// Dictionary&lt;int, AbstractPlayerController&gt; players[(int)player] - keys 0..3 only - so it
    /// throws KeyNotFoundException about a third of the time per roll. Thrown inside a boss
    /// coroutine that exception kills the coroutine, which is why a boss stops re-targeting and
    /// keeps attacking a dead player's last position.
    ///
    /// Two patches: make DoesPlayerExist total (any id outside 0..3 is simply "does not exist"),
    /// and replace GetRandom with a uniform pick over the living players.
    /// </summary>
    internal static class BossTargeting
    {
        internal static readonly PlayerId[] Slots =
        {
            PlayerId.PlayerOne, PlayerId.PlayerTwo, PlayerId.PlayerThree, PlayerId.PlayerFour
        };

        private static PlayerId _lastTarget = PlayerId.None;

        internal static bool SlotExists(PlayerId id)
        {
            try
            {
                return PlayerManager.DoesPlayerExist(id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void NoteTarget(AbstractPlayerController target)
        {
            if (!Plugin.LogTargetSwitches.Value) return;
            if (target == null) return;

            PlayerId id;
            try { id = target.id; }
            catch (Exception) { return; }

            if (id == _lastTarget) return;
            _lastTarget = id;

            Plugin.Log.LogInfo("[4P-Fix] Boss target switched to " + id +
                               " (alive: " + DescribeAlive() + ")");
        }

        internal static string DescribeAlive()
        {
            var s = string.Empty;
            foreach (var id in Slots)
            {
                if (!SlotExists(id)) continue;
                if (s.Length > 0) s += ",";
                s += id.ToString();
            }
            return (s.Length > 0) ? s : "none";
        }
    }

    /// <summary>
    /// Redirects aim away from corpses.
    ///
    /// Most bosses never re-evaluate their target. DicePalaceBoozeLevelOlive is typical: it sets
    /// `nextPlayerTarget = PlayerId.PlayerOne` in Awake, never assigns it again, and each shot does
    /// `PlayerManager.GetPlayer(nextPlayerTarget).center`. There are 245 such hardcoded
    /// GetPlayer(PlayerOne/PlayerTwo) calls across 68 files, plus an unknown number of bosses
    /// caching an AbstractPlayerController in a private field at attack startup - far too many to
    /// patch individually, and their fields are not reachable by name.
    ///
    /// The one thing they all funnel through is AbstractPlayerController.center, the property they
    /// read to aim. It is `public virtual` with no overrides in this assembly, so a single postfix
    /// on its getter covers every boss and every cached reference: ask a dead player where it is,
    /// and you get the nearest living player instead.
    ///
    /// Scoped to levels only, and only ever for a player that is actually dead - a living player's
    /// center is returned untouched, so camera framing, coin pickup and player movement are
    /// unaffected. PlayerManager.Center/CameraCenter already exclude the dead via DoesPlayerExist.
    /// </summary>
    [HarmonyPatch]
    internal static class DeadPlayerAimRedirectPatch
    {
        /// <summary>
        /// The base getter is virtual and LevelPlayerController overrides it - which is precisely
        /// the controller used in boss fights. Patching only the base would never fire there, so
        /// collect the base getter plus every override in the assembly.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new List<MethodBase>();
            var baseType = typeof(AbstractPlayerController);

            var baseGetter = AccessTools.PropertyGetter(baseType, "center");
            if (baseGetter != null) targets.Add(baseGetter);

            try
            {
                const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                foreach (var t in baseType.Assembly.GetTypes())
                {
                    if (t == baseType || !baseType.IsAssignableFrom(t)) continue;
                    var over = t.GetMethod("get_center", declared, null, Type.EmptyTypes, null);
                    if (over != null) targets.Add(over);
                }
            }
            catch (Exception e)
            {
                // Fall back to the one override we know about.
                Plugin.Log.LogWarning("[4P-Fix] Could not enumerate center overrides (" + e.Message + "); using known types.");
                var known = AccessTools.PropertyGetter(typeof(LevelPlayerController), "center");
                if (known != null && !targets.Contains(known)) targets.Add(known);
            }

            foreach (var t in targets)
            {
                Plugin.Log.LogInfo("[4P-Fix] Aim redirect will patch: " + t.DeclaringType.Name + ".get_center");
            }
            return targets;
        }

        private static int _cachedFrame = -1;
        private static AbstractPlayerController _cachedTarget;
        // Per dead slot, so two corpses redirecting at once do not alternate past a single
        // last-seen pair and re-log every frame.
        private static readonly PlayerId[] _lastToFor =
        {
            PlayerId.None, PlayerId.None, PlayerId.None, PlayerId.None
        };

        private static void Postfix(AbstractPlayerController __instance, ref Vector3 __result)
        {
            if (!Plugin.RedirectDeadPlayerAim.Value) return;
            if (__instance == null) return;

            try
            {
                // Levels only. The overworld has no bosses and its own player handling.
                if (Level.Current == null) return;
                if (!__instance.IsDead)
                {
                    // Alive again - forget the last redirect so the next death logs once more.
                    var revived = (int)__instance.id;
                    if (revived >= 0 && revived <= 3) _lastToFor[revived] = PlayerId.None;
                    return;
                }

                var living = PickLiving(__instance);
                if (living == null) return;

                // Compute directly rather than reading living.center, so this postfix can never
                // re-enter itself.
                var pos = living.transform.position;
                try { pos += (Vector3)living.collider.offset; }
                catch (Exception) { /* collider not ready - raw position is close enough */ }

                __result = pos;
                Note(__instance, living);
            }
            catch (Exception)
            {
                // center is read many times per frame; never let a hiccup spam or throw.
            }
        }

        /// <summary>Nearest living player to the corpse, so aim stays locally sensible.</summary>
        private static AbstractPlayerController PickLiving(AbstractPlayerController deadPlayer)
        {
            if (_cachedFrame == Time.frameCount) return _cachedTarget;

            AbstractPlayerController best = null;
            var bestDistance = float.MaxValue;
            var from = deadPlayer.transform.position;

            foreach (var id in BossTargeting.Slots)
            {
                if (!BossTargeting.SlotExists(id)) continue; // excludes dead and missing

                var candidate = PlayerManager.GetPlayer(id);
                if (candidate == null) continue;

                var d = Vector3.SqrMagnitude(candidate.transform.position - from);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = candidate;
            }

            _cachedFrame = Time.frameCount;
            _cachedTarget = best;
            return best;
        }

        private static void Note(AbstractPlayerController deadPlayer, AbstractPlayerController living)
        {
            if (!Plugin.LogTargetSwitches.Value) return;

            PlayerId from, to;
            try { from = deadPlayer.id; to = living.id; }
            catch (Exception) { return; }

            var slot = (int)from;
            if (slot < 0 || slot > 3) return;
            if (_lastToFor[slot] == to) return;
            _lastToFor[slot] = to;

            Plugin.Log.LogInfo("[4P-Fix] Aim redirect: " + from + " is dead -> aiming at " + to +
                               " (alive: " + BossTargeting.DescribeAlive() + ")");
        }
    }

    /// <summary>
    /// Makes DoesPlayerExist total. PlayerId.Any and PlayerId.None are not dictionary keys, and
    /// GetRandom feeds them in constantly. Anything outside slots 0..3 simply does not exist.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.DoesPlayerExist))]
    internal static class DoesPlayerExistPatch
    {
        private static bool Prefix(PlayerId player, ref bool __result)
        {
            if (!Plugin.FixBossTargeting.Value) return true;

            var slot = (int)player;
            if (slot >= 0 && slot <= 3) return true; // real slot - run the original

            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Replaces GetRandom with a uniform pick over living players across all four slots.
    /// DoesPlayerExist already excludes the dead, so P3/P4 inherit aggro once P1/P2 fall.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetRandom))]
    internal static class GetRandomPatch
    {
        private static readonly List<AbstractPlayerController> Alive = new List<AbstractPlayerController>(4);

        private static bool Prefix(ref AbstractPlayerController __result)
        {
            if (!Plugin.FixBossTargeting.Value) return true;

            try
            {
                Alive.Clear();

                foreach (var id in BossTargeting.Slots)
                {
                    if (!BossTargeting.SlotExists(id)) continue;
                    var p = PlayerManager.GetPlayer(id);
                    if (p != null) Alive.Add(p);
                }

                if (Alive.Count == 0)
                {
                    // Everyone is down - hand back Player One like the original's degenerate path
                    // so callers that never null-check keep working.
                    __result = SafeGet(PlayerId.PlayerOne) ?? SafeGet(PlayerId.PlayerTwo);
                    return false;
                }

                __result = Alive[UnityEngine.Random.Range(0, Alive.Count)];
                BossTargeting.NoteTarget(__result);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[4P-Fix] GetRandom replacement failed, falling back to vanilla: " + e.Message);
                return true;
            }
        }

        private static AbstractPlayerController SafeGet(PlayerId id)
        {
            try
            {
                var p = PlayerManager.GetPlayer(id);
                return (p == null) ? null : p;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
