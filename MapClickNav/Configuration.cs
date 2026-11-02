using System;
using Dalamud.Configuration;

namespace MapClickNav;

public enum ModifierKey
{
    Ctrl = 0,
    Alt = 1,
    Shift = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Hold this and left-click the map to travel.</summary>
    public ModifierKey Modifier { get; set; } = ModifierKey.Ctrl;

    /// <summary>Clicking another zone's map teleports there first (nearest attuned aetheryte).</summary>
    public bool CrossZoneTeleport { get; set; } = true;

    /// <summary>Within your zone, teleport to the nearest aetheryte when that's faster than traveling.</summary>
    public bool TeleportIfFaster { get; set; } = true;

    /// <summary>Teleport only if it saves at least this much distance (CBT's default: 300).</summary>
    public int TeleportMargin { get; set; } = 300;

    /// <summary>Summon Mount Roulette before traveling (fly if the zone allows, otherwise ride).</summary>
    public bool AutoMount { get; set; } = true;

    /// <summary>In zones where you can't fly, still mount for ground travel.</summary>
    public bool MountForGroundTravel { get; set; } = true;

    /// <summary>Give up on mounting after this many seconds and travel without a mount.</summary>
    public int MountTimeoutSeconds { get; set; } = 6;

    /// <summary>Echo what the plugin is doing to chat.</summary>
    public bool ChatFeedback { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
