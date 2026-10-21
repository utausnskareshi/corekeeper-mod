#nullable enable
using System;
using System.Collections.Generic;
using PugMod;
using UnityEngine;

namespace CksSkinExport
{
    /// <summary>
    /// Puts the picture on the local player's layers, the way the cks tool's own mod does.
    ///
    /// The body layer gets the picture through the game's own SpriteSheetSkin.SetSkin. Hair and
    /// eyes always get a fully transparent texture, since the picture is the whole character; the
    /// shirt, trousers, helmet and armour get one too unless the settings say to show them.
    /// Hiding through SetSkin rather than switching objects off leaves the game's next rebuild
    /// free to put everything back, which is how the original look returns.
    /// </summary>
    internal static class CksSkinApplier
    {
        /// <summary>Size of the sheet the game draws a player from.</summary>
        internal const int ExpectedWidth = 234;
        internal const int ExpectedHeight = 156;

        /// <summary>Length of a character identifier, in hex digits.</summary>
        private const int GuidLength = 32;

        /// <summary>A fully transparent texture used to hide a layer; one shared by every layer.</summary>
        private static Texture2D? _transparent;

        /// <summary>Set once a lookup of the cks tool's own picture has failed, so it is reported once.</summary>
        private static bool _warnedToolLookup;

        /// <summary>Set once the character could not be identified, so it is reported once.</summary>
        private static bool _warnedIdentity;

        /// <summary>
        /// A colour table that maps a colour onto itself, so the body layer's skin-tone table changes
        /// nothing. The picture already holds its final colours. One entry each side: the counts have
        /// to match, or the game warns and ignores it.
        /// </summary>
        private static readonly List<Color> IdentityColours = new() { new Color(1f, 0f, 1f, 1f) };

        /// <summary>
        /// Applies the picture to one player. Expected to run right after the game has rebuilt that
        /// player's appearance.
        /// </summary>
        public static void Apply(PlayerController player)
        {
            if (CksSkinMod.IsShuttingDown || player == null || !player.isLocal)
            {
                return;
            }

            Texture2D? picture = CksSkinMod.Picture;
            if (picture == null || ToolPictureWins())
            {
                return;
            }

            SetLayer(player.bodySkin, picture);

            if (player.skinColorReplacer != null)
            {
                player.skinColorReplacer.SetColorReplacement(IdentityColours, IdentityColours);
            }

            Hide(player.hairSkin);
            Hide(player.hairShadeSkin);
            Hide(player.eyesSkin);

            if (!CksSkinMod.ShowGear)
            {
                Hide(player.shirtSkin);
                Hide(player.pantsSkin);
                Hide(player.helmSkin);
                Hide(player.breastArmorSkin);
                Hide(player.pantsArmorSkin);
            }
        }

        /// <summary>
        /// Whether the cks tool has applied a picture of its own to the character being played.
        ///
        /// That picture was chosen for this one character, while this mod puts the same picture on
        /// whoever is played, so the more particular choice is kept and this mod does nothing. The
        /// tool's mod applies first (this mod's patch runs at a lower priority), so doing nothing
        /// leaves its picture on screen; removing it in the tool brings this one back within seconds,
        /// because the tool's mod rebuilds the players when its picture goes.
        /// </summary>
        private static bool ToolPictureWins()
        {
            if (!CksSkinMod.ToolModLoaded)
            {
                return false;
            }

            string? guid = LocalGuid();
            if (guid == null)
            {
                // The tool's mod cannot tell which character this is either, so it is not drawing
                return false;
            }

            try
            {
                return API.ConfigFilesystem.FileExists(CksSkinMod.ToolModName + "/skins/" + guid + ".png");
            }
            catch (Exception ex)
            {
                if (!_warnedToolLookup)
                {
                    _warnedToolLookup = true;
                    CksSkinMod.LogWarning($"Could not check for a picture from the cks tool, so this one is used: {ex.Message}");
                }

                return false;
            }
        }

        /// <summary>The identifier of the character being played, in the form the cks tool names its pictures.</summary>
        private static string? LocalGuid()
        {
            try
            {
                SaveManager saves = Manager.saves;
                if (saves == null)
                {
                    return null;
                }

                string text = saves.GetCharacterGuid().ToString();
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
            catch (Exception ex)
            {
                if (!_warnedIdentity)
                {
                    _warnedIdentity = true;
                    CksSkinMod.LogWarning($"Could not identify the character being played: {ex.Message}");
                }

                return null;
            }
        }

        /// <summary>
        /// Gives every player on screen their own appearance back, by asking the game to rebuild it.
        /// Called while shutting down, after the mod has been disarmed.
        /// </summary>
        public static void RestoreAllPlayers()
        {
            // Inactive ones are pooled and get rebuilt by the game when they are next used
            foreach (PlayerController player in UnityEngine.Object.FindObjectsByType<PlayerController>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (player == null)
                {
                    continue;
                }

                // One failure must not stop the others
                try
                {
                    player.RefreshCustomization();
                }
                catch (Exception ex)
                {
                    CksSkinMod.LogWarning($"Could not restore a player's own appearance: {ex.Message}");
                }
            }
        }

        public static void ReleaseSharedResources()
        {
            if (_transparent != null)
            {
                UnityEngine.Object.Destroy(_transparent);
                _transparent = null;
            }

            _warnedToolLookup = false;
            _warnedIdentity = false;
        }

        private static void SetLayer(SpriteSheetSkin? layer, Texture2D texture)
        {
            if (layer != null)
            {
                layer.SetSkin(texture);
            }
        }

        private static void Hide(SpriteSheetSkin? layer)
        {
            if (layer != null)
            {
                layer.SetSkin(Transparent());
            }
        }

        private static Texture2D Transparent()
        {
            if (_transparent != null)
            {
                return _transparent;
            }

            // Any UV samples transparent, so one pixel is enough. Named with the prefix the picture
            // has, which the cks tool's own mod reads (see CksSkinMod.LoadPicture)
            _transparent = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "CksSkin_Transparent",
            };
            _transparent.SetPixel(0, 0, Color.clear);
            _transparent.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return _transparent;
        }
    }
}
