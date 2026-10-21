using CommunityToolkit.Mvvm.ComponentModel;
using CoreKeeperSkinTool.Export;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.ViewModels;

/// <summary>
/// What the "Export for mod.io" screen shows, and the choices it hands back.
///
/// Kept apart from the window so the rules - what may be exported, what is warned about - can be
/// tested without one. The checks are the exporter's own, so the screen and the file can never
/// disagree about what is accepted.
/// </summary>
public sealed partial class ModIoExportDialogModel : ObservableObject
{
    /// <param name="defaultName">The name offered, cut to the longest accepted.</param>
    /// <param name="gearShownNow">How the "Armour, clothes and helmet" toggle is set now, which is offered first.</param>
    /// <param name="game">What is known about the game's version.</param>
    public ModIoExportDialogModel(string defaultName, bool gearShownNow, GameVersionCheck game)
    {
        string name = (defaultName ?? string.Empty).Trim();
        _name = CutToMaxLength(name);
        _version = ModIoExporter.DefaultVersion;
        _showGear = gearShownNow;

        GearShownNow = gearShownNow;
        Game = game;
        GameVersionTag = ModIoExporter.GameVersionTag(game.Version);
    }

    /// <summary>
    /// The offered name cut to the longest accepted, without leaving half of a surrogate pair at the
    /// end: an emoji or a rare kanji split there fails the name check as a broken character, so the
    /// name offered would be refused before anything was typed. The limit counts UTF-16 units, as
    /// the exporter does.
    /// </summary>
    private static string CutToMaxLength(string name)
    {
        int cut = ModIoExporter.MaxDisplayNameLength;
        if (name.Length <= cut)
        {
            return name;
        }

        return char.IsHighSurrogate(name[cut - 1]) ? name[..(cut - 1)] : name[..cut];
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _version;

    /// <summary>Whether the game's clothes and armour are drawn over the picture in the mod.</summary>
    [ObservableProperty]
    private bool _showGear;

    /// <summary>Ticked by the person exporting; nothing is written until it is.</summary>
    [ObservableProperty]
    private bool _rightsConfirmed;

    /// <summary>How the gear toggle is set now: the look that has been seen in the game.</summary>
    public bool GearShownNow { get; }

    /// <summary>What is known about the game the mod is for.</summary>
    public GameVersionCheck Game { get; }

    /// <summary>The mod.io Game Version tag the memo asks for.</summary>
    public string GameVersionTag { get; }

    /// <summary>The other side of <see cref="ShowGear"/>, for the second of the two choices.</summary>
    public bool HideGear
    {
        get => !ShowGear;
        set => ShowGear = !value;
    }

    /// <summary>
    /// Whether the chosen look differs from the one the toggle shows in the game now - most likely a
    /// look that was never tried there.
    /// </summary>
    public bool IsUntested => ShowGear != GearShownNow;

    /// <summary>The tag to pick on mod.io, and why it may need changing.</summary>
    public string GameVersionNote
    {
        get
        {
            string tag = Loc.Instance.Format("modio.gameVersion", GameVersionTag);
            return Game.State switch
            {
                GameVersionState.Supported => tag,
                GameVersionState.Unsupported => tag + Environment.NewLine + Loc.Instance.Format("modio.gameVersionUnsupported", Game.Version),
                _ => tag + Environment.NewLine + Loc.Instance.Format("modio.gameVersionUnknown", GameVersionTag),
            };
        }
    }

    /// <summary>What is wrong with the name or the version, or an empty string.</summary>
    public string Problem
    {
        get
        {
            try
            {
                ModIoExporter.CheckDisplayName(Name);
                ModIoExporter.CheckVersion(Version);
                return string.Empty;
            }
            catch (ToolException ex)
            {
                return Loc.Instance.Describe(ex);
            }
        }
    }

    /// <summary>Whether a problem is shown.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Whether the export button can be pressed.</summary>
    public bool CanExport => !HasProblem && RightsConfirmed;

    /// <summary>The choices as the exporter takes them. Only called once <see cref="CanExport"/> holds.</summary>
    public ModIoExportOptions Result() =>
        new(ModIoExporter.CheckDisplayName(Name), ModIoExporter.CheckVersion(Version), ShowGear);

    partial void OnNameChanged(string value) => NotifyChecks();

    partial void OnVersionChanged(string value) => NotifyChecks();

    partial void OnRightsConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    partial void OnShowGearChanged(bool value)
    {
        OnPropertyChanged(nameof(HideGear));
        OnPropertyChanged(nameof(IsUntested));
    }

    private void NotifyChecks()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(CanExport));
    }

    /// <summary>Longest file name offered, before ".zip".</summary>
    private const int MaxSuggestedStem = 80;

    /// <summary>
    /// The file name offered when choosing where to save: the name and the version, with anything
    /// Windows refuses in a file name replaced. The memo and the logo are named after it.
    /// </summary>
    public static string SuggestedFileName(ModIoExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        char[] invalid = Path.GetInvalidFileNameChars();
        string stem = new([.. $"{options.DisplayName}-{options.Version}".Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]);

        // Windows drops trailing dots and spaces from a name, which would leave the memo and the
        // logo named after something other than the zip
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length > MaxSuggestedStem)
        {
            stem = stem[..MaxSuggestedStem].TrimEnd('.', ' ');
        }

        return (stem.Length == 0 ? "skin" : stem) + ".zip";
    }
}
