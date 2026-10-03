using System.Runtime.InteropServices;
using CoreKeeperSkinTool.Imaging;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Telling a picture too big for the memory left from any other failure.
///
/// Large pictures in all three slots - a phone photo as front, right and back - need several
/// gigabytes (8192x8192 three times: 3.2 GB to load, 4.7 GB while settings change, measured), and
/// when that is not there the failure arrives as one of three English sentences: SkiaSharp's
/// "Unable to allocate pixels for the bitmap.", a native "External component has thrown an
/// exception." from setting the pixels, or OutOfMemoryException. The window showed them as they
/// were, and the command line called them unexpected with a stack trace and exit 2; none said to
/// use smaller pictures, which is all it takes (the test campaign of 2026-10-01).
/// </summary>
public sealed class AllocationFailureTests
{
    [Fact]
    public void 確保の失敗の3通りを見分ける()
    {
        Assert.True(PixelOps.IsAllocationFailure(new OutOfMemoryException()));
        Assert.True(PixelOps.IsAllocationFailure(new SEHException()));
        Assert.True(PixelOps.IsAllocationFailure(new Exception("Unable to allocate pixels for the bitmap.")));
    }

    [Fact]
    public void ほかの失敗は確保の失敗と言わない()
    {
        Assert.False(PixelOps.IsAllocationFailure(new IOException("locked")));
        Assert.False(PixelOps.IsAllocationFailure(new InvalidOperationException("Unable to allocate pixels for the bitmap.")));
        Assert.False(PixelOps.IsAllocationFailure(new Exception("something else")));
        Assert.False(PixelOps.IsAllocationFailure(new ToolException("error.x", [], "x")));
    }

    [Fact]
    public void コマンドラインは確保の失敗を想定外のエラーより先に受けて縮小を勧める()
    {
        string program = File.ReadAllText(Path.Combine(
            FindRoot(), "src", "tool", "Program.cs"));

        int memory = program.IndexOf("catch (Exception ex) when (PixelOps.IsAllocationFailure(ex))", StringComparison.Ordinal);
        int unexpected = program.IndexOf("Console.Error.WriteLine($\"想定外のエラー:", StringComparison.Ordinal);
        Assert.True(memory >= 0 && memory < unexpected, "確保の失敗を想定外のエラーより先に受けていない");

        string branch = program[memory..unexpected];
        Assert.Contains("縮小", branch, StringComparison.Ordinal);
        Assert.Contains("--side", branch, StringComparison.Ordinal);
        Assert.Contains("return 1;", branch, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "tool")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "リポジトリのルートが見つからない");
        return directory!.FullName;
    }
}
