using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Imaging;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// What "Save" does when the file chosen is one it should not, or cannot, write.
///
/// Saving onto the picture that was opened replaced it with the sheet for good: the conversion
/// went on working from the copy in memory, so nothing in the session showed the loss, and opening
/// the file again read it as a finished sheet with the import settings out of reach. The command
/// line refuses the same thing (EnsureDifferentFiles), and the help tells people to go back to the
/// original picture to rebuild. A file open in another program failed with .NET's English sentence
/// in the Japanese window, saying nothing about what to do (the test campaign of 2026-09-30).
///
/// The view model looks for the real game as it is built, so its wiring is read off the source;
/// the two decisions it makes are static and run here.
/// </summary>
public sealed class SaveGuardTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cks-save-" + Guid.NewGuid().ToString("N"));

    public SaveGuardTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Loc.Instance.Current = _original;
        Directory.Delete(_directory, recursive: true);
    }

    private static string SaveBody() => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        "public void Save(string path)");

    // ------------------------------------------------------------ onto the picture being converted

    [Fact]
    public void 同じファイルは書き方が違っても同じと判定する()
    {
        string file = Path.Combine(_directory, "art.png");
        File.WriteAllBytes(file, [1]);

        Assert.True(MainViewModel.PointsAtSameFile(file, file.ToUpperInvariant()));
        Assert.True(MainViewModel.PointsAtSameFile(file, Path.Combine(_directory, "sub", "..", "art.png")));
        Assert.False(MainViewModel.PointsAtSameFile(file, Path.Combine(_directory, "Skin.png")));
    }

    [Fact]
    public void リンクを通した書き方も同じファイルと判定する()
    {
        // A picture opened through a junction - a moved library, a synced folder linked back in - and
        // saved by its real path, or the other way round, is one file. Case and ".." alone hold with
        // Path.GetFullPath, so weakening the comparison to that passed every test here; the link is
        // what PathSafety.Normalize follows (the test campaign of 2026-10-01).
        string real = Path.Combine(_directory, "real");
        Directory.CreateDirectory(real);
        string file = Path.Combine(real, "art.png");
        File.WriteAllBytes(file, [1]);

        string link = Path.Combine(_directory, "linked");
        Assert.True(TryCreateJunction(link, real), "接合点を作成できなかった");

        try
        {
            Assert.True(MainViewModel.PointsAtSameFile(file, Path.Combine(link, "art.png")));
            Assert.True(MainViewModel.PointsAtSameFile(Path.Combine(link, "art.png"), file));
            Assert.False(MainViewModel.PointsAtSameFile(file, Path.Combine(link, "other.png")));
        }
        finally
        {
            // The link alone, so the recursive delete in Dispose does not meet it
            Directory.Delete(link);
        }
    }

    /// <summary>Creates a directory junction, the link Windows makes without needing elevation.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void 解決できないパスは同じとみなさず保存側の失敗に任せる()
    {
        // Thrown here, it would come out of Save with no message at all; the write reports it
        Assert.False(MainViewModel.PointsAtSameFile(Path.Combine(_directory, "art.png"), "bad\0name.png"));
    }

    [Fact]
    public void 元の絵への保存は書く前に止めて理由を言う()
    {
        string body = SaveBody();

        int guard = body.IndexOf("HeldSourcePaths()", StringComparison.Ordinal);
        int refusal = body.IndexOf("SetStatus(\"status.saveOverSource\", path);", StringComparison.Ordinal);
        int encode = body.IndexOf("PixelOps.EncodePng(", StringComparison.Ordinal);

        Assert.True(guard >= 0, "Save が開いている元の絵と比べていない");
        Assert.True(refusal > guard && refusal < encode, "元の絵への保存を書く前に断っていない");
        Assert.StartsWith("return;", body[(refusal + "SetStatus(\"status.saveOverSource\", path);".Length)..].TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void 比べるのは変換の元として持っている絵だけ()
    {
        // A sheet opened as a sheet leaves the source picture unset, and saving it back where it
        // came from is how the help says to carry on with the work. Comparing SourcePath on its own
        // would refuse exactly that.
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "private IEnumerable<string> HeldSourcePaths()");

        Assert.Matches(@"_sourceImage is not null && SourcePath is \{ Length: > 0 \}", body);
        Assert.Matches(@"_sideImage is not null && _sidePath is \{ Length: > 0 \}", body);
        Assert.Matches(@"_backImage is not null && _backPath is \{ Length: > 0 \}", body);
    }

    [Theory]
    [InlineData("ja", "別の名前")]
    [InlineData("en", "another name")]
    public void 元の絵への保存を断る文は別の名前を勧める(string language, string advice)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        string text = Loc.Instance.Format("status.saveOverSource", "PATH");

        Assert.Contains("PATH", text, StringComparison.Ordinal);
        Assert.Contains(advice, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ a file open elsewhere

    [Fact]
    public void 他のプログラムが開いているファイルへの保存は共有違反と分かる()
    {
        // The premise, measured: the write opens the file with FileShare.None, so any other open
        // handle refuses it with ERROR_SHARING_VIOLATION
        string file = Path.Combine(_directory, "locked.png");
        File.WriteAllBytes(file, [1]);

        using SKBitmap bitmap = PixelOps.CreateEmpty(1, 1);
        IOException error;
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            error = Assert.ThrowsAny<IOException>(() => PixelOps.EncodePng(bitmap, file));
        }

        Assert.True(MainViewModel.IsSharingViolation(error), $"共有違反と判定されない: HResult=0x{error.HResult:X8}");

        // Other I/O failures keep the message they had
        Assert.False(MainViewModel.IsSharingViolation(new DirectoryNotFoundException("x")));
        Assert.False(MainViewModel.IsSharingViolation(new IOException("x")));
    }

    [Fact]
    public void 共有違反だけを先に受けて他の失敗は今の文のまま()
    {
        string body = SaveBody();

        int inUse = body.IndexOf("catch (IOException ex) when (IsSharingViolation(ex))", StringComparison.Ordinal);
        int other = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);

        Assert.True(inUse >= 0, "共有違反を受ける節が無い");
        Assert.True(other > inUse, "共有違反の節が一般の節より後にある");
        Assert.Contains("SetStatus(\"status.saveFailedInUse\", path);", body[inUse..other], StringComparison.Ordinal);
        Assert.Contains("SetStatus(\"status.saveFailed\", ex);", body[other..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "他のプログラム")]
    [InlineData("en", "another program")]
    public void 使用中のファイルへの保存の失敗は対処を言う(string language, string cause)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        string text = Loc.Instance.Format("status.saveFailedInUse", "PATH");

        Assert.Contains("PATH", text, StringComparison.Ordinal);
        Assert.Contains(cause, text, StringComparison.Ordinal);
        Assert.DoesNotContain("being used by another process", text, StringComparison.Ordinal);
    }
}
