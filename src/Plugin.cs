using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Bug-fix pack for the 4-player Cuphead setup.
    ///
    /// This plugin assumes Assembly-CSharp.dll has already been hard-patched for four players
    /// (PlayerId.PlayerThree/PlayerFour exist, PlayerManager tracks 4 slots). It only repairs the
    /// places where that conversion left two-player assumptions behind.
    /// </summary>
    [BepInPlugin(Guid, "Cuphead 4-Player Fixes", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.mod.cuphead.4player.fixes";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // --- Coins / save sync ------------------------------------------------
        internal static ConfigEntry<bool> SyncExtrasToSave;

        // --- Mr. Wheezy spawns ------------------------------------------------
        internal static ConfigEntry<bool> FixWheezySpawns;
        internal static ConfigEntry<bool> WheezyFixAllDicePalace;
        internal static ConfigEntry<float> HazardClearance;
        internal static ConfigEntry<float> ExtraSpawnSpacing;

        // --- Camera rescue ----------------------------------------------------
        internal static ConfigEntry<bool> RescueOffscreenPlayers;
        internal static ConfigEntry<float> ViewportMargin;
        internal static ConfigEntry<float> RescueCooldown;

        // --- Tint persistence -------------------------------------------------
        internal static ConfigEntry<bool> PersistExtraTints;
        internal static ConfigEntry<string> Player3Color;
        internal static ConfigEntry<string> Player4Color;

        // --- DLC transition ---------------------------------------------------
        internal static ConfigEntry<bool> FixAnyPlayerInput;
        internal static ConfigEntry<bool> ParkExtrasForDlc;
        internal static ConfigEntry<bool> GiftChaliceToExtras;

        // --- Damage scaling ----------------------------------------------------
        internal static ConfigEntry<bool> EnableDamageScaling;
        internal static ConfigEntry<float> DamageMultiplier1P;
        internal static ConfigEntry<float> DamageMultiplier2P;
        internal static ConfigEntry<float> DamageMultiplier3P;
        internal static ConfigEntry<float> DamageMultiplier4P;
        internal static ConfigEntry<bool> ScaleByAlivePlayers;
        internal static ConfigEntry<bool> EnableDynamicDefaultScaling;
        internal static ConfigEntry<bool> LogDamageScaling;

        // --- Boss targeting ----------------------------------------------------
        internal static ConfigEntry<bool> FixBossTargeting;
        internal static ConfigEntry<bool> FixChessBossTargeting;
        internal static ConfigEntry<bool> RedirectDeadPlayerAim;
        internal static ConfigEntry<bool> LogTargetSwitches;

        // --- Overworld interaction --------------------------------------------
        internal static ConfigEntry<bool> ExtraPlayersCanInteract;
        internal static ConfigEntry<bool> ExtrasUseMugmanEntities;
        internal static ConfigEntry<bool> VerboseMapInteractions;
        internal static ConfigEntry<bool> MapInteractionWatchdog;
        internal static ConfigEntry<bool> FixChaliceGhostArray;
        internal static ConfigEntry<bool> FixChaliceVictoryBranch;
        internal static ConfigEntry<bool> ForceVictoryCleanup;
        internal static ConfigEntry<bool> RegroupStrandedExtras;
        internal static ConfigEntry<float> MapRegroupDistance;

        // --- Join gating ------------------------------------------------------
        internal static ConfigEntry<bool> BlockJoinOutsideGameplay;
        internal static ConfigEntry<bool> RestoreJoinOnGameplayLoad;
        internal static ConfigEntry<bool> GateTitleScreen;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // LogMessage, not LogInfo: this is the "did Awake even start" marker and must survive
            // any log-level filtering.
            Log.LogMessage("Cuphead4PFixes: Awake starting.");

            // Every stage is isolated. A failure in one must never stop the others, and must never
            // leave Harmony unpatched silently - that was invisible in the previous run.
            Stage("BindConfig", BindConfig);
            Stage("JoinGate.Init", JoinGate.Init);

            Stage("Harmony.PatchAll", () =>
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(typeof(Plugin).Assembly);
            });

            Stage("FixRunner", () =>
            {
                var runner = new GameObject("Cuphead4PFixes_Runner");
                runner.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(runner);
                runner.AddComponent<FixRunner>();
            });

            Log.LogMessage("Cuphead4PFixes: Awake complete (" + _patched + " stage(s) OK, " + _failed + " failed).");
        }

        private int _patched;
        private int _failed;

        private void Stage(string name, Action body)
        {
            try
            {
                body();
                _patched++;
                Log.LogInfo("Stage OK: " + name);
            }
            catch (Exception e)
            {
                _failed++;
                Log.LogError("Stage FAILED: " + name + " -> " + e);
            }
        }

        private void BindConfig()
        {
            SyncExtrasToSave = Config.Bind("Coins", "SyncExtrasToSave", true,
                "On every save load and slot select, pair the extra players' wallets - P3 takes Player One's coin balance, P4 takes Player Two's - and grant them every weapon/super/charm P1 and P2 own. Saves written before the 4-player conversion contain no P3/P4 data, so JsonUtility leaves them at constructor defaults (0 coins, peashooter only). Loadouts are never touched.");

            FixWheezySpawns = Config.Bind("Spawns", "FixWheezySpawns", true,
                "Move P3/P4 off the cigar/flame hazard spawn points in the Mr. Wheezy (DicePalaceCigar) fight.");
            WheezyFixAllDicePalace = Config.Bind("Spawns", "ApplyToAllDicePalace", false,
                "Apply the same hazard-aware spawn placement to every Dice Palace mini-boss, not just Mr. Wheezy.");
            HazardClearance = Config.Bind("Spawns", "HazardClearance", 140f,
                "Minimum world-unit distance a spawning extra player must keep from a detected hazard spawn point.");
            ExtraSpawnSpacing = Config.Bind("Spawns", "ExtraSpawnSpacing", 120f,
                "Horizontal gap between extra players when they are re-placed.");

            RescueOffscreenPlayers = Config.Bind("Camera", "RescueOffscreenPlayers", true,
                "Teleport any player who falls past the left or bottom edge of the camera back next to Player 1. Prevents the Treetop Trouble offscreen softlock.");
            ViewportMargin = Config.Bind("Camera", "ViewportMargin", 0.04f,
                "How far outside the viewport (0-1 units) a player must be before being rescued.");
            RescueCooldown = Config.Bind("Camera", "RescueCooldown", 0.75f,
                "Minimum seconds between two rescues of the same player.");

            PersistExtraTints = Config.Bind("Colors", "PersistExtraTints", true,
                "Re-apply P3/P4 sprite tints in LateUpdate so lighting zones and colour coroutines cannot wash them out.");
            Player3Color = Config.Bind("Colors", "Player3Color", "#0FFF00",
                "Hex tint for Player 3. Default matches the tint the game already applies.");
            Player4Color = Config.Bind("Colors", "Player4Color", "#FFFF00",
                "Hex tint for Player 4.");

            FixAnyPlayerInput = Config.Bind("DLC", "FixAnyPlayerInput", true,
                "Strip null Rewired players out of CupheadInput.AnyPlayerInput. Without this, a missing P3/P4 input object throws inside cutscene coroutines and softlocks the DLC intro.");
            ParkExtrasForDlc = Config.Bind("DLC", "ParkExtrasForDlc", true,
                "Hide P3/P4 overworld avatars while the boatman transition into the DLC runs, and restore them once the DLC map loads.");
            GiftChaliceToExtras = Config.Bind("DLC", "GiftChaliceToExtras", true,
                "Give P3/P4 the Chalice charm at the same moment the game gives it to P1/P2, so inventories stay in sync.");

            EnableDamageScaling = Config.Bind("DamageScaling", "EnableDamageScaling", true,
                "Override PlayerManager.DamageMultiplier - the single getter every player weapon and super reads - with the per-player-count curve below. Vanilla hardcodes 1/livingPlayers.");
            DamageMultiplier1P = Config.Bind("DamageScaling", "DamageMultiplier_1P", 1.0f,
                "Damage each player deals while 1 player is ALIVE. Vanilla equivalent: 1.00.");
            DamageMultiplier2P = Config.Bind("DamageScaling", "DamageMultiplier_2P", 0.5f,
                "Damage each player deals while 2 are ALIVE. Matches vanilla (0.50).");
            DamageMultiplier3P = Config.Bind("DamageScaling", "DamageMultiplier_3P", 0.35f,
                "Damage each player deals while 3 are ALIVE. Vanilla equivalent: 0.33.");
            DamageMultiplier4P = Config.Bind("DamageScaling", "DamageMultiplier_4P", 0.25f,
                "Damage each player deals while 4 are ALIVE. Matches vanilla (0.25).");
            ScaleByAlivePlayers = Config.Bind("DamageScaling", "ScaleByAlivePlayers", true,
                "If true, damage scaling dynamically adjusts based on currently alive players (e.g., if P2 dies in a 4P game, damage increases to the 3P rate). If false, damage scaling stays locked to the total active player count regardless of deaths.");
            EnableDynamicDefaultScaling = Config.Bind("DamageScaling", "EnableDynamicDefaultScaling", true,
                "If a multiplier above is unset or invalid (zero, negative, NaN or infinity), fall back to 1.0 / activePlayerCount instead.");
            LogDamageScaling = Config.Bind("DamageScaling", "LogDamageScaling", true,
                "Log the active player count and multiplier on level start, and whenever the count changes mid-fight.");

            FixBossTargeting = Config.Bind("Bosses", "FixBossTargeting", true,
                "Replace PlayerManager.GetRandom with a uniform pick over living players, and make DoesPlayerExist total. Vanilla GetRandom rolls PlayerId.Any/None into a Dictionary keyed 0-3 and throws KeyNotFoundException, which kills the calling boss coroutine and freezes its targeting.");
            FixChessBossTargeting = Config.Bind("Bosses", "FixChessBossTargeting", true,
                "King's Leap: ChessKnightLevelKnight, ChessBishopLevelBishop and ChessBishopLevelCandle hardcode GetPlayer(PlayerOne)/GetPlayer(PlayerTwo) as their only candidates. Redirect the PlayerTwo slot to a rotating living player among P2/P3/P4 so extras can be targeted. Pawn, Queen and Rook already use the four-player-aware GetNext/GetRandom/GetFirst and are left alone.");
            RedirectDeadPlayerAim = Config.Bind("Bosses", "RedirectDeadPlayerAim", true,
                "In levels, make a dead player's AbstractPlayerController.center report the nearest LIVING player's position. Most bosses cache a target (e.g. DicePalaceBoozeLevelOlive sets nextPlayerTarget = PlayerOne in Awake and never updates it) and there are 245 hardcoded GetPlayer(PlayerOne/Two) calls across 68 files, so redirecting the property they all aim with is the only tractable fix.");
            LogTargetSwitches = Config.Bind("Bosses", "LogTargetSwitches", false,
                "Diagnostic only, off by default. Logs each time boss target selection returns a different player, and each time a dead player's aim is redirected. Targeting runs every frame, so this is verbose - enable it only while investigating a targeting problem.");

            ExtraPlayersCanInteract = Config.Bind("Map", "ExtraPlayersCanInteract", true,
                "Let P3/P4 trigger NPC dialogues, level entrances and Porkrind's Shop. Vanilla interaction code only indexes Map.Current.players[0..1], so extra players (who live in Map.Current.altplayers) otherwise walk over everything with no effect.");
            ExtrasUseMugmanEntities = Config.Bind("Map", "ExtrasUseMugmanEntities", true,
                "Allow extras to activate entities flagged Interactor.Mugman. Entities flagged Interactor.Both always require Cuphead and Mugman together and are never given to an extra.");
            MapInteractionWatchdog = Config.Bind("Map", "MapInteractionWatchdog", true,
                "After the overworld has been idle for a full second, clear the static HasPopupOpened flag if it is still set and re-Enable any extra player left Stationary. Recovers from an interaction whose teardown never ran, which would otherwise make every shop/level/NPC permanently unusable.");
            FixChaliceGhostArray = Config.Bind("Map", "FixChaliceGhostArray", true,
                "THE Chalice victory softlock. MapPlayerAnimationController.Init indexes the prefab's 2-entry ghostInPortal array by player id (and by 1 - id), so a Chalice P3/P4 returning to the map throws IndexOutOfRangeException out of Map.Awake - leaving the map half-built, Map.Start unrun and the whole victory sequence dead. Grows the array to 4 and clamps every element read.");
            FixChaliceVictoryBranch = Config.Bind("Map", "FixChaliceVictoryBranch", true,
                "Transpile Map+<start_cr>d__0.MoveNext: drop the bogus '|| PlayerManager.Multiplayer2' from the Chalice victory branch condition, and make all 12 OnWinComplete call sites null-safe so a missing avatar cannot kill the coroutine mid-sequence.");
            ForceVictoryCleanup = Config.Bind("Map", "ForceVictoryCleanup", true,
                "When Map.CurrentState becomes Ready - the last thing start_cr does - make sure every map player is walking and the pause system is released. Event-driven off the game's own completion signal, not polling.");
            RegroupStrandedExtras = Config.Bind("Map", "RegroupStrandedExtras", true,
                "On map load, move any extra player that spawned further than MapRegroupDistance from Player One back beside the group. Covers a Chalice victory return leaving P3/P4 at a stale pre-level coordinate.");
            MapRegroupDistance = Config.Bind("Map", "MapRegroupDistance", 5f,
                "How far from Player One (in map world units, roughly 1.0 scale) an extra may spawn before being regrouped.");
            VerboseMapInteractions = Config.Bind("Map", "VerboseMapInteractions", false,
                "Log every frame an extra player is standing in range of an interactive entity. Noisy - only enable when diagnosing why a prompt will not fire.");

            BlockJoinOutsideGameplay = Config.Bind("Join", "BlockJoinOutsideGameplay", true,
                "Block joining for slots 1-3 while no Map or Level is live (menus, cutscenes, loading). Never applies during gameplay, and never touches Player One.");
            RestoreJoinOnGameplayLoad = Config.Bind("Join", "RestoreJoinOnGameplayLoad", true,
                "Re-arm joining for slots 1-3 in a Level.Start / Map.Start postfix, so a block from the preceding menu can never leak into gameplay. Note this enables joining slightly earlier than vanilla, which defers it to Level._OnLevelStart and the end of Map.start_cr.");
            GateTitleScreen = Config.Bind("Join", "GateTitleScreen", false,
                "Also block slots 1-3 on scene_title/scene_start. Off by default: vanilla already disables those slots there (StartScreen.Awake calls PlayerManager.ResetPlayers), so gating buys nothing and the title screen is the one place a mistake costs a boot softlock.");
        }

        private void OnDestroy()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
