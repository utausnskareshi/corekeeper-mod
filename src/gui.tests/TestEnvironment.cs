using System.Runtime.CompilerServices;
using CoreKeeperSkinTool.Gui.Localization;

// The language tests share process-wide state: the Loc singleton and the environment variable
// that redirects the settings file. Running classes in parallel therefore lets one class observe
// or overwrite another's settings, which showed up as an intermittent failure. This assembly is
// small and finishes in well under a second, so serialising it costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Sets up the environment shared by every test in this assembly.
///
/// The language tests switch <see cref="Loc.Current"/> repeatedly, and each switch persists the
/// choice. Without a redirect that would be the real settings file of whoever runs the tests, so
/// a cancelled or failing run could leave the installed application in the wrong language.
/// A module initializer is used because it runs before any test class can touch
/// <see cref="Loc.Instance"/>, whose constructor already reads the stored language.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"cks-test-settings-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(LanguageSettings.DirectoryOverrideVariable, directory);
    }
}
