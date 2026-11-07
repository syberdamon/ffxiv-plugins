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

    /// <summary>Show the small ON/OFF + MT/OT control panel (remembered across sessions).</summary>
    public bool ShowControlWindow { get; set; } = true;

    /// <summary>Stop the control panel from being dragged.</summary>
    public bool LockControlWindow { get; set; } = false;

    /// <summary>Show a one-line status under the control panel buttons.</summary>
    public bool ShowStatusLine { get; set; } = true;

    // ---- Loose mobs (trash pulls): an enemy attacking a non-tank party member.
    /// <summary>Target loose mobs automatically (worst one first) so your next hits land on them.</summary>
    public bool AutoTargetLooseMobs { get; set; } = false;
    /// <summary>Provoke a loose mob when Provoke is ready.</summary>
    public bool ProvokeLooseMobs { get; set; } = false;
    /// <summary>A mob must stay on that player this long before it counts (ignores brief target swaps).</summary>
    public int LooseMobDelayMs { get; set; } = 1000;
    /// <summary>Leave mobs alone while a boss is up (tank swaps and mechanics are the plan's job).</summary>
    public bool TrashOnly { get; set; } = true;
    /// <summary>Mobs on the other tank aren't loose.</summary>
    public bool IgnoreMobsOnCoTank { get; set; } = true;

    /// <summary>Echo each toggle to chat.</summary>
    public bool ChatFeedback { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
