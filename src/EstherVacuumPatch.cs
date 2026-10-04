using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Cuphead4PFixes
{
    // Esther adds a VacuumForce to EVERY plane, but only keeps forcePlayer1/2.
    // P3/P4's forces therefore never ramp up or get removed. After the phase 3
    // transition destroys the vacuum transform, their force getter throws
    // inside PlanePlayerMotor.Move before it can apply the player's position.
    internal static class EstherVacuum
    {
        private static readonly FieldInfo ExternalForces =
            AccessTools.Field(typeof(PlanePlayerMotor), "externalForces");

        private static List<PlanePlayerMotor.Force> GetForces(PlayerId id)
        {
            // Include dead/inactive players: their motor survives a revive.
            var player = Extras.GetPlayer(id) as PlanePlayerController;
            if (player == null) return null;
            var motor = player.motor;
            return motor == null ? null : ExternalForces.GetValue(motor) as List<PlanePlayerMotor.Force>;
        }

        internal static int RemoveVacuumForces(List<PlanePlayerMotor.Force> forces)
        {
            // Never evaluate force.force: its Unity target may already be gone.
            // Preserve unrelated wind/knockback forces on the same motor.
            return forces == null ? 0 : forces.RemoveAll(force => force is FlyingCowboyLevelCowboy.VacuumForce);
        }

        internal static void AdvanceVacuumForces(List<PlanePlayerMotor.Force> forces, float delta)
        {
            if (forces == null) return;
            foreach (var force in forces)
            {
                var vacuum = force as FlyingCowboyLevelCowboy.VacuumForce;
                if (vacuum != null) vacuum.UpdateStrength(delta);
            }
        }

        internal static void UpdateExtras()
        {
            if (!Plugin.FixEstherVacuumForces.Value) return;
            // The original Update still advances P1/P2. Do not advance them twice.
            foreach (var id in Extras.ExtraIds)
                AdvanceVacuumForces(GetForces(id), CupheadTime.Delta);
        }

        internal static void Clear(string reason)
        {
            if (!Plugin.FixEstherVacuumForces.Value) return;
            var removed = 0;
            foreach (var id in Extras.AllIds) removed += RemoveVacuumForces(GetForces(id));
            if (removed > 0)
                Plugin.Log.LogMessage("[4P-Esther] Removed " + removed +
                    " leftover vacuum force(s): " + reason + ".");
        }
    }

    [HarmonyPatch(typeof(FlyingCowboyLevelCowboy), "Update")]
    internal static class EstherVacuumUpdatePatch
    {
        private static void Postfix() { EstherVacuum.UpdateExtras(); }
    }

    [HarmonyPatch(typeof(FlyingCowboyLevelCowboy), "endVacuumPullPlayer")]
    internal static class EstherVacuumEndPatch
    {
        // startVacuumPullPlayer calls this first, so repeated attacks and a
        // player joining during a vacuum cannot accumulate extra forces either.
        private static void Postfix() { EstherVacuum.Clear("vacuum ended/restarted"); }
    }

    [HarmonyPatch(typeof(FlyingCowboyLevelCowboy), "OnDisable")]
    internal static class EstherVacuumDisablePatch
    {
        // Also runs when phase3_cr destroys the old boss, or a retry unloads it.
        private static void Postfix() { EstherVacuum.Clear("boss disabled/destroyed"); }
    }
}
