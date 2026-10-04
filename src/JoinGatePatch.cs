using System;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Blocks extra controllers (slots 1-3) from joining while there is no live Map or Level.
    ///
    /// Hard rules:
    ///
    ///   1. GAMEPLAY IS UNTOUCHABLE. If Level.Current or Map.Current is non-null the prefix returns
    ///      immediately without reading or writing a single slot field. The game drives joining by
    ///      itself there - Level._OnLevelStart and the tail of Map.start_cr both call
    ///      SetPlayerCanJoin(PlayerTwo, true, true) once play is actually ready.
    ///   2. Slot 0 is never touched. All state changes go through
    ///      PlayerManager.SetPlayerCanJoin(PlayerId.PlayerTwo, ...), which by construction writes
    ///      only slots 1, 2 and 3 - so there is no reflection and no way to reach Player One.
    ///   3. No snapshot/restore. An earlier version saved canJoin in the menus (false) and restored
    ///      that stale value after the level had already enabled joining, which permanently locked
    ///      P3/P4 out. Blocking is now purely momentary: we never put a value back.
    ///   4. Fail open - any exception disables the gate for the session.
    /// </summary>
    internal static class JoinGate
    {
        internal static bool GateDisabled;
        internal static bool SlotSelectActive;

        private static bool _blocked;
        private static string _activeSceneName = string.Empty;
        private static string _lastAllowedSceneLogged;

        internal static void Init()
        {
            try
            {
                _activeSceneName = SceneManager.GetActiveScene().name;
            }
            catch (Exception e)
            {
                _activeSceneName = string.Empty;
                Plugin.Log.LogWarning("[4P-Fix] JoinGate.Init: could not read active scene: " + e.Message);
            }

            try
            {
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] JoinGate.Init: scene events unavailable: " + e.Message);
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            try { _activeSceneName = to.name; } catch (Exception) { }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try { _activeSceneName = SceneManager.GetActiveScene().name; } catch (Exception) { }
        }

        internal static string ActiveScene
        {
            get { return _activeSceneName; }
        }

        private static bool IsMenuScene(string scene)
        {
            if (scene == "scene_slot_select") return true;
            if (!Plugin.GateTitleScreen.Value) return false;
            return scene == "scene_title" || scene == "scene_start";
        }

        /// <summary>True while a Map or Level owns the session - the gate must keep its hands off.</summary>
        private static bool GameplayActive()
        {
            return Level.Current != null || Map.Current != null;
        }

        internal static void Enforce()
        {
            if (GateDisabled) return;
            if (!Plugin.BlockJoinOutsideGameplay.Value) return;

            // Rule 1: active gameplay runs completely untouched.
            if (GameplayActive())
            {
                NoteAllowed();
                return;
            }

            // Title/start stay vanilla unless explicitly opted in.
            if (!Plugin.GateTitleScreen.Value &&
                (_activeSceneName == "scene_title" || _activeSceneName == "scene_start"))
            {
                NoteAllowed();
                return;
            }

            var inMenu = SlotSelectActive || IsMenuScene(_activeSceneName);

            // Outside gameplay we block: menus, and the gaps between scenes (cutscenes, loading).
            if (!inMenu && !NoGameplayYet())
            {
                NoteAllowed();
                return;
            }

            Block();
        }

        /// <summary>No Level and no Map: a transition, cutscene or loading screen.</summary>
        private static bool NoGameplayYet()
        {
            return !GameplayActive();
        }

        private static void Block()
        {
            // Writes slots 1, 2 and 3 only. Slot 0 is structurally unreachable through this call.
            PlayerManager.SetPlayerCanJoin(PlayerId.PlayerTwo, canJoin: false, promptBeforeJoin: false);

            if (!_blocked)
            {
                _blocked = true;
                _lastAllowedSceneLogged = null;
                Plugin.Log.LogInfo("[4P-Fix] Join blocked in scene: " + _activeSceneName +
                                   " (slots 1-3; slot 0 untouched)");
            }
        }

        private static void NoteAllowed()
        {
            if (_blocked)
            {
                _blocked = false;
                _lastAllowedSceneLogged = null;
            }

            if (_lastAllowedSceneLogged == _activeSceneName) return;
            _lastAllowedSceneLogged = _activeSceneName;
            Plugin.Log.LogInfo("[4P-Fix] Joined allowed in scene: " + _activeSceneName);
        }

        /// <summary>
        /// Re-arms joining for slots 1-3 when a Level or Map finishes initialising, so a block that
        /// happened during the preceding menu/transition can never leak into gameplay.
        /// </summary>
        internal static void RestoreForGameplay(string source)
        {
            if (!Plugin.RestoreJoinOnGameplayLoad.Value) return;

            try
            {
                PlayerManager.SetPlayerCanJoin(PlayerId.PlayerTwo, canJoin: true, promptBeforeJoin: true);
                _blocked = false;
                _lastAllowedSceneLogged = null;
                Plugin.Log.LogInfo("[4P-Fix] Joined allowed in scene: " + _activeSceneName +
                                   " (re-armed slots 1-3 from " + source + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] RestoreForGameplay(" + source + ") failed: " + e.Message);
            }
        }

        internal static void Disable(string reason)
        {
            if (GateDisabled) return;
            GateDisabled = true;
            Plugin.Log.LogError("[4P-Fix] Join gate DISABLED for this session: " + reason);
        }
    }

    /// <summary>
    /// Must never throw and never return false: if PlayerManager.Update does not run, slot 0 cannot
    /// join and the title screen hangs on "Press Any Button".
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Update))]
    internal static class PlayerManagerUpdatePatch
    {
        private static void Prefix()
        {
            try
            {
                JoinGate.Enforce();
            }
            catch (Exception e)
            {
                JoinGate.Disable(e.ToString());
            }
        }
    }

    /// <summary>Re-arm joining once a Level has initialised.</summary>
    [HarmonyPatch(typeof(Level), "Start")]
    internal static class LevelStartJoinPatch
    {
        private static void Postfix(Level __instance)
        {
            if (__instance == null) return;
            JoinGate.RestoreForGameplay("Level.Start");
        }
    }

    /// <summary>Re-arm joining once the overworld has initialised.</summary>
    [HarmonyPatch(typeof(Map), "Start")]
    internal static class MapStartJoinPatch
    {
        private static void Postfix(Map __instance)
        {
            if (__instance == null) return;
            JoinGate.RestoreForGameplay("Map.Start");
        }
    }

    /// <summary>
    /// Tracks the slot-select screen only. Never clears prompts (PlayerManager.ClearJoinPrompt
    /// loops i = 0..3 and would reach slot 0) and never touches the Cuphead/Mugman picker.
    /// </summary>
    [HarmonyPatch(typeof(SlotSelectScreen), "Awake")]
    internal static class SlotSelectScreenAwakePatch
    {
        private static void Postfix(SlotSelectScreen __instance)
        {
            if (__instance == null) return;
            JoinGate.SlotSelectActive = true;
        }
    }

    [HarmonyPatch(typeof(SlotSelectScreen), "OnDestroy")]
    internal static class SlotSelectScreenOnDestroyPatch
    {
        private static void Postfix()
        {
            JoinGate.SlotSelectActive = false;
        }
    }
}
