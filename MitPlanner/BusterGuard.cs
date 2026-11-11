using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MitPlanner;

/// <summary>
/// Safety net for tank busters the plan doesn't cover (often head-marker busters that haven't
/// been learned yet). When cactbot's tank-buster marker lands on you, or its shared-buster
/// marker lands on a tank, and nothing planned or just used covers it, this picks the best mits
/// that are off cooldown right now (Big, else Rampart + Secondary; plus Short), calls them out,
/// and presses them if auto-use is allowed.
/// </summary>
public sealed class BusterGuard : IDisposable
{
    private sealed record Press(uint ActionId, DateTime NotBefore, DateTime GiveUpAt, ulong TargetId = 0)
    {
        public DateTime LastPress { get; set; } = DateTime.MinValue;
    }

    /// <summary>A mit used this recently is assumed to still be up for the buster.</summary>
    private const double RecentlyUsedSeconds = 10;

    private readonly Configuration config;
    private readonly TimelineRunner runner;
    private readonly Targeting targeting;
    private readonly PlanTracker tracker;
    private readonly List<Press> queue = new();
    private DateTime lastReaction = DateTime.MinValue;

    /// <summary>Text for the callout window while a reaction is fresh.</summary>
    public string? Callout { get; private set; }
    public DateTime CalloutUntil { get; private set; }
    public bool ShowCallout => Callout != null && DateTime.UtcNow < CalloutUntil;

    public BusterGuard(Configuration config, TimelineRunner runner, Targeting targeting, PlanTracker tracker)
    {
        this.config = config;
        this.runner = runner;
        this.targeting = targeting;
        this.tracker = tracker;
        targeting.MarkerPlaced += OnMarker;
        runner.PullStarted += queue.Clear;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        targeting.MarkerPlaced -= OnMarker;
        runner.PullStarted -= queue.Clear;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnMarker(uint entityId, uint markerId)
    {
        try
        {
            var fight = runner.Fight;
            if (fight?.MarkerKinds == null || !runner.Running || runner.Preview) return;
            if (!config.ReactToBusterMarkers || !config.PlanEnabled) return;
            if (!fight.MarkerKinds.TryGetValue(markerId, out var kind) || !kind.IsBuster()) return;

            var me = Plugin.ObjectTable.LocalPlayer;
            if (me != null && HealerKits.For(me.ClassJob.RowId) is { } healer)
            {
                OnHealerMarker(healer, entityId, kind);
                return;
            }
            var kit = me != null ? TankKits.For(me.ClassJob.RowId) : null;
            if (me == null || kit == null) return;

            var onMe = entityId == me.EntityId;
            var shared = kind == MechanicType.SharedTankBuster;
            if (!onMe && !(shared && IsPartyTank(entityId))) return;
            if ((DateTime.UtcNow - lastReaction).TotalSeconds < 6) return; // both tanks' markers arrive together
            lastReaction = DateTime.UtcNow;

            var catalog = ActionCatalog.ForCurrentJob();
            if (Covered(kit, catalog)) return;

            bool Ready(uint id) => id != 0 && catalog.Any(c => c.ActionId == id) && !ActionState.Get(id).OnCooldown;
            var picks = new List<uint>();
            if (Ready(kit.Big)) picks.Add(kit.Big);
            else
            {
                if (Ready(kit.Rampart)) picks.Add(kit.Rampart);
                if (Ready(kit.Secondary) && kit.Secondary != kit.Short) picks.Add(kit.Secondary);
            }
            if (Ready(kit.Short)) picks.Add(kit.Short);

            var what = shared ? "Shared tank buster" : "Tank buster on you";
            if (picks.Count == 0)
            {
                Show($"{what}: no mits off cooldown!");
                return;
            }

            var names = string.Join(" + ", picks.Select(id => catalog.FirstOrDefault(c => c.ActionId == id)?.Name ?? id.ToString()));
            var auto = config.AllowAutoUse;
            Show($"{what}: {(auto ? "using" : "use")} {names}");
            Plugin.ChatGui.Print($"[MitPlanner] {what}, not covered by the plan: {(auto ? "using" : "use")} {names}.");

            if (!auto) return;
            var now = DateTime.UtcNow;
            for (var i = 0; i < picks.Count; i++)
            {
                var at = now.AddSeconds(i * 1.25); // same spacing as planned presses (animation lock)
                queue.Add(new Press(picks[i], at, at.AddSeconds(4)));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Buster guard failed");
        }
    }

    /// <summary>
    /// Healers: a tank-buster marker (any kind) landed on a tank and nothing planned covers it:
    /// the first off-GCD tank shield that's off cooldown, cast on that tank. One, not all: the
    /// tank has their own mits too. GCD shields are never pressed here.
    /// </summary>
    private void OnHealerMarker(HealerKit kit, uint entityId, MechanicType kind)
    {
        if (!IsPartyTank(entityId)) return;
        if ((DateTime.UtcNow - lastReaction).TotalSeconds < 6) return;
        lastReaction = DateTime.UtcNow;
        if (Plugin.ObjectTable.SearchByEntityId(entityId) is not { } tank) return;

        var catalog = ActionCatalog.ForCurrentJob();
        var shields = kit.Buster.Where(id => catalog.Any(c => c.ActionId == id)).ToList();
        // Covered: one was used in the last few seconds, or the plan has one coming in the next 10 s.
        foreach (var id in shields)
        {
            var recast = catalog.First(c => c.ActionId == id).RecastSeconds;
            var st = ActionState.Get(id);
            if (st.OnCooldown && recast > 0 && recast - st.Remaining <= RecentlyUsedSeconds) return;
        }
        if (tracker.Active.Any(p => shields.Contains(p.Entry.ActionId) && !p.Skipped && !p.NotReady && !p.Done &&
                                    p.SecondsToHit >= -0.5f && p.SecondsToHit <= 10)) return;

        var pick = shields.FirstOrDefault(id => !ActionState.Get(id).OnCooldown);
        var who = tank.Name.TextValue;
        var what = kind == MechanicType.SharedTankBuster ? "Shared tank buster" : $"Tank buster on {who}";
        if (pick == 0) { Show($"{what}: no tank shield off cooldown!"); return; }

        var name = catalog.First(c => c.ActionId == pick).Name;
        var auto = config.AllowAutoUse;
        Show($"{what}: {(auto ? "using" : "use")} {name} on {who}");
        Plugin.ChatGui.Print($"[MitPlanner] {what}, not covered by the plan: {(auto ? "using" : "use")} {name} on {who}.");
        if (auto) queue.Add(new Press(pick, DateTime.UtcNow, DateTime.UtcNow.AddSeconds(4), tank.GameObjectId));
    }

    /// <summary>
    /// Already handled if a heavy mit was used in the last few seconds, or the plan has one
    /// coming for a mechanic in the next 10 s that applies to you.
    /// </summary>
    private bool Covered(TankKit kit, IReadOnlyList<CatalogEntry> catalog)
    {
        var heavy = new[] { kit.Big, kit.Rampart, kit.Secondary, kit.Invuln }.Where(id => id != 0).ToHashSet();
        foreach (var id in heavy)
        {
            var recast = catalog.FirstOrDefault(c => c.ActionId == id)?.RecastSeconds ?? 0;
            var st = ActionState.Get(id);
            if (st.OnCooldown && recast > 0 && recast - st.Remaining <= RecentlyUsedSeconds) return true;
        }
        return tracker.Active.Any(p => heavy.Contains(p.Entry.ActionId) && !p.Skipped && !p.NotReady &&
                                       p.SecondsToHit >= -0.5f && p.SecondsToHit <= 10 &&
                                       p.Entry.Condition != TargetCondition.IfNotTargeted);
    }

    private static bool IsPartyTank(uint entityId) =>
        Plugin.PartyList.Any(m => m.EntityId == entityId && TankKits.For(m.ClassJob.RowId) != null);

    private void Show(string text)
    {
        Callout = text;
        CalloutUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void OnUpdate(IFramework framework)
    {
        if (queue.Count == 0) return;
        var now = DateTime.UtcNow;
        var press = queue[0];
        if (now < press.NotBefore) return;
        if (ActionState.Get(press.ActionId).OnCooldown || now > press.GiveUpAt) { queue.RemoveAt(0); return; }
        if ((now - press.LastPress).TotalSeconds < PlanTracker.AutoRetrySeconds) return;
        press.LastPress = now;
        if (press.TargetId != 0) ActionState.Use(press.ActionId, press.TargetId);
        else ActionState.Use(press.ActionId);
    }
}
