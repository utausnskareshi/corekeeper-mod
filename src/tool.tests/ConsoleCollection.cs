namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Groups the tests that drive <c>Program.Main</c> and redirect the console.
///
/// Console.Out and Console.Error are process-wide, and xUnit runs test classes in parallel, so
/// two such classes running at once swap the writer out from under each other: a class that only
/// swallows output restored its own writer over the one a class reading the output had installed,
/// and the reader was handed the other test's error message. Sharing a collection makes them run
/// one after another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "console";
}
