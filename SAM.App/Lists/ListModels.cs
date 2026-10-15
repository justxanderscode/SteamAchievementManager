using System.Globalization;
using SAM.App.Helpers;
using SAM.App.Localization;

namespace SAM.App.Lists;

/// <summary>The state of a game that decides its category.</summary>
public enum ListKind
{
    All = 0,
    Done = 1,
    NotAnymore = 2,
    Started = 3,
    CantFinish = 4,
    Untouched = 9,
}

/// <summary>Visibility: 1.0 all, 1.1 visible on the profile (👀✔), 1.2 Profile Features Limited (👀✖).</summary>
public enum ListScope
{
    All = 0,
    Visible = 1,
    Limited = 2,
}

/// <summary>A game in the achievement lists.</summary>
public sealed class ListGame : ObservableObject
{
    internal ListGame(uint appId, string name, int unlocked, int total, bool? vetted)
    {
        AppId = appId;
        Name = name;
        Unlocked = unlocked;
        Total = total;
        Vetted = vetted;
    }

    public uint AppId { get; }

    public string Name { get; }

    public int Unlocked { get; }

    public int Total { get; }

    /// <summary>true = visible on the profile, false = Profile Features Limited, null = not reported by Steam yet.</summary>
    public bool? Vetted { get; }

    public ListKind Kind { get; internal set; }

    public string? CantFinishReason { get; internal set; }

    public bool IsNotAnymoreManual { get; internal set; }

    public double Ratio => Total == 0 ? 0 : (double)Unlocked / Total;

    public string ProgressText => Unlocked.ToString("N0", CultureInfo.CurrentCulture) + " / " + Total.ToString("N0", CultureInfo.CurrentCulture);

    public string PercentText => Ratio.ToString("P0", CultureInfo.CurrentCulture);

    public string VisibilityText => Vetted switch
    {
        true => "👀✔",
        false => "👀✖",
        null => "👀?",
    };

    public string VisibilityTooltip => Vetted switch
    {
        true => Strings.VisibilityVisibleTooltip,
        false => Strings.VisibilityLimitedTooltip,
        null => Strings.VisibilityUnknownTooltip,
    };

    public bool HasReason => !string.IsNullOrEmpty(CantFinishReason);

    public bool IsCantFinish => Kind == ListKind.CantFinish;

    /// <summary>Marked - even if the game has reached 100 % since.</summary>
    public bool IsCantFinishMarked => CantFinishReason is not null;

    public bool IsNotAnymore => Kind == ListKind.NotAnymore;

    /// <summary>A game at 100 % cannot be "can't finish"; an existing mark stays editable.</summary>
    public bool CanMarkCantFinish => Kind != ListKind.Done || IsCantFinishMarked;

    public bool CanMarkNotAnymore => Kind is ListKind.Started or ListKind.Untouched or ListKind.CantFinish;

    /// <summary>Line for the guide text, as in the model guide: "Name (reason)".</summary>
    public string GuideLine => HasReason ? Name + " (" + CantFinishReason + ")" : Name;

    public override string ToString() => Name + ", " + ProgressText;
}

/// <summary>A category such as "🎖️ 1.1.2 👀✔ Not 100 % Anymore".</summary>
public sealed class ListCategory : ObservableObject
{
    internal ListCategory(ListScope scope, ListKind kind, string title, string description, string? groupCaption)
    {
        Scope = scope;
        Kind = kind;
        Title = title;
        Description = description;
        GroupCaption = groupCaption;
    }

    public ListScope Scope { get; }

    public ListKind Kind { get; }

    /// <summary>Name as in the Steam collection - deliberately not translated.</summary>
    public string Title { get; }

    public string Description { get; }

    /// <summary>Heading above the first category of a group.</summary>
    public string? GroupCaption { get; }

    public bool IsGroupStart => GroupCaption is not null;

    public List<ListGame> Games { get; internal set; } = [];

    public string CountText => Games.Count.ToString("N0", CultureInfo.CurrentCulture);

    public override string ToString() => Title + ", " + CountText;
}
