using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace MaterialStash;

/// <summary>
/// Money ventures: when nothing on the stock list fits a retainer, send it for whatever it can bring
/// back that's worth the most on your home world, among items that sell at least N ventures' worth a
/// day (N adjustable; optionally counting what other retainers are already fetching and what you hold
/// to sell). What comes back goes on the to-sell list: it stays in your bags, a sound plays, chat lists
/// it, and the bag slots are boxed until it's gone (sold, or listed by auto-sell).
/// </summary>
public sealed class Selling : IDisposable
{
    public sealed record Pick(VentureTask Task, int Yield, long UnitPrice, long Value, double UnitsPerDay, double Demand);

    private const double MarketTax = 0.95;

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly Market market;
    private readonly Director director;
    private DateTime nextPrefetch = DateTime.MinValue, nextBagCheck = DateTime.MinValue;
    private Dictionary<uint, int> lastInBags = new();
    private bool primed;

    public Selling(Configuration config, Tracker tracker, Market market, Director director)
    {
        this.config = config;
        this.tracker = tracker;
        this.market = market;
        this.director = director;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static string HomeWorld()
    {
        try { return Plugin.ObjectTable.LocalPlayer?.HomeWorld.Value.Name.ExtractText() ?? ""; }
        catch (Exception) { return ""; }
    }

    private static bool Marketable(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId % 1_000_000) is { } r && r.ItemSearchCategory.RowId != 0 && !r.IsUntradable;

    /// <summary>Every venture item at least one of your retainers can bring back (for prices).</summary>
    public HashSet<uint> CandidateItems()
    {
        var set = new HashSet<uint>();
        foreach (var r in Director.Retainers())
        {
            var cat = Ventures.CategoryFor(r.ClassJob);
            foreach (var t in Ventures.AllTasks())
                if (t.Category == cat && t.RetainerLevel <= r.Level && Marketable(t.ItemId)) set.Add(t.ItemId);
        }
        return set;
    }

    /// <summary>The best money venture for this retainer, or null (leave it to AutoRetainer).</summary>
    public Pick? Choose(Director.RetainerInfo r, ArRetainerStats stats, Dictionary<uint, int>? incoming = null)
    {
        var me = tracker.Me();
        if (me == null || !config.MoneyVentures) return null;
        incoming ??= director.Incoming();
        Pick? best = null;
        var cat = Ventures.CategoryFor(r.ClassJob);
        foreach (var t in Ventures.AllTasks())
        {
            if (t.Category != cat || Director.CantTake(t, r, stats) != null) continue;
            if (market.Get(t.ItemId) is not { } info || info.Price <= 0) continue;
            var y = Ventures.Yield(t, r.ClassJob, stats.Ilvl, stats.Perception);
            if (y <= 0) continue;
            var value = (long)(y * info.Price * MarketTax);
            if (value < config.MinVentureValue) continue;
            // Demand: the item must sell N ventures' worth a day (plus, combined, what's coming and what's held).
            double load = y;
            if (config.DemandCombined)
                load += incoming.GetValueOrDefault(t.ItemId) + Tracker.Stacks(Tracker.Bags, id => id % 1_000_000 == t.ItemId).Sum(s => s.Qty);
            var demand = config.DemandFactor * load;
            if (info.UnitsPerDay < demand) continue;
            if (best == null || value > best.Value) best = new Pick(t, y, info.Price, value, info.UnitsPerDay, demand);
        }
        return best;
    }

    /// <summary>A money venture was chosen: its item goes on the to-sell list.</summary>
    public void Queue(uint itemId)
    {
        var me = tracker.Me();
        if (me != null && me.SellQueue.Add(itemId % 1_000_000)) tracker.Save(force: true);
    }

    /// <summary>Queued items in your bags now: item -> quantity.</summary>
    public Dictionary<uint, int> InBags()
    {
        var me = tracker.Me();
        var result = new Dictionary<uint, int>();
        if (me == null || me.SellQueue.Count == 0) return result;
        foreach (var s in Tracker.Stacks(Tracker.Bags, id => me.SellQueue.Contains(id % 1_000_000)))
            result[s.ItemId % 1_000_000] = result.GetValueOrDefault(s.ItemId % 1_000_000) + s.Qty;
        return result;
    }

    /// <summary>Price to list at: lowest home-world listing minus the undercut, else the median sale.</summary>
    public long ListPrice(uint itemId)
    {
        if (market.Get(itemId) is not { } i) return 0;
        if (i.Lowest > 0) return Math.Max(1, i.Lowest - config.Undercut);
        return i.Median;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (Tracker.ContentId() == 0) return;
            if (DateTime.UtcNow >= nextPrefetch)
            {
                nextPrefetch = DateTime.UtcNow.AddMinutes(5);
                if (config.MoneyVentures || config.AutoSell)
                {
                    var ids = CandidateItems();
                    if (tracker.Me() is { } me) ids.UnionWith(me.SellQueue);
                    market.Request(ids, HomeWorld(), Math.Max(10, config.PriceRefreshMinutes));
                }
            }
            if (DateTime.UtcNow >= nextBagCheck)
            {
                nextBagCheck = DateTime.UtcNow.AddSeconds(2);
                CheckBags();
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: selling update failed"); }
    }

    /// <summary>New to-sell items in the bags: sound and a chat list. Also drops items no longer coming or held.</summary>
    private void CheckBags()
    {
        var me = tracker.Me();
        if (me == null) return;
        var now = InBags();
        var arrived = now.Where(kv => kv.Value > lastInBags.GetValueOrDefault(kv.Key)).ToList();
        if (arrived.Count > 0 && primed)
        {
            if (config.SellSound) PlaySound(config.SellSoundId);
            var lines = now.Select(kv =>
            {
                var price = market.Get(kv.Key)?.Price ?? 0;
                return $"{Items.Info(kv.Key)?.Name} x{kv.Value}" + (price > 0 ? $" (~{kv.Value * price * MarketTax:N0} gil)" : "");
            });
            Plugin.ChatGui.Print($"[Stash] To sell (kept in your bags, boxed): {string.Join(", ", lines)}");
        }
        lastInBags = now;
        primed = true;

        // Off the list once it's neither in your bags nor on its way from a venture.
        var coming = director.Incoming().Keys.Select(k => k % 1_000_000).ToHashSet();
        if (me.SellQueue.RemoveWhere(id => !now.ContainsKey(id) && !coming.Contains(id)) > 0) tracker.Save(force: true);
    }

    private static unsafe void PlaySound(int id)
    {
        try { UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(id, 1, 16)); }
        catch (Exception ex) { Plugin.Log.Debug(ex, "sound failed"); }
    }
}
