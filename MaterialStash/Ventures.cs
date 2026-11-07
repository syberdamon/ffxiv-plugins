using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Lumina.Excel.Sheets;

namespace MaterialStash;

/// <summary>A targeted venture (hunting, mining, botany, fishing) that brings back one item.</summary>
public sealed record VentureTask(
    uint TaskId, uint ItemId, uint Category, int RetainerLevel, int RequiredGathering, int RequiredItemLevel,
    uint GatheringItemId, int[] Quantity, int[] PerceptionDoL, int[] PerceptionFsh, int[] ItemLevelDoW);

/// <summary>
/// The game's venture list, indexed by the item each targeted venture brings back. Which retainer can
/// take which venture (class category, level, gathering / item level) follows AutoRetainer's rules.
/// </summary>
public static class Ventures
{
    // ClassJob ids
    public const uint Miner = 16, Botanist = 17, Fisher = 18;
    // ClassJobCategory ids used by retainer ventures (from AutoRetainer)
    public const uint CatMiner = 17, CatBotanist = 18, CatFisher = 19, CatCombat = 34;

    private static Dictionary<uint, List<VentureTask>>? byItem;
    private static Dictionary<uint, VentureTask>? byTask;

    public static uint CategoryFor(uint classJob) => classJob switch
    {
        Botanist => CatBotanist,
        Miner => CatMiner,
        Fisher => CatFisher,
        _ => CatCombat,
    };

    public static string CategoryName(uint cat) => cat switch
    {
        CatMiner => "MIN",
        CatBotanist => "BTN",
        CatFisher => "FSH",
        _ => "combat",
    };

    public static bool IsGatherer(uint classJob) => classJob is Miner or Botanist or Fisher;

    public static IReadOnlyList<VentureTask> For(uint itemId)
    {
        Build();
        return byItem!.TryGetValue(itemId % 1_000_000, out var l) ? l : Array.Empty<VentureTask>();
    }

    public static IEnumerable<VentureTask> AllTasks()
    {
        Build();
        return byTask!.Values;
    }

    public static VentureTask? Task(uint taskId)
    {
        Build();
        return byTask!.TryGetValue(taskId, out var t) ? t : null;
    }

    private static void Build()
    {
        if (byItem != null) return;
        var items = new Dictionary<uint, List<VentureTask>>();
        var tasks = new Dictionary<uint, VentureTask>();
        try
        {
            var dm = Plugin.DataManager;
            var normals = dm.GetExcelSheet<RetainerTaskNormal>();
            var gatheringByItem = new Dictionary<uint, uint>();
            foreach (var g in dm.GetExcelSheet<GatheringItem>())
                if (g.Item.RowId != 0 && !gatheringByItem.ContainsKey(g.Item.RowId)) gatheringByItem[g.Item.RowId] = g.RowId;

            foreach (var t in dm.GetExcelSheet<RetainerTask>())
            {
                if (t.IsRandom || t.Task.RowId == 0) continue;
                if (normals.GetRowOrDefault(t.Task.RowId) is not { } n || n.Item.RowId == 0) continue;
                var param = t.RetainerTaskParameter.ValueNullable;
                var vt = new VentureTask(
                    t.RowId, n.Item.RowId, t.ClassJobCategory.RowId, t.RetainerLevel, t.RequiredGathering, t.RequiredItemLevel,
                    gatheringByItem.GetValueOrDefault(n.Item.RowId),
                    Ints(n.Quantity.Count, i => n.Quantity[i]),
                    param == null ? Array.Empty<int>() : Ints(param.Value.PerceptionDoL.Count, i => param.Value.PerceptionDoL[i]),
                    param == null ? Array.Empty<int>() : Ints(param.Value.PerceptionFSH.Count, i => param.Value.PerceptionFSH[i]),
                    param == null ? Array.Empty<int>() : Ints(param.Value.ItemLevelDoW.Count, i => param.Value.ItemLevelDoW[i]));
                tasks[t.RowId] = vt;
                if (!items.TryGetValue(vt.ItemId, out var list)) items[vt.ItemId] = list = new();
                list.Add(vt);
            }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash: reading the venture list failed"); }
        byItem = items;
        byTask = tasks;
    }

    private static int[] Ints(int count, Func<int, int> at)
    {
        var a = new int[count];
        for (var i = 0; i < count; i++) a[i] = at(i);
        return a;
    }

    /// <summary>
    /// Items brought back by this venture for a retainer with these stats (-1 = unknown: the
    /// lowest amount). Same thresholds AutoRetainer uses.
    /// </summary>
    public static int Yield(VentureTask t, uint classJob, int ilvl, int perception)
    {
        if (t.Quantity.Length == 0) return 0;
        var index = 0;
        var (thresholds, stat) = classJob == Fisher ? (t.PerceptionFsh, perception)
            : IsGatherer(classJob) ? (t.PerceptionDoL, perception)
            : (t.ItemLevelDoW, ilvl);
        if (stat >= 0)
            for (var i = 0; i < thresholds.Length; i++)
                if (stat >= thresholds[i]) index = i + 1;
        return t.Quantity[Math.Min(index, t.Quantity.Length - 1)];
    }

    // ---- "have you gathered this item yourself?" (the game won't send a retainer for it otherwise)

    private delegate byte IsGatheredDelegate(ushort gatheringItem);
    private static IsGatheredDelegate? isGathered;
    private static bool sigTried;

    /// <summary>
    /// True/false from the game; null if the check isn't available (signature from AutoRetainer;
    /// a game patch can move it).
    /// </summary>
    public static bool? Gathered(uint gatheringItemId)
    {
        if (gatheringItemId == 0) return true;
        if (!sigTried)
        {
            sigTried = true;
            try
            {
                if (Plugin.SigScanner.TryScanText("48 89 5C 24 ?? 57 48 83 EC 20 8B D9 8B F9", out var ptr))
                    isGathered = Marshal.GetDelegateForFunctionPointer<IsGatheredDelegate>(ptr);
                else Plugin.Log.Warning("Material Stash: gathered-item check not found (game patch?): assuming gathered.");
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: gathered-item check unavailable"); }
        }
        if (isGathered == null) return null;
        try { return isGathered((ushort)gatheringItemId) != 0; }
        catch (Exception) { return null; }
    }
}
