using System.Globalization;
using System.Text;
using CoreKeeperSkinTool.Cli;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool;

/// <summary>
/// Command line tool that converts any 2D image into a sprite sheet for Core Keeper's player body layer.
/// </summary>
public static class Program
{
    /// <summary>
    /// The product version the build stamps, the same value the executable's file properties show
    /// (1.3.0+&lt;commit&gt;). A fixed "0.1.0" here disagreed with both, and said nothing about which
    /// build a report came from.
    /// </summary>
    private static readonly string Version =
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Program).Assembly)
            ?.InformationalVersion ?? "0";

    public static int Main(string[] args)
    {
        // Keep non-ASCII messages from being mangled. Any failure here is cosmetic,
        // so it must never take the whole command down; the catch is deliberately broad
        // because a redirected or unusual console can fail in more ways than IOException.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // Only the display suffers, so carry on.
        }

        using CancellationTokenSource cancellation = new();

        // The first press asks the run to stop; a second one is left to the runtime. Installing
        // copies every character's sheet to one side before swapping any of them in, and being
        // killed between those two steps leaves a full-sized file behind that the mod never reads
        // and that install --list never shows. Handing the first press to the run lets its own
        // cleanup happen instead, which Ctrl+C otherwise skips - it does not unwind.
        //
        // Not every command watches the token: the conversion itself does not. A press that
        // appears to do nothing therefore has to stay answerable, and that is what leaving the
        // second one to the runtime is for.
        //
        // Held in a variable so it can be taken off again. Left subscribed, the handler outlives
        // the source it captured, and a press arriving while the runtime is finishing up would
        // call Cancel on a disposed source - on the console's own thread, where nothing catches.
        ConsoleCancelEventHandler interrupt = (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            e.Cancel = true;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already on the way out; there is nothing left to cancel
            }
        };

        Console.CancelKeyPress += interrupt;

        try
        {
            return Run(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Said plainly, because the one thing the user wants to know is whether the game is
            // now half-changed. The token is only read before generate writes its output and while
            // the copies are made, so it is not.
            Console.Error.WriteLine("中断した。キャラクターの画像は1つも入れ替えていない。");
            return 130;
        }
        catch (ToolException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (IsExpectedEnvironmentFailure(ex))
        {
            // Everyday situations such as a missing file, a locked file or a denied folder.
            // A stack trace here would only bury the one line the user needs.
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (PixelOps.IsAllocationFailure(ex))
        {
            // Large pictures and too little memory, not a defect: told as "unexpected" with a stack
            // trace and exit 2, it said nothing of the one remedy. The original sentence is kept,
            // as a native exception can have another cause.
            Console.Error.WriteLine("エラー: 絵の処理に必要なメモリを確保できなかった可能性がある。");
            Console.Error.WriteLine("  絵を縮小するか、--side / --back を外してから、もう一度実行すること。");
            Console.Error.WriteLine($"  （{ex.GetType().Name}: {ex.Message}）");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"想定外のエラー: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("詳細:");
            Console.Error.WriteLine(ex.ToString());
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }
    }

    /// <summary>
    /// Whether the exception describes an ordinary environment problem rather than a defect.
    /// These deserve a plain one-line message, not a stack trace.
    /// </summary>
    private static bool IsExpectedEnvironmentFailure(Exception ex) => ex is
        FileNotFoundException or
        DirectoryNotFoundException or
        UnauthorizedAccessException or
        PathTooLongException or
        NotSupportedException or
        ArgumentException or
        IOException;

    private static int Run(string[] args, CancellationToken cancellation)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        string command = args[0].TrimStart('-').ToLowerInvariant();

        // The parser reads the first token that does not begin with a dash as the command name,
        // so a command written with dashes got past the dispatch here and then came back from
        // the parser as "unknown option: --validate" - while "cks --help" worked, because help
        // never reaches the parser. Handing on the resolved name makes both spellings behave.
        string[] rest = [command, .. args.Skip(1)];

        return command switch
        {
            "generate" or "gen" => RunGenerate(rest, cancellation),
            "install" => RunInstall(rest, cancellation),
            "template" => RunTemplate(rest),
            "validate" => RunValidate(rest),
            "layout" => RunLayout(rest),
            "version" or "v" => PrintVersion(),
            "help" or "h" or "?" => PrintHelp(),
            var unknown => throw new ToolException(
                $"未知のコマンド: {unknown}{Environment.NewLine}`cks help` で使い方を確認すること。"),
        };
    }

    // ---------------------------------------------------------------- generate

    private static int RunGenerate(string[] args, CancellationToken cancellation)
    {
        CommandLine cmd = CommandLine.Parse(
            args,
            aliases: new Dictionary<string, string>
            {
                ["i"] = "input",
                ["o"] = "output",
                ["l"] = "layout",
            },
            valueOptions: new HashSet<string>
            {
                "input", "output", "layout", "anim", "resample", "bg-tolerance",
                "alpha-threshold", "outline", "colors", "width", "height",
                "offset-x", "offset-y", "mods-dir", "character", "game-dir",
                "side", "back",
            },
            knownFlags: new HashSet<string> { "remove-bg", "no-trim", "quiet", "install", "keep-back-face" });

        bool quiet = cmd.HasFlag("quiet");

        // Checked before any work is done. These three are read only by the install step, so
        // without --install they were accepted and thrown away, and the run still ended in
        // success - which meant a forgotten --install looked exactly like an applied one. That
        // also contradicts the parser's own rule that an unknown option is an error rather than
        // something to ignore quietly.
        string[] installOnly =
            [.. new[] { "character", "game-dir", "mods-dir" }.Where(cmd.HasOption)];
        if (!cmd.HasFlag("install") && installOnly.Length > 0)
        {
            throw new ToolException(
                $"--install が無いので {string.Join(" / ", installOnly.Select(o => $"--{o}"))} は何もしない。" +
                Environment.NewLine + "  ゲームへ配置するなら --install を付けること。" +
                Environment.NewLine + "  画像を作るだけなら、指定したオプションを外すこと。");
        }

        // The same two checks `install` makes, because --install sends this command down the very
        // same install path. Without them `--mods-dir "$DIR"` with the variable unset detected a
        // folder on its own and wrote every character's picture there, reporting success - the
        // one thing the check on the install command exists to prevent, reached by the other
        // command. --character needs no check here: SelectCharacters makes it on the shared path.
        if (cmd.HasFlag("install"))
        {
            RequireValueIfGiven(cmd, "mods-dir", "自動検出させるなら --mods-dir ごと外すこと。");
            RequireValueIfGiven(cmd, "game-dir", "自動検出させるなら --game-dir ごと外すこと。");
        }

        SheetLayout layout = LoadLayout(cmd);

        // An empty value reached the decoder, which said "入力画像が見つからない: " with nothing
        // after it and no word of which option was empty - an unset shell variable is the usual way
        RequireValueIfGiven(cmd, "input", "変換する画像のパスを指定すること。");
        string inputPath = cmd.GetRequiredString("input");
        string outputPath = RequirePath(cmd, "output", "skin.png");

        EnsureDifferentFiles(inputPath, outputPath, "--input", "--output");

        // The facings are source pictures too, and were not covered when they were added. Writing
        // the sheet over one of them destroyed it outright: a 400x400 drawing passed to --side
        // came back as the 234x156 sheet, with nothing said and a successful exit.
        foreach (string facing in new[] { "side", "back" })
        {
            if (cmd.HasOption(facing))
            {
                // Before the front picture is read, so an empty value stops the run at once
                RequireValueIfGiven(cmd, facing, $"正面の絵を流用するなら --{facing} ごと外すこと。");
                EnsureDifferentFiles(
                    cmd.GetRequiredString(facing), outputPath, $"--{facing}", "--output");
            }
        }

        // Only the output is checked against them. Naming the same picture twice is allowed and
        // useful: passing the front to --side is how a drawing is kept from being mirrored on
        // the right-facing frames, which is what the help says --side does.

        // Reject values that cannot mean anything rather than quietly ignoring them.
        // A --width of 0 used to fall back to the layout default, so a typo produced a
        // perfectly normal-looking sheet built from settings the user never asked for.
        int boxWidth = RequirePositiveOrUnset(cmd, "width", layout.Cell.Width);
        int boxHeight = RequirePositiveOrUnset(cmd, "height", layout.Cell.Height);
        int tolerance = RequireInRange(cmd, "bg-tolerance", 16, 0, 255);

        // The conversion itself is left to Core's SkinPipeline, which the GUI also uses,
        // so identical settings always give identical results in the CLI and the GUI.
        SkinOptions options = new(
            RemoveBackground: cmd.HasFlag("remove-bg"),
            BackgroundTolerance: tolerance,
            Trim: !cmd.HasFlag("no-trim"),
            BoxWidth: boxWidth,
            BoxHeight: boxHeight,
            Resample: cmd.GetEnum("resample", ResampleMode.Smooth),
            AlphaThreshold: cmd.GetByte("alpha-threshold", 128),
            Colors: RequireColors(cmd),
            Outline: cmd.HasOption("outline") ? ParseColor(cmd.GetRequiredString("outline")) : null,
            OffsetX: cmd.GetInt("offset-x", 0),
            OffsetY: cmd.GetInt("offset-y", 0),
            Animation: cmd.GetEnum("anim", AnimationStyle.Lively),
            HideFaceOnBackFrames: !cmd.HasFlag("keep-back-face"));

        SkinOptions resolved = options.WithDefaultsFrom(layout);

        using (SKBitmap source = PixelOps.Decode(inputPath, out bool decodeComplete))
        {
            // Warnings go to stderr, and are not silenced by --quiet: --quiet is for scripted
            // runs, which is precisely where nobody is watching. A truncated download decodes
            // into a picture whose missing half is transparent, and the pipeline then trims to
            // the part that survived and scales it up to fill the box, so the sheet looks
            // deliberate. Saying nothing here let that reach the game with exit code 0.
            if (!decodeComplete)
            {
                Console.Error.WriteLine(
                    $"警告: 画像を最後まで読み取れなかった: {inputPath}");
                Console.Error.WriteLine(
                    "      欠けた部分は透明として扱われる。ファイルが壊れているか、途中までしか保存されていない可能性がある。");
            }

            // Loaded alongside the front view so that all three are disposed together, and so a
            // missing file is reported before any conversion work is done
            using SKBitmap? side = DecodeOptional(cmd, "side");
            using SKBitmap? back = DecodeOptional(cmd, "back");

            SkinBuildResult result = SkinPipeline.Build(
                new SkinSources(source, side, back), layout, options);

            using (result.Sheet)
            {
                // The conversion does not watch the token, so a first Ctrl+C pressed during it is
                // only seen here. Not looking went on to write over the output and end with exit 0 -
                // silently with --quiet - where the press had always stopped the run before.
                cancellation.ThrowIfCancellationRequested();

                bool overwriting = File.Exists(outputPath);
                PixelOps.EncodePng(result.Sheet, outputPath);

                if (!quiet)
                {
                    Console.WriteLine($"入力          : {inputPath} ({result.SourceSize.Width}x{result.SourceSize.Height})");
                    Console.WriteLine($"切り詰め後    : {result.TrimmedSize.Width}x{result.TrimmedSize.Height}");
                    Console.WriteLine($"配置サイズ    : {DescribePlacedSizes(result)}  " +
                                      $"({DescribeBox(result, resolved)}" +
                                      $"{(resolved.Outline is null ? string.Empty : " / うち輪郭に外周1px")})");
                    Console.WriteLine($"動きの付け方  : {resolved.Animation.ToString().ToLowerInvariant()}");
                    Console.WriteLine($"出力          : {Path.GetFullPath(outputPath)} " +
                                      $"({layout.Texture.Width}x{layout.Texture.Height}){(overwriting ? " ※上書き" : string.Empty)}");

                    if (result.UsesLittleOfBox)
                    {
                        Console.WriteLine();
                        // Said in terms of the box, not in fixed numbers. The judgement compares
                        // the picture's proportions against the box's, and the box is something
                        // --width and --height change, so quoting "0.6 to 0.7" told a square
                        // picture in a 16x4 box that it was tall and narrow and then advised a
                        // shape that would have used even less of the width.
                        Console.WriteLine(
                            $"補足: 元画像が配置枠より縦に細長いため、配置幅 {resolved.BoxWidth} のうち " +
                            $"{result.SpriteSize.Width} しか使えていない。");
                        Console.WriteLine("      配置枠に近い縦横比の絵にすると、その分だけ細部が残る。");
                    }

                    Console.WriteLine();
                    Console.WriteLine("補足: 左向きのコマは右向きのコマの水平反転で描画される仕様のため、");
                    Console.WriteLine("      文字やロゴなど左右非対称な絵は左に歩くと鏡像になる。");
                }

                // On stderr and outside the quiet check, like the clipping warning below. This
                // one covers the gap the clipping count cannot see: art between the box width
                // and the cell width is placed outside what `cks layout` calls the safe drawing
                // area, yet nothing leaves the cell, so no pixel is discarded and the run
                // finished in silence with the character drawn over the lines.
                WarnAboutWideFacings(result, resolved.BoxWidth, layout.Cell.Width);

                // Outside the quiet check and on stderr, for the same reason as the truncated
                // read above: this one says part of the picture is gone, and a scripted run
                // that pipes stdout would otherwise install a clipped sheet without a word.
                if (result.ClippedPixels > 0)
                {
                    Console.Error.WriteLine(
                        $"警告: コマからはみ出した {result.ClippedPixels} ピクセルを切り捨てた。");
                    Console.Error.WriteLine(
                        "      --width / --height を小さくするか、--offset-y で位置を調整すること。");

                    // --width limits the front only, as the warning above says, so a wide side or
                    // back picture is not reined in by it. The advice above stays, because the
                    // clipping may still come from --offset-y or the front; which it is cannot be
                    // told from the count.
                    if (result.RightSize?.Width > resolved.BoxWidth || result.UpSize?.Width > resolved.BoxWidth)
                    {
                        Console.Error.WriteLine(
                            "      向き別の絵の幅は --width では制限されない。そちらがはみ出しているなら、" +
                            "--height を小さくするか、正面より横長にならない絵を渡すこと。");
                    }
                }
            }
        }

        if (cmd.HasFlag("install"))
        {
            InstallSheet(cmd, layout, outputPath, quiet, cancellation);
        }

        return 0;
    }

    /// <summary>
    /// The sizes the art was actually placed at, named per facing once more than one is in play.
    ///
    /// One number pair was printed for every run, and it was always the front's. Supplying
    /// --side or --back changed what the sheet contains without changing this line, so a square
    /// side view beside a 40x60 front was reported as "13x19" while 19x19 went into the sheet.
    /// With no facing supplied the front's art fills every frame, and the old single pair is
    /// still the whole truth - so the wording there is left exactly as it was.
    /// </summary>
    private static string DescribePlacedSizes(SkinBuildResult result)
    {
        string front = $"{result.SpriteSize.Width}x{result.SpriteSize.Height}";

        if (result.RightSize is null && result.UpSize is null)
        {
            return front;
        }

        string sizes = $"正面 {front}";

        if (result.RightSize is { } right)
        {
            sizes += $" / 右向き {right.Width}x{right.Height}";
        }

        if (result.UpSize is { } up)
        {
            sizes += $" / 背面 {up.Width}x{up.Height}";
        }

        return sizes;
    }

    /// <summary>
    /// The box the sizes beside it were measured against.
    ///
    /// Only the front is fitted into it. The other facings are scaled to the height the front
    /// reached and nothing caps their width, so calling the box a plain "上限" while a 19-pixel
    /// side sits in the sheet states a limit the tool does not enforce.
    /// </summary>
    private static string DescribeBox(SkinBuildResult result, SkinOptions resolved)
    {
        string box = $"{resolved.BoxWidth}x{resolved.BoxHeight}";

        return result.RightSize is null && result.UpSize is null
            ? $"上限 {box}"
            : $"正面の上限 {box}、向き別の絵は高さのみ揃え幅は制限しない";
    }

    /// <summary>
    /// Says so when a facing other than the front ended up wider than the box.
    ///
    /// The box is the user's own --width, or the layout's safe drawing area when it was not
    /// given, so nothing new is being decided here - it is the number the summary line has
    /// always printed, now compared against art it never covered.
    ///
    /// Both facings are gathered first so the explanation and the advice are written once. Said
    /// per facing, a run that hands the same wide drawing to --side and --back printed six lines
    /// of stderr, four of them the same two sentences twice over.
    /// </summary>
    private static void WarnAboutWideFacings(SkinBuildResult result, int boxWidth, int cellWidth)
    {
        List<string> over = [];

        foreach ((string facing, (int Width, int Height)? size) in
                 new[] { ("右向き", result.RightSize), ("背面", result.UpSize) })
        {
            if (size is { } placed && placed.Width > boxWidth)
            {
                over.Add($"{facing} {placed.Width}");
            }
        }

        if (over.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"警告: 向き別の絵が配置幅 {boxWidth} を超えている（{string.Join(" / ", over)}）。");
        Console.Error.WriteLine(
            "      向き別の絵は高さだけ正面に揃えるため、幅は制限されない。");
        Console.Error.WriteLine(
            $"      コマ({cellWidth}px)に収めたいなら、正面より横長にならない絵を渡すこと。");
    }

    // ----------------------------------------------------------------- install

    private static int RunInstall(string[] args, CancellationToken cancellation)
    {
        CommandLine cmd = CommandLine.Parse(
            args,
            aliases: new Dictionary<string, string> { ["i"] = "input", ["l"] = "layout" },
            valueOptions: new HashSet<string> { "input", "layout", "mods-dir", "character", "game-dir" },
            knownFlags: new HashSet<string> { "quiet", "list" });

        // "Not given" means detect; "given as nothing" is a mistake and must not mean detect.
        // An unset shell variable in `--mods-dir "$DIR"` otherwise fell back to whatever the
        // machine happened to have, which is a different folder from the one intended.
        RequireValueIfGiven(cmd, "mods-dir", "自動検出させるなら --mods-dir ごと外すこと。");
        RequireValueIfGiven(cmd, "game-dir", "自動検出させるなら --game-dir ごと外すこと。");

        SheetLayout layout = LoadLayout(cmd);

        // When only the candidate destinations are wanted
        if (cmd.HasFlag("list"))
        {
            // --list reads nothing else. Accepting options it then ignores meant a run written
            // as an install, with --list left in by mistake, printed a listing and exited 0 -
            // looking exactly like a successful install that had in fact applied nothing.
            string[] ignored = [.. new[] { "input", "character" }.Where(cmd.HasOption)];
            if (ignored.Length > 0)
            {
                throw new ToolException(
                    $"--list は {string.Join(" / ", ignored.Select(o => $"--{o}"))} を使わない。" +
                    Environment.NewLine + "  配置するなら --list を外すこと。");
            }

            // Honour --mods-dir here as the install path does. Detection is exactly what fails
            // in the situation --mods-dir exists for, and this listing is the only place the
            // slot numbers --character wants can be read, so ignoring it left no way to get
            // them - while any candidates it did find belong to a different user folder, whose
            // slot numbers point at different characters.
            IReadOnlyList<ModConfigLocation> locations = cmd.HasOption("mods-dir")
                ? [GameLocator.Resolve(cmd.GetString("mods-dir"))]
                : GameLocator.FindModConfigLocations();

            // Refused here too, as install refuses it: listed, the saves folder looked like a
            // destination that install would then turn down
            foreach (ModConfigLocation location in locations)
            {
                RefuseSavesFolderAsModsDir(cmd, location);
            }

            if (locations.Count == 0)
            {
                // To stderr, as every other failure is. On stdout it was piped away with the
                // listing a script was collecting, leaving only exit code 1 to explain itself.
                // The same two facts GameLocator gives for the same condition. The game makes
                // <id>\mods as it starts, before the title screen (Manager.EarlyInit, measured on
                // 1.3.0.2), so starting it once is enough; entering a world, as this used to say,
                // is not needed. --mods-dir stays as the way out, since --list accepts it.
                Console.Error.WriteLine(
                    "MOD 設定フォルダが見つからない。ゲームを一度起動すると（タイトル画面が出た時点で）作成される。" +
                    Environment.NewLine +
                    "  場所が分かっている場合は --mods-dir で直接指定すること。");
                return 1;
            }

            Console.WriteLine("MOD 設定フォルダの候補:");
            foreach (ModConfigLocation location in locations)
            {
                Console.WriteLine($"  {location.Describe(),-24} {location.ModsDirectory}");

                // The characters are listed here too: --character needs a number or an
                // identifier, and this is the only place either of them can be read from.
                IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(location);
                if (characters.Count == 0)
                {
                    // Told apart as install tells them apart. A --mods-dir with no saves folder
                    // beside it was listed as having no characters, and somebody who already has
                    // some could not see that the folder was what was wrong.
                    //
                    // Only for a folder the user gave. A detected one is always the game's own
                    // <id>\mods, which the game makes at its first start while saves appears only
                    // with the first character - so there the missing saves folder does mean "no
                    // character yet", and advice about --mods-dir, never used, led nowhere.
                    string saves = CharacterLocator.SavesDirectoryFor(location);
                    if (cmd.HasOption("mods-dir") && !Directory.Exists(saves))
                    {
                        Console.WriteLine(
                            $"      セーブフォルダが見つからない: {saves}" + Environment.NewLine +
                            "      --mods-dir には、その隣に saves フォルダがある mods フォルダを指定すること。");
                        continue;
                    }

                    Console.WriteLine("      操作キャラクターなし（ゲームで1体以上作成すること）");
                    continue;
                }

                IReadOnlySet<string> installed = CharacterSkins.InstalledGuids(
                    location.ModsDirectory,
                    SheetInstaller.DefaultModFolderName);

                foreach (GameCharacter character in characters)
                {
                    string mark = installed.Contains(character.Guid) ? "適用中" : "未適用";
                    string kind = character.IsCreative ? " ［クリエイティブ］" : string.Empty;

                    // The slot is what --character takes, and it is not the displayed number
                    // once creative characters are in the list, so both are shown.
                    Console.WriteLine(
                        $"      スロット {character.SlotIndex,2}  {character.Guid}  [{mark}]  " +
                        character.Describe("(名前なし)") + kind);
                }

                // Images whose character was deleted in game. Nothing else ever mentions them,
                // so without this the only way to notice they are still there is to look in the
                // folder, and the only way to remove them is to uninstall the mod.
                IReadOnlyList<string> orphans = CharacterSkins.OrphanedGuids(
                    location.ModsDirectory,
                    SheetInstaller.DefaultModFolderName,
                    characters);

                foreach (string orphan in orphans)
                {
                    Console.WriteLine($"      (削除済み)   {orphan}  [不要]  このキャラクターはゲームに存在しない");
                }

                if (orphans.Count > 0)
                {
                    Console.WriteLine(
                        $"      → 不要な画像が {orphans.Count} 件ある。" +
                        $"{CharacterSkins.DirectoryFor(location.ModsDirectory, SheetInstaller.DefaultModFolderName)} から削除してよい。");
                }
            }

            return 0;
        }

        // As for generate: an empty value otherwise ended, after the game and the characters had
        // been looked up, in "入力画像が見つからない: " with nothing after it
        RequireValueIfGiven(cmd, "input", "配置するシートのパスを指定すること。");
        InstallSheet(cmd, layout, cmd.GetRequiredString("input"), cmd.HasFlag("quiet"), cancellation);
        return 0;
    }

    /// <summary>
    /// Reads a size option that must be positive when given. Zero means "not specified",
    /// which is only valid when the option is absent altogether.
    /// </summary>
    /// <param name="max">
    /// Largest value that makes sense. Art is placed inside one 26-pixel cell, so a bigger
    /// number cannot help: it only decided how large an intermediate bitmap to build. Without a
    /// ceiling a mistyped 4000 took eighteen seconds, 8000 never finished, and 100000 ended in
    /// SkiaSharp's own allocation failure with a stack trace and exit code 2.
    /// </param>
    private static int RequirePositiveOrUnset(CommandLine cmd, string name, int max)
    {
        if (!cmd.HasOption(name))
        {
            return 0;
        }

        int value = cmd.GetInt(name, 0);
        if (value <= 0 || value > max)
        {
            throw new ToolException($"--{name} は 1〜{max} の範囲で指定すること（指定値: {value}）");
        }

        return value;
    }

    /// <summary>Reads an option that must fall inside a range, rejecting anything outside it.</summary>
    private static int RequireInRange(CommandLine cmd, string name, int fallback, int min, int max)
    {
        if (!cmd.HasOption(name))
        {
            return fallback;
        }

        int value = cmd.GetInt(name, fallback);
        if (value < min || value > max)
        {
            throw new ToolException($"--{name} は {min}〜{max} の範囲で指定すること（指定値: {value}）");
        }

        return value;
    }

    /// <summary>
    /// Reads a path option, falling back when it is absent but refusing a blank one.
    ///
    /// An unset shell variable turns <c>-o "$OUT"</c> into an empty value, which reached
    /// Path.GetFullPath and came back as an ArgumentException with a stack trace. The input side
    /// has always answered that with a plain message, so the output side does too.
    /// </summary>
    private static string RequirePath(CommandLine cmd, string name, string fallback)
    {
        if (!cmd.HasOption(name))
        {
            return fallback;
        }

        string? value = cmd.GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolException($"--{name} に値が無い。出力先のパスを指定すること。");
        }

        // Windows still maps these names to devices wherever they appear in a path. Writing to
        // one succeeded and reported a path, but produced no file at all: the run looked like it
        // had worked, and only the next step - which could not find the file - said otherwise.
        string stem = Path.GetFileNameWithoutExtension(value);
        if (ReservedDeviceNames.Contains(stem))
        {
            throw new ToolException(
                $"--{name} に予約された名前は使えない: {value}" + Environment.NewLine +
                "  Windows がデバイスとして扱うため、ファイルが作られない。");
        }

        // A folder is not a file name. Writing to an existing folder failed only after the whole
        // conversion, with the runtime's English "Access to the path ... is denied.", which reads
        // as a permissions problem; one written with a trailing separator failed with "Could not
        // find a part of the path" after the missing folder had already been created.
        if (Directory.Exists(value) || Path.EndsInDirectorySeparator(value))
        {
            throw new ToolException(
                $"--{name} にはファイル名まで指定すること（フォルダが指定された）: {value}" + Environment.NewLine +
                $"  例: {Path.Combine(value, Path.GetFileName(fallback))}");
        }

        return value;
    }

    /// <summary>Names Windows resolves to a device rather than to a file.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Refuses an option that was written but left empty.
    ///
    /// The parser can tell "absent" from "present but blank"; treating them alike turned an
    /// unset shell variable into a silent fall back to the default, which for a folder means
    /// acting on somewhere the user did not choose.
    /// </summary>
    private static void RequireValueIfGiven(CommandLine cmd, string name, string advice)
    {
        if (cmd.HasOption(name) && string.IsNullOrWhiteSpace(cmd.GetString(name)))
        {
            throw new ToolException($"--{name} に値が無い。" + Environment.NewLine + $"  {advice}");
        }
    }

    /// <summary>
    /// Refuses two options that name the same file.
    ///
    /// Writing the output over the input destroyed the original picture and said nothing about
    /// it: the summary line reads "overwriting", which is true of any second run, so nothing on
    /// screen distinguished replacing an old sheet from destroying the artwork it was made from.
    /// With --quiet it printed nothing at all. The same applies to the template's two outputs,
    /// where the guide landed on the blank template and only the guide survived.
    /// </summary>
    /// <summary>
    /// Reads one of the optional facings, or returns null when it was not given.
    ///
    /// A truncated file is reported the same way the front view's is. It matters more here, not
    /// less: a side view that decoded to half a character would be placed into the walking
    /// frames alone, and the fault would look like a bug in the animation.
    /// </summary>
    private static SKBitmap? DecodeOptional(CommandLine cmd, string option)
    {
        if (!cmd.HasOption(option))
        {
            return null;
        }

        string path = cmd.GetRequiredString(option);
        SKBitmap picture = PixelOps.Decode(path, out bool complete);

        if (!complete)
        {
            Console.Error.WriteLine($"警告: 画像を最後まで読み取れなかった: {path}");
            Console.Error.WriteLine(
                "      欠けた部分は透明として扱われる。ファイルが壊れているか、途中までしか保存されていない可能性がある。");
        }

        return picture;
    }

    private static void EnsureDifferentFiles(string first, string second, string firstName, string secondName)
    {
        string a;
        string b;
        try
        {
            a = PathSafety.Normalize(first);
            b = PathSafety.Normalize(second);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or System.Security.SecurityException)
        {
            // Unresolvable paths fail later with their own message; nothing to compare here
            return;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || LooksLikeSameFile(first, second))
        {
            throw new ToolException(
                $"{secondName} が {firstName} と同じファイルを指している: {second}" + Environment.NewLine +
                "  元のファイルが失われるため、別の名前を指定すること。");
        }
    }

    /// <summary>
    /// Whether two different paths appear to be one file, judged by what the file system records.
    ///
    /// A hard link is not a reparse point, so following links does not reveal it and the two
    /// paths compare as different places - and writing the sheet to one of them destroyed the
    /// picture it had just been made from. Hard links share every timestamp, size and attribute,
    /// because they share the file itself.
    ///
    /// A guess, and deliberately one that errs towards refusing: being told to choose another
    /// name costs a moment, and the alternative costs the original picture. Two genuinely
    /// distinct files agreeing on creation time, last write time and length to the tick is not
    /// something that happens by accident.
    /// </summary>
    private static bool LooksLikeSameFile(string first, string second)
    {
        try
        {
            FileInfo a = new(first);
            FileInfo b = new(second);

            return a.Exists && b.Exists
                && a.Length == b.Length
                && a.CreationTimeUtc == b.CreationTimeUtc
                && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or System.Security.SecurityException)
        {
            // Unreadable either way; the run fails later with its own message
            return false;
        }
    }

    /// <summary>
    /// Reads the colour count, which is either off or a real reduction.
    ///
    /// One colour is rejected further down the pipeline, but a negative value used to pass the
    /// "greater than zero" test as "off" and end the run successfully having reduced nothing,
    /// with no sign in the output that it had been ignored.
    /// </summary>
    private static int RequireColors(CommandLine cmd)
    {
        if (!cmd.HasOption("colors"))
        {
            return 0;
        }

        int value = cmd.GetInt("colors", 0);
        if (value is not 0 && value < 2)
        {
            throw new ToolException(
                $"--colors は 0（減色しない）か 2 以上にすること（指定値: {value}）");
        }

        return value;
    }

    /// <summary>
    /// Refuses a --mods-dir that is a saves folder.
    ///
    /// The saves folder looked for beside the one given is then the given folder itself, so the
    /// characters were found and nothing else noticed: install put the pictures in
    /// saves\CustomPlayerSkin\skins, reported as placed and never read by the mod, and --list showed
    /// the folder as a destination with its characters as not applied.
    /// </summary>
    private static void RefuseSavesFolderAsModsDir(CommandLine cmd, ModConfigLocation location)
    {
        if (cmd.HasOption("mods-dir")
            && string.Equals(
                Path.TrimEndingDirectorySeparator(location.ModsDirectory),
                CharacterLocator.SavesDirectoryFor(location),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(
                $"--mods-dir にセーブフォルダが指定された: {location.ModsDirectory}" + Environment.NewLine +
                "  --mods-dir には、その隣の mods フォルダを指定すること。");
        }
    }

    /// <summary>Installs an existing sheet where the mod reads it; shared by generate --install and install.</summary>
    private static void InstallSheet(
        CommandLine cmd, SheetLayout layout, string sheetPath, bool quiet,
        CancellationToken cancellation)
    {
        string game = CheckGame(cmd);

        ModConfigLocation location = GameLocator.Resolve(cmd.GetString("mods-dir"));
        string modFolder = SheetInstaller.DefaultModFolderName;

        RefuseSavesFolderAsModsDir(cmd, location);

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(location);
        if (characters.Count == 0)
        {
            // Two different problems used to share one message. The advice "create a character
            // in the game" is useless when the saves folder is not where this run is looking,
            // and --mods-dir is exactly what someone reaches for when detection fails - so the
            // person most likely to hit it is the one least able to act on being told to go and
            // make another character.
            //
            // Only for a folder the user gave, as --list does. A detected one is always the game's
            // own <id>\mods, which the game makes at its first start while saves appears only with
            // the first character - so there the missing saves folder does mean "no character
            // yet", and advice about --mods-dir, never used, led nowhere.
            string saves = CharacterLocator.SavesDirectoryFor(location);
            if (cmd.HasOption("mods-dir") && !Directory.Exists(saves))
            {
                throw new ToolException(
                    "セーブフォルダが見つからないため配置できない。" + Environment.NewLine +
                    $"  探した場所: {saves}" + Environment.NewLine +
                    "  --mods-dir には、その隣に saves フォルダがある mods フォルダを指定すること。");
            }

            throw new ToolException(
                "ゲームに操作キャラクターが1体もないため配置できない。" + Environment.NewLine +
                "  Core Keeper でキャラクターを1体以上作成してから実行すること。");
        }

        IReadOnlyList<GameCharacter> targets = SelectCharacters(cmd, characters);

        // Read before installing, so the report can say which characters had a picture already.
        // Without --character the default is everyone, and replacing a picture leaves no copy
        // of the old one, so a run that quietly gave every character the same look was
        // indistinguishable in the output from one that applied to empty slots.
        HashSet<string> alreadyApplied = new(
            CharacterSkins.InstalledGuids(location.ModsDirectory, modFolder),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CharacterSkinResult> results = CharacterSkins.Install(
            sheetPath, layout, location.ModsDirectory, modFolder, [.. targets.Select(c => c.Guid)],
            cancellation);

        // After placing, and even with --quiet, like the version warning: the sheet works, but not
        // in the colours checked, and nothing else would say so. It stays as the user made it.
        if (PixelOps.ColourProfileChangesPixels(sheetPath))
        {
            Console.Error.WriteLine(ColourProfileNote);
        }

        if (!quiet)
        {
            Console.WriteLine();
            // Walked from the results, not zipped against the targets. Two characters can carry
            // the same identifier - copying a save file is all it takes - and the install reduces
            // those to one destination, so the two lists are different lengths. Zipping them
            // then paired every later character with the wrong file and dropped the last one from
            // the report altogether, while its picture had in fact been applied.
            foreach (CharacterSkinResult result in results)
            {
                bool overwrote = alreadyApplied.Contains(result.Guid);

                Console.WriteLine($"配置先          : {result.Path}{(overwrote ? " ※上書き" : string.Empty)}");

                foreach (GameCharacter character in targets.Where(
                    c => string.Equals(c.Guid, result.Guid, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"  キャラクター  : {character.Describe("(名前なし)")}");
                }
            }
        }

        ReportWhetherGameShowsIt(game, quiet);
    }

    /// <summary>
    /// Says whether the game will show what was just placed.
    ///
    /// "It updates within a few seconds" used to be said whatever the state of the mod. With the
    /// mod not in the game nothing there reads the picture at all, and a mod the game refused to
    /// load - a game update is enough - reads it no more than a missing one does. The window has
    /// said so since the audit of 1.3.0.2; this is the same answer for the command line.
    ///
    /// Both of those go to stderr and ignore --quiet, as the version warning does: --quiet leaves
    /// out the report, not the one line saying that none of it will show.
    /// </summary>
    private static void ReportWhetherGameShowsIt(string game, bool quiet)
    {
        string modFolder = SheetInstaller.DefaultModFolderName;
        InstalledModInfo installed = ModPayload.GetInstalled(game, modFolder);

        if (!installed.IsInstalled)
        {
            // The command line has no way to install the mod; the window is where that is done
            Console.Error.WriteLine("注意: MOD がゲームに入っていないため、このままではゲームに反映されない。");
            Console.Error.WriteLine("  cks-gui の「MOD を導入」を押してから、ゲームを起動（起動中なら再起動）すること。");
            return;
        }

        ModLoadVerdict verdict = GameLog.CheckLastRun(game, installed);
        if (verdict.Failure is { } failure)
        {
            ReportLoadFailure(verdict, failure, game, modFolder);
            return;
        }

        if (!quiet)
        {
            Console.WriteLine("ゲームを起動したままでも数秒で反映される（MOD が更新を監視している）。");
        }
    }

    /// <summary>Says that the game's last start did not take the mod, why, and what can be done about it.</summary>
    private static void ReportLoadFailure(ModLoadVerdict verdict, ModLoadFailure failure, string game, string modFolder)
    {
        string when = verdict.Run?.StartedUtc is { } started ? GameLog.DescribeTime(started) : "日時不明";

        if (failure.Kind == ModLoadFailureKind.Patch)
        {
            // Loaded, so not "could not be read"; but with the hooks missing the game never calls
            // into the mod when it rebuilds a look, and the picture may never go on
            Console.Error.WriteLine(
                $"警告: 前回ゲームを起動したとき（{when}）、MOD をゲームに組み込めていない（Harmony）。" +
                "見た目が差し替わらないことがある。");
        }
        else
        {
            Console.Error.WriteLine($"警告: 前回ゲームを起動したとき（{when}）、MOD を読み込めていない: {DescribeFailure(failure)}");
            Console.Error.WriteLine("  このままではゲームに反映されない。");
        }

        // The same two ways out the window gives. A mod that differs from the one this build
        // carries has a fix to hand; one that does not is older than the game, and only a newer
        // build of this tool can bring a mod that suits it.
        Console.Error.WriteLine(ModPayload.InstalledMatchesPayload(game, modFolder)
            ? "  ゲームの更新に MOD がまだ対応していない可能性がある。新しい版の cks が出ていないか確かめること。"
            : "  入っている MOD はこの cks が持つものと違う。cks-gui の「MOD を更新」を押してから、ゲームを再起動すること。");

        Console.Error.WriteLine($"  ゲームのログ: {verdict.LogPath}");
        foreach (string detail in failure.Details)
        {
            Console.Error.WriteLine($"    {detail}");
        }

        Console.Error.WriteLine();
    }

    /// <summary>Why the game did not take the mod, in a phrase that follows "MOD を読み込めていない:".</summary>
    private static string DescribeFailure(ModLoadFailure failure) => failure.Kind switch
    {
        ModLoadFailureKind.CodeSecurity =>
            $"ゲームのコード検査で拒否された（{failure.Summary ?? failure.Reason ?? "詳細はログを参照"}）",
        ModLoadFailureKind.Compile =>
            $"ゲームでのコンパイルに失敗した（{failure.Summary ?? failure.Reason ?? "詳細はログを参照"}）",
        ModLoadFailureKind.AssetBundle => "MOD のアセットバンドルを読み込めなかった",
        ModLoadFailureKind.Manifest => "MOD の ModManifest.json を読めなかった",
        _ => $"ゲームの MOD ローダーがエラーを返した（{failure.Reason ?? "詳細はログを参照"}）",
    };

    /// <summary>
    /// Checks the game the same way the window does at start-up.
    ///
    /// Having no game at all is fatal: there is nowhere to install to. A version this build has
    /// not been checked against only earns a warning, because whether to go ahead anyway is the
    /// user's call rather than this program's.
    /// </summary>
    /// <returns>The installation that was checked, for asking about the mod in it afterwards.</returns>
    private static string CheckGame(CommandLine cmd)
    {
        string? explicitDirectory = cmd.GetString("game-dir");

        if (explicitDirectory is not null && !GameLocator.IsGameDirectory(explicitDirectory))
        {
            throw new ToolException(
                $"--game-dir に Core Keeper が見つからない（CoreKeeper.exe が無い）: {explicitDirectory}");
        }

        string? game = GameLocator.FindGameInstallations(explicitDirectory).FirstOrDefault();
        if (game is null)
        {
            throw new ToolException(
                "Core Keeper のインストール先が見つからない。" + Environment.NewLine +
                "  --game-dir で CoreKeeper.exe があるフォルダを指定すること。");
        }

        GameVersionCheck check = GameVersion.Check(game);
        if (check.State == GameVersionState.Supported)
        {
            return game;
        }

        // Warnings go to stderr so that piping the output of a scripted run still carries them.
        // "could not be read" is kept apart from "not supported": saying the version is outside
        // the supported range when nothing was read at all is a confident claim about something
        // that was never established, and it sends the user looking for the wrong fix.
        Console.Error.WriteLine(check.State == GameVersionState.Unsupported
            ? "警告: 動作を保証できないバージョン。"
            : "警告: ゲームのバージョンを確認できなかったため、対応バージョンか判断できない。");

        Console.Error.WriteLine($"  ゲームの場所      : {game}");
        Console.Error.WriteLine($"  このPCのバージョン: {check.Version ?? "(読み取れず)"}");
        Console.Error.WriteLine($"  対応バージョン    : {GameVersion.DescribeSupported(check.Supported)}");
        Console.Error.WriteLine("  見た目が崩れる、まったく反映されないといったことが起こり得る。");
        Console.Error.WriteLine("  重要なセーブデータは事前に控えを取ること。");
        Console.Error.WriteLine();
        return game;
    }

    /// <summary>
    /// Narrows the characters to those named by <c>--character</c>, or all of them when the
    /// option is absent.
    ///
    /// The slot number is accepted as well as the full identifier, because the identifier is not
    /// something anyone can be expected to type from memory. It is the raw slot as printed by
    /// <c>--list</c>, not the displayed number: creative characters are numbered from one again
    /// on screen, so the displayed number is not unique.
    /// </summary>
    private static IReadOnlyList<GameCharacter> SelectCharacters(
        CommandLine cmd, IReadOnlyList<GameCharacter> characters)
    {
        // "Not given" means everyone; "given as nothing" is a mistake and must not mean everyone.
        // Treating the two alike turned an unset shell variable in `--character "$CH"` into a
        // silent overwrite of every character's picture, and CharacterSkins.Install replaces
        // files outright, so the pictures other characters were using cannot be got back.
        if (!cmd.HasOption("character"))
        {
            return characters;
        }

        string? requested = cmd.GetString("character");
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new ToolException(
                "--character に値が無い。" + Environment.NewLine +
                "  すべてのキャラクターに適用するなら --character ごと外すこと。");
        }

        List<GameCharacter> selected = [];

        foreach (string token in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GameCharacter? match =
                characters.FirstOrDefault(c => string.Equals(c.Guid, token, StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(token, out int slot)
                    ? characters.FirstOrDefault(c => c.SlotIndex == slot)
                    : null);

            if (match is null)
            {
                string list = string.Join(
                    Environment.NewLine,
                    characters.Select(c =>
                        $"    スロット {c.SlotIndex,2}  {c.Guid}  {c.Describe("(名前なし)")}" +
                        (c.IsCreative ? " ［クリエイティブ］" : string.Empty)));

                throw new ToolException(
                    $"--character に指定されたキャラクターが見つからない: {token}" + Environment.NewLine +
                    "  スロット番号か識別子で指定すること。存在するキャラクター:" + Environment.NewLine + list);
            }

            if (!selected.Contains(match))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    // ---------------------------------------------------------------- template

    private static int RunTemplate(string[] args)
    {
        CommandLine cmd = CommandLine.Parse(
            args,
            aliases: new Dictionary<string, string> { ["o"] = "output", ["l"] = "layout" },
            valueOptions: new HashSet<string> { "output", "guide", "layout" },
            knownFlags: new HashSet<string> { "no-guide", "quiet" });

        SheetLayout layout = LoadLayout(cmd);
        string outputPath = RequirePath(cmd, "output", "template.png");

        // Asking for no guide and naming one contradict each other. Honouring --no-guide and
        // dropping --guide without a word left the user waiting for a file that was never going
        // to appear, and the run ended in success.
        if (cmd.HasFlag("no-guide") && cmd.HasOption("guide"))
        {
            throw new ToolException(
                "--no-guide と --guide は同時に指定できない。" + Environment.NewLine +
                "  ガイド画像が要るなら --no-guide を外すこと。");
        }

        // Resolved before anything is written, so the second file cannot land on the first
        string? guidePath = cmd.HasFlag("no-guide")
            ? null
            : RequirePath(cmd, "guide", BuildGuidePath(outputPath));

        if (guidePath is not null)
        {
            EnsureDifferentFiles(outputPath, guidePath, "--output", "--guide");
        }

        // The template is the file people draw on, and the default name is the same every time:
        // running template again in the same folder replaced a drawing with a blank sheet, with
        // the usual output and a successful exit. A blank one is still replaced, so running it
        // twice keeps working; the guide is always regenerated, being made by this command.
        if (File.Exists(outputPath) && !IsBlankImage(outputPath))
        {
            throw new ToolException(
                $"--output の {outputPath} は既にあり、空のテンプレートではない（絵が描かれているか、画像として読めない）。" +
                Environment.NewLine +
                "  上書きすると元に戻せないため、別の名前を -o で指定するか、不要なら先に削除すること。");
        }

        using (SKBitmap blank = SheetComposer.CreateBlankTemplate(layout))
        {
            PixelOps.EncodePng(blank, outputPath);
        }

        if (guidePath is not null)
        {
            using SKBitmap guide = SheetComposer.CreateGuideOverlay(layout);
            PixelOps.EncodePng(guide, guidePath);
        }

        if (!cmd.HasFlag("quiet"))
        {
            Console.WriteLine($"テンプレート  : {Path.GetFullPath(outputPath)} " +
                              $"({layout.Texture.Width}x{layout.Texture.Height} 透明)");
            if (guidePath is not null)
            {
                Console.WriteLine($"ガイド        : {Path.GetFullPath(guidePath)}");
                Console.WriteLine();
                Console.WriteLine("使い方: テンプレートに描き、ガイドは別レイヤーとして重ねて位置合わせに使うこと。");
                Console.WriteLine("        水色=コマ境界 / 桃色=立ち姿の推奨範囲 / 黄色=足元の高さと水平中心");
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a file is a fully transparent image - what template itself writes. A file that
    /// cannot be read as an image, or stops part-way, cannot be shown to be blank, so it is not.
    ///
    /// A file that cannot be opened at all is not answered here: it goes up with its own message.
    /// Taken as "not blank", one held open by another program was refused as a drawing or not an
    /// image - wrong about a blank template, and the advice to delete it fails while it is held.
    /// </summary>
    private static bool IsBlankImage(string path)
    {
        try
        {
            using SKBitmap existing = PixelOps.Decode(path, out bool complete);
            return complete && existing.Pixels.All(p => p.Alpha == 0);
        }
        catch (ToolException ex) when (ex.MessageKey != "error.image.locked")
        {
            return false;
        }
    }

    private static string BuildGuidePath(string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        string name = Path.GetFileNameWithoutExtension(outputPath);
        string extension = Path.GetExtension(outputPath);
        string fileName = $"{name}-guide{extension}";
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    // ---------------------------------------------------------------- validate

    /// <summary>
    /// Said of a sheet whose colour description changes its colours (PixelOps.ColourProfileChangesPixels).
    /// The window converts before placing, so the advice points there as one way round it.
    /// </summary>
    private const string ColourProfileNote =
        "注意: この PNG は sRGB 以外の色プロファイル（iCCP / gAMA）を持っている。ゲームはそれを無視して保存された数値を" +
        "そのまま使うため、cks の検査や cks-gui での見た目と色が変わる。\n" +
        "      sRGB に変換して保存し直すか、cks-gui で開いてから「ゲームへ配置」すること。";

    private static int RunValidate(string[] args)
    {
        CommandLine cmd = CommandLine.Parse(
            args,
            aliases: new Dictionary<string, string> { ["i"] = "input", ["l"] = "layout" },
            valueOptions: new HashSet<string> { "input", "layout" },
            knownFlags: new HashSet<string>());

        SheetLayout layout = LoadLayout(cmd);
        RequireValueIfGiven(cmd, "input", "検査するシートのパスを指定すること。");
        string inputPath = cmd.GetRequiredString("input");

        using SKBitmap sheet = PixelOps.Decode(inputPath, out bool complete);
        List<string> problems = [];

        // A file that stopped part-way through decodes into an image of the right size whose
        // missing part is transparent, so every check below passes and the report says the sheet
        // is fine. The verdict this command exists to give would then be wrong.
        if (!complete)
        {
            problems.Add("ファイルが途中で切れている。読めなかった部分は透明になっている。");
        }

        // Read the same way install reads it: a WEBP or JPEG renamed .png decodes here, and the
        // game, which reads PNG only, then shows nothing of it
        if (!SheetInstaller.HasPngSignature(inputPath))
        {
            problems.Add("PNG 形式ではない。ゲームは PNG しか正しく読めないため、PNG 形式で保存し直すこと。");
        }
        else if (complete && SheetInstaller.PngEndIsMissing(inputPath))
        {
            // Every pixel decoded, so the sentence above about transparent parts would be wrong;
            // what is missing is the end of the file, which the game needs to read it at all
            problems.Add("ファイルの末尾（IEND）が欠けているか壊れている。ゲームはこの PNG を読み込めないため、保存し直すこと。");
        }

        if (sheet.Width != layout.Texture.Width || sheet.Height != layout.Texture.Height)
        {
            problems.Add(
                $"寸法が違う: {sheet.Width}x{sheet.Height} " +
                $"(期待値 {layout.Texture.Width}x{layout.Texture.Height})");
        }

        Console.WriteLine($"検査対象      : {Path.GetFullPath(inputPath)}");
        Console.WriteLine($"寸法          : {sheet.Width}x{sheet.Height}");

        if (problems.Count == 0)
        {
            SKColor[] pixels = sheet.Pixels;
            int emptyFrames = 0;
            int bleedingFrames = 0;
            int semiTransparent = 0;
            HashSet<uint> distinctColors = [];

            foreach (FrameRect frame in layout.Frames)
            {
                int opaque = 0;
                bool touchesEdge = false;

                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        SKColor color = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x];
                        if (color.Alpha == 0)
                        {
                            continue;
                        }

                        opaque++;
                        distinctColors.Add((uint)color);
                        if (color.Alpha is > 0 and < 255)
                        {
                            semiTransparent++;
                        }

                        if (x == 0 || y == 0 || x == frame.W - 1 || y == frame.H - 1)
                        {
                            touchesEdge = true;
                        }
                    }
                }

                if (opaque == 0) { emptyFrames++; }
                if (touchesEdge) { bleedingFrames++; }
            }

            Console.WriteLine($"コマ数        : {layout.Frames.Count}");
            Console.WriteLine($"色数          : {distinctColors.Count}");
            Console.WriteLine($"半透明        : {semiTransparent} px");

            if (emptyFrames > 0)
            {
                problems.Add($"{emptyFrames} コマが完全に空。そのポーズでキャラクターが消える。");
            }

            if (bleedingFrames > 0)
            {
                problems.Add($"{bleedingFrames} コマで絵がコマの端に接している。隣のコマとつながって見える恐れがある。");
            }

            if (semiTransparent > 0)
            {
                Console.WriteLine();
                Console.WriteLine("注意: 半透明ピクセルがある。ゲーム内では輪郭がぼやけて見えることがある。");
                Console.WriteLine("      generate の --alpha-threshold で切り捨てられる。");
            }

            // The colours counted above are converted ones; the game will use the stored numbers
            if (PixelOps.ColourProfileChangesPixels(inputPath))
            {
                Console.WriteLine();
                Console.WriteLine(ColourProfileNote);
            }
        }

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("検査結果      : 問題なし");
            return 0;
        }

        Console.WriteLine("検査結果      : 問題あり");
        foreach (string problem in problems)
        {
            Console.WriteLine($"  - {problem}");
        }

        return 1;
    }

    // ------------------------------------------------------------------ layout

    private static int RunLayout(string[] args)
    {
        CommandLine cmd = CommandLine.Parse(
            args,
            aliases: new Dictionary<string, string> { ["l"] = "layout" },
            valueOptions: new HashSet<string> { "layout" },
            knownFlags: new HashSet<string>());

        SheetLayout layout = LoadLayout(cmd);

        // One set of measurements can be right for several builds, so this names every build
        // they have been checked against rather than only the one they were taken from.
        Console.WriteLine($"対応ゲーム版  : {string.Join(", ", layout.VerifiedOn)}");
        Console.WriteLine($"元テクスチャ  : {layout.SourceTexture}");
        Console.WriteLine($"シート寸法    : {layout.Texture.Width}x{layout.Texture.Height}");
        Console.WriteLine($"1コマの寸法   : {layout.Cell.Width}x{layout.Cell.Height}");
        Console.WriteLine($"コマ数        : {layout.FrameCount}");
        Console.WriteLine($"安全描画域    : x={layout.ContentBox.X} y={layout.ContentBox.Y} " +
                          $"{layout.ContentBox.Width}x{layout.ContentBox.Height}");
        Console.WriteLine($"立ち姿の基準  : x={layout.StandingBox.X} y={layout.StandingBox.Y} " +
                          $"{layout.StandingBox.Width}x{layout.StandingBox.Height} " +
                          $"(足元 y={layout.StandingBox.BaselineY} / 中心 x={layout.StandingBox.CenterX})");
        Console.WriteLine();
        Console.WriteLine("アニメーション:");
        foreach ((string name, int[] indices) in layout.Animations.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {name,-22} {string.Join(", ", indices)}");
        }

        return 0;
    }

    // ------------------------------------------------------------------ Shared helpers

    private static SheetLayout LoadLayout(CommandLine cmd)
    {
        // Every command takes --layout, and an empty value said "レイアウト定義が見つからない: "
        // with nothing after it
        RequireValueIfGiven(cmd, "layout", "埋め込みのレイアウトを使うなら --layout ごと外すこと。");
        string? path = cmd.GetString("layout");
        return path is null ? SheetLayout.LoadEmbedded() : SheetLayout.LoadFile(path);
    }

    /// <summary>Parses a colour in <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> form.</summary>
    private static SKColor ParseColor(string text)
    {
        // Trimmed before anything is measured. NumberStyles.HexNumber allows surrounding white
        // space, so "   " arrived as six blanks, parsed as zero, and gave pure black that no
        // one asked for, while "123456  " parsed as a part-transparent colour. Measuring the
        // length after the trim, and refusing anything that is not a hex digit, is what makes
        // the checks below mean what they say.
        string hex = text.Trim().TrimStart('#').Trim();

        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if (hex.Length == 6)
        {
            hex += "FF";
        }

        if (hex.Length != 8
            || !hex.All(char.IsAsciiHexDigit)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            throw new ToolException($"色の指定を解釈できない: {text}（例: #000000, #1a1a2eff, #fff）");
        }

        return new SKColor(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"cks {Version}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine($"""
            cks {Version} - Core Keeper スキン作成ツール（コマンドライン版）

            使い方:
              cks generate -i <画像> [-o <出力.png>] [--install] [オプション]
              cks install -i <シート.png> [--character <list>] [--mods-dir <path>]
              cks install --list [--mods-dir <path>]
              cks template [-o <template.png>] [--no-guide]
              cks validate -i <シート.png>
              cks layout
              cks version | help

            共通オプション:
              -l, --layout <path>      レイアウト定義の差し替え          既定: 内蔵
                                       generate / install / validate / template / layout で使える
                  --quiet              結果表示を抑制する
                                       generate / install / template で使える
                                       （警告は抑制されず stderr に出る）

            generate  任意の画像をスプライトシート(234x156)へ変換する
              -i, --input <path>       入力画像 (PNG/JPEG/BMP/GIF/WEBP)  ※必須
                                       正面向きの絵。指定が無い向きはこれを使う
              -o, --output <path>      出力先 PNG                        既定: skin.png
                  --side <path>        右向きのコマに使う絵              既定: 正面を流用
                                       右を向いた絵を渡すこと。左右反転はしない
                  --back <path>        背面のコマに使う絵                既定: 正面を流用
                                       指定すると顔隠しは行わない
                                       側面・背面は正面と同じ高さに縮小され、
                                       減色は3枚まとめて行うため色がずれない
                                       高さだけを揃えるので幅は制限されず、
                                       正面より横長の絵は --width の配置幅を超える
                                       （超えた分はコマ 26px を出たところで切り捨て）
                  --anim <style>       static | bob | lively             既定: lively
                       lively = 歩行と攻撃を上下動＋伸縮で動かす
                       bob    = 変形させず位置だけ動かす
                       static = 全コマ同じ絵にする
                  --resample <mode>    smooth | nearest                  既定: smooth
                                       既にドット絵なら nearest が向く
                  --remove-bg          四隅と地続きの背景を透過する
                  --bg-tolerance <n>   背景とみなす色の許容差 0-255       既定: 16
                  --alpha-threshold <n> 半透明を切り捨てる境目 0-255      既定: 128
                  --colors <n>         減色後の色数 (0 で無効、他は2以上) 既定: 0
                  --outline <color>    輪郭線の色 (#000000 など)         既定: 無し
                  --width <n>          配置サイズの幅                    既定: 安全描画域
                  --height <n>         配置サイズの高さ                  既定: 安全描画域
                  --offset-x <n>       水平位置の微調整 (正で右)         既定: 0
                  --offset-y <n>       垂直位置の微調整 (正で下)         既定: 0
                  --no-trim            余白の自動切り詰めをしない
                  --keep-back-face     背面のコマにも正面の絵をそのまま使う
                                       既定では背面のコマの頭部を髪色で塗り、
                                       後頭部に顔が出ないようにする
                  --install            生成後そのままゲームへ配置する
                                       --character / --game-dir / --mods-dir も併用できる
                                       （--install が無いのに指定するとエラーになる）

            install   生成済みシートを MOD の読み込み先へ配置する
              -i, --input <path>       配置するシート PNG   ※--list 以外では必須
                  --mods-dir <path>    MOD 設定フォルダを直接指定
                  --character <list>   適用する操作キャラクター          既定: 全員
                                       スロット番号か識別子をカンマ区切りで指定
                                       番号は --list が表示するスロット番号
                  --game-dir <path>    Core Keeper のインストール先を直接指定
                                       （対応バージョンの確認に使う）
                  --list               配置先とキャラクター一覧を表示するだけ

            template  自分で描くための空テンプレートとガイドを出力する
              -o, --output <path>      空テンプレートの出力先            既定: template.png
                  --guide <path>       ガイド画像の出力先                既定: <出力>-guide.png
                  --no-guide           ガイド画像を出力しない

            validate  生成済みシートが仕様を満たすか検査する
              -i, --input <path>       検査するシート PNG                ※必須

            layout    レイアウト定義の内容を表示する（既定では内蔵の定義）

            例:
              cks generate -i cat.png --remove-bg --outline "#000000" --install
              cks generate -i logo.png --anim bob --colors 16
              cks install -i skin.png
              cks template -o work/template.png
            """);
        return 0;
    }
}
