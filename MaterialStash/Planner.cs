using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash;

/// <summary>
/// Decides where things go, from the retainer records (the open retainer's record is refreshed
/// before every move, so it's exact; the others are as last seen).
///  Materials: retainers that already hold the item (keep together), then the category's homes in
///  order, then the overflow order; the first with room wins.
///  Crystals: retainers in the bell's list order, each filled to the cap before the next; you keep
///  a set amount of each, taken back from the last retainer holding them when you're short.
/// </summary>
public sealed class Planner
{
    private readonly Configuration config;
    private readonly Tracker tracker;

    public Planner(Configuration config, Tracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
    }

    private List<ulong> Usable(CharacterData me) => me.Order.Where(id => !me.Excluded.Contains(id)).ToList();

    /// <summary>Candidate retainers for an item, first choice first.</summary>
    public List<ulong> Destinations(uint itemId)
    {
        var me = tracker.Me();
        if (me == null) return new();
        var usable = Usable(me).ToHashSet();
        var cat = Items.Info(itemId)?.CategoryId ?? 0;
        var homes = me.CategoryHomes.TryGetValue(cat, out var h) ? h : new List<ulong>();
        var preference = homes.Concat(me.Overflow).Where(usable.Contains).Distinct().ToList();

        var result = new List<ulong>();
        if (config.KeepTogether)
        {
            var holders = me.Order.Where(id => usable.Contains(id) && me.Retainers.TryGetValue(id, out var s) && s.Items.ContainsKey(itemId)).ToList();
            // Holders the preference lists rank, in that order, then any other holder.
            result.AddRange(preference.Where(holders.Contains));
            result.AddRange(holders.Where(x => !result.Contains(x)));
        }
        result.AddRange(preference.Where(x => !result.Contains(x)));
        return result;
    }

    /// <summary>Room for (some of) this item on that retainer, as far as we know. Unknown = yes (we'll see when it's open).</summary>
    public bool HasRoom(ulong retainerId, uint itemId)
    {
        var me = tracker.Me();
        if (me == null || !me.Retainers.TryGetValue(retainerId, out var s)) return false;
        if (s.Items.TryGetValue(itemId, out var qty) && s.Stacks.TryGetValue(itemId, out var stacks) && stacks * Tracker.StackMax(itemId) > qty) return true;
        if (s.UsedSlots < 0 || s.Seen == null) return true;
        return s.UsedSlots < Tracker.RetainerSlots - config.ReserveSlots;
    }

    public ulong? Best(uint itemId)
    {
        foreach (var id in Destinations(itemId))
            if (HasRoom(id, itemId)) return id;
        return null;
    }

    private int CrystalsOn(ulong retainerId, uint crystalId) =>
        tracker.Me()?.Retainers.TryGetValue(retainerId, out var s) == true ? s.Crystals.GetValueOrDefault(crystalId) : 0;

    /// <summary>The first retainer in list order that isn't at the cap for this crystal.</summary>
    public ulong? CrystalDepositTarget(uint crystalId)
    {
        var me = tracker.Me();
        if (me == null) return null;
        foreach (var id in Usable(me))
            if (CrystalsOn(id, crystalId) < Items.CrystalCap) return id;
        return null;
    }

    /// <summary>The last retainer in list order holding this crystal (so earlier ones stay full).</summary>
    public ulong? CrystalWithdrawSource(uint crystalId)
    {
        var me = tracker.Me();
        if (me == null) return null;
        foreach (var id in Enumerable.Reverse(Usable(me)))
            if (CrystalsOn(id, crystalId) > 0) return id;
        return null;
    }

    public readonly record struct Move(Tracker.Stack Stack, ulong? To);
    public readonly record struct CrystalMove(uint CrystalId, int Amount, bool Deposit, ulong? Retainer);

    /// <summary>Every stashable stack in your bags and where it would go.</summary>
    public List<Move> PreviewMaterials() =>
        Tracker.Stacks(Tracker.Bags, id => Items.Stashable(id, config)).Select(s => new Move(s, Best(s.ItemId))).ToList();

    /// <summary>Crystals to put away (above your keep amount) or take back (below it).</summary>
    public List<CrystalMove> PreviewCrystals()
    {
        var list = new List<CrystalMove>();
        if (!config.IncludeCrystals) return list;
        var mine = Tracker.Crystals(InventoryType.Crystals);
        for (var c = Items.FirstCrystal; c <= Items.LastCrystal; c++)
        {
            var have = mine.TryGetValue(c, out var m) ? m.Qty : 0;
            var keep = config.KeepOf(c);
            if (have > keep) list.Add(new CrystalMove(c, have - keep, true, CrystalDepositTarget(c)));
            else if (have < keep && config.CrystalWithdrawToKeep && CrystalWithdrawSource(c) is { } src)
                list.Add(new CrystalMove(c, keep - have, false, src));
        }
        return list;
    }

    /// <summary>Retainers a stash run needs to visit, in the bell's order.</summary>
    public List<ulong> RetainersToVisit()
    {
        var me = tracker.Me();
        if (me == null) return new();
        var wanted = PreviewMaterials().Where(m => m.To != null).Select(m => m.To!.Value)
            .Concat(PreviewCrystals().Where(c => c.Retainer != null).Select(c => c.Retainer!.Value))
            .ToHashSet();
        return me.Order.Where(wanted.Contains).ToList();
    }
}
