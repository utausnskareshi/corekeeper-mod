using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui;

/// <summary>What a notice shown before the main window is asking of the user.</summary>
public enum StartupNoticeKind
{
    /// <summary>Nothing can be done about it; the application stops here.</summary>
    Fatal,

    /// <summary>The terms of use, which have to be accepted before going any further.</summary>
    Disclaimer,

    /// <summary>Something worth knowing, which the user may acknowledge and carry on past.</summary>
    Warning,
}

/// <summary>
/// One screen shown before the main window.
///
/// The text is carried in every language rather than just the chosen one. These screens appear
/// before the main window, which is the only place the language can be changed, so the reader
/// could otherwise be shown terms they cannot read with no way to switch. They are therefore
/// written out in all of them at once.
/// </summary>
/// <param name="Kind">What it is asking of the user.</param>
/// <param name="Messages">The body of the notice, one entry per language.</param>
/// <param name="Details">Supporting detail, one entry per language; may be empty.</param>
/// <param name="OfferFolderChoice">
/// Whether to offer pointing at a game folder. Both of the cases that raise a notice about the
/// game are ones a different folder could answer: it may be installed more than once, and only
/// one copy need be the version this build was checked against.
/// </param>
public sealed record StartupNotice(
    StartupNoticeKind Kind,
    IReadOnlyList<LocalizedText> Messages,
    IReadOnlyList<LocalizedText> Details,
    bool OfferFolderChoice = false)
{
    /// <summary>Every language's message run together, for logging and for tests.</summary>
    public string Message => string.Join("\n\n", Messages.Select(m => m.Text));

    /// <summary>Every language's detail run together, for logging and for tests.</summary>
    public string Detail => string.Join("\n\n", Details.Select(m => m.Text));

    /// <summary>Replaces the message with plain text that is not translated, such as a file path.</summary>
    public StartupNotice WithLiteralMessage(string text) =>
        this with { Messages = [.. Messages.Select(m => m with { Text = text })] };
}

/// <summary>
/// Works out what has to be said before the main window opens.
///
/// Only one thing stops the application outright: having no game to write to at all. A version
/// this build has not been checked against is worth a clear warning, but it is the user's call
/// whether to go ahead, so it does not stop them.
/// </summary>
public static class StartupGate
{
    /// <summary>Version recorded when the terms are accepted.</summary>
    public static string ApplicationVersion { get; } =
        typeof(StartupGate).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// The notices to show, in order, before the main window. Empty means open it straight away.
    /// </summary>
    public static IReadOnlyList<StartupNotice> Check()
    {
        List<StartupNotice> notices = [];

        string? gamePath;
        try
        {
            gamePath = GameLocator.FindGameInstallations(GamePathSettings.Load()).FirstOrDefault();
        }
        catch (Exception ex)
        {
            return [new StartupNotice(
                StartupNoticeKind.Fatal,
                Loc.Instance.EveryLanguage("startup.searchFailed"),
                Literal(ex.Message),
                OfferFolderChoice: true)];
        }

        // Without the game there is nowhere to install to and nothing to check against, so this
        // is the one condition the application cannot carry on past.
        if (gamePath is null)
        {
            return [new StartupNotice(
                StartupNoticeKind.Fatal,
                Loc.Instance.EveryLanguage("startup.gameNotFound"),
                Loc.Instance.EveryLanguage("startup.gameNotFoundDetail"),
                OfferFolderChoice: true)];
        }

        // Asked before anything else the user can act on, because it governs using the
        // application at all rather than any one thing it does.
        if (!DisclaimerSettings.IsAccepted)
        {
            notices.Add(new StartupNotice(
                StartupNoticeKind.Disclaimer,
                Loc.Instance.EveryLanguage("startup.disclaimer"),
                Loc.Instance.EveryLanguage("startup.disclaimerDetail")));
        }

        StartupNotice? version = CheckVersion(gamePath);
        if (version is not null)
        {
            notices.Add(version);
        }

        return notices;
    }

    /// <summary>
    /// Compares the installed version with the one this build was checked against.
    /// Null when they agree, which is the case that needs no screen at all.
    /// </summary>
    private static StartupNotice? CheckVersion(string gamePath)
    {
        GameVersionCheck check;
        try
        {
            check = GameVersion.Check(gamePath);
        }
        catch (Exception ex)
        {
            return new StartupNotice(
                StartupNoticeKind.Warning,
                Loc.Instance.EveryLanguage("startup.versionFailed"),
                Literal(ex.Message),
                OfferFolderChoice: true);
        }

        if (check.State == GameVersionState.Supported)
        {
            return null;
        }

        // At the granularity the list is matched on, so a 1.3.0 build is not read as unsupported
        string supported = GameVersion.DescribeSupported(check.Supported);

        // "could not be read" has a translation of its own, so it is resolved per language
        // rather than once. Resolving it once stamped whichever language happened to be
        // selected into every block, putting an English phrase inside the Japanese text.
        object?[] DetailArguments(string code) =>
            [gamePath, check.Version ?? Loc.Instance.InLanguage(code, "startup.versionNone"), supported];

        return new StartupNotice(
            StartupNoticeKind.Warning,
            check.State == GameVersionState.Unsupported
                ? Loc.Instance.EveryLanguage("startup.versionDiffers", supported, check.Version!)
                : Loc.Instance.EveryLanguage("startup.versionUnknown", supported),
            Loc.Instance.EveryLanguageTranslated("startup.versionDetail", DetailArguments),
            OfferFolderChoice: true);
    }

    /// <summary>
    /// Wraps text that is the same in every language, such as a file path or a message from the
    /// system, so it is shown once rather than repeated under each language heading.
    /// </summary>
    private static IReadOnlyList<LocalizedText> Literal(string text) =>
        [new LocalizedText(string.Empty, text)];
}
