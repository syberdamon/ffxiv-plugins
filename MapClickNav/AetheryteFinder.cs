using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace MapClickNav;

/// <summary>
/// Finds the attuned aetheryte closest to a destination.
/// Approach adapted from CBT/Automaton by Puni.sh (BSD-3-Clause): Utilities/Coords.cs.
/// </summary>
public static unsafe class AetheryteFinder
{
    public readonly record struct Result(uint AetheryteId, string Name, Vector3 Position);

    /// <summary>
    /// Closest teleportable (real, attuned) aetheryte in <paramref name="territoryId"/> to <paramref name="worldPos"/>.
    /// Aethernet shards are skipped, same as CBT's /tpflag.
    /// </summary>
    public static Result? FindClosest(uint territoryId, Vector3 worldPos)
    {
        // Special cases from CBT: zones whose aetheryte lives in a neighboring zone.
        if (territoryId == 886) return Describe(70); // Firmament -> Foundation (Ishgard)
        if (territoryId == 478) return Describe(75); // Idyllshire (Dravanian Hinterlands)

        var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
        var ui = UIState.Instance();

        Result? best = null;
        var bestDist = float.MaxValue;
        foreach (var a in sheet.Where(a => a.IsAetheryte && a.Territory.RowId == territoryId))
        {
            if (ui != null && !ui->IsAetheryteUnlocked(a.RowId)) continue;
            if (GetPosition(a) is not { } pos) continue;

            // Maps are mostly flat; compare on the ground plane like CBT does.
            var d = Vector2.DistanceSquared(new(worldPos.X, worldPos.Z), new(pos.X, pos.Z));
            if (d < bestDist)
            {
                bestDist = d;
                best = new Result(a.RowId, a.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Aetheryte {a.RowId}", pos);
            }
        }
        return best;
    }

    /// <summary>
    /// CBT's rule: teleport if (aetheryte to destination) + margin is shorter than (you to destination).
    /// The margin approximates the distance you'd cover in the time a teleport + remount takes.
    /// </summary>
    public static bool IsTeleportFaster(Vector3 playerPos, Vector3 dest, Vector3 aetherytePos, float margin) =>
        Vector3.Distance(dest, aetherytePos) + margin < Vector3.Distance(dest, playerPos);

    private static Result? Describe(uint aetheryteId)
    {
        var row = Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        if (row == null) return null;
        return new Result(aetheryteId, row.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Aetheryte {aetheryteId}",
            GetPosition(row.Value) ?? Vector3.Zero);
    }

    private static Vector3? GetPosition(Aetheryte a)
    {
        // Preferred: the aetheryte's Level row has exact world coordinates.
        if (a.Level.Count > 0 && a.Level[0].ValueNullable is { } level)
            return new Vector3(level.X, level.Y, level.Z);

        // Fallback: its map marker (pixel coordinates on the zone map).
        var map = a.Territory.ValueNullable?.Map.ValueNullable;
        if (map == null) return null;

        foreach (var marker in Plugin.DataManager.GetSubrowExcelSheet<MapMarker>().SelectMany(m => m))
        {
            if (marker.DataType != 3 || marker.DataKey.RowId != a.RowId) continue;
            // [Assumption] standard pixel -> world conversion: (pixel - 1024) / (SizeFactor/100) - offset.
            var scale = map.Value.SizeFactor / 100f;
            return new Vector3((marker.X - 1024f) / scale - map.Value.OffsetX, 0, (marker.Y - 1024f) / scale - map.Value.OffsetY);
        }
        return null;
    }
}
