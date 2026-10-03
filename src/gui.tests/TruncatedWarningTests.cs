namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that the "this file stopped part-way" warning is written only by a load that actually
/// puts its picture on screen.
///
/// The flag used to be set as soon as the decode finished - before the wait on the build gate,
/// and before the version check that decides whether the load publishes at all. A conversion
/// holding the gate makes that wait seconds long, and anything raising <c>_loadVersion</c> in the
/// meantime sends the waiting load home with its bitmap thrown away: choosing a preset or the
/// random character does exactly that, through <c>SupersedePendingBuild</c>, and neither writes
/// the flag back. What was left was a banner about a file nobody had opened, sitting over a
/// complete picture, with the summary line under it describing that other picture - and the
/// banner is meant to stay until something else is opened, so it never went away on its own.
///
/// Read off the repository's own source, the way <see cref="LoadOrderingTests"/> does and for the
/// same reason: reproducing this needs a conversion still holding the gate when the next click
/// arrives, and the view model takes no seam for arranging that.
/// </summary>
public sealed class TruncatedWarningTests
{
    private const string FrontSignature = "public async Task LoadImageAsync(string path)";

    private const string GateWait = "await _buildGate.WaitAsync();";

    private const string VersionCheck = "if (load != _loadVersion)";

    private const string Publish = "_sourceImage = decoded;";

    private static string LoadBody() => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        FrontSignature);

    /// <summary>The offset of <paramref name="needle"/>, with a message naming it when absent.</summary>
    private static int Offset(string body, string needle)
    {
        int at = body.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"LoadImageAsync に {needle} が無い");
        return at;
    }

    [Fact]
    public void 途中で切れた警告は公開が決まってから立てる()
    {
        // The regression itself. Both the field and the bound property are checked: the banner
        // reads the property and the conversion's summary line reads the field, so either one
        // left behind by a load that never published talks about a picture that is not on screen.
        string body = LoadBody();

        int publish = Offset(body, Publish);

        Assert.True(
            Offset(body, "_truncatedSource = !complete;") > publish,
            "_truncatedSource を公開より前で立てている");

        Assert.True(
            Offset(body, "SourceWasTruncated = !complete;") > publish,
            "SourceWasTruncated を公開より前で立てている");
    }

    [Fact]
    public void 検査の土台として公開はゲートの中で世代を照合している()
    {
        // The check above is worth nothing if the publish itself stops being guarded: with no
        // version check between the gate and the swap, a superseded load would publish anyway
        // and the flag would be right by accident rather than by construction.
        string body = LoadBody();

        int gate = Offset(body, GateWait);
        int publish = Offset(body, Publish);

        int guard = body.IndexOf(VersionCheck, gate, StringComparison.Ordinal);

        Assert.True(guard >= 0 && guard < publish, "ゲートを取ってから公開するまでに世代の照合が無い");
    }
}
