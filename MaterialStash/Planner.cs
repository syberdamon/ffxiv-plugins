using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash;

/// <summary>
/// Decides where things go, from the retainer records (the open retainer's record is refreshed
/// before every move, so it's exact; the others are as last seen).
///
/// Homes, most specific first: an item pin, a name rule ("contains Totem"), the item's game category,
/// the category's group. Stash: retainers already holding the item (NQ and HQ together), then its
/// homes, then the overflow order; the first with room wins.
/// Tidy: an item with homes that sits on a retainer outside them goes home; an item with no homes that
/// is split across retainers is merged onto the one holding the most.
/// Crystals: retainers in the bell's list order, each filled to the cap before the next.
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

    private static List<ulong> Usable(CharacterData me) => me.Order.Where(id => !me.Excluded.Contains(id)).ToList();

    private static uint Nq(uint id) => id % 1_000_000;

    // ------------------------------------------------------------------ homes

    /// <summary>The item's explicit homes (pin, name rule, category, group), first choice first; empty = none.</summary>
    public List<ulong> Homes(uint itemId, CharacterData me)
    {
        var id = Nq(itemId);
        var usable = Usable(me).ToHashSet();
        List<ulong> Keep(IEnumerable<ulong> l) => l.Where(usable.Contains).Distinct().ToList();

        if (me.ItemPins.TryGetValue(id, out var pin) && pin.Count > 0) return Keep(pin);
        var info = Items.Info(id);
        var name = info?.Name ?? "";
        foreach (var rule in me.NameRules)
            if (rule.Text.Length > 0 && rule.Homes.Count > 0 && name.Contains(rule.Text, StringComparison.OrdinalIgnoreCase))
                return Keep(rule.Homes);
        var cat = info?.CategoryId ?? 0;
        if (me.CategoryHomes.TryGetValue(cat, out var ch) && ch.Count > 0) return Keep(ch);
        if (config.GroupOf(cat) is { } g && me.GroupHomes.TryGetValue(g.Name, out var gh) && gh.Count > 0) return Keep(gh);
        return new();
    }

    /// <summary>Where a rule puts it, for display: "pin", "name: Totem", "category", "group: Housing", or "".</summary>
    public string HomeSource(uint itemId, CharacterData me)
    {
        var id = Nq(itemId);
        if (me.ItemPins.TryGetValue(id, out var pin) && pin.Count > 0) return "pinned";
        var info = Items.Info(id);
        foreach (var rule in me.NameRules)
            if (rule.Text.Length > 0 && rule.Homes.Count > 0 && (info?.Name ?? "").Contains(rule.Text, StringComparison.OrdinalIgnoreCase))
                return $"name: {rule.Text}";
        var cat = info?.CategoryId ?? 0;
        if (me.CategoryHomes.TryGetValue(cat, out var ch) && ch.Count > 0) return $"category: {info?.Category}";
        if (config.GroupOf(cat) is { } g && me.GroupHomes.TryGetValue(g.Name, out var gh) && gh.Count > 0) return $"group: {g.Name}";
        return "";
    }

    /// <summary>Retainers holding this item (NQ or HQ), in list order.</summary>
    public List<ulong> Holders(uint itemId, CharacterData me)
    {
        var id = Nq(itemId);
        return Usable(me).Where(r => me.Retainers.TryGetValue(r, out var s) && (s.Items.ContainsKey(id) || s.Items.ContainsKey(id + 1_000_000))).ToList();
    }

    private static int HeldOn(CharacterData me, ulong r, uint itemId)
    {
        var id = Nq(itemId);
        return me.Retainers.TryGetValue(r, out var s) ? s.Items.GetValueOrDefault(id) + s.Items.GetValueOrDefault(id + 1_000_000) : 0;
    }

    /// <summary>Candidate retainers for depositing an item, first choice first.</summary>
    public List<ulong> Destinations(uint itemId)
    {
        var me = tracker.Me();
        if (me == null) return new();
        var homes = Homes(itemId, me);
        var preference = homes.Concat(Usable(me).Where(me.Overflow.Contains).OrderBy(me.Overflow.IndexOf)).Distinct().ToList();

        var result = new List<ulong>();
        if (config.KeepTogether)
        {
            var holders = Holders(itemId, me);
            // With homes, only holders that are homes count (a stray elsewhere doesn't pull more there).
            if (homes.Count > 0) holders = holders.Where(homes.Contains).ToList();
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

    // ------------------------------------------------------------------ tidy

    private bool TidyAllowed(uint itemId)
    {
        var info = Items.Info(itemId);
        if (info == null || Items.IsCrystal(itemId)) return false;
        return config.GroupOf(info.CategoryId)?.Tidy ?? true;
    }

    /// <summary>
    /// Where this item should live, for tidying (null = leave it). With homes: the first home holding
    /// it with room, else the first home with room. Without: if it's on more than one retainer, the
    /// one holding the most (ties: list order).
    /// </summary>
    public ulong? TidyTarget(uint itemId, CharacterData me)
    {
        var homes = Homes(itemId, me);
        if (homes.Count > 0)
        {
            foreach (var h in homes) if (HeldOn(me, h, itemId) > 0 && HasRoom(h, itemId)) return h;
            foreach (var h in homes) if (HasRoom(h, itemId)) return h;
            return null;
        }
        var holders = Holders(itemId, me);
        if (holders.Count < 2) return holders.Count == 1 ? holders[0] : Best(itemId);
        return holders.OrderByDescending(h => HeldOn(me, h, itemId)).ThenBy(h => me.Order.IndexOf(h)).First();
    }

    /// <summary>Should this item leave retainer r in a tidy? Returns where it goes, or null.</summary>
    public ulong? TidyMoveFrom(ulong r, uint itemId, CharacterData me)
    {
        if (!TidyAllowed(itemId)) return null;
        var homes = Homes(itemId, me);
        if (homes.Count > 0)
        {
            if (homes.Contains(r)) return null;
            var t = TidyTarget(itemId, me);
            return t != null && t != r ? t : null;
        }
        var holders = Holders(itemId, me);
        if (holders.Count < 2) return null;
        var keeper = TidyTarget(itemId, me);
        return keeper != null && keeper != r && HasRoom(keeper.Value, itemId) ? keeper : null;
    }

    public readonly record struct TidyMove(ulong From, ulong To, uint ItemId, int Stacks, bool Merge);

    /// <summary>Every move a tidy would make, from the records.</summary>
    public List<TidyMove> TidyPlan()
    {
        var me = tracker.Me();
        var list = new List<TidyMove>();
        if (me == null) return list;
        foreach (var r in Usable(me))
        {
            if (!me.Retainers.TryGetValue(r, out var s)) continue;
            foreach (var (id, _) in s.Items)
            {
                if (TidyMoveFrom(r, id, me) is not { } to) continue;
                list.Add(new TidyMove(r, to, id, s.Stacks.GetValueOrDefault(id, 1), Homes(id, me).Count == 0));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ crystals

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

    // ------------------------------------------------------------------ previews

    public readonly record struct Move(Tracker.Stack Stack, ulong? To);
    public readonly record struct CrystalMove(uint CrystalId, int Amount, bool Deposit, ulong? Retainer);

    /// <summary>While set, Stash takes only these items (NQ ids) and leaves crystals alone (after-AutoRetainer runs).</summary>
    public HashSet<uint>? Only { get; set; }
    public bool Allows(uint id) => Only == null || Only.Contains(Nq(id));

    /// <summary>Every stack in your bags that Stash takes, and where it would go.</summary>
    public List<Move> PreviewMaterials()
    {
        var me = tracker.Me();
        return Tracker.Stacks(Tracker.Bags, id => Items.Depositable(id, config, me) && Allows(id))
            .Select(s => new Move(s, me != null && me.TidyCarry.Contains(Nq(s.ItemId)) ? TidyTarget(s.ItemId, me) ?? Best(s.ItemId) : Best(s.ItemId)))
            .ToList();
    }

    /// <summary>Crystals to put away (above your keep amount) or take back (below it).</summary>
    public List<CrystalMove> PreviewCrystals()
    {
        var list = new List<CrystalMove>();
        if (!config.IncludeCrystals || Only != null) return list;
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

    /// <summary>Retainers a tidy lap visits: anything to take out, anything to bring, in the bell's order.</summary>
    public List<ulong> TidyVisits()
    {
        var me = tracker.Me();
        if (me == null) return new();
        var wanted = TidyPlan().SelectMany(m => new[] { m.From, m.To }).ToHashSet();
        foreach (var r in RetainersToVisit()) wanted.Add(r);
        return me.Order.Where(wanted.Contains).ToList();
    }
}
