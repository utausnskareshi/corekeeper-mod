using System.IO.Compression;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for the containment guard used when unpacking the bundled mod.
///
/// The payload is built by this project, so a hostile archive is not the everyday case; the
/// guard exists because the extraction target is inside the user's game folder, where writing
/// one file to the wrong place can break the installation. A corrupted or swapped payload has
/// to be refused rather than trusted.
/// </summary>
public sealed class ZipSlipTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cks-zipslip-test-{Guid.NewGuid():N}");

    public ZipSlipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Builds an archive whose entry names are written verbatim, escapes included.</summary>
    private MemoryStream ArchiveWith(params string[] entryNames)
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in entryNames)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter writer = new(entry.Open());
                writer.Write("payload");
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private void Extract(string destination, params string[] entryNames)
    {
        using MemoryStream buffer = ArchiveWith(entryNames);
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        ModPayload.ExtractSafely(archive, destination);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("Scripts/../../escaped.txt")]
    public void 展開先の外へ出る項目を拒否する(string entryName)
    {
        string destination = Path.Combine(_root, "game", "Mods", "CustomPlayerSkin");
        Directory.CreateDirectory(destination);

        Assert.Throws<ToolException>(() => Extract(destination, entryName));

        // Nothing may have been written above the destination
        Assert.False(
            File.Exists(Path.Combine(_root, "game", "Mods", "escaped.txt")),
            "展開先の外にファイルが書かれた");
        Assert.False(
            File.Exists(Path.Combine(_root, "escaped.txt")),
            "展開先の外にファイルが書かれた");
    }

    [Fact]
    public void 絶対パスの項目を拒否する()
    {
        string destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);
        string outside = Path.Combine(_root, "outside.txt");

        Assert.Throws<ToolException>(() => Extract(destination, outside.Replace('\\', '/')));
        Assert.False(File.Exists(outside), "絶対パス指定で展開先の外に書かれた");
    }

    [Fact]
    public void 正常な項目はサブフォルダごと展開される()
    {
        string destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        Extract(destination, "ModManifest.json", "Scripts/Mod.cs", "Scripts/Patches/Patch.cs");

        Assert.True(File.Exists(Path.Combine(destination, "ModManifest.json")));
        Assert.True(File.Exists(Path.Combine(destination, "Scripts", "Mod.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Scripts", "Patches", "Patch.cs")));
    }

    [Fact]
    public void バックスラッシュ区切りの項目もフォルダとして展開される()
    {
        // PowerShell 5.1's Compress-Archive produces these, and the payload was built that way
        string destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        Extract(destination, "Scripts\\Mod.cs");

        Assert.True(File.Exists(Path.Combine(destination, "Scripts", "Mod.cs")));
        Assert.Empty(Directory.GetFiles(destination, "Scripts*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("Scripts/..")]
    public void 展開先そのものを指す項目を拒否する(string entryName)
    {
        // "Within the destination" is also true of the destination itself, so a plain
        // containment test lets these through. ExtractToFile then gets a folder as its target
        // and fails with a permission error that says nothing about the payload being wrong.
        string destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        Assert.Throws<ToolException>(() => Extract(destination, entryName));
        Assert.True(Directory.Exists(destination), "展開先そのものが壊された");
    }
}
