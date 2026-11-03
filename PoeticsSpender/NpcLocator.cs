using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace PoeticsSpender;

/// <summary>
/// Where a named NPC stands, from game data (Level sheet placements of the ENpcResident),
/// so we can walk there before the NPC is close enough to be loaded into the object table.
/// Fallback: the wiki map coordinates converted to world space.
/// </summary>
public static class NpcLocator
{
    private static readonly Dictionary<string, Vector3?> Cache = new();

    // Wiki map coordinates (Idyllshire map, size factor 400, no offset) if game data has no placement.
    private static readonly Dictionary<string, Vector2> MapCoords = new()
    {
        ["Hismena"] = new(5.7f, 5.2f),
        ["Bertana"] = new(5.9f, 5.2f),
    };

    public static Vector3? Find(string englishName, uint territory, float playerY)
    {
        if (Cache.TryGetValue(englishName, out var hit)) return hit ?? FromMap(englishName, playerY);

        Vector3? found = null;
        try
        {
            var ids = Plugin.DataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English)
                .Where(r => r.Singular.ExtractText().Equals(englishName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RowId)
                .ToHashSet();
            if (ids.Count > 0)
            {
                foreach (var lv in Plugin.DataManager.GetExcelSheet<Level>())
                {
                    if (lv.Territory.RowId != territory || !ids.Contains(lv.Object.RowId)) continue;
                    found = new Vector3(lv.X, lv.Y, lv.Z);
                    break;
                }
            }
            Plugin.Log.Info($"{englishName}: npc ids [{string.Join(",", ids)}], placement {(found?.ToString() ?? "not in Level sheet")}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't look up {englishName}'s placement");
        }

        Cache[englishName] = found;
        return found ?? FromMap(englishName, playerY);
    }

    private static Vector3? FromMap(string name, float playerY)
    {
        if (!MapCoords.TryGetValue(name, out var c)) return null;
        return new Vector3(ToWorld(c.X), playerY, ToWorld(c.Y));
    }

    /// <summary>
    /// Inverse of Dalamud's MapUtil: coord = 0.02*offset + 2048/sizeFactor + 0.02*world + 1.
    /// [Assumption] Idyllshire: size factor 400, offset 0.
    /// </summary>
    private static float ToWorld(float coord) => (coord - 1f - 2048f / 400f) / 0.02f;
}
