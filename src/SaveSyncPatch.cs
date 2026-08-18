using System;
using System.Reflection;
using HarmonyLib;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Gives P3/P4 the save slot's real coin balance and item unlocks.
    ///
    /// Why they start blank: PlayerData.OnLoaded runs JsonUtility.FromJson&lt;PlayerData&gt;(json),
    /// and JsonUtility leaves fields that are absent from the JSON at their constructor defaults.
    /// A save written before the 4-player conversion has no playerThree/playerFour, so they are
    /// built by `new PlayerInventory()` - money = 0, weapons = { peashot, plane_peashot }.
    ///
    /// Loadouts are deliberately NOT touched here. Character and loadout handling belongs to the
    /// mod, so this class only ever writes `money` and the three unlock lists.
    ///
    /// Wallets are paired: P3 takes Player One's balance, P4 takes Player Two's. Values are assigned
    /// directly rather than raised to a max, which keeps the sync idempotent - and that matters
    /// because PlayerData.Init wipes
    /// _saveFiles and re-runs OnLoaded every time the slot-select screen starts - so this sync has
    /// to be safe to run repeatedly rather than gated to once per slot.
    ///
    /// GetCurrency/AddCurrency are never patched: all seven coin sites in the game already call
    /// AddCurrency once per player, so aliasing the extra players onto P1's wallet would bank every
    /// coin four times.
    /// </summary>
    internal static class SaveSync
    {
        private const int SaveSlotCount = 3;

        private static FieldInfo _inventoriesField;

        private static PlayerData.PlayerInventories GetInventories(PlayerData data)
        {
            if (data == null) return null;
            if (_inventoriesField == null)
            {
                _inventoriesField = AccessTools.Field(typeof(PlayerData), "inventories");
                if (_inventoriesField == null)
                {
                    Plugin.Log.LogError("[4P-Fix] PlayerData.inventories field not found - coin sync cannot run.");
                    return null;
                }
            }
            return _inventoriesField.GetValue(data) as PlayerData.PlayerInventories;
        }

        internal static void SyncAllSlots(string source)
        {
            if (!Plugin.SyncExtrasToSave.Value) return;

            for (var slot = 0; slot < SaveSlotCount; slot++)
            {
                SyncSlot(slot, source);
            }
        }

        internal static void SyncSlot(int slot, string source)
        {
            if (!Plugin.SyncExtrasToSave.Value) return;

            try
            {
                var data = PlayerData.GetDataForSlot(slot);
                if (data == null)
                {
                    Plugin.Log.LogWarning("[4P-Fix] Slot " + slot + ": no PlayerData (" + source + ").");
                    return;
                }

                var inv = GetInventories(data);
                if (inv == null) return;

                var p1 = inv.GetPlayer(PlayerId.PlayerOne);
                var p2 = inv.GetPlayer(PlayerId.PlayerTwo);
                var p3 = inv.GetPlayer(PlayerId.PlayerThree);
                var p4 = inv.GetPlayer(PlayerId.PlayerFour);

                if (p1 == null)
                {
                    Plugin.Log.LogWarning("[4P-Fix] Slot " + slot + ": Player One inventory is null (" + source + ").");
                    return;
                }

                // Paired wallets: P3 mirrors Player One, P4 mirrors Player Two. If this save has no
                // Player Two data at all, fall back to P1 so P4 is never left at zero.
                var targetP3 = p1.money;
                var targetP4 = (p2 != null) ? p2.money : p1.money;

                var beforeP3 = (p3 != null) ? p3.money : -1;
                var beforeP4 = (p4 != null) ? p4.money : -1;

                if (p3 != null) p3.money = targetP3;
                if (p4 != null) p4.money = targetP4;

                var mergedP3 = (p3 != null) ? MergeUnlocks(p1, p2, p3) : 0;
                var mergedP4 = (p4 != null) ? MergeUnlocks(p1, p2, p4) : 0;

                Plugin.Log.LogInfo(string.Format(
                    "[4P-Fix] Slot {0} coin sync ({1}): P1={2} P2={3} | P3(<-P1) {4}->{5} P4(<-P2) {6}->{7} | unlocks merged P3={8} P4={9} | P3 owns {10}w/{11}s/{12}c",
                    slot, source, p1.money, (p2 != null) ? p2.money : -1,
                    beforeP3, (p3 != null) ? p3.money : -1,
                    beforeP4, (p4 != null) ? p4.money : -1,
                    mergedP3, mergedP4,
                    (p3 != null && p3._weapons != null) ? p3._weapons.Count : 0,
                    (p3 != null && p3._supers != null) ? p3._supers.Count : 0,
                    (p3 != null && p3._charms != null) ? p3._charms.Count : 0));

                if (targetP3 == 0 || targetP4 == 0)
                {
                    Plugin.Log.LogWarning("[4P-Fix] Slot " + slot + ": source balance read as 0 (P1=" +
                                          targetP3 + ", P4 source=" + targetP4 +
                                          "). If the shop shows coins for that player, the read is wrong - report this line.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[4P-Fix] SyncSlot(" + slot + ") failed: " + e);
            }
        }

        /// <summary>Adds every weapon/super/charm P1 and P2 own to the target. Returns how many were added.</summary>
        private static int MergeUnlocks(
            PlayerData.PlayerInventory p1,
            PlayerData.PlayerInventory p2,
            PlayerData.PlayerInventory target)
        {
            var added = 0;

            added += MergeFrom(p1, target);
            if (p2 != null) added += MergeFrom(p2, target);

            return added;
        }

        private static int MergeFrom(PlayerData.PlayerInventory from, PlayerData.PlayerInventory to)
        {
            var added = 0;

            if (from._weapons != null && to._weapons != null)
            {
                foreach (var w in from._weapons)
                {
                    if (!to._weapons.Contains(w)) { to._weapons.Add(w); added++; }
                }
            }

            if (from._supers != null && to._supers != null)
            {
                foreach (var s in from._supers)
                {
                    if (!to._supers.Contains(s)) { to._supers.Add(s); added++; }
                }
            }

            if (from._charms != null && to._charms != null)
            {
                foreach (var c in from._charms)
                {
                    if (!to._charms.Contains(c)) { to._charms.Add(c); added++; }
                }
            }

            return added;
        }
    }

    /// <summary>
    /// Every save slot has just been deserialised. This fires again on each PlayerData.Init - the
    /// slot-select screen re-inits and re-loads - so the sync must run every time, not once.
    /// </summary>
    [HarmonyPatch(typeof(PlayerData), "OnLoaded")]
    internal static class PlayerDataOnLoadedPatch
    {
        private static void Postfix()
        {
            if (!PlayerData.Initialized) return;
            SaveSync.SyncAllSlots("OnLoaded");
        }
    }

    /// <summary>Catches a slot being selected or switched after the initial load.</summary>
    [HarmonyPatch(typeof(PlayerData), "set_CurrentSaveFileIndex")]
    internal static class PlayerDataSlotIndexPatch
    {
        private static void Postfix()
        {
            if (!PlayerData.Initialized) return;
            SaveSync.SyncSlot(PlayerData.CurrentSaveFileIndex, "slot select");
        }
    }
}
