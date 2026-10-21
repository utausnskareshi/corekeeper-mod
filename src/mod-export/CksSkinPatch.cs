#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CksSkinExport
{
    /// <summary>
    /// Hooks into <see cref="PlayerController"/>, the same two places the cks tool's own mod uses.
    ///
    /// The game calls RefreshCustomization every time it rebuilds a player's appearance from the
    /// character's settings - equipping something, respawning - so the picture is put back each time.
    ///
    /// Both run at a low priority, after the cks tool's own mod, whatever the load order: a character
    /// it has given a picture keeps that one (see <see cref="CksSkinApplier"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class CksSkinPatch
    {
        /// <summary>Errors already reported, so one that repeats on every rebuild is logged once.</summary>
        private static readonly HashSet<string> ReportedErrors = new();

        /// <summary>Upper bound on the errors remembered, in case something unforeseen varies them.</summary>
        private const int MaxReportedErrors = 64;

        /// <summary>
        /// The player's own spawn callback, named by a literal because the member is protected.
        /// After a game update, check that PlayerController still declares it: if it stops, Harmony
        /// finds no target and drops this whole class, RefreshCustomization included.
        /// </summary>
        private const string OnSpawnMethod = "OnSpawn";

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.RefreshCustomization))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void RefreshCustomization_Postfix(PlayerController __instance)
        {
            SafeApply(__instance, nameof(PlayerController.RefreshCustomization));
        }

        /// <summary>
        /// A safety net for a player that starts being drawn without a rebuild. Typed as object and
        /// tested, so anything else that ever reached it is ignored rather than throwing.
        /// </summary>
        [HarmonyPatch(typeof(PlayerController), OnSpawnMethod)]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void OnSpawn_Postfix(object __instance)
        {
            if (__instance is PlayerController player)
            {
                SafeApply(player, OnSpawnMethod);
            }
        }

        /// <summary>Never lets an exception into the game's own update; losing the picture is enough.</summary>
        private static void SafeApply(PlayerController player, string origin)
        {
            try
            {
                CksSkinApplier.Apply(player);
            }
            catch (Exception ex)
            {
                // Keyed by where and what, not by the message, which can vary on every call
                string key = origin + ":" + ex.GetType().FullName;
                if (ReportedErrors.Count < MaxReportedErrors && ReportedErrors.Add(key))
                {
                    CksSkinMod.LogWarning($"Applying the picture failed in {origin}: {ex}");
                }
            }
        }
    }
}
