#nullable enable
using System;
using System.Text;
using PugMod;
using UnityEngine;

namespace CksSkinExport
{
    /// <summary>
    /// Entry point of a skin mod written by Core Keeper Skin Maker (cks) for mod.io.
    ///
    /// The picture travels inside the mod: Skin/skin.png next to these scripts, read through the
    /// loader's own LoadedMod.GetFile, so nothing has to be placed anywhere by hand. Skin/settings.txt
    /// says whether the game's own clothes and armour are drawn over the picture.
    ///
    /// The picture goes on the local player only - whichever character is being played - and never
    /// on anyone else on screen. Two rules keep it predictable when other skins are around:
    /// a character the cks tool has applied a picture to keeps that picture (see
    /// <see cref="CksSkinApplier"/>), and when several skins made with cks are enabled at once, only
    /// the first in the load order is used.
    ///
    /// The scripts are the same in every mod the tool writes; only the files under Skin/,
    /// ModManifest.json and NOTICE.txt differ.
    /// </summary>
    public class CksSkinMod : IMod
    {
        /// <summary>
        /// Raised whenever what these scripts do changes, and written to the log as they load, so
        /// a report says which build of the scripts was running.
        /// </summary>
        public const string ScriptVersion = "1.0.0";

        /// <summary>
        /// How every mod the tool writes is named. The tool gives each one a name of its own after
        /// this prefix, which is how the skins tell each other apart from other mods.
        /// </summary>
        internal const string NamePrefix = "CksSkin_";

        /// <summary>Name of the mod the cks tool installs into the game itself.</summary>
        internal const string ToolModName = "CustomPlayerSkin";

        private const string PicturePath = "Skin/skin.png";
        private const string SettingsPath = "Skin/settings.txt";

        /// <summary>The picture, or null when this mod is not the one drawing.</summary>
        internal static Texture2D? Picture { get; private set; }

        /// <summary>Whether the game's clothes and armour are drawn over the picture.</summary>
        internal static bool ShowGear { get; private set; }

        /// <summary>Whether the cks tool's own mod is loaded, which decides whether its pictures are looked for.</summary>
        internal static bool ToolModLoaded { get; private set; }

        /// <summary>This mod's name, which starts every line it writes to the log.</summary>
        internal static string LogName { get; private set; } = "CksSkin";

        /// <summary>
        /// Set once the mod is shutting down, after which nothing is applied again.
        ///
        /// Putting the players back goes through the game's own rebuild, which fires this mod's
        /// patch while it is still installed; without the flag the picture went straight back on.
        /// </summary>
        internal static bool IsShuttingDown { get; private set; }

        public void EarlyInit()
        {
            // Static, so cleared here: a mod loaded again in the same process starts from nothing
            IsShuttingDown = false;
            Picture = null;
            ShowGear = false;
            ToolModLoaded = false;

            LoadedMod? self = null;
            LoadedMod? firstSkin = null;
            foreach (LoadedMod mod in API.ModLoader.LoadedMods)
            {
                if (mod == null)
                {
                    continue;
                }

                if (mod.Handlers != null && mod.Handlers.Contains(this))
                {
                    self = mod;
                }

                string name = mod.Metadata.name ?? string.Empty;
                if (firstSkin == null && name.StartsWith(NamePrefix, StringComparison.Ordinal))
                {
                    firstSkin = mod;
                }

                if (name == ToolModName)
                {
                    ToolModLoaded = true;
                }
            }

            if (self == null)
            {
                Debug.LogWarning("[CksSkin] Could not find this mod among the loaded mods, so no picture is applied.");
                return;
            }

            LogName = self.Metadata.name ?? LogName;
            string display = string.IsNullOrEmpty(self.Metadata.displayName) ? LogName : self.Metadata.displayName;

            // Only one picture can be on the player, and every skin from the tool aims at the same
            // one. The load order decides, so the answer is the same in every one of them.
            if (firstSkin != null && firstSkin != self)
            {
                string first = firstSkin.Metadata.displayName is { Length: > 0 } shown
                    ? shown
                    : firstSkin.Metadata.name ?? "?";
                Log($"Not applied: \"{first}\" is another skin made with cks and comes first in the load order. " +
                    "Enable only one of them to use this one.");
                return;
            }

            Settings settings = ReadSettings(self);
            ShowGear = settings.ShowGear;

            Texture2D? picture = LoadPicture(self);
            if (picture == null)
            {
                return;
            }

            Picture = picture;
            Log($"Loaded \"{display}\" {settings.Version} (scripts {ScriptVersion}): " +
                $"{picture.width}x{picture.height}, clothes and armour {(ShowGear ? "shown" : "hidden")}.");
        }

        public void Init()
        {
        }

        public void Shutdown()
        {
            // Disarmed before the players are rebuilt, which fires this mod's own patch
            IsShuttingDown = true;

            try
            {
                CksSkinApplier.RestoreAllPlayers();
            }
            catch (Exception ex)
            {
                LogWarning($"Could not restore the players' own appearance while shutting down: {ex.Message}");
            }

            CksSkinApplier.ReleaseSharedResources();

            if (Picture != null)
            {
                UnityEngine.Object.Destroy(Picture);
                Picture = null;
            }
        }

        public void ModObjectLoaded(UnityEngine.Object obj)
        {
            // This mod ships no asset bundles
        }

        /// <summary>Refused: there is no clean way to take back the patches and the replaced textures.</summary>
        public bool CanBeUnloaded() => false;

        public void Update()
        {
        }

        /// <summary>What Skin/settings.txt says.</summary>
        private struct Settings
        {
            public bool ShowGear;
            public string Version;
        }

        /// <summary>
        /// Reads Skin/settings.txt: one key=value per line, '#' starting a comment.
        ///
        /// A plain format on purpose, read with nothing but string handling, so there is no
        /// serializer between the file and the one switch it carries. A missing or unreadable file
        /// leaves the clothes and armour hidden, which is the tool's own default.
        /// </summary>
        private static Settings ReadSettings(LoadedMod self)
        {
            Settings settings = new() { ShowGear = false, Version = "?" };

            byte[]? bytes = ReadFile(self, SettingsPath);
            if (bytes == null)
            {
                LogWarning($"{SettingsPath} is missing, so clothes and armour stay hidden.");
                return settings;
            }

            foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();

                if (key == "showGear")
                {
                    if (value == "true")
                    {
                        settings.ShowGear = true;
                    }
                    else if (value != "false")
                    {
                        LogWarning($"showGear in {SettingsPath} is \"{value}\", not true or false, so clothes and armour stay hidden.");
                    }
                }
                else if (key == "version" && value.Length > 0)
                {
                    settings.Version = value;
                }
            }

            return settings;
        }

        /// <summary>Loads Skin/skin.png as a texture set up for pixel art, or null when it cannot be read.</summary>
        private static Texture2D? LoadPicture(LoadedMod self)
        {
            byte[]? bytes = ReadFile(self, PicturePath);
            if (bytes == null || bytes.Length == 0)
            {
                LogWarning($"{PicturePath} is missing or empty, so no picture is applied.");
                return null;
            }

            Texture2D? texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            try
            {
                if (!texture.LoadImage(bytes))
                {
                    LogWarning($"{PicturePath} could not be read as a PNG, so no picture is applied.");
                    return null;
                }

                // Set again after loading, because LoadImage resets them
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;

                // Named after this mod (CksSkin_...): the cks tool's own mod reads the name to tell
                // that this picture is on the character, and leaves its capture alone meanwhile
                texture.name = LogName;
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                if (texture.width != CksSkinApplier.ExpectedWidth || texture.height != CksSkinApplier.ExpectedHeight)
                {
                    LogWarning($"The picture is {texture.width}x{texture.height}, not the " +
                               $"{CksSkinApplier.ExpectedWidth}x{CksSkinApplier.ExpectedHeight} the game draws from, " +
                               "so the frames will not line up.");
                }

                Texture2D loaded = texture;
                texture = null;
                return loaded;
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        /// <summary>One of this mod's own files, or null when it is missing or cannot be read.</summary>
        private static byte[]? ReadFile(LoadedMod self, string path)
        {
            try
            {
                return self.GetFile(path);
            }
            catch (Exception ex)
            {
                LogWarning($"Could not read {path}: {ex.Message}");
                return null;
            }
        }

        internal static void Log(string message) => Debug.Log($"[{LogName}] {message}");

        internal static void LogWarning(string message) => Debug.LogWarning($"[{LogName}] {message}");
    }
}
