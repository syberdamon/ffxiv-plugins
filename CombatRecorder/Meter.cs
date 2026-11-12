using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
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

    // ---- raid-buff crediting (FFLogs-style rDPS / aDPS)
    /// <summary>Damage this player dealt that came from other players' buffs (taken off for rDPS and aDPS).</summary>
    public long BuffedByOthers;
    /// <summary>Damage other players dealt that came from this player's buffs (added for rDPS).</summary>
    public long BuffGiven;
    /// <summary>Hits with no outside crit / direct hit rate buff, for estimating this player's own rates.</summary>
    public int PlainCritHits, PlainCrits, PlainDhHits, PlainDhs;

    /// <summary>rDPS damage: own damage, minus what others' buffs added, plus what this player's buffs added to others.</summary>
    public long RaidDamage => Damage - BuffedByOthers + BuffGiven;
    /// <summary>aDPS damage: own damage minus what others' buffs added.</summary>
    public long AdjustedDamage => Damage - BuffedByOthers;
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
///  - rDPS / aDPS (FFLogs-style): on each direct hit, the part added by another player's raid buff (see RaidBuffs)
///    is taken off the hitter (aDPS) and, for rDPS, handed to the player who gave the buff. Damage-up buffs are
///    exact; crit / direct hit rate buffs use each player's own rates estimated from their unbuffed hits
///    [Estimate]. DoT ticks aren't re-credited (they snapshot buffs when applied) [Estimate].
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
                    if (Player(e, src) is { } c)
                    {
                        AddDamage(c, ActionName(action), value, crit, dh);
                        CreditBuffs(e, c, src, target, value, crit, dh);
                    }
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

    // ================================================================ raid buffs (rDPS / aDPS)

    private enum BuffKind { Damage, Crit, DirectHit, CritAndDh }

    /// <summary>
    /// Raid buffs by English status name (matched against the game's status sheet, so every ID with that name counts).
    /// Damage % for melee+tanks / ranged+healers (the same unless it's an AST card); OnTarget = an enemy debuff.
    /// Values per Dawntrail tooltips. Radiant Finale assumes 3 codas (6%) and Technical/Standard Finish full steps (5%):
    /// [Estimate] when fewer were used.
    /// </summary>
    private static readonly (string Name, BuffKind Kind, double Melee, double Ranged, bool OnTarget)[] RaidBuffs =
    {
        ("Divination", BuffKind.Damage, 0.06, 0.06, false),
        ("The Balance", BuffKind.Damage, 0.06, 0.03, false),
        ("The Spear", BuffKind.Damage, 0.03, 0.06, false),
        ("Arcane Circle", BuffKind.Damage, 0.03, 0.03, false),
        ("Brotherhood", BuffKind.Damage, 0.05, 0.05, false),
        ("Embolden", BuffKind.Damage, 0.05, 0.05, false),
        ("Searing Light", BuffKind.Damage, 0.05, 0.05, false),
        ("Starry Muse", BuffKind.Damage, 0.05, 0.05, false),
        ("Technical Finish", BuffKind.Damage, 0.05, 0.05, false),
        ("Standard Finish", BuffKind.Damage, 0.05, 0.05, false),
        ("Radiant Finale", BuffKind.Damage, 0.06, 0.06, false),
        ("Dokumori", BuffKind.Damage, 0.05, 0.05, true),
        ("Battle Litany", BuffKind.Crit, 0.10, 0.10, false),
        ("Chain Stratagem", BuffKind.Crit, 0.10, 0.10, true),
        ("Battle Voice", BuffKind.DirectHit, 0.20, 0.20, false),
        ("Devilment", BuffKind.CritAndDh, 0.20, 0.20, false),
    };

    private static readonly HashSet<string> MeleeOrTank = new()
    {
        "PLD", "WAR", "DRK", "GNB", "GLA", "MRD", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "PGL", "LNC", "ROG",
    };

    private static Dictionary<uint, int>? buffIds; // status ID -> index into RaidBuffs

    private static Dictionary<uint, int> BuffIds()
    {
        if (buffIds != null) return buffIds;
        var byName = new Dictionary<string, int>();
        for (var i = 0; i < RaidBuffs.Length; i++) byName[RaidBuffs[i].Name] = i;
        var map = new Dictionary<uint, int>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaStatus>(ClientLanguage.English))
            if (byName.TryGetValue(row.Name.ExtractText(), out var i)) map[row.RowId] = i;
        return buffIds = map;
    }

    /// <summary>Running estimate of a rate from unbuffed hits, starting from 25% (worth 20 hits) so early numbers stay sane.</summary>
    private static double Rate(int hits, int count, double lo, double hi) => Math.Clamp((count + 0.25 * 20) / (hits + 20.0), lo, hi);

    /// <summary>Takes the part of this hit that other players' raid buffs added off the hitter and gives it to them.</summary>
    private void CreditBuffs(Encounter e, Combatant hitter, IBattleChara src, uint targetId, long value, bool crit, bool dh)
    {
        var ids = BuffIds();
        var melee = MeleeOrTank.Contains(hitter.Job);
        var found = new List<(IBattleChara From, BuffKind Kind, double Pct)>();
        void Scan(IBattleChara? holder, bool onTarget)
        {
            if (holder == null) return;
            foreach (var st in holder.StatusList)
            {
                if (st.StatusId == 0 || !ids.TryGetValue(st.StatusId, out var i) || RaidBuffs[i].OnTarget != onTarget) continue;
                if (Owner(st.SourceId) is not { } from || from.EntityId == src.EntityId) continue; // own buffs stay with the hitter
                var b = RaidBuffs[i];
                if (!found.Any(f => f.From.EntityId == from.EntityId && f.Kind == b.Kind && f.Pct == (melee ? b.Melee : b.Ranged)))
                    found.Add((from, b.Kind, melee ? b.Melee : b.Ranged));
            }
        }
        Scan(src, false);
        Scan(Plugin.ObjectTable.SearchByEntityId(targetId) as IBattleChara, true);

        var critBuffs = found.Where(f => f.Kind is BuffKind.Crit or BuffKind.CritAndDh).ToList();
        var dhBuffs = found.Where(f => f.Kind is BuffKind.DirectHit or BuffKind.CritAndDh).ToList();

        // Own rates, before this hit updates them.
        var critRate = Rate(hitter.PlainCritHits, hitter.PlainCrits, 0.05, 0.6);
        var dhRate = Rate(hitter.PlainDhHits, hitter.PlainDhs, 0.0, 0.6);
        if (critBuffs.Count == 0) { hitter.PlainCritHits++; if (crit) hitter.PlainCrits++; }
        if (dhBuffs.Count == 0) { hitter.PlainDhHits++; if (dh) hitter.PlainDhs++; }
        if (found.Count == 0 || value <= 0) return;

        var credits = new List<(IBattleChara From, double Amount)>();

        // Damage-up buffs multiply: split the extra by each buff's share of the combined multiplier (log split, exact).
        var dmgBuffs = found.Where(f => f.Kind == BuffKind.Damage).ToList();
        var mult = dmgBuffs.Aggregate(1.0, (m, f) => m * (1 + f.Pct));
        var unbuffed = value / mult;
        if (dmgBuffs.Count > 0)
        {
            var extra = value - unbuffed;
            var logTotal = Math.Log(mult);
            foreach (var f in dmgBuffs) credits.Add((f.From, extra * Math.Log(1 + f.Pct) / logTotal));
        }

        // Crit rate buffs: if the hit crit, the crit's extra damage is credited by the buff's share of the total crit rate.
        // Crit damage multiplier = 1.35 + crit rate (the game's formula). [Estimate] via the estimated own rate.
        if (crit && critBuffs.Count > 0)
        {
            var sum = critBuffs.Sum(f => f.Pct);
            var extra = unbuffed * (1 - 1 / (1.35 + critRate)) * sum / (critRate + sum);
            foreach (var f in critBuffs) credits.Add((f.From, extra * f.Pct / sum));
        }

        // Direct hit rate buffs: same idea, direct hits are x1.25.
        if (dh && dhBuffs.Count > 0)
        {
            var sum = dhBuffs.Sum(f => f.Pct);
            var extra = unbuffed * (1 - 1 / 1.25) * sum / (dhRate + sum);
            foreach (var f in dhBuffs) credits.Add((f.From, extra * f.Pct / sum));
        }

        foreach (var (from, amount) in credits)
        {
            var give = (long)Math.Round(amount);
            if (give <= 0 || Player(e, from) is not { } giver) continue; // buffer not counted (not in party): leave it with the hitter
            giver.BuffGiven += give;
            hitter.BuffedByOthers += give;
        }
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
