using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreKeeperSkinTool.Editing;
using CoreKeeperSkinTool.Gui.Editing;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.ViewModels;

/// <summary>An animation that can be played in the preview.</summary>
/// <param name="Key">Name as used by the layout definition, such as idle or run.</param>
/// <param name="Label">Name shown on screen.</param>
public sealed class AnimationChoice(string key, string labelKey) : LocalizedChoice(labelKey)
{
    public string Key { get; } = key;
}

/// <summary>A facing for the preview. Left is drawn by mirroring the right-facing frames.</summary>
/// <param name="Key">Direction name as used by the layout definition.</param>
/// <param name="Label">Name shown on screen.</param>
/// <param name="Mirrored">Whether to draw the frame mirrored.</param>
public sealed class DirectionChoice(string key, string labelKey, bool mirrored) : LocalizedChoice(labelKey)
{
    public string Key { get; } = key;

    /// <summary>Whether to draw it mirrored.</summary>
    public bool Mirrored { get; } = mirrored;
}

/// <summary>
/// Base type for items shown in a list.
/// It exposes a notifying <see cref="Display"/> so only the label changes on a language switch.
/// Unlike relying on ToString, this refreshes the label without losing the selection.
/// </summary>
public abstract class LocalizedChoice(string labelKey) : System.ComponentModel.INotifyPropertyChanged
{
    public string LabelKey { get; } = labelKey;

    public string Display => Loc.Instance[LabelKey];

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void RefreshDisplay() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
}

/// <summary>
/// State of the main window.
///
/// The sheet produced by importing an image is held as an <see cref="EditorDocument"/>.
/// Pixel edits are then applied to it. Changing the import settings rebuilds the sheet,
/// so the user is asked to confirm when edits would be lost.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SheetLayout _layout = SheetLayout.LoadEmbedded();

    /// <summary>
    /// The sheet geometry, exposed so the canvas can draw its guides.
    /// Without this binding <c>PixelCanvas.Layout</c> stays null and the guide overlay,
    /// along with the checkbox that switches it on, does nothing at all.
    /// </summary>
    public SheetLayout Layout => _layout;
    private readonly DispatcherTimer _animationTimer;

    /// <summary>The loaded source image, kept so settings changes do not force a reload.</summary>
    private SKBitmap? _sourceImage;

    /// <summary>
    /// Pictures for the side-facing and back-facing frames, when the user supplied them.
    ///
    /// Optional throughout: with neither, the sheet is built exactly as it always was, from the
    /// front view alone. They live under the same gate as the front picture, because a
    /// conversion reads all three on a worker thread.
    /// </summary>
    private SKBitmap? _sideImage;

    private SKBitmap? _backImage;

    /// <summary>Where the side-facing and back-facing pictures came from, for the panel to show.</summary>
    private string? _sidePath;

    private string? _backPath;

    /// <summary>Token used to collapse a burst of refresh requests.</summary>
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Serialises access to <see cref="_sourceImage"/>.
    /// A conversion reads it on a worker thread, so nothing may replace or dispose it meanwhile.
    /// </summary>
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>
    /// Loads of a picture under way, the front or another facing, from the start of the decode to
    /// the end of what follows it. Touched on the UI thread only.
    /// </summary>
    private int _loadsInFlight;

    /// <summary>
    /// Rebuilds asked for and not yet finished, from the request through its 120 ms wait to the end
    /// of the conversion it starts. Counted across threads, the wait running on the thread pool.
    /// </summary>
    private int _rebuildRequestsInFlight;

    /// <summary>
    /// Pictures that were on their way in and did not arrive: a load or a conversion that failed,
    /// that was refused because the canvas had been drawn on, or that was called off, and a preset
    /// that could not be built. Counted, with what was said about the latest one, for the buttons
    /// that wait for the picture to settle. Waiting through one of these, "Apply to game" and
    /// "Save" went on with the picture from before and said they had applied or saved it, over the
    /// message that said why the new one had not come. Touched on the UI thread only.
    /// </summary>
    private int _picturesNotArrived;

    /// <summary>What was said about the latest picture that did not arrive.</summary>
    private Func<string>? _whyNotArrived;

    /// <summary>
    /// Incremented for every conversion request. Only the request whose number still matches
    /// may publish its result, so a superseded conversion cannot overwrite a newer one.
    /// </summary>
    private int _buildVersion;

    /// <summary>
    /// Incremented for every file opened. Decoding runs on a worker thread, so the load that
    /// finishes last is not necessarily the one asked for last, and only the newest may publish.
    /// </summary>
    private int _loadVersion;

    /// <summary>
    /// Ordering for the side and back pictures, one number each.
    ///
    /// Kept apart from <see cref="_loadVersion"/> because a facing does not replace the front
    /// picture and must not call its load off. Two loads of the same facing do replace each
    /// other, for the same reason the front picture's do: decoding runs on a worker thread, so
    /// the one that finishes last is not necessarily the one asked for last.
    /// </summary>
    private int _sideVersion;
    private int _backVersion;

    /// <summary>Position within the animation currently being played.</summary>
    private int _animationStep;

    private WriteableBitmap? _sheetBuffer;
    private WriteableBitmap? _animationBuffer;

    public MainViewModel()
    {
        BoxWidth = _layout.ContentBox.Width;
        BoxHeight = _layout.ContentBox.Height;

        Animations =
        [
            new AnimationChoice("idle", "anim.idle"),
            new AnimationChoice("run", "anim.run"),
            new AnimationChoice("swing", "anim.swing"),
            new AnimationChoice("aim", "anim.aim"),
            new AnimationChoice("hold", "anim.hold"),
            new AnimationChoice("sit", "anim.sit"),
            new AnimationChoice("sitArmsInFront", "anim.sitArmsInFront"),
        ];
        _selectedAnimation = Animations[1];

        Directions =
        [
            new DirectionChoice("down", "dir.down", false),
            new DirectionChoice("right", "dir.right", false),
            new DirectionChoice("right", "dir.left", true),
            new DirectionChoice("up", "dir.up", false),
        ];
        _selectedDirection = Directions[0];

        Frames = [];
        RebuildFrameList();
        _selectedFrameChoice = Frames[0];

        // Rebuild list labels and the cursor readout whenever the language changes
        Loc.Instance.PropertyChanged += (_, _) => OnLanguageChanged();

        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 8) };
        // Caught here and recorded: an exception escaping a timer's Tick reaches no handler on
        // Avalonia 12.1.1 and stops the timer for good, so the preview froze with no dialog, no
        // log, and Start() - left enabled - doing nothing until play was pressed twice
        _animationTimer.Tick += (_, _) =>
        {
            try
            {
                AdvanceAnimation();
            }
            catch (Exception ex)
            {
                Program.ReportHandled(ex);
            }
        };

        RefreshEnvironment();
        LoadPresets();

        // Said last so it is what stays on screen: without a character there is nothing to apply
        // to, and every other message would only distract from that.
        if (!HasCharacters)
        {
            SetStatus("character.none");
        }
    }

    // ------------------------------------------------------------ Presets

    private PartsLayout? _parts;
    private PresetLibrary? _presetLibrary;

    /// <summary>Every preset, plus the part guide at the front.</summary>
    public ObservableCollection<PresetChoice> Presets { get; } = [];

    [ObservableProperty]
    private PresetChoice? _selectedPreset;

    /// <summary>Set while the list is being populated, so filling it does not load anything.</summary>
    private bool _loadingPresets;

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (_loadingPresets || value is null)
        {
            return;
        }

        ApplyPreset(value, recolourOnly: false);
    }

    /// <summary>
    /// Builds the list of presets and shows the first one.
    ///
    /// Thumbnails are rendered up front: there are thirty of them, each is a handful of
    /// rectangles, and rendering them lazily inside a dropdown would stutter on first open.
    /// </summary>
    private void LoadPresets()
    {
        try
        {
            _parts = PartsLayout.LoadEmbedded();
            _presetLibrary = PresetLibrary.LoadEmbedded();

            FrameRect thumbnailFrame = _layout.Frames.Single(f => f.Index == 0);

            _loadingPresets = true;
            Presets.Clear();

            using (SKBitmap guide = StarterCharacter.Build(_layout, _parts))
            {
                Presets.Add(PresetChoice.PartGuide(
                    PresetChoice.MakeThumbnail(guide, thumbnailFrame, ThumbnailZoom)));
            }

            // Ordered by category so related characters sit together in the list; Avalonia has
            // no grouped items view, so the order is what does the grouping.
            IEnumerable<PresetDefinition> ordered = _presetLibrary.Presets
                .OrderBy(p => Array.IndexOf(_presetLibrary.Categories, p.Category));

            foreach (PresetDefinition definition in ordered)
            {
                using SKBitmap sheet = PresetCharacter.Build(_layout, _parts, definition);
                Presets.Add(PresetChoice.For(
                    definition, PresetChoice.MakeThumbnail(sheet, thumbnailFrame, ThumbnailZoom)));
            }

            _loadingPresets = false;

            // Assigning this runs the handler, which loads the sheet
            SelectedPreset = Presets.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _loadingPresets = false;

            // Failing here is no reason to refuse to start: the user can still open an image
            SetStatus("status.starterFailed", ex);
        }
    }

    /// <summary>How much each thumbnail is scaled up from its 26-pixel frame.</summary>
    private const int ThumbnailZoom = 3;

    /// <summary>
    /// Loads a preset, or applies only its colours to the sheet already open.
    ///
    /// Recolouring exists because trying a palette should not cost the shapes the user has
    /// drawn: it maps every colour of the preset's own sheet onto the corresponding colour of
    /// the current one, leaving every pixel exactly where it is.
    /// </summary>
    public void ApplyPreset(PresetChoice choice, bool recolourOnly)
    {
        if (_parts is null)
        {
            return;
        }

        SupersedePendingBuild();

        try
        {
            using SKBitmap sheet = choice.Definition is null
                ? StarterCharacter.Build(_layout, _parts)
                : PresetCharacter.Build(_layout, _parts, choice.Definition);

            if (recolourOnly && Document is { } current)
            {
                current.Recolour(sheet);
                RefreshPalette();
                RefreshSheetImage();
                UpdateAnimationFrame();
                NotifyHistoryChanged();
                SetStatus(() => Loc.Instance.Format("status.recolored", choice.Display));
                return;
            }

            // Replaced in place when there is hand-drawn work to lose, so one Ctrl+Z brings it
            // back. Building a fresh document threw the undo history away with it, and the
            // preset dropdown reaches here on any selection change - including a stray mouse
            // wheel over it - so edits could vanish with no dialog and no way back.
            if (Document is { } existing && WouldDiscardEdits)
            {
                // Folded, so that trying one preset after another costs one undo step rather than
                // one each. The dropdown reaches here on every selection change, a mouse wheel
                // included, and the history holds sixty-four steps: without folding, a user
                // comparing presets over their own drawing pushed it off the end and lost it.
                existing.Replace(sheet, foldIntoPrevious: true);
            }
            else
            {
                Document = new EditorDocument(sheet, _layout);
            }

            RefreshPalette();
            RefreshSheetImage();
            UpdateAnimationFrame();
            OnPropertyChanged(nameof(HasSourceImage));
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(CanInstallToGame));
            OnPropertyChanged(nameof(NeedsRegenerate));
            NotifyHistoryChanged();

            if (IsPlaying)
            {
                _animationTimer.Start();
            }

            // A preset made from a picture is shaded art scaled down, nearly every pixel its own
            // colour, so the fill and the colour replace reach a pixel or two - "paint over it as
            // it is" promised what they could not do there.
            SetStatus(() => choice.Definition is null
                ? Loc.Instance["status.starter"]
                : !choice.Definition.IsDrawn
                    ? Loc.Instance.Format("status.presetLoadedPicture", choice.Display)
                    : Loc.Instance.Format("status.presetLoaded", choice.Display));
        }
        catch (Exception ex)
        {
            SetStatus("status.starterFailed", ex);
            NoteNotArrived();
        }
    }

    /// <summary>Applies the colours of the selected preset without touching the shapes.</summary>
    public void RecolourToSelectedPreset()
    {
        if (SelectedPreset is { } choice)
        {
            ApplyPreset(choice, recolourOnly: true);
        }
    }

    /// <summary>
    /// Builds a character from a random combination and shows it.
    ///
    /// The thirty presets are a starting point rather than the whole range; this reaches the
    /// rest of it without asking anyone to author more entries.
    /// </summary>
    public void LoadRandomCharacter()
    {
        if (_parts is null || _presetLibrary is null)
        {
            return;
        }

        SupersedePendingBuild();

        try
        {
            PresetDefinition random = RandomPreset.Create(_presetLibrary, Random.Shared);
            using SKBitmap sheet = PresetCharacter.Build(_layout, _parts, random);

            // The same protection the preset list has, which this button never got. Building a
            // fresh document throws the undo history away with it, so pressing Random over
            // hand-drawn work destroyed it outright - no dialog beforehand and nothing able to
            // bring it back, which is worse than the preset case it was fixed alongside. Folded
            // for the same reason too: pressing Random until something appeals costs one undo
            // step rather than one per press.
            if (Document is { } existing && WouldDiscardEdits)
            {
                existing.Replace(sheet, foldIntoPrevious: true);
            }
            else
            {
                Document = new EditorDocument(sheet, _layout);
            }

            RefreshPalette();
            RefreshSheetImage();
            OnPropertyChanged(nameof(HasSourceImage));
            UpdateAnimationFrame();
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(CanInstallToGame));
            OnPropertyChanged(nameof(NeedsRegenerate));
            NotifyHistoryChanged();

            if (IsPlaying)
            {
                _animationTimer.Start();
            }

            SetStatus(() => Loc.Instance.Format("status.presetLoaded", Loc.Instance["preset.random"]));
        }
        catch (Exception ex)
        {
            SetStatus("status.starterFailed", ex);
            NoteNotArrived();
        }
    }

    /// <summary>The sheet being edited; null before an image is imported.</summary>
    public EditorDocument? Document { get; private set; }

    /// <summary>Entry point for switching language; the list at the top right binds to it directly.</summary>
    public Loc Localization => Loc.Instance;

    /// <summary>
    /// The window's title, carrying the tool's version. The version follows the game's - 1.3.0 for
    /// Core Keeper 1.3.0.x - and nothing else in the window said which one was running.
    /// </summary>
    public string WindowTitle => $"{Loc.Instance["app.title"]} v{ProductVersion}";

    /// <summary>The tool's version as major.minor.patch, set once in src/Directory.Build.props.</summary>
    internal static string ProductVersion { get; } =
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>
    /// The character the sheet was taken from, or null when it came from a file.
    ///
    /// A captured appearance is filed under the character's identifier inside the mod's own
    /// settings folder, so its path names a place the user never chose and a file they cannot
    /// recognise. What they picked was a character, so a character is what is shown.
    ///
    /// The choice itself is kept rather than its name as text. The text was made in the language
    /// of the moment, so after a switch the label still read "［クリエイティブ］" or "（名前なし）"
    /// while everything around it had changed; Display reads the current language each time.
    /// </summary>
    private CharacterChoice? _fetchedFrom;

    /// <summary>Path of the loaded image, or guidance text when nothing is loaded.</summary>
    public string SourceDisplay =>
        _fetchedFrom is { } character
            ? Loc.Instance.Format("character.fetchedFrom", character.Display)
            : SourcePath ?? Loc.Instance["top.dropHint"];

    partial void OnSourcePathChanged(string? value)
    {
        // Opening a file replaces anything taken from the game, so the label goes back to the path
        _fetchedFrom = null;
        OnPropertyChanged(nameof(SourceDisplay));
    }

    /// <summary>Last cursor position, used to rebuild the readout after a language change.</summary>
    private (int X, int Y)? _lastCursor;

    /// <summary>
    /// The notice that a language switch could not be saved, while it is what the line says, and
    /// the message it was written over.
    ///
    /// The notice is about one switch. Written again like any other message, it stayed after a
    /// later switch had saved - naming a place that could by then be written, and saying the
    /// language would go back at the next start when it would not (the final review of
    /// 2026-10-02). A switch that saves puts back what the notice covered.
    /// </summary>
    private Func<string>? _languageNotice;

    /// <inheritdoc cref="_languageNotice"/>
    private Func<string>? _beforeLanguageNotice;

    /// <summary>
    /// Rebuilds list labels and already-composed strings when the language changes.
    /// Only bound strings refresh automatically, so these are updated explicitly.
    /// </summary>
    private void OnLanguageChanged()
    {
        foreach (LocalizedChoice choice in Animations.Cast<LocalizedChoice>()
                     .Concat(Directions)
                     .Concat(Frames))
        {
            choice.RefreshDisplay();
        }

        // The preset list is built once at start-up and never rebuilt, so its rows carry their
        // own refresh. Without it every preset name and group label stayed in the old language
        // for the rest of the session while the whole window around them changed.
        foreach (PresetChoice preset in Presets)
        {
            preset.RefreshDisplay();
        }

        OnPropertyChanged(nameof(SourceDisplay));
        OnPropertyChanged(nameof(PlayButtonLabel));
        OnPropertyChanged(nameof(GearButtonLabel));
        OnPropertyChanged(nameof(StaticPreviewNote));
        OnPropertyChanged(nameof(WindowTitle));

        // Read from the localisation on every get, and nothing else raises them: the mod button
        // is only notified when the installed state actually changes, which a language switch
        // does not do, and the "no characters" panel had no raiser at all.
        OnPropertyChanged(nameof(ModButtonLabel));
        OnPropertyChanged(nameof(ModButtonTip));
        OnPropertyChanged(nameof(NoCharactersMessage));

        // These two read the localisation only while no file is chosen, which is the state the
        // window opens in - so the one line that stayed in the old language was the one every
        // user sees first.
        OnPropertyChanged(nameof(SideImageLabel));
        OnPropertyChanged(nameof(BackImageLabel));

        RefreshEnvironment();

        if (_lastCursor is { } cursor)
        {
            UpdateCursorInfo(cursor.X, cursor.Y);
        }

        // The notice that the last switch could not be saved goes, if nothing has been said since:
        // this switch says afresh, below, whether it saved
        if (_languageNotice is not null && ReferenceEquals(_statusRecipe, _languageNotice))
        {
            _statusRecipe = _beforeLanguageNotice;
        }

        _languageNotice = null;
        _beforeLanguageNotice = null;

        // Written again from the recipe that produced it, so the line follows the switch rather
        // than staying in the language it was composed in. A line written as a plain string has
        // no recipe to follow; the opening hint stands in while nothing has been said yet.
        if (_statusRecipe is { } compose)
        {
            // Caught broadly, and only here. Writing a message for the first time must be allowed
            // to fail where it is written - that is where a broken message is a bug worth seeing.
            // This write happens inside the language switch, so a throw would escape into the
            // list that started it and leave the window half-changed. Keeping the text that is
            // already there is what the line did before it followed the switch at all, and the
            // recipe is dropped so a broken one is not tried again on every later switch.
            try
            {
                SetStatus(compose);
            }
            catch (Exception)
            {
                _statusRecipe = null;
            }
        }
        else if (Document is null)
        {
            SetStatus("status.start");
        }

        // Last, over whatever the line was rewritten to: the language changed on screen but did
        // not reach the settings file, so it will not be there at the next start. Said, as a game
        // folder that could not be saved is.
        if (!Loc.Instance.LastLanguageSaved)
        {
            string covered = Status;
            _beforeLanguageNotice = _statusRecipe ?? (() => covered);
            SetStatus("status.languageNotSaved", SettingsFile.DescribePath());
            _languageNotice = _statusRecipe;
        }
    }

    // ------------------------------------------------------------ View state

    [ObservableProperty]
    private string? _sourcePath;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// How the message on the status line was written, kept so it can be written again.
    ///
    /// A finished sentence cannot be translated a second time. Until this existed, the status
    /// line stayed in the language it had been composed in while every other string in the
    /// window followed the language switch, and only the next action put it right. Holding on to
    /// the function that wrote the message - rather than to the text it produced - lets the same
    /// message be written again in the language now in force.
    ///
    /// Null when the line was last written by assigning a plain string, which carries no
    /// instructions for writing it again.
    /// </summary>
    private Func<string>? _statusRecipe;

    /// <summary>
    /// Forgets how the line was written whenever it is written as a plain string.
    ///
    /// Messages should go through SetStatus, but an assignment that does not must never leave
    /// the previous recipe behind: a language switch would then bring back a message the caller
    /// had already replaced. New text left untranslated is the milder failure of the two.
    /// </summary>
    partial void OnStatusChanged(string value) => _statusRecipe = null;

    /// <summary>
    /// Writes the status line, remembering how the message was composed so that it can be
    /// composed again after a language switch.
    ///
    /// The message is composed here and now, so a fault in composing it surfaces where the
    /// message is written rather than in the middle of a language switch.
    ///
    /// Whatever the message reads must still mean the same thing when it is read again: a value
    /// copied into a local, or something that translates itself such as <c>choice.Display</c>.
    /// Reading a property the user can change in the meantime - <see cref="PenColor"/>, say -
    /// would rewrite history, so pass it to the overload that takes arguments instead: that one
    /// takes them as they stand now.
    /// </summary>
    public void SetStatus(Func<string> compose)
    {
        Status = compose();

        // Set afterwards on purpose: assigning Status runs OnStatusChanged, which clears this.
        _statusRecipe = compose;
    }

    /// <summary>Writes a message that takes no arguments.</summary>
    public void SetStatus(string key) => SetStatus(() => Loc.Instance[key]);

    /// <summary>
    /// Writes a message with its arguments filled in.
    /// The arguments are taken as they stand now, so nothing that changes later can rewrite it.
    /// </summary>
    public void SetStatus(string key, params object?[] args) =>
        SetStatus(() => Loc.Instance.Format(key, args));

    /// <summary>Writes the description of a fault as the whole message.</summary>
    public void SetStatus(Exception cause) => SetStatus(() => Loc.Instance.Describe(cause));

    /// <summary>
    /// Writes a message whose only argument is the description of a fault.
    /// The description is resolved as the line is written, so it is translated along with it.
    /// </summary>
    public void SetStatus(string key, Exception cause) =>
        SetStatus(() => Loc.Instance.Format(key, Loc.Instance.Describe(cause)));

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Whether a captured appearance keeps the helmet and armour bands.
    ///
    /// Off by default. Equipment is whatever the character happened to be wearing when the game
    /// wrote the capture, not part of how they look, and a drawing started from one is easier to
    /// work on without a helmet already painted over the head.
    /// </summary>
    [ObservableProperty]
    private bool _includeEquipment;

    /// <summary>The sheet shown on the editing surface.</summary>
    [ObservableProperty]
    private WriteableBitmap? _sheetImage;

    /// <summary>The frame currently shown in the animation preview.</summary>
    [ObservableProperty]
    private WriteableBitmap? _animationImage;

    [ObservableProperty]
    private string _cursorInfo = string.Empty;

    public bool HasDocument => Document is not null;

    /// <summary>
    /// Whether there is a picture for the import settings to work on.
    ///
    /// False after opening a finished sheet, which is edited as it is. The settings panel is
    /// disabled then: left enabled it accepted every change and did nothing with any of them,
    /// with no message to say why.
    /// </summary>
    public bool HasSourceImage => _sourceImage is not null;

    /// <summary>Whether a picture was supplied for the side-facing frames.</summary>
    public bool HasSideImage => _sideImage is not null;

    /// <summary>Whether a picture was supplied for the back-facing frames.</summary>
    public bool HasBackImage => _backImage is not null;

    /// <summary>Name of the side-facing picture, or a word saying there is none.</summary>
    public string SideImageLabel => FacingLabel(_sidePath);

    /// <summary>Name of the back-facing picture, or a word saying there is none.</summary>
    public string BackImageLabel => FacingLabel(_backPath);

    private string FacingLabel(string? path) =>
        path is { Length: > 0 } ? Path.GetFileName(path) : Loc.Instance["views.none"];

    // ------------------------------------------------------------ Import settings

    [ObservableProperty]
    private bool _removeBackground = true;

    [ObservableProperty]
    private int _backgroundTolerance = 16;

    [ObservableProperty]
    private bool _trim = true;

    [ObservableProperty]
    private int _boxWidth;

    [ObservableProperty]
    private int _boxHeight;

    [ObservableProperty]
    private int _offsetX;

    [ObservableProperty]
    private int _offsetY;

    [ObservableProperty]
    private int _alphaThreshold = 128;

    [ObservableProperty]
    private bool _useQuantize;

    [ObservableProperty]
    private int _colors = 16;

    /// <summary>
    /// Off by default. An outline only ever applies to an imported picture - presets draw their
    /// own - and an imported picture almost always has one already, so adding a second cost
    /// twice over: the border took roughly a third of the sprite's pixels, and making room for
    /// it shrank the art itself by two pixels each way. Measured on a generated character, the
    /// added border alone was 44 of the 137 opaque pixels and turned the hair into a black cap.
    /// </summary>
    [ObservableProperty]
    private bool _useOutline;

    [ObservableProperty]
    private string _outlineColor = "#101018";

    [ObservableProperty]
    private bool _useNearest;

    /// <summary>
    /// Whether to build a walk and an attack that actually move.
    ///
    /// On by default: without it every frame holds the identical picture, and the result looks
    /// like a bug rather than a choice. Only run and swing have more than one frame in the
    /// sheet, so nothing else changes either way.
    /// </summary>
    [ObservableProperty]
    private bool _useMotion = true;

    /// <summary>Whether to move the art without ever reshaping it.</summary>
    [ObservableProperty]
    private bool _motionShiftOnly;

    /// <summary>
    /// Whether to mirror the art in the side-facing frames.
    ///
    /// The game draws only right-facing frames and mirrors them to face left, so a picture that
    /// faces left keeps facing left while the character walks right. Turning this on makes an
    /// asymmetric picture face the way it is travelling. Off by default, because it is exactly
    /// wrong for a picture that already faces right.
    /// </summary>
    [ObservableProperty]
    private bool _mirrorSideFrames;

    /// <summary>
    /// Whether to fill in the head on the thirteen frames that show the character from behind.
    ///
    /// One picture goes into all thirty-nine frames, so without this the face drawn for the
    /// front turns up on the back of the head as well, eyes and all. Measured from the game's
    /// own character, its back-facing frames carry no eyes at all and its hair reaches further
    /// down to cover where the face would be. On by default for that reason.
    /// </summary>
    [ObservableProperty]
    private bool _hideFaceOnBackFrames = true;

    partial void OnRemoveBackgroundChanged(bool value) => RequestRebuild();
    partial void OnBackgroundToleranceChanged(int value) => RequestRebuild();
    partial void OnTrimChanged(bool value) => RequestRebuild();
    partial void OnBoxWidthChanged(int value) => RequestRebuild();
    partial void OnBoxHeightChanged(int value) => RequestRebuild();
    partial void OnOffsetXChanged(int value) => RequestRebuild();
    partial void OnOffsetYChanged(int value) => RequestRebuild();
    partial void OnAlphaThresholdChanged(int value) => RequestRebuild();
    partial void OnUseQuantizeChanged(bool value) => RequestRebuild();
    partial void OnColorsChanged(int value) => RequestRebuild();
    partial void OnUseOutlineChanged(bool value) => RequestRebuild();
    partial void OnOutlineColorChanged(string value) => RequestRebuild();
    partial void OnUseNearestChanged(bool value) => RequestRebuild();
    partial void OnUseMotionChanged(bool value) => RequestRebuild();
    partial void OnMotionShiftOnlyChanged(bool value) => RequestRebuild();
    partial void OnMirrorSideFramesChanged(bool value) => RequestRebuild();
    partial void OnHideFaceOnBackFramesChanged(bool value) => RequestRebuild();

    // ------------------------------------------------------------ Editing settings

    [ObservableProperty]
    private EditorTool _tool = EditorTool.Pen;

    [ObservableProperty]
    private bool _editAllFrames = true;

    /// <summary>
    /// Whether every stroke is also drawn on the other side of the frame.
    ///
    /// The character faces the viewer and is symmetric about the middle of the cell, so an eye,
    /// a shoulder or a boot has a twin. Off by default: the twin is right for a face and wrong
    /// for anything deliberately one-sided, and it is easier to notice a missing reflection than
    /// an unwanted one.
    /// </summary>
    [ObservableProperty]
    private bool _mirrorDrawing;

    [ObservableProperty]
    private string _penColor = "#FFFFFF";

    [ObservableProperty]
    private int _zoom = 8;

    [ObservableProperty]
    private bool _showGrid = true;

    [ObservableProperty]
    private bool _showGuides = true;

    [ObservableProperty]
    private bool _useLightBackground;

    /// <summary>Frame being edited; null shows the whole sheet at once.</summary>
    [ObservableProperty]
    private FrameRect? _selectedFrame;

    /// <summary>Frame selection, kept in step with <see cref="SelectedFrame"/>.</summary>
    [ObservableProperty]
    private FrameChoice? _selectedFrameChoice;

    partial void OnSelectedFrameChoiceChanged(FrameChoice? value) => SelectedFrame = value?.Frame;

    public ObservableCollection<FrameChoice> Frames { get; }

    public bool CanUndo => Document?.CanUndo == true;

    public bool CanRedo => Document?.CanRedo == true;

    // ------------------------------------------------------------ Preview settings

    public IReadOnlyList<AnimationChoice> Animations { get; }

    public IReadOnlyList<DirectionChoice> Directions { get; }

    [ObservableProperty]
    private AnimationChoice _selectedAnimation;

    [ObservableProperty]
    private DirectionChoice _selectedDirection;

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private int _framesPerSecond = 8;

    [ObservableProperty]
    private int _animationZoom = 8;

    partial void OnSelectedAnimationChanged(AnimationChoice value)
    {
        _animationStep = 0;
        UpdateAnimationFrame();
    }

    partial void OnSelectedDirectionChanged(DirectionChoice value) => UpdateAnimationFrame();

    /// <summary>
    /// Label for the play button.
    ///
    /// The button is a toggle that starts switched on, so a fixed "play" caption told the user
    /// the opposite of what pressing it would do. Naming the action it will perform makes the
    /// current state readable at a glance, which matters here because a sheet built from a
    /// single picture looks identical whether it is playing or not.
    /// </summary>
    public string PlayButtonLabel => Loc.Instance[IsPlaying ? "preview.pause" : "preview.play"];

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
        }

        OnPropertyChanged(nameof(PlayButtonLabel));
    }

    partial void OnFramesPerSecondChanged(int value) =>
        _animationTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(value, 1, 30));

    // ------------------------------------------------------------ Import

    /// <summary>
    /// Loads an image and starts an editing session.
    ///
    /// A file whose dimensions are exactly the sheet's is opened as a finished sheet rather than
    /// converted. Everything this window saves is such a sheet, and putting one back through the
    /// conversion trimmed the whole 234x156 picture down to sixteen pixels and stamped that into
    /// all thirty-nine cells - so saving and reopening one's own work returned something else
    /// entirely. No ordinary source picture is exactly this size by accident, and one that is
    /// would be better opened as a sheet anyway.
    /// </summary>
    public async Task LoadImageAsync(string path)
    {
        // Decoding runs on a worker thread, so two loads can be in flight at once and the slower
        // one is not necessarily the older one. Without this, opening a large picture and then a
        // small one left the large one's result landing last and replacing it.
        int load = ++_loadVersion;

        // Taken here, before the decode and before the wait on the gate. Both of those happen
        // with the canvas live, and both can take seconds on a large picture: read any later and
        // whatever was drawn in the meantime would count as something the user agreed to lose.
        (EditorDocument? Document, bool Edited) baseline = (Document, WouldDiscardEdits);

        // The revision at the same moment. A document that is already edited stays IsModified
        // however much more is drawn on it, so what was drawn after this is told by the count.
        long revisionAtStart = Document?.Revision ?? 0;

        // Shown for the decode too, not only for the conversion that follows. An eight-thousand
        // pixel photograph spends a quarter of a second here with nothing on screen to say so,
        // and the canvas stays live throughout, so the window looks idle while it is not.
        IsBusy = true;

        // Counted for "Apply to game" and "Save", which wait until nothing is on its way in
        _loadsInFlight++;

        try
        {
            (SKBitmap decoded, bool complete) = await Task.Run(() =>
            {
                SKBitmap bitmap = PixelOps.Decode(path, out bool readWhole);
                return (bitmap, readWhole);
            });

            if (load != _loadVersion)
            {
                decoded.Dispose();
                return;
            }

            if (_layout.IsFinishedSheet(decoded.Width, decoded.Height))
            {
                bool? asSheet = await OpenAsSheetAsync(decoded);

                // Dismissed without answering, so neither reading was chosen and nothing is
                // opened. Converting on the way out - which is what dismissing used to do -
                // turned "I picked the wrong file" into the very conversion the question was
                // asked to avoid, and there was nothing to undo afterwards.
                if (asSheet is null)
                {
                    decoded.Dispose();
                    SetStatus("status.loadCancelled");
                    NoteNotArrived();
                    return;
                }

                if (load != _loadVersion)
                {
                    decoded.Dispose();
                    return;
                }

                if (asSheet == true)
                {
                    await LoadAsSheetAsync(decoded, path, complete, load, baseline, revisionAtStart);
                    return;
                }
            }

            // A conversion already in flight is still reading the previous bitmap on a worker
            // thread. Freeing it here would pull the pixel buffer out from under SkiaSharp,
            // so the swap waits for that conversion to finish first.
            await _buildGate.WaitAsync();
            try
            {
                // Checked again with the gate held: waiting for it can take as long as a
                // conversion, and another file may have been opened in the meantime.
                if (load != _loadVersion)
                {
                    decoded.Dispose();
                    return;
                }

                // Nothing drawn on the document now showing, and that document put there by a
                // conversion of the previous picture that finished while this one was being read
                // or waited here. That is not a change the user made, so the reading is taken
                // again: kept, it made the new picture's conversion count as replaced and throw
                // itself away, leaving this file's name over the old picture and a message naming
                // a button that was not on screen. Whether there was drawing when the file was
                // chosen does not matter: the only conversion that can publish over a drawing is
                // one whose loss the user already agreed to. Anything drawn on the new document
                // keeps WouldDiscardEdits true and the reading as it was.
                if (!WouldDiscardEdits && !ReferenceEquals(Document, baseline.Document))
                {
                    baseline = (Document, false);
                    revisionAtStart = Document?.Revision ?? 0;
                }

                _sourceImage?.Dispose();
                _sourceImage = decoded;
                SourcePath = path;

                // The command line has always warned about this; the window did not, and the
                // window is the way most people load a picture. A download that stopped halfway
                // decodes into an image whose missing part is transparent, and the conversion
                // then trims to what survived and scales it up to fill the frame, so the result
                // looks deliberate.
                //
                // Written here, with the picture, and not as soon as the decode finished: the
                // check above sends a superseded load home without publishing, and a flag set
                // before it stayed behind. Choosing a preset or the random character while a
                // conversion held the gate did just that, and the banner - which stays until
                // something else is opened - then described a file that was never opened, over a
                // complete picture and a summary line about that other picture.
                _truncatedSource = !complete;
                SourceWasTruncated = !complete;

                // Raised here, where the picture arrives, rather than only when a conversion
                // publishes. The import settings panel is bound to this, and the conversion that
                // follows can decline to publish - it does exactly that when the canvas was drawn
                // on while the file was being read. The panel then stayed disabled for the rest of
                // the session with a picture loaded behind it, and no setting could be reached.
                OnPropertyChanged(nameof(HasSourceImage));
            }
            finally
            {
                _buildGate.Release();
            }

            // Opening an image replaces everything the user had when they chose it - but only
            // that, which is why the reading taken at the top goes with it.
            await RebuildAsync(discardEdits: true, baseline, revisionAtStart);
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
            NoteNotArrived();
        }
        catch (Exception ex) when (PixelOps.IsAllocationFailure(ex))
        {
            // Too little memory for the pictures: said with the remedy, not as the runtime's
            // English sentence (which is kept, a native failure having other possible causes)
            SetStatus("status.outOfMemory", ex.Message);
            NoteNotArrived();
        }
        catch (Exception ex)
        {
            SetStatus("status.loadFailed", ex);
            NoteNotArrived();
        }
        finally
        {
            _loadsInFlight--;

            // Only the newest load may put the indicator out, for the same reason the conversion
            // checks its own number: a superseded load finishing first would report "done" while
            // the one that replaced it is still running.
            if (load == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Opens a finished sheet for editing, without converting it.
    ///
    /// The import settings play no part from here on: there is no source picture to apply them
    /// to. The previous one is released and the offer to rebuild goes with it, so the settings
    /// cannot silently replace a sheet they had nothing to do with.
    /// </summary>
    /// <summary>
    /// Asked when a file is the sheet's size but does not look like one, to choose between
    /// opening it unchanged and converting it. True opens it as a sheet, false converts it, and
    /// null means the question was dismissed without answering - Escape, or the close button -
    /// in which case nothing is opened at all.
    ///
    /// Set by the window, which is the only part that can put a dialog on screen. Left unset -
    /// in tests, say - the sheet reading is taken, which is what the size alone used to decide.
    /// </summary>
    public Func<Task<bool?>>? AskWhetherToOpenAsSheet { get; set; }

    /// <summary>
    /// Asks whether hand-made edits may be lost, or null when nothing is there to ask.
    ///
    /// Opening a file asks this from the window, before the view model is called at all. Taking a
    /// character's appearance is started from a button inside the list, which binds to a command
    /// here, so the question has to be reachable from here too.
    /// </summary>
    public Func<Task<bool>>? AskWhetherToDiscardEdits { get; set; }

    /// <summary>
    /// Loads a picture for the side-facing frames.
    ///
    /// Kept apart from the front picture on purpose: it is optional, it never opens as a
    /// finished sheet, and losing it costs nothing that cannot be reloaded - so none of the
    /// questions the front picture has to ask apply here.
    /// </summary>
    public Task LoadSideImageAsync(string path) => LoadFacingAsync(path, side: true);

    /// <summary>Loads a picture for the back-facing frames.</summary>
    public Task LoadBackImageAsync(string path) => LoadFacingAsync(path, side: false);

    /// <summary>Goes back to using the front picture for the side-facing frames.</summary>
    public Task ClearSideImageAsync() => LoadFacingAsync(null, side: true);

    /// <summary>Goes back to using the front picture for the back-facing frames.</summary>
    public Task ClearBackImageAsync() => LoadFacingAsync(null, side: false);

    /// <summary>
    /// Swaps in - or removes - one of the optional facings and converts again.
    ///
    /// The swap happens under the build gate for the same reason the front picture's does: a
    /// conversion on a worker thread is reading these bitmaps, and freeing one underneath it
    /// would pull the pixel buffer out from under SkiaSharp.
    /// </summary>
    private async Task LoadFacingAsync(string? path, bool side)
    {
        // Watched, not raised. Opening a finished sheet frees these two bitmaps under the gate,
        // and a facing still being decoded would otherwise take the gate afterwards and put one
        // back - the panel is disabled in that state, so the file could not be removed again,
        // and the next ordinary picture opened would silently be built with a facing nobody
        // asked for. Reading the number is enough to catch that.
        //
        // Raising it as well called off whatever the front picture was doing. Clearing a facing
        // while a large picture was still being opened threw that picture away without a word,
        // leaving the previous picture's success message on screen: measured at 270ms for a
        // 4000x4000 decode, and longer again while a conversion holds the gate. Ordering these
        // loads after the front picture's was never the point - not landing on top of one was.
        int load = _loadVersion;

        // Each facing orders itself against its own loads only, so two files dropped on the same
        // button still replace each other and neither facing disturbs the other.
        int facingLoad = side ? ++_sideVersion : ++_backVersion;

        // Left behind either by a newer load of this facing, or by whatever replaces the front
        // picture - opening a sheet and choosing a preset both free these bitmaps.
        bool Superseded() =>
            load != _loadVersion || facingLoad != (side ? _sideVersion : _backVersion);

        SKBitmap? decoded = null;
        bool complete = true;
        bool busy = false;

        // Counted for "Apply to game" and "Save", which wait until nothing is on its way in
        _loadsInFlight++;

        try
        {
            if (path is { Length: > 0 })
            {
                IsBusy = true;
                busy = true;

                (decoded, complete) = await Task.Run(() =>
                {
                    SKBitmap bitmap = PixelOps.Decode(path, out bool readWhole);
                    return (bitmap, readWhole);
                });

                if (Superseded())
                {
                    decoded.Dispose();
                    return;
                }
            }

            await _buildGate.WaitAsync();
            try
            {
                // Checked again with the gate held: waiting for it can take as long as a
                // conversion, and a sheet may have been opened in the meantime.
                if (Superseded())
                {
                    decoded?.Dispose();
                    return;
                }

                // The truncation is remembered with the picture, here where it is known to be put
                // in use - the rule the front picture follows. Clearing a facing hands no picture
                // over, which puts its warning away too.
                if (side)
                {
                    _sideImage?.Dispose();
                    _sideImage = decoded;
                    _sidePath = path;
                    _sideTruncated = decoded is not null && !complete;
                }
                else
                {
                    _backImage?.Dispose();
                    _backImage = decoded;
                    _backPath = path;
                    _backTruncated = decoded is not null && !complete;
                }

                // Taken over by the field above, so the failure path below must not free it
                decoded = null;
            }
            finally
            {
                _buildGate.Release();
            }

            OnPropertyChanged(side ? nameof(HasSideImage) : nameof(HasBackImage));
            OnPropertyChanged(side ? nameof(SideImageLabel) : nameof(BackImageLabel));

            // A half-written file is worth saying here for the same reason it is for the front
            // picture: the missing part decodes transparent, the conversion trims to what
            // survived and scales it up to fill the frame, and the result looks deliberate. It
            // shows up as one facing's frames holding a different, larger character.
            SetStatus(
                path is { Length: > 0 }
                    ? complete
                        ? (side ? "status.sideLoaded" : "status.backLoaded")
                        : (side ? "status.sideLoadedTruncated" : "status.backLoadedTruncated")
                    : (side ? "status.sideCleared" : "status.backCleared"));

            RequestRebuild();
        }
        catch (Exception ex) when (PixelOps.IsAllocationFailure(ex))
        {
            decoded?.Dispose();
            SetStatus("status.outOfMemory", ex.Message);
            NoteNotArrived();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            decoded?.Dispose();
            SetStatus("status.loadFailed", ex);
            NoteNotArrived();
        }
        finally
        {
            _loadsInFlight--;

            // Only the newest load may put the indicator out, and only if it turned it on. A
            // clear takes no decode and finishes at once, so without this it reported "done"
            // over a picture that was still being opened.
            if (busy && !Superseded())
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Whether to take a sheet-sized file as a finished sheet.
    ///
    /// The size on its own was enough to decide, and for everything this program saves it is
    /// right. It is not enough for somebody's own artwork that happens to be 234x156: that was
    /// carved into thirty-nine unrelated fragments, and because opening a sheet releases the
    /// source picture, every import setting then did nothing at all, without a word - no way
    /// back short of resizing the file in another program.
    /// </summary>
    private async Task<bool?> OpenAsSheetAsync(SKBitmap candidate)
    {
        if (AskWhetherToOpenAsSheet is not { } ask)
        {
            return true;
        }

        SKColor[] pixels = candidate.Pixels;
        int width = candidate.Width;

        if (!_layout.PaintedOutsideFrames((x, y) => pixels[(y * width) + x].Alpha))
        {
            return true;
        }

        return await ask();
    }

    private async Task LoadAsSheetAsync(
        SKBitmap sheet, string path, bool complete, int load,
        (EditorDocument? Document, bool Edited) baseline, long revisionAtStart)
    {
        try
        {
            // What the user agreed to lose is what was on the canvas when they chose the file.
            // Passed in rather than read here: the decode and the confirmation dialog both come
            // before this, the canvas is live through both, and reading now would count anything
            // drawn in the meantime as already agreed to.
            EditorDocument? documentAtStart = baseline.Document;
            bool editedAtStart = baseline.Edited;

            // Taken again for the same reason as in LoadImageAsync: a conversion of the previous
            // picture that published during the decode or the question is not the user's doing,
            // whether or not there was drawing when the file was chosen. Done before the supersede
            // below, after which no conversion can publish, so from here only drawing can change
            // the document - and with the reading now "not edited", drawing on the new document
            // is what the check under the gate catches.
            if (!WouldDiscardEdits && !ReferenceEquals(Document, documentAtStart))
            {
                documentAtStart = Document;
                editedAtStart = false;
                revisionAtStart = Document?.Revision ?? 0;
            }

            SupersedePendingBuild();

            // Superseding raises the load number as well as the build number, so that choosing a
            // preset calls off a file already on its way in. That includes the number this load
            // was handed before it got here, which is now one behind - and the check after the
            // gate compares against it. Left as it was, that check could only ever decline: the
            // sheet was never put on the canvas and nothing was said about it, so opening a
            // finished sheet did nothing at all. Claiming the new number is what makes this load
            // the current one, while still leaving the check able to notice a later one.
            load = _loadVersion;

            // Built before the wait so a malformed sheet is reported without holding the gate.
            // The constructor copies the pixels out, so the bitmap can be freed either way.
            EditorDocument document = new(sheet, _layout);

            // Cancelling a conversion does not stop the one already inside Task.Run; it is
            // still reading _sourceImage. Freeing that bitmap here would pull the pixel buffer
            // out from under SkiaSharp mid-read, so the swap waits for the worker to finish.
            await _buildGate.WaitAsync();
            try
            {
                if (load != _loadVersion)
                {
                    return;
                }

                // Drawn on since this started, or the document swapped out from under it. The
                // second condition applies even when edits were already to be discarded: that
                // permission covered the picture as it stood when the file was chosen. The third
                // is the same for a document that was already edited then, where only the
                // revision can tell further drawing apart; once undone back to nothing to lose,
                // WouldDiscardEdits is false and there is no reason to decline.
                if (!ReferenceEquals(Document, documentAtStart)
                    || (!editedAtStart && WouldDiscardEdits)
                    || (WouldDiscardEdits && Document?.Revision != revisionAtStart))
                {
                    SetStatus("status.loadCancelledByEdits");
                    NoteNotArrived();
                    return;
                }

                _sourceImage?.Dispose();
                _sourceImage = null;

                // A finished sheet is edited as it is, so there is no conversion for the other
                // facings to take part in. Holding them would keep two pictures alive for a panel
                // that is now disabled, and leave them waiting to be applied to whatever picture
                // is opened next.
                _sideImage?.Dispose();
                _sideImage = null;
                _sidePath = null;
                _sideTruncated = false;
                _backImage?.Dispose();
                _backImage = null;
                _backPath = null;
                _backTruncated = false;
            }
            finally
            {
                _buildGate.Release();
            }

            OnPropertyChanged(nameof(HasSideImage));
            OnPropertyChanged(nameof(HasBackImage));
            OnPropertyChanged(nameof(SideImageLabel));
            OnPropertyChanged(nameof(BackImageLabel));

            _truncatedSource = false;
            SourceWasTruncated = !complete;
            _settingsChangedSinceBuild = false;

            Document = document;
            SourcePath = path;

            RefreshPalette();
            OnPropertyChanged(nameof(HasSourceImage));
            RefreshSheetImage();
            UpdateAnimationFrame();
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(CanInstallToGame));
            OnPropertyChanged(nameof(NeedsRegenerate));
            NotifyHistoryChanged();

            if (IsPlaying)
            {
                _animationTimer.Start();
            }

            SetStatus(() => Loc.Instance.Format("status.loadedSheet", path)
                            + (complete ? string.Empty : Loc.Instance["status.sourceTruncated"]));
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
            NoteNotArrived();
        }
        finally
        {
            sheet.Dispose();
        }
    }

    /// <summary>
    /// Applies changed import settings to the sheet.
    /// Hand-made edits are lost, so the caller must confirm before calling this.
    /// </summary>
    public Task RegenerateAsync()
    {
        // Reached only after the view has shown the confirmation dialog, so the user has
        // already agreed to lose the hand edits.
        _ = CancelPending();
        _settingsChangedSinceBuild = false;
        return RebuildAsync(discardEdits: true);
    }

    /// <summary>Whether proceeding would lose hand-made edits.</summary>
    public bool WouldDiscardEdits => Document?.IsModified == true;

    private void RequestRebuild()
    {
        if (_sourceImage is null)
        {
            return;
        }

        // Never rebuild silently over hand edits; the view asks for confirmation first.
        if (WouldDiscardEdits)
        {
            // A rebuild scheduled before the edits must be called off too, otherwise it fires a
            // moment later and takes the edits with it.
            CancelPending();

            _settingsChangedSinceBuild = true;
            SetStatus("status.needRegenerate");
            NoteNotArrived();
            OnPropertyChanged(nameof(NeedsRegenerate));
            return;
        }

        // A change that is being honoured leaves nothing outstanding to offer a rebuild for
        _settingsChangedSinceBuild = false;

        // Swap the source in first, then cancel and dispose the one it replaced. Doing it the
        // other way round leaves _pending pointing at a source that its own task has already
        // disposed, and the next call then throws ObjectDisposedException from Cancel(). That
        // throw happens inside an [ObservableProperty] setter, where the binding system swallows
        // it: the rebuild silently stops happening, the field updates but the change
        // notification does not, and every later settings change fails the same way.
        CancellationTokenSource cts = new();
        CancellationToken token = cts.Token;
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, cts);

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Its task got there first, which is fine: cancelling is only a shortcut.
            }

            previous.Dispose();
        }

        // Counted from here until the conversion it starts has finished, for "Apply to game" and
        // "Save", which wait for it. _pending alone leaves a gap: it is cleared below on a worker
        // thread before the conversion is dispatched and takes the gate.
        Interlocked.Increment(ref _rebuildRequestsInFlight);

        // No token is passed to Task.Run on purpose: with one, an already-cancelled token would
        // skip the delegate entirely.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token);

                // No longer queued: the wait is over and the conversion is about to run. Left in
                // place, _pending still names this finished request, and CancelPending answers
                // "a conversion really was queued" for it - which is the one thing
                // SupersedePendingBuild uses to decide that a settings change is outstanding.
                // Choosing a preset after any settings change had been honoured therefore put
                // "apply the settings and rebuild" back on screen, and pressing it replaced the
                // preset and everything drawn over it with a conversion of the old picture,
                // undo stack included. The guard below was written for exactly that and could
                // not hold while this stayed set.
                //
                // Compare-and-swap, because a later request may already own the field: if it
                // does, that request disposes this source itself and this must not touch it.
                if (Interlocked.CompareExchange(ref _pending, null, cts) == cts)
                {
                    cts.Dispose();
                }

                await Dispatcher.UIThread.InvokeAsync(() => RebuildAsync(discardEdits: false));
            }
            catch (OperationCanceledException)
            {
                // Simply superseded by a later request, so ignore it
            }
            catch (ObjectDisposedException)
            {
                // The source was replaced and disposed while this task was waiting on it
            }
            finally
            {
                Interlocked.Decrement(ref _rebuildRequestsInFlight);
            }
        });
    }

    /// <summary>
    /// Whether an import setting was changed after the sheet was last built.
    ///
    /// Set when a change is refused because it would discard edits, and when a conversion that
    /// was still queued is called off with the change unhonoured. Cleared once a rebuild actually
    /// happens. Without it the Regenerate button appeared as soon as anything was drawn,
    /// which is worse than merely wrong: pressing it rebuilds from the import settings and
    /// throws the drawing away, for a settings change the user never made.
    /// </summary>
    private bool _settingsChangedSinceBuild;

    /// <summary>Whether the picture currently loaded could only be read in part.</summary>
    private bool _truncatedSource;

    /// <summary>
    /// Whether the right-facing and back-facing pictures in use could only be read in part.
    ///
    /// Said on every conversion, as the front's is. Said only when the picture was loaded, the
    /// warning lasted until the conversion that load asks for finished - about 140ms - and not at
    /// all when something had been drawn, since the refusal to rebuild wrote over it at once. The
    /// facing's frames then held what survived scaled up to fill them, with nothing on screen to
    /// say why, while the command line warns about the same file.
    /// </summary>
    private bool _sideTruncated;

    /// <inheritdoc cref="_sideTruncated"/>
    private bool _backTruncated;

    /// <summary>
    /// The sentence naming a facing as what was cut off, or empty when that is not the case.
    ///
    /// Only the front is fitted into the box; the other facings are scaled to the height it reached
    /// and nothing caps their width, so a picture wider in proportion than the front runs past the
    /// frame. The summary said only how many pixels were cut off, beside numbers that are the
    /// front's and fit, and the note there about a narrow source is about the front too: following
    /// it changed nothing, and neither did any setting in the window.
    ///
    /// Said of a facing only when that facing's own frames lost pixels at the sides. A facing a
    /// little wider than the box still fits its frame, and what a vertical offset then cuts off is
    /// the offset's doing: going by the overall count, which is one maximum over every frame, the
    /// note named the facing's width for it and sent the user to change the wrong picture.
    /// </summary>
    internal static string WideFacingNote(
        (int Width, int Height)? right, int rightClippedSideways,
        (int Width, int Height)? up, int upClippedSideways,
        int boxWidth)
    {
        List<string> over = [];

        if (right is { } rightSize && rightSize.Width > boxWidth && rightClippedSideways > 0)
        {
            over.Add(Loc.Instance.Format("status.facingWideRight", rightSize.Width));
        }

        if (up is { } upSize && upSize.Width > boxWidth && upClippedSideways > 0)
        {
            over.Add(Loc.Instance.Format("status.facingWideBack", upSize.Width));
        }

        return over.Count == 0
            ? string.Empty
            : Loc.Instance.Format("status.facingWide", string.Join(" / ", over), boxWidth);
    }

    /// <summary>
    /// Whether whatever is on screen came from a file that stopped part-way through.
    ///
    /// Shown as its own line above the status, and left there until something else is opened.
    /// Said once in the status line it was gone by the next message, and for a sheet opened as
    /// it is - which is not converted again - there was no later message to repeat it in. The
    /// part that could not be read is transparent, so nothing about the result looks wrong.
    /// </summary>
    [ObservableProperty]
    private bool _sourceWasTruncated;

    /// <summary>Whether a rebuild is pending.</summary>
    public bool NeedsRegenerate =>
        _settingsChangedSinceBuild && WouldDiscardEdits && _sourceImage is not null;

    /// <summary>
    /// Runs one conversion.
    ///
    /// Two things have to hold even when the user keeps changing settings while a conversion runs.
    /// Only one conversion may touch <c>_sourceImage</c> at a time, which the gate enforces, and
    /// only the newest request may publish its result, which the version number enforces.
    /// Without the latter, a slow conversion finishing after a faster newer one would replace the
    /// document with output the current settings no longer describe.
    /// </summary>
    /// <summary>
    /// Drops any conversion that is queued or already running, because the caller is about to
    /// decide what the document holds.
    ///
    /// Choosing a preset used to leave a debounced rebuild armed: it fired a moment later and
    /// replaced the character the user had just picked with the converted image, while the
    /// preset dropdown went on showing the preset. The settings change the rebuild was for is
    /// still outstanding afterwards, so the offer to rebuild comes back.
    /// </summary>
    private void SupersedePendingBuild()
    {
        bool cancelledQueuedBuild = CancelPending();
        _buildVersion++;

        // A load in flight has to be called off too, not only a conversion. Only the build
        // number was raised before, and a load raises its own conversion once its decode
        // finishes - so choosing a preset while a file was being opened let the file land on
        // top of the preset a moment later, with no undo and with the preset's name still
        // showing in the box, which meant picking it again raised no event and could not undo it.
        _loadVersion++;

        // Bumping the number leaves no successor, and a conversion already running clears the
        // indicator only when its own number still matches - which it no longer does. Nothing
        // else would ever put it out, so the progress bar stayed on screen for good.
        IsBusy = false;

        // Only when a conversion really was queued. A queued conversion means a settings change
        // is still waiting to be honoured, and the offer to rebuild should come back - but this
        // runs for a preset and for the random character too, and there nothing was outstanding.
        // Raising the flag regardless put "apply the settings and rebuild" on screen after the
        // user had merely picked a character and drawn on it, and pressing it - which is what
        // the button asks for - replaced both with a conversion of a picture they had moved on
        // from, with nothing to undo.
        if (!cancelledQueuedBuild)
        {
            return;
        }

        if (_sourceImage is not null)
        {
            _settingsChangedSinceBuild = true;
            OnPropertyChanged(nameof(NeedsRegenerate));
        }
    }

    /// <summary>Cancels a debounced rebuild that has not run yet.</summary>
    /// <returns>Whether a conversion really was queued, and so really was called off.</returns>
    private bool CancelPending()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, null);
        if (previous is null)
        {
            return false;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed itself
        }

        previous.Dispose();
        return true;
    }

    /// <param name="discardEdits">
    /// Whether the caller has the user's consent to throw away hand-drawn pixels. Only the two
    /// paths the user asks for directly - opening an image and pressing "rebuild from settings" -
    /// pass true. A debounced rebuild passes false and gives up rather than destroying work.
    /// </param>
    /// <param name="baseline">
    /// The document and its edited state as they stood when the user asked for this, when that
    /// is not now. A load takes its reading before decoding and before waiting on the gate,
    /// both of which happen ahead of this call and both of which the canvas stays live through:
    /// read here instead, anything drawn in between counts as already agreed to.
    /// </param>
    /// <param name="revisionAtStart">
    /// The document's revision taken with <paramref name="baseline"/>, for the same reason; when
    /// left out, the revision now.
    /// </param>
    private async Task RebuildAsync(
        bool discardEdits, (EditorDocument? Document, bool Edited)? baseline = null,
        long? revisionAtStart = null)
    {
        if (_sourceImage is null)
        {
            return;
        }

        if (!discardEdits && WouldDiscardEdits)
        {
            // Marked outstanding, as the same refusal in RequestRebuild does. Without it the
            // message names a button that is not on screen: NeedsRegenerate also requires this
            // flag, and RequestRebuild clears it when it accepts a change - so by the time the
            // rebuild it scheduled gets here and refuses, the flag is false. The settings change
            // was then dropped with no way in the window to apply it.
            _settingsChangedSinceBuild = true;
            SetStatus("status.needRegenerate");
            NoteNotArrived();
            OnPropertyChanged(nameof(NeedsRegenerate));
            return;
        }

        int version = ++_buildVersion;

        // What the user agreed to lose is what was on the canvas when they asked for this, not
        // whatever they draw while it runs. Opening a large picture takes long enough to draw
        // in, and the canvas stays live throughout: publishing regardless threw that away with
        // no confirmation and no undo, and it landed even while the "your edits will be lost"
        // dialog was still on screen waiting for an answer.
        EditorDocument? documentAtStart = baseline?.Document ?? Document;
        bool editedAtStart = baseline?.Edited ?? WouldDiscardEdits;

        // A document already edited at the start stays IsModified however much more is drawn on
        // it, so drawing during the conversion is told by the revision instead
        long revisionAtStartValue = revisionAtStart ?? Document?.Revision ?? 0;

        IsBusy = true;

        await _buildGate.WaitAsync();
        try
        {
            // Superseded while queued, so let the newer request do the work.
            if (version != _buildVersion)
            {
                return;
            }

            SKBitmap? source = _sourceImage;
            if (source is null)
            {
                return;
            }

            SkinOptions options = BuildOptions();

            // Read with the gate held, so the conversion sees the set of pictures as one state
            SkinSources sources = new(source, _sideImage, _backImage);
            SkinBuildResult built = await Task.Run(() => SkinPipeline.Build(sources, _layout, options));

            // Re-checked here, not just on entry: the conversion runs on a worker thread and the
            // canvas stays live throughout, so the user can have drawn something in the meantime.
            // Publishing now would replace their work along with its undo history.
            // Drawn on since this started, or the document swapped out from under it. The second
            // condition applies even when edits were to be discarded: that permission covered
            // the picture as it stood when the request was made. The third is the same for a
            // document that was already edited then - the usual case for that permission - where
            // only the revision can tell further drawing apart. Drawn and then undone back to
            // nothing to lose, WouldDiscardEdits is false and there is nothing to protect. An undo
            // and a redo in between count as a change too, which declines on the safe side.
            bool editedSinceStart =
                !ReferenceEquals(Document, documentAtStart) || (!editedAtStart && WouldDiscardEdits)
                || (WouldDiscardEdits && Document?.Revision != revisionAtStartValue);

            if (version != _buildVersion || editedSinceStart || (!discardEdits && WouldDiscardEdits))
            {
                built.Sheet.Dispose();

                // Superseded, so say nothing at all: the newer request owns both the outcome and
                // the message, and reporting this one as "the import settings changed" named a
                // cause the user had not touched whenever the newer request was a preset or the
                // random character.
                //
                // It used to also require that nothing had been drawn, on the reasoning that a
                // protected edit was still worth reporting. That could not work: choosing a
                // preset installs a new document, so the drawn-since-start test is true for every
                // preset that superseded a conversion - which is the whole case this exists for.
                // A request that has been superseded is never the one that should speak; if an
                // edit needs protecting, the request that is still current will say so.
                if (version != _buildVersion)
                {
                    return;
                }

                if (editedSinceStart || (!discardEdits && WouldDiscardEdits))
                {
                    // Same as the refusal at the top of this method: the message names the rebuild
                    // button, so the button has to be there to press.
                    _settingsChangedSinceBuild = true;
                    SetStatus("status.needRegenerate");
                    NoteNotArrived();
                    OnPropertyChanged(nameof(NeedsRegenerate));
                }

                return;
            }

            using (built.Sheet)
            {
                Document = new EditorDocument(built.Sheet, _layout);
            }

            // The sheet just published was built from the settings read when this started. A
            // change made while it ran over a drawing the user had agreed to lose was refused by
            // RequestRebuild, because the old document was still edited, and publishing then left
            // an unedited sheet in the old settings with NeedsRegenerate false and the summary over
            // the message - the sheet silently disagreeing with the panel. So that change is asked
            // for again now, over a document that no longer refuses it. Built from the current
            // settings, on the other hand, the sheet leaves nothing outstanding, whatever was
            // refused before: left raised, the flag brought "rebuild from settings" back after one
            // pixel was drawn, for settings already applied.
            if (BuildOptions() == options)
            {
                _settingsChangedSinceBuild = false;
            }
            else
            {
                RequestRebuild();
            }

            OnPropertyChanged(nameof(HasSourceImage));
            RefreshPalette();
            RefreshSheetImage();
            UpdateAnimationFrame();
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(CanInstallToGame));
            OnPropertyChanged(nameof(NeedsRegenerate));
            NotifyHistoryChanged();

            if (IsPlaying)
            {
                _animationTimer.Start();
            }

            // Copied out of the result before the message is written. The sheet it arrived with
            // is disposed just above, and the message outlives this method so that it can be
            // written again after a language switch. The extra sentences are composed
            // inside the message for the same reason: composed here, they would be left behind
            // in the language of the day while the sentence around them changed.
            (int Width, int Height) sourceSize = built.SourceSize;
            (int Width, int Height) trimmedSize = built.TrimmedSize;
            (int Width, int Height) spriteSize = built.SpriteSize;
            int clippedPixels = built.ClippedPixels;
            int boxWidth = built.BoxSize.Width;
            bool usesLittleOfBox = built.UsesLittleOfBox;
            bool truncatedSource = _truncatedSource;
            (int Width, int Height)? rightSize = built.RightSize;
            (int Width, int Height)? upSize = built.UpSize;
            int rightClippedSideways = built.RightClippedSideways;
            int upClippedSideways = built.UpClippedSideways;

            // Read with the gate still held, so they describe the facings this conversion used
            bool sideTruncated = _sideTruncated;
            bool backTruncated = _backTruncated;

            SetStatus(() =>
            {
                string clipped = clippedPixels > 0
                    ? Loc.Instance.Format("status.clipped", clippedPixels)
                    : string.Empty;

                // Right after the count it explains. The frame is the same for every facing, so
                // the count alone cannot say which picture ran past it.
                string wide = WideFacingNote(rightSize, rightClippedSideways, upSize, upClippedSideways, boxWidth);

                // Said on every conversion of a truncated source, not only on the load, because
                // the summary line is rewritten each time and the warning would otherwise
                // disappear the first time a setting changed. The same goes for the facings.
                string truncated = truncatedSource
                    ? Loc.Instance["status.sourceTruncated"]
                    : string.Empty;

                string facingTruncated =
                    (sideTruncated ? Loc.Instance["status.sideTruncated"] : string.Empty)
                    + (backTruncated ? Loc.Instance["status.backTruncated"] : string.Empty);

                // The art keeps its aspect ratio, so a tall narrow source leaves columns of the
                // frame empty - detail that could have been kept. Nothing here can recover it,
                // so the only useful thing to do is say which picture would do better.
                string narrow = usesLittleOfBox
                    ? Loc.Instance.Format("status.sourceNarrow", spriteSize.Width, boxWidth)
                    : string.Empty;

                return Loc.Instance.Format(
                    "status.summary",
                    sourceSize.Width, sourceSize.Height,
                    trimmedSize.Width, trimmedSize.Height,
                    spriteSize.Width, spriteSize.Height,
                    clipped) + wide + truncated + facingTruncated + narrow;
            });
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
            NoteNotArrived();
        }
        catch (Exception ex) when (PixelOps.IsAllocationFailure(ex))
        {
            // Large pictures in all three slots can need several gigabytes; when they are not there
            // the remedy - smaller pictures, or fewer - is what has to be said
            SetStatus("status.outOfMemory", ex.Message);
            NoteNotArrived();
        }
        catch (Exception ex)
        {
            SetStatus("status.convertFailed", ex);
            NoteNotArrived();
        }
        finally
        {
            // Only the newest request may clear the indicator; otherwise a superseded
            // conversion finishing first would report "done" while another is still running.
            if (version == _buildVersion)
            {
                IsBusy = false;
            }

            _buildGate.Release();
        }
    }

    private SkinOptions BuildOptions() => new(
        RemoveBackground: RemoveBackground,
        BackgroundTolerance: BackgroundTolerance,
        Trim: Trim,
        BoxWidth: BoxWidth,
        BoxHeight: BoxHeight,
        Resample: UseNearest ? ResampleMode.Nearest : ResampleMode.Smooth,
        AlphaThreshold: (byte)Math.Clamp(AlphaThreshold, 0, 255),
        Colors: UseQuantize ? Colors : 0,
        // An unreadable outline colour falls back to black here on purpose: the outline exists to
        // separate the silhouette from a dark background, so black is the useful default, and
        // dropping the outline entirely would look like the checkbox had been ignored.
        Outline: UseOutline ? ParseColor(OutlineColor) ?? SKColors.Black : null,
        OffsetX: OffsetX,
        OffsetY: OffsetY,
        Animation: !UseMotion
            ? AnimationStyle.Static
            : MotionShiftOnly ? AnimationStyle.Bob : AnimationStyle.Lively,
        MirrorSideFrames: MirrorSideFrames,
        HideFaceOnBackFrames: HideFaceOnBackFrames);

    // ------------------------------------------------------------ Editing actions

    /// <summary>Handles a press or drag on the editing surface.</summary>
    /// <summary>
    /// Closes the current stroke so the next change starts its own undo step.
    ///
    /// Raised on button release and on losing the pointer. Switching tools while the button is
    /// held produces edits with no matching press, and without this the editor would treat them
    /// as part of the previous stroke, leaving them impossible to undo on their own.
    /// </summary>
    public void EndStroke()
    {
        // A shape has been shown as a preview up to now; letting go is what writes it
        if (_dragOrigin is { } origin && _dragCurrent is { } end)
        {
            CommitShape(origin, end);
        }

        CancelShape();
        _shapeCalledOff = false;

        Document?.EndChange();
        NotifyHistoryChanged();

        // Once per stroke rather than once per pixel: the colours in use cannot change without a
        // stroke, and recounting them is dearer than everything else a pixel costs put together.
        RefreshPalette();
    }

    /// <summary>
    /// Calls off a line or rectangle being dragged, for Escape and for the other button. Returns
    /// whether there was one. The stroke itself goes on until the button comes up, and then writes
    /// nothing.
    /// </summary>
    public bool CancelShapeInProgress()
    {
        if (_dragOrigin is null)
        {
            return false;
        }

        CancelShape();
        _shapeCalledOff = true;
        return true;
    }

    /// <summary>Whether the stroke under way had its shape called off, so it writes nothing more.</summary>
    private bool _shapeCalledOff;

    /// <summary>Forgets the shape in progress, so nothing is written when the button comes up.</summary>
    private void CancelShape()
    {
        _dragOrigin = null;
        _dragCurrent = null;
        RefreshPreview();
    }

    /// <summary>Where the current drag began, for the tools that span two points.</summary>
    private (int X, int Y)? _dragOrigin;

    private (int X, int Y)? _dragCurrent;

    /// <summary>
    /// The tool, and the settings that go with it, as they stood when the drag began.
    ///
    /// Everything about a shape has to be decided by the press, not by the release. Reading the
    /// current values instead meant that switching tool - or the mirror, or the scope - before
    /// letting go wrote something other than what the preview had been showing all along, and
    /// switching to a tool that is not a shape threw the shape away without a word.
    /// </summary>
    private EditorTool _dragTool;

    private bool _dragMirror;

    private EditScope _dragScope;

    /// <summary>
    /// The frame the drag began in, or null if it began outside every frame.
    ///
    /// A shape is clipped to it. Dragging from one frame into the next used to run the line
    /// straight across the boundary and into the neighbour, which is nobody's intent: the frames
    /// are separate pictures that happen to be stored side by side.
    /// </summary>
    private FrameRect? _dragFrame;

    /// <summary>
    /// The pixels a shape tool would write if the button were released now.
    ///
    /// Shown by the canvas as an overlay rather than painted into the sheet: a shape is only
    /// decided once both ends are known, and drawing it into the picture on every pointer move
    /// would have to be undone again on the next one.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<(int X, int Y)> _previewPixels = [];

    private void RefreshPreview() =>
        PreviewPixels = _dragOrigin is { } origin && _dragCurrent is { } end
            ? Spread(ShapePixels(origin, end))
            : [];

    /// <summary>
    /// Everywhere the shape will actually be written: its mirror, and the same position in every
    /// frame, when those are switched on.
    ///
    /// The preview used to show the bare shape in one frame while the release wrote it into all
    /// thirty-nine and mirrored - up to seventy-eight times as many pixels as had been shown.
    /// The freehand tools have never had this problem, because they paint as they go and every
    /// copy appears immediately; only the shapes, which show an overlay instead, could disagree.
    /// </summary>
    private IReadOnlyList<(int X, int Y)> Spread(IReadOnlyList<(int X, int Y)> shape)
    {
        if (Document is not { } document)
        {
            return shape;
        }

        HashSet<(int X, int Y)> spread = [];

        foreach ((int x, int y) in shape)
        {
            Add(x, y);

            if (_dragMirror && document.TryGetFrameAt(x, y, out FrameRect frame))
            {
                Add(frame.X + frame.W - 1 - (x - frame.X), y);
            }
        }

        return [.. spread];

        void Add(int x, int y)
        {
            if (_dragScope != EditScope.AllFrames
                || !document.TryGetFrameAt(x, y, out FrameRect source))
            {
                spread.Add((x, y));
                return;
            }

            int cellX = x - source.X;
            int cellY = y - source.YTopLeft;

            foreach (FrameRect target in document.Layout.Frames)
            {
                if (cellX < target.W && cellY < target.H)
                {
                    spread.Add((target.X + cellX, target.YTopLeft + cellY));
                }
            }
        }
    }

    /// <summary>
    /// The pixels the shape covers, clipped to the frame the drag began in.
    ///
    /// Decided by <see cref="_dragTool"/> rather than by the current tool, so the preview and
    /// what finally lands are always the same shape.
    /// </summary>
    private IReadOnlyList<(int X, int Y)> ShapePixels((int X, int Y) origin, (int X, int Y) end)
    {
        IReadOnlyList<(int X, int Y)> pixels = _dragTool switch
        {
            EditorTool.Line => EditorDocument.LinePixels(origin.X, origin.Y, end.X, end.Y),
            EditorTool.Rectangle =>
                EditorDocument.RectanglePixels(origin.X, origin.Y, end.X, end.Y, filled: false),
            _ => EditorDocument.RectanglePixels(origin.X, origin.Y, end.X, end.Y, filled: true),
        };

        if (_dragFrame is { } frame)
        {
            return EditorDocument.ClipToFrame(pixels, frame);
        }

        // Begun outside every frame, where there is no frame to clip to. The cell it landed in
        // is the bounded answer, as it is for the colour replace tool: unclipped, one rectangle
        // dragged from a gap ran through eleven frames at once.
        if (Document is { } document)
        {
            SKRectI cell = document.CellAt(_dragOrigin?.X ?? 0, _dragOrigin?.Y ?? 0);
            return [.. pixels.Where(p =>
                p.X >= cell.Left && p.X < cell.Right && p.Y >= cell.Top && p.Y < cell.Bottom)];
        }

        return pixels;
    }

    private void CommitShape((int X, int Y) origin, (int X, int Y) end)
    {
        if (Document is not { } document)
        {
            return;
        }

        if (ParseColor(PenColor) is not { } colour)
        {
            SetStatus("status.badColor", PenColor);
            return;
        }

        document.BeginChange();

        foreach ((int x, int y) in ShapePixels(origin, end))
        {
            document.Paint(x, y, colour, _dragScope, _dragMirror);
        }

        RefreshSheetImage();
        UpdateAnimationFrame();
        OnPropertyChanged(nameof(NeedsRegenerate));
    }

    public void HandlePixel(int x, int y, bool isStart) =>
        HandlePixel(x, y, isStart, erase: false, interpolated: false);

    /// <param name="erase">
    /// Erase rather than paint, whatever tool is selected. Set by the right button.
    /// </param>
    public void HandlePixel(int x, int y, bool isStart, bool erase, bool interpolated)
    {
        if (Document is not { } document)
        {
            return;
        }

        EditScope scope = EditAllFrames ? EditScope.AllFrames : EditScope.SingleFrame;

        // A shape called off part-way - Escape, or the other button - leaves the rest of its
        // stroke doing nothing. With its origin gone, the shape tools painted freehand along the
        // remaining drag instead.
        if (isStart)
        {
            _shapeCalledOff = false;
        }
        else if (_shapeCalledOff)
        {
            return;
        }

        // The right button erases outright, so the shape and colour tools step aside for it
        if (erase)
        {
            // A shape still pending from before is dropped, or it would be written as well the
            // moment the button came up, on top of what was just erased. (The other button pressed
            // part-way through a stroke does not reach here - the canvas keeps a stroke to the
            // button that began it - but calls a shape off through CancelShapeInProgress, as
            // Escape does.)
            CancelShape();

            if (isStart)
            {
                document.BeginChange();
            }

            document.Paint(x, y, SKColors.Transparent, scope, MirrorDrawing);

            RefreshSheetImage();
            UpdateAnimationFrame();
            NotifyHistoryChanged();
            OnPropertyChanged(nameof(NeedsRegenerate));
            return;
        }

        // A shape is defined by where the drag began and where it is now, so nothing is written
        // until the button comes up. Until then the view shows where it would land.
        if (isStart && Tool.IsDrag())
        {
            // Everything the shape needs is settled here, at the press. See _dragTool.
            _dragTool = Tool;
            _dragMirror = MirrorDrawing;
            _dragScope = scope;
            _dragFrame = document.TryGetFrameAt(x, y, out FrameRect started) ? started : null;
            _dragOrigin = (x, y);
        }

        if (_dragOrigin is not null)
        {
            _dragCurrent = (x, y);

            // Only the position the pointer actually reached is worth redrawing for. Redrawing
            // for the filled-in ones as well recomputed the whole shape once per pixel crossed -
            // measured at 10 ms and 40 MB for one sample of a sheet-wide rectangle, of which all
            // but the last was overwritten before anything could see it.
            if (!interpolated)
            {
                RefreshPreview();
            }

            return;
        }

        switch (Tool)
        {
            case EditorTool.ColourReplace:
                if (!isStart)
                {
                    return;
                }

                if (ParseColor(PenColor) is not { } replacement)
                {
                    SetStatus("status.badColor", PenColor);
                    return;
                }

                document.BeginChange();

                int changed = document.ReplaceColour(x, y, replacement, scope);
                SetStatus(() => changed > 0
                    ? Loc.Instance.Format("status.colorReplaced", changed)
                    : Loc.Instance["status.colorReplacedNothing"]);

                break;

            case EditorTool.Picker:
                if (isStart)
                {
                    SKColor picked = document.GetPixel(x, y);

                    // A fully transparent pixel - a click one pixel off the outline, at a low
                    // zoom - is no colour to paint with. Taken as one, the pen then erased, over
                    // every frame by default, and all the user was told was "#00000000".
                    if (picked.Alpha == 0)
                    {
                        SetStatus("status.colorPickedTransparent");
                        return;
                    }

                    // Opacity kept, so picking a half-transparent pixel and painting with it
                    // gives back what was picked rather than an opaque version of it.
                    PenColor = Describe(picked);
                    SetStatus("status.colorPicked", PenColor);
                }

                return;

            case EditorTool.Bucket:
                if (!isStart)
                {
                    return;
                }

                // Half-typed or invalid text is not a colour. Painting black instead would
                // stamp something the user never chose over their picture.
                if (ParseColor(PenColor) is not { } fillColor)
                {
                    SetStatus("status.badColor", PenColor);
                    return;
                }

                // The fill works inside a frame, so a click in a gap or an unused cell has
                // nowhere to work. It used to do nothing and say nothing, leaving whatever the
                // last operation had reported on screen - which reads as if the fill worked.
                if (!document.TryGetFrameAt(x, y, out _))
                {
                    SetStatus("status.fillOutsideFrame");
                    return;
                }

                document.BeginChange();

                // Said only when nothing happened. A fill that works is visible on the canvas and
                // needs no sentence, but one that finds the area already this colour changed
                // nothing and left the previous message standing - which reads as if it worked.
                if (document.Fill(x, y, fillColor, scope, out int skippedFrames) == 0)
                {
                    SetStatus("status.fillNothing");
                }
                else if (skippedFrames > 0)
                {
                    // Over every frame, the frames where the clicked spot holds another colour
                    // are left alone - their art differs - and that happens out of sight
                    SetStatus("status.fillSkippedFrames", skippedFrames);
                }

                break;

            case EditorTool.Eraser:
                if (isStart)
                {
                    document.BeginChange();
                }

                document.Paint(x, y, SKColors.Transparent, scope, MirrorDrawing);
                break;

            default:
                if (ParseColor(PenColor) is not { } penColor)
                {
                    SetStatus("status.badColor", PenColor);
                    return;
                }

                if (isStart)
                {
                    document.BeginChange();
                }

                document.Paint(x, y, penColor, scope, MirrorDrawing);
                break;
        }

        RefreshSheetImage();
        UpdateAnimationFrame();
        NotifyHistoryChanged();
        OnPropertyChanged(nameof(NeedsRegenerate));
    }

    /// <summary>Formats the coordinate and colour under the cursor for display.</summary>
    public void UpdateCursorInfo(int x, int y)
    {
        if (Document is not { } document)
        {
            return;
        }

        _lastCursor = (x, y);

        SKColor color = document.GetPixel(x, y);
        string frame = document.TryGetFrameAt(x, y, out FrameRect rect)
            ? Loc.Instance.Format(
                "status.inFrame", rect.Index, $"{rect.Anim}_{rect.Dir}", x - rect.X, y - rect.YTopLeft)
            : Loc.Instance["status.outsideFrame"];

        CursorInfo = color.Alpha == 0
            ? Loc.Instance.Format("status.cursorTransparent", x, y, frame)
            : Loc.Instance.Format(
                // Opacity kept, so the readout and the picker agree about the same pixel.
                "status.cursorColor", x, y, Describe(color), frame);
    }

    public void Undo()
    {
        if (Document?.Undo() == true)
        {
            RefreshPalette();
            RefreshSheetImage();
            UpdateAnimationFrame();
            NotifyHistoryChanged();
        }
    }

    public void Redo()
    {
        if (Document?.Redo() == true)
        {
            RefreshPalette();
            RefreshSheetImage();
            UpdateAnimationFrame();
            NotifyHistoryChanged();
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        // Undo, redo and a recolour all change whether there are edits to lose, and the
        // Regenerate button is bound to that. Raising it only from the drawing path left the
        // button on screen after an undo had already taken the edits back.
        OnPropertyChanged(nameof(NeedsRegenerate));
    }

    // ------------------------------------------------------------ View refresh

    private void RefreshSheetImage()
    {
        if (Document is not { } document)
        {
            return;
        }

        _sheetBuffer = BitmapBridge.Write(_sheetBuffer, document.Pixels, document.Width, document.Height);

        // Re-assigning the same instance does not redraw, so clear it first
        SheetImage = null;
        SheetImage = _sheetBuffer;
    }

    /// <summary>
    /// The colours the sheet actually uses, most common first.
    ///
    /// Typing a hex code was the only way to choose a colour that was not already under the
    /// pointer, which made matching a shade the picture already has a matter of picking it up
    /// first and hoping not to lose it. The palette is the picture's own colours, so the shades
    /// worth reaching for are always one click away.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _palette = [];

    /// <summary>
    /// Recounts the colours in use.
    ///
    /// Called when the sheet changes as a whole - a stroke finishing, an undo, a load - and not
    /// from <see cref="RefreshSheetImage"/>, which runs once per pixel painted. Measured at
    /// 0.43 ms per call against 0.16 ms for the pixel transfer it sat behind, so on a drag that
    /// interpolates forty positions it cost 17 ms a sample and rebuilt all twenty-four buttons
    /// forty times over, thirty-nine of which were thrown away unseen.
    /// </summary>
    private void RefreshPalette()
    {
        if (Document is { } document)
        {
            RefreshPalette(document);
        }
    }

    private void RefreshPalette(EditorDocument document)
    {
        Dictionary<uint, int> tally = [];

        foreach (SKColor colour in document.Pixels)
        {
            if (colour.Alpha == 0)
            {
                continue;
            }

            tally[(uint)colour] = tally.GetValueOrDefault((uint)colour) + 1;
        }

        // Capped because a converted photograph can carry hundreds, and a wall of swatches is
        // no easier to choose from than a text box. Counted distinct before the cap, not after:
        // taking twenty-four tallies first and then removing duplicates among them left fewer
        // than twenty-four swatches whenever the same colour appeared at two levels of opacity.
        List<string> fresh =
        [
            .. tally.OrderByDescending(pair => pair.Value)
                .Select(pair => Describe((SKColor)pair.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxPaletteEntries),
        ];

        // Assigning an equal-but-new list makes the view discard every button and build them
        // again, which is visible as a flicker and is what the swatches cost most of the time.
        if (!fresh.SequenceEqual(Palette, StringComparer.OrdinalIgnoreCase))
        {
            Palette = fresh;
        }
    }

    /// <summary>
    /// Writes a colour the way the colour box reads it.
    ///
    /// Opacity is kept when it is not full, so picking a half-transparent colour and painting
    /// with it gives back the colour that was picked rather than an opaque version of it.
    /// </summary>
    private static string Describe(SKColor colour) =>
        colour.Alpha == 255
            ? $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}"
            : $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}{colour.Alpha:X2}";

    /// <summary>How many swatches the palette shows.</summary>
    private const int MaxPaletteEntries = 24;

    /// <summary>Chooses a colour from the palette.</summary>
    [RelayCommand]
    private void UsePaletteColour(string? colour)
    {
        if (!string.IsNullOrWhiteSpace(colour))
        {
            PenColor = colour;
        }
    }

    private void AdvanceAnimation()
    {
        _animationStep++;
        UpdateAnimationFrame();
    }

    /// <summary>
    /// Explains a preview that cannot move, or an empty string when it can.
    ///
    /// A sheet built from one picture holds the same art in every frame unless the walk bob is
    /// switched on, so the preview looks frozen and the play button appears broken. Saying so
    /// is the difference between a confusing bug and an understood limitation.
    /// </summary>
    public string StaticPreviewNote =>
        _previewIsStatic ? Loc.Instance["preview.staticNote"] : string.Empty;

    /// <summary>Whether the note above has anything to say, used to hide the row entirely.</summary>
    public bool HasStaticPreviewNote => _previewIsStatic;

    private bool _previewIsStatic;

    /// <summary>Whether every frame of an animation holds exactly the same pixels.</summary>
    private static bool FramesAreIdentical(EditorDocument document, int[] indices)
    {
        if (indices.Length < 2)
        {
            return false;
        }

        if (!document.FrameLookup.TryGetValue(indices[0], out FrameRect? first))
        {
            return false;
        }

        IReadOnlyList<SKColor> pixels = document.Pixels;

        foreach (int index in indices.Skip(1))
        {
            if (!document.FrameLookup.TryGetValue(index, out FrameRect? other)
                || other.W != first.W || other.H != first.H)
            {
                return false;
            }

            for (int y = 0; y < first.H; y++)
            {
                for (int x = 0; x < first.W; x++)
                {
                    SKColor a = pixels[((first.YTopLeft + y) * document.Width) + first.X + x];
                    SKColor b = pixels[((other.YTopLeft + y) * document.Width) + other.X + x];
                    if (a != b)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private void UpdateAnimationFrame()
    {
        if (Document is not { } document)
        {
            return;
        }

        string key = $"{SelectedAnimation.Key}_{SelectedDirection.Key}";
        if (!_layout.Animations.TryGetValue(key, out int[]? indices) || indices.Length == 0)
        {
            return;
        }

        bool wasStatic = _previewIsStatic;
        _previewIsStatic = FramesAreIdentical(document, indices);
        if (_previewIsStatic != wasStatic)
        {
            OnPropertyChanged(nameof(StaticPreviewNote));
            OnPropertyChanged(nameof(HasStaticPreviewNote));
        }

        int index = indices[Math.Abs(_animationStep) % indices.Length];
        if (!document.FrameLookup.TryGetValue(index, out FrameRect? frame))
        {
            return;
        }

        _animationBuffer = BitmapBridge.WriteFrame(
            _animationBuffer, document.Pixels, document.Width, frame, SelectedDirection.Mirrored,
            checkerBackground: true);

        AnimationImage = null;
        AnimationImage = _animationBuffer;
    }

    private void RebuildFrameList()
    {
        Frames.Clear();
        Frames.Add(FrameChoice.WholeSheet());
        foreach (FrameRect frame in _layout.Frames.OrderBy(f => f.Index))
        {
            Frames.Add(FrameChoice.For(frame, $"{frame.Index:00}  {frame.Anim}_{frame.Dir} #{frame.Frame}"));
        }
    }

    // ------------------------------------------------------------ Output

    /// <summary>
    /// Whether two paths name the same file, compared the way the command line compares its input
    /// and output (EnsureDifferentFiles): fully resolved, links followed, case ignored.
    ///
    /// A path that cannot be resolved counts as different. The write that follows fails on it
    /// with its own message, which says more than a refusal made up here could.
    /// </summary>
    internal static bool PointsAtSameFile(string first, string second)
    {
        try
        {
            return string.Equals(
                PathSafety.Normalize(first), PathSafety.Normalize(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a write failed because another program has the file open
    /// (ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION).
    ///
    /// The save dialog hands such a file back without a word, and the write opens it for itself
    /// alone, so any other program holding it open - an image viewer, an editor - refuses it.
    /// </summary>
    internal static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// The pictures a conversion is being made from, which saving over would lose.
    ///
    /// Only while each is held as a source. A finished sheet opened as it is leaves the source
    /// picture unset and its path in SourcePath, and saving it back where it came from is how the
    /// help says to carry on with the work - comparing SourcePath on its own refused exactly that.
    /// </summary>
    private IEnumerable<string> HeldSourcePaths()
    {
        if (_sourceImage is not null && SourcePath is { Length: > 0 } source)
        {
            yield return source;
        }

        if (_sideImage is not null && _sidePath is { Length: > 0 } sidePath)
        {
            yield return sidePath;
        }

        if (_backImage is not null && _backPath is { Length: > 0 } backPath)
        {
            yield return backPath;
        }
    }

    /// <summary>
    /// Whether the picture on screen is still about to change: a conversion is queued (the 120 ms
    /// a settings change waits) or running, or a picture is being loaded.
    ///
    /// Read only from markers that always clear themselves - the requests and the loads count down
    /// in finally and the gate is released in finally - and not from IsBusy, which a single slip
    /// would leave set and with it every press of "Apply to game" waiting for good.
    /// </summary>
    private bool ConversionInFlight
    {
        get
        {
            return Volatile.Read(ref _rebuildRequestsInFlight) > 0
                   || _buildGate.CurrentCount == 0
                   || _loadsInFlight > 0;
        }
    }

    /// <summary>
    /// Waits for the picture on screen to settle.
    ///
    /// Polled, because there is no one thing to await: a load asks for a conversion when it is
    /// done, and a setting changed while waiting queues another. Short enough not to be felt; a
    /// large picture keeps it waiting for as long as its conversion takes, which is the point.
    /// </summary>
    private async Task WaitUntilSettledAsync()
    {
        if (!ConversionInFlight)
        {
            return;
        }

        SetStatus("status.waitingForConversion");
        while (ConversionInFlight)
        {
            await Task.Delay(30);
        }
    }

    /// <summary>
    /// Marks the message just written as the reason a picture on its way in did not arrive, for a
    /// button waiting for that picture (see <see cref="NotArrivedSince"/>).
    /// </summary>
    private void NoteNotArrived()
    {
        _picturesNotArrived++;

        // Kept the way the line was written, so it follows a language switch as the line does.
        // SetStatus always leaves one; the text stands in should it ever not.
        string said = Status;
        _whyNotArrived = _statusRecipe ?? (() => said);
    }

    /// <summary>
    /// What was said about a picture that did not arrive after <paramref name="mark"/> was read
    /// from <see cref="_picturesNotArrived"/>, or null when every one that was on its way arrived.
    /// </summary>
    private Func<string>? NotArrivedSince(int mark) => _picturesNotArrived == mark ? null : _whyNotArrived;

    /// <summary>
    /// "Apply to game" as the button does it: after whatever conversion or load is on its way.
    ///
    /// Written at the press, the document was the one before a settings change still waiting its
    /// 120 ms, or before a conversion or a load that was running: the game got the previous
    /// picture, the screen turned into the new one a moment later, and the conversion's summary
    /// wrote over the "applied" message (measured for every import setting, for a large load of 2
    /// to 3 seconds, and for a right-facing or back picture just put in).
    /// </summary>
    public async Task InstallToGameWhenSettledAsync()
    {
        int mark = _picturesNotArrived;
        await WaitUntilSettledAsync();

        // Not written when what it waited for did not come. The game got the picture from before
        // under "applied", and the message saying why the new one had not come - a file that could
        // not be read, too little memory, the rebuild refused over a drawing - was gone (the final
        // review of 2026-10-02). That message stays, and says nothing was applied.
        if (NotArrivedSince(mark) is { } why)
        {
            SetStatus(() => why() + Loc.Instance["status.notAppliedAfterWait"]);
            return;
        }

        InstallToGame();
    }

    /// <summary>"Save" as the button does it: after whatever conversion or load is on its way.</summary>
    public async Task SaveWhenSettledAsync(string path)
    {
        int mark = _picturesNotArrived;
        await WaitUntilSettledAsync();

        // Not written when what it waited for did not come, as "Apply to game" is not
        if (NotArrivedSince(mark) is { } why)
        {
            SetStatus(() => why() + Loc.Instance["status.notSavedAfterWait"]);
            return;
        }

        Save(path);
    }

    /// <summary>
    /// "Remove from characters" as the button does it, after the conversion on its way. The removal
    /// does not depend on the picture; its message did: pressed during a conversion, it was written
    /// over by the conversion's summary the moment that finished.
    /// </summary>
    public async Task RemoveFromCharactersWhenSettledAsync(IReadOnlyList<string> guids)
    {
        int mark = _picturesNotArrived;
        await WaitUntilSettledAsync();
        RemoveFromCharacters(guids);

        // Done whatever became of the picture, which the removal does not use - and the user
        // confirmed it for these characters. Why the picture did not come is said after the
        // removal's report rather than lost under it.
        if (NotArrivedSince(mark) is { } why && _statusRecipe is { } removed)
        {
            SetStatus(() => removed() + "\n" + why());
        }
    }

    public void Save(string path)
    {
        if (Document is not { } document)
        {
            SetStatus("status.noImage");
            return;
        }

        // Never onto a picture the sheet is being made from. The operating system asks only
        // whether to replace "a file", and once replaced the picture is gone: the conversion goes on
        // from the copy in memory, so nothing shows the loss until the file is opened again - as a
        // finished sheet, with the import settings out of reach. The command line refuses the same.
        if (HeldSourcePaths().Any(source => PointsAtSameFile(source, path)))
        {
            SetStatus("status.saveOverSource", path);
            return;
        }

        try
        {
            using SKBitmap sheet = document.ToBitmap();
            PixelOps.EncodePng(sheet, path);
            SetStatus("status.saved", path);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // Said in the window's language with what to do about it. The runtime's own sentence
            // is English whatever the window's language, and names no way out.
            SetStatus("status.saveFailedInUse", path);
        }
        catch (Exception ex)
        {
            SetStatus("status.saveFailed", ex);
        }
    }

    /// <summary>
    /// Writes the current sheet as the appearance of every ticked character.
    ///
    /// Each character gets its own copy of the image, so applying to one leaves the others as
    /// they were and each character can end up looking different.
    /// </summary>
    public void InstallToGame()
    {
        if (Document is not { } document)
        {
            SetStatus("status.noImage");
            return;
        }

        if (!HasCharacters)
        {
            SetStatus("character.none");
            return;
        }

        IReadOnlyList<CharacterChoice> targets = SelectedCharacters;
        if (targets.Count == 0)
        {
            SetStatus("status.noCharacterSelected");
            return;
        }

        string temporary = Path.Combine(Path.GetTempPath(), $"cks-{Guid.NewGuid():N}.png");
        try
        {
            using (SKBitmap sheet = document.ToBitmap())
            {
                PixelOps.EncodePng(sheet, temporary);
            }

            ModConfigLocation location = ResolveModConfigLocation(out Func<string> note);

            try
            {
                CharacterSkins.Install(
                    temporary, _layout, location.ModsDirectory, SheetInstaller.DefaultModFolderName,
                    [.. targets.Select(c => c.Character.Guid)]);
            }
            finally
            {
                // Read back from disk rather than assumed from "the call did not throw". A
                // failure partway through can leave earlier characters already replaced, and
                // marking every row unapplied then hid them from the remove action - so the
                // install could not be undone from here.
                SyncInstalledState(location);
            }

            // Taken now, as note is, so that switching language rewrites the same message
            Func<string> missing = ModMissingNote();

            SetStatus(() => Loc.Instance.Format(
                "status.installedCharacters",
                targets.Count,
                string.Join(" / ", targets.Select(c => c.Display))) + note() + missing());
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
        }
        catch (Exception ex)
        {
            SetStatus("status.installFailed", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // A leftover file in the temp folder is harmless, and throwing from a finally
                // block would replace the real result, success or failure, with this one.
            }
        }
    }

    /// <summary>
    /// Removes the replaced appearance from every ticked character, leaving the mod installed.
    ///
    /// Only those characters' images are deleted, so the others keep theirs. Removing the mod
    /// itself is a separate action, on the button in the top bar.
    /// </summary>
    /// <param name="confirmed">
    /// The characters the user was actually shown and agreed to. Read afresh here, the selection
    /// could have moved on: closing the confirmation activates the window, which rescans the save
    /// folder and rebuilds the rows before the dialog's continuation runs, so what was listed in
    /// the question and what was about to be restored need not be the same set.
    /// </param>
    public void RemoveFromCharacters(IReadOnlyList<string>? confirmed = null)
    {
        IReadOnlyList<CharacterChoice> targets = confirmed is null
            ? SelectedCharacters
            : [.. SelectedCharacters.Where(c => confirmed.Contains(c.Character.Guid, StringComparer.OrdinalIgnoreCase))];

        if (targets.Count == 0)
        {
            SetStatus("status.noCharacterSelected");
            return;
        }

        try
        {
            ModConfigLocation location = ResolveModConfigLocation(out Func<string> note);

            IReadOnlyList<CharacterSkinResult> results;
            try
            {
                results = CharacterSkins.Remove(
                    location.ModsDirectory, SheetInstaller.DefaultModFolderName,
                    [.. targets.Select(c => c.Character.Guid)]);
            }
            finally
            {
                SyncInstalledState(location);
            }

            // A character whose image could not be deleted now comes back as a result carrying
            // the reason, instead of ending the whole removal with an exception. That has to be
            // said: reporting only the successes would leave the user thinking every character
            // they ticked was restored while some still wear the replacement.
            string[] failed =
            [
                .. results.Where(r => r.Error is not null)
                    .Select(r => CharacterDisplay(targets, r.Guid)),
            ];

            int removed = results.Count(r => r.Changed);

            if (failed.Length > 0)
            {
                SetStatus(() => Loc.Instance.Format(
                    "status.removeCharactersPartial", removed, string.Join(" / ", failed)) + note());
                return;
            }

            SetStatus(() => removed > 0
                ? Loc.Instance.Format(
                    "status.removedCharacters",
                    removed,
                    string.Join(" / ", targets.Select(c => c.Display))) + note()
                : Loc.Instance["status.removeCharactersNothing"]);
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
        }
        catch (Exception ex)
        {
            SetStatus("status.removeCharactersFailed", ex);
        }
    }

    /// <summary>
    /// The name to show for one character identifier, falling back to the identifier itself when
    /// the row is no longer in the list.
    /// </summary>
    private static string CharacterDisplay(IReadOnlyList<CharacterChoice> rows, string guid) =>
        rows.FirstOrDefault(c => string.Equals(c.Character.Guid, guid, StringComparison.OrdinalIgnoreCase))
            ?.Display ?? guid;

    /// <summary>
    /// Brings every row's "applied" mark in line with what is actually in the skins folder.
    ///
    /// Called after an install or a removal, including a failed one. Both write file by file, so
    /// a failure partway through leaves some characters changed and some not, and a row that
    /// disagrees with disk cannot be corrected from the window: the refresh on activation
    /// fingerprints the save folder only and never notices the skins folder at all.
    /// </summary>
    private void SyncInstalledState(ModConfigLocation location)
    {
        try
        {
            IReadOnlySet<string> installed = CharacterSkins.InstalledGuids(
                location.ModsDirectory, SheetInstaller.DefaultModFolderName);

            foreach (CharacterChoice choice in Characters)
            {
                choice.HasSkin = installed.Contains(choice.Character.Guid);
            }
        }
        catch (Exception)
        {
            // Reading it back is a correction, not the operation itself; failing to do so must
            // not replace the real result with this one.
        }
    }

    /// <summary>
    /// The characters that would lose their appearance, for the confirmation screen.
    /// Only those that actually have an image are listed; ticking one that has none does nothing.
    /// </summary>
    public IReadOnlyList<string> CharactersWithSkinToRemove() =>
        [.. SelectedCharacters.Where(c => c.HasSkin).Select(c => c.Display)];

    /// <summary>
    /// The identifiers behind <see cref="CharactersWithSkinToRemove"/>, for the caller to hand
    /// back once the user has agreed, so the set that was confirmed is the set that is acted on.
    /// </summary>
    public IReadOnlyList<string> GuidsWithSkinToRemove() =>
        [.. SelectedCharacters.Where(c => c.HasSkin).Select(c => c.Character.Guid)];

    /// <summary>
    /// Picks the mod settings folder to install into.
    ///
    /// <see cref="GameLocator.Resolve"/> refuses to choose when several exist and tells the user
    /// to pass --mods-dir, which is a CLI option this window does not have. Rather than a dead
    /// end, the most recently used folder is chosen and the choice is stated in the status line.
    /// </summary>
    private static ModConfigLocation ResolveModConfigLocation(out Func<string> note)
    {
        IReadOnlyList<ModConfigLocation> candidates = GameLocator.FindModConfigLocations();

        if (candidates.Count <= 1)
        {
            note = static () => string.Empty;
            return GameLocator.Resolve(null);
        }

        // Handed back as a function rather than as finished text, so the status line it is
        // appended to can be written again in another language. Both of its values are read out
        // here: writing it again must not go back to the file system, which is what listed the
        // folders in the first place.
        int count = candidates.Count;
        string chosen = candidates[0].Describe();

        // FindModConfigLocations returns them with the one the game started with last first
        note = () => Environment.NewLine
                     + Loc.Instance.Format("status.multipleConfigs", count, chosen);
        return candidates[0];
    }

    // ------------------------------------------------------------ Characters

    /// <summary>
    /// The player characters saved in the game, each with a tick box.
    ///
    /// A character deleted in the game disappears from here on the next refresh, which is what
    /// stops it from being applied to: the mod files an image under the character's identifier,
    /// and an identifier that no longer belongs to anyone can never be matched again.
    /// </summary>
    public ObservableCollection<CharacterChoice> Characters { get; } = [];

    /// <summary>Whether the game has at least one character to apply to.</summary>
    [ObservableProperty]
    private bool _hasCharacters;

    /// <summary>Explains what to do when the game has no characters at all.</summary>
    public string NoCharactersMessage => Loc.Instance["character.none"];

    /// <summary>Whether anything is ticked, which is what the install and remove actions need.</summary>
    public bool HasSelectedCharacters => Characters.Any(c => c.IsSelected);

    /// <summary>Installing needs both a picture to install and somebody to install it for.</summary>
    public bool CanInstallToGame => HasDocument && HasSelectedCharacters;

    /// <summary>
    /// What the list was read from as of the last scan: the mods folder it went through and the
    /// state of the save folders (see <see cref="CharactersFingerprint"/>).
    /// </summary>
    private string _charactersFingerprint = string.Empty;

    /// <summary>
    /// Re-reads the characters only when the game's save folders have actually changed, or the
    /// mods folder the window would now apply to is not the one the list was read through.
    ///
    /// Called whenever the window regains focus, which is what happens when the user creates a
    /// character in the game and switches back. Without it the list would only ever be as
    /// current as it was when the application started.
    /// </summary>
    public void RefreshCharactersIfChanged()
    {
        string fingerprint;
        try
        {
            IReadOnlyList<ModConfigLocation> candidates = GameLocator.FindModConfigLocations();
            fingerprint = CharactersFingerprint(
                candidates.Count > 0 ? candidates[0] : null, CharacterLocator.Fingerprint());
        }
        catch (Exception)
        {
            // Cannot tell, so leave the list alone rather than rebuilding it on every focus
            return;
        }

        if (fingerprint != _charactersFingerprint)
        {
            RefreshCharacters();
        }
    }

    /// <summary>
    /// What the character list was read from: the mods folder it went through ("-" for none, when
    /// every account's characters are listed) and the state of the save folders.
    ///
    /// The save folders alone were not enough. Applying, removing, fetching and the gear toggle
    /// choose the most recently used mods folder again when pressed, and which one that is follows
    /// the README.txt the game rewrites at every start. Starting the game with another Steam
    /// account and coming back without saving left the list showing the first account while
    /// "Apply to game" wrote into the second one's folder.
    /// </summary>
    internal static string CharactersFingerprint(ModConfigLocation? location, string savesFingerprint) =>
        $"{location?.ModsDirectory ?? "-"}>{savesFingerprint}";

    /// <summary>
    /// Re-reads the characters from the game's save folder and which of them have an image.
    ///
    /// The ticks are carried over by identifier rather than by position, so a character deleted
    /// in game does not silently hand its tick to whichever character took its slot.
    /// </summary>
    public void RefreshCharacters()
    {
        HashSet<string> ticked = [.. Characters.Where(c => c.IsSelected).Select(c => c.Character.Guid)];
        bool hadAny = Characters.Count > 0;

        Characters.Clear();

        // Cleared first and only set once the scan has finished. Recording it up front meant a
        // scan that threw left the list empty and the fingerprint current, so every later window
        // activation compared equal and refused to try again: the character list stayed empty,
        // and Install stayed disabled, until the application was restarted.
        _charactersFingerprint = string.Empty;

        try
        {
            IReadOnlyList<ModConfigLocation> candidates = GameLocator.FindModConfigLocations();
            ModConfigLocation? location = candidates.Count > 0 ? candidates[0] : null;

            // The characters are read from the save folder, which exists even when no mod has
            // ever run; the mods folder does not. Reading them through the mods folder would
            // report "no characters" on a perfectly normal installation.
            IReadOnlyList<GameCharacter> characters = location is null
                ? CharacterLocator.FindAll()
                : CharacterLocator.Find(location);

            IReadOnlySet<string> installed = location is null
                ? new HashSet<string>()
                : CharacterSkins.InstalledGuids(
                    location.ModsDirectory, SheetInstaller.DefaultModFolderName);

            foreach (GameCharacter character in characters)
            {
                CharacterChoice choice = new(character, installed.Contains(character.Guid));

                if (hadAny)
                {
                    choice.IsSelected = ticked.Contains(character.Guid);
                }

                choice.PropertyChanged += OnCharacterChoiceChanged;
                Characters.Add(choice);
            }

            // Only now, with a complete list in hand, does this state count as current
            _charactersFingerprint = CharactersFingerprint(location, CharacterLocator.Fingerprint());
        }
        catch (Exception ex)
        {
            SetStatus("status.characterScanFailed", ex);
        }

        HasCharacters = Characters.Count > 0;
        OnPropertyChanged(nameof(HasSelectedCharacters));
        OnPropertyChanged(nameof(CanInstallToGame));
    }

    private void OnCharacterChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterChoice.IsSelected))
        {
            OnPropertyChanged(nameof(HasSelectedCharacters));
        OnPropertyChanged(nameof(CanInstallToGame));
        }
    }

    /// <summary>Ticks or unticks every character at once.</summary>
    public void SelectAllCharacters(bool selected)
    {
        foreach (CharacterChoice choice in Characters)
        {
            choice.IsSelected = selected;
        }
    }

    /// <summary>The characters the next action applies to.</summary>
    private IReadOnlyList<CharacterChoice> SelectedCharacters =>
        [.. Characters.Where(c => c.IsSelected)];

    // ------------------------------------------------------ Environment and mod install

    /// <summary>The detected game installation; the first one is used when several exist.</summary>
    private string? _gamePath;

    /// <summary>
    /// Whether the game's own shirt, trousers, helmet and armour are drawn over the replacement.
    ///
    /// Off by default: the picture replaces the whole character, and the game's clothing on top
    /// of it rarely lines up. Turning it on writes the mod's settings directly, and because the
    /// game re-reads each setting from disk on every access, it takes effect without a restart.
    /// </summary>
    [ObservableProperty]
    private bool _showGameGear;

    /// <summary>
    /// Set while the toggle is being brought in line with what is already on disk, so that
    /// reading the current state does not write it straight back out again.
    /// </summary>
    private bool _syncingGearSetting;

    /// <summary>
    /// Label for the gear toggle, stating what is happening now rather than what pressing it
    /// would do. The pill's fill already signals which of the two states it is in.
    /// </summary>
    public string GearButtonLabel => Loc.Instance[ShowGameGear ? "gear.on" : "gear.off"];

    partial void OnShowGameGearChanged(bool value)
    {
        OnPropertyChanged(nameof(GearButtonLabel));

        if (_syncingGearSetting)
        {
            return;
        }

        try
        {
            ModConfigLocation location = ResolveModConfigLocation(out Func<string> note);
            ModConfigWriter.SetGearHidden(
                location.ModsDirectory, SheetInstaller.DefaultModFolderName, hide: !value);

            Func<string> missing = ModMissingNote();
            SetStatus(() => Loc.Instance[value ? "status.gearShown" : "status.gearHidden"] + note() + missing());
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
            RevertGearToggle(value);
        }
        catch (Exception ex)
        {
            SetStatus("status.gearFailed", ex);
            RevertGearToggle(value);
        }
    }

    /// <summary>
    /// Puts the toggle back after a write that did not go through.
    ///
    /// Left on the new value, the pill said the gear showed over settings that still hid it, and
    /// the message for a write that stopped part way - running it again switches the rest - held
    /// only if pressing again asked for the same thing: with the pill already on the new value,
    /// the next press asked for the opposite.
    ///
    /// Posted rather than set here. This runs inside the binding's write-back from the click, and
    /// measured on Avalonia 12 the ToggleButton does not take a value changed at that moment: the
    /// view model went back, the pill stayed pressed, and the next click set the value it already
    /// had and wrote nothing. Not read back from disk either: after a partial write the four
    /// settings disagree, IsGearHidden answers "shown" for that, and a retry from there asked for
    /// "hidden" whichever way the failed press had gone.
    /// </summary>
    private void RevertGearToggle(bool attempted) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Set by something else in the meantime, such as a refresh bringing it in line with
            // the disk, which is then the better answer
            if (ShowGameGear != attempted)
            {
                return;
            }

            _syncingGearSetting = true;
            try
            {
                ShowGameGear = !attempted;
            }
            finally
            {
                _syncingGearSetting = false;
            }
        });

    /// <summary>Brings the toggle in line with the setting already written on disk.</summary>
    private void SyncGearSetting()
    {
        try
        {
            IReadOnlyList<ModConfigLocation> candidates = GameLocator.FindModConfigLocations();
            if (candidates.Count == 0)
            {
                return;
            }

            bool hidden = ModConfigWriter.IsGearHidden(
                candidates[0].ModsDirectory, SheetInstaller.DefaultModFolderName);

            _syncingGearSetting = true;
            ShowGameGear = !hidden;
        }
        catch (Exception)
        {
            // The toggle simply keeps its default; nothing here is worth interrupting startup for
        }
        finally
        {
            _syncingGearSetting = false;
        }
    }

    [ObservableProperty]
    private string _environmentStatus = string.Empty;

    /// <summary>Whether the install action may be offered: the game was found and the mod is missing or replaceable.</summary>
    [ObservableProperty]
    private bool _canInstallMod;

    /// <summary>Whether the game was found. Install actions stay hidden when it was not.</summary>
    [ObservableProperty]
    private bool _isGameFound;

    /// <summary>
    /// Whether the mod is currently in the game.
    ///
    /// The one button in the top bar both installs and removes, so this decides which of the two
    /// pressing it does.
    /// </summary>
    [ObservableProperty]
    private bool _isModInstalled;

    /// <summary>
    /// A line for a message that says a change reaches the game, added when the mod is not in
    /// the game and nothing there reads the change at all.
    ///
    /// "If the game is running it updates within a few seconds" was said whatever the state of
    /// the mod. The loader of 1.3.0.2 scans the Mods folder once, as the game starts, so even
    /// after "Install mod" a running game needs a restart. Only when the game was found and the
    /// mod can be installed: IsModInstalled is also false when the game was not found or its
    /// state could not be read, and saying "the mod is missing" then would be a guess.
    /// </summary>
    private Func<string> ModMissingNote()
    {
        bool missing = IsGameFound && CanInstallMod && !IsModInstalled;
        return () => missing ? Environment.NewLine + Loc.Instance["status.modMissingNote"] : string.Empty;
    }

    /// <summary>Label of the install/remove button, stating what pressing it would do.</summary>
    public string ModButtonLabel =>
        Loc.Instance[IsModInstalled ? "action.removeMod" : "action.installMod"];

    /// <summary>Tooltip of the install/remove button.</summary>
    public string ModButtonTip =>
        Loc.Instance[IsModInstalled ? "action.removeModTip" : "action.installModTip"];

    partial void OnIsModInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ModButtonLabel));
        OnPropertyChanged(nameof(ModButtonTip));
    }

    /// <summary>
    /// Whether the mod in the game differs from the one this application carries.
    ///
    /// Shown as its own button rather than folded into the install and remove toggle. Replacing
    /// an installed mod used to mean removing it first, and removing takes the mod's settings
    /// folder with it - which is where every character's applied picture lives.
    /// </summary>
    [ObservableProperty]
    private bool _isModOutdated;

    /// <summary>
    /// Re-examines the environment.
    /// Called at startup and after installing or removing. It only inspects files, so it is instant.
    /// </summary>
    public void RefreshEnvironment()
    {
        try
        {
            SyncGearSetting();
            RefreshCharacters();

            // A folder the user pointed at wins over detection, so an installation in an unusual
            // place stays usable even if Steam's own records cannot be read.
            _gamePath = GameLocator.FindGameInstallations(GamePathSettings.Load()).FirstOrDefault();
            IsGameFound = _gamePath is not null;

            // IsModOutdated is lowered with IsModInstalled on every way out, as the catch below
            // does. These two returned without it, so a game folder that went missing left
            // "MOD を更新" on screen under ゲームが見つかりません.
            if (_gamePath is null)
            {
                EnvironmentStatus = Loc.Instance["env.gameNotFound"];
                CanInstallMod = false;
                IsModInstalled = false;
                IsModOutdated = false;
                ClearModLoadWarning();
                return;
            }

            if (!ModPayload.IsAvailable)
            {
                // A build without the payload, such as one made during development
                EnvironmentStatus = Loc.Instance["env.payloadMissing"];
                CanInstallMod = false;
                IsModInstalled = false;
                IsModOutdated = false;
                ClearModLoadWarning();
                return;
            }

            PayloadInfo? payload = ModPayload.GetInfo();
            InstalledModInfo installed = ModPayload.GetInstalled(
                _gamePath, payload?.Name ?? SheetInstaller.DefaultModFolderName);

            CanInstallMod = true;
            IsModInstalled = installed.IsInstalled;

            // Only worth asking about once something is installed to compare against
            IsModOutdated = installed.IsInstalled
                && !ModPayload.InstalledMatchesPayload(
                    _gamePath, payload?.Name ?? SheetInstaller.DefaultModFolderName);
            EnvironmentStatus = installed.IsInstalled
                ? Loc.Instance.Format("env.modInstalled", _gamePath)
                : Loc.Instance.Format("env.modMissing", _gamePath);

            // After IsModOutdated, which decides what the warning tells the user to press
            UpdateModLoadWarning(installed);
        }
        catch (Exception ex)
        {
            EnvironmentStatus = Loc.Instance.Format("env.checkFailed", Loc.Instance.Describe(ex));
            CanInstallMod = false;
            IsModInstalled = false;
            IsModOutdated = false;
            ClearModLoadWarning();
        }
    }

    /// <summary>
    /// A line under the environment status saying that the game failed to load the mod the last
    /// time it started, and what to do about it. Empty when it did not, or when that cannot be told.
    ///
    /// "MOD 導入済み" was all the bar ever said once the files were in place. The game compiles the
    /// mod when it starts and checks it against its own rules, a game update can make either fail,
    /// and the one place that records it is the game's own log, which is what this is read from.
    /// </summary>
    [ObservableProperty]
    private string _modLoadWarning = string.Empty;

    /// <summary>The lines from the game's log behind the warning, shown when the pointer rests on it.</summary>
    [ObservableProperty]
    private string _modLoadWarningDetail = string.Empty;

    /// <summary>Whether the warning line is shown.</summary>
    public bool HasModLoadWarning => ModLoadWarning.Length > 0;

    partial void OnModLoadWarningChanged(string value) => OnPropertyChanged(nameof(HasModLoadWarning));

    /// <summary>The mod the warning was last worked out for; null when none is installed.</summary>
    private InstalledModInfo? _modLoadChecked;

    /// <summary>The state of the game's log when the warning was last worked out.</summary>
    private string _modLoadFingerprint = string.Empty;

    /// <summary>Takes the warning down, for every state in which there is no installed mod to warn about.</summary>
    private void ClearModLoadWarning()
    {
        _modLoadChecked = null;
        _modLoadFingerprint = string.Empty;
        ModLoadWarning = string.Empty;
        ModLoadWarningDetail = string.Empty;
    }

    /// <summary>Asks the game's log whether its last start took the mod, and says so under the status.</summary>
    private void UpdateModLoadWarning(InstalledModInfo installed)
    {
        if (_gamePath is null || !installed.IsInstalled)
        {
            ClearModLoadWarning();
            return;
        }

        try
        {
            // Taken before the log is read, so a line the game writes during the read makes the
            // next activation read it again rather than be taken for what is already shown
            _modLoadFingerprint = GameLog.Fingerprint(GameLocator.FindPlayerLog());
            _modLoadChecked = installed;

            ModLoadVerdict verdict = GameLog.CheckLastRun(_gamePath, installed);
            (ModLoadWarning, ModLoadWarningDetail) = ModLoadMessages.Describe(verdict, IsModOutdated);
        }
        catch (Exception)
        {
            // Advice only. Escaping into RefreshEnvironment would turn the whole bar into "could
            // not check the environment" - and the install button off - over one line of advice
            // that could not be worked out.
            ModLoadWarning = string.Empty;
            ModLoadWarningDetail = string.Empty;
        }
    }

    /// <summary>
    /// Reads the game's log again when it has changed since it was last read.
    ///
    /// Called whenever the window comes back to the front, which is what happens after starting
    /// the game and watching it refuse the mod. Without it the bar went on saying nothing until
    /// this application was next started or the mod next installed.
    /// </summary>
    public void RefreshModLoadIfChanged()
    {
        if (_modLoadChecked is not { } installed)
        {
            return;
        }

        string fingerprint;
        try
        {
            fingerprint = GameLog.Fingerprint(GameLocator.FindPlayerLog());
        }
        catch (Exception)
        {
            // Cannot tell, so the line keeps what it said, as the character list does
            return;
        }

        if (fingerprint != _modLoadFingerprint)
        {
            UpdateModLoadWarning(installed);
        }
    }

    /// <summary>
    /// Points the application at a game folder chosen by the user.
    ///
    /// Steam can install into any library folder on any drive, and detection follows Steam's own
    /// records to find it. This is the way out when that fails anyway: an unreadable registry, a
    /// library Steam has forgotten, or a copy of the game moved by hand.
    /// </summary>
    /// <returns>Whether the folder was accepted.</returns>
    public bool SetGamePath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            // Clearing it goes back to detecting the folder automatically
            if (!GamePathSettings.Save(null))
            {
                SetStatus("status.gamePathNotSaved", SettingsFile.DescribePath());
                return false;
            }

            RefreshEnvironment();
            SetStatus("status.gamePathCleared");
            return true;
        }

        if (!GameLocator.IsGameDirectory(directory))
        {
            SetStatus("status.gamePathInvalid", directory);
            return false;
        }

        if (!GamePathSettings.Save(directory))
        {
            // Without this the two lines on screen contradicted each other: the status line said
            // the folder was set while the environment line, which re-reads the file, still said
            // the game could not be found.
            SetStatus("status.gamePathNotSaved", SettingsFile.DescribePath());
            return false;
        }

        RefreshEnvironment();
        SetStatus("status.gamePathSet", directory);
        return true;
    }

    /// <summary>Whether a folder chosen by hand is currently in force.</summary>
    public static bool HasManualGamePath => !string.IsNullOrWhiteSpace(GamePathSettings.Load());

    /// <summary>Installs the bundled mod into the game. Unity is not required.</summary>
    public void InstallMod() => InstallMod(replacing: false);

    /// <summary>
    /// Replaces an already installed mod with the one this application carries.
    ///
    /// The same work as installing: the installer swaps the folder in place and leaves the mod's
    /// settings folder alone, so applied pictures and captured appearances survive. Only the
    /// message differs, because being told an update happened is not the same as being told an
    /// install did.
    /// </summary>
    public void UpdateMod() => InstallMod(replacing: true);

    private void InstallMod(bool replacing)
    {
        if (_gamePath is null)
        {
            SetStatus("env.gameNotFound");
            return;
        }

        try
        {
            string destination = ModPayload.InstallTo(_gamePath);
            SetStatus(replacing ? "status.modUpdated" : "status.modInstalled", destination);
            RefreshEnvironment();
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
        }
        catch (Exception ex)
        {
            SetStatus("status.modInstallFailed", ex);
        }
    }

    // ------------------------------------------------------------ Removal

    /// <summary>
    /// Puts a character's appearance from inside the game onto the canvas.
    ///
    /// Nothing in a save file records how a character looks. It records which parts and colours
    /// were chosen, and the pixels behind those choices live in the game's own asset bundles,
    /// tinted by a shader as they are drawn. The finished appearance exists only on screen, so the
    /// mod is what writes it out and this reads what the mod wrote.
    ///
    /// The button is never disabled. Having nothing captured yet is the ordinary state on a first
    /// run, and a button greyed out then says nothing about what to do about it; pressing it says.
    /// </summary>
    [RelayCommand]
    private async Task FetchFromGame(CharacterChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        // Asked first, before anything is read. The question is about the drawing that is on the
        // canvas now, and reading the file first would spend that time with the canvas live.
        if (AskWhetherToDiscardEdits is { } confirm && !await confirm())
        {
            SetStatus("status.fetchCancelled");
            return;
        }

        int load = ++_loadVersion;

        // Taken before the read, for the same reason the file loader takes it there: anything
        // drawn while the file is being read is not something the user agreed to lose.
        (EditorDocument? Document, bool Edited) baseline = (Document, WouldDiscardEdits);
        long revisionAtStart = Document?.Revision ?? 0;

        IsBusy = true;

        // Counted as a load, as opening a file is: the captured picture is read and composed before
        // the gate is taken, and "Apply to game" pressed in the meantime wrote the picture from
        // before while the screen went on to the fetched one
        _loadsInFlight++;

        try
        {
            ModConfigLocation location = ResolveModConfigLocation(out Func<string> note);
            string path = CapturedSkins.PathFor(
                location.ModsDirectory, SheetInstaller.DefaultModFolderName, choice.Character.Guid);

            if (!File.Exists(path))
            {
                // Three different reasons, and they need different advice.
                //
                // A character this tool is already drawing over is never captured at all: the mod
                // steps aside rather than write its own picture back out as though it were the
                // game's. "Load the character in the game" is then advice that can never work,
                // however many times it is followed - taking the picture off again is what makes
                // a capture possible. Read from the folder rather than from the row's own mark,
                // which is only brought up to date after an install or a removal.
                bool replaced = CharacterSkins
                    .InstalledGuids(location.ModsDirectory, SheetInstaller.DefaultModFolderName)
                    .Contains(choice.Character.Guid);

                // Nothing captured at all usually means the mod has not run yet; captures for
                // other characters but not this one means it has, and this character simply has
                // not been played since.
                bool anyCaptured = CapturedSkins
                    .CapturedGuids(location.ModsDirectory, SheetInstaller.DefaultModFolderName)
                    .Count > 0;

                string reason = replaced
                    ? "status.fetchNotCapturedApplied"
                    : anyCaptured
                        ? "status.fetchNotCaptured"
                        : "status.fetchNothingCaptured";

                SetStatus(() => Loc.Instance.Format(reason, choice.Display));
                NoteNotArrived();
                return;
            }

            // Checked before the image is read, so a mod whose bands mean something else is turned
            // away with an answer rather than composited into a picture that is quietly wrong.
            CapturedSkins.EnsureFormatUnderstood(
                location.ModsDirectory, SheetInstaller.DefaultModFolderName);

            // Read once here so the sheet cannot be composited against a setting the user changed
            // while it was being built.
            bool includeEquipment = IncludeEquipment;

            SKBitmap sheet = await Task.Run(
                () => CapturedSkins.Compose(path, _layout, includeEquipment));

            if (load != _loadVersion)
            {
                sheet.Dispose();
                return;
            }

            // Disposes the sheet itself, and declines the load when the canvas was drawn on in the
            // meantime. It also reports a sheet it cannot use, which is why nothing is claimed here
            // until the path it recorded proves the load went through.
            await LoadAsSheetAsync(sheet, path, complete: true, load, baseline, revisionAtStart);

            if (SourcePath == path)
            {
                _fetchedFrom = choice;
                OnPropertyChanged(nameof(SourceDisplay));

                // A character with a picture applied is never captured again - the mod steps
                // aside, as the refusal above explains - so what was read is how it looked just
                // before the picture went on, which can be long ago. Said so rather than
                // "took the in-game appearance", which a character in the same state but with no
                // file left is refused as impossible. Asked from the folder, as there.
                bool applied = CharacterSkins
                    .InstalledGuids(location.ModsDirectory, SheetInstaller.DefaultModFolderName)
                    .Contains(choice.Character.Guid);
                string fetched = applied ? "status.fetchedWhileApplied" : "status.fetched";

                SetStatus(() => Loc.Instance.Format(fetched, choice.Display) + note());
            }
        }
        catch (ToolException ex)
        {
            SetStatus(ex);
            NoteNotArrived();
        }
        catch (Exception ex)
        {
            SetStatus("status.fetchFailed", ex);
            NoteNotArrived();
        }
        finally
        {
            _loadsInFlight--;

            // Only the newest request may put the indicator out, as with the file loader
            if (load == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Builds the removal plan. It lists only what exists at the moment the button is pressed,
    /// so the confirmation screen can state exactly what will be removed and how much.
    /// </summary>
    /// <returns>
    /// The plan, or null when the scan itself failed. Null and an empty plan must stay
    /// distinguishable: reporting a failure as "nothing found" would tell the user the mod is
    /// already gone while it is still installed and still loading in the game.
    /// </returns>
    public RemovalPlan? PlanRemoval()
    {
        try
        {
            // The same installation the install half writes to. Letting removal detect the game
            // on its own ignored a folder the user had pointed at by hand, so the mod could be
            // installed into it and then reported as "nothing to remove" - with no way to take
            // it back out from here.
            return ModUninstaller.Plan(
                SheetInstaller.DefaultModFolderName,
                _gamePath is null ? null : [_gamePath]);
        }
        catch (Exception ex)
        {
            SetStatus("status.uninstallFailed", ex);
            return null;
        }
    }

    /// <summary>Executes the plan and removes what this tool installed.</summary>
    public void ExecuteRemoval(RemovalPlan plan)
    {
        try
        {
            RemovalResult result = ModUninstaller.Execute(plan, SheetInstaller.DefaultModFolderName);

            if (result.Failures.Count > 0)
            {
                // Re-read as a full removal is: part of it did happen, and the bar, the mod
                // buttons and every character's "適用中" went on describing what was there
                // before. Re-read first and report after, so that a failed character scan
                // cannot overwrite the report of the partial removal.
                RefreshEnvironment();

                // Named as the confirmation dialog names them, with the whole path. Both targets are
                // called CustomPlayerSkin - the mod in the game folder and its settings in the user's
                // data - so the last segment alone could not say which one was left, or where.
                //
                // Only the kind's key is taken here; its name is looked up inside the message, so a
                // language switch translates it along with the sentence around it.
                int removedCount = result.Removed.Count;
                List<(string? KindKey, string Path, string Reason)> failed = result.Failures.Select(f =>
                {
                    RemovalTarget? target = plan.Targets.FirstOrDefault(t => t.Path == f.Path);
                    string? kindKey = target is null
                        ? null
                        : target.Kind == RemovalKind.ModInstall ? "uninstall.kindMod" : "uninstall.kindConfig";
                    return (kindKey, f.Path, f.Reason);
                }).ToList();

                SetStatus(() => Loc.Instance.Format(
                    "status.uninstallPartial",
                    removedCount,
                    string.Join(" / ", failed.Select(f =>
                        $"[{(f.KindKey is null ? Path.GetFileName(f.Path) : Loc.Instance[f.KindKey])}] {f.Path}: {f.Reason}"))));
                return;
            }

            SetStatus(() => result.Removed.Count > 0
                ? Loc.Instance.Format("status.uninstalled", result.Removed.Count)
                : Loc.Instance["status.uninstallNothing"]);

            RefreshEnvironment();
        }
        catch (Exception ex)
        {
            SetStatus("status.uninstallFailed", ex);
        }
    }

    // ------------------------------------------------------------ Helpers

    /// <summary>
    /// Parses a colour, or returns null when the text is not one.
    ///
    /// This is the single definition of what a colour string means. The swatch beside the input
    /// box used to run its own parser, which read eight hex digits as AARRGGBB while this one
    /// read them as RRGGBBAA, and which accepted forms this one rejected. The two therefore
    /// disagreed: the swatch previewed one colour and the pen painted another, and a string
    /// neither could use was painted as opaque black while the swatch showed it as transparent.
    /// </summary>
    public static SKColor? TryParseColor(string? text)
    {
        string hex = (text ?? string.Empty).Trim().TrimStart('#');

        if (hex.Length == 3 || hex.Length == 4)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if (hex.Length == 6)
        {
            hex += "FF";
        }

        if (hex.Length != 8
            || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        return new SKColor(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    /// <summary>
    /// Parses a colour for painting. Text that is not a colour leaves the pixels alone rather
    /// than stamping black over the picture, which is not something the user asked for.
    /// </summary>
    private static SKColor? ParseColor(string text) => TryParseColor(text);
}

/// <summary>A frame that can be selected for editing.</summary>
/// <param name="Frame">The target frame; null means the whole sheet.</param>
/// <param name="Label">Name shown on screen.</param>
public sealed class FrameChoice : LocalizedChoice
{
    private readonly string? _literal;

    private FrameChoice(FrameRect? frame, string labelKey, string? literal) : base(labelKey)
    {
        Frame = frame;
        _literal = literal;
    }

    public FrameRect? Frame { get; }

    /// <summary>Frame names are language-independent identifiers, so they are shown untranslated.</summary>
    public new string Display => _literal ?? Loc.Instance[LabelKey];

    public static FrameChoice WholeSheet() => new(null, "editor.wholeSheet", null);

    public static FrameChoice For(FrameRect frame, string label) => new(frame, string.Empty, label);
}
