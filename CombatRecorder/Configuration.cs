using System;
using Dalamud.Configuration;

namespace CombatRecorder;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Record fights outside duties too (open world, FATEs, hunts).</summary>
    public bool RecordOutsideDuties { get; set; } = false;

    /// <summary>Snapshot interval for HP/MP/positions, seconds.</summary>
    public float SnapshotSeconds { get; set; } = 0.5f;

    /// <summary>Keep at most this many files; the oldest are deleted.</summary>
    public int MaxFiles { get; set; } = 400;

    /// <summary>Print a chat line when a recording starts and ends.</summary>
    public bool ChatNotices { get; set; } = true;

    // ---- DPS meter
    public bool MeterEnabled { get; set; } = true;
    public bool ShowMeter { get; set; } = true;
    public bool MeterCompact { get; set; } = false;
    public bool MeterLocked { get; set; } = false;
    public bool MeterHideNames { get; set; } = false;
    /// <summary>Count every player around, not just your party (open world).</summary>
    public bool MeterEveryone { get; set; } = false;
    public bool MeterOnlyInCombat { get; set; } = false;
    public float MeterOpacity { get; set; } = 0.75f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
