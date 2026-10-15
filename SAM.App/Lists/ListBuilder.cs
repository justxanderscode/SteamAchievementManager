using System.Globalization;
using SAM.App.Localization;

namespace SAM.App.Lists;

/// <summary>
/// Sorts the games into the categories of the Steam collections:
/// 1.x.0 All, 1.x.1 Done, 1.x.2 Not 100 % Anymore, 1.x.3 Started, 1.x.4 Can't Finish
/// for all games (1.0), visible ones (1.1, 👀✔) and Profile Features Limited (1.2, 👀✖).
/// </summary>
internal static class ListBuilder
{
    public static readonly StringComparer NameComparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>Remembers every game at 100 %. Returns true if anything changed.</summary>
    public static bool UpdateHistory(ListState state, IEnumerable<(ListGame Game, bool Complete)> games, long now)
    {
        var changed = false;

        foreach (var (game, complete) in games)
        {
            if (!complete)
            {
                continue;
            }

            if (state.Perfect.TryGetValue(game.AppId, out var record))
            {
                record.Total = game.Total;
                record.LastSeen = now;
            }
            else
            {
                state.Perfect[game.AppId] = new PerfectRecord { Total = game.Total, FirstSeen = now, LastSeen = now };
            }

            // Back at 100 %: a later drop should be detected again.
            state.NotAnymoreDismissed.Remove(game.AppId);
            changed = true;
        }

        return changed;
    }

    /// <summary>Priority: 100 % &gt; Can't Finish &gt; Not 100 % Anymore &gt; Started &gt; Untouched.</summary>
    public static void Classify(ListState state, ListGame game, bool complete)
    {
        game.CantFinishReason = state.CantFinish.GetValueOrDefault(game.AppId);
        game.IsNotAnymoreManual = state.NotAnymoreManual.Contains(game.AppId);

        var notAnymore = game.IsNotAnymoreManual ||
                         state.Perfect.ContainsKey(game.AppId) && !state.NotAnymoreDismissed.Contains(game.AppId);

        game.Kind = complete ? ListKind.Done
            : game.CantFinishReason is not null ? ListKind.CantFinish
            : notAnymore ? ListKind.NotAnymore
            : game.Unlocked > 0 ? ListKind.Started
            : ListKind.Untouched;
    }

    public static List<ListCategory> Build(IReadOnlyCollection<ListGame> games)
    {
        var sorted = games.OrderBy(g => GuideExport.SortKey(g.Name), NameComparer).ThenBy(g => g.Name, NameComparer).ThenBy(g => g.AppId).ToList();
        var categories = new List<ListCategory>();

        foreach (var scope in new[] { ListScope.All, ListScope.Visible, ListScope.Limited })
        {
            foreach (var kind in new[] { ListKind.All, ListKind.Done, ListKind.NotAnymore, ListKind.Started, ListKind.CantFinish })
            {
                var caption = kind == ListKind.All
                    ? scope switch { ListScope.Visible => Strings.GroupVisible, ListScope.Limited => Strings.GroupLimited, _ => Strings.GroupAll }
                    : null;

                categories.Add(new ListCategory(scope, kind, Title(scope, kind), Description(kind), caption)
                {
                    Games = sorted.Where(g => InScope(g, scope) && InKind(g, kind)).ToList(),
                });
            }
        }

        categories.Add(new ListCategory(ListScope.All, ListKind.Untouched, Strings.CategoryUntouched, Strings.CategoryUntouchedDesc, Strings.GroupOther)
        {
            Games = sorted.Where(g => g.Kind == ListKind.Untouched).ToList(),
        });

        return categories;
    }

    /// <summary>Exactly the names of the Steam collections, e.g. "🎖️ 1.0.1 All Done" or "🎖️ 1.2.4 👀✖ Can't Finish".</summary>
    public static string Title(ListScope scope, ListKind kind)
    {
        var number = "1." + (int)scope + "." + (int)kind;
        var name = kind switch
        {
            ListKind.All => "All",
            ListKind.Done => scope == ListScope.All ? "All Done" : "Done",
            ListKind.NotAnymore => "Not 100 % Anymore",
            ListKind.Started => scope == ListScope.All ? "All Started" : "Started",
            ListKind.CantFinish => scope == ListScope.All ? "All Can't Finish" : "Can't Finish",
            _ => "",
        };
        var eyes = scope switch { ListScope.Visible => "👀✔ ", ListScope.Limited => "👀✖ ", _ => "" };
        return "🎖️ " + number + " " + eyes + name;
    }

    private static string Description(ListKind kind) => kind switch
    {
        ListKind.Done => Strings.CategoryDoneDesc,
        ListKind.NotAnymore => Strings.CategoryNotAnymoreDesc,
        ListKind.Started => Strings.CategoryStartedDesc,
        ListKind.CantFinish => Strings.CategoryCantFinishDesc,
        _ => Strings.CategoryAllDesc,
    };

    private static bool InScope(ListGame game, ListScope scope) => scope switch
    {
        ListScope.Visible => game.Vetted == true,
        ListScope.Limited => game.Vetted == false,
        _ => true,
    };

    private static bool InKind(ListGame game, ListKind kind)
        => kind == ListKind.All ? game.Kind != ListKind.Untouched : game.Kind == kind;
}
