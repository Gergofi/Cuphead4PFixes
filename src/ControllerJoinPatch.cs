using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Rewired;

namespace Cuphead4PFixes
{
    internal static class ControllerJoin
    {
        private static readonly FieldInfo Slots = AccessTools.Field(typeof(PlayerManager), "playerSlots");
        private static readonly Type SlotType = Slots.FieldType.GetElementType();
        private static readonly FieldInfo State = AccessTools.Field(SlotType, "joinState");
        private static readonly FieldInfo ControllerState = AccessTools.Field(SlotType, "controllerState");
        private static readonly FieldInfo ControllerId = AccessTools.Field(SlotType, "controllerId");
        private static string _lastSnapshot;
        private static int _pressFrame = -1;
        private static string _sampleScene;
        private static readonly JoinPressPolicy Policy = new JoinPressPolicy();
        private static readonly List<JoinControllerPress> Presses = new List<JoinControllerPress>();
        private static readonly Dictionary<int, Joystick> Devices = new Dictionary<int, Joystick>();

        internal static void Reset()
        {
            Policy.Reset();
            _pressFrame = -1;
            Devices.Clear();
            Presses.Clear();
        }

        internal static void SamplePresses()
        {
            if (!ReInput.isReady) { Reset(); return; }
            var frame = UnityEngine.Time.frameCount;
            if (_pressFrame == frame) return;
            _pressFrame = frame;
            if (_sampleScene != JoinGate.ActiveScene)
            {
                Policy.CancelPending();
                _sampleScene = JoinGate.ActiveScene;
            }
            Presses.Clear();
            Devices.Clear();
            foreach (var joystick in ReInput.controllers.Joysticks)
            {
                var press = new JoinControllerPress {
                    Id = joystick.id, ButtonCount = joystick.buttonCount,
                    Assigned = ReInput.controllers.IsJoystickAssigned(joystick),
                    AnyButtonDown = joystick.GetAnyButtonDown(), Connection = joystick
                };
                for (var i = 0; i < joystick.buttonCount && i < 64; i++)
                {
                    if (joystick.GetButtonDown(i)) press.Down |= 1UL << i;
                    if (joystick.GetButton(i)) press.Held |= 1UL << i;
                }
                Presses.Add(press);
                Devices[joystick.id] = joystick;
            }
            Policy.Observe(UnityEngine.Time.realtimeSinceStartup, Presses, (first, second) => {
                if (Plugin.LogControllerJoins.Value)
                    Plugin.Log.LogMessage("[4P-Input] MIRRORED PAIR: frame=" + _pressFrame +
                        " devices=" + first + "/" + second + " (remembered across presses)");
            });
        }

        private static void ReportEcho(PlayerId playerId, int candidate, int owner)
        {
            if (Plugin.LogControllerJoins.Value)
                Plugin.Log.LogMessage("[4P-Input] JOIN ECHO BLOCKED: frame=" + _pressFrame +
                    " requester=" + playerId + " candidate=" + candidate + " linkedOwnedDevice=" + owner +
                    Policy.DescribeInput());
        }

        // The game does not assign the joystick to Rewired until the second
        // press confirms joining. Its slot remembers the ID on the FIRST press,
        // however. Treat that prompt/request as ownership so later slots in the
        // same Update cannot claim the very same still-unassigned controller.
        internal static bool OwnedByOther(int id, int requester)
        {
            var slots = Slots.GetValue(null) as Array;
            if (slots == null) return false;
            for (var i = 0; i < slots.Length && i < 4; i++)
            {
                if (i == requester) continue;
                var slot = slots.GetValue(i);
                var state = Convert.ToInt32(State.GetValue(slot));
                // PlayerSlot.JoinState: PromptDisplayed=1, Requested=2, Joined=3.
                // NotJoining/Leaving release the reservation; no separate cache
                // can leak across cancelling a prompt or resetting a session.
                if (state < 1 || state > 3) continue;
                if (Convert.ToInt32(ControllerState.GetValue(slot)) == 1 &&
                    (int)ControllerId.GetValue(slot) == id) return true;
                var input = PlayerManager.GetPlayerInput((PlayerId)i);
                if (input != null && input.controllers.ContainsController<Joystick>(id)) return true;
            }
            return false;
        }

        internal static Joystick FindJoinController(PlayerId playerId)
        {
            if (!Plugin.FixDuplicateControllerJoin.Value)
                return CupheadInput.CheckForUnconnectedControllerPress();

            SamplePresses();
            var selected = Policy.Select((int)playerId, OwnedByOther, press => !press.Assigned,
                Plugin.FilterMirroredJoinPresses.Value, (candidate, owner) => ReportEcho(playerId, candidate, owner));
            if (selected < 0) return null;
            var joystick = Devices[selected];
            if (Plugin.LogControllerJoins.Value)
            {
                Plugin.Log.LogMessage("[4P-Input] Join candidate: " + playerId +
                    " joystick=" + joystick.id + " name=" + joystick.name + " frame=" + _pressFrame +
                    " pressDown=0x" + Policy.SelectedDown.ToString("X") +
                    " pressHeld=0x" + Policy.SelectedHeld.ToString("X") + Policy.DescribeInput());
            }
            return joystick;
        }

        internal static bool JoinButtonDown(Player input, PlayerId playerId)
        {
            if (input == null) return false;
            if (!Plugin.FixDuplicateControllerJoin.Value) return input.GetAnyButtonDown();
            // Keep P1/P2's keyboard input, but don't let aggregated controller
            // input bypass the delayed decision (including during reconnect).
            if ((int)playerId < 2 && input.GetAnyButtonDown() &&
                input.controllers.Keyboard.GetAnyButtonDown()) return true;
            SamplePresses();
            return Policy.Select((int)playerId, OwnedByOther,
                press => input.controllers.ContainsController<Joystick>(press.Id),
                Plugin.FilterMirroredJoinPresses.Value,
                (candidate, owner) => ReportEcho(playerId, candidate, owner)) >= 0;
        }

        internal static void LogChanges()
        {
            if (!Plugin.LogControllerJoins.Value) return;
            try
            {
                var slots = Slots.GetValue(null) as Array;
                if (slots == null) return;
                var text = new StringBuilder();
                foreach (var joystick in ReInput.controllers.Joysticks)
                    text.Append(" device{").Append(joystick.id).Append(":").Append(joystick.name)
                        .Append(" system=").Append(joystick.systemId)
                        .Append(" instance=").Append(joystick.deviceInstanceGuid).Append("}");
                for (var i = 0; i < slots.Length && i < 4; i++)
                {
                    var slot = slots.GetValue(i);
                    var input = PlayerManager.GetPlayerInput((PlayerId)i);
                    text.Append(" P").Append(i + 1).Append("{").Append(State.GetValue(slot))
                        .Append(" deviceState=").Append(ControllerState.GetValue(slot))
                        .Append(" rememberedId=").Append(ControllerId.GetValue(slot))
                        .Append(" joysticks=[");
                    if (input != null)
                        foreach (var joystick in input.controllers.Joysticks)
                            text.Append(joystick.id).Append(":").Append(joystick.name).Append(";");
                    text.Append("]}");
                }
                var snapshot = text.ToString();
                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;
                Plugin.Log.LogMessage("[4P-Input] " + JoinGate.ActiveScene + snapshot);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[4P-Input] Could not read join state: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "Update")]
    internal static class ControllerJoinUpdatePatch
    {
        private static int LocalIndex(CodeInstruction code)
        {
            if (code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Stloc_0) return 0;
            if (code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Stloc_1) return 1;
            if (code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Stloc_2) return 2;
            if (code.opcode == OpCodes.Ldloc_3 || code.opcode == OpCodes.Stloc_3) return 3;
            var local = code.operand as LocalBuilder;
            return local != null ? local.LocalIndex : Convert.ToInt32(code.operand);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!PatchChecks(codes))
                Plugin.Log.LogError("[4P-Input] PATCH NOT APPLIED: expected two controller and two button checks in PlayerManager.Update.");
            else
                Plugin.Log.LogMessage("[4P-Input] PATCH APPLIED: controller reservations, delayed joins, remembered mirror pairs and independent P3/P4 input.");
            return codes;
        }

        internal static bool PatchChecks(List<CodeInstruction> codes)
        {
            var getInput = AccessTools.Method(typeof(PlayerManager), "GetPlayerInput");
            var findController = AccessTools.Method(typeof(CupheadInput), "CheckForUnconnectedControllerPress");
            var anyButton = AccessTools.Method(typeof(Player), "GetAnyButtonDown", Type.EmptyTypes);
            var playerIds = new Dictionary<int, CodeInstruction>();
            var replacements = new Dictionary<int, CodeInstruction>();
            var controllerChecks = 0;
            var buttonChecks = 0;

            // Resolve the slot argument from the actual local data flow, not
            // hardcoded compiler local indexes. Covers joining and reconnecting.
            for (var i = 1; i + 1 < codes.Count; i++)
                if (codes[i].Calls(getInput) && codes[i - 1].IsLdloc() && codes[i + 1].IsStloc())
                    playerIds[LocalIndex(codes[i + 1])] = codes[i - 1];
            for (var i = 0; i < codes.Count; i++)
            {
                CodeInstruction playerId;
                if (codes[i].Calls(findController) && i + 3 < codes.Count && codes[i + 1].IsStloc() &&
                    codes[i + 2].IsLdloc() && codes[i + 3].Calls(getInput))
                {
                    replacements[i] = codes[i + 2];
                    controllerChecks++;
                }
                else if (codes[i].Calls(anyButton) && i > 0 && codes[i - 1].IsLdloc() &&
                    playerIds.TryGetValue(LocalIndex(codes[i - 1]), out playerId))
                {
                    replacements[i] = playerId;
                    buttonChecks++;
                }
            }
            if (controllerChecks != 2 || buttonChecks != 2) return false;

            for (var i = codes.Count - 1; i >= 0; i--)
            {
                CodeInstruction playerId;
                if (!replacements.TryGetValue(i, out playerId)) continue;
                var argument = new CodeInstruction(playerId.opcode, playerId.operand);
                argument.labels.AddRange(codes[i].labels);
                codes[i].labels.Clear();
                argument.blocks.AddRange(codes[i].blocks);
                codes[i].blocks.Clear();
                var replacement = AccessTools.Method(typeof(ControllerJoin),
                    codes[i].Calls(findController) ? "FindJoinController" : "JoinButtonDown");
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = replacement;
                codes.Insert(i, argument);
            }
            return true;
        }

        private static void Prefix()
        {
            if (Plugin.FixDuplicateControllerJoin.Value) ControllerJoin.SamplePresses();
        }

        private static void Postfix() { ControllerJoin.LogChanges(); }
    }

    [HarmonyPatch(typeof(PlayerManager), "ResetPlayers")]
    internal static class ControllerJoinResetPatch
    {
        private static void Postfix() { ControllerJoin.Reset(); }
    }
}
