using CoreKeeperSkinTool.Editing;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for the preset characters.
///
/// A preset is a recipe rather than an image, so the things worth checking are that every recipe
/// actually produces a usable sheet, that no two of them come out looking the same, and that
/// nothing spills outside its frame.
/// </summary>
public sealed class PresetTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();
    private static readonly PartsLayout Parts = PartsLayout.LoadEmbedded();
    private static readonly PresetLibrary Library = PresetLibrary.LoadEmbedded();

    /// <summary>The pixels of one frame, as a comparable string.</summary>
    private static string Signature(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        System.Text.StringBuilder builder = new();

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                builder.Append((uint)pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x]);
                builder.Append(',');
            }
        }

        return builder.ToString();
    }

    [Fact]
    public void プリセットが30種ある()
    {
        Assert.Equal(30, Library.Presets.Count);
    }

    /// <summary>
    /// The game only stores right-facing side art and mirrors it to walk left, so the side frames
    /// have to carry cues that say which way the character faces.
    ///
    /// Measured against the same recipe drawn without those cues, which is the only way to see
    /// what they contribute. An earlier version of this compared a side frame against its own
    /// mirror image; that passed with the whole feature removed, because the measured part boxes
    /// are not centred on the cell, so any recipe scores highly on it.
    /// </summary>
    [Fact]
    public void 横向きのコマには向きを示す描き分けがある()
    {
        IReadOnlyList<FrameRect> sideFrames = [.. Layout.Frames.Where(f => f.Dir == "right")];

        foreach (PresetDefinition preset in Library.Presets)
        {
            using SKBitmap withCues = PresetCharacter.Build(Layout, Parts, preset, sideViewEnabled: true);
            using SKBitmap without = PresetCharacter.Build(Layout, Parts, preset, sideViewEnabled: false);

            SKColor[] a = withCues.Pixels;
            SKColor[] b = without.Pixels;

            int changed = 0;
            foreach (FrameRect frame in sideFrames)
            {
                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        int i = ((frame.YTopLeft + y) * withCues.Width) + frame.X + x;
                        if (a[i] != b[i])
                        {
                            changed++;
                        }
                    }
                }
            }

            Assert.True(
                changed >= 10 * sideFrames.Count,
                $"{preset.Key} の右向き {sideFrames.Count} コマで、向きの描き分けが {changed} ピクセルしかない");
        }
    }

    /// <summary>
    /// Every frame outside the right-facing ones must be untouched by the side-view work, or a
    /// change meant for the walk cycle has quietly altered how the character faces the camera.
    /// </summary>
    [Fact]
    public void 向きの描き分けは右向きのコマ以外を変えない()
    {
        foreach (PresetDefinition preset in Library.Presets)
        {
            using SKBitmap withCues = PresetCharacter.Build(Layout, Parts, preset, sideViewEnabled: true);
            using SKBitmap without = PresetCharacter.Build(Layout, Parts, preset, sideViewEnabled: false);

            foreach (FrameRect frame in Layout.Frames.Where(f => f.Dir != "right"))
            {
                Assert.Equal(Signature(without, frame), Signature(withCues, frame));
            }
        }
    }

    /// <summary>
    /// A cue that lands outside the cell is silently dropped by the painter, so a feature can be
    /// missing from almost every frame without anything failing. The antenna's lit tip was drawn
    /// above the top edge and survived in only two of the thirty-nine frames.
    /// </summary>
    [Fact]
    public void 特徴の描画がコマの外へ出て消えない()
    {
        PresetDefinition antenna = Library.Presets.First(p => p.Has("antenna"));
        SKColor tip = PresetDefinition.Parse(antenna.Accent);

        using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, antenna);
        SKColor[] pixels = sheet.Pixels;

        List<int> missing = [];
        foreach (FrameRect frame in Layout.Frames)
        {
            bool found = false;
            for (int y = 0; y < frame.H && !found; y++)
            {
                for (int x = 0; x < frame.W && !found; x++)
                {
                    found = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x] == tip;
                }
            }

            if (!found)
            {
                missing.Add(frame.Index);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"{antenna.Key} のアンテナの先端が {missing.Count} コマで消えている: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Every build option has to draw differently from the others, or a recipe declaring one is
    /// promising something it does not deliver. "wide" used to render identically to "normal",
    /// which covered eleven of the shipped presets and a third of every random character.
    ///
    /// Blob bodies are checked as well. They took no notice of the build at all, so slime and
    /// frog ("wide") and ghost ("normal") drew the same pixels whatever they declared — and the
    /// earlier version of this test excluded blobs, which is why that went unnoticed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 体格の指定はそれぞれ別の絵になる(bool blob)
    {
        PresetDefinition baseline = Library.Presets.First(p => p.Has("blob") == blob);
        FrameRect frame = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" });

        Dictionary<string, string> byBuild = [];
        foreach (string build in new[] { "slim", "normal", "wide" })
        {
            // PresetDefinition is a mutable class rather than a record, so the option under test
            // is set and put back rather than copied.
            string original = baseline.Build;
            baseline.Build = build;

            try
            {
                using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, baseline);
                byBuild[build] = Signature(sheet, frame);
            }
            finally
            {
                baseline.Build = original;
            }
        }

        Assert.Equal(3, byBuild.Values.Distinct().Count());
    }

    [Fact]
    public void すべてのプリセットが全コマに絵を持つ()
    {
        foreach (PresetDefinition preset in Library.Presets)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            SKColor[] pixels = sheet.Pixels;

            foreach (FrameRect frame in Layout.Frames)
            {
                bool any = false;
                for (int y = 0; y < frame.H && !any; y++)
                {
                    for (int x = 0; x < frame.W && !any; x++)
                    {
                        any = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha != 0;
                    }
                }

                Assert.True(any, $"{preset.Key} のコマ {frame.Index} が空になっている");
            }
        }
    }

    [Fact]
    public void 背面のコマに顔を描かない()
    {
        // The game has no eyes in any frame facing away from the camera. Drawing a default pair
        // when the measurement was absent put a face on the back of the character's head.
        SKColor eyeColour = new(0x1A, 0x1A, 0x22);

        FrameRect[] backFrames = [.. Layout.Frames.Where(f => f.Dir == "up")];
        Assert.NotEmpty(backFrames);

        foreach (PresetDefinition preset in Library.Presets)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            SKColor[] pixels = sheet.Pixels;

            foreach (FrameRect frame in backFrames)
            {
                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        Assert.NotEqual(
                            eyeColour,
                            pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x]);
                    }
                }
            }
        }
    }

    [Fact]
    public void 正面と横のコマには顔を描く()
    {
        // The other half of the rule: skipping the eyes must not have made them disappear
        // from the frames that do face the camera.
        SKColor eyeColour = new(0x1A, 0x1A, 0x22);
        PresetDefinition preset = Library.Presets.Single(p => p.Key == "adventurer");

        using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
        SKColor[] pixels = sheet.Pixels;

        foreach (FrameRect frame in Layout.Frames.Where(f => f.Dir is "down" or "right"))
        {
            int found = 0;
            for (int y = 0; y < frame.H; y++)
            {
                for (int x = 0; x < frame.W; x++)
                {
                    if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x] == eyeColour)
                    {
                        found++;
                    }
                }
            }

            Assert.True(found > 0, $"コマ {frame.Index} ({frame.Anim}_{frame.Dir}) に目が無い");
        }
    }

    [Fact]
    public void プリセット同士が見た目で区別できる()
    {
        FrameRect frame = Layout.Frames.Single(f => f.Index == 0);
        Dictionary<string, string> seen = [];

        foreach (PresetDefinition preset in Library.Presets)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            string signature = Signature(sheet, frame);

            Assert.False(
                seen.TryGetValue(signature, out string? other),
                $"{preset.Key} と {other} の見た目が完全に同じ");

            seen[signature] = preset.Key;
        }
    }

    [Fact]
    public void プリセットは隣のコマへはみ出さない()
    {
        // Everything is clipped to its own frame, so no pixel of one frame may be written by
        // the drawing of another. Checked by confirming the sheet is the declared size and that
        // the gaps between frames stay empty.
        using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, Library.Presets[0]);

        Assert.Equal(Layout.Texture.Width, sheet.Width);
        Assert.Equal(Layout.Texture.Height, sheet.Height);

        HashSet<(int, int)> insideAFrame = [];
        foreach (FrameRect frame in Layout.Frames)
        {
            for (int y = 0; y < frame.H; y++)
            {
                for (int x = 0; x < frame.W; x++)
                {
                    insideAFrame.Add((frame.X + x, frame.YTopLeft + y));
                }
            }
        }

        SKColor[] pixels = sheet.Pixels;
        for (int y = 0; y < sheet.Height; y++)
        {
            for (int x = 0; x < sheet.Width; x++)
            {
                if (pixels[(y * sheet.Width) + x].Alpha != 0)
                {
                    Assert.True(insideAFrame.Contains((x, y)), $"コマの外 ({x},{y}) に描かれている");
                }
            }
        }
    }

    [Fact]
    public void 定義の重複や不正な分類を拒否する()
    {
        PresetLibrary duplicate = new()
        {
            Categories = ["job"],
            Presets =
            [
                new PresetDefinition { Key = "a", Category = "job" },
                new PresetDefinition { Key = "a", Category = "job" },
            ],
        };
        Assert.Throws<ToolException>(duplicate.Validate);

        PresetLibrary unknownCategory = new()
        {
            Categories = ["job"],
            Presets = [new PresetDefinition { Key = "a", Category = "nope" }],
        };
        Assert.Throws<ToolException>(unknownCategory.Validate);

        Assert.Throws<ToolException>(new PresetLibrary().Validate);
    }

    [Fact]
    public void ランダム生成は毎回異なるものを作る()
    {
        Random random = new(12345);
        HashSet<string> signatures = [];
        FrameRect frame = Layout.Frames.Single(f => f.Index == 0);

        for (int i = 0; i < 20; i++)
        {
            PresetDefinition preset = RandomPreset.Create(Library, random);
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            signatures.Add(Signature(sheet, frame));
        }

        // A couple of collisions would be tolerable; anything close to all of them means the
        // randomisation is not actually varying the result.
        Assert.True(signatures.Count >= 18, $"20 回中 {signatures.Count} 種類しか出なかった");
    }

    [Fact]
    public void ランダム生成の結果も全コマに絵を持つ()
    {
        Random random = new(999);

        for (int i = 0; i < 5; i++)
        {
            PresetDefinition preset = RandomPreset.Create(Library, random);
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            SKColor[] pixels = sheet.Pixels;

            foreach (FrameRect frame in Layout.Frames)
            {
                bool any = false;
                for (int y = 0; y < frame.H && !any; y++)
                {
                    for (int x = 0; x < frame.W && !any; x++)
                    {
                        any = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha != 0;
                    }
                }

                Assert.True(any, $"ランダム {i} のコマ {frame.Index} が空になっている");
            }
        }
    }

    [Fact]
    public void 配色差し替えは形を保ったまま色だけ変える()
    {
        using SKBitmap original = PresetCharacter.Build(Layout, Parts, Library.Presets[0]);
        using SKBitmap palette = PresetCharacter.Build(Layout, Parts, Library.Presets[10]);

        EditorDocument document = new(original, Layout);
        SKColor[] before = [.. document.Pixels];

        document.Recolour(palette);
        SKColor[] after = [.. document.Pixels];

        // The silhouette is untouched: exactly the same pixels are opaque
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Alpha == 0, after[i].Alpha == 0);
        }

        Assert.NotEqual(before, after);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);

        // And it can be taken back in one step
        Assert.True(document.Undo());
        SKColor[] restored = [.. document.Pixels];
        Assert.Equal(before.Length, restored.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i], restored[i]);
        }
    }

    [Fact]
    public void 配色差し替えは寸法違いを拒否する()
    {
        using SKBitmap original = PresetCharacter.Build(Layout, Parts, Library.Presets[0]);
        using SKBitmap wrongSize = PixelOps.CreateEmpty(10, 10);

        EditorDocument document = new(original, Layout);

        Assert.Throws<ToolException>(() => document.Recolour(wrongSize));
    }

    /// <summary>
    /// The build is meant to change how wide the body reads, and the side frames redraw the arms
    /// over the top of the front-facing pass. That redraw has to honour the build as well.
    ///
    /// It did not: the arms went back at the un-inset columns, which handed a slim character
    /// exactly a normal character's silhouette in all thirteen right-facing frames. The setting
    /// was silently dead in that pass - the same shape as the bug the comment on the inset itself
    /// records, where "wide" shared the normal value and eleven presets drew identically.
    /// </summary>
    [Fact]
    public void 右向きのコマでも体格の設定が体の幅に効く()
    {
        static PresetDefinition Recipe(string build) =>
            new() { Key = build, Category = "test", Build = build };

        using SKBitmap slim = PresetCharacter.Build(Layout, Parts, Recipe("slim"));
        using SKBitmap normal = PresetCharacter.Build(Layout, Parts, Recipe("normal"));
        using SKBitmap wide = PresetCharacter.Build(Layout, Parts, Recipe("wide"));

        IReadOnlyList<FrameRect> sideFrames = [.. Layout.Frames.Where(f => f.Dir == "right")];
        Assert.NotEmpty(sideFrames);

        foreach (FrameRect frame in sideFrames)
        {
            PartBox? torso = Parts.Part("shirt")?.For(frame.Index);
            if (torso is null)
            {
                continue;
            }

            // Measured across the middle of the torso, where the arms are the outermost thing
            // drawn and the head and legs cannot reach
            int row = torso.Y + (torso.H / 2);

            int slimWidth = PaintedWidth(slim, frame, row);
            int normalWidth = PaintedWidth(normal, frame, row);
            int wideWidth = PaintedWidth(wide, frame, row);

            Assert.True(
                slimWidth < normalWidth,
                $"コマ {frame.Index}: 細身が普通と同じ幅（{slimWidth} と {normalWidth}）");
            Assert.True(
                normalWidth < wideWidth,
                $"コマ {frame.Index}: 幅広が普通と同じ幅（{normalWidth} と {wideWidth}）");
        }
    }

    /// <summary>How wide one row inside a frame is painted, edge to edge.</summary>
    private static int PaintedWidth(SKBitmap sheet, FrameRect frame, int row)
    {
        SKColor[] pixels = sheet.Pixels;
        int first = -1;
        int last = -1;

        for (int x = 0; x < frame.W; x++)
        {
            if (pixels[((frame.YTopLeft + row) * sheet.Width) + frame.X + x].Alpha == 0)
            {
                continue;
            }

            first = first < 0 ? x : first;
            last = x;
        }

        return first < 0 ? 0 : last - first + 1;
    }
}
