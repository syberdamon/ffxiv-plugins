using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace MitPlanner;

/// <summary>An ability the current job can use that has its own cooldown.</summary>
public sealed record CatalogEntry(uint ActionId, string Name, uint Icon, int Level, float RecastSeconds);

/// <summary>
/// Builds the list of cooldown abilities for a job straight from game data, so every combat job
/// works without hand-written lists. Rebuilt when your job or level changes.
/// </summary>
public static unsafe class ActionCatalog
{
    // Cooldown group 58 is the shared GCD; everything else is a real cooldown.
    private const byte GcdGroup = 58;

    private static uint cachedJob;
    private static int cachedLevel = -1;
    private static List<CatalogEntry> cached = new();
    private static readonly Dictionary<string, PropertyInfo?> CategoryProps = new();

    public static IReadOnlyList<CatalogEntry> ForCurrentJob()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return Array.Empty<CatalogEntry>();

        var jobId = player.ClassJob.RowId;
        var level = (int)player.Level;
        if (jobId == cachedJob && level == cachedLevel) return cached;

        cachedJob = jobId;
        cachedLevel = level;
        cached = Build(jobId, level);
        return cached;
    }

    /// <summary>Look up one action's display info (used for icons of placed abilities).</summary>
    public static CatalogEntry? Find(uint actionId) => ForCurrentJob().FirstOrDefault(e => e.ActionId == actionId);

    private static List<CatalogEntry> Build(uint jobId, int level)
    {
        var job = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId);
        if (job == null) return new();
        var abbreviation = job.Value.Abbreviation.ExtractText();

        var am = ActionManager.Instance();
        var candidates = new List<(Action Row, uint Adjusted)>();

        foreach (var row in Plugin.DataManager.GetExcelSheet<Action>())
        {
            if (!row.IsPlayerAction || row.IsPvP) continue;
            if (row.Recast100ms == 0 || row.CooldownGroup == GcdGroup) continue;
            if (row.ClassJobLevel > level) continue;
            if (!CategoryAllows(row.ClassJobCategory.ValueNullable, abbreviation)) continue;

            var adjusted = am != null ? am->GetAdjustedActionId(row.RowId) : row.RowId;
            candidates.Add((row, adjusted));
        }

        // An ability and its upgrade (Vengeance / Damnation) resolve to the same live action.
        // Keep one entry per live action, keyed by the base (lowest) ID, shown with the live icon.
        var entries = new List<CatalogEntry>();
        foreach (var group in candidates.GroupBy(c => c.Adjusted))
        {
            var baseRow = group.OrderBy(c => c.Row.RowId).First().Row;
            var live = Plugin.DataManager.GetExcelSheet<Action>().GetRowOrDefault(group.Key) ?? baseRow;
            entries.Add(new CatalogEntry(
                baseRow.RowId,
                live.Name.ExtractText(),
                live.Icon,
                baseRow.ClassJobLevel,
                live.Recast100ms / 10f));
        }

        return entries.OrderBy(e => e.Level).ThenBy(e => e.Name).ToList();
    }

    /// <summary>ClassJobCategory has one bool column per job abbreviation (WAR, MRD, ...).</summary>
    private static bool CategoryAllows(ClassJobCategory? category, string abbreviation)
    {
        if (category == null || string.IsNullOrEmpty(abbreviation)) return false;
        if (!CategoryProps.TryGetValue(abbreviation, out var prop))
        {
            prop = typeof(ClassJobCategory).GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
            CategoryProps[abbreviation] = prop;
        }
        return prop?.GetValue(category.Value) is true;
    }
}
