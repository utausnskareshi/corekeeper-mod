using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// "Apply to game" and "Save" while a conversion is queued or running, or a picture is loading.
///
/// Both wrote the document as it stood at the press. A settings change waits 120 ms before it
/// converts and the result replaces the document only when it is done, so a press in between wrote
/// the previous picture: the screen then turned into the new one, the "applied" message was
/// overwritten by the conversion's summary, and the game held a picture nobody was looking at.
/// Measured for every import setting, during a large load (2 to 3 seconds for 4000x6000 and
/// 8192x8192), after a right-facing or back picture came in, and after "rebuild"; about 0.6 s on a
/// 2048x3072 picture (the test campaign of 2026-10-01).
///
/// Read off the source, as LoadOrderingTests are, because the view model looks for the real game
/// as it is built.
/// </summary>
public sealed class BusyApplyTests
{
    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    private static string Window()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "gui")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "gui", "Views", "MainWindow.axaml.cs"));
    }

    [Fact]
    public void 適用と保存のボタンは変換が落ち着いてから書く()
    {
        string window = Window();

        string install = LanguageNotificationTests.MethodBody(window, "private async void OnInstallClicked(");
        Assert.Contains("await Model.InstallToGameWhenSettledAsync()", install, StringComparison.Ordinal);

        string save = LanguageNotificationTests.MethodBody(window, "private async void OnSaveClicked(");
        Assert.Contains("await Model.SaveWhenSettledAsync(path)", save, StringComparison.Ordinal);
        Assert.DoesNotContain("Model.Save(path)", save, StringComparison.Ordinal);
    }

    [Fact]
    public void キャラクターから外すのも変換が落ち着いてからにして知らせを残す()
    {
        // The removal itself was always right; its message was what went. Pressed during a
        // conversion, "restored the original appearance" was overwritten by the conversion's summary
        // as soon as it finished (I5 of the test campaign of 2026-10-01). The characters removed are
        // still the ones confirmed in the dialog.
        string remove = LanguageNotificationTests.MethodBody(Window(), "private async void OnRemoveFromCharactersClicked(");
        Assert.Contains("await Model.RemoveFromCharactersWhenSettledAsync(confirmed)", remove, StringComparison.Ordinal);

        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public async Task RemoveFromCharactersWhenSettledAsync(");
        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        int act = body.IndexOf("RemoveFromCharacters(", StringComparison.Ordinal);
        Assert.True(wait >= 0 && act > wait, "外す前に待っていない");
    }

    [Fact]
    public void 落ち着くまで待ってから今の絵で書く()
    {
        string model = ViewModel();

        foreach ((string signature, string call) in new[]
                 {
                     ("public async Task InstallToGameWhenSettledAsync()", "InstallToGame();"),
                     ("public async Task SaveWhenSettledAsync(string path)", "Save(path);"),
                 })
        {
            string body = LanguageNotificationTests.MethodBody(model, signature);
            int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
            int write = body.IndexOf(call, StringComparison.Ordinal);
            Assert.True(wait >= 0 && write > wait, $"{signature} が待つ前に書いている");
        }
    }

    [Fact]
    public void 落ち着いたかは自分で必ず戻る印だけで判断する()
    {
        // IsBusy is not used: a flag that ever stuck would leave the buttons waiting for good. The
        // queued request clears itself, the gate is released in finally, and the loads count down in
        // finally.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private bool ConversionInFlight");
        Assert.Contains("_rebuildRequestsInFlight", body, StringComparison.Ordinal);
        Assert.Contains("_buildGate.CurrentCount == 0", body, StringComparison.Ordinal);
        Assert.Contains("_loadsInFlight > 0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IsBusy", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 予約した作り直しは変換が終わるまで数え続ける()
    {
        // The queued request clears _pending on a worker thread before the conversion is dispatched
        // and takes the gate, and a check falling in that gap saw nothing on its way: 4 presses in 28
        // still wrote the previous picture with only _pending and the gate to go by. Counted from
        // the moment it is queued until the conversion it starts has finished.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private void RequestRebuild()");

        int up = body.IndexOf("Interlocked.Increment(ref _rebuildRequestsInFlight);", StringComparison.Ordinal);
        int run = body.IndexOf("Task.Run(", StringComparison.Ordinal);
        Assert.True(up >= 0 && run > up, "作り直しの予約を数えるのが Task.Run より前に無い");

        string task = body[run..];
        int rebuild = task.IndexOf("RebuildAsync(discardEdits: false)", StringComparison.Ordinal);
        Match lastFinally = Regex.Matches(task, @"finally\s*\{").Last();
        Assert.True(rebuild >= 0 && lastFinally.Index > rebuild, "作り直しの後の finally が無い");
        Assert.Contains("Interlocked.Decrement(ref _rebuildRequestsInFlight);", task[lastFinally.Index..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public async Task LoadImageAsync(string path)", "\"status.loadFailed\"")]
    [InlineData("private async Task LoadFacingAsync(string? path, bool side)", "\"status.loadFailed\"")]
    [InlineData("private async Task RebuildAsync(", "\"status.convertFailed\"")]
    public void メモリが足りないときは縮小を勧める文を出す(string signature, string generic)
    {
        // Three English sentences reached the status line for one cause - not enough memory for
        // large pictures - and none said to use smaller ones or take the facings out (the test
        // campaign of 2026-10-01). Taken before the general catch.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), signature);

        int memory = body.IndexOf("when (PixelOps.IsAllocationFailure(ex))", StringComparison.Ordinal);
        int general = body.IndexOf(generic, StringComparison.Ordinal);
        Assert.True(memory >= 0 && memory < general, "メモリ不足を一般の失敗より先に受けていない");
        Assert.Contains("\"status.outOfMemory\"", body[memory..general], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public async Task LoadImageAsync(string path)")]
    [InlineData("private async Task LoadFacingAsync(string? path, bool side)")]
    public void 読み込みは数えてから始め必ず数え戻す(string signature)
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), signature);

        int up = body.IndexOf("_loadsInFlight++;", StringComparison.Ordinal);
        int open = body.IndexOf("try", up < 0 ? 0 : up, StringComparison.Ordinal);
        Assert.True(up >= 0 && open > up, "読み込みを数えるのが try より前に無い");

        // The count goes back down in the finally of that same try, whatever happens inside it
        Match lastFinally = Regex.Matches(body, @"finally\s*\{").Last();
        Assert.Contains("_loadsInFlight--;", body[lastFinally.Index..], StringComparison.Ordinal);
    }

    [Fact]
    public void ゲームから取得するのも読み込みとして数える()
    {
        // The fetch reads and composes a captured picture before it reaches the gate, and nothing
        // counted it: "Apply to game" pressed during it wrote the picture from before while the
        // screen turned into the fetched one (the final review of 2026-10-02, RV-S12)
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task FetchFromGame(CharacterChoice? choice)");

        int load = body.IndexOf("++_loadVersion", StringComparison.Ordinal);
        int up = body.IndexOf("_loadsInFlight++;", StringComparison.Ordinal);
        int open = body.IndexOf("try", up < 0 ? 0 : up, StringComparison.Ordinal);
        Assert.True(load >= 0 && up > load && open > up, "取得を数えるのが try より前に無い");

        Match lastFinally = Regex.Matches(body, @"finally\s*\{").Last();
        Assert.Contains("_loadsInFlight--;", body[lastFinally.Index..], StringComparison.Ordinal);
    }

    [Fact]
    public void 落ち着くまで待つのは印が消えるまで繰り返す()
    {
        // The wait itself. With "if" in place of "while" it waited 30 ms once and then wrote the
        // document as it stood, which is the bug back - and every other check here still passed
        // (the final review of 2026-10-02, X48c)
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task WaitUntilSettledAsync()");

        int loop = body.IndexOf("while (ConversionInFlight)", StringComparison.Ordinal);
        Assert.True(loop >= 0, "印が消えるまで繰り返していない");
        Assert.Contains("await Task.Delay(", body[loop..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public async Task InstallToGameWhenSettledAsync()", "InstallToGame();", "\"status.notAppliedAfterWait\"")]
    [InlineData("public async Task SaveWhenSettledAsync(string path)", "Save(path);", "\"status.notSavedAfterWait\"")]
    public void 待っていた絵が届かなければ配置も保存もせず理由を残す(string signature, string write, string note)
    {
        // A load that failed - a file that is not a picture, too little memory - or a rebuild
        // refused over a drawing ended the wait like any other, and the button then wrote the
        // picture from before and said "applied" or "saved" over the message that said why the new
        // one had not come (the final review of 2026-10-02, RV-S11 and the refusal in RV-S1)
        string body = LanguageNotificationTests.MethodBody(ViewModel(), signature);

        int mark = body.IndexOf("int mark = _picturesNotArrived;", StringComparison.Ordinal);
        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        Assert.True(mark >= 0 && wait > mark, "待つ前に届かなかった数を控えていない");

        Match check = Regex.Match(body, @"if \(NotArrivedSince\(mark\) is \{ \} why\)\s*\{(?<branch>[^}]*)\}");
        Assert.True(check.Success && check.Index > wait, "待った後に届かなかったかを見ていない");
        string branch = check.Groups["branch"].Value;
        Assert.Contains("why()", branch, StringComparison.Ordinal);
        Assert.Contains(note, branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.True(body.IndexOf(write, StringComparison.Ordinal) > check.Index + check.Length, $"{write} が届かなかったかを見る前にある");
    }

    [Fact]
    public void 外すのは絵が届かなくても行い届かなかった理由も残す()
    {
        // The removal does not use the picture, and the user confirmed it for these characters, so
        // it is done whatever became of the picture - and the reason the picture did not come is
        // said after the removal's report rather than lost under it
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public async Task RemoveFromCharactersWhenSettledAsync(");

        int mark = body.IndexOf("int mark = _picturesNotArrived;", StringComparison.Ordinal);
        Match removed = Regex.Match(body, @"await WaitUntilSettledAsync\(\);\s*RemoveFromCharacters\(guids\);");
        int check = body.IndexOf("NotArrivedSince(mark) is { } why", StringComparison.Ordinal);
        Assert.True(mark >= 0 && removed.Success && removed.Index > mark, "待った後にそのまま外していない");
        Assert.True(check > removed.Index, "外した後に届かなかった理由を添えていない");
        Assert.Contains("why()", body[check..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public async Task LoadImageAsync(string path)", new[] { "SetStatus(\"status.loadCancelled\");", "SetStatus(ex);", "SetStatus(\"status.outOfMemory\", ex.Message);", "SetStatus(\"status.loadFailed\", ex);" })]
    [InlineData("private async Task LoadAsSheetAsync(", new[] { "SetStatus(\"status.loadCancelledByEdits\");", "SetStatus(ex);" })]
    [InlineData("private async Task LoadFacingAsync(string? path, bool side)", new[] { "SetStatus(\"status.outOfMemory\", ex.Message);", "SetStatus(\"status.loadFailed\", ex);" })]
    [InlineData("private void RequestRebuild()", new[] { "SetStatus(\"status.needRegenerate\");" })]
    [InlineData("private async Task RebuildAsync(", new[] { "SetStatus(\"status.needRegenerate\");", "SetStatus(ex);", "SetStatus(\"status.outOfMemory\", ex.Message);", "SetStatus(\"status.convertFailed\", ex);" })]
    [InlineData("private async Task FetchFromGame(CharacterChoice? choice)", new[] { "SetStatus(() => Loc.Instance.Format(reason, choice.Display));", "SetStatus(ex);", "SetStatus(\"status.fetchFailed\", ex);" })]
    [InlineData("public void ApplyPreset(PresetChoice choice, bool recolourOnly)", new[] { "SetStatus(\"status.starterFailed\", ex);" })]
    [InlineData("public void LoadRandomCharacter()", new[] { "SetStatus(\"status.starterFailed\", ex);" })]
    public void 届かなかった絵はどこで終わっても数える(string signature, string[] said)
    {
        // Every way a picture on its way in can end without arriving - it failed, it was refused
        // over a drawing, it was called off - is counted where that is said, so a button waiting for
        // the picture can tell. One left out writes the old picture under "applied" again.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), signature);

        foreach (string message in said)
        {
            int written = Regex.Matches(body, Regex.Escape(message)).Count;
            int counted = Regex.Matches(body, Regex.Escape(message) + @"\s*NoteNotArrived\(\);").Count;
            Assert.True(written > 0, $"{signature} に {message} が無い");
            Assert.True(written == counted, $"{signature} の {message} のうち数えていないものがある（{counted}/{written}）");
        }
    }

    [Fact]
    public void 届かなかった数は理由と一緒に控える()
    {
        string note = LanguageNotificationTests.MethodBody(ViewModel(), "private void NoteNotArrived()");
        Assert.Contains("_picturesNotArrived++;", note, StringComparison.Ordinal);
        Assert.Contains("_whyNotArrived = _statusRecipe", note, StringComparison.Ordinal);

        // Only a count that moved while waiting stops a button; nothing moved, nothing is said
        Assert.Matches(
            @"NotArrivedSince\(int mark\)\s*=>\s*_picturesNotArrived == mark \? null : _whyNotArrived;",
            ViewModel());
    }
}
