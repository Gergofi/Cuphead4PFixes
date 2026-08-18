using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>
    /// Drives the two fixes that have to win the last word each frame.
    ///
    /// Issue 3 - offscreen rescue. PlatformingLevel's camera only ever advances
    /// (UpdatePlatforming moves x when <c>position.x &lt; center.x</c>), so a straggler who falls
    /// behind or drops into a pit can end up permanently outside the view with no way back. We
    /// pull any such player to Player 1.
    ///
    /// Issue 4 - tint persistence. Level.Update re-applies the P3/P4 tints, but lighting zones
    /// drive LevelPlayerAnimationController.setColor_cr, and coroutines run *after* Update. The
    /// coroutine therefore wins and the extra players wash back to white. LateUpdate runs after
    /// both, so re-applying here holds under every lighting condition.
    /// </summary>
    internal class FixRunner : MonoBehaviour
    {
        private readonly Dictionary<PlayerId, float> _lastRescue = new Dictionary<PlayerId, float>();
        private UnityEngine.Camera _cachedCamera;
        private CupheadLevelCamera _cachedOwner;

        private void LateUpdate()
        {
            try
            {
                if (Plugin.PersistExtraTints.Value) ApplyTints();
                if (Plugin.RescueOffscreenPlayers.Value) RescueOffscreen();
                MapWatchdog.Tick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("FixRunner.LateUpdate: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- tints

        private void ApplyTints()
        {
            if (Level.Current == null) return;

            var p3 = Extras.GetPlayer(PlayerId.PlayerThree);
            if (p3 != null && !IsChalice(p3))
            {
                Extras.ApplyTint(p3, Extras.ParseColor(Plugin.Player3Color.Value, new Color(0.06f, 1f, 0f)));
            }

            var p4 = Extras.GetPlayer(PlayerId.PlayerFour);
            if (p4 != null && !IsChalice(p4))
            {
                Extras.ApplyTint(p4, Extras.ParseColor(Plugin.Player4Color.Value, new Color(1f, 1f, 0f)));
            }
        }

        private static bool IsChalice(AbstractPlayerController player)
        {
            try
            {
                var stats = player.stats;
                return stats != null && stats.isChalice;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------- rescue

        private UnityEngine.Camera ResolveCamera()
        {
            var owner = CupheadLevelCamera.Current;
            if (owner == null) return null;

            if (_cachedOwner != owner || _cachedCamera == null)
            {
                _cachedOwner = owner;
                _cachedCamera = owner.GetComponent<UnityEngine.Camera>();
                if (_cachedCamera == null)
                {
                    _cachedCamera = owner.GetComponentInChildren<UnityEngine.Camera>();
                }
                if (_cachedCamera == null)
                {
                    _cachedCamera = UnityEngine.Camera.main;
                }
            }
            return _cachedCamera;
        }

        private void RescueOffscreen()
        {
            var level = Level.Current;
            if (level == null) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            var anchor = Extras.GetAnchor();
            if (anchor == null) return;

            var margin = Mathf.Max(0f, Plugin.ViewportMargin.Value);
            var cooldown = Mathf.Max(0.1f, Plugin.RescueCooldown.Value);
            var now = Time.unscaledTime;

            foreach (var id in Extras.AllIds)
            {
                var player = Extras.GetLivePlayer(id);
                if (player == null) continue;
                if (ReferenceEquals(player, anchor)) continue;

                // Don't fight the intro or the win animation.
                if (!player.levelStarted || player.levelEnded) continue;

                float last;
                if (_lastRescue.TryGetValue(id, out last) && now - last < cooldown) continue;

                Vector3 viewport;
                try
                {
                    viewport = cam.WorldToViewportPoint(player.center);
                }
                catch (Exception)
                {
                    continue;
                }

                // Behind the camera plane counts as lost too.
                var lost = viewport.z < 0f
                           || viewport.x < -margin
                           || viewport.y < -margin;

                if (!lost) continue;

                var target = anchor.transform.position;
                // Nudge sideways so the rescued player does not land exactly inside the anchor.
                target.x += (id == PlayerId.PlayerFour) ? 40f : -40f;
                target.y += 30f;

                player.transform.position = target;
                _lastRescue[id] = now;

                Plugin.Log.LogInfo(
                    "Rescued " + id + " from offscreen (viewport " + viewport.x.ToString("F2") + ", " +
                    viewport.y.ToString("F2") + ") -> " + target);
            }
        }
    }
}
