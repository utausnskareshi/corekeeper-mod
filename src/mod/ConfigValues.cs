#nullable enable
using System;
using PugMod;

namespace CustomPlayerSkin
{
    /// <summary>
    /// Mod settings. They are registered with the game's own configuration system
    /// (<see cref="API.Config"/>), which takes care of creating and persisting the settings file.
    /// </summary>
    public sealed class ConfigValues
    {
        private const string Section = "General";

        /// <summary>Key of the reload interval; the game names the setting's file after it.</summary>
        private const string ReloadIntervalKey = "reloadIntervalSeconds";

        /// <summary>The reload interval the setting is registered with.</summary>
        public const int DefaultReloadIntervalSeconds = 2;

        /// <summary>
        /// Where the game keeps the reload interval, relative to the settings folder. The config API
        /// names each file &lt;mod&gt;/&lt;section&gt;-&lt;key&gt;.json (measured on 1.3.0.2), so this is
        /// built from the same section and key the setting is registered with.
        /// </summary>
        public const string ReloadIntervalFile =
            CustomPlayerSkinMod.RootDirectory + Section + "-" + ReloadIntervalKey + ".json";

        // Keys of the hide settings, shared by their registration and by Hidden below, so the file
        // looked for is always the file the game writes for that setting.
        private const string HideHairKey = "hideHair";
        private const string HideEyesKey = "hideEyes";
        private const string HideShirtKey = "hideShirt";
        private const string HidePantsKey = "hidePants";
        private const string HideHelmKey = "hideHelm";
        private const string HideArmorKey = "hideArmor";

        /// <summary>
        /// A hide setting as the mod uses it.
        ///
        /// A setting whose file is gone reads as default(T) on 1.3.0.2, not as its registered
        /// default - what "Remove mod" in the tool leaves while the game runs, until the game starts
        /// again and registers them afresh. A picture placed again in that session was drawn with the
        /// game's hair, eyes and clothes over it. All six are registered as true, so a false one is
        /// believed only while its own file is there; asked only when the value is false. Each is
        /// judged by its own file, because the tool's gear toggle writes four of them back.
        /// </summary>
        internal static bool Hidden(IConfigEntry<bool> entry, string key) =>
            entry.Value || SkinStore.Exists(CustomPlayerSkinMod.RootDirectory + Section + "-" + key + ".json") == FileState.Missing;

        public bool HairHidden => Hidden(HideHair, HideHairKey);
        public bool EyesHidden => Hidden(HideEyes, HideEyesKey);
        public bool ShirtHidden => Hidden(HideShirt, HideShirtKey);
        public bool PantsHidden => Hidden(HidePants, HidePantsKey);
        public bool HelmHidden => Hidden(HideHelm, HideHelmKey);
        public bool ArmorHidden => Hidden(HideArmor, HideArmorKey);

        // init accessors require System.Runtime.CompilerServices.IsExternalInit, which does not
        // exist in the Unity runtime, so private set is used instead.
        public IConfigEntry<bool> LocalPlayerOnly { get; private set; } = default!;
        public IConfigEntry<bool> HideHair { get; private set; } = default!;
        public IConfigEntry<bool> HideEyes { get; private set; } = default!;
        public IConfigEntry<bool> HideShirt { get; private set; } = default!;
        public IConfigEntry<bool> HidePants { get; private set; } = default!;
        public IConfigEntry<bool> HideHelm { get; private set; } = default!;
        public IConfigEntry<bool> HideArmor { get; private set; } = default!;
        public IConfigEntry<int> ReloadIntervalSeconds { get; private set; } = default!;
        public IConfigEntry<bool> CaptureAppearance { get; private set; } = default!;

        public static ConfigValues Register()
        {
            string mod = CustomPlayerSkinMod.ModName;

            return new ConfigValues
            {
                // Each character's image is named after that character's own identifier, under
                // CustomPlayerSkin/skins/. A character with no image there keeps the game's own
                // appearance, which is how "apply to this character only" is expressed.
                LocalPlayerOnly = RegisterOrDefault(
                    mod, Section,
                    "自分の操作キャラクターだけに適用する。無効にすると、同じ画像を持つ他プレイヤーにも適用される。",
                    "localPlayerOnly", true),

                // The whole body is replaced, so overlays such as hair, eyes and clothing are hidden by default.
                // Leaving them on would draw the game's own hair and clothes over the replacement art.
                HideHair = RegisterOrDefault(
                    mod, Section, "髪を非表示にする。", HideHairKey, true),
                HideEyes = RegisterOrDefault(
                    mod, Section, "目を非表示にする。", HideEyesKey, true),
                HideShirt = RegisterOrDefault(
                    mod, Section, "シャツを非表示にする。", HideShirtKey, true),
                HidePants = RegisterOrDefault(
                    mod, Section, "ズボンを非表示にする。", HidePantsKey, true),
                HideHelm = RegisterOrDefault(
                    mod, Section, "装備中の兜を非表示にする。", HideHelmKey, true),
                HideArmor = RegisterOrDefault(
                    mod, Section, "装備中の胴・脚防具を非表示にする。", HideArmorKey, true),

                ReloadIntervalSeconds = RegisterOrDefault(
                    mod, Section,
                    "画像の更新を確認する間隔（秒）。0 以下で自動再読み込みを無効にする。",
                    ReloadIntervalKey, DefaultReloadIntervalSeconds),

                // What the tool's "fetch" button reads. Written only for the character being
                // played here, and only when the picture would differ from the one already saved.
                CaptureAppearance = RegisterOrDefault(
                    mod, Section,
                    "ゲーム内の見た目を書き出す。cks ツールの「取得」で編集画面へ取り込めるようになる。",
                    "captureAppearance", true),
            };
        }

        /// <summary>
        /// Registers one setting, and falls back to its default for this run when the game cannot
        /// read its file.
        ///
        /// The game's Register parses an existing file on the spot and lets a parse failure escape
        /// (no handler in its IL on 1.3.0.3-2aca), and the loader catches what EarlyInit throws but
        /// keeps the mod loaded. So one hand-edited General-*.json the game cannot parse - saved with
        /// a byte order mark, as PowerShell 5.1's Set-Content -Encoding UTF8 and Out-File do, or left
        /// with a trailing comma - stopped the rest from being registered and left the settings unset:
        /// no picture was applied and nothing was captured, on every start, while the log still said
        /// the mod had loaded. Now only that one setting is lost, and it keeps its registered default.
        ///
        /// The file is not rewritten: it is the user's own edit, and once it is fixed the next start
        /// reads it again. Until then that setting does not follow its file, since the fallback never
        /// reads it - including the tool's gear toggle for the four gear settings.
        /// </summary>
        private static IConfigEntry<T> RegisterOrDefault<T>(string mod, string section, string description, string key, T defaultValue)
        {
            try
            {
                return API.Config.Register(mod, section, description, key, defaultValue);
            }
            catch (Exception ex)
            {
                CustomPlayerSkinMod.LogWarning(
                    $"設定ファイル {section}-{key}.json を読めなかったため、この起動では既定値 ({defaultValue}) を使う。" +
                    $"ファイルを直すか削除すると、次の起動から反映される: {ex.Message}");
                return new FixedEntry<T>(defaultValue);
            }
        }

        /// <summary>
        /// A setting held in memory only, standing in for one whose file could not be read.
        /// The game's IConfigEntry has nothing but Value (1.3.0.3-2aca).
        /// </summary>
        private sealed class FixedEntry<T> : IConfigEntry<T>
        {
            public FixedEntry(T value)
            {
                Value = value;
            }

            public T Value { get; set; }
        }
    }
}
