using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// Puts what this AutoRetainer cycle's ventures brought back into its home retainers once it's done.
/// Venture rewards land in your bags, so while AutoRetainer is busy the bags are watched and any
/// item whose count goes up is noted. Afterwards a Stash run takes just those items, and only the
/// ones on the stock list (directly or as a recipe ingredient); crystals and everything else that
/// was already in your bags stay put, as do money-venture items (to sell).
///
/// Two ways in:
///  - Multi mode: AutoRetainer's character post-processing (the IPC other plugins use for work
///    after a character's retainers are done, before it moves on). We ask for a turn only when
///    something's waiting; in it we walk to the bell if needed, tidy, then hand control back.
///  - Single character at the bell: when AutoRetainer goes from busy to idle with the retainer list
///    still open, the tidy starts there.
/// </summary>
public sealed class AfterAutoRetainer : IDisposable
{
    private const string Me = "MaterialStash";
    private readonly Plugin plugin;
    private readonly ICallGateSubscriber<object> onAdditional;
    private readonly ICallGateSubscriber<string, object> onReady;
    private readonly ICallGateSubscriber<string, object> request;
    private readonly ICallGateSubscriber<object> finish;

    private bool wasBusy;
    private ulong cid;
    private Dictionary<uint, int>? lastBags;
    /// <summary>Items (NQ ids) whose count went up in your bags during this AutoRetainer cycle.</summary>
    private readonly HashSet<uint> gained = new();
    private DateTime nextPoll;
    private DateTime idleSince = DateTime.MinValue;
    private enum Step { Idle, Bell, Run, Running }
    private Step step;
    private bool inPostprocess;
    private DateTime stepStart;

    public string Status { get; private set; } = "";

    public AfterAutoRetainer(Plugin plugin)
    {
        this.plugin = plugin;
        var pi = Plugin.PluginInterface;
        onAdditional = pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask");
        onReady = pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess");
        request = pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess");
        finish = pi.GetIpcSubscriber<object>("AutoRetainer.FinishCharacterPostprocessRequest");
        onAdditional.Subscribe(OnAdditional);
        onReady.Subscribe(OnReady);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        try { onAdditional.Unsubscribe(OnAdditional); } catch (Exception) { }
        try { onReady.Unsubscribe(OnReady); } catch (Exception) { }
        if (inPostprocess) Done("plugin unloaded");
    }

    /// <summary>This cycle's venture returns that the stock list wants and Stash would take.</summary>
    private HashSet<uint> ToStash()
    {
        var wanted = Stock.Wanted(plugin.Configuration);
        var me = plugin.Tracker.Me();
        return gained.Where(id => wanted.ContainsKey(id) && !Items.IsCrystal(id) && Items.Depositable(id, plugin.Configuration, me)).ToHashSet();
    }

    private bool Waiting => plugin.Configuration.StashAfterAutoRetainer && ToStash().Count > 0;

    private static Dictionary<uint, int> BagCounts()
    {
        var d = new Dictionary<uint, int>();
        foreach (var st in Tracker.Stacks(Tracker.Bags, _ => true)) d[st.ItemId % 1_000_000] = d.GetValueOrDefault(st.ItemId % 1_000_000) + st.Qty;
        return d;
    }

    /// <summary>While AutoRetainer works, note every item that turns up in (or grows in) your bags.</summary>
    private void Watch(bool arBusy)
    {
        var now = Tracker.ContentId();
        if (now != cid) { cid = now; gained.Clear(); lastBags = null; } // another character
        if (!arBusy) { lastBags = null; return; }
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(0.5);
        var bags = BagCounts();
        if (lastBags != null)
            foreach (var (id, n) in bags)
                if (n > lastBags.GetValueOrDefault(id)) gained.Add(id);
        lastBags = bags;
    }

    private void OnAdditional()
    {
        try { if (Waiting) request.InvokeAction(Me); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: couldn't ask AutoRetainer for a post-processing turn"); }
    }

    private void OnReady(string who)
    {
        if (who != Me) return;
        inPostprocess = true;
        if (!Waiting) { Done(null); return; }
        Begin();
    }

    private void Begin()
    {
        step = Runner.BellListOpen ? Step.Run : Step.Bell;
        stepStart = DateTime.UtcNow;
        if (step == Step.Bell) plugin.Bell.Start();
        Status = "Stashing venture returns...";
    }

    private void Done(string? why)
    {
        step = Step.Idle;
        gained.Clear();
        if (why != null) Plugin.ChatGui.Print($"[Stash] Venture returns: {why}");
        if (!inPostprocess) return;
        inPostprocess = false;
        try { finish.InvokeAction(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: couldn't hand control back to AutoRetainer"); }
    }

    private void OnUpdate(IFramework _)
    {
        var ar = plugin.AutoRetainer.IsBusy;
        try
        {
            if (step is Step.Idle or Step.Bell) Watch(ar && !inPostprocess);
            switch (step)
            {
                case Step.Idle:
                    // Single character: AutoRetainer just finished with the list still open.
                    if (wasBusy && !ar) idleSince = DateTime.UtcNow;
                    if (!ar && idleSince != DateTime.MinValue && (DateTime.UtcNow - idleSince).TotalSeconds > 2)
                    {
                        idleSince = DateTime.MinValue;
                        if (Waiting && Runner.BellListOpen && !plugin.Runner.Busy) { step = Step.Run; stepStart = DateTime.UtcNow; }
                        else gained.Clear(); // nothing for us this cycle: start the next one fresh
                    }
                    break;
                case Step.Bell:
                    if (Runner.BellListOpen) { step = Step.Run; stepStart = DateTime.UtcNow; break; }
                    if (!plugin.Bell.Busy && (DateTime.UtcNow - stepStart).TotalSeconds > 5 || (DateTime.UtcNow - stepStart).TotalMinutes > 3)
                        Done("couldn't get to a summoning bell; they wait in your bags for your next Stash.");
                    break;
                case Step.Run:
                    var items = ToStash();
                    if (items.Count == 0) { Done(null); break; }
                    plugin.Runner.StartOnly(items, inPostprocess);
                    if (plugin.Runner.Busy)
                    {
                        step = Step.Running;
                        Plugin.ChatGui.Print("[Stash] Stashing this cycle's venture returns: " + string.Join(", ", items.Take(8).Select(i => Items.Info(i)?.Name ?? $"#{i}")) + "...");
                    }
                    else if ((DateTime.UtcNow - stepStart).TotalSeconds > 10) Done(plugin.Runner.Status);
                    break;
                case Step.Running:
                    if (!plugin.Runner.Busy)
                    {
                        if (inPostprocess) GameUi.Close("RetainerList"); // AutoRetainer moves on from a closed bell
                        Done(null);
                    }
                    else if ((DateTime.UtcNow - stepStart).TotalMinutes > 15) { plugin.Runner.Stop(); Done("took too long: stopped."); }
                    break;
            }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash: after-AutoRetainer tidy failed"); Done(null); }
        wasBusy = ar;
    }
}
