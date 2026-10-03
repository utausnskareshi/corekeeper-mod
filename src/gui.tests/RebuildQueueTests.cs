using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that the debounced conversion stops calling itself queued once it has run.
///
/// The window offers to rebuild from the import settings only while a settings change is still
/// outstanding, and the one thing it goes by is whether a queued conversion was called off -
/// <c>CancelPending</c> answers that from <c>_pending</c> being non-null. The field is set when
/// the conversion is scheduled, so unless the task clears it when its wait ends, a conversion
/// that already ran still answers "yes". Choosing a preset then put the rebuild offer back on
/// screen with nothing outstanding, and pressing it - which is what the button asks for -
/// replaced the preset and every pixel drawn over it with a conversion of the picture the user
/// had moved on from, undo stack included.
///
/// Read from the source, as the other checks on this view model are: the type needs a live
/// Avalonia dispatcher, and the ordering this pins down is between two awaits inside a
/// fire-and-forget task, which no test that drives the type from outside can observe.
/// </summary>
public sealed class RebuildQueueTests
{
    [Fact]
    public void 待ち終えた変換は待機中を名乗らない()
    {
        string source = LanguageNotificationTests.ViewModelSource();

        // The debounce lives in one Task.Run: wait, then hand the conversion to the UI thread.
        Match delay = Regex.Match(source, @"await\s+Task\.Delay\(\s*\d+\s*,\s*token\s*\)\s*;");
        Assert.True(delay.Success, "デバウンスの待機が見つからない");

        int handOff = source.IndexOf("Dispatcher.UIThread.InvokeAsync", delay.Index, StringComparison.Ordinal);
        Assert.True(handOff > 0, "変換を UI スレッドへ渡す箇所が見つからない");

        string betweenWaitAndRun = source[(delay.Index + delay.Length)..handOff];

        Assert.Contains(
            "_pending",
            betweenWaitAndRun,
            StringComparison.Ordinal);

        // Cleared, not re-armed. Anything but null here would leave the same wrong answer.
        Assert.Matches(@"CompareExchange\(\s*ref\s+_pending\s*,\s*null\s*,", betweenWaitAndRun);
    }
}
