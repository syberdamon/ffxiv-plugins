using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MaterialStash.Core;
using Syberdamon.Shared;

namespace MaterialStash;

/// <summary>
/// Starts a Turn-In Manager run once you're free to travel, and retries refusals that pass on their own.
/// Right after GatherBuddy stops you can still be mounted, flying, gathering or moving, and Lifestream then
/// refuses the teleport (seen: 40 ores gathered, "Lifestream refused the teleport", run over in the field).
/// </summary>
public sealed unsafe class TurnInStarter
{
    private const int MaxTries = 5;
    private readonly TurnInIpc ipc;
    private readonly GatherBuddyIpc gather;
    private readonly VNavIpc vnav;
    private uint item;
    private int count, tries;
    private DateTime next, since, lastDismount;

    public TurnInStarter(TurnInIpc ipc, GatherBuddyIpc gather, VNavIpc vnav) { this.ipc = ipc; this.gather = gather; this.vnav = vnav; }

    /// <summary>What it's waiting on, for the status line.</summary>
    public string Waiting { get; private set; } = "";

    public void Begin(uint itemId, int buy)
    {
        item = itemId; count = buy; tries = 0;
        next = since = DateTime.UtcNow;
        Waiting = "";
    }

    /// <summary>
    /// Null while still waiting or retrying; ("", false) once the run has started; (why, true) when it kept
    /// failing for a reason that should have passed; (why, false) when Turn-In Manager said no for good.
    /// </summary>
    public (string Why, bool Stuck)? Tick()
    {
        if (DateTime.UtcNow < next) return null;
        next = DateTime.UtcNow.AddSeconds(1);
        if (Busy() is { } busy)
        {
            Waiting = busy;
            if ((DateTime.UtcNow - since).TotalMinutes > 3) return ($"couldn't get free to travel for 3 minutes ({busy})", true);
            return null;
        }
        Waiting = "";
        var msg = ipc.Start(item, count);
        if (msg.Length == 0) return ("", false);
        if (!Passing(msg)) return (msg, false);
        if (++tries >= MaxTries) return ($"{msg} (tried {tries} times)", true);
        Waiting = $"Turn-In Manager: {msg} Trying again ({tries}/{MaxTries})...";
        next = DateTime.UtcNow.AddSeconds(6);
        return null;
    }

    /// <summary>Refusals that come from the moment (travel, another run finishing), not from what's in your bags.</summary>
    private static bool Passing(string msg) =>
        msg.Contains("Lifestream", StringComparison.OrdinalIgnoreCase) || msg.Contains("already going", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("combat", StringComparison.OrdinalIgnoreCase) || msg.Contains("logged in", StringComparison.OrdinalIgnoreCase);

    private static readonly ConditionFlag[] BusyFlags =
    {
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.Casting, ConditionFlag.Gathering,
        ConditionFlag.ExecutingGatheringAction, ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.Occupied,
        ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.InCombat, ConditionFlag.Jumping,
    };

    /// <summary>Why you can't travel yet, or null. Gets you off your mount (once every 3 s) if that's all that's left.</summary>
    private string? Busy()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) return "loading";
        if (gather.AutoGathering) { gather.StopAutoGather(); return "GatherBuddy's auto-gather is still on"; }
        if (vnav.IsBusy) { vnav.Stop(); return "still moving"; }
        foreach (var f in BusyFlags) if (Plugin.Condition[f]) return f.ToString();
        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.InFlight])
        {
            // [Assumption] dismounting first is what lets the teleport through; general action 23 = Dismount (lands first when flying).
            if ((DateTime.UtcNow - lastDismount).TotalSeconds > 3)
            {
                lastDismount = DateTime.UtcNow;
                FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 23);
            }
            return "getting off the mount";
        }
        return null;
    }
}

/// <summary>
/// "Turn in collectables": every collectable in your bags that the appraiser pays scrips for, one scrip kind
/// at a time, through Turn-In Manager. If a scrip goal uses that scrip, its item is bought with the scrips
/// (up to what the goal still needs) so a turn-in never runs into the 4,000 cap; otherwise nothing is bought
/// and collectables that would go over the cap stay in your bags.
/// </summary>
public sealed class CollectableTurnIn : IDisposable
{
    private readonly Plugin plugin;
    private readonly TurnInStarter starter;
    private readonly Queue<(uint Currency, uint Item, int Buy, int Held)> queue = new();
    private (uint Currency, uint Item, int Buy, int Held) current;
    private enum Step { Idle, Starting, Running }
    private Step step;
    private DateTime stepStart, next;
    private readonly List<string> done = new();

    public string Status { get; private set; } = "";
    public bool Busy => step != Step.Idle;

    public CollectableTurnIn(Plugin plugin)
    {
        this.plugin = plugin;
        starter = new TurnInStarter(plugin.Scrips.TurnIn, plugin.Scrips.GatherBuddy, new VNavIpc());
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) Stop("Plugin unloaded.");
    }

    /// <summary>Collectables in your bags that pay something, by scrip kind.</summary>
    public static Dictionary<uint, int> Payable()
    {
        var rw = ScripData.Rewards(Plugin.DataManager);
        return ScripData.CollectablesInBags(Plugin.DataManager)
            .Where(c => rw.TryGetValue(c.ItemId, out var r) && ScripData.Pays(r, c.Collectability) > 0)
            .GroupBy(c => rw[c.ItemId].CurrencyItemId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public string? CantStart()
    {
        if (Busy) return "Already turning in.";
        if (plugin.Tracker.Me() == null) return "Log in first.";
        if (!plugin.Scrips.TurnIn.IsLoaded) return "Turn-In Manager isn't installed or loaded (it does the turn-ins).";
        if (plugin.Scrips.Busy) return "Scrip goals are running (they turn in as they go).";
        if (plugin.Runner.Busy) return "A stash run is going.";
        if (Payable().Count == 0) return "No collectables in your bags that pay scrips.";
        return null;
    }

    public void Start()
    {
        if (CantStart() is { } why) { Status = why; Plugin.ChatGui.PrintError($"[Stash] {why}"); return; }
        var me = plugin.Tracker.Me()!;
        var shop = ScripData.Shop(Plugin.DataManager);
        queue.Clear();
        done.Clear();
        foreach (var (currency, held) in Payable())
        {
            // A goal paid in this scrip: buy toward it. Otherwise any item of that scrip, buying none.
            var goal = plugin.Configuration.ScripGoals
                .Where(g => g.Enabled && g.Target > 0 && shop.TryGetValue(g.ItemId, out var e) && e.CurrencyItemId == currency)
                .Select(g => (g.ItemId, Need: ScripGoals.Plan(g, me)?.Need ?? 0))
                .FirstOrDefault(x => x.Need > 0);
            if (goal.ItemId != 0) { queue.Enqueue((currency, goal.ItemId, goal.Need, held)); continue; }
            var any = shop.Values.Where(e => e.CurrencyItemId == currency).OrderBy(e => e.Cost).FirstOrDefault();
            if (any == null) { done.Add($"{held} {ScripData.ScripNames.GetValueOrDefault(currency, "scrip")} collectable(s): no shop entry found, skipped."); continue; }
            queue.Enqueue((currency, any.ItemId, 0, held));
        }
        NextKind();
    }

    public void Stop(string why = "Stopped.")
    {
        if (!Busy) return;
        if (step == Step.Running && plugin.Scrips.TurnIn.Busy) plugin.Scrips.TurnIn.Stop();
        step = Step.Idle;
        Status = $"Turn in collectables: {why}";
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private static string Scrip(uint currency) => ScripData.ScripNames.GetValueOrDefault(currency, "scrips");

    private void NextKind()
    {
        if (queue.Count == 0)
        {
            step = Step.Idle;
            Status = done.Count == 0 ? "Turn in collectables: nothing to do." : "Turn in collectables: " + string.Join(" ", done);
            Plugin.ChatGui.Print($"[Stash] {Status}");
            return;
        }
        current = queue.Dequeue();
        starter.Begin(current.Item, current.Buy);
        step = Step.Starting;
        stepStart = DateTime.UtcNow;
        Status = $"Turning in {current.Held} collectable(s) for {Scrip(current.Currency)}...";
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(1);
        try
        {
            var ti = plugin.Scrips.TurnIn;
            switch (step)
            {
                case Step.Starting:
                    var r = starter.Tick();
                    if (r == null) { Status = $"{Scrip(current.Currency)}: {(starter.Waiting.Length > 0 ? $"waiting ({starter.Waiting})" : "starting")}..."; return; }
                    if (r.Value.Why.Length > 0)
                    {
                        if (r.Value.Stuck) { Stop($"Turn-In Manager couldn't start: {r.Value.Why}"); return; }
                        done.Add($"{Scrip(current.Currency)}: {r.Value.Why}");
                        NextKind();
                        return;
                    }
                    step = Step.Running;
                    stepStart = DateTime.UtcNow;
                    return;
                case Step.Running:
                    if (ti.Busy) { Status = $"{Scrip(current.Currency)}: {ti.Status}"; return; }
                    if ((DateTime.UtcNow - stepStart).TotalSeconds < 2) return;
                    if (ti.Failed) { Stop($"Turn-In Manager stopped: {ti.Status}"); return; }
                    var left = Payable().GetValueOrDefault(current.Currency);
                    done.Add($"{Scrip(current.Currency)}: {current.Held - left} turned in" +
                             (ti.Bought > 0 ? $", bought {ti.Bought} {Items.Info(current.Item)?.Name}" : "") +
                             (left > 0 ? $", {left} left (scrip cap)." : "."));
                    NextKind();
                    return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Turn in collectables failed");
            Stop("Error (see /xllog).");
        }
    }
}
