using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MitPlanner;

/// <summary>
/// Where "Arena center" is for ground-targeted heals, best source first:
///   1. BossMod Reborn's arena center (IPC "BossMod.Hints.ArenaCenter"), live, so arenas that change
///      shape mid-fight are followed. Only trusted once it's shown it isn't just following you: with
///      no proper boss module (hunts, FATEs, some dungeon bosses) BossMod reports a point near your
///      character instead. A real center has to be seen 7+ yalms from you once in the pull.
///   2. A center you saved for this zone ("Set arena center here").
///   3. Where the boss was when the plugin first saw it this pull. [Assumption] most bosses start
///      mid-arena; for the ones that don't, save a center.
/// BossMod gives X/Z only; the height is yours. [Assumption] the game puts the AoE on the floor.
/// </summary>
public sealed class ArenaCenter : IDisposable
{
    /// <summary>Further than this from you = BossMod's center can't be the "follow the player" fallback
    /// (that one is your position rounded to 5 y, re-centered when you move 5 y).</summary>
    private const float ProvenDistance = 7f;

    private readonly Configuration config;
    private readonly TimelineRunner runner;
    private ICallGateSubscriber<Vector2>? bmCenter;
    private ICallGateSubscriber<bool>? bmActive;
    private Vector3? bossStart;
    private bool bmTrusted;
    private DateTime nextPoll = DateTime.MinValue;
    private Vector2? lastBm;

    public string Source { get; private set; } = "none";

    public ArenaCenter(Configuration config, TimelineRunner runner)
    {
        this.config = config;
        this.runner = runner;
        runner.PullStarted += Reset;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        runner.PullStarted -= Reset;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void Reset()
    {
        bossStart = null;
        bmTrusted = false;
        lastBm = null;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!runner.Running || runner.Preview) return;
        if (bossStart == null && runner.BossEntityId != 0 && Plugin.ObjectTable.SearchByEntityId(runner.BossEntityId) is { } boss)
            bossStart = boss.Position;

        // Poll BossMod a few times a second; it's cheap but there's no need for every frame.
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(0.25);
        lastBm = ReadBossMod();
        if (!bmTrusted && lastBm is { } c && Plugin.ObjectTable.LocalPlayer is { } me &&
            Vector2.Distance(c, new Vector2(me.Position.X, me.Position.Z)) > ProvenDistance)
            bmTrusted = true;
    }

    private Vector2? ReadBossMod()
    {
        try
        {
            bmActive ??= Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
            bmCenter ??= Plugin.PluginInterface.GetIpcSubscriber<Vector2>("BossMod.Hints.ArenaCenter");
            if (!bmActive.InvokeFunc()) return null;
            var c = bmCenter.InvokeFunc();
            return c == Vector2.Zero ? null : c;
        }
        catch
        {
            return null; // BossMod Reborn not installed/loaded: fine, use the other sources
        }
    }

    /// <summary>The arena center for this pull, or null if nothing knows it.</summary>
    public Vector3? Get()
    {
        var y = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
        if (bmTrusted && lastBm is { } c) { Source = "BossMod Reborn"; return new Vector3(c.X, y, c.Y); }
        if (runner.Fight is { } f && config.ArenaCenters.TryGetValue(f.TerritoryId, out var saved)) { Source = "saved"; return saved.ToVector(); }
        if (bossStart is { } b) { Source = "boss start position"; return b; }
        Source = "unknown";
        return null;
    }

    /// <summary>For the Status tab: what each source says right now.</summary>
    public string Describe()
    {
        var bm = lastBm is { } c ? $"{c.X:0.0}, {c.Y:0.0} ({(bmTrusted ? "trusted" : "not proven yet: may be following you")})" : "no answer";
        var saved = runner.Fight is { } f && config.ArenaCenters.TryGetValue(f.TerritoryId, out var s) ? $"{s.X:0.0}, {s.Z:0.0}" : "none";
        var boss = bossStart is { } b ? $"{b.X:0.0}, {b.Z:0.0}" : "not seen";
        return $"BossMod: {bm}; saved: {saved}; boss start: {boss}";
    }
}
