using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace MitPlanner.Decision;

/// <summary>
/// Dungeon route maps (built offline by tools/build_routes.py from Combat Recorder logs, embedded as
/// MitPlanner.Routes.&lt;territory&gt;.json): where each pull happens, how big it is, how long it
/// lasts, the walk before it, and which stops are bosses. Plus pacing.json, the fallback for
/// dungeons without a map: median gap between pulls and pull length over all recorded runs.
/// </summary>
public static class RouteMap
{
    public sealed class Stop
    {
        public float X { get; set; }
        public float Z { get; set; }
        public float Radius { get; set; }
        public float Mobs { get; set; }
        public float Duration { get; set; }
        public float? GapBefore { get; set; }
        public string? Boss { get; set; }
        public int Seen { get; set; }
    }

    public sealed class Map
    {
        public uint Territory { get; set; }
        public string Zone { get; set; } = "";
        public int Runs { get; set; }
        public List<Stop> Stops { get; set; } = new();
    }

    public sealed class Pacing
    {
        public float GapMedian { get; set; } = 17f;
        public float GapP25 { get; set; } = 12f;
        public float GapP75 { get; set; } = 24f;
        public float PullMedian { get; set; } = 60f;
    }

    private const string Prefix = "MitPlanner.Routes.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static Dictionary<uint, Map>? maps;
    private static Pacing? pacing;

    public static Pacing Pace { get { Load(); return pacing!; } }

    public static Map? For(uint territory)
    {
        Load();
        return maps!.GetValueOrDefault(territory);
    }

    private static void Load()
    {
        if (maps != null) return;
        maps = new();
        pacing = new Pacing();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix) && n.EndsWith(".json")))
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(res)!;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (res.EndsWith("pacing.json")) pacing = JsonSerializer.Deserialize<Pacing>(text, Json) ?? pacing;
                else if (JsonSerializer.Deserialize<Map>(text, Json) is { } m && m.Stops.Count > 0) maps[m.Territory] = m;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Couldn't read route map {res}");
            }
        }
    }

    /// <summary>The stop you're at (nearest within its radius), or -1.</summary>
    public static int StopAt(Map map, Vector3 pos)
    {
        var best = -1;
        var bestD = float.MaxValue;
        for (var i = 0; i < map.Stops.Count; i++)
        {
            var s = map.Stops[i];
            var d = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(s.X, s.Z));
            if (d <= s.Radius && d < bestD) { best = i; bestD = d; }
        }
        return best;
    }
}

/// <summary>What comes after this pull, and how sure we are.</summary>
public readonly record struct Lookahead(
    float RestOfPull,     // seconds this pull is expected to last from now
    float SecondsUntil,   // until the next pull starts (rest of this pull + the walk)
    float NextMobs,       // its size (0 for a boss)
    bool NextIsBoss,
    float BossChance,     // 0..1: 1/0 from a map, a guess without one
    float NextDuration,
    string Source)        // "map: stop 4 of 12" / "pacing (no map)" / ...
{
    public override string ToString() =>
        $"next={(NextIsBoss ? "boss" : $"{NextMobs:0} mobs")} in {SecondsUntil:0}s ({Source})";
}

public static class Lookaheads
{
    /// <summary>
    /// Typical pack size and boss share when there's no map. [Estimate] from the maps built
    /// 2026-11-11: packs median ~5-6 mobs; about 3 of every 10 stops are bosses.
    /// </summary>
    private const float TypicalMobs = 5f;
    private const float BossShareNoMap = 0.3f;

    /// <param name="pullElapsed">Seconds since this pull started.</param>
    /// <param name="ttk">Trash mode's time-to-kill estimate for this pull, if it has one.</param>
    public static Lookahead From(uint territory, Vector3 pos, float pullElapsed, float? ttk)
    {
        var pace = RouteMap.Pace;
        var map = RouteMap.For(territory);
        if (map != null)
        {
            var at = RouteMap.StopAt(map, pos);
            if (at >= 0)
            {
                var here = map.Stops[at];
                var rest = ttk ?? Math.Max(5f, here.Duration - pullElapsed);
                if (at + 1 < map.Stops.Count)
                {
                    var next = map.Stops[at + 1];
                    var boss = next.Boss != null;
                    return new(rest, rest + (next.GapBefore ?? pace.GapMedian), boss ? 0 : next.Mobs, boss, boss ? 1 : 0,
                               next.Duration, $"map: stop {at + 1} of {map.Stops.Count}");
                }
                return new(rest, float.PositiveInfinity, 0, false, 0, 0, "map: last stop");
            }
        }
        var restNoMap = ttk ?? Math.Max(5f, pace.PullMedian - pullElapsed);
        return new(restNoMap, restNoMap + pace.GapMedian, TypicalMobs, false, BossShareNoMap, pace.PullMedian,
                   map != null ? "pacing (off the map)" : "pacing (no map)");
    }
}
