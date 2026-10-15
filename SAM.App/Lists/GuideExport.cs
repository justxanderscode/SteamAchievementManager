using System.Globalization;
using System.Text;

namespace SAM.App.Lists;

/// <summary>A section for a Steam guide: a heading and one game per line.</summary>
public sealed record GuideSection(string Title, string Body)
{
    public string LengthText => Localization.Strings.ExportSectionInfo(Body.Length.ToString("N0", CultureInfo.CurrentCulture));

    public override string ToString() => Title;
}

/// <summary>
/// Builds the guide text in the style of "Steam Achievement Statistics": VG = visible games, PFL = Profile Features
/// Limited, UG = untouched games. Sections that are too long are split alphabetically ("A - H", "Sa - St"),
/// so none exceeds the character limit of a guide section. Headings stay in English like in the model guide.
/// </summary>
internal static class GuideExport
{
    public const int DefaultLimit = 8000;

    public static List<GuideSection> Build(IReadOnlyList<ListCategory> categories, bool includeUntouched, int limit)
    {
        limit = Math.Max(500, limit);
        var sections = new List<GuideSection> { Overview(categories, includeUntouched) };

        foreach (var (scope, prefix) in new[] { (ListScope.Visible, "VG: "), (ListScope.Limited, "PFL: ") })
        {
            foreach (var (kind, label) in new[]
                     {
                         (ListKind.Done, "Completed Games"),
                         (ListKind.NotAnymore, "Not 100 % Anymore"),
                         (ListKind.Started, "Unfinished Games"),
                         (ListKind.CantFinish, "Games I can't finish"),
                     })
            {
                var games = Find(categories, scope, kind).Games.Select(g => g.GuideLine).ToList();

                // Empty lists only appear in the overview; an empty section would be useless in the guide.
                if (games.Count > 0)
                {
                    sections.AddRange(Split(prefix + label, prefix + label, games, limit, letters: 1));
                }
            }
        }

        // Games without reported visibility are only in 1.0.x - listed separately in the guide.
        var unknown = Find(categories, ListScope.All, ListKind.All).Games.Where(g => g.Vetted is null).Select(g => g.GuideLine).ToList();

        if (unknown.Count > 0)
        {
            sections.AddRange(Split("Unknown visibility: Games", "Unknown visibility: Games", unknown, limit, letters: 1));
        }

        if (includeUntouched)
        {
            var untouched = Find(categories, ListScope.All, ListKind.Untouched).Games;

            foreach (var group in untouched.GroupBy(g => InitialGroup(g.Name)).OrderBy(g => GroupOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                sections.AddRange(Split("UG: " + group.Key, "UG:", group.Select(g => g.Name).ToList(), limit, letters: 2));
            }
        }

        return sections;
    }

    private static ListCategory Find(IReadOnlyList<ListCategory> categories, ListScope scope, ListKind kind)
        => categories.First(c => c.Scope == scope && c.Kind == kind);

    private static GuideSection Overview(IReadOnlyList<ListCategory> categories, bool includeUntouched)
    {
        var lines = new List<string>
        {
            "VG = Visible Games (👀✔) · PFL = Profile Features Limited (👀✖) · UG = Untouched Games",
            "",
        };

        foreach (var category in categories)
        {
            if (category.Kind == ListKind.Untouched && !includeUntouched)
            {
                continue;
            }

            var title = category.Kind == ListKind.Untouched ? "Untouched Games" : category.Title;
            lines.Add(title + ": " + category.Games.Count.ToString(CultureInfo.InvariantCulture));
        }

        lines.Add("");
        lines.Add("Date: " + DateTime.Now.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
        return new GuideSection("Quick Overview", string.Join("\n", lines));
    }

    /// <summary>If everything fits into one section the heading is "Label (n)"; otherwise "SplitLabel A - H (n)", for UG "UG: Sa - St (n)".</summary>
    private static IEnumerable<GuideSection> Split(string label, string splitLabel, List<string> lines, int limit, int letters)
    {
        if (Length(lines) <= limit)
        {
            yield return new GuideSection(label + " (" + lines.Count.ToString(CultureInfo.InvariantCulture) + ")", string.Join("\n", lines));
            yield break;
        }

        var chunk = new List<string>();
        var length = 0;

        foreach (var line in lines)
        {
            var added = chunk.Count == 0 ? line.Length : line.Length + 1;

            if (chunk.Count > 0 && length + added > limit)
            {
                yield return Chunk(splitLabel, chunk, letters);
                chunk = [];
                added = line.Length;
                length = 0;
            }

            chunk.Add(line);
            length += added;
        }

        if (chunk.Count > 0)
        {
            yield return Chunk(splitLabel, chunk, letters);
        }
    }

    private static GuideSection Chunk(string label, List<string> chunk, int letters)
    {
        var first = RangeKey(chunk[0], letters);
        var last = RangeKey(chunk[^1], letters);
        var range = first == last ? first : first + " - " + last;
        return new GuideSection(label + " " + range + " (" + chunk.Count.ToString(CultureInfo.InvariantCulture) + ")", string.Join("\n", chunk));
    }

    private static int Length(List<string> lines) => lines.Count == 0 ? 0 : lines.Sum(l => l.Length) + lines.Count - 1;

    /// <summary>Initial letters for range labels, e.g. "Sa" for "Satisfactory".</summary>
    private static string RangeKey(string name, int letters)
    {
        var chars = SortKey(name).TakeWhile(char.IsLetterOrDigit).Take(letters).ToArray();

        if (chars.Length == 0)
        {
            return "#";
        }

        return char.ToUpperInvariant(chars[0]) + new string(chars.Skip(1).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Group for untouched games as in the model guide: "0 - 9", "A" … "Z", "Chinese Symbols" etc.</summary>
    public static string InitialGroup(string name)
    {
        var first = SortKey(name).FirstOrDefault();

        if (first == default || !char.IsLetter(first))
        {
            return "0 - 9";
        }

        var upper = char.ToUpperInvariant(first);

        return first switch
        {
            _ when upper is >= 'A' and <= 'Z' => upper.ToString(),
            _ when first is >= 'Ͱ' and <= 'Ͽ' => "Greek Symbols",
            _ when first is >= 'Ѐ' and <= 'ӿ' => "Cyrillic Symbols",
            _ when first is >= '぀' and <= 'ヿ' => "Japanese Symbols",
            _ when first is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' => "Korean Symbols",
            _ when first is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' => "Chinese Symbols",
            _ => "Other Symbols",
        };
    }

    private static int GroupOrder(string key) => key switch
    {
        "0 - 9" => 0,
        { Length: 1 } => 1,
        _ => 2,
    };

    /// <summary>
    /// Sort key: without accents and punctuation, whitespace collapsed - "[TDA00] Muv-Luv" sorts under T,
    /// "★ Fallalypse ★" under F and "S.W.I.N.E." like "SWINE". This keeps the range labels of split sections in line with the order.
    /// </summary>
    public static string SortKey(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in Strip(name))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        var key = builder.ToString().TrimEnd();
        return key.Length == 0 ? name : key;
    }

    /// <summary>Remove accents so "Élite" ends up under E.</summary>
    private static string Strip(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
