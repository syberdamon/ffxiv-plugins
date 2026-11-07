using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MateriaManager;

/// <summary>Transmutation rule for one materia item (one type at one grade).</summary>
[Serializable]
public class FodderRule
{
    public bool Fodder { get; set; }
    /// <summary>Never use the last N of this one (in your bags).</summary>
    public int Keep { get; set; }
    /// <summary>You ticked/unticked it yourself (results never override that).</summary>
    public bool UserSet { get; set; }
}

/// <summary>How much of one materia the materia retainer holds. Custom = false: use the defaults.</summary>
[Serializable]
public class RetainerRule
{
    public bool Custom { get; set; }
    public bool Skip { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    // ---- saddlebag
    /// <summary>Opening the saddlebag moves bag materia into it (paused while materia is held for transmuting).</summary>
    public bool AutoStoreSaddlebag { get; set; } = true;

    // ---- materia retainer
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = "";
    /// <summary>Opening the materia retainer's inventory balances it to the targets.</summary>
    public bool AutoBalanceRetainer { get; set; } = true;
    /// <summary>Below Min the retainer is topped up to Max; above Max the extra comes back to your bags.</summary>
    public int DefaultMin { get; set; } = 800;
    public int DefaultMax { get; set; } = 800;
    /// <summary>Only grades at or above this are kept in the retainer (lower grades stay with you).</summary>
    public int RetainerMinGrade { get; set; } = 1;
    /// <summary>Top up from the saddlebag too when it's open (bags first).</summary>
    public bool FillFromSaddlebag { get; set; } = true;

    // ---- pull by grade (into your bags, from the saddlebag and/or the open retainer)
    public int PullGradeFrom { get; set; } = 11;
    public int PullGradeTo { get; set; } = 12;
    public bool PullFromSaddlebag { get; set; } = true;
    public bool PullFromRetainer { get; set; } = true;
    public Dictionary<uint, RetainerRule> RetainerRules { get; set; } = new();
    /// <summary>What the materia retainer held when last opened (item id -> count), for display.</summary>
    public Dictionary<uint, int> RetainerSeen { get; set; } = new();
    public DateTime? RetainerSeenAt { get; set; }

    // ---- transmutation (item id -> rule)
    public Dictionary<uint, FodderRule> Rules { get; set; } = new();
    public bool SameGradeBatches { get; set; } = true;
    /// <summary>Stop after this many batches per trip (a safety cap).</summary>
    public int MaxBatchesPerRun { get; set; } = 10;
    /// <summary>A result at a grade being transmuted is used in later batches (keep rolling until a grade runs short).</summary>
    public bool RollResults { get; set; } = true;
    /// <summary>Results from the current trip go first in the next batches and aren't held back for the retainer.</summary>
    public bool RerollResultsFirst { get; set; } = true;
    /// <summary>Transmute results not rolled again yet (item id -> count), kept across trips.</summary>
    public Dictionary<uint, int> Rerolls { get; set; } = new();
    /// <summary>Master cap: nothing above this grade is ever transmuted, whatever is ticked.</summary>
    public int MaxTransmuteGrade { get; set; } = 11;
    /// <summary>Never transmute materia already at its type's highest grade (it can't upgrade, only change type).</summary>
    public bool SkipTopGrade { get; set; } = true;
    /// <summary>Don't transmute what the materia retainer still needs to reach its maximum.</summary>
    public bool ReserveForRetainer { get; set; } = true;

    // ---- purge: sell low-grade odds and ends that can't make a batch of five
    public int PurgeBelowGrade { get; set; } = 7;
    public int PurgeUnder { get; set; } = 5;
    /// <summary>Also keep them if the grade still has five in total across types (they can batch mixed).</summary>
    public bool PurgeSpareMixable { get; set; } = false;
    public bool UseLifestream { get; set; } = true;

    // ---- v1 (kept for migration)
    public bool AutoStore { get; set; } = true;

    public (int Min, int Max)? TargetFor(MateriaInfo m)
    {
        if (RetainerRules.TryGetValue(m.ItemId, out var r) && r.Custom) return r.Skip ? null : (r.Min, Math.Max(r.Min, r.Max));
        if (m.Grade < RetainerMinGrade) return null;
        return (DefaultMin, Math.Max(DefaultMin, DefaultMax));
    }

    public void Migrate()
    {
        if (Version < 2) { AutoStoreSaddlebag = AutoStore; Version = 2; }
        if (Version < 3) { SeedRerollsFromLog(); Version = 3; }
    }

    /// <summary>v3: results from earlier trips (net gains in the transmute log) join the reroll list.</summary>
    private void SeedRerollsFromLog()
    {
        try
        {
            var path = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "transmute_log.jsonl");
            if (!System.IO.File.Exists(path)) return;
            var net = new Dictionary<uint, int>();
            foreach (var line in System.IO.File.ReadLines(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.GetProperty("type").GetString() != "batch") continue;
                foreach (var i in e.GetProperty("in").EnumerateArray()) { var id = i.GetProperty("id").GetUInt32(); net[id] = net.GetValueOrDefault(id) - 1; }
                if (e.TryGetProperty("out", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Object)
                { var id = o.GetProperty("id").GetUInt32(); net[id] = net.GetValueOrDefault(id) + 1; }
            }
            foreach (var (id, n) in net) if (n > 0) Rerolls[id] = n;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't read the transmute log for rerolls"); }
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
