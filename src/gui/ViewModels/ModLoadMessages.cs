using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.ViewModels;

/// <summary>
/// Puts what the game's log says about the mod into words for the environment bar.
///
/// Kept apart from the view model, which looks for the real game as it is built, so the wording
/// can be tested in both languages without touching the user's machine.
/// </summary>
public static class ModLoadMessages
{
    /// <summary>
    /// The line to show under the environment status and the text behind it, or two empty strings
    /// when there is nothing to warn about.
    /// </summary>
    /// <param name="verdict">What the log said.</param>
    /// <param name="updateOffered">
    /// Whether "Update mod" is on screen. The mod in the game then differs from the one this
    /// application carries, and replacing it is the fix to hand; otherwise the installed one is
    /// already the newest there is, and only a newer build of this application can help.
    /// </param>
    public static (string Line, string Detail) Describe(ModLoadVerdict verdict, bool updateOffered)
    {
        // A failure always has a start time - the verdict is "cannot tell" without one - but the
        // line quotes it, and saying nothing beats saying "at an unknown time"
        if (verdict.Failure is not { } failure || verdict.Run?.StartedUtc is not { } started)
        {
            return (string.Empty, string.Empty);
        }

        string when = GameLog.DescribeTime(started);
        string advice = Loc.Instance[updateOffered ? "env.modLoadAdviceUpdate" : "env.modLoadAdvice"];

        // The mod did load when only its patches failed, so "could not load" would be wrong there;
        // what the user sees is the same, though: the picture may never go on
        string line = failure.Kind == ModLoadFailureKind.Patch
            ? Loc.Instance.Format("env.modPatchFailed", when, advice)
            : Loc.Instance.Format("env.modLoadFailed", when, Reason(failure), advice);

        // The lines as the game wrote them, which are English whatever the language, and the file
        // they came from, for anyone who wants the rest
        string detail = string.Join(
            Environment.NewLine,
            [Loc.Instance.Format("env.modLoadLog", verdict.LogPath), .. failure.Details]);

        return (line, detail);
    }

    /// <summary>Why the game did not take the mod, as the phrase the failure line is built around.</summary>
    private static string Reason(ModLoadFailure failure)
    {
        // Every kind but two carries a summary; the loader's reason, and failing that the kind
        // itself, keep the parentheses from ever standing empty
        string summary = failure.Summary ?? failure.Reason ?? failure.Kind.ToString();

        return failure.Kind switch
        {
            ModLoadFailureKind.CodeSecurity => Loc.Instance.Format("env.modLoadRefused", summary),
            ModLoadFailureKind.Compile => Loc.Instance.Format("env.modLoadNotCompiled", summary),
            ModLoadFailureKind.AssetBundle => Loc.Instance["env.modLoadBundle"],
            ModLoadFailureKind.Manifest => Loc.Instance["env.modLoadManifest"],
            _ => Loc.Instance.Format("env.modLoadError", summary),
        };
    }
}
