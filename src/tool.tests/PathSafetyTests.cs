using CoreKeeperSkinTool;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for the path checks that protect the destructive operations.
///
/// These exist because the original delete guard compared names as text without
/// canonicalising them: a mod folder name of ".." satisfied both "the name matches"
/// and "the parent is Mods", and the recursive delete then removed the parent folder.
/// </summary>
public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    [InlineData("C:")]
    public void EnsureSingleSegment_パス構造を含む名前を拒否する(string name)
    {
        Assert.Throws<ToolException>(() => PathSafety.EnsureSingleSegment(name, "テスト名"));
    }

    [Theory]
    [InlineData("CustomPlayerSkin")]
    [InlineData("Skin.png")]
    [InlineData("日本語のフォルダ")]
    [InlineData("mod-with-dash_and_underscore")]
    public void EnsureSingleSegment_通常の名前は素通しする(string name)
    {
        Assert.Equal(name, PathSafety.EnsureSingleSegment(name, "テスト名"));
    }

    [Theory]
    [InlineData("...")]
    [InlineData(".. ")]
    [InlineData("X.")]
    [InlineData("X ")]
    [InlineData("CustomPlayerSkin.")]
    public void EnsureSingleSegment_末尾のドットや空白を拒否する(string name)
    {
        // Found by the adversarial pass. Windows strips trailing dots and spaces when resolving
        // a path, so "..." resolves to the parent even though it is not spelled "..", and it
        // passed every spelling-based check. Combining it under Mods produced Mods itself,
        // which would have made the swap move the entire Mods folder aside.
        Assert.Throws<ToolException>(() => PathSafety.EnsureSingleSegment(name, "テスト名"));
    }

    [Fact]
    public void IsWithin_末尾区切りの有無で判定が変わらない()
    {
        string root = Path.Combine(Path.GetTempPath(), "cks-trailing");

        Assert.True(PathSafety.IsWithin(root, root + Path.DirectorySeparatorChar));
        Assert.True(PathSafety.IsWithin(root + Path.DirectorySeparatorChar, root));
    }

    [Fact]
    public void EnsureInside_末尾区切り付きでルート自身を指す場合も拒否する()
    {
        // Path.GetFullPath keeps a trailing separator for a path whose last segment is stripped,
        // and comparing that against the root as plain text said the two were different.
        string root = Path.Combine(Path.GetTempPath(), "cks-inside-trailing");

        Assert.Throws<ToolException>(
            () => PathSafety.EnsureInside(root, root + Path.DirectorySeparatorChar, "テスト対象"));
        Assert.Throws<ToolException>(
            () => PathSafety.EnsureInside(root, Path.Combine(root, "..."), "テスト対象"));
    }

    [Fact]
    public void DeleteDirectory_読み取り専用のファイルごと削除できる()
    {
        // Directory.Delete(recursive) refuses a read-only file, and one is enough to abort the
        // whole call: "remove everything" then left the mod half-deleted.
        string root = Path.Combine(Path.GetTempPath(), $"cks-ro-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "Scripts");
        Directory.CreateDirectory(nested);

        string file = Path.Combine(nested, "locked.cs");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            PathSafety.DeleteDirectory(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteDirectory_存在しなくても例外にならない()
    {
        PathSafety.DeleteDirectory(Path.Combine(Path.GetTempPath(), $"cks-none-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void EnsureSingleSegment_ファイル名に使えない文字を拒否する()
    {
        Assert.Throws<ToolException>(() => PathSafety.EnsureSingleSegment("bad*name", "テスト名"));
        Assert.Throws<ToolException>(() => PathSafety.EnsureSingleSegment("bad?name", "テスト名"));
    }

    [Fact]
    public void IsWithin_配下と自身を真とし外を偽とする()
    {
        string root = Path.Combine(Path.GetTempPath(), "cks-within-root");

        Assert.True(PathSafety.IsWithin(root, root));
        Assert.True(PathSafety.IsWithin(root, Path.Combine(root, "child")));
        Assert.True(PathSafety.IsWithin(root, Path.Combine(root, "a", "..", "b")));

        Assert.False(PathSafety.IsWithin(root, Path.Combine(root, "..")));
        Assert.False(PathSafety.IsWithin(root, Path.Combine(root, "..", "sibling")));
        Assert.False(PathSafety.IsWithin(root, Path.GetTempPath()));
    }

    [Fact]
    public void IsWithin_名前が前方一致する兄弟フォルダを配下と誤認しない()
    {
        string root = Path.Combine(Path.GetTempPath(), "cks-mod");
        string sibling = Path.Combine(Path.GetTempPath(), "cks-mod-backup");

        Assert.False(PathSafety.IsWithin(root, sibling));
    }

    [Fact]
    public void EnsureInside_ルート自身は配下として認めない()
    {
        string root = Path.Combine(Path.GetTempPath(), "cks-inside-root");

        Assert.Throws<ToolException>(() => PathSafety.EnsureInside(root, root, "テスト対象"));
        Assert.Throws<ToolException>(
            () => PathSafety.EnsureInside(root, Path.Combine(root, ".."), "テスト対象"));
    }

    [Fact]
    public void DeleteFile_読み取り専用のファイルも消せる()
    {
        // DeleteFile is the way in for all three destructive paths - removing a character's
        // picture, clearing abandoned .new files, and taking the manifest out of a leftover
        // working folder - and clearing the read-only attribute is what holds them up. Nothing
        // tested it, so replacing it with a plain File.Delete kept every test green while, on a
        // real machine, a picture marked read-only by a backup or cloud-sync tool could no
        // longer be removed and the user was left unable to undo an installation.
        string root = Path.Combine(Path.GetTempPath(), $"cks-rofile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string path = Path.Combine(root, "readonly.png");
            File.WriteAllText(path, "x");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            PathSafety.DeleteFile(path);

            Assert.False(File.Exists(path), "読み取り専用のファイルが残っている");
        }
        finally
        {
            PathSafety.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeleteFile_存在しないパスは何もせずに戻る()
    {
        // Callers use this to clear things that may or may not be there
        PathSafety.DeleteFile(
            Path.Combine(Path.GetTempPath(), $"cks-none-{Guid.NewGuid():N}", "nothing.png"));
    }

    /// <summary>
    /// The one path whose trailing separator carries meaning.
    ///
    /// "C:\" is the root of C; "C:" is wherever the process happens to be on C. Trimming it made
    /// every path later built from that root drive-relative, so files went somewhere nobody named.
    /// Reachable from the command line, where --mods-dir is normalised before anything is written
    /// under it.
    /// </summary>
    [Fact]
    public void Normalize_ドライブ直下は相対パスにならない()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        string normalized = PathSafety.Normalize(root);

        Assert.True(
            Path.IsPathFullyQualified(normalized),
            $"ドライブ直下が完全修飾でなくなっている: {normalized}");

        // And a path built from it still lands at the root, not beside the working directory
        string built = Path.Combine(normalized, "CustomPlayerSkin");
        Assert.Equal(Path.Combine(root, "CustomPlayerSkin"), Path.GetFullPath(built));
    }

    [Fact]
    public void Normalize_末尾の区切りは通常のフォルダでは落ちる()
    {
        // The reason the trim exists at all: the same folder written two ways has to compare equal
        string folder = Path.Combine(Path.GetTempPath(), $"cks-trim-{Guid.NewGuid():N}");

        Assert.Equal(
            PathSafety.Normalize(folder),
            PathSafety.Normalize(folder + Path.DirectorySeparatorChar));
    }
}
