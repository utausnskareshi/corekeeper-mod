#nullable enable
using System;
using Unity.Entities;

namespace CustomPlayerSkin
{
    /// <summary>
    /// Works out which saved character a <see cref="PlayerController"/> belongs to.
    ///
    /// The identifier is the one the game writes into the character's save file, which is also
    /// what the companion tool names each image after. Two characters can share a name and a
    /// save slot is reused once a character is deleted, so nothing but this identifier is safe
    /// to decide an appearance by.
    /// </summary>
    internal static class CharacterIdentity
    {
        /// <summary>Length of the identifier the game writes, in hex digits.</summary>
        private const int GuidLength = 32;

        /// <summary>Errors already reported, so a failure every frame does not flood the log.</summary>
        private static bool _warnedLocal;
        private static bool _warnedRemote;

        /// <summary>
        /// The identifier of the character this player is, or null when it cannot be told.
        ///
        /// Null means "leave this player alone": an unknown character must keep the game's own
        /// appearance rather than be given someone else's.
        /// </summary>
        public static string? For(PlayerController player)
        {
            if (player == null)
            {
                return null;
            }

            if (player.isLocal)
            {
                return LocalGuid();
            }

            // Everyone else on screen is another player in multiplayer. Their character lives on
            // their machine, so their identifier has to come from the networked entity instead.
            return CustomPlayerSkinMod.Config.LocalPlayerOnly.Value ? null : RemoteGuid(player);
        }

        /// <summary>The character currently loaded on this machine.</summary>
        private static string? LocalGuid()
        {
            try
            {
                SaveManager saves = Manager.saves;
                if (saves == null)
                {
                    return null;
                }

                return Normalize(saves.GetCharacterGuid().ToString());
            }
            catch (Exception ex)
            {
                if (!_warnedLocal)
                {
                    _warnedLocal = true;
                    CustomPlayerSkinMod.LogWarning(
                        $"操作キャラクターの識別子を取得できなかったため、見た目を差し替えない: {ex.Message}");
                }

                return null;
            }
        }

        /// <summary>
        /// The character another player is using, read from their entity.
        ///
        /// Every step is checked because this runs while the world is being built and torn down,
        /// where the entity can be gone or the component not yet added.
        /// </summary>
        private static string? RemoteGuid(PlayerController player)
        {
            try
            {
                World world = player.world;
                if (world == null || !world.IsCreated)
                {
                    return null;
                }

                Entity entity = player.entity;
                if (entity == Entity.Null)
                {
                    return null;
                }

                EntityManager entities = world.EntityManager;
                if (!entities.Exists(entity) || !entities.HasComponent<CharacterGuidCD>(entity))
                {
                    return null;
                }

                return Normalize(entities.GetComponentData<CharacterGuidCD>(entity).Value.ToString());
            }
            catch (Exception ex)
            {
                if (!_warnedRemote)
                {
                    _warnedRemote = true;
                    CustomPlayerSkinMod.LogWarning(
                        $"他プレイヤーの識別子を取得できなかったため、そのプレイヤーは差し替えない: {ex.Message}");
                }

                return null;
            }
        }

        /// <summary>
        /// Reduces the identifier to the form the image files are named in.
        ///
        /// Only the case is adjusted. Anything that is not the expected 32 hex digits is refused
        /// rather than passed on, because the value goes on to build a file name.
        /// </summary>
        private static string? Normalize(string? text)
        {
            if (text == null || text.Length != GuidLength)
            {
                return null;
            }

            foreach (char c in text)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return null;
                }
            }

            return text.ToLowerInvariant();
        }
    }
}
