using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// ICE break: while Ices Cosmic Exploration (ICE) is running and enough retainers are back from their
/// ventures, let ICE finish its current mission ("Stop after current mission"), go to a summoning bell
/// (one in sight, else Lifestream to the market board), let AutoRetainer collect and resend, stash the
/// stock items those ventures brought back, go back to Cosmic Exploration (Lifestream /li cosmic) and
/// start ICE again. Only acts while ICE is running; does nothing while another Material Stash job runs.
///
/// AutoRetainer starts by itself when the bell opens with ventures ready (its "Action on accessing
/// retainer bell if any ventures available" = Enable AutoRetainer, the default). While we stash
/// afterwards, AutoRetainer is suppressed so a second bell visit doesn't set it off again.
/// </summary>
public sealed unsafe class IceBreak : IDisposable
{
    private enum Phase { Idle, Finishing, Settle, ToBell, Retainers, Stash, Return, Resume }

    private readonly Plugin plugin;
    private readonly IceIpc ice = new();
    private readonly LifestreamIpc lifestream = new();
    private Configuration C => plugin.Configuration;
    private AutoRetainerIpc Ar => plugin.AutoRetainer;

    private Phase phase = Phase.Idle;
    private DateTime next, phaseStart, lastEnd = DateTime.MinValue;
    private DateTime calmSince, arIdleSince, closedSince, arrived, leaveStart;
    private bool arSeen, stashStarted, travelAsked, craftLogTried, suppressed;
    private int enableTries;

    public string Status { get; private set; } = "";
    public bool Active => phase != Phase.Idle;
    /// <summary>Waiting for ICE to finish its mission before leaving (ICE is still running).</summary>
    public bool Finishing => phase == Phase.Finishing;
    /// <summary>ICE is installed (the IPC answers).</summary>
    public bool IceLoaded => ice.IsLoaded;

    private static readonly ConditionFlag[] Activity =
    {
        ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction,
        ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction, ConditionFlag.Fishing,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.Casting,
        ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
    };

    public IceBreak(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Active) Stop("plugin unloaded");
        Unsuppress();
    }

    // ------------------------------------------------------------------ readiness

    /// <summary>Retainers back from a venture (from the game's list once a bell has been used this session, else AutoRetainer).</summary>
    public static int ReadyCount(AutoRetainerIpc ar)
    {
        var rm = RetainerManager.Instance();
        if (rm != null && rm->IsReady && rm->GetRetainerCount() > 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var n = 0;
            for (uint i = 0; i < rm->GetRetainerCount(); i++)
            {
                var r = rm->GetRetainerBySortedIndex(i);
                if (r == null || r->RetainerId == 0) continue;
                // Only retainers that went out: one with no venture at all might be one AutoRetainer leaves alone.
                if (r->VentureId != 0 && r->VentureComplete <= now) n++;
            }
            return n;
        }
        return ar.AnyReady ? 1 : 0;
    }

    /// <summary>Another Material Stash job is running (the break waits for it).</summary>
    private bool OthersBusy =>
        plugin.Runner.Busy || plugin.Bell.Busy || plugin.Saddle.Busy || plugin.Storeroom.Busy || plugin.Wardrobe.Busy ||
        plugin.Cleanup.Desynthing || plugin.AfterAr.Busy || (plugin.Scrips.Busy && !plugin.Scrips.Earning);

    // ------------------------------------------------------------------ control

    /// <summary>Take a break now (ICE must be running). For testing, or when you'd rather not wait.</summary>
    public string StartNow()
    {
        if (Active) return "An ICE break is already under way.";
        if (!ice.Running) return "ICE isn't running.";
        if (!Ar.IsLoaded) return "AutoRetainer isn't loaded.";
        if (OthersBusy) return "Another Material Stash job is running.";
        Begin("on request");
        return "";
    }

    /// <summary>Cancel the break where it is. ICE is left as it is (not restarted).</summary>
    public void Stop(string why = "stopped")
    {
        if (!Active) return;
        if (phase == Phase.Finishing) ice.StopAfterCurrent(false);
        if (phase == Phase.ToBell) plugin.Bell.Stop();
        if (phase == Phase.Return && lifestream.IsBusy) lifestream.Abort();
        Unsuppress();
        plugin.AfterAr.Release();
        phase = Phase.Idle;
        lastEnd = DateTime.UtcNow;
        Status = $"Break {why}.";
        Plugin.ChatGui.Print($"[Stash] ICE break {why}{(ice.Running ? "." : ": ICE is off, start it again yourself.")}");
    }

    private void Begin(string why)
    {
        ice.StopAfterCurrent(true);
        plugin.AfterAr.Hold = true;
        Set(Phase.Finishing, "ICE finishing its current mission...");
        Plugin.ChatGui.Print($"[Stash] ICE break ({why}): ICE stops after this mission, then retainers and stash, then back to ICE.");
    }

    private void Set(Phase p, string status)
    {
        phase = p;
        Status = status;
        phaseStart = DateTime.UtcNow;
        calmSince = arIdleSince = closedSince = arrived = leaveStart = DateTime.MinValue;
        arSeen = stashStarted = travelAsked = craftLogTried = false;
        enableTries = 0;
    }

    private double Elapsed => (DateTime.UtcNow - phaseStart).TotalSeconds;

    private static void Note(string msg) => Plugin.ChatGui.Print($"[Stash] ICE break: {msg}");

    /// <summary>Something went wrong before ICE was stopped or after we left: give up, ICE stays off.</summary>
    private void Fail(string why)
    {
        Unsuppress();
        plugin.AfterAr.Release();
        ice.StopAfterCurrent(false);
        phase = Phase.Idle;
        lastEnd = DateTime.UtcNow;
        Status = why;
        Plugin.ChatGui.PrintError($"[Stash] ICE break: {why}");
    }

    private void Unsuppress()
    {
        if (!suppressed) return;
        Ar.Suppress(false);
        suppressed = false;
    }

    // ------------------------------------------------------------------ tick

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(phase == Phase.Idle ? 2 : 0.5);
        try
        {
            switch (phase)
            {
                case Phase.Idle: TickIdle(); break;
                case Phase.Finishing: TickFinishing(); break;
                case Phase.Settle: TickSettle(); break;
                case Phase.ToBell: TickToBell(); break;
                case Phase.Retainers: TickRetainers(); break;
                case Phase.Stash: TickStash(); break;
                case Phase.Return: TickReturn(); break;
                case Phase.Resume: TickResume(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Material Stash: ICE break failed");
            Fail("error (see /xllog).");
        }
    }

    private void TickIdle()
    {
        if (!C.IceBreakEnabled) { Status = "Off."; return; }
        if (Tracker.ContentId() == 0) return;
        if (!ice.Running) { Status = ice.IsLoaded ? "Waiting for ICE to run." : "ICE isn't loaded."; return; }
        if (!Ar.IsLoaded) { Status = "AutoRetainer isn't loaded."; return; }
        var ready = ReadyCount(Ar);
        var min = Math.Max(1, C.IceBreakMinReady);
        Status = $"Watching: {ready}/{min} retainer{(min == 1 ? "" : "s")} back.";
        var gapEnds = lastEnd.AddMinutes(Math.Max(0, C.IceBreakMinGapMinutes));
        if (DateTime.UtcNow < gapEnds) { Status += $" (next break no sooner than {Math.Ceiling((gapEnds - DateTime.UtcNow).TotalMinutes)} min)"; return; }
        if (ready < min || !Ar.AnyReady) return;
        if (OthersBusy || Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) return;
        Begin($"{ready} retainer{(ready == 1 ? "" : "s")} back");
    }

    private void TickFinishing()
    {
        if (!C.IceBreakEnabled) { Stop("cancelled (turned off)"); return; }
        if (!ice.Running) { Set(Phase.Settle, "ICE stopped: getting ready to leave..."); return; }
        var wait = Math.Max(1, C.IceBreakMissionWaitMinutes);
        Status = $"ICE finishing its current mission ({ice.State})...";
        if (Elapsed > wait * 60)
        {
            Note($"ICE didn't finish its mission within {wait} min: stopping it now.");
            ice.Disable();
            Set(Phase.Settle, "ICE stopped: getting ready to leave...");
        }
    }

    private void TickSettle()
    {
        var busy = Activity.Any(f => Plugin.Condition[f]) || Plugin.ObjectTable.LocalPlayer == null;
        if (busy)
        {
            calmSince = DateTime.MinValue;
            Status = "Waiting for the character to be free...";
            // Left in the crafting log (crafting stance): closing the log ends it.
            var crafting = Plugin.Condition[ConditionFlag.Crafting] || Plugin.Condition[ConditionFlag.PreparingToCraft];
            if (crafting && !Plugin.Condition[ConditionFlag.ExecutingCraftingAction] && !craftLogTried && Elapsed > 8 && GameUi.IsOpen("RecipeNote"))
            {
                craftLogTried = true;
                SendChat("/craftinglog");
            }
            if (Elapsed > 90) { Note("the character stayed busy after ICE stopped: going back to ICE."); Set(Phase.Resume, "Starting ICE again..."); }
            return;
        }
        if (calmSince == DateTime.MinValue) { calmSince = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - calmSince).TotalSeconds < 2) return;
        if (Runner.BellListOpen) { Set(Phase.Retainers, "AutoRetainer..."); return; }
        plugin.Bell.Start();
        Set(Phase.ToBell, "Going to a summoning bell...");
    }

    private void TickToBell()
    {
        if (Runner.BellListOpen) { Set(Phase.Retainers, "Waiting for AutoRetainer..."); return; }
        Status = plugin.Bell.Status.Length > 0 ? plugin.Bell.Status : "Going to a summoning bell...";
        if ((!plugin.Bell.Busy && Elapsed > 5) || Elapsed > 300)
        {
            plugin.Bell.Stop();
            Note($"couldn't get to a summoning bell ({plugin.Bell.Status}): going back to ICE.");
            plugin.AfterAr.Release();
            Set(Phase.Return, "Going back to Cosmic Exploration...");
        }
    }

    private void TickRetainers()
    {
        var arBusy = Ar.IsBusy;
        var listOpen = Runner.BellListOpen;
        if (arBusy) { arSeen = true; arIdleSince = DateTime.MinValue; }
        else if (arIdleSince == DateTime.MinValue) arIdleSince = DateTime.UtcNow;

        if (!arSeen)
        {
            Status = "Waiting for AutoRetainer to start...";
            if (Elapsed > 20)
            {
                Note("AutoRetainer didn't start at the bell (check its \"Action on accessing retainer bell if any ventures available\" = Enable AutoRetainer).");
                Set(Phase.Stash, "Stash...");
            }
            return;
        }

        var idle = arBusy ? 0 : (DateTime.UtcNow - arIdleSince).TotalSeconds;
        var soon = (Ar.SecondsToNext(Tracker.ContentId()) is { } s && s <= 300) || GameSecondsToNext() is { } g && g <= 300;
        // Done: AutoRetainer closed the list and went quiet, or it sits at an open list with nothing ready
        // and nothing due within 5 min (with "stay 5 min" on it waits for those, and so do we).
        // AutoRetainer can keep reporting a retainer as available that it won't touch (e.g. one it's set to skip):
        // idle 30 s at the open list with nothing due soon counts as done too.
        var done = !arBusy && ((idle >= 3 && !listOpen) || (idle >= 5 && listOpen && !Ar.AnyReady && !soon) || (idle >= 30 && listOpen && !soon));
        Status = arBusy ? "AutoRetainer is working..." : listOpen ? "AutoRetainer waiting at the bell for a retainer due back soon..." : "AutoRetainer finishing...";
        if (Elapsed > 25 * 60) { Note("AutoRetainer took over 25 min: moving on."); done = true; }
        if (!done) return;
        Set(Phase.Stash, "Stash...");
    }

    private void TickStash()
    {
        if (!stashStarted)
        {
            if (Ar.IsBusy && Elapsed < 15) return; // let AutoRetainer wind down
            if (StillAtBell()) return;
            if (!C.StashAfterAutoRetainer || !plugin.AfterAr.Pending)
            {
                plugin.AfterAr.Release();
                Set(Phase.Return, "Going back to Cosmic Exploration...");
                return;
            }
            Ar.Suppress(true); // reopening the bell must not set AutoRetainer off again
            suppressed = true;
            if (!plugin.AfterAr.StashNow())
            {
                Unsuppress();
                plugin.AfterAr.Release();
                Set(Phase.Return, "Going back to Cosmic Exploration...");
                return;
            }
            stashStarted = true;
            Status = "Stashing this cycle's venture returns...";
            return;
        }
        if (plugin.AfterAr.Busy)
        {
            Status = plugin.AfterAr.Status.Length > 0 ? plugin.AfterAr.Status : "Stashing this cycle's venture returns...";
            return;
        }
        if (StillAtBell()) { closedSince = DateTime.MinValue; return; }
        if (closedSince == DateTime.MinValue) { closedSince = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - closedSince).TotalSeconds < 2) return; // let the bell close fully before releasing AutoRetainer
        Unsuppress();
        plugin.AfterAr.Release();
        Set(Phase.Return, "Going back to Cosmic Exploration...");
    }

    private void TickReturn()
    {
        var moving = lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
                     || Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] || Plugin.ObjectTable.LocalPlayer == null;
        if (moving) { arrived = DateTime.MinValue; Status = "Travelling back to Cosmic Exploration..."; return; }
        if (CosmoData.InZone)
        {
            if (arrived == DateTime.MinValue) { arrived = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - arrived).TotalSeconds < 4) return; // let the zone settle
            Set(Phase.Resume, "Starting ICE again...");
            return;
        }
        if (!travelAsked)
        {
            if (!lifestream.IsLoaded) { Fail("Lifestream isn't loaded, so I can't take you back to Cosmic Exploration. ICE is off."); return; }
            if (StillAtBell()) return;
            if (!lifestream.Execute("cosmic")) { Fail("Lifestream didn't take /li cosmic. ICE is off."); return; }
            travelAsked = true;
            Status = "Lifestream: going back to Cosmic Exploration...";
            return;
        }
        if (Elapsed > 300) Fail("didn't get back to Cosmic Exploration within 5 min. ICE is off.");
    }

    private void TickResume()
    {
        if (ice.Running)
        {
            plugin.AfterAr.Release();
            phase = Phase.Idle;
            lastEnd = DateTime.UtcNow;
            Status = "Back to ICE.";
            Note("done, ICE is running again.");
            return;
        }
        if (enableTries == 0 || (Elapsed > 15 && enableTries < 2))
        {
            ice.StopAfterCurrent(false);
            if (!ice.Enable()) { Fail("ICE didn't answer. ICE is off."); return; }
            enableTries++;
            phaseStart = DateTime.UtcNow;
            Status = "Starting ICE again...";
            return;
        }
        if (Elapsed > 15) Fail("ICE didn't start again (see ICE's window). ICE is off.");
    }

    // ------------------------------------------------------------------ leaving the bell

    private static readonly string[] RetainerWindows = { "RetainerSellList", "RetainerSell", "InventoryRetainerLarge", "InventoryRetainer" };

    /// <summary>
    /// Close whatever is still open at the bell, one window per call: a retainer's inventory or market list,
    /// its dialogue, its menu (Quit), then the retainer list. True while anything is still open.
    /// A run that stopped part-way (or AutoRetainer) can leave a retainer open, and the list's own close does
    /// nothing while one is: that's what used to hold the break at the bell. After a minute, gives up (with a dump).
    /// </summary>
    private bool StillAtBell()
    {
        var open = Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || Runner.BellListOpen
                   || RetainerWindows.Any(GameUi.IsOpen);
        if (!open) { leaveStart = DateTime.MinValue; return false; }
        if (leaveStart == DateTime.MinValue) leaveStart = DateTime.UtcNow;
        if ((DateTime.UtcNow - leaveStart).TotalSeconds > 60)
        {
            GameUi.Dump("RetainerList", "ICE break: stuck leaving the bell. Visible: " + string.Join(", ", GameUi.VisibleAddons().OrderBy(n => n)));
            Fail("couldn't close the retainer windows within a minute (dump saved). ICE is off.");
            return true;
        }
        Status = "Leaving the summoning bell...";
        if (GameUi.IsOpen("ContextMenu")) { GameUi.Close("ContextMenu"); return true; }
        foreach (var name in RetainerWindows)
        {
            var w = GameUi.Get(name);
            if (w != null) { w->Close(true); return true; }
        }
        var talk = GameUi.Get("Talk");
        if (talk != null) { GameUi.ClickTalk(talk); return true; }
        var ss = GameUi.Get("SelectString");
        if (ss != null)
        {
            var quit = GameUi.MenuEntries(ss, false).FindIndex(r => r.StartsWith("Quit", StringComparison.OrdinalIgnoreCase));
            if (quit >= 0) { ss->FireCallbackInt(quit); return true; }
        }
        if (Runner.BellListOpen) { GameUi.Close("RetainerList"); return true; }
        return true; // still "at the bell" for a moment while the game lets go
    }

    /// <summary>Seconds until the next retainer still out is back, from the game's own list (null = none out / not read).</summary>
    private static long? GameSecondsToNext()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? best = null;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0 || r->VentureId == 0 || r->VentureComplete <= now) continue;
            var left = (long)r->VentureComplete - now;
            if (best == null || left < best) best = left;
        }
        return best;
    }

    /// <summary>Send a line through the chat box, like typing it.</summary>
    private static void SendChat(string line)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        var str = Utf8String.FromString(line);
        try { ui->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }
}
