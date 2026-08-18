using System;
using UnityEngine;

namespace Cuphead4PFixes
{
    /// <summary>Shared, defensive helpers for reaching the extra players.</summary>
    internal static class Extras
    {
        internal static readonly PlayerId[] ExtraIds = { PlayerId.PlayerThree, PlayerId.PlayerFour };
        internal static readonly PlayerId[] AllIds =
        {
            PlayerId.PlayerOne, PlayerId.PlayerTwo, PlayerId.PlayerThree, PlayerId.PlayerFour
        };

        /// <summary>
        /// PlayerManager.GetPlayer indexes a dictionary that is only populated after
        /// PlayerManager.Awake, and blows up for PlayerId.Any/None. Never let that reach a patch.
        /// </summary>
        internal static AbstractPlayerController GetPlayer(PlayerId id)
        {
            try
            {
                var p = PlayerManager.GetPlayer(id);
                // Unity's fake-null: a destroyed controller compares equal to null.
                return (p == null) ? null : p;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A player that exists, is not dead and whose GameObject is live.</summary>
        internal static AbstractPlayerController GetLivePlayer(PlayerId id)
        {
            var p = GetPlayer(id);
            if (p == null) return null;
            try
            {
                if (p.IsDead) return null;
                if (!p.gameObject.activeInHierarchy) return null;
            }
            catch (Exception)
            {
                return null;
            }
            return p;
        }

        /// <summary>Player 1 if usable, otherwise the first other living player.</summary>
        internal static AbstractPlayerController GetAnchor()
        {
            var anchor = GetLivePlayer(PlayerId.PlayerOne);
            if (anchor != null) return anchor;
            foreach (var id in AllIds)
            {
                var p = GetLivePlayer(id);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>
        /// Applies a sprite tint to whichever concrete player controller this is.
        /// Level and Plane controllers expose separate animation controllers.
        /// </summary>
        internal static void ApplyTint(AbstractPlayerController player, Color color)
        {
            if (player == null) return;
            try
            {
                var level = player as LevelPlayerController;
                if (level != null)
                {
                    var ac = level.animationController;
                    if (ac != null) ac.SetColor(color);
                    return;
                }

                var plane = player as PlanePlayerController;
                if (plane != null)
                {
                    var ac = plane.animationController;
                    if (ac != null) ac.SetColor(color);
                }
            }
            catch (Exception)
            {
                // A controller mid-destruction is not worth a log spam every frame.
            }
        }

        /// <summary>Parses "#RRGGBB"/"RRGGBB"; returns the fallback when the string is unusable.</summary>
        internal static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            var s = hex.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            Color parsed;
            return ColorUtility.TryParseHtmlString(s, out parsed) ? parsed : fallback;
        }
    }
}
