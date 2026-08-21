#nullable enable
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
                LocalPlayerOnly = API.Config.Register(
                    mod, Section,
                    "自分の操作キャラクターだけに適用する。無効にすると、同じ画像を持つ他プレイヤーにも適用される。",
                    "localPlayerOnly", true),

                // The whole body is replaced, so overlays such as hair, eyes and clothing are hidden by default.
                // Leaving them on would draw the game's own hair and clothes over the replacement art.
                HideHair = API.Config.Register(
                    mod, Section, "髪を非表示にする。", "hideHair", true),
                HideEyes = API.Config.Register(
                    mod, Section, "目を非表示にする。", "hideEyes", true),
                HideShirt = API.Config.Register(
                    mod, Section, "シャツを非表示にする。", "hideShirt", true),
                HidePants = API.Config.Register(
                    mod, Section, "ズボンを非表示にする。", "hidePants", true),
                HideHelm = API.Config.Register(
                    mod, Section, "装備中の兜を非表示にする。", "hideHelm", true),
                HideArmor = API.Config.Register(
                    mod, Section, "装備中の胴・脚防具を非表示にする。", "hideArmor", true),

                ReloadIntervalSeconds = API.Config.Register(
                    mod, Section,
                    "画像の更新を確認する間隔（秒）。0 以下で自動再読み込みを無効にする。",
                    "reloadIntervalSeconds", 2),

                // What the tool's "fetch" button reads. Written only for the character being
                // played here, and only when the picture would differ from the one already saved.
                CaptureAppearance = API.Config.Register(
                    mod, Section,
                    "ゲーム内の見た目を書き出す。cks ツールの「取得」で編集画面へ取り込めるようになる。",
                    "captureAppearance", true),
            };
        }
    }
}
