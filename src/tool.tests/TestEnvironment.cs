using System.Runtime.CompilerServices;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Keeps every test in this assembly away from the real player's log.
///
/// Placing a picture from the command line reads the game's log to say whether the game took the
/// mod. Without this the answer would come from whatever the person running the tests last did in
/// the game, and would change each time they started it. A path that does not exist reads as "no
/// log"; a test that needs one writes its own and points the variable at it.
///
/// A module initializer, so it is in place before any test can reach the command line.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable(
            GameLocator.PlayerLogOverrideVariable,
            Path.Combine(Path.GetTempPath(), $"cks-test-no-player-log-{Guid.NewGuid():N}", "Player.log"));
}
