using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MitPlanner;

public enum PullState { Off, Idle, Pulling, Set, WindingDown, Boss }

/// <summary>
/// Trash mode: tank mitigation for dungeon pulls, by rules (docs/DUNGEON_MIT_RULES.md).
/// Reacts to mobs on you, HP and incoming damage, whether the pull has settled, and the
/// estimated time left in the pull. Presses only with "Allow auto-use"; otherwise calls out.
/// Everything it sees and does goes to a run log (TrashLog) for tuning.
/// </summary>
public sealed class TrashMode : IDisposable
{
    // ---------------------------------------------------------------- per-job extras
    private const uint ArmsLength = 7548;

    /// <summary>Self-heal per job (PLD's Clemency is a GCD spell, so none).</summary>
    private static readonly Dictionary<string, uint> Heals = new()
    {
        ["Warrior"] = 3552,     // Equilibrium
        ["Gunbreaker"] = 16151, // Aurora
        ["Dark Knight"] = 3641, // Abyssal Drain
    };

    /// <summary>
    /// How long each mit counts as "up" after use, in seconds. [Assumption] Dawntrail values
    /// from memory; tune from the logs. Recasts come from game data.
    /// </summary>
    private static readonly Dictionary<uint, float> Durations = new()
    {
        [7531] = 20,                                  // Rampart
        [TankKits.ReprisalId] = 15,                   // Reprisal [Verify: 15 s in 7.x]
        [ArmsLength] = 6,                             // Arm's Length barrier (slow lingers on attackers)
        [17] = 15, [44] = 15, [3636] = 15, [16148] = 15, // the 40%s
        [22] = 10, [40] = 10, [25754] = 10, [16140] = 20, // Bulwark, Thrill, Oblation, Camouflage
        [3542] = 8, [3551] = 8, [7393] = 7, [16161] = 8,  // Sheltron, Bloodwhetting, TBN, HoC
        [30] = 10, [43] = 10, [3638] = 10, [16152] = 10,  // invulns
        [3552] = 15, [16151] = 18, [3641] = 1,            // heals (regen windows)
        [7388] = 15, [3540] = 15, [16471] = 15, [16160] = 15, // party mits
    };

    private const double TickSeconds = 0.1;
    private const double PressSpacing = 1.25;   // animation lock, same as planned presses
    private const double EmergencySpacing = 0.7;
    private const float BossHpFactor = 8f;      // [Assumption] an enemy with 8x your max HP is a boss

    // ---------------------------------------------------------------- state
    private readonly Configuration config;
    private readonly TimelineRunner runner;
    private readonly TrashLog log;
    private readonly Queue<(DateTime At, float Hp)> hpSamples = new();
    private readonly Queue<(DateTime At, float Loss)> lossSamples = new();
    private readonly Queue<(DateTime At, double MobHp, int Count)> mobSamples = new();
    private readonly Dictionary<string, DateTime> skipLogged = new();
    private readonly Dictionary<uint, bool> wasOnCooldown = new();
    private HashSet<uint>? stunIds;

    private DateTime nextTick = DateTime.MinValue;
    private DateTime lastPress = DateTime.MinValue;
    private DateTime lastSnap = DateTime.MinValue;
    private Vector3 anchor;
    private DateTime lastMoved = DateTime.UtcNow;
    private float lastHp = -1;
    private bool inPull;
    private bool majorUsedThisSet;
    private DateTime pullStart;
    private float pullMinHp;
    private int pullPresses;
    private bool dead;
    private uint loggedTerritory;
    private readonly Dictionary<uint, DateTime> calledOut = new();

    public PullState State { get; private set; } = PullState.Off;
    public string Summary { get; private set; } = "";
    public string? Callout { get; private set; }
    public DateTime CalloutUntil { get; private set; }
    public bool ShowCallout => Callout != null && DateTime.UtcNow < CalloutUntil;
    public TrashLog Log => log;

    public TrashMode(Configuration config, TimelineRunner runner)
    {
        this.config = config;
        this.runner = runner;
        log = new TrashLog(config);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        log.Dispose();
    }

    private TrashSettings S => config.Trash;

    // ================================================================ tick

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow.AddSeconds(TickSeconds);
        try { Tick(); }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Trash mode tick failed");
            nextTick = DateTime.UtcNow.AddSeconds(2);
        }
    }

    private void Tick()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var kit = me != null ? TankKits.For(me.ClassJob.RowId) : null;
        UpdateLogFile(me, kit);

        if (me == null || kit == null || !S.Enabled || !InDungeon())
        {
            SetState(PullState.Off, me, null);
            return;
        }

        var now = DateTime.UtcNow;
        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        var hp = me.MaxHp > 0 ? 100f * me.CurrentHp / me.MaxHp : 0;

        // Death
        if (me.CurrentHp == 0 && !dead) { dead = true; log.Write("DEATH", Describe(me, kit, Read(me))); }
        if (me.CurrentHp > 0) dead = false;

        // Movement (stationary timer)
        if (Vector3.Distance(anchor, me.Position) > 0.3f) { anchor = me.Position; lastMoved = now; }

        // HP samples: net change and gross damage taken (sum of drops) over the last 2 s
        if (lastHp >= 0 && me.CurrentHp < lastHp) lossSamples.Enqueue((now, lastHp - me.CurrentHp));
        lastHp = me.CurrentHp;
        hpSamples.Enqueue((now, hp));
        Trim(hpSamples, s => s.At, 3);
        Trim(lossSamples, s => s.At, 2);

        if (!inCombat)
        {
            EndPull(me);
            SetState(PullState.Idle, me, null);
            return;
        }

        var sig = Read(me);
        TrackOtherPresses(kit, sig);

        // Snapshot once a second in combat
        if ((now - lastSnap).TotalSeconds >= 1)
        {
            lastSnap = now;
            log.Write("SNAP", Describe(me, kit, sig));
        }
        if (inPull) pullMinHp = Math.Min(pullMinHp, hp);

        // ---- state
        PullState state;
        var forced = false;
        if (sig.Boss) state = PullState.Boss;
        else if (sig.MobsOnMe >= S.MinMobs && sig.Stationary >= S.StationarySeconds && sig.Gathered >= S.GatherShare)
            state = sig.Ttk is { } t && t < S.WindDownSeconds ? PullState.WindingDown : PullState.Set;
        else if (sig.MobsOnMe >= 2 && hp < S.MovingHpPercent) { state = PullState.Set; forced = true; }
        else if (sig.MobsOnMe >= 1) state = PullState.Pulling;
        else state = PullState.Idle;

        if (state is PullState.Set && State is not PullState.Set)
        {
            if (!inPull) { inPull = true; pullStart = now; pullMinHp = hp; pullPresses = 0; }
            majorUsedThisSet = false;
            log.Write("PULL", $"{(forced ? "forced (low HP while moving)" : "set")}\t{Describe(me, kit, sig)}");
        }
        if (state is PullState.Pulling && !inPull) { inPull = true; pullStart = now; pullMinHp = hp; pullPresses = 0; }
        SetState(state, me, sig);

        if (me.IsCasting || Plugin.Condition[ConditionFlag.Mounted] || me.CurrentHp == 0) return;

        // ---- rules
        if (Emergency(me, kit, sig, hp)) return;
        if ((now - lastPress).TotalSeconds < PressSpacing) return;

        switch (state)
        {
            case PullState.Set:
                if (sig.Stunned && !forced) { Skip("stunned", "mobs stunned: holding mits"); return; }
                Rotation(me, kit, sig, hp, forced);
                break;
            case PullState.WindingDown:
                if (hp < S.HealHpPercent) { if (!TryPress(kit.Short, "short", "winding down, low HP", me, kit, sig)) TryHeal(me, kit, sig, hp); }
                break;
            case PullState.Pulling:
                Skip("moving", "pulling/moving: holding mits");
                break;
        }
    }

    // ================================================================ rules

    private bool Emergency(IBattleChara me, TankKit kit, Signals sig, float hp)
    {
        if (State is PullState.Off or PullState.Idle || sig.MobsOnMe == 0 && State != PullState.Boss) return false;
        if ((DateTime.UtcNow - lastPress).TotalSeconds < EmergencySpacing) return false;

        var majors = new[] { kit.Big, kit.Rampart, ArmsLength };
        var majorActive = majors.Any(Active);
        var majorReady = majors.Any(Ready);

        // Lethal: this many seconds of current incoming damage, with no heals, would kill you.
        var lethal = sig.GrossHpPerSec > 0 && sig.GrossHpPerSec * S.InvulnLethalSeconds >= me.CurrentHp;
        if (lethal)
        {
            if (!majorActive && majorReady)
                return TryPress(Ready(kit.Big) ? kit.Big : Ready(kit.Rampart) ? kit.Rampart : ArmsLength, "emergency", $"lethal in {S.InvulnLethalSeconds:0.#}s: major", me, kit, sig);
            if (!majorActive && !majorReady && InvulnAllowed(kit) && Ready(kit.Invuln))
            {
                log.Write("INVULN", Describe(me, kit, sig));
                return TryPress(kit.Invuln, "invuln", $"lethal in {S.InvulnLethalSeconds:0.#}s, no major mit available", me, kit, sig);
            }
        }

        var lowHp = hp < S.EmergencyHpPercent || (hp < 50 && sig.GrossPctPerSec > 15);
        if (!lowHp) return false;
        log.Write("EMERGENCY", Describe(me, kit, sig));
        if (!Active(kit.Short) && TryPress(kit.Short, "emergency", "low HP: short", me, kit, sig)) return true;
        if (!majorActive && (TryPress(kit.Big, "emergency", "low HP: 40%", me, kit, sig) ||
                             TryPress(kit.Rampart, "emergency", "low HP: Rampart", me, kit, sig))) return true;
        if (TryHeal(me, kit, sig, hp)) return true;
        if (!Active(kit.Secondary) && TryPress(kit.Secondary, "emergency", "low HP: secondary", me, kit, sig)) return true;
        return false;
    }

    private void Rotation(IBattleChara me, TankKit kit, Signals sig, float hp, bool forced)
    {
        bool PullLasts(float seconds) => sig.Ttk is { } t ? t >= seconds : sig.AvgMobHpPct > 50;

        // 1. Short: every time it's back.
        if (!Active(kit.Short) && TryPress(kit.Short, "opener/short", "set, short back", me, kit, sig)) return;

        // WAR: Raw Intuition / Bloodwhetting heal on hit and usually cap HP on AoE pulls, so
        // nothing else goes out while it's up, or while HP is still high after it falls off.
        // (Emergency and invuln rules are separate.)
        if (kit.Job == "Warrior" && S.WarBloodwhettingOnly && me.Level >= S.WarShortHealLevel &&
            (Active(kit.Short) || hp >= S.WarOtherMitsBelowHpPct))
        {
            Skip("bwonly", Active(kit.Short)
                ? "WAR short-heal only: Bloodwhetting/Raw Intuition up, holding other mits"
                : $"WAR short-heal only: HP {hp:0}% >= {S.WarOtherMitsBelowHpPct:0}%, holding other mits");
            return;
        }

        // 2. Reprisal every pack.
        if (sig.MobsOnMe >= S.MinMobs && !Active(TankKits.ReprisalId) &&
            TryPress(TankKits.ReprisalId, "reprisal", "every pack", me, kit, sig)) return;

        // 3. One big mit at a time; refill when it expires while the pull is still long.
        var majorActive = Active(kit.Big) || Active(kit.Rampart) || Active(ArmsLength);
        if (!majorActive && (!majorUsedThisSet || PullLasts(S.GapMinPullSeconds) || hp < S.EscalateHpPercent))
        {
            var bigOk = Ready(kit.Big) && (PullLasts(S.HeavyMinPullSeconds) || hp < S.EscalateHpPercent);
            var warHold = kit.Job == "Warrior" && Active(kit.Short) && hp >= S.WarRampartAfterBloodwhettingHp;
            var reason = !majorUsedThisSet ? "opener" : hp < S.EscalateHpPercent ? $"HP<{S.EscalateHpPercent:0}%" : "gap refill";

            if (sig.MobsOnMe >= S.BigPullMobs && bigOk) { if (TryPress(kit.Big, "major", $"{reason}, big pull ({sig.MobsOnMe} mobs)", me, kit, sig)) { majorUsedThisSet = true; return; } }
            else if (Ready(kit.Rampart) && !warHold) { if (TryPress(kit.Rampart, "major", reason, me, kit, sig)) { majorUsedThisSet = true; return; } }
            else if (Ready(kit.Rampart) && warHold) Skip("warhold", "WAR: Rampart waits for Bloodwhetting to wear off");
            else if (bigOk) { if (TryPress(kit.Big, "major", $"{reason}, Rampart down", me, kit, sig)) { majorUsedThisSet = true; return; } }
            else if (TryPress(ArmsLength, "major", $"{reason}, Rampart and 40% down", me, kit, sig)) { majorUsedThisSet = true; return; }
        }

        // 4. Secondary fills gaps (or stacks when HP is dropping).
        if (!Active(kit.Secondary) && (!majorActive || hp < S.EscalateHpPercent) && PullLasts(S.GapMinPullSeconds) &&
            TryPress(kit.Secondary, "secondary", majorActive ? $"HP<{S.EscalateHpPercent:0}%" : "gap fill", me, kit, sig)) return;

        // 5. Heals.
        if (hp < S.HealHpPercent) TryHeal(me, kit, sig, hp);
    }

    private bool TryHeal(IBattleChara me, TankKit kit, Signals sig, float hp)
    {
        if (Heals.TryGetValue(kit.Job, out var heal) && !Active(heal) && TryPress(heal, "heal", $"HP {hp:0}%", me, kit, sig)) return true;
        if (!S.PartyMitsAsHeals || hp >= S.HealHpPercent - 10 || Active(kit.PartyMit)) return false;
        // Shake It Off consumes Thrill, Damnation/Vengeance and Bloodwhetting: never while they're up.
        if (kit.Job == "Warrior" && (Active(kit.Secondary) || Active(kit.Big) || Active(kit.Short)))
        {
            Skip("sio", "WAR: Shake It Off held (would consume Thrill/40%/Bloodwhetting)");
            return false;
        }
        return TryPress(kit.PartyMit, "heal", $"party mit as heal, HP {hp:0}%", me, kit, sig);
    }

    private bool InvulnAllowed(TankKit kit) => kit.Job switch
    {
        "Paladin" => S.InvulnPaladin,
        "Warrior" => S.InvulnWarrior,
        "Dark Knight" => S.InvulnDarkKnight,
        "Gunbreaker" => S.InvulnGunbreaker,
        _ => false,
    };

    // ================================================================ press / callout

    private bool TryPress(uint id, string layer, string reason, IBattleChara me, TankKit kit, Signals sig)
    {
        if (!Ready(id)) return false;
        var name = ActionCatalog.Find(id)?.Name ?? id.ToString();
        var now = DateTime.UtcNow;

        if (config.AllowAutoUse)
        {
            ActionState.Use(id);
            lastPress = now;
            pullPresses++;
            wasOnCooldown[id] = true; // don't log it again as "someone else used it"
            log.Write("PRESS", $"{name}\tlayer={layer}\treason={reason}\t{Describe(me, kit, sig)}");
            return true;
        }

        // Callout only: once per ability every 6 s.
        if (calledOut.TryGetValue(id, out var at) && (now - at).TotalSeconds < 6) return false;
        calledOut[id] = now;
        Callout = $"Trash: {name} ({reason})";
        CalloutUntil = now.AddSeconds(3);
        log.Write("CALLOUT", $"{name}\tlayer={layer}\treason={reason}\t{Describe(me, kit, sig)}");
        return true;
    }

    private static bool Ready(uint id)
    {
        if (id == 0 || ActionCatalog.Find(id) == null) return false; // not on this job / level (sync aware)
        var st = ActionState.Get(id);
        return !st.OnCooldown && st.Usable;
    }

    private static bool Active(uint id) =>
        id != 0 && ActionState.SecondsSinceUse(id) < Durations.GetValueOrDefault(id, 10);

    /// <summary>Log mits you (or another plugin) pressed, so the log shows the whole picture.</summary>
    private void TrackOtherPresses(TankKit kit, Signals sig)
    {
        foreach (var id in new[] { kit.Big, kit.Rampart, kit.Secondary, kit.Short, kit.Invuln, kit.PartyMit, TankKits.ReprisalId, ArmsLength })
        {
            if (id == 0) continue;
            var on = ActionState.SecondsSinceUse(id) < 1.5f;
            if (on && !wasOnCooldown.GetValueOrDefault(id))
                log.Write("USED", $"{ActionCatalog.Find(id)?.Name ?? id.ToString()}\tby=you/other\thp={sig.HpPct:0}%\tmobs={sig.MobsOnMe}");
            wasOnCooldown[id] = on;
        }
    }

    private void Skip(string key, string why)
    {
        if (skipLogged.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < 3) return;
        skipLogged[key] = DateTime.UtcNow;
        log.Write("SKIP", why);
    }

    // ================================================================ signals

    private readonly record struct Signals(
        float HpPct, float GrossHpPerSec, float GrossPctPerSec, float NetPctPerSec,
        int MobsOnMe, int Engaged, float Gathered, float Stationary, float? Ttk, float AvgMobHpPct,
        bool Stunned, bool Boss);

    private Signals Read(IBattleChara me)
    {
        var now = DateTime.UtcNow;
        var enemies = Plugin.ObjectTable.OfType<IBattleNpc>()
            .Where(n => (n.StatusFlags & StatusFlags.Hostile) != 0 && n.CurrentHp > 0 && n.IsTargetable &&
                        (n.StatusFlags & StatusFlags.InCombat) != 0 &&
                        Vector3.Distance(n.Position, me.Position) <= 30)
            .ToList();
        var onMe = enemies.Where(n => n.TargetObjectId == me.GameObjectId && Vector3.Distance(n.Position, me.Position) <= 15).ToList();
        var gathered = onMe.Count == 0 ? 0 :
            (float)onMe.Count(n => Vector3.Distance(n.Position, me.Position) <= S.GatherRadius + n.HitboxRadius) / onMe.Count;

        // Pull time left = engaged mob HP / party damage rate (last 4 s, same mob count only).
        var mobHp = enemies.Sum(n => (double)n.CurrentHp);
        mobSamples.Enqueue((now, mobHp, enemies.Count));
        Trim(mobSamples, s => s.At, 4);
        float? ttk = null;
        var first = mobSamples.Peek();
        var span = (now - first.At).TotalSeconds;
        if (span >= 3 && first.Count >= enemies.Count && first.MobHp > mobHp)
            ttk = (float)(mobHp / ((first.MobHp - mobHp) / span));

        var gross = lossSamples.Sum(s => s.Loss) / 2f;
        var net = hpSamples.Count > 1 ? (hpSamples.Peek().Hp - hpSamples.Last().Hp) / (float)Math.Max(0.5, (hpSamples.Last().At - hpSamples.Peek().At).TotalSeconds) : 0;

        stunIds ??= Plugin.DataManager.GetExcelSheet<Status>(Dalamud.Game.ClientLanguage.English)
            .Where(s => s.Name.ExtractText() == "Stun").Select(s => s.RowId).ToHashSet();
        var stunned = onMe.Count > 0 && onMe.Count(n => n.StatusList.Any(st => stunIds.Contains(st.StatusId))) * 2 >= onMe.Count;

        var boss = enemies.Count <= 2 && enemies.Any(n => n.MaxHp >= BossHpFactor * me.MaxHp);

        return new Signals(
            me.MaxHp > 0 ? 100f * me.CurrentHp / me.MaxHp : 0,
            gross, me.MaxHp > 0 ? 100f * gross / me.MaxHp : 0, net,
            onMe.Count, enemies.Count, gathered, (float)(now - lastMoved).TotalSeconds, ttk,
            enemies.Count == 0 ? 0 : (float)enemies.Average(n => n.MaxHp > 0 ? 100.0 * n.CurrentHp / n.MaxHp : 0),
            stunned, boss);
    }

    private string Describe(IBattleChara me, TankKit kit, Signals s)
    {
        var up = new[] { kit.Big, kit.Rampart, kit.Secondary, kit.Short, kit.Invuln, kit.PartyMit, TankKits.ReprisalId, ArmsLength }
            .Where(id => id != 0 && Active(id)).Select(id => ActionCatalog.Find(id)?.Name ?? id.ToString());
        return $"state={State}\thp={s.HpPct:0}%\tdmg={s.GrossPctPerSec:0.0}%/s\tnet={s.NetPctPerSec:0.0}%/s\tmobs={s.MobsOnMe}/{s.Engaged}" +
               $"\tgathered={s.Gathered:P0}\tstill={s.Stationary:0.0}s\tttk={(s.Ttk is { } t ? $"{t:0}s" : "?")}\tmobhp={s.AvgMobHpPct:0}%" +
               $"\tstun={(s.Stunned ? "y" : "n")}\tboss={(s.Boss ? "y" : "n")}\tup=[{string.Join(",", up)}]";
    }

    // ================================================================ bookkeeping

    private void SetState(PullState state, IBattleChara? me, Signals? sig)
    {
        State = state;
        Summary = sig is { } s
            ? $"{state}: {s.MobsOnMe} mobs on you, {s.HpPct:0}% HP, {s.GrossPctPerSec:0.0}%/s, pull ~{(s.Ttk is { } t ? $"{t:0}s" : "?")} left"
            : state.ToString();
    }

    private void EndPull(IBattleChara me)
    {
        if (!inPull) return;
        inPull = false;
        log.Write("END", $"duration={(DateTime.UtcNow - pullStart).TotalSeconds:0}s\tminhp={pullMinHp:0}%\tpresses={pullPresses}\tdead={(me.CurrentHp == 0 ? "y" : "n")}");
    }

    private void UpdateLogFile(IBattleChara? me, TankKit? kit)
    {
        var territory = Plugin.ClientState.TerritoryType;
        var want = me != null && kit != null && S.Enabled && S.Log && InDungeon();
        if (!want) { if (loggedTerritory != 0) { log.Close(); loggedTerritory = 0; } return; }
        if (loggedTerritory == territory) return;
        loggedTerritory = territory;
        var zone = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.ContentFinderCondition.ValueNullable?.Name.ExtractText();
        var job = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(me!.ClassJob.RowId)?.Abbreviation.ExtractText() ?? "?";
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var party = string.Join(",", Plugin.PartyList.Select(m => jobs.GetRowOrDefault(m.ClassJob.RowId)?.Abbreviation.ExtractText() ?? "?"));
        log.Open(string.IsNullOrWhiteSpace(zone) ? $"zone{territory}" : zone, $"{job}{me.Level}", party);
    }

    /// <summary>Dungeons only ([Assumption] ContentType 2 = Dungeons in the ContentType sheet).</summary>
    private static bool InDungeon()
    {
        if (!Plugin.Condition[ConditionFlag.BoundByDuty]) return false;
        var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        return tt?.ContentFinderCondition.ValueNullable?.ContentType.RowId == 2;
    }

    private static void Trim<T>(Queue<T> q, Func<T, DateTime> at, double seconds)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-seconds);
        while (q.Count > 0 && at(q.Peek()) < cutoff) q.Dequeue();
    }
}
