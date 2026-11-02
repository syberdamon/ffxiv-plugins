using System;
using Dalamud.Configuration;

namespace TankStanceManager;

public enum TankRole
{
    MainTank = 0,
    OffTank = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = false;

    public TankRole Role { get; set; } = TankRole.OffTank;

    /// <summary>OT mode: turn stance ON when your enmity falls below this % of the top (MT).</summary>
    public int LowThreshold { get; set; } = 75;

    /// <summary>OT mode: turn stance OFF once your enmity reaches this % of the top (MT).</summary>
    public int HighThreshold { get; set; } = 90;

    /// <summary>OT mode only: act only inside instanced duties.</summary>
    public bool DutiesOnly { get; set; } = true;

    /// <summary>OT mode only: require another tank in the party before acting.</summary>
    public bool RequireCoTank { get; set; } = true;

    /// <summary>OT mode: also turn stance on if pushed to #3 or lower, even while above the low threshold.</summary>
    public bool TreatRankThreeAsLow { get; set; } = true;

    /// <summary>Minimum time between stance presses, to prevent flip-flopping.</summary>
    public int MinToggleIntervalMs { get; set; } = 2500;

    /// <summary>Echo each toggle to chat.</summary>
    public bool ChatFeedback { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
