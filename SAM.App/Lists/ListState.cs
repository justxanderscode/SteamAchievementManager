using System.Globalization;
using System.Text.Json;
using SAM.App.Helpers;

namespace SAM.App.Lists;

/// <summary>
/// What the app remembers per Steam account: which games it has already seen at 100 %
/// (this is what "Not 100 % Anymore" is derived from) and what was marked by hand.
/// </summary>
internal sealed class ListState
{
    /// <summary>Games that were at 100 % at some point.</summary>
    public Dictionary<uint, PerfectRecord> Perfect { get; set; } = [];

    /// <summary>Marked as "Can't Finish" by hand, with a reason.</summary>
    public Dictionary<uint, string> CantFinish { get; set; } = [];

    /// <summary>Marked as "Not 100 % Anymore" by hand.</summary>
    public HashSet<uint> NotAnymoreManual { get; set; } = [];

    /// <summary>Detected automatically, but removed again by hand.</summary>
    public HashSet<uint> NotAnymoreDismissed { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathFor(uint accountId)
        => Path.Combine(AppPaths.Data, "lists-" + accountId.ToString(CultureInfo.InvariantCulture) + ".json");

    public static ListState Load(uint accountId)
    {
        var path = PathFor(accountId);

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate) && JsonSerializer.Deserialize<ListState>(File.ReadAllText(candidate), Options) is { } state)
                {
                    return state;
                }
            }
            catch (Exception)
            {
                // Damaged: try the backup.
            }
        }

        return new ListState();
    }

    /// <summary>Saves through a temporary file; the previous version is kept as .bak.</summary>
    public void Save(uint accountId)
    {
        var path = PathFor(accountId);
        Directory.CreateDirectory(AppPaths.Data);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));

        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".bak");
        }
        else
        {
            File.Move(temp, path);
        }
    }
}

internal sealed class PerfectRecord
{
    public int Total { get; set; }

    /// <summary>Unix time at which the app first saw the game at 100 %.</summary>
    public long FirstSeen { get; set; }

    public long LastSeen { get; set; }
}
