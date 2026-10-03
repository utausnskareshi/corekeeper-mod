namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Groups the tests that start a child process, so that nothing else runs while they do.
///
/// Process.Start with UseShellExecute off creates the child with handle inheritance switched on,
/// and SkiaSharp opens a file by path through the C runtime, whose handles are inheritable. A child
/// started while another class was decoding carried that handle off with it, and the other class
/// then could not delete its own file for as long as the child lived: "being used by another
/// process", in the cleanup, after every assertion had passed. Measured: 47 of 8000
/// decode-then-delete rounds failed with cmd.exe being started alongside, none without it, and
/// none when the file was opened through a FileStream instead. The suite failed about one run in
/// seven this way.
///
/// The program itself never starts a process, so this only ever reached the tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChildProcessCollection
{
    public const string Name = "child-process";
}
