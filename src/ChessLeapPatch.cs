using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Cuphead4PFixes
{
    /// <summary>
    /// King's Leap (Chess Castle) support for Players 3 and 4.
    ///
    /// There is no MapKingOfGames or MapKingOfGamesRope type. The overworld entrance is an ordinary
    /// map entity ("KingOfGamesWorldMap") already covered by the AbstractMapInteractiveEntity patch.
    /// The rope and the King live INSIDE the castle level and derive from a different base -
    /// AbstractLevelInteractiveEntity - whose subclasses here are exactly ChessCastleLevelStart
    /// (rope up), ChessCastleLevelExit (rope down) and ChessCastleLevelKingInteractionPoint.
    ///
    /// That base polls from FixedUpdate (not Update) and hardcodes PlayerOne/PlayerTwo in all four
    /// interactor branches, so P3/P4 can never grab the rope or talk to the King. Unlike the map
    /// version there is no per-player dialogue array to resize - prompts go through Show(PlayerId) -
    /// so extras can simply be offered the same activation path.
    /// </summary>
    internal static class LevelInteractionExtras
    {
        private const int InteractButton = 13;

        private static MethodInfo _playerWithinDistance;
        private static MethodInfo _playerIsDashing;
        private static MethodInfo _activateWithPlayer;
        private static FieldInfo _stateField;
        private static bool _ready;
        private static bool _broken;

        private static bool EnsureReflection()
        {
            if (_ready) return true;
            if (_broken) return false;

            try
            {
                var t = typeof(AbstractLevelInteractiveEntity);
                _playerWithinDistance = AccessTools.Method(t, "PlayerWithinDistance", new[] { typeof(PlayerId) });
                _playerIsDashing = AccessTools.Method(t, "PlayerIsDashing", new[] { typeof(PlayerId) });
                _activateWithPlayer = AccessTools.Method(t, "Activate", new[] { typeof(AbstractPlayerController) });
                _stateField = AccessTools.Field(t, "<state>k__BackingField");

                if (_playerWithinDistance == null || _playerIsDashing == null ||
                    _activateWithPlayer == null || _stateField == null)
                {
                    throw new Exception("AbstractLevelInteractiveEntity members not found");
                }

                _ready = true;
                return true;
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Log.LogError("[4P-Fix] King's Leap interaction reflection failed: " + e.Message);
                return false;
            }
        }

        internal static void TryExtraInteract(AbstractLevelInteractiveEntity entity)
        {
            if (!Plugin.ExtraPlayersCanInteract.Value) return;
            if (entity == null) return;
            if (!EnsureReflection()) return;

            // Interactor.Both genuinely needs Cuphead AND Mugman together; never substitute.
            if (entity.interactor == AbstractLevelInteractiveEntity.Interactor.Both) return;
            if (Convert.ToInt32(_stateField.GetValue(entity)) == 2 /* Activated */) return;

            foreach (var id in Extras.ExtraIds)
            {
                var player = Extras.GetPlayer(id);
                if (player == null) continue;
                if (player.IsDead) continue;

                var input = player.input;
                if (input == null || input.actions == null) continue;

                if (!(bool)_playerWithinDistance.Invoke(entity, new object[] { id })) continue;
                if (!input.actions.GetButtonDown(InteractButton)) continue;
                if ((bool)_playerIsDashing.Invoke(entity, new object[] { id })) continue;

                Plugin.Log.LogInfo("[4P-Fix] King's Leap interact: entity=" + entity.name +
                                   " (" + entity.GetType().Name + ") interactor=" + entity.interactor +
                                   " by=" + id);

                _activateWithPlayer.Invoke(entity, new object[] { player });
                return;
            }
        }
    }

    [HarmonyPatch(typeof(AbstractLevelInteractiveEntity), "FixedUpdate")]
    internal static class AbstractLevelInteractiveEntityFixedUpdatePatch
    {
        private static void Postfix(AbstractLevelInteractiveEntity __instance)
        {
            if (__instance == null) return;
            try
            {
                LevelInteractionExtras.TryExtraInteract(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[4P-Fix] King's Leap interaction postfix failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Chess boss targeting.
    ///
    /// Measured, not assumed: ChessPawnLevelPawn, ChessQueenLevelQueen and ChessRookLevelRook make
    /// zero hardcoded GetPlayer(PlayerOne/PlayerTwo) calls - they already target through
    /// PlayerManager.GetNext()/GetRandom()/GetFirst(), all of which are four-player aware (and
    /// GetRandom is repaired elsewhere in this plugin). Only three types hardcode the pair:
    /// ChessKnightLevelKnight (8), ChessBishopLevelBishop (8) and ChessBishopLevelCandle (2).
    ///
    /// Their shape is always "P1 and P2, then coin-flip or compare distances", e.g. the Knight:
    ///     var p1 = GetPlayer(PlayerOne); var p2 = GetPlayer(PlayerTwo);
    ///     if (p2 != null &amp;&amp; !p2.IsDead) targetPlayer = Rand.Bool() ? p1 : p2;
    /// so P3/P4 are never candidates while P1/P2 live. Rather than rewrite each boss, the PlayerTwo
    /// slot is redirected to a rotating living "secondary" chosen from P2/P3/P4. The coin flips and
    /// distance comparisons then naturally include the extras, and the existing
    /// `if (targetPlayer.IsDead) targetPlayer = GetNext()` recovery still works.
    ///
    /// The choice is cached per frame so every call inside one method agrees on the same player.
    /// </summary>
    internal static class ChessTargeting
    {
        private static readonly PlayerId[] Secondary =
        {
            PlayerId.PlayerTwo, PlayerId.PlayerThree, PlayerId.PlayerFour
        };

        private static readonly List<AbstractPlayerController> Candidates = new List<AbstractPlayerController>(3);

        private static int _cachedFrame = -1;
        private static AbstractPlayerController _cached;
        private static PlayerId _lastLogged = PlayerId.None;

        /// <summary>Replaces GetPlayer(PlayerId.PlayerTwo) inside the hardcoded chess bosses.</summary>
        public static AbstractPlayerController SecondaryTarget()
        {
            try
            {
                if (_cachedFrame == UnityEngine.Time.frameCount) return _cached;
                _cachedFrame = UnityEngine.Time.frameCount;

                Candidates.Clear();
                foreach (var id in Secondary)
                {
                    if (!BossTargeting.SlotExists(id)) continue; // excludes dead and missing
                    var p = PlayerManager.GetPlayer(id);
                    if (p != null) Candidates.Add(p);
                }

                if (Candidates.Count == 0)
                {
                    // Nobody else alive - hand back the real Player Two, exactly as vanilla would
                    // (callers null-check and fall back to Player One).
                    _cached = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                    return _cached;
                }

                _cached = Candidates[UnityEngine.Random.Range(0, Candidates.Count)];

                if (Plugin.LogTargetSwitches.Value && _cached != null && _cached.id != _lastLogged)
                {
                    _lastLogged = _cached.id;
                    if (_cached.id == PlayerId.PlayerThree || _cached.id == PlayerId.PlayerFour)
                    {
                        Plugin.Log.LogInfo("[4P-Fix] Chess boss selected " + _cached.id +
                                           " as target (alive: " + BossTargeting.DescribeAlive() + ")");
                    }
                }

                return _cached;
            }
            catch (Exception)
            {
                try { return PlayerManager.GetPlayer(PlayerId.PlayerTwo); }
                catch (Exception) { return null; }
            }
        }
    }

    [HarmonyPatch]
    internal static class ChessBossTargetingPatch
    {
        private static readonly string[] HardcodedBosses =
        {
            "ChessKnightLevelKnight",
            "ChessBishopLevelBishop",
            "ChessBishopLevelCandle"
        };

        /// <summary>
        /// Only patches methods whose IL actually contains `call PlayerManager.GetPlayer(PlayerId)`,
        /// found by scanning for the call opcode plus that method's metadata token. Nested types are
        /// included because most of this logic lives in coroutine state machines.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var results = new List<MethodBase>();
            var getPlayer = AccessTools.Method(typeof(PlayerManager), "GetPlayer", new[] { typeof(PlayerId) });
            if (getPlayer == null)
            {
                Plugin.Log.LogError("[4P-Fix] PlayerManager.GetPlayer(PlayerId) not found - chess targeting skipped.");
                return results;
            }

            var token = BitConverter.GetBytes(getPlayer.MetadataToken);

            foreach (var name in HardcodedBosses)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    Plugin.Log.LogWarning("[4P-Fix] Chess type not found: " + name);
                    continue;
                }

                CollectFrom(type, token, results);
                foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    CollectFrom(nested, token, results);
                }
            }

            Plugin.Log.LogInfo("[4P-Fix] Chess targeting: " + results.Count + " method(s) reference GetPlayer and will be retargeted.");
            return results;
        }

        private static void CollectFrom(Type type, byte[] token, List<MethodBase> results)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsAbstract) continue;

                byte[] il;
                try
                {
                    var body = m.GetMethodBody();
                    if (body == null) continue;
                    il = body.GetILAsByteArray();
                }
                catch (Exception)
                {
                    continue;
                }
                if (il == null) continue;

                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28) continue; // call
                    if (il[i + 1] == token[0] && il[i + 2] == token[1] &&
                        il[i + 3] == token[2] && il[i + 4] == token[3])
                    {
                        results.Add(m);
                        break;
                    }
                }
            }
        }

        private static bool Prepare()
        {
            return Plugin.FixChessBossTargeting.Value;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getPlayer = AccessTools.Method(typeof(PlayerManager), "GetPlayer", new[] { typeof(PlayerId) });
            var secondary = AccessTools.Method(typeof(ChessTargeting), "SecondaryTarget");

            var output = new List<CodeInstruction>();

            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Call && getPlayer != null && getPlayer.Equals(ins.operand) && output.Count > 0)
                {
                    // Only the PlayerTwo slot is redirected; PlayerOne stays the anchor.
                    var previous = output[output.Count - 1];
                    if (IsLoadOfOne(previous) && secondary != null)
                    {
                        output.RemoveAt(output.Count - 1);
                        var replacement = new CodeInstruction(OpCodes.Call, secondary);
                        replacement.labels.AddRange(previous.labels);
                        replacement.labels.AddRange(ins.labels);
                        replacement.blocks.AddRange(previous.blocks);
                        output.Add(replacement);
                        continue;
                    }
                }

                output.Add(ins);
            }

            return output;
        }

        /// <summary>True for any encoding of the constant 1, i.e. PlayerId.PlayerTwo.</summary>
        private static bool IsLoadOfOne(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Ldc_I4_1) return true;
            if (ins.opcode == OpCodes.Ldc_I4 && ins.operand is int && (int)ins.operand == 1) return true;
            if (ins.opcode == OpCodes.Ldc_I4_S && ins.operand != null && Convert.ToInt32(ins.operand) == 1) return true;
            return false;
        }
    }
}
