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
    /// <summary>Compact view: add each player's share (%) of the party total for the sorted stat.</summary>
    public bool MeterCompactPercent { get; set; } = true;
    public bool MeterLocked { get; set; } = false;
    public bool MeterHideNames { get; set; } = false;
    /// <summary>Count every player around, not just your party (open world).</summary>
    public bool MeterEveryone { get; set; } = false;
    public bool MeterOnlyInCombat { get; set; } = false;
    public float MeterOpacity { get; set; } = 0.75f;

    /// <summary>With "only while fighting": keep the meter up this many seconds after a fight ends.</summary>
    public int MeterLingerSeconds { get; set; } = 10;
    public bool MeterHideInCutscenes { get; set; } = true;
    /// <summary>When locked, mouse clicks pass through to the game (use settings or /crec meter move to unlock).</summary>
    public bool MeterClickThrough { get; set; } = false;

    /// <summary>0 = damage, 1 = healing, 2 = damage taken. Orders rows and sizes the compact bars.</summary>
    public int MeterSortBy { get; set; } = 0;
    /// <summary>Show at most this many rows (0 = all). You're always included.</summary>
    public int MeterMaxRows { get; set; } = 0;
    public float MeterFontScale { get; set; } = 1f;
    public int MeterBarWidth { get; set; } = 240;
    public bool MeterRoleColors { get; set; } = false;
    public bool MeterHighlightMe { get; set; } = true;
    public bool MeterShowTitle { get; set; } = true;

    // Full-view columns (Name is always shown).
    public bool MeterColDps { get; set; } = true;
    public bool MeterColShare { get; set; } = true;
    public bool MeterColDamage { get; set; } = true;
    public bool MeterColCrit { get; set; } = true;
    public bool MeterColDh { get; set; } = true;
    public bool MeterColDeaths { get; set; } = true;
    public bool MeterColHps { get; set; } = true;
    public bool MeterColTaken { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
