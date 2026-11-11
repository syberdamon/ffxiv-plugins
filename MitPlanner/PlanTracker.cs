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
    string? SendTo,         // OT Sent Mits: name of the player it'll be cast on; healers: "on Tank", "at arena center"
    bool Placed = false);   // Earthly Star placed; SecondsToPress now counts to the detonation

/// <summary>
/// Watches the plan against the running timeline: which cooldowns are coming due, whether you
/// pressed them, and (only when allowed) presses them for you.
/// </summary>
public sealed class PlanTracker : IDisposable
{
    private sealed class State
    {
        public bool Started;
        public DateTime StartedAt;
        public bool Placed;          // Earthly Star is on the field
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
    private readonly ArenaCenter arena;
    private readonly Dictionary<Guid, State> states = new();
    private readonly List<Prompt> active = new();

    public IReadOnlyList<Prompt> Active => active;

    public PlanTracker(Configuration config, TimelineRunner runner, Targeting targeting, ArenaCenter arena)
    {
        this.arena = arena;
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

            var ability = ActionCatalog.Find(entry.ActionId);
            var isGcd = ability?.IsGcd ?? Timing.IsGcd(entry.ActionId);
            var matures = HealerKits.MatureSeconds(entry.ActionId) > 0;
            // Press early enough to finish LeadSeconds before the hit (cast time, Eukrasia first,
            // GCD room and Earthly Star's maturing included: see Timing).
            var pressAt = ev.Time - Timing.PressBefore(entry);
            var finishAt = ev.Time - entry.LeadSeconds;
            var showFrom = pressAt - config.CalloutHeadsUp;
            if (now < showFrom || now > ev.Time + 1f) continue;

            if (!states.TryGetValue(entry.Id, out var st)) { st = new State(); states[entry.Id] = st; }
            var status = ActionState.Get(entry.ActionId);

            // Targeting condition: decided live, so it follows tank swaps and late markers.
            TargetCheck? target = null;
            var applies = true;
            if (entry.Condition != TargetCondition.Always || entry.Placement == EntryPlacement.OnTank)
            {
                var t = targeting.Check(runner.Fight!, ev);
                target = t;
                if (entry.Condition != TargetCondition.Always)
                    applies = t.Source != TargetSource.Unknown && entry.Condition switch
                    {
                        TargetCondition.IfTargeted => t.IsMe,
                        TargetCondition.IfNotTargeted => !t.IsMe,
                        TargetCondition.IfTankTargeted => Placement.IsTank(t.TargetObjectId),
                        _ => true,
                    };
            }
            var targetUnknown = entry.Condition != TargetCondition.Always && target is { Source: TargetSource.Unknown };

            // OT Sent Mits: cast on whoever the mechanic targets; fall back to the co-tank.
            ulong sendTarget = 0;
            string? sendName = null;
            if (entry.SendToTarget && ability?.CanTargetAlly == true)
            {
                var me = Plugin.ObjectTable.LocalPlayer;
                sendTarget = target is { Source: not TargetSource.Unknown, TargetObjectId: not 0 } t2 && !t2.IsMe
                    ? t2.TargetObjectId
                    : Targeting.CoTankObjectId();
                sendName = sendTarget != 0 ? Plugin.ObjectTable.SearchById(sendTarget)?.Name.TextValue : null;
                if (sendTarget == 0 || sendTarget == me?.GameObjectId) applies = false; // nobody to send it to
            }

            // Placement (healers): on the tank / on you / on the boss / at the arena center.
            var place = entry.SendToTarget ? PressTarget.Default : Placement.Resolve(entry, ability, target, runner, arena);
            if (place.Label != null) sendName = place.Label;

            if (!st.Started)
            {
                st.Started = true;
                st.StartedAt = DateTime.UtcNow;
                st.CooldownAtStart = status.OnCooldown;
                st.ChargesAtStart = status.Charges;
                if (config.ChatCallouts)
                    Plugin.ChatGui.Print($"[MitPlanner] {ability?.Name ?? "Cooldown"} for {ev.Label} in {pressAt - now:0}s");
            }

            if (!st.Done) CheckDone(entry, st, status, isGcd, matures);

            // Auto-use: only with both switches on, from the press time until the mechanic lands, never in preview.
            // Press, then press again every 0.25s until it actually goes off. The game queues
            // off-GCD abilities during animation lock (and a GCD in the last part of the GCD),
            // so a repeat press while queued is harmless. GCD spells only when allowed per entry.
            // Never auto-press when the condition says it isn't for you, or we can't tell yet.
            if (!st.Done && applies && config.AllowAutoUse && entry.AutoUse && !runner.Preview &&
                (!isGcd || entry.AllowGcdAutoUse) && !place.Unresolved &&
                now >= pressAt && now <= ev.Time &&
                (DateTime.UtcNow - st.LastAutoPress).TotalSeconds >= AutoRetrySeconds)
            {
                st.LastAutoPress = DateTime.UtcNow; // throttles retries even when the game refuses a press
                if (Press(entry, st, status, isGcd, matures, now >= finishAt, sendTarget, place))
                {
                    st.AutoPresses++;
                    if (st.AutoPresses > 1)
                        Plugin.Log.Debug($"[MitPlanner] Auto-use retry #{st.AutoPresses - 1} for {entry.EventKey} / action {entry.ActionId}");
                }
            }

            // Keep a finished prompt on screen briefly as confirmation.
            if (st.Done && (DateTime.UtcNow - st.DoneAt).TotalSeconds > 1.5) continue;

            // A GCD "on cooldown" is just the GCD rolling; a placed Earthly Star is waiting to detonate.
            var notReady = status.OnCooldown && !st.Done && !isGcd && !st.Placed;
            active.Add(new Prompt(entry, ev, ability,
                (st.Placed ? finishAt : pressAt) - now, ev.Time - now, st.Done, notReady, st.AutoPresses,
                !applies && !targetUnknown && !st.Done, target, sendName, st.Placed));
        }

        active.Sort((a, b) => a.SecondsToPress.CompareTo(b.SecondsToPress));
    }

    /// <summary>Did it go off? Off-GCDs: went on cooldown (or spent a charge) after the callout began.
    /// GCDs: the GCD was last started by it. Earthly Star: placed, then detonated (or expired).</summary>
    private static void CheckDone(PlanEntry entry, State st, AbilityStatus status, bool isGcd, bool matures)
    {
        var done = false;
        if (matures)
        {
            // Star on the field: the button has turned into Stellar Detonation.
            var starOut = ActionState.Adjusted(entry.ActionId) != entry.ActionId;
            if (starOut) st.Placed = true;
            else if (st.Placed) done = true; // detonated (or it went off by itself)
        }
        else if (isGcd)
        {
            done = ActionState.GcdUsedWithin(entry.ActionId, (float)(DateTime.UtcNow - st.StartedAt).TotalSeconds);
        }
        else
        {
            // Once it's been seen ready, any later cooldown start counts as "pressed".
            if (st.CooldownAtStart && !status.OnCooldown)
            {
                st.CooldownAtStart = false;
                st.ChargesAtStart = status.Charges;
            }
            done = (!st.CooldownAtStart && status.OnCooldown) ||
                   (status.MaxCharges > 1 && status.Charges < st.ChargesAtStart);
        }
        if (!done) return;
        st.Done = true;
        st.DoneAt = DateTime.UtcNow;
    }

    /// <summary>One auto-press. Returns false when there was nothing to press yet.</summary>
    private static bool Press(PlanEntry entry, State st, AbilityStatus status, bool isGcd, bool matures, bool finishTime,
        ulong sendTarget, PressTarget place)
    {
        // Earthly Star: place it (at the press time), then press it again at the finish time to detonate.
        if (matures)
        {
            if (st.Placed) return finishTime && ActionState.Use(entry.ActionId);
            if (status.OnCooldown) return false;
            return place.Location is { } at ? ActionState.UseAt(entry.ActionId, at) : ActionState.Use(entry.ActionId);
        }

        if (!isGcd && status.OnCooldown) return false;

        // Sage: Eukrasia first; once it's up, Diagnosis/Prognosis is the Eukrasian version.
        var pressId = HealerKits.PressId(entry.ActionId);
        if (HealerKits.Prereq(entry.ActionId) is var prereq and not 0 && ActionState.Adjusted(pressId) == pressId)
            return ActionState.Use(prereq);

        if (place.Location is { } loc) return ActionState.UseAt(pressId, loc);
        if (place.ObjectId != 0) return ActionState.Use(pressId, place.ObjectId);
        if (sendTarget != 0) return ActionState.Use(pressId, sendTarget);
        return ActionState.Use(pressId);
    }
}
