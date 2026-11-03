using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;

namespace MitPlanner;

/// <summary>A fight the plugin knows: which zone, which timeline, and default mechanic tags.</summary>
/// <param name="Category">"ShB Trial", "DT Raid", ... (from cactbot's folder layout).</param>
/// <param name="Path">Timeline path inside the bundle ("05-shb/trial/ruby_weapon").</param>
public sealed record FightDefinition(
    uint TerritoryId,
    string Name,
    string Category,
    string Path,
    IReadOnlyDictionary<string, MechanicType> DefaultTags,
    IReadOnlyDictionary<string, uint>? HeadMarkers = null)  // label -> head marker that marks the target
{
    public string Display => $"{Name}  [{Category}]";
}

/// <summary>
/// Every fight cactbot has a timeline for, from the bundle built by
/// .github/scripts/sync_cactbot.py (refreshed weekly by CI). Only the small index is read at
/// startup; each fight's timeline is unpacked and parsed the first time it's needed.
/// Default tags = cactbot trigger responses, then <see cref="CuratedFights"/> on top, then your
/// own edits in game (Configuration.TagOverrides) on top of that.
/// </summary>
public static class Fights
{
    private const string BundleResource = "MitPlanner.Timelines.cactbot.zip";

    private static readonly Lazy<IReadOnlyList<FightDefinition>> AllLazy = new(LoadIndex);
    private static readonly Dictionary<uint, Timeline> Cache = new();

    public static IReadOnlyList<FightDefinition> All => AllLazy.Value;

    public static FightDefinition? ForTerritory(uint territoryId) =>
        All.FirstOrDefault(f => f.TerritoryId == territoryId);

    public static Timeline Load(FightDefinition fight)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(fight.TerritoryId, out var t)) return t;
            t = Timeline.Parse(fight.Name, ReadEntry($"timelines/{fight.Path}.txt"));
            Cache[fight.TerritoryId] = t;
            return t;
        }
    }

    // ================================================================ bundle

    private sealed class IndexEntry
    {
        public uint territory { get; set; }
        public string zoneKey { get; set; } = "";
        public string path { get; set; } = "";
        public string category { get; set; } = "";
        public Dictionary<string, string> tags { get; set; } = new();
    }

    private static string ReadEntry(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundleResource)
            ?? throw new FileNotFoundException($"Missing embedded {BundleResource}");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new FileNotFoundException($"{name} not in timeline bundle");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<FightDefinition> LoadIndex()
    {
        List<IndexEntry> index;
        try
        {
            index = JsonSerializer.Deserialize<List<IndexEntry>>(ReadEntry("index.json")) ?? new();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't read the timeline bundle");
            return Array.Empty<FightDefinition>();
        }

        var fights = new List<FightDefinition>(index.Count);
        foreach (var e in index)
        {
            var tags = new Dictionary<string, MechanicType>(StringComparer.Ordinal);
            foreach (var (label, type) in e.tags)
                if (Enum.TryParse<MechanicType>(type, out var t)) tags[label] = t;

            IReadOnlyDictionary<string, uint>? markers = null;
            if (CuratedFights.ByTerritory.TryGetValue(e.territory, out var curated))
            {
                foreach (var (label, t) in curated.Tags) tags[label] = t;
                markers = curated.HeadMarkers;
            }

            fights.Add(new FightDefinition(e.territory, GameName(e.territory, e.zoneKey), e.category, e.path, tags, markers));
        }

        // Newest expansion first, then by content type and name.
        return fights
            .OrderByDescending(f => ExpansionOrder(f.Path))
            .ThenBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ExpansionOrder(string path) =>
        int.TryParse(path.Split('-')[0], out var n) ? n : 0;

    /// <summary>
    /// The duty's name from game data (Duty Finder name, so "(Savage)"/"(Extreme)" are kept),
    /// falling back to the zone's place name, then to cactbot's zone key split into words.
    /// </summary>
    private static string GameName(uint territory, string zoneKey)
    {
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
            if (row is { } tt)
            {
                var duty = tt.ContentFinderCondition.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(duty)) return Capitalize(duty);
                var place = tt.PlaceName.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(place)) return Capitalize(place);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"No game name for zone {territory}");
        }
        return Regex.Replace(zoneKey, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    // Duty names start lower-case in game data ("the Cinder Drift").
    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
