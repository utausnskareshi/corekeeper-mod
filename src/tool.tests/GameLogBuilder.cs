using System.Globalization;
using System.Text;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Writes a game log the way Core Keeper 1.3.0.2 writes one, for the tests that read it.
///
/// A start that loads the mod follows the real logs of 2026-09-24 and 2026-09-28 line for line.
/// Those contain no failure, so the ways a start fails follow the loader's own messages instead -
/// PugMod.Loader, SideLoader, RoslynCSharp and Trivial.CodeSecurity, read from the game's
/// assemblies - and the refusal of System.Reflection uses the lines a real log showed on
/// 2026-09-22. Unity follows an error with its stack, so the failures carry a few frames of one.
/// Paths and names are made up.
/// </summary>
internal sealed class GameLogBuilder
{
    public const string ModName = "CustomPlayerSkin";

    public const string Version = "1.3.0.2-182b";

    /// <summary>Where the loader keeps its working copies, as Application.temporaryCachePath writes it.</summary>
    private const string TemporaryCache = "C:/Users/player/AppData/Local/Temp/Pugstorm/Core Keeper";

    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Unity's header, which every log starts with.</summary>
    public GameLogBuilder Start(DateTime startedUtc, string gameDirectory, string version = Version)
    {
        _lines.Add("Mono path[0] = 'C:/Games/Core Keeper/CoreKeeper_Data/Managed'");
        _lines.Add("Input System module state changed to: Initialized.");
        _lines.Add("--- Startup info ---");
        _lines.Add($"Time (UTC): {startedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}");
        _lines.Add($"Game version: {version}");
        _lines.Add("Unity version: 6000.0.59f2");
        _lines.Add("OS version: Windows 11  (10.0.26200) 64bit");
        _lines.Add("--- END Startup info ---");
        _lines.Add($"CommandLineArgs.args[0]: {Path.Combine(gameDirectory, "CoreKeeper.exe")}");
        _lines.Add("Initializing Steamworks with AppID 1621690");
        _lines.Add($"CommandLineArgs.args[0]: {Path.Combine(gameDirectory, "CoreKeeper.exe")}");
        return this;
    }

    /// <summary>The side loader finding a mod in StreamingAssets\Mods, in the mix of separators it writes.</summary>
    public GameLogBuilder Discover(string gameDirectory, string mod = ModName)
    {
        string streamingAssets = gameDirectory.Replace('\\', '/') + "/CoreKeeper_Data/StreamingAssets/Mods";
        _lines.Add($"loaded mod {mod} at {streamingAssets}\\{mod}");
        _lines.Add("no mod.io mods loaded User not authenticated. (code: 20100)");
        _lines.Add("steamworkshop loader has been initialized and async mod fetching query task completed");
        return this;
    }

    /// <summary>The loader starting to compile a mod.</summary>
    public GameLogBuilder Compile(string mod = ModName)
    {
        _lines.Add($"Creating modified script files at {TemporaryCache}\\ModLoader\\{mod}");
        return this;
    }

    /// <summary>Everything after the compile of a start that loaded the mod, down to the mod's own greeting.</summary>
    public GameLogBuilder Loaded(string mod = ModName)
    {
        _lines.Add($"Assembly '{mod}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' has passed code security verification");
        _lines.Add($"Successfully compiled {mod} safetyCheck=True");
        _lines.Add($"loading Bundles/{mod}_Windows.assetbundle");
        _lines.Add($"staged mod bundle: {mod}_Windows.assetbundle");
        _lines.Add($"couldn't load Assets/{mod}/{mod}.asmdef from asset bundle Bundles/{mod}_Windows.assetbundle");
        _lines.Add($"Ignoring Bundles/{mod}_Linux.assetbundle: wrong platform");
        _lines.Add("<RI> Initializing input.");
        _lines.Add("Loading data blocks...");
        _lines.Add($"[{mod}] 読み込み完了 (v0.4.0)");
        _lines.Add($"[{mod}]   キャラクターごとの画像を <MOD設定フォルダ>/{mod}/skins/ に置くこと。");
        return this;
    }

    /// <summary>The code check refusing the compiled mod, and the loader giving up on it.</summary>
    public GameLogBuilder RefusedByCodeCheck(string mod = ModName, string refused = "System.Reflection")
    {
        _lines.Add(
            $"Assembly '{mod}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' has failed code security verification. " +
            "Illegal Assembly Reference = '0', Illegal Namespace References = '1', Illegal Type References = '0', " +
            "Illegal Member References = '0', Illegal PInvoke References = '0'");
        Stack("UnityEngine.Debug:LogError (object)");
        _lines.Add($"Illegal reference to disallowed namespace: {refused}");
        _lines.Add($"\tIllegal usage of '{refused}.MethodBase' in method '{mod}.Patches.PlayerControllerPatch::OnSpawn_Postfix'");
        Stack("UnityEngine.Debug:LogError (object)");
        _lines.Add("failed to compile mod, got null");
        Stack("UnityEngine.Debug:LogError (object)");
        _lines.Add($"mod {mod} load error: CompileFailed");
        return this;
    }

    /// <summary>The compiler refusing the mod's source, and the loader giving up on it.</summary>
    public GameLogBuilder DoesNotCompile(string mod = ModName, params (string File, int Line, string Code, string Message)[] errors)
    {
        _lines.Add("__Roslyn Compile Output__");

        foreach ((string file, int line, string code, string message) in errors)
        {
            string path = $"C:\\Users\\player\\AppData\\Local\\Temp\\Pugstorm\\Core Keeper\\ModLoader\\{mod}\\Scripts\\{file}";
            _lines.Add($"{path}({line},17): error {code}: {message}");
            Stack("UnityEngine.Debug:LogError (object)");
        }

        _lines.Add("failed to compile mod, got null");
        Stack("UnityEngine.Debug:LogError (object)");
        _lines.Add($"mod {mod} load error: CompileFailed");
        return this;
    }

    /// <summary>The loader giving up on the mod's scripts for a reason it names and nothing else explains.</summary>
    public GameLogBuilder LoadError(string reason, string mod = ModName)
    {
        _lines.Add($"mod {mod} load error: {reason}");
        return this;
    }

    /// <summary>A compile that worked, with Harmony then throwing while it put the patches in.</summary>
    public GameLogBuilder PatchThrows(string mod = ModName)
    {
        _lines.Add($"Assembly '{mod}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' has passed code security verification");
        _lines.Add($"Successfully compiled {mod} safetyCheck=True");
        _lines.Add($"failed to patch mod {mod}, got exception");
        _lines.Add("HarmonyLib.HarmonyException: Patching exception in method null ---> System.ArgumentException: Undefined target method for patch method static System.Void CustomPlayerSkin.Patches.PlayerControllerPatch::OnSpawn_Postfix(System.Object __instance)");
        Stack("UnityEngine.Debug:LogException (System.Exception)");
        _lines.Add($"loading Bundles/{mod}_Windows.assetbundle");
        _lines.Add($"staged mod bundle: {mod}_Windows.assetbundle");
        _lines.Add($"[{mod}] 読み込み完了 (v0.4.0)");
        return this;
    }

    /// <summary>A compile that worked, with the loader then refusing to patch a type the game protects.</summary>
    public GameLogBuilder PatchRefused(string type, string mod = ModName)
    {
        _lines.Add($"Successfully compiled {mod} safetyCheck=True");
        _lines.Add($"Trying to patch disallowed type {type}");
        _lines.Add($"mod {mod}: patching failed");
        return this;
    }

    /// <summary>A compile that worked, with the mod's asset bundle then failing to load.</summary>
    public GameLogBuilder BundleFails(string mod = ModName)
    {
        _lines.Add($"Successfully compiled {mod} safetyCheck=True");
        _lines.Add($"loading Bundles/{mod}_Windows.assetbundle");
        _lines.Add($"failed to load assetbundle from mod {mod}");
        Stack("UnityEngine.Debug:LogError (object)");
        return this;
    }

    /// <summary>The side loader failing to read a mod's manifest.</summary>
    public GameLogBuilder ManifestUnreadable(string gameDirectory, string mod = ModName)
    {
        string streamingAssets = gameDirectory.Replace('\\', '/') + "/CoreKeeper_Data/StreamingAssets/Mods";
        _lines.Add($"failed to load mod: {streamingAssets}\\{mod}");
        Stack("UnityEngine.Debug:LogError (object)");
        _lines.Add("ArgumentException: JSON parse error: Invalid value.");
        return this;
    }

    /// <summary>Any other line.</summary>
    public GameLogBuilder Line(string text)
    {
        _lines.Add(text);
        return this;
    }

    /// <summary>Writes the log as the game does: UTF-8 without a byte order mark, CRLF.</summary>
    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", _lines) + "\r\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>A few frames of the stack Unity prints after an error.</summary>
    private void Stack(string logCall)
    {
        _lines.Add("0x00007ff8b73c636c (UnityPlayer) UnityMain");
        _lines.Add("0x00007ff8b6a493be (UnityPlayer) ");
        _lines.Add($"0x000001b5ee864e55 (Mono JIT Code) {logCall}");
        _lines.Add("0x00007ff8b6158850 (mono-2.0-bdwgc) mono_runtime_invoke");
        _lines.Add(string.Empty);
    }
}
