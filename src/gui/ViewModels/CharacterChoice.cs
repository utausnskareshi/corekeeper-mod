using CommunityToolkit.Mvvm.ComponentModel;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.ViewModels;

/// <summary>
/// One saved player character, as a row the user can tick.
///
/// Only characters that still exist in the game are ever built into one of these, so a character
/// deleted in game simply stops appearing and can no longer be applied to.
/// </summary>
public sealed partial class CharacterChoice : ObservableObject
{
    public CharacterChoice(GameCharacter character, bool hasSkin)
    {
        Character = character;
        _hasSkin = hasSkin;

        // Ticked by default when it already has an image: the common action is to change the
        // look of the characters that are already using one.
        _isSelected = hasSkin;
    }

    /// <summary>The character this row stands for.</summary>
    public GameCharacter Character { get; }

    /// <summary>Whether this character is included in the next install or removal.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether an image is currently installed for this character.</summary>
    [ObservableProperty]
    private bool _hasSkin;

    /// <summary>
    /// Number and name, as shown in the list.
    ///
    /// Creative-mode characters are marked, because the game numbers them from one again and two
    /// rows reading "#1" with no other difference would be indistinguishable.
    /// </summary>
    public string Display
    {
        get
        {
            string text = Character.Describe(Loc.Instance["character.unnamed"]);
            return Character.IsCreative ? $"{text} {Loc.Instance["character.creative"]}" : text;
        }
    }

    /// <summary>Whether this character currently has a replaced appearance.</summary>
    public string StateLabel =>
        Loc.Instance[HasSkin ? "character.applied" : "character.notApplied"];

    /// <summary>
    /// The full identifier, shown only as a tooltip. Two characters can share a name, and this is
    /// the only thing that tells them apart.
    /// </summary>
    public string Tooltip => Loc.Instance.Format(
        "character.tooltip", Character.Guid, Character.LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    partial void OnHasSkinChanged(bool value) => OnPropertyChanged(nameof(StateLabel));
}
