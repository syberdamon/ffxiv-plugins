using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MateriaManager;

public enum StoreTo { Saddlebag = 0, Retainer = 1 }

/// <summary>Transmutation rule for one materia item (one type at one grade).</summary>
[Serializable]
public class FodderRule
{
    public bool Fodder { get; set; }
    /// <summary>Never use the last N of this one.</summary>
    public int Keep { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---- storage
    /// <summary>Opening the saddlebag / a retainer's inventory moves all bag materia there. Turn off while melding.</summary>
    public bool AutoStore { get; set; } = true;
    public StoreTo StoreTo { get; set; } = StoreTo.Saddlebag;

    // ---- transmutation (item id -> rule)
    public Dictionary<uint, FodderRule> Rules { get; set; } = new();
    /// <summary>Only batch five of the same grade (the result is then that grade or higher).</summary>
    public bool SameGradeBatches { get; set; } = true;
    public bool UseLifestream { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
