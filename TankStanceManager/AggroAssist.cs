using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TankStanceManager;

/// <summary>
/// Trash pulls: find enemies attacking a party member who isn't a tank (someone pulled off you, or a
/// mob your AoEs missed), target the worst one so your next hits land on it, and Provoke it if
/// Provoke is ready. "Who has aggro" = the enemy's current target. A mob only counts once it has
/// been on that player for a short delay, and never while it's casting (a cast can target someone
/// other than its aggro holder). Mobs on the other tank are left alone.
/// </summary>
public sealed unsafe class AggroAssist : IDisposable
{
    private const uint ProvokeId = 7533;
    private const float ProvokeRange = 25f;
    private const float BossHpFactor = 8f;   // [Assumption] same rule as Mit Planner: 8x your max HP = boss

    private static readonly HashSet<uint> TankJobs = new() { 1, 3, 19, 21, 32, 37 };

    private readonly Configuration config;
    private readonly Dictionary<ulong, DateTime> looseSince = new();
    private DateTime lastTargetSwap = DateTime.MinValue;
    private DateTime lastProvoke = DateTime.MinValue;

    public string StatusText { get; private set; } = "Off";
    public string LastAction { get; private set; } = "None";

    public AggroAssist(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        try { Tick(); }
        catch (Exception ex)
        {
            StatusText = "Error (see /xllog)";
            Plugin.Log.Error(ex, "AggroAssist tick failed");
        }
    }

    private void Tick()
    {
        if (!config.Enabled || !(config.AutoTargetLooseMobs || config.ProvokeLooseMobs)) { StatusText = "Off"; looseSince.Clear(); return; }
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || !TankJobs.Contains(me.ClassJob.RowId)) { StatusText = "Not on a tank job"; return; }
        if (!Plugin.Condition[ConditionFlag.InCombat]) { StatusText = "Out of combat"; looseSince.Clear(); return; }
        if (config.DutiesOnly && !Plugin.Condition[ConditionFlag.BoundByDuty] && !Plugin.Condition[ConditionFlag.BoundByDuty56])
        { StatusText = "Not in a duty"; return; }

        // Party members who aren't tanks (you and any co-tank are fine holders).
        var party = new Dictionary<ulong, IPlayerCharacter>();
        foreach (var m in Plugin.PartyList)
            if (m.GameObject is IPlayerCharacter pc && pc.GameObjectId != me.GameObjectId) party[pc.GameObjectId] = pc;
        var squishies = party.Values.Where(p => !(config.IgnoreMobsOnCoTank && TankJobs.Contains(p.ClassJob.RowId)))
                             .Select(p => p.GameObjectId).ToHashSet();

        var enemies = Plugin.ObjectTable.OfType<IBattleNpc>()
            .Where(n => (n.StatusFlags & StatusFlags.Hostile) != 0 && (n.StatusFlags & StatusFlags.InCombat) != 0 &&
                        n.CurrentHp > 0 && n.IsTargetable && Vector3.Distance(n.Position, me.Position) <= 40)
            .ToList();
        if (config.TrashOnly && enemies.Any(n => n.MaxHp >= BossHpFactor * me.MaxHp))
        { StatusText = "Boss fight: standing by"; looseSince.Clear(); return; }

        var now = DateTime.UtcNow;
        var loose = new List<IBattleNpc>();
        var seen = new HashSet<ulong>();
        foreach (var n in enemies)
        {
            seen.Add(n.GameObjectId);
            var onSquishy = squishies.Contains(n.TargetObjectId) && !n.IsCasting;
            if (!onSquishy) { looseSince.Remove(n.GameObjectId); continue; }
            if (!looseSince.TryGetValue(n.GameObjectId, out var since)) looseSince[n.GameObjectId] = since = now;
            if ((now - since).TotalMilliseconds >= config.LooseMobDelayMs) loose.Add(n);
        }
        foreach (var id in looseSince.Keys.Where(k => !seen.Contains(k)).ToList()) looseSince.Remove(id);

        if (loose.Count == 0) { StatusText = $"Holding: {enemies.Count(n => n.TargetObjectId == me.GameObjectId)}/{enemies.Count} on you"; return; }

        // Worst first: the one on the lowest-HP player, then the closest.
        var pick = loose
            .OrderBy(n => party.TryGetValue(n.TargetObjectId, out var p) && p.MaxHp > 0 ? (float)p.CurrentHp / p.MaxHp : 1f)
            .ThenBy(n => Vector3.Distance(n.Position, me.Position))
            .First();
        var victim = party.TryGetValue(pick.TargetObjectId, out var v) ? v.ClassJob.Value.Abbreviation.ExtractText() : "?";
        StatusText = $"{loose.Count} loose: {pick.Name} on {victim}";

        if (config.AutoTargetLooseMobs && Plugin.TargetManager.Target?.GameObjectId != pick.GameObjectId &&
            (now - lastTargetSwap).TotalMilliseconds >= 1500)
        {
            Plugin.TargetManager.Target = pick;
            lastTargetSwap = now;
            Note($"target {pick.Name} (on {victim})");
        }

        if (config.ProvokeLooseMobs && (now - lastProvoke).TotalSeconds >= 2 &&
            Vector3.Distance(pick.Position, me.Position) <= ProvokeRange + pick.HitboxRadius)
        {
            var am = ActionManager.Instance();
            if (am == null || am->AnimationLock > 0 || me.IsCasting) return;
            if (am->GetActionStatus(ActionType.Action, ProvokeId, pick.GameObjectId) != 0) return; // on cooldown / out of range / no LoS
            if (am->UseAction(ActionType.Action, ProvokeId, pick.GameObjectId))
            {
                lastProvoke = now;
                Note($"Provoke {pick.Name} (was on {victim})");
            }
        }
    }

    private void Note(string what)
    {
        LastAction = $"{DateTime.Now:HH:mm:ss} {what}";
        if (config.ChatFeedback) Plugin.ChatGui.Print($"[TSM] {what}");
    }
}
