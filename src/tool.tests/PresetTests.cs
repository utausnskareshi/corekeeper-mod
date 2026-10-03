using CoreKeeperSkinTool.Editing;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for the preset characters.
///
/// What every preset owes, whichever kind it is: a usable sheet, all thirty-nine frames filled,
/// nothing spilling outside its frame, and a look of its own.
///
/// Beyond that the two kinds differ. A recipe is drawn from seven colours and a few shape
/// choices, so it can be asked whether the side frames carry their side cues and whether the
/// eyes went where eyes go. A picture has whatever its artist put in it, and asking the same
/// questions of it would be asking about the picture rather than about this code - so the tests
/// that measure drawing are scoped to the recipes, and the pictures have their own.
/// </summary>
public sealed class PresetTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();
    private static readonly PartsLayout Parts = PartsLayout.LoadEmbedded();
    private static readonly PresetLibrary Library = PresetLibrary.LoadEmbedded();

    /// <summary>The presets that are drawn from a recipe, which is what the drawing tests measure.</summary>
    private static IEnumerable<PresetDefinition> Drawn => Library.Presets.Where(p => p.IsDrawn);

    /// <summary>The presets that are a picture instead.</summary>
    private static IEnumerable<PresetDefinition> Pictured => Library.Presets.Where(p => !p.IsDrawn);

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

        // Recipes only. A picture is placed the same way whichever direction the frame faces,
        // so there is nothing here for it to differ by.
        foreach (PresetDefinition preset in Drawn)
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
        foreach (PresetDefinition preset in Drawn)
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
        // A recipe: "build" is a drawing instruction, and a picture has whatever build its
        // artist drew. Taken from the drawn ones for that reason, not to dodge a failure.
        PresetDefinition baseline = Drawn.First(p => p.Has("blob") == blob);
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

        // Recipes only. This looks for one exact colour, which a recipe uses for eyes and
        // nothing else; a picture of several hundred colours could hold it anywhere, so the
        // same check there would be measuring coincidence. What keeps a face off the back of a
        // picture is HideFaceOnBackFrames, which the placement test below covers.
        foreach (PresetDefinition preset in Drawn)
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

        // A recipe, because this looks for the colour a recipe paints eyes with. It used to be
        // the adventurer, which is now one of the pictures.
        PresetDefinition preset = Library.Presets.Single(p => p.Key == "golem");

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

    // ------------------------------------------------------------ Presets made of a picture

    [Fact]
    public void 絵のプリセットと描画のプリセットが両方ある()
    {
        // Both paths have to stay exercised. Everything below measures the pictures and much of
        // what is above measures the recipes, and either set would pass vacuously on an empty
        // collection - which is exactly what a mistyped category or a lost image field produces.
        Assert.NotEmpty(Pictured);
        Assert.NotEmpty(Drawn);
    }

    [Fact]
    public void 絵のプリセットは背面のコマで顔を隠す()
    {
        // The stored picture is a front view, so without this the back of the head carries a
        // face. Compared against the idle frame of each direction rather than against a colour,
        // because a picture has no one colour that means "eye".
        FrameRect front = Layout.Frames.First(f => f.Anim == "idle" && f.Dir == "down");
        FrameRect back = Layout.Frames.First(f => f.Anim == "idle" && f.Dir == "up");

        foreach (PresetDefinition preset in Pictured)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);

            Assert.False(
                Signature(sheet, front) == Signature(sheet, back),
                $"{preset.Key}: 背面のコマが正面と同じ絵のまま。顔が後頭部に出る");
        }
    }

    [Fact]
    public void 絵のプリセットは隣のコマへはみ出さない()
    {
        // The composer clips to the cell, but the picture arrives at a size nobody chose - it is
        // whatever came out of cleaning - so the clipping is what stands between a tall hat and
        // the frame above it.
        foreach (PresetDefinition preset in Pictured)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            SKColor[] pixels = sheet.Pixels;

            HashSet<int> inside = [];
            foreach (FrameRect frame in Layout.Frames)
            {
                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        inside.Add(((frame.YTopLeft + y) * sheet.Width) + frame.X + x);
                    }
                }
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].Alpha != 0 && !inside.Contains(i))
                {
                    Assert.Fail($"{preset.Key}: コマの外に画素がある (index {i})");
                }
            }
        }
    }

    [Fact]
    public void 絵のプリセットはコマの縁に触れない()
    {
        // What being too large looks like once it has happened. The composer clips to the cell,
        // so art that did not fit is not reported by anything the sheet itself can be asked -
        // it just arrives with its hat or its shoulders sliced off at the boundary. The game's
        // own character keeps three pixels of air on each side and two below, so a picture
        // reaching the edge has been cut.
        foreach (PresetDefinition preset in Pictured)
        {
            using SKBitmap sheet = PresetCharacter.Build(Layout, Parts, preset);
            SKColor[] pixels = sheet.Pixels;

            foreach (FrameRect frame in Layout.Frames)
            {
                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        bool edge = x == 0 || y == 0 || x == frame.W - 1 || y == frame.H - 1;
                        if (!edge)
                        {
                            continue;
                        }

                        Assert.True(
                            pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha == 0,
                            $"{preset.Key}: コマ {frame.Index} の縁に画素がある。絵が大きすぎて切れている");
                    }
                }
            }
        }
    }

    [Fact]
    public void 埋め込んだ絵はすべてどれかのプリセットが使う()
    {
        // The other direction of the same contract the library checks. A picture left behind
        // after its preset was renamed costs every user the download of a file nothing reads,
        // and nothing else would ever mention it.
        HashSet<string> used = [.. Pictured.Select(p => p.Image!)];

        foreach (string name in PresetArt.Names())
        {
            Assert.True(used.Contains(name), $"どのプリセットも使っていない絵が埋め込まれている: {name}");
        }
    }

    [Fact]
    public void 埋め込まれていない絵を指すプリセットを拒否する()
    {
        PresetLibrary library = new()
        {
            Categories = ["job"],
            Presets =
            [
                new PresetDefinition { Key = "ghostly", Category = "job", Image = "no-such-file.png" },
            ],
        };

        ToolException error = Assert.Throws<ToolException>(library.Validate);

        Assert.Contains("no-such-file.png", error.Message, StringComparison.Ordinal);
    }
}
