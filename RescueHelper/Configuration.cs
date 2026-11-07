using System;
using Dalamud.Configuration;

namespace RescueHelper;

public enum RescueMode { Click = 0, Automatic = 1 }

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public RescueMode Mode { get; set; } = RescueMode.Click;

    /// <summary>Show someone as in danger when the next AoE resolves within this many seconds.</summary>
    public float WarnSeconds { get; set; } = 3f;
    /// <summary>Automatic: they must have been in danger at least this long (give them a chance to walk out)...</summary>
    public int AutoDelayMs { get; set; } = 500;
    /// <summary>...and the AoE must resolve within this many seconds.</summary>
    public float AutoWithinSeconds { get; set; } = 1.8f;

    public bool IgnoreTanksHoldingEnemies { get; set; } = true;
    /// <summary>Skip players with a head marker in the last few seconds (spreads, stacks: their spot is "unsafe" on purpose).</summary>
    public bool IgnoreMarkedPlayers { get; set; } = true;
    public float MarkerMemorySeconds { get; set; } = 8f;

    public bool ShowPanel { get; set; } = true;
    public bool LockPanel { get; set; } = false;
    public bool ChatFeedback { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
