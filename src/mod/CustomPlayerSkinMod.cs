#nullable enable
using System.Collections.Generic;
using PugMod;
using UnityEngine;

namespace CustomPlayerSkin
{
    /// <summary>
    /// Entry point of the mod that replaces a player character's appearance with any image.
    ///
    /// The replacement is re-applied after every <c>PlayerController.RefreshCustomization</c>.
    /// The game rebuilds the appearance from the character settings (DataBlock), so a single
    /// application would be undone by an equipment change or a respawn.
    ///
    /// Each character has its own image, named after that character's identifier and kept under
    /// <c>CustomPlayerSkin/skins/</c>. A character with no image there is left exactly as the
    /// game draws it, which is how "apply to this character only" works.
    ///
    /// Multiplayer assumes every participant has installed the same mod. Only the local player is
    /// replaced by default; the <c>localPlayerOnly</c> setting opens it up to everyone on screen,
    /// and even then a player whose character has no image here keeps their own appearance.
    /// </summary>
    public class CustomPlayerSkinMod : IMod
    {
        public const string ModName = "CustomPlayerSkin";
        /// <summary>
        /// Written to the log as the mod loads, so a log says which build ran. It stood at 0.3.0 from
        /// the first release through every later change, and telling whether a report came from the
        /// build before a fix meant comparing the loader's working copy of the scripts by hand.
        /// Raise it whenever what the mod does changes.
        /// </summary>
        public const string ModVersion = "0.4.1";

        /// <summary>Directory holding the settings and images, relative to the game's mod settings area.</summary>
        public const string RootDirectory = ModName + "/";

        /// <summary>Directory holding one image per character.</summary>
        public const string SkinsDirectory = RootDirectory + "skins/";

        private ConfigValues _config = default!;
        private float _nextReloadCheck;

        /// <summary>Configuration values, also read from the patches.</summary>
        internal static ConfigValues Config { get; private set; } = default!;

        /// <summary>
        /// One character's image as it currently stands on disk.
        ///
        /// A character with no image is cached too, with a null texture, so that a character
        /// being drawn every frame does not mean a filesystem lookup every frame.
        /// </summary>
        private sealed class SkinEntry
        {
            public Texture2D? Texture;

            /// <summary>Modification time of the loaded version; 0 when the file is absent.</summary>
            public long Timestamp;

            /// <summary>Which version is currently failing to load, and how often it has failed.</summary>
            public long FailedTimestamp;
            public int FailureCount;

            /// <summary>
            /// Failures in a row for this character, whatever version they were.
            ///
            /// FailureCount alone gives up only on one version of a file, and starts again the
            /// moment the timestamp moves. A file that keeps being rewritten and keeps failing -
            /// an editor saving over it, a sync client touching it - therefore never reached the
            /// limit, and every attempt wrote two more lines to the log. Reset on a load that
            /// works, so a file that is fixed starts from nothing again.
            /// </summary>
            public int ConsecutiveFailures;

            /// <summary>
            /// When this character was last drawn, so entries for characters that have gone can
            /// be dropped. Without it the dictionary only ever grew: in multiplayer, with
            /// localPlayerOnly turned off, every character ever rendered stayed in it for the
            /// rest of the session, was stat'ed on every poll, and kept its texture resident.
            /// </summary>
            public float LastSeen;
        }

        /// <summary>
        /// How long a character may go without a rebuild before its entry is dropped, unless the
        /// character is still on screen.
        ///
        /// Generous on purpose: re-reading one image is cheap, and an entry that is dropped while
        /// its character is merely off screen costs only that. Whether a character is still drawn
        /// cannot be told from LastSeen alone - it moves only when the game rebuilds a look - so
        /// the characters on screen are looked for when entries expire, and theirs are kept.
        /// </summary>
        private const float EntryLifetimeSeconds = 300f;

        /// <summary>Images by character identifier, in the lower-case form the files are named in.</summary>
        private static readonly Dictionary<string, SkinEntry> Skins = new();

        /// <summary>
        /// Set once the mod is shutting down, after which no image is handed out again.
        ///
        /// Restoring the players goes through the game's own rebuild, which fires the very patch
        /// this mod installs; the patches are still live at that point, because the loader undoes
        /// them only after Shutdown returns. Without this flag the rebuild called back into
        /// <see cref="SkinFor"/>, which loads on demand, so the skin being removed was read off
        /// disk and applied again on the way out.
        /// </summary>
        private static bool _shuttingDown;

        public void EarlyInit()
        {
            // Cleared here, because it is static and the flag outlives one run of this lifecycle.
            // A mod reloaded in the same process came back with everything disarmed and said
            // nothing about why: no skin applied, nothing captured, no warning to explain it.
            _shuttingDown = false;

            // Most of the game is not initialised yet, so only the configuration is loaded here.
            _config = ConfigValues.Register();
            Config = _config;
        }

        public void Init()
        {
            SkinStore.EnsureDirectory(RootDirectory);
            SkinStore.EnsureDirectory(SkinsDirectory);

            Log($"読み込み完了 (v{ModVersion})");
            Log($"  キャラクターごとの画像を <MOD設定フォルダ>/{SkinsDirectory} に置くこと。");
            Log("  ファイル名はキャラクターの識別子。cks ツールの「ゲームへ配置」が用意する。");
        }

        public void Shutdown()
        {
            // Disarmed before anything else. Clearing the dictionary is not enough on its own,
            // because looking a character up loads its image on demand: the restore below would
            // have put the skin straight back and left the shared transparent texture in use
            // after it had been destroyed.
            _shuttingDown = true;

            List<Texture2D> textures = new();
            foreach (KeyValuePair<string, SkinEntry> pair in Skins)
            {
                if (pair.Value.Texture != null)
                {
                    textures.Add(pair.Value.Texture);
                }
            }

            Skins.Clear();

            try
            {
                SkinApplier.RestoreAllPlayers();
            }
            catch (System.Exception ex)
            {
                LogWarning($"終了時の見た目の復帰に失敗した: {ex.Message}");
            }

            SkinApplier.ReleaseSharedResources();

            // Dropped so a reload starts over rather than trusting what a session that has already
            // ended remembered about which captures were written.
            SkinCapture.Reset();

            foreach (Texture2D texture in textures)
            {
                Object.Destroy(texture);
            }

            Log("終了");
        }

        public void ModObjectLoaded(Object obj)
        {
            // This mod ships no asset bundles, so there is nothing to do.
        }

        /// <summary>
        /// Dynamic unloading is refused: there is no clean way to undo the Harmony patches and replaced textures.
        /// </summary>
        public bool CanBeUnloaded() => false;

        /// <summary>
        /// Whether the mod is on its way out.
        ///
        /// Exposed because the capture has to honour it too. Shutdown asks the game to rebuild
        /// every player back to its own appearance, and that rebuild fires this mod's patch while
        /// the patches are still installed - which is what the flag was introduced for. The
        /// capture runs in the same patch, ahead of the applier.
        /// </summary>
        internal static bool IsShuttingDown => _shuttingDown;

        /// <summary>
        /// Whether this mod is currently drawing over this character's appearance.
        ///
        /// Asked by the capture, which must not run while that is true. What it would read back is
        /// this mod's own picture rather than the game's - the applier puts the loaded image into
        /// the body layer through the game's own SetSkin, and puts a transparent texture into every
        /// layer the settings hide. Writing that over the captured file destroyed the one thing the
        /// tool's fetch button exists to read, and left a character with no hair, eyes or clothes.
        ///
        /// Deliberately not <see cref="SkinFor"/>: that loads from disk on demand and stamps
        /// LastSeen, so asking it from the capture path would both read files there and keep an
        /// entry alive that nothing is using.
        /// </summary>
        internal static bool IsReplacing(string guid) =>
            !_shuttingDown
            && Skins.TryGetValue(guid, out SkinEntry entry)
            && entry.Texture != null;

        /// <summary>
        /// The image for one character, or null when that character has none and should therefore
        /// keep the game's own appearance.
        /// </summary>
        internal static Texture2D? SkinFor(string guid)
        {
            if (_shuttingDown)
            {
                return null;
            }

            if (Skins.TryGetValue(guid, out SkinEntry existing))
            {
                existing.LastSeen = Time.unscaledTime;
                return existing.Texture;
            }

            SkinEntry entry = new() { LastSeen = Time.unscaledTime };
            Skins[guid] = entry;

            // First sight of this character. Reporting a missing image once, here, is worth it:
            // it is the only way the log ever says "this character was not set up".
            Reload(guid, entry, logWhenMissing: true);
            return entry.Texture;
        }

        public void Update()
        {
            // This runs inside the game's own update loop, so nothing may escape from here.
            try
            {
                // Ahead of the reload timer, and holding a timer of its own. A capture waiting for
                // a character's artwork to finish loading has nothing to do with how often skins
                // are re-read from disk - and that timer can be switched off altogether, which
                // would have taken the wait with it. Poll returns on its first line almost every
                // frame, and guards itself, so nothing escapes from it either.
                SkinCapture.Poll();

                // The cheap check comes first. This runs every frame, so the setting is only
                // consulted when the timer actually fires rather than on every single frame.
                if (Time.unscaledTime < _nextReloadCheck)
                {
                    return;
                }

                int interval = ReloadInterval();
                if (interval <= 0)
                {
                    // Hot reload is switched off. Look again in a while in case it is switched
                    // back on, instead of asking the setting again on every frame.
                    _nextReloadCheck = Time.unscaledTime + ReloadBackoffSeconds;
                    return;
                }

                _nextReloadCheck = Time.unscaledTime + interval;

                if (ReloadChangedSkins())
                {
                    SkinApplier.RefreshAllPlayers();
                    return;
                }

                // The settings can be changed from outside the game, by the companion tool.
                // They only take effect when the appearance is next rebuilt, which without this
                // would mean "not until you change equipment".
                //
                // Skipped entirely while nothing is applied. Each setting read opens and parses
                // its own file, so this was seven file reads every couple of seconds for the
                // whole session even for someone who had never applied an image to anyone.
                if (AnySkinApplied() && HaveSettingsChanged())
                {
                    Log("表示設定の変更を検知したので再適用する");
                    SkinApplier.RefreshAllPlayers();
                }
            }
            catch (System.Exception ex)
            {
                // Back off for a while: an error that repeats every frame would flood the log.
                _nextReloadCheck = Time.unscaledTime + ReloadBackoffSeconds;
                LogWarning($"更新確認に失敗した（{ReloadBackoffSeconds} 秒後に再試行する）: {ex}");
            }
        }

        /// <summary>
        /// How often to look for changed images, in seconds; 0 or less means not at all.
        ///
        /// A missing settings file does not read as the registered default on 1.3.0.2: the
        /// game's get_Value returns default(T), 0 here. That is the state the tool's "Remove mod"
        /// leaves while the game runs - the images and every setting go together - and 0 meant
        /// "reloading is off", so the removed image stayed on screen, and with the hide settings
        /// reading false as well, the game's own hair and clothes were drawn over it at the next
        /// rebuild. With the file gone the registered default is used, so the removal is noticed
        /// and the original look comes back. Only a missing file falls back: a file saying 0 still
        /// turns reloading off, and the usual poll asks nothing extra.
        /// </summary>
        private int ReloadInterval()
        {
            int interval = _config.ReloadIntervalSeconds.Value;
            if (interval <= 0 && SkinStore.Exists(ConfigValues.ReloadIntervalFile) == FileState.Missing)
            {
                return ConfigValues.DefaultReloadIntervalSeconds;
            }

            return interval;
        }

        /// <summary>How long to wait after a failed poll before trying again.</summary>
        private const float ReloadBackoffSeconds = 30f;

        /// <summary>Path of the image belonging to one character.</summary>
        private static string PathFor(string guid) => SkinsDirectory + guid + ".png";

        /// <summary>
        /// Reloads every character whose image changed on disk.
        ///
        /// Only characters already seen are polled. One that has never been drawn has no cache
        /// entry, and it will read its image the first time it is drawn anyway.
        /// </summary>
        /// <returns>Whether anything changed, meaning the players need rebuilding.</returns>
        private static bool ReloadChangedSkins()
        {
            bool changed = false;
            List<string>? expired = null;
            float now = Time.unscaledTime;

            // The entries are mutated in place; keys are collected here and removed afterwards,
            // because the dictionary must not be modified while it is being walked.
            foreach (KeyValuePair<string, SkinEntry> pair in Skins)
            {
                if (now - pair.Value.LastSeen > EntryLifetimeSeconds)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                    continue;
                }

                // A version that is failing and whose file has since gone counts as a change too.
                // For a character whose picture never loaded, the recorded time stays 0 while a
                // rewritten bad file keeps failing, and a removed file reads 0 as well - so the
                // removal went unseen, the failure count was never reset, and the next bad picture
                // put there said nothing at all.
                if (SkinStore.HasChangedSince(PathFor(pair.Key), pair.Value.Timestamp)
                    || (pair.Value.FailedTimestamp != 0 && SkinStore.GetTimestamp(PathFor(pair.Key)) == 0))
                {
                    // Only a reload that actually produced a different texture is worth a
                    // rebuild. Counting every attempt meant a file that kept changing and kept
                    // failing to load rebuilt every player's appearance on every poll - sixty
                    // times over in one run - for a picture that never changed at all.
                    Texture2D? before = pair.Value.Texture;

                    Reload(pair.Key, pair.Value, logWhenMissing: false);

                    if (!ReferenceEquals(before, pair.Value.Texture))
                    {
                        changed = true;
                    }
                }
            }

            if (expired is not null)
            {
                // Characters still on screen keep their entries. LastSeen moves only when the
                // game rebuilds a look, and a character standing in the same equipment never gets
                // one, so the character being played went "unseen" every five minutes while drawn
                // the whole time. Dropping it rebuilt every player, re-read and re-decoded the same
                // image - the ten "画像を読み込んだ" lines one real session on 1.3.0.2 logged - and
                // for a character with no image said "no image" again every five minutes. Asked
                // once, only when something has expired, with the identity the entries are keyed by.
                HashSet<string>? onScreen = null;
                foreach (PlayerController player in Object.FindObjectsByType<PlayerController>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (player == null)
                    {
                        continue;
                    }

                    string? id = CharacterIdentity.For(player);
                    if (id != null)
                    {
                        (onScreen ??= new HashSet<string>()).Add(id);
                    }
                }

                foreach (string guid in expired)
                {
                    if (Skins.TryGetValue(guid, out SkinEntry entry))
                    {
                        if (onScreen != null && onScreen.Contains(guid))
                        {
                            entry.LastSeen = now;
                            continue;
                        }

                        // Dropping an entry that still has a texture has to be followed by a
                        // rebuild. LastSeen is only touched by SkinFor, and SkinFor is reached
                        // from the equipment-changed and respawn hooks - not from anything that
                        // runs every frame - so a character standing still goes "unseen" while
                        // being drawn the whole time. Destroying its texture then left the
                        // material still set to use a replacement that no longer existed, and
                        // the body rendered white until something else forced a rebuild.
                        //
                        // Marking it changed sends it through the same path a deleted image
                        // takes, which re-reads the file and puts the character back as it was.
                        //
                        // An entry with no texture needs the rebuild as well. It is how a character
                        // with no image yet is watched for one, and dropping it quietly left
                        // nothing to poll: an image placed after five minutes of drawing in the
                        // tool, with the game left running as the tool says it can be, was never
                        // picked up until the character changed equipment or respawned. The
                        // rebuild puts the entry back for every character still on screen.
                        changed = true;

                        Discard(entry);
                        Skins.Remove(guid);
                    }
                }
            }

            return changed;
        }

        /// <summary>Whether any character currently has an image applied.</summary>
        private static bool AnySkinApplied()
        {
            foreach (KeyValuePair<string, SkinEntry> pair in Skins)
            {
                if (pair.Value.Texture != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The settings as they were when the appearance was last built.</summary>
        private string? _lastSettings;

        /// <summary>
        /// Whether any setting differs from the last time the appearance was built.
        ///
        /// Reading them is a file read each, which is why this only runs on the poll rather than
        /// every frame. The values are compared as one string so that a single check covers all
        /// of them.
        /// </summary>
        private bool HaveSettingsChanged()
        {
            string current =
                (_config.LocalPlayerOnly.Value ? "1" : "0") +
                // The values Apply uses, so a setting written back after its file was gone is noticed
                (_config.HairHidden ? "1" : "0") +
                (_config.EyesHidden ? "1" : "0") +
                (_config.ShirtHidden ? "1" : "0") +
                (_config.PantsHidden ? "1" : "0") +
                (_config.HelmHidden ? "1" : "0") +
                (_config.ArmorHidden ? "1" : "0");

            if (current == _lastSettings)
            {
                return false;
            }

            bool changed = _lastSettings != null;
            _lastSettings = current;
            return changed;
        }

        /// <summary>How many times one version of an image is retried before it is given up on.</summary>
        private const int MaxLoadAttempts = 3;

        /// <summary>
        /// How many failures in a row for one character are worth writing to the log.
        ///
        /// Past this the file is being rewritten and still failing, and there is nothing new to
        /// say: a run against a file touched on every poll wrote over sixty warnings.
        /// </summary>
        private const int MaxTotalLoadFailures = 6;

        /// <summary>
        /// Reloads one character's image.
        ///
        /// Distinguishes "the file is gone" from "the file exists but cannot be read".
        /// The former stops replacing and reverts that character; the latter keeps the previous
        /// appearance, so that catching a half-written file mid-edit does not blank the character.
        /// </summary>
        private static void Reload(string guid, SkinEntry entry, bool logWhenMissing)
        {
            string path = PathFor(guid);
            long timestamp = SkinStore.GetTimestamp(path);

            FileState state = SkinStore.Exists(path);
            if (state == FileState.Unknown)
            {
                // Could not tell. Keep the current look and retry at the next poll,
                // which is why the timestamp is deliberately left untouched.
                LogWarning($"画像の状態を確認できなかったため、次回の確認まで現状を維持する: {path}");
                return;
            }

            if (state == FileState.Missing)
            {
                if (logWhenMissing)
                {
                    Log($"このキャラクター用の画像が無いためバニラの見た目のままにする: <MOD設定フォルダ>/{path}");
                }

                entry.Timestamp = timestamp;

                // A picture put back after this is a new one and is reported afresh. Without it, a
                // bad file placed after a long run of failures would say nothing at all, now that
                // the reasons below are capped as well.
                entry.ConsecutiveFailures = 0;

                // Forgotten with the file, so the poll stops sending this entry here every time
                entry.FailedTimestamp = 0;
                Discard(entry);
                return;
            }

            // The reasons are capped with this method's own lines: past the limit a file that is
            // rewritten and still failing has nothing new to say, and SkinStore wrote one on every poll
            Texture2D? loaded = SkinStore.LoadTexture(path, logFailures: entry.ConsecutiveFailures < MaxTotalLoadFailures);
            if (loaded == null)
            {
                // A file caught mid-write has to be retried, so the timestamp is not recorded
                // straight away. A file that is simply not a valid PNG would otherwise be
                // retried forever, logging and rebuilding every player's appearance on every
                // poll, so after a few attempts this version is accepted as bad and skipped
                // until the file changes again.
                if (timestamp == entry.FailedTimestamp)
                {
                    entry.FailureCount++;
                }
                else
                {
                    entry.FailedTimestamp = timestamp;
                    entry.FailureCount = 1;
                }

                entry.ConsecutiveFailures++;

                if (entry.FailureCount >= MaxLoadAttempts)
                {
                    entry.Timestamp = timestamp;

                    // Said once. Past the limit the file is being rewritten and still failing,
                    // and repeating this every poll only buries the rest of the log.
                    if (entry.ConsecutiveFailures <= MaxTotalLoadFailures)
                    {
                        LogWarning(
                            $"画像を {MaxLoadAttempts} 回読み込めなかったため、この画像の再試行をやめる: {path}");
                        LogWarning("  cks ツールで作り直すか、正しい PNG を置き直すこと。");
                    }

                    return;
                }

                if (entry.ConsecutiveFailures <= MaxTotalLoadFailures)
                {
                    LogWarning($"画像を読み込めなかったため、直前の見た目を維持する: {path}");
                }

                return;
            }

            if (entry.Texture != null)
            {
                Object.Destroy(entry.Texture);
            }

            entry.Texture = loaded;
            entry.Timestamp = timestamp;
            entry.FailedTimestamp = 0;
            entry.FailureCount = 0;
            entry.ConsecutiveFailures = 0;
            Log($"画像を読み込んだ: {path} ({loaded.width}x{loaded.height})");
        }

        /// <summary>Drops one character's image so the game's own appearance is used again.</summary>
        private static void Discard(SkinEntry entry)
        {
            if (entry.Texture == null)
            {
                return;
            }

            Object.Destroy(entry.Texture);
            entry.Texture = null;
        }

        internal static void Log(string message) => Debug.Log($"[{ModName}] {message}");

        internal static void LogWarning(string message) => Debug.LogWarning($"[{ModName}] {message}");
    }
}
