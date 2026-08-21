#nullable enable
using System;
using PugMod;
using UnityEngine;

namespace CustomPlayerSkin
{
    /// <summary>Result of asking whether a file exists.</summary>
    internal enum FileState
    {
        /// <summary>The file is there.</summary>
        Present,

        /// <summary>The file is definitely not there.</summary>
        Missing,

        /// <summary>The question could not be answered; treat as "no change".</summary>
        Unknown,
    }

    /// <summary>
    /// Reading and writing image files.
    ///
    /// <see cref="System.IO"/> is blocked by the mod safety checks, so file access always goes
    /// through <see cref="API.ConfigFilesystem"/>. That needs no relaxation of the checks,
    /// nor does the mod get flagged as one that skipped safety checks.
    /// </summary>
    internal static class SkinStore
    {
        /// <summary>
        /// Writes a file, replacing whatever was there.
        ///
        /// Reported rather than thrown: the caller is a capture that runs on a timer, and a write
        /// that cannot happen is a reason to try again later rather than to stop the game.
        /// </summary>
        /// <returns>True when the bytes were written.</returns>
        public static bool Write(string path, byte[] bytes)
        {
            try
            {
                API.ConfigFilesystem.Write(path, bytes);
                return true;
            }
            catch (Exception ex)
            {
                CustomPlayerSkinMod.LogWarning($"ファイルを書き込めなかった ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Creates the directory if it does not already exist.</summary>
        public static void EnsureDirectory(string path)
        {
            try
            {
                if (!API.ConfigFilesystem.DirectoryExists(path))
                {
                    API.ConfigFilesystem.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                CustomPlayerSkinMod.LogWarning($"ディレクトリを作成できなかった ({path}): {ex.Message}");
            }
        }

        /// <summary>
        /// Whether the file is there.
        ///
        /// A read failure is reported as <see cref="FileState.Unknown"/> rather than as "missing".
        /// The caller treats "missing" as "the user removed the image, restore the vanilla look",
        /// which would be the wrong response to a transient error.
        /// </summary>
        public static FileState Exists(string path)
        {
            try
            {
                return API.ConfigFilesystem.FileExists(path) ? FileState.Present : FileState.Missing;
            }
            catch (Exception ex)
            {
                CustomPlayerSkinMod.LogWarning($"ファイルの存在確認に失敗した ({path}): {ex.Message}");
                return FileState.Unknown;
            }
        }

        /// <summary>Timestamp value meaning "could not be read".</summary>
        public const long UnknownTimestamp = -1;

        /// <summary>
        /// Returns the modification time in ticks, 0 when the file is absent,
        /// or <see cref="UnknownTimestamp"/> when it could not be read.
        /// </summary>
        public static long GetTimestamp(string path)
        {
            try
            {
                return API.ConfigFilesystem.FileExists(path)
                    ? API.ConfigFilesystem.GetFileTime(path).Ticks
                    : 0;
            }
            catch (Exception)
            {
                return UnknownTimestamp;
            }
        }

        /// <summary>
        /// Whether the modification time changed, which drives hot reloading.
        ///
        /// A timestamp that could not be read counts as "unchanged". Reporting a change would
        /// start a reload that is bound to fail, and repeat it at every poll.
        /// </summary>
        public static bool HasChangedSince(string path, long previousTimestamp)
        {
            long current = GetTimestamp(path);
            return current != UnknownTimestamp && current != previousTimestamp;
        }

        /// <summary>
        /// Loads a PNG and returns a texture configured for pixel art: point filtering, no tiling.
        /// Returns null when it cannot be read.
        /// </summary>
        public static Texture2D? LoadTexture(string path)
        {
            try
            {
                if (!API.ConfigFilesystem.FileExists(path))
                {
                    CustomPlayerSkinMod.LogWarning($"読み込み直前に画像が消えていた: {path}");
                    return null;
                }

                byte[] bytes = API.ConfigFilesystem.Read(path);
                if (bytes == null || bytes.Length == 0)
                {
                    CustomPlayerSkinMod.LogWarning($"画像が空だった: {path}");
                    return null;
                }

                Texture2D? texture = null;
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
                    {
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                    };

                    if (!texture.LoadImage(bytes))
                    {
                        CustomPlayerSkinMod.LogWarning($"PNG として読み込めなかった: {path}");
                        UnityEngine.Object.Destroy(texture);
                        return null;
                    }

                    // Re-apply after loading, because LoadImage overwrites these settings
                    texture.filterMode = FilterMode.Point;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                    Texture2D loaded = texture;
                    texture = null;
                    return loaded;
                }
                finally
                {
                    // Anything thrown after the texture exists would otherwise leak it, and this
                    // runs again every reloadIntervalSeconds, so the leak would keep growing.
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                }
            }
            catch (Exception ex)
            {
                CustomPlayerSkinMod.LogWarning($"画像の読み込みに失敗した ({path}): {ex.Message}");
                return null;
            }
        }

    }
}
