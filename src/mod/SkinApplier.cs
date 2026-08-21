#nullable enable
using UnityEngine;

namespace CustomPlayerSkin
{
    /// <summary>
    /// Applies the loaded image to each layer of <see cref="PlayerController"/>.
    ///
    /// To hide overlay layers such as hair, eyes and clothing, rather than disabling GameObjects
    /// we call SetSkin with a fully transparent texture. That follows the same path the game itself uses,
    /// so a rebuild after an equipment change only needs the same overwrite again, with few side effects.
    /// </summary>
    internal static class SkinApplier
    {
        /// <summary>
        /// Sheet dimensions used by the game. A loaded image that differs triggers a warning, and
        /// the capture in <see cref="SkinCapture"/> measures the layers it reads against these.
        /// </summary>
        internal const int ExpectedWidth = 234;
        internal const int ExpectedHeight = 156;

        /// <summary>A fully transparent texture used to hide a layer; reused across calls.</summary>
        private static Texture2D? _transparent;

        /// <summary>
        /// Dimensions of the texture the last size warning was about.
        ///
        /// Keyed by size rather than by instance id: every hot reload produces a new texture with
        /// a new id, so an id-keyed set would both grow without bound and warn again for what is
        /// really the same mistake.
        /// </summary>
        private static int _warnedWidth = -1;
        private static int _warnedHeight = -1;

        /// <summary>
        /// Replaces the appearance of a single character.
        /// Expected to run immediately after <see cref="PlayerController.RefreshCustomization"/>.
        ///
        /// Which image is used depends on which saved character this player is. A character the
        /// tool was never told to apply to has no image, and is left exactly as the game drew it.
        /// </summary>
        public static void Apply(PlayerController player)
        {
            if (player == null)
            {
                return;
            }

            string? guid = CharacterIdentity.For(player);
            if (guid == null)
            {
                return;
            }

            Texture2D? skin = CustomPlayerSkinMod.SkinFor(guid);
            if (skin == null)
            {
                return;
            }

            WarnIfUnexpectedSize(skin);

            ConfigValues config = CustomPlayerSkinMod.Config;

            // Each setting is read once. This runs for every player on every appearance rebuild,
            // and HideHair alone was being read twice; how much work an IConfigEntry does behind
            // its Value property is not this mod's to assume.
            bool hideHair = config.HideHair.Value;
            bool hideEyes = config.HideEyes.Value;
            bool hideShirt = config.HideShirt.Value;
            bool hidePants = config.HidePants.Value;
            bool hideHelm = config.HideHelm.Value;
            bool hideArmor = config.HideArmor.Value;

            SetLayer(player.bodySkin, skin);

            // Hide the layers that would be drawn over the replacement art
            SetLayerHidden(player.hairSkin, hideHair);
            SetLayerHidden(player.hairShadeSkin, hideHair);
            SetLayerHidden(player.eyesSkin, hideEyes);
            SetLayerHidden(player.shirtSkin, hideShirt);
            SetLayerHidden(player.pantsSkin, hidePants);
            SetLayerHidden(player.helmSkin, hideHelm);
            SetLayerHidden(player.breastArmorSkin, hideArmor);
            SetLayerHidden(player.pantsArmorSkin, hideArmor);
        }

        /// <summary>
        /// Re-applies to every player on screen, used right after the image is hot-reloaded.
        /// Not needed normally, since the RefreshCustomization patch applies it per player.
        /// </summary>
        public static void RefreshAllPlayers() => RebuildAllPlayers("再適用");

        /// <summary>
        /// Stops replacing and returns every player to the game's own appearance.
        ///
        /// Rather than remembering and restoring each layer's original texture,
        /// we call <see cref="PlayerController.RefreshCustomization"/> and let the game rebuild them.
        /// This restores equipment and hairstyle correctly and keeps no state on our side.
        ///
        /// The rebuild fires this mod's own patch, and the patches are still installed while
        /// Shutdown runs, so the applier has to be disarmed before this is called rather than
        /// merely emptied. <c>CustomPlayerSkinMod.Shutdown</c> sets the flag that makes
        /// <c>SkinFor</c> hand out nothing; without it the on-demand load put the skin straight
        /// back on the way out.
        /// </summary>
        public static void RestoreAllPlayers() => RebuildAllPlayers("復帰");

        /// <summary>
        /// Asks the game to rebuild every player's appearance from its own data.
        ///
        /// This is used both to re-apply after a hot reload and to return to the vanilla look,
        /// because the two are the same operation: the game rebuilds each layer, and the
        /// <c>RefreshCustomization</c> patch then applies the replacement if one is loaded.
        /// Applying directly instead would never undo a hidden layer, since hiding writes a
        /// transparent texture and there is nothing on our side to put back.
        /// </summary>
        private static void RebuildAllPlayers(string what)
        {
            // Inactive objects are skipped. The game pools PlayerControllers, and a pooled one
            // still reports the character it last held, so including them meant rebuilding the
            // full set of layers for players who are not on screen at all. An inactive one gets
            // its appearance built by the game when it is next occupied, which fires this mod's
            // patch anyway, so nothing is lost by leaving it alone.
            foreach (PlayerController player in Object.FindObjectsByType<PlayerController>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (player == null)
                {
                    continue;
                }

                // Uninitialised instances can still be present.
                // One failure must not stop the others, nor escape into the game's update loop.
                try
                {
                    player.RefreshCustomization();
                }
                catch (System.Exception ex)
                {
                    CustomPlayerSkinMod.LogWarning($"見た目の{what}に失敗したプレイヤーがいる: {ex.Message}");
                }
            }
        }

        public static void ReleaseSharedResources()
        {
            if (_transparent != null)
            {
                Object.Destroy(_transparent);
                _transparent = null;
            }

            _warnedWidth = -1;
            _warnedHeight = -1;
        }

        private static void SetLayer(SpriteSheetSkin? layer, Texture2D texture)
        {
            if (layer != null)
            {
                layer.SetSkin(texture);
            }
        }

        /// <summary>Applies a transparent texture when the layer is hidden; otherwise does nothing.</summary>
        private static void SetLayerHidden(SpriteSheetSkin? layer, bool hidden)
        {
            if (layer != null && hidden)
            {
                layer.SetSkin(GetTransparentTexture());
            }
        }

        private static Texture2D GetTransparentTexture()
        {
            if (_transparent != null)
            {
                return _transparent;
            }

            // Any UV must sample transparent, so a single pixel is enough
            _transparent = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "CustomPlayerSkin_Transparent",
            };
            _transparent.SetPixel(0, 0, Color.clear);
            _transparent.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return _transparent;
        }

        /// <summary>
        /// An image whose dimensions differ shifts every frame and shows the wrong parts of the art.
        /// Application still proceeds, but a single warning is logged so the cause is visible.
        /// </summary>
        private static void WarnIfUnexpectedSize(Texture2D skin)
        {
            if (skin.width == ExpectedWidth && skin.height == ExpectedHeight)
            {
                return;
            }

            if (skin.width == _warnedWidth && skin.height == _warnedHeight)
            {
                return;
            }

            _warnedWidth = skin.width;
            _warnedHeight = skin.height;

            CustomPlayerSkinMod.LogWarning(
                $"画像の寸法が {skin.width}x{skin.height} で、想定の {ExpectedWidth}x{ExpectedHeight} と違う。" +
                "コマ割りがずれて表示が崩れる。cks ツールで生成し直すこと。");
        }
    }
}
