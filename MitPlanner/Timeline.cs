using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MitPlanner;

public enum MechanicType
{
    Other = 0,
    TankBuster = 1,
    Raidwide = 2,
    Stack = 3,
    Mechanic = 4,
    SharedTankBuster = 5,   // both tanks take it (cactbot Responses.sharedTankBuster)
}

public static class MechanicTypes
{
    public static bool IsBuster(this MechanicType t) => t is MechanicType.TankBuster or MechanicType.SharedTankBuster;

    /// <summary>Which tag wins when several apply (lower = stronger).</summary>
    public static int Rank(this MechanicType t) => t switch
    {
        MechanicType.SharedTankBuster => 0,
        MechanicType.TankBuster => 1,
        MechanicType.Raidwide => 2,
        MechanicType.Stack => 3,
        MechanicType.Mechanic => 4,
        _ => 9,
    };
}

public enum SyncKind
{
    None,
    Ability,      // fires when the ability resolves (network "action effect")
    StartsUsing,  // fires when the cast bar starts
    SystemLog,    // a system message, e.g. "The Shattered Lair will be sealed off" (dungeon boss start)
}

/// <summary>One line of a cactbot timeline.</summary>
public sealed class TimelineEvent
{
    public int Index { get; init; }
    public float Time { get; init; }
    public string Label { get; init; } = "";
    public bool Hidden { get; set; }

    /// <summary>1 for the first "Stamp", 2 for the second, ... (used to attach plans).</summary>
    public int Occurrence { get; set; }

    public SyncKind Sync { get; init; }
    public Regex? IdPattern { get; init; }

    /// <summary>SystemLogMessage syncs: LogMessage row id and first parameter (e.g. a PlaceName id).</summary>
    public uint LogId { get; init; }
    public uint? LogParam1 { get; init; }
    public float WindowBefore { get; init; } = 2.5f;  // cactbot default
    public float WindowAfter { get; init; } = 2.5f;
    /// <summary>Where a sync on this line sends the clock (a time, or a resolved "label").</summary>
    public float? Jump { get; set; }

    /// <summary>"forcejump": jump when the clock reaches this line, synced or not (loops).</summary>
    public bool ForceJump { get; init; }

    /// <summary>Unresolved jump "label" target, filled in after the whole file is read.</summary>
    internal string? JumpLabel { get; init; }
    public float Duration { get; init; }

    /// <summary>
    /// Estimated real fight time. cactbot parks later phases at placeholder times (phase 2 at
    /// 1000s), so long empty gaps are collapsed to approximate elapsed time.
    /// </summary>
    public float FightTime { get; set; }

    /// <summary>True once a gap has been collapsed: the fight time is an estimate.</summary>
    public bool FightTimeEstimated { get; set; }

    /// <summary>Stable key for plans and tags: "Stamp#2".</summary>
    public string Key => $"{Label}#{Occurrence}";
}

/// <summary>
/// Parser for the subset of cactbot's timeline format the bundled fights use:
///   time "label" [Ability|StartsUsing { id: "4AC7" | ["A","B"] | "4A9[67]", ... }] [window a,b] [jump t] [duration d]
///   hideall "label"
///   time label "name"                  (a named point in time, for jump "name")
///   ... jump "name" | forcejump t | forcejump "name"
/// Other sync types (InCombat, ActorControl, NpcYell, ...) are shown but never sync the clock.
/// Format reference: cactbot docs/TimelineGuide.md (Apache-2.0).
/// </summary>
public sealed class Timeline
{
    public string Name { get; private init; } = "";
    public IReadOnlyList<TimelineEvent> Events { get; private init; } = Array.Empty<TimelineEvent>();

    private static readonly Regex LineRx = new(
        @"^(?<time>\d+(?:\.\d+)?)\s+""(?<label>[^""]*)""(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex LogSyncRx = new(@"\bSystemLogMessage\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex LogIdRx = new(@"\bid:\s*""(?<v>[0-9A-Fa-f]+)""", RegexOptions.Compiled);
    private static readonly Regex LogParamRx = new(@"\bparam1:\s*""(?<v>[0-9A-Fa-f]+)""", RegexOptions.Compiled);
    private static readonly Regex SyncRx = new(
        @"\b(?<kind>Ability|StartsUsing)\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(
        @"\bid:\s*(?:""(?<one>[^""]+)""|\[(?<many>[^\]]+)\])", RegexOptions.Compiled);
    private static readonly Regex WindowRx = new(@"\bwindow\s+(?<a>\d+(?:\.\d+)?)(?:\s*,\s*(?<b>\d+(?:\.\d+)?))?", RegexOptions.Compiled);
    private static readonly Regex JumpRx = new(
        @"\b(?<force>force)?jump\s+(?:(?<t>\d+(?:\.\d+)?)|""(?<l>[^""]*)"")", RegexOptions.Compiled);
    private static readonly Regex LabelRx = new(@"^(?<time>\d+(?:\.\d+)?)\s+label\s+""(?<name>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex DurationRx = new(@"\bduration\s+(?<d>\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex HideAllRx = new(@"^hideall\s+""(?<label>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex QuotedRx = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static Timeline Parse(string name, string text)
    {
        var events = new List<TimelineEvent>();
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            var hide = HideAllRx.Match(line);
            if (hide.Success) { hidden.Add(hide.Groups["label"].Value); continue; }

            var named = LabelRx.Match(line);
            if (named.Success) { labels[named.Groups["name"].Value] = F(named.Groups["time"].Value); continue; }

            var m = LineRx.Match(line);
            if (!m.Success) continue;

            var rest = m.Groups["rest"].Value;
            var sync = SyncRx.Match(rest);
            Regex? pattern = null;
            var kind = SyncKind.None;
            uint logId = 0;
            uint? logParam = null;
            if (sync.Success)
            {
                kind = sync.Groups["kind"].Value == "Ability" ? SyncKind.Ability : SyncKind.StartsUsing;
                pattern = BuildIdPattern(sync.Groups["body"].Value);
                if (pattern == null) kind = SyncKind.None;
            }
            else if (LogSyncRx.Match(rest) is { Success: true } log &&
                     LogIdRx.Match(log.Groups["body"].Value) is { Success: true } lid &&
                     uint.TryParse(lid.Groups["v"].Value, NumberStyles.HexNumber, null, out logId))
            {
                kind = SyncKind.SystemLog;
                if (LogParamRx.Match(log.Groups["body"].Value) is { Success: true } lp &&
                    uint.TryParse(lp.Groups["v"].Value, NumberStyles.HexNumber, null, out var p1))
                    logParam = p1;
            }

            var window = WindowRx.Match(rest);
            float before = 2.5f, after = 2.5f;
            if (window.Success)
            {
                before = F(window.Groups["a"].Value);
                after = window.Groups["b"].Success ? F(window.Groups["b"].Value) : before;
            }

            var jump = JumpRx.Match(rest);
            var duration = DurationRx.Match(rest);

            events.Add(new TimelineEvent
            {
                Index = events.Count,
                Time = F(m.Groups["time"].Value),
                Label = m.Groups["label"].Value,
                Sync = kind,
                IdPattern = pattern,
                LogId = logId,
                LogParam1 = logParam,
                WindowBefore = before,
                WindowAfter = after,
                Jump = jump.Success && jump.Groups["t"].Success ? F(jump.Groups["t"].Value) : null,
                JumpLabel = jump.Success && jump.Groups["l"].Success ? jump.Groups["l"].Value : null,
                ForceJump = jump.Success && jump.Groups["force"].Success,
                Duration = duration.Success ? F(duration.Groups["d"].Value) : 0,
            });
        }

        foreach (var e in events)
            if (e.JumpLabel != null && labels.TryGetValue(e.JumpLabel, out var at)) e.Jump = at;

        // Hidden: explicit hideall, plus cactbot's "--x--" helper lines.
        var counts = new Dictionary<string, int>();
        foreach (var e in events)
        {
            e.Hidden = hidden.Contains(e.Label) || e.Label == "Start" || (e.Label.StartsWith("--") && e.Label.EndsWith("--"));
            counts[e.Label] = counts.GetValueOrDefault(e.Label) + 1;
            e.Occurrence = counts[e.Label];
        }

        var ordered = events.OrderBy(e => e.Time).ThenBy(e => e.Index).ToList();
        EstimateFightTimes(ordered);
        return new Timeline { Name = name, Events = ordered };
    }

    /// <summary>Gaps longer than this with no timeline lines are phase jumps, not real time.</summary>
    private const float PhaseGap = 30f;
    /// <summary>[Estimate] Time assumed to pass across a collapsed jump (transition cast / targetable delay).</summary>
    private const float CollapsedGap = 5f;

    private static void EstimateFightTimes(List<TimelineEvent> events)
    {
        float fight = 0;
        var estimated = false;
        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                var prev = events[i - 1];
                var gap = events[i].Time - prev.Time;
                if (gap > PhaseGap)
                {
                    // A line with a duration (e.g. the cutscene) really does take that long.
                    gap = prev.Duration > 0 ? Math.Min(gap, prev.Duration) : CollapsedGap;
                    estimated = true;
                }
                fight += gap;
            }
            else
            {
                fight = events[0].Time;
            }
            events[i].FightTime = fight;
            events[i].FightTimeEstimated = estimated;
        }
    }

    /// <summary>cactbot ids are hex strings or small regexes ("4A9[67]"); match case-insensitively, whole id.</summary>
    private static Regex? BuildIdPattern(string body)
    {
        var id = IdRx.Match(body);
        if (!id.Success) return null;

        var parts = id.Groups["one"].Success
            ? new[] { id.Groups["one"].Value }
            : QuotedRx.Matches(id.Groups["many"].Value).Select(q => q.Groups[1].Value).ToArray();
        if (parts.Length == 0) return null;

        try { return new Regex($"^(?:{string.Join("|", parts)})$", RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch (ArgumentException) { return null; }
    }

    private static string StripComment(string line)
    {
        // '#' starts a comment unless it's inside quotes.
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == '#' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
