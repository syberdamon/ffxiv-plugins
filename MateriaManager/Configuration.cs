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
    public int Version { get; set; } = 2;

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
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
