using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace CombatRecorder;

/// <summary>One combatant's totals in an encounter (pets and ground effects count for their owner).</summary>
public sealed class Combatant
{
    public uint Id;
    public string Name = "";
    public string Job = "";
    public bool IsMe, InParty;
    public long Damage, Healing, Taken;
    public int Hits, Crits, DirectHits, Deaths;
    public long MaxHit;
    public string MaxHitName = "";
    public readonly Dictionary<string, SkillTotals> Skills = new();
}

public sealed class SkillTotals
{
    public long Damage, Healing;
    public int Hits, Crits, DirectHits;
    public long Max;
}

/// <summary>A fight: from the first damage between you/your party and an enemy, to leaving combat.</summary>
public sealed class Encounter
{
    public DateTime Start, LastDamage;
    public DateTime? End;
    public string Zone = "";
    public readonly Dictionary<uint, Combatant> Players = new();
    public readonly Dictionary<uint, (string Name, long Taken)> Enemies = new();

    /// <summary>ACT's encounter duration: first to last damage dealt (at least one second).</summary>
    public double Seconds => Math.Max(1, ((End == null ? DateTime.UtcNow : LastDamage) - Start).TotalSeconds);
    public string Title => Enemies.Count == 0 ? Zone : Enemies.Values.OrderByDescending(e => e.Taken).First().Name;
    public long TotalDamage => Players.Values.Sum(p => p.Damage);
}

/// <summary>
/// The meter, counted the way ACT (and so Cactbot's overlays) does for raw numbers:
///  - Damage = every hit's amount (normal, blocked and parried), plus damage-over-time ticks; healing
///    likewise including heal-over-time ticks (overhealing included, as ACT's "Healed").
///  - An encounter starts at the first damage between you or a party member (or their pets) and an enemy,
///    and ends when you leave combat; DPS = damage / encounter duration (first to last damage), the same
///    duration for everyone ("ENCDPS"), so a death or downtime lowers your number.
///  - DoT ticks name their source when the game sends it; when it doesn't, the tick goes to whoever has that
///    status on the target, split evenly if several do. [Estimate] for that case, as in ACT.
/// The last 10 encounters are kept.
/// </summary>
public sealed unsafe class Meter : IDisposable
{
    private const uint DoTCategory = 0x605, HoTCategory = 0x604, DeathCategory = 0x6;
    private const uint NoSource = 0xE0000000;

    private readonly Configuration config;
    private readonly Recorder recorder;
    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? hook;
    private DateTime outOfCombatSince = DateTime.MinValue;

    public Encounter? Current { get; private set; }
    public List<Encounter> History { get; } = new();

    public Meter(Configuration config, Recorder recorder)
    {
        this.config = config;
        this.recorder = recorder;
        recorder.Effect += OnEffect;
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                (nint)PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, OnActorControl);
            hook.Enable();
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Meter: couldn't hook actor control (DoT/HoT ticks won't count)."); }
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.TerritoryChanged += OnTerritory;
    }

    public void Dispose()
    {
        recorder.Effect -= OnEffect;
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.TerritoryChanged -= OnTerritory;
        hook?.Dispose();
    }

    public void Reset() { Current = null; History.Clear(); }

    private void OnTerritory(uint t) => EndEncounter();

    private void OnUpdate(IFramework framework)
    {
        if (Current == null || Current.End != null) return;
        if (Plugin.Condition[ConditionFlag.InCombat]) { outOfCombatSince = DateTime.MinValue; return; }
        if (outOfCombatSince == DateTime.MinValue) { outOfCombatSince = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - outOfCombatSince).TotalSeconds > 2) EndEncounter();
    }

    private void EndEncounter()
    {
        if (Current == null || Current.End != null) return;
        Current.End = DateTime.UtcNow;
        History.Insert(0, Current);
        if (History.Count > 10) History.RemoveRange(10, History.Count - 10);
    }

    // ================================================================ who's who

    /// <summary>A player, or the player owning this pet / chocobo / ground effect; null for enemies and others.</summary>
    private static IBattleChara? Owner(uint entityId)
    {
        var o = Plugin.ObjectTable.SearchByEntityId(entityId);
        if (o is IPlayerCharacter pc) return pc;
        if (o != null && o.OwnerId != NoSource && o.OwnerId != 0 &&
            Plugin.ObjectTable.SearchByEntityId(o.OwnerId) is IPlayerCharacter owner) return owner;
        return null;
    }

    private static bool IsEnemy(uint entityId) =>
        Plugin.ObjectTable.SearchByEntityId(entityId) is IBattleNpc n && (byte)n.BattleNpcKind is 5 or 1 /* combatant (enemies), or a boss part */;

    private static bool InParty(uint entityId)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me != null && me.EntityId == entityId) return true;
        foreach (var m in Plugin.PartyList) if (m.EntityId == entityId) return true;
        return false;
    }

    private Combatant? Player(Encounter e, IBattleChara pc)
    {
        if (!config.MeterEveryone && !InParty(pc.EntityId)) return null;
        if (e.Players.TryGetValue(pc.EntityId, out var c)) return c;
        var me = Plugin.ObjectTable.LocalPlayer;
        c = new Combatant
        {
            Id = pc.EntityId,
            Name = pc.Name.TextValue,
            Job = pc.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "",
            IsMe = me != null && me.EntityId == pc.EntityId,
            InParty = InParty(pc.EntityId),
        };
        e.Players[pc.EntityId] = c;
        return c;
    }

    private Encounter? EncounterFor(bool damageStarts)
    {
        if (Current != null && Current.End == null) return Current;
        if (!damageStarts) return null;
        Current = new Encounter { Start = DateTime.UtcNow, LastDamage = DateTime.UtcNow, Zone = ZoneName() };
        outOfCombatSince = DateTime.MinValue;
        return Current;
    }

    private static string ZoneName()
    {
        var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        return tt?.ContentFinderCondition.ValueNullable?.Name.ExtractText() is { Length: > 0 } d ? d : tt?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
    }

    private static readonly Dictionary<uint, string> ActionNames = new();
    private static string ActionName(uint id)
    {
        if (ActionNames.TryGetValue(id, out var n)) return n;
        n = Plugin.DataManager.GetExcelSheet<Action>().GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } a ? a : $"#{id}";
        if (id == 7 || id == 8) n = "Attack";
        return ActionNames[id] = n;
    }

    private static string StatusName(uint id) =>
        (Plugin.DataManager.GetExcelSheet<LuminaStatus>().GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } s ? s : $"#{id}") + " (tick)";

    // ================================================================ events

    private void OnEffect(uint caster, uint action, uint target, byte type, long value, bool crit, bool dh, bool onSource)
    {
        try
        {
            if (!config.MeterEnabled) return;
            var damage = type is 3 or 5 or 6;
            if (damage && !onSource)
            {
                var src = Owner(caster);
                if (src != null && IsEnemy(target))
                {
                    var e = EncounterFor(true)!;
                    e.LastDamage = DateTime.UtcNow;
                    var t = e.Enemies.GetValueOrDefault(target);
                    e.Enemies[target] = (Plugin.ObjectTable.SearchByEntityId(target)?.Name.TextValue ?? "", t.Taken + value);
                    if (Player(e, src) is { } c) AddDamage(c, ActionName(action), value, crit, dh);
                }
                else if (IsEnemy(caster) && Owner(target) is { } victim)
                {
                    var e = EncounterFor(InParty(victim.EntityId));
                    if (e != null) { e.LastDamage = DateTime.UtcNow; if (Player(e, victim) is { } c) c.Taken += value; }
                }
            }
            else if (type == 4)
            {
                var src = Owner(caster);
                var e = EncounterFor(false);
                if (src != null && e != null && Player(e, src) is { } c) AddHeal(c, ActionName(action), value);
            }
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Meter: effect"); }
    }

    private static void AddDamage(Combatant c, string skill, long value, bool crit, bool dh)
    {
        c.Damage += value; c.Hits++;
        if (crit) c.Crits++;
        if (dh) c.DirectHits++;
        if (value > c.MaxHit) { c.MaxHit = value; c.MaxHitName = skill; }
        if (!c.Skills.TryGetValue(skill, out var s)) c.Skills[skill] = s = new SkillTotals();
        s.Damage += value; s.Hits++;
        if (crit) s.Crits++;
        if (dh) s.DirectHits++;
        s.Max = Math.Max(s.Max, value);
    }

    private static void AddHeal(Combatant c, string skill, long value)
    {
        c.Healing += value;
        if (!c.Skills.TryGetValue(skill, out var s)) c.Skills[skill] = s = new SkillTotals();
        s.Healing += value;
    }

    private void OnActorControl(uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4, uint arg5, uint arg6,
        uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        hook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
        try
        {
            if (!config.MeterEnabled) return;
            switch (category)
            {
                case DoTCategory: OnTick(entityId, arg1, arg2, arg3, true); break;
                case HoTCategory: OnTick(entityId, arg1, arg2, arg3, false); break;
                case DeathCategory:
                    if (Current is { End: null } e && e.Players.TryGetValue(entityId, out var c)) c.Deaths++;
                    break;
            }
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Meter: actor control"); }
    }

    /// <summary>A DoT/HoT tick on entityId: status, amount, source (when the game sends it).</summary>
    private void OnTick(uint targetId, uint status, uint amount, uint source, bool dot)
    {
        if (amount == 0) return;
        var e = EncounterFor(false);
        if (e == null) return;
        var sources = new List<IBattleChara>();
        if (source != 0 && source != NoSource && Owner(source) is { } s) sources.Add(s);
        else if (Plugin.ObjectTable.SearchByEntityId(targetId) is IBattleChara tgt)
        {
            foreach (var st in tgt.StatusList)
                if (st.StatusId != 0 && (status == 0 || st.StatusId == status) && Owner(st.SourceId) is { } o && !sources.Contains(o)) sources.Add(o);
        }
        if (sources.Count == 0) return;
        var each = amount / sources.Count;
        foreach (var src in sources)
        {
            if (Player(e, src) is not { } c) continue;
            var name = StatusName(status);
            if (dot)
            {
                if (!IsEnemy(targetId)) continue;
                e.LastDamage = DateTime.UtcNow;
                var t = e.Enemies.GetValueOrDefault(targetId);
                e.Enemies[targetId] = (Plugin.ObjectTable.SearchByEntityId(targetId)?.Name.TextValue ?? "", t.Taken + each);
                AddDamage(c, name, each, false, false);
            }
            else AddHeal(c, name, each);
        }
    }
}
