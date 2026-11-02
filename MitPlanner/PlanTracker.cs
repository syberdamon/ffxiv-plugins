using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace MitPlanner;

/// <summary>A cooldown that should be pressed soon, for the callout window.</summary>
public sealed record Prompt(
    PlanEntry Entry,
    TimelineEvent Event,
    CatalogEntry? Ability,
    float SecondsToPress,   // negative = press time has passed
    float SecondsToHit,
    bool Done,
    bool NotReady,          // still on cooldown, so it can't be pressed yet
    int AutoPresses,        // how many times auto-use has pressed it this pull
    bool Skipped,           // condition says this one isn't for you (e.g. buster on the other tank)
    TargetCheck? Target,    // who the mechanic targets, when the entry has a condition
    string? SendTo);        // OT Sent Mits: name of the player it'll be cast on

/// <summary>
/// Watches the plan against the running timeline: which cooldowns are coming due, whether you
/// pressed them, and (only when allowed) presses them for you.
/// </summary>
public sealed class PlanTracker : IDisposable
{
    private sealed class State
    {
        public bool Started;
        public bool CooldownAtStart;
        public int ChargesAtStart;
        public bool Done;
        public DateTime DoneAt;
        public DateTime LastAutoPress = DateTime.MinValue;
        public int AutoPresses;
    }

    /// <summary>Re-press an auto-use cooldown if it hasn't gone off within this long.</summary>
    public const double AutoRetrySeconds = 0.25;

    private readonly Configuration config;
    private readonly TimelineRunner runner;
    private readonly Targeting targeting;
    private readonly Dictionary<Guid, State> states = new();
    private readonly List<Prompt> active = new();

    public IReadOnlyList<Prompt> Active => active;

    public PlanTracker(Configuration config, TimelineRunner runner, Targeting targeting)
    {
        this.config = config;
        this.runner = runner;
        this.targeting = targeting;
        runner.PullStarted += states.Clear;
        runner.JumpedBack += OnJumpedBack;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        runner.PullStarted -= states.Clear;
        runner.JumpedBack -= OnJumpedBack;
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>The fight looped back: re-arm every planned cooldown for mechanics that will repeat.</summary>
    private void OnJumpedBack(float newTime)
    {
        var plan = CurrentPlan();
        if (plan == null) return;
        foreach (var entry in plan)
            if (runner.FindByKey(entry.EventKey) is { } ev && ev.Time >= newTime)
                states.Remove(entry.Id);
    }

    public List<PlanEntry>? CurrentPlan()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || runner.Fight == null) return null;
        return config.PlanFor(runner.Fight.TerritoryId, player.ClassJob.RowId);
    }

    private void OnUpdate(IFramework framework)
    {
        active.Clear();
        if (!runner.Running || runner.Fight == null || !config.PlanEnabled) return;

        var plan = CurrentPlan();
        if (plan == null || plan.Count == 0) return;

        var now = runner.Now;
        foreach (var entry in plan)
        {
            var ev = runner.FindByKey(entry.EventKey);
            if (ev == null) continue;

            var pressAt = ev.Time - entry.LeadSeconds;
            var showFrom = pressAt - config.CalloutHeadsUp;
            if (now < showFrom || now > ev.Time + 1f) continue;

            if (!states.TryGetValue(entry.Id, out var st)) { st = new State(); states[entry.Id] = st; }
            var status = ActionState.Get(entry.ActionId);

            // Targeting condition: decided live, so it follows tank swaps and late markers.
            TargetCheck? target = null;
            var applies = true;
            if (entry.Condition != TargetCondition.Always)
            {
                var t = targeting.Check(runner.Fight!, ev);
                target = t;
                applies = t.Source != TargetSource.Unknown &&
                          (entry.Condition == TargetCondition.IfTargeted ? t.IsMe : !t.IsMe);
            }
            var targetUnknown = target is { Source: TargetSource.Unknown };

            // OT Sent Mits: cast on whoever the mechanic targets; fall back to the co-tank.
            ulong sendTarget = 0;
            string? sendName = null;
            if (entry.SendToTarget && ActionCatalog.Find(entry.ActionId)?.CanTargetAlly == true)
            {
                var me = Plugin.ObjectTable.LocalPlayer;
                sendTarget = target is { Source: not TargetSource.Unknown, TargetObjectId: not 0 } t2 && !t2.IsMe
                    ? t2.TargetObjectId
                    : Targeting.CoTankObjectId();
                sendName = sendTarget != 0 ? Plugin.ObjectTable.SearchById(sendTarget)?.Name.TextValue : null;
                if (sendTarget == 0 || sendTarget == me?.GameObjectId) applies = false; // nobody to send it to
            }

            if (!st.Started)
            {
                st.Started = true;
                st.CooldownAtStart = status.OnCooldown;
                st.ChargesAtStart = status.Charges;
                if (config.ChatCallouts)
                    Plugin.ChatGui.Print($"[MitPlanner] {ActionCatalog.Find(entry.ActionId)?.Name ?? "Cooldown"} for {ev.Label} in {pressAt - now:0}s");
            }

            // Once it's been seen ready, any later cooldown start counts as "pressed".
            if (st.CooldownAtStart && !status.OnCooldown)
            {
                st.CooldownAtStart = false;
                st.ChargesAtStart = status.Charges;
            }

            // Pressed? (went on cooldown, or a charge was spent, after the callout began)
            if (!st.Done && ((!st.CooldownAtStart && status.OnCooldown) ||
                             (status.MaxCharges > 1 && status.Charges < st.ChargesAtStart)))
            {
                st.Done = true;
                st.DoneAt = DateTime.UtcNow;
            }

            // Auto-use: only with both switches on, from the press time until the mechanic lands, never in preview.
            // Press, then press again every 0.25s until the cooldown actually starts. The game queues
            // off-GCD abilities during animation lock, so a repeat press while queued is harmless.
            // Never auto-press when the condition says it isn't for you, or we can't tell yet.
            if (!st.Done && applies && config.AllowAutoUse && entry.AutoUse && !runner.Preview &&
                now >= pressAt && now <= ev.Time && !status.OnCooldown &&
                (DateTime.UtcNow - st.LastAutoPress).TotalSeconds >= AutoRetrySeconds)
            {
                st.LastAutoPress = DateTime.UtcNow;
                st.AutoPresses++;
                if (sendTarget != 0) ActionState.Use(entry.ActionId, sendTarget);
                else ActionState.Use(entry.ActionId);
                if (st.AutoPresses > 1)
                    Plugin.Log.Debug($"[MitPlanner] Auto-use retry #{st.AutoPresses - 1} for {entry.EventKey} / action {entry.ActionId}");
            }

            // Keep a finished prompt on screen briefly as confirmation.
            if (st.Done && (DateTime.UtcNow - st.DoneAt).TotalSeconds > 1.5) continue;

            active.Add(new Prompt(entry, ev, ActionCatalog.Find(entry.ActionId),
                pressAt - now, ev.Time - now, st.Done, status.OnCooldown && !st.Done, st.AutoPresses,
                !applies && !targetUnknown && !st.Done, target, sendName));
        }

        active.Sort((a, b) => a.SecondsToPress.CompareTo(b.SecondsToPress));
    }
}
