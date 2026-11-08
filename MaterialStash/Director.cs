using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash;

/// <summary>AutoRetainer's stored stats for one retainer (only the fields we read; filled by IPC's JSON conversion).</summary>
public class ArRetainerStats
{
    public int Ilvl = -1;
    public int Gathering = -1;
    public int Perception = -1;
}

/// <summary>
/// Keeps the stock list topped up through AutoRetainer. Just before AutoRetainer sends a retainer
/// out, it asks plugins whether they want a particular venture for it ("OnSendRetainerToVenture" /
/// "SetVenture"; AutoRetainer's own override mechanism). If a stock item is short and this retainer
/// can venture for it, we answer with that venture; otherwise we stay silent and AutoRetainer does
/// what it was going to do. Only that one send is affected: AutoRetainer's plans aren't changed.
/// </summary>
public sealed unsafe class Director : IDisposable
{
    public sealed record Decision(DateTime At, string Retainer, string Item, int Yield, string Why);
    public readonly record struct RetainerInfo(ulong Id, string Name, uint ClassJob, int Level, uint VentureId);

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly ICallGateSubscriber<string, object> onSend;
    private readonly ICallGateSubscriber<uint, object> setVenture;
    private readonly ICallGateSubscriber<ulong, string, ArRetainerStats> getStats;
    /// <summary>Ventures we just chose, until the game shows them (retainer id -> venture, when).</summary>
    private readonly Dictionary<ulong, (uint Task, DateTime At)> recent = new();

    public List<Decision> Log { get; } = new();
    /// <summary>Set by the Director: a venture the game refused for this character recently.</summary>
    public static Func<uint, bool>? IsBlocked;
    private DateTime nextCheck = DateTime.MinValue;
    /// <summary>Money ventures, when nothing on the stock list fits (set by the plugin after both exist).</summary>
    public Selling? Selling { get; set; }

    public Director(Configuration config, Tracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
        var pi = Plugin.PluginInterface;
        onSend = pi.GetIpcSubscriber<string, object>("AutoRetainer.OnSendRetainerToVenture");
        setVenture = pi.GetIpcSubscriber<uint, object>("AutoRetainer.SetVenture");
        getStats = pi.GetIpcSubscriber<ulong, string, ArRetainerStats>("AutoRetainer.GetAdditionalRetainerData");
        onSend.Subscribe(OnSend);
        IsBlocked = task => tracker.Me() is { } me && me.BlockedVentures.TryGetValue(task, out var at) && (DateTime.UtcNow - at).TotalDays < 7;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        try { onSend.Unsubscribe(OnSend); } catch (Exception) { }
        Plugin.Framework.Update -= OnUpdate;
        IsBlocked = null;
    }

    /// <summary>
    /// The fallback when a venture we picked doesn't take (e.g. an item the game says you haven't
    /// gathered, where our own check couldn't tell): once the retainer list is back with the retainer
    /// on something else, that venture is noted as refused for this character and skipped for a week,
    /// so the next send picks the next stock item, a money venture, or leaves it to AutoRetainer.
    /// </summary>
    private void OnUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow.AddSeconds(2);
        try
        {
            if (!Core.GameUi.IsOpen("RetainerList")) return; // the list's data is fresh while it's up
            if (recent.Count == 0) return;
            var me = tracker.Me();
            foreach (var r in Retainers())
            {
                if (!recent.TryGetValue(r.Id, out var rc)) continue;
                var age = (DateTime.UtcNow - rc.At).TotalSeconds;
                if (age < 8) continue; // still being sent
                if (r.VentureId == rc.Task) { recent.Remove(r.Id); continue; }
                if (age > 600) { recent.Remove(r.Id); continue; } // too old to judge
                recent.Remove(r.Id);
                if (me == null) continue;
                me.BlockedVentures[rc.Task] = DateTime.UtcNow;
                config.Save();
                var name = Ventures.Task(rc.Task) is { } vt ? Items.Info(vt.ItemId)?.Name ?? $"#{vt.ItemId}" : $"venture {rc.Task}";
                Log.Insert(0, new Decision(DateTime.Now, r.Name, name, 0, "the game refused it: skipped for a week"));
                if (Log.Count > 30) Log.RemoveAt(Log.Count - 1);
                Plugin.ChatGui.Print($"[Stash] {r.Name} couldn't be sent for {name} (not gathered yourself yet?). I'll pick something else for a week.");
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: venture check failed"); }
    }

    // ------------------------------------------------------------------ retainers

    public static List<RetainerInfo> Retainers()
    {
        var list = new List<RetainerInfo>();
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return list;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            list.Add(new RetainerInfo(r->RetainerId, MemoryHelper.ReadStringNullTerminated((nint)r + 8), r->ClassJob, r->Level, r->VentureId));
        }
        return list;
    }

    public ArRetainerStats Stats(string retainerName)
    {
        try
        {
            var cid = Tracker.ContentId();
            return cid == 0 ? new() : getStats.InvokeFunc(cid, retainerName) ?? new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>Items on their way back from ventures now out (or done, not yet collected): item -> estimated amount.</summary>
    public Dictionary<uint, int> Incoming()
    {
        var result = new Dictionary<uint, int>();
        foreach (var r in Retainers())
        {
            var task = recent.TryGetValue(r.Id, out var rc) && (DateTime.UtcNow - rc.At).TotalMinutes < 2 ? rc.Task : r.VentureId;
            if (task == 0 || Ventures.Task(task) is not { } vt) continue;
            var s = Stats(r.Name);
            result[vt.ItemId] = result.GetValueOrDefault(vt.ItemId) + Ventures.Yield(vt, r.ClassJob, s.Ilvl, s.Perception);
        }
        return result;
    }

    /// <summary>Why this retainer can't take this venture, or null if it can.</summary>
    public static string? CantTake(VentureTask t, RetainerInfo r, ArRetainerStats s)
    {
        if (t.Category != Ventures.CategoryFor(r.ClassJob)) return "wrong class";
        if (r.Level < t.RetainerLevel) return $"needs retainer level {t.RetainerLevel}";
        if (t.RequiredGathering > 0 && s.Gathering >= 0 && s.Gathering < t.RequiredGathering) return $"needs gathering {t.RequiredGathering}";
        if (t.RequiredItemLevel > 0 && s.Ilvl >= 0 && s.Ilvl < t.RequiredItemLevel) return $"needs item level {t.RequiredItemLevel}";
        if (Ventures.IsGatherer(r.ClassJob) && Ventures.GatheredItem(t.ItemId) == false) return "you haven't gathered it yourself yet";
        if (IsBlocked?.Invoke(t.TaskId) == true) return "the game refused it last time (tried again after a week)";
        return null;
    }

    /// <summary>The venture this retainer should go on for stock, or null (leave it to AutoRetainer).</summary>
    public (VentureTask Task, StockNeed Need, int Yield)? Choose(RetainerInfo r)
    {
        var me = tracker.Me();
        if (me == null || me.NoRedirect.Contains(r.Id)) return null;
        var stats = Stats(r.Name);
        foreach (var need in Stock.Needs(config, me, Incoming()))
        {
            if (!need.BelowTarget) break; // the rest are stocked
            foreach (var t in Ventures.For(need.ItemId).OrderByDescending(t => t.RetainerLevel))
            {
                if (CantTake(t, r, stats) != null) continue;
                var yield = Ventures.Yield(t, r.ClassJob, stats.Ilvl, stats.Perception);
                if (yield <= 0) continue;
                return (t, need, yield);
            }
        }
        return null;
    }

    private void OnSend(string retainerName)
    {
        try
        {
            var r = Retainers().FirstOrDefault(x => x.Name == retainerName);
            if (r.Id == 0) return;
            if (!config.RedirectVentures) return;
            if (tracker.Me() is { } me0 && me0.NoRedirect.Contains(r.Id)) return;
            if (Choose(r) is not { } c)
            {
                // Nothing on the stock list for this retainer: the best money venture, if any passes.
                if (Selling?.Choose(r, Stats(r.Name)) is not { } m) return;
                setVenture.InvokeAction(m.Task.TaskId);
                recent[r.Id] = (m.Task.TaskId, DateTime.UtcNow);
                Selling.Queue(m.Task.ItemId);
                var name = Items.Info(m.Task.ItemId)?.Name ?? $"#{m.Task.ItemId}";
                var mwhy = $"money: ~{m.Value:N0} gil at {m.UnitPrice:N0} each, sells {m.UnitsPerDay:N0}/day";
                Log.Insert(0, new Decision(DateTime.Now, retainerName, name, m.Yield, mwhy));
                if (Log.Count > 30) Log.RemoveAt(Log.Count - 1);
                if (config.AnnounceRedirects) Plugin.ChatGui.Print($"[Stash] {retainerName} -> {name} (~{m.Yield}; {mwhy})");
                return;
            }
            setVenture.InvokeAction(c.Task.TaskId);
            recent[r.Id] = (c.Task.TaskId, DateTime.UtcNow);
            var why = c.Need.BelowMin ? $"below minimum: {c.Need.Total:N0}/{c.Need.Min:N0}" : $"toward target: {c.Need.Total:N0}/{c.Need.Target:N0}";
            Log.Insert(0, new Decision(DateTime.Now, retainerName, c.Need.Name, c.Yield, why));
            if (Log.Count > 30) Log.RemoveAt(Log.Count - 1);
            if (config.AnnounceRedirects)
                Plugin.ChatGui.Print($"[Stash] {retainerName} -> {c.Need.Name} (~{c.Yield}; {why})");
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash: venture redirect failed (AutoRetainer continues with its own plan)"); }
    }
}
