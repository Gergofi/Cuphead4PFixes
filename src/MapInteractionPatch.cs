using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Lets Player 3 and Player 4 use the overworld: NPCs (MapDialogueInteraction), level entrances
    /// (MapLevelLoader), Porkrind's Shop (MapShopLoader) and scene transitions (MapSceneLoader) all
    /// derive from AbstractMapInteractiveEntity, so one patch on its Update covers every case.
    ///
    /// Interaction here is NOT collision-trigger based - there is no MapNPC/MapLevel type and no
    /// OnTriggerStay2D on these entities. Update polls distance via PlayerWithinDistance(int i),
    /// which indexes Map.Current.players[i] - an array of length 2. P3/P4 live in
    /// Map.Current.altplayers and are never consulted, which is why they walk over everything.
    ///
    /// The critical detail: an extra player must own a real prompt bubble, because the subclasses
    /// gate on it. MapDialogueInteraction.Activate begins
    ///
    ///     if (dialogues[(int)player.id] != null &amp;&amp; dialogues[(int)player.id].transform.localScale.x == 1f ...)
    ///
    /// and AbstractMapInteractiveEntity.Activate begins `dialogues[(int)player.id]`. `dialogues` is
    /// MapUIInteractionDialogue[2], so PlayerThree (id 2) would either throw or silently no-op.
    /// So we grow that array to 4 and drive a real Show/Hide lifecycle for the extras, then call the
    /// subclasses' own virtual Activate(MapPlayerController) - no reimplementation of their logic.
    ///
    /// Map coins need no patch: MapCoin.OnTriggerEnter2D never checks which player collided and
    /// already credits all four wallets.
    /// </summary>
    internal static class MapInteraction
    {
        private const int InteractButton = 13; // the action id vanilla Update uses
        private const int SlotCount = 4;

        private static FieldInfo _lockInputField;
        private static FieldInfo _stateField;
        private static MethodInfo _showMethod;
        private static MethodInfo _activateWithPlayer;
        private static bool _ready;
        private static bool _broken;

        private static bool EnsureReflection()
        {
            if (_ready) return true;
            if (_broken) return false;

            try
            {
                var t = typeof(AbstractMapInteractiveEntity);
                _lockInputField = AccessTools.Field(t, "lockInput");
                _stateField = AccessTools.Field(t, "<state>k__BackingField");
                _showMethod = AccessTools.Method(t, "Show", new[] { typeof(PlayerInput) });
                _activateWithPlayer = AccessTools.Method(t, "Activate", new[] { typeof(MapPlayerController) });

                if (_lockInputField == null || _stateField == null || _showMethod == null || _activateWithPlayer == null)
                    throw new Exception("AbstractMapInteractiveEntity members not found");

                _ready = true;
                return true;
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Log.LogError("[4P-Fix] Map interaction reflection failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Mirrors the guard block at the top of AbstractMapInteractiveEntity.Update.</summary>
        private static bool WorldBusy()
        {
            try
            {
                if (InterruptingPrompt.IsInterrupting()) return true;
                if (MapConfirmStartUI.Current != null &&
                    MapConfirmStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return true;
                if (MapDifficultySelectStartUI.Current != null &&
                    MapDifficultySelectStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return true;
                if (MapBasicStartUI.Current != null &&
                    MapBasicStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return true;
                if (MapEventNotification.Current != null && MapEventNotification.Current.showing) return true;
                if (Map.Current != null && Map.Current.CurrentState == Map.State.Graveyard) return true;
                if (SceneLoader.IsInBlurTransition) return true;
            }
            catch (Exception)
            {
                return true;
            }
            return false;
        }

        private static bool InteractorAllowsExtras(AbstractMapInteractiveEntity entity)
        {
            switch (entity.interactor)
            {
                case AbstractMapInteractiveEntity.Interactor.Both:
                    return false; // needs Cuphead AND Mugman together
                case AbstractMapInteractiveEntity.Interactor.Mugman:
                    return Plugin.ExtrasUseMugmanEntities.Value;
                default:
                    return true;
            }
        }

        /// <summary>dialogues is authored as length 2; extras need slots 2 and 3.</summary>
        private static bool EnsureDialogueSlots(AbstractMapInteractiveEntity entity)
        {
            var d = entity.dialogues;
            if (d != null && d.Length >= SlotCount) return true;

            var grown = new MapUIInteractionDialogue[SlotCount];
            if (d != null)
            {
                for (var i = 0; i < d.Length && i < SlotCount; i++) grown[i] = d[i];
            }
            entity.dialogues = grown;
            return true;
        }

        private static bool InRange(AbstractMapInteractiveEntity entity, MapPlayerController player, out float distance)
        {
            distance = float.MaxValue;
            if (player == null) return false;
            if (player.state != MapPlayerController.State.Walking) return false;
            if (player.hideInteractionPrompts) return false;

            var anchor = (Vector2)entity.transform.position + entity.interactionPoint;
            distance = Vector2.Distance(anchor, player.transform.position);
            return distance <= entity.interactionDistance;
        }

        internal static void TryExtraInteract(AbstractMapInteractiveEntity entity)
        {
            if (!Plugin.ExtraPlayersCanInteract.Value) return;
            if (entity == null) return;
            if (!EnsureReflection()) return;

            var map = Map.Current;
            if (map == null) return;

            var extras = map.altplayers;
            if (extras == null) return;
            if (!InteractorAllowsExtras(entity)) return;
            if (!EnsureDialogueSlots(entity)) return;

            // NOTE: state == Activated is deliberately NOT part of this gate.
            //
            // base.Activate leaves the entity in State.Activated, and nothing resets it except
            // Show(), which assigns state = State.Ready. Vanilla recovers because Check() runs from
            // Update() every frame - ahead of Update's own `state == Activated` early-return - and
            // re-Shows for P1/P2. Extras have no such path, so if we refuse to Show while Activated
            // the entity latches and can never be used again. Re-Showing once the world is idle is
            // exactly the vanilla recovery, and WorldBusy() already blocks us while a popup or
            // dialogue is up.
            var busy = map.CurrentState != Map.State.Ready
                       || (bool)_lockInputField.GetValue(entity)
                       || WorldBusy();

            for (var i = 0; i < extras.Length; i++)
            {
                var extra = extras[i];
                if (extra == null) continue;

                var slot = (int)extra.id;
                if (slot < 2 || slot >= SlotCount) continue; // extras only; never touch P1/P2 slots

                var distance = float.MaxValue;
                var inRange = false;
                if (!busy) inRange = InRange(entity, extra, out distance);

                // --- prompt lifecycle -------------------------------------------------
                if (!inRange)
                {
                    HidePrompt(entity, slot, "out of range");
                    continue;
                }

                if (entity.dialogues[slot] == null)
                {
                    ShowPrompt(entity, extra, slot, distance);
                    continue; // let the bubble finish scaling in before it can be activated
                }

                // --- activation --------------------------------------------------------
                var input = extra.input;
                if (input == null || input.actions == null) continue;
                if (!input.actions.GetButtonDown(InteractButton)) continue;

                Activate(entity, extra, slot, distance);
                return;
            }
        }

        /// <summary>Creates a prompt bubble for an extra and registers it so it can never be orphaned.</summary>
        private static void ShowPrompt(AbstractMapInteractiveEntity entity, MapPlayerController extra, int slot, float distance)
        {
            try
            {
                var dialogue = _showMethod.Invoke(entity, new object[] { extra.input }) as MapUIInteractionDialogue;
                if (dialogue == null)
                {
                    // Never retry blindly next frame - that is how you get a stack of bubbles.
                    Plugin.Log.LogWarning("[4P-Fix] Show() returned null for " + extra.id + " on " + entity.name +
                                          "; not retrying this approach.");
                    return;
                }

                entity.dialogues[slot] = dialogue;
                PromptTracker.Track(entity, slot, dialogue);

                if (Plugin.VerboseMapInteractions.Value)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "[4P-Fix] Prompt CREATED: scene={0} entity={1} player={2} dist={3:F2} (tracked={4})",
                        SceneManager.GetActiveScene().name, entity.name, extra.id, distance, PromptTracker.Count));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Show() failed for " + extra.id + " on " + entity.name + ": " + e.Message);
            }
        }

        /// <summary>Closes and untracks an extra's prompt. Safe to call when there is none.</summary>
        internal static void HidePrompt(AbstractMapInteractiveEntity entity, int slot, string reason)
        {
            if (entity == null || entity.dialogues == null || slot >= entity.dialogues.Length) return;

            var dialogue = entity.dialogues[slot];
            if (dialogue == null)
            {
                entity.dialogues[slot] = null;
                return;
            }

            try { entity.Hide(dialogue); } catch (Exception) { }
            entity.dialogues[slot] = null;
            PromptTracker.Untrack(dialogue);

            if (Plugin.VerboseMapInteractions.Value)
            {
                Plugin.Log.LogInfo("[4P-Fix] Prompt DESTROYED: entity=" + entity.name + " slot=" + slot +
                                   " reason=" + reason + " (tracked=" + PromptTracker.Count + ")");
            }
        }

        private static void Activate(AbstractMapInteractiveEntity entity, MapPlayerController player, int slot, float distance)
        {
            try
            {
                Plugin.Log.LogInfo(string.Format(
                    "[4P-Fix] Map ACTIVATE: scene={0} entity={1} ({2}) interactor={3} by={4} dist={5:F2} mapState={6}",
                    SceneManager.GetActiveScene().name, entity.name, entity.GetType().Name,
                    entity.interactor, player.id, distance, Map.Current.CurrentState));

                // Dispatches to the subclass override (shop / level / NPC), which does the real
                // work and calls base.Activate(player) to set playerActivating, close the bubble,
                // mark the entity Activated and raise OnActivateEvent.
                _activateWithPlayer.Invoke(entity, new object[] { player });

                // The subclass may DECLINE - MapDialogueInteraction needs the bubble at
                // localScale.x == 1f, MapShopLoader needs !HasPopupOpened. When it declines,
                // base.Activate never ran and the bubble was never closed. Releasing our reference
                // unconditionally here is what orphaned bubbles onto the player and let the next
                // frame create another one. Only release the slot when the entity really activated.
                var activated = Convert.ToInt32(_stateField.GetValue(entity)) == 2;

                if (activated)
                {
                    // base.Activate closed the bubble but only nulled its own local copy.
                    var dialogue = (entity.dialogues != null && slot < entity.dialogues.Length)
                        ? entity.dialogues[slot] : null;
                    if (dialogue != null) PromptTracker.Untrack(dialogue);
                    if (entity.dialogues != null && slot < entity.dialogues.Length) entity.dialogues[slot] = null;
                }
                else
                {
                    // Declined: keep the bubble tracked so walking away still cleans it up.
                    Plugin.Log.LogInfo("[4P-Fix] Activate declined by " + entity.GetType().Name + " on " +
                                       entity.name + " - prompt retained for cleanup.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[4P-Fix] Activate(" + player.id + ") on " + entity.name + " failed: " +
                                    (e.InnerException != null ? e.InnerException.ToString() : e.ToString()));
            }
        }
    }

    /// <summary>
    /// Owns every prompt bubble this plugin creates, so none can be left floating.
    ///
    /// A bubble is an orphan the moment the entity stops pointing at it - the entity was destroyed,
    /// the map changed, or a subclass declined activation and something released the slot. Vanilla
    /// has no cleanup for extras' bubbles, so we sweep them ourselves.
    /// </summary>
    internal static class PromptTracker
    {
        private class Entry
        {
            public AbstractMapInteractiveEntity Entity;
            public int Slot;
            public MapUIInteractionDialogue Dialogue;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        internal static int Count { get { return Entries.Count; } }

        internal static void Track(AbstractMapInteractiveEntity entity, int slot, MapUIInteractionDialogue dialogue)
        {
            if (entity == null || dialogue == null) return;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Dialogue == dialogue) return; // already tracked - never duplicate
            }
            Entries.Add(new Entry { Entity = entity, Slot = slot, Dialogue = dialogue });
        }

        internal static void Untrack(MapUIInteractionDialogue dialogue)
        {
            if (dialogue == null) return;
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Dialogue == dialogue) Entries.RemoveAt(i);
            }
        }

        /// <summary>Closes any bubble its entity no longer references.</summary>
        internal static void Sweep()
        {
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var e = Entries[i];

                if (e.Dialogue == null) { Entries.RemoveAt(i); continue; }

                var stillOwned = e.Entity != null
                                 && e.Entity.dialogues != null
                                 && e.Slot < e.Entity.dialogues.Length
                                 && e.Entity.dialogues[e.Slot] == e.Dialogue;

                if (stillOwned) continue;

                try { e.Dialogue.Close(); } catch (Exception) { }
                Entries.RemoveAt(i);
                Plugin.Log.LogInfo("[4P-Fix] Prompt DESTROYED: orphaned bubble swept (entity=" +
                                   (e.Entity != null ? e.Entity.name : "<destroyed>") + " slot=" + e.Slot + ")");
            }
        }

        /// <summary>New map: the old scene's UI is already gone, just drop the references.</summary>
        internal static void Reset()
        {
            if (Entries.Count > 0)
            {
                Plugin.Log.LogInfo("[4P-Fix] Prompt tracker reset for new map (dropped " + Entries.Count + " reference(s)).");
            }
            Entries.Clear();
        }
    }

    /// <summary>
    /// Clears the two global latches that can strand the whole overworld after an interaction ends.
    ///
    ///   * AbstractMapInteractiveEntity.HasPopupOpened is a STATIC shared by every shop and level
    ///     entrance. MapShopLoader/MapLevelLoader set it true on activate and clear it in their
    ///     OnBack/OnLoadLevel handlers. If a popup is dismissed by a path that never runs those
    ///     handlers, the flag stays true and no popup entity anywhere will ever open again.
    ///   * MapDialogueInteraction.CutScene_cr calls Disable() on every player (extras included),
    ///     setting MapPlayerController.state = Stationary, and relies on reactivate_input_cr to
    ///     Enable() them. If that coroutine is interrupted, players stay frozen.
    ///
    /// Both are only ever repaired after the overworld has been provably idle for a full second, so
    /// this can never fire during a legitimate dialogue, popup or transition.
    /// </summary>
    internal static class MapWatchdog
    {
        private const float IdleGrace = 1.0f;

        private static FieldInfo _hasPopupOpenedField;
        private static float _idleSeconds;
        private static float _lastTick;

        internal static void Tick()
        {
            if (!Plugin.MapInteractionWatchdog.Value) return;

            // ~4 Hz: FindObjectsOfType is too costly for every frame.
            var now = Time.unscaledTime;
            if (now - _lastTick < 0.25f) return;
            var delta = now - _lastTick;
            _lastTick = now;

            try
            {
                var map = Map.Current;
                if (map == null)
                {
                    _idleSeconds = 0f;
                    return;
                }

                // Orphan sweep runs regardless of idle state - a stray bubble must never persist.
                PromptTracker.Sweep();

                if (!OverworldIdle(map))
                {
                    _idleSeconds = 0f;
                    return;
                }

                _idleSeconds += delta;
                if (_idleSeconds < IdleGrace) return;

                // Victory recovery used to live here as idle polling. It is now handled directly by
                // MapVictoryPatch: the start_cr transpiler stops the coroutine dying, and
                // Map.set_CurrentState(Ready) does the completion sweep off the game's own signal.
                ClearStuckPopupFlag();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Map watchdog failed: " + e.Message);
            }
        }

        private static bool OverworldIdle(Map map)
        {
            if (map.CurrentState != Map.State.Ready) return false;
            if (InterruptingPrompt.IsInterrupting()) return false;
            if (SceneLoader.IsInBlurTransition) return false;
            if (MapBasicStartUI.Current != null &&
                MapBasicStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return false;
            if (MapConfirmStartUI.Current != null &&
                MapConfirmStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return false;
            if (MapDifficultySelectStartUI.Current != null &&
                MapDifficultySelectStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive) return false;
            if (MapEventNotification.Current != null && MapEventNotification.Current.showing) return false;

            // A real pause menu or the equip menu means the player paused on purpose. Never treat
            // that as idle, or the watchdog would force-unpause them out of their own menu.
            try
            {
                if (AbstractEquipUI.Current != null &&
                    AbstractEquipUI.Current.CurrentState != AbstractEquipUI.ActiveState.Inactive) return false;
            }
            catch (Exception)
            {
                return false;
            }

            var pauseUIs = UnityEngine.Object.FindObjectsOfType<AbstractPauseGUI>();
            if (pauseUIs != null)
            {
                foreach (var gui in pauseUIs)
                {
                    if (gui != null && gui.state != AbstractPauseGUI.State.Unpaused) return false;
                }
            }

            var npcs = UnityEngine.Object.FindObjectsOfType<MapDialogueInteraction>();
            if (npcs != null)
            {
                foreach (var npc in npcs)
                {
                    if (npc != null && npc.currentlySpeaking) return false;
                }
            }

            return true;
        }

        private static void ClearStuckPopupFlag()
        {
            if (_hasPopupOpenedField == null)
            {
                _hasPopupOpenedField = AccessTools.Field(typeof(AbstractMapInteractiveEntity), "HasPopupOpened");
                if (_hasPopupOpenedField == null) return;
            }

            if (!(bool)_hasPopupOpenedField.GetValue(null)) return;

            _hasPopupOpenedField.SetValue(null, false);
            Plugin.Log.LogInfo("[4P-Fix] Interaction reset: HasPopupOpened was stuck true with the overworld idle - cleared, shops/levels are usable again.");
        }

        internal static string DescribeChalice()
        {
            try
            {
                var flags = PlayerManager.playerWasChalice;
                if (flags == null) return "unknown";

                var s = string.Empty;
                for (var i = 0; i < flags.Length && i < BossTargeting.Slots.Length; i++)
                {
                    if (!flags[i]) continue;
                    if (s.Length > 0) s += ",";
                    s += BossTargeting.Slots[i].ToString();
                }
                return (s.Length > 0) ? s : "none";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    [HarmonyPatch(typeof(AbstractMapInteractiveEntity), "Update")]
    internal static class AbstractMapInteractiveEntityUpdatePatch
    {
        private static void Postfix(AbstractMapInteractiveEntity __instance)
        {
            if (__instance == null) return;
            try
            {
                MapInteraction.TryExtraInteract(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[4P-Fix] Map interaction postfix failed: " + e.Message);
            }
        }
    }

    /// <summary>Reports overworld state once per map load, for diagnosing interaction problems.</summary>
    [HarmonyPatch(typeof(Map), "Start")]
    internal static class MapStartDiagnosticsPatch
    {
        private static void Postfix(Map __instance)
        {
            if (__instance == null) return;
            try
            {
                PromptTracker.Reset();
                RegroupStrandedExtras(__instance);

                Plugin.Log.LogInfo(string.Format(
                    "[4P-Fix] Map loaded: scene={0} state={1} | P1={2} P2={3} P3={4} P4={5} | Multiplayer={6} M2={7} M3={8}",
                    SceneManager.GetActiveScene().name,
                    __instance.CurrentState,
                    Describe(__instance.players, 0), Describe(__instance.players, 1),
                    Describe(__instance.altplayers, 0), Describe(__instance.altplayers, 1),
                    PlayerManager.Multiplayer, PlayerManager.Multiplayer2, PlayerManager.Multiplayer3));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Fix] Map diagnostics failed: " + e.Message);
            }
        }

        private static string Describe(MapPlayerController[] arr, int i)
        {
            if (arr == null || i >= arr.Length || arr[i] == null) return "none";
            return arr[i].id.ToString();
        }

        /// <summary>
        /// Map.CreatePlayers copies playerThreePosition = playerOnePosition, but a Chalice victory
        /// return can leave an extra at a stale coordinate from before the level. If one lands far
        /// from Player One, put it back beside the group.
        /// </summary>
        private static void RegroupStrandedExtras(Map map)
        {
            if (!Plugin.RegroupStrandedExtras.Value) return;

            var main = map.players;
            if (main == null || main.Length == 0 || main[0] == null) return;

            var extras = map.altplayers;
            if (extras == null) return;

            var anchor = main[0].transform.position;
            var limit = Mathf.Max(0.5f, Plugin.MapRegroupDistance.Value);
            var offset = 0.7f;

            foreach (var extra in extras)
            {
                if (extra == null) continue;

                var distance = Vector3.Distance(extra.transform.position, anchor);
                if (distance <= limit) continue;

                extra.transform.position = anchor + new Vector3(offset, offset, 0f);
                offset += 0.7f;

                Plugin.Log.LogInfo(string.Format(
                    "[4P-Fix] Regrouped {0}: was {1:F1} units from Player One (limit {2:F1}) - moved beside the group. Chalice player: {3}",
                    extra.id, distance, limit, MapWatchdog.DescribeChalice()));
            }
        }
    }
}
