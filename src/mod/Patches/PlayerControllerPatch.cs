#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CustomPlayerSkin.Patches
{
    /// <summary>
    /// Hooks into <see cref="PlayerController"/>.
    ///
    /// The game calls <c>RefreshCustomization</c> every time it rebuilds the appearance from the
    /// character settings. Re-applying on every call keeps the replacement in place through
    /// equipment changes, respawns and other players joining.
    ///
    /// Every player on screen goes through here; which of them is actually replaced is decided
    /// in <see cref="SkinApplier.Apply"/>, from the saved character each one belongs to.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerControllerPatch
    {
        /// <summary>Records reported errors so the log is not flooded with duplicates.</summary>
        private static readonly HashSet<string> ReportedErrors = new();

        /// <summary>
        /// Upper bound on distinct error keys kept. The key cannot include the exception message,
        /// because a message carrying a varying value (a coordinate, an object name) would make
        /// every occurrence unique and defeat the whole point of suppressing duplicates.
        /// This cap only guards against an unforeseen source of variety.
        /// </summary>
        private const int MaxReportedErrors = 64;

        /// <summary>
        /// The player's own spawn callback. A literal rather than <c>nameof</c>, because the
        /// member is protected and so cannot be named from here.
        /// </summary>
        private const string OnSpawnMethod = "OnSpawn";

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.RefreshCustomization))]
        [HarmonyPostfix]
        private static void RefreshCustomization_Postfix(PlayerController __instance)
        {
            SafeApply(__instance, nameof(PlayerController.RefreshCustomization));
        }

        /// <summary>
        /// A safety net for paths that start rendering without going through
        /// <c>RefreshCustomization</c>, such as the character creation preview.
        ///
        /// The game's Harmony (0Harmony 2.10.2, measured on 1.3.0.3) looks an attribute's method name
        /// up on the named type only (AccessTools.DeclaredMethod), so this lands on the player's own
        /// <c>OnSpawn</c> override - <c>EntityMonoBehaviour</c> declares one too, but it is not
        /// searched. Should a later version drop the player's override, Harmony finds no target and
        /// throws for this whole class before applying any of its patches: RefreshCustomization goes
        /// unpatched as well and no picture is applied. The loader logs "failed to patch mod", which
        /// the companion tool reports as a patch failure. The name is a literal, so compiling against
        /// the game does not catch it; after a game update, check that PlayerController still
        /// declares OnSpawn. Resolving the method here instead would mean touching
        /// <c>System.Reflection</c> - the mod loader verifies the compiled assembly and rejects the
        /// whole of it for naming that namespace. A test over this source keeps the namespace out.
        /// </summary>
        [HarmonyPatch(typeof(PlayerController), OnSpawnMethod)]
        [HarmonyPostfix]
        private static void OnSpawn_Postfix(object __instance)
        {
            // Typed as object and tested rather than cast: one type test per call, where a
            // PlayerController parameter would throw InvalidCastException for anything else that
            // ever reached this postfix. It stays as a cheap guard; with the game's Harmony only the
            // player's own override is patched (see above).
            if (__instance is PlayerController player)
            {
                SafeApply(player, OnSpawnMethod);
            }
        }

        /// <summary>
        /// Never let an exception propagate into the game.
        /// This runs in the middle of the game's rendering update, so a fault of ours
        /// A fault here must not take the game's own work down with it; losing the look is enough.
        /// </summary>
        private static void SafeApply(PlayerController player, string origin)
        {
            try
            {
                // Before the replacement, not after. The game has just rebuilt the layers from its
                // own data, so this is the one moment the vanilla appearance is what is loaded.
                // Told that this is a rebuild, so the capture drops any back-off it had built up:
                // this is the one moment the appearance can have changed, and an equipment change
                // that arrives during the back-off would otherwise go unseen for up to a minute.
                SkinCapture.Capture(player, rebuilt: true);

                SkinApplier.Apply(player);
            }
            catch (Exception ex)
            {
                // Keyed by origin and exception type only. Including the message would let a
                // message that varies per call log once per call, flooding the very log this
                // is meant to protect.
                string key = origin + ":" + ex.GetType().FullName;
                if (ReportedErrors.Count < MaxReportedErrors && ReportedErrors.Add(key))
                {
                    CustomPlayerSkinMod.LogWarning($"{origin} での適用に失敗した: {ex}");
                }
            }
        }
    }
}
