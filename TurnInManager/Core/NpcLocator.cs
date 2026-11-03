using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace TurnInManager.Core;

/// <summary>Where a named NPC stands (territory + position), from the Level sheet. English names.</summary>
public static class NpcLocator
{
    private static readonly Dictionary<string, List<(uint Territory, Vector3 Pos)>> Cache = new();

    public static List<(uint Territory, Vector3 Pos)> Find(string englishName)
    {
        if (Cache.TryGetValue(englishName, out var hit)) return hit;
        var found = new List<(uint, Vector3)>();
        try
        {
            var ids = Plugin.DataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English)
                .Where(r => r.Singular.ExtractText().Equals(englishName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RowId).ToHashSet();
            if (ids.Count > 0)
                foreach (var lv in Plugin.DataManager.GetExcelSheet<Level>())
                    if (ids.Contains(lv.Object.RowId))
                        found.Add((lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z)));
            Plugin.Log.Info($"{englishName}: npc ids [{string.Join(",", ids)}], placements {found.Count}");
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, $"Couldn't look up {englishName}"); }
        Cache[englishName] = found;
        return found;
    }

    public static Vector3? InTerritory(string englishName, uint territory) =>
        Find(englishName).Where(p => p.Territory == territory).Select(p => (Vector3?)p.Pos).FirstOrDefault();
}
