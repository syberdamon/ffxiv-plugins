using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace PoeticsSpender;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Which topsoil to buy (index into <see cref="Soils.Options"/>).</summary>
    public int SoilIndex { get; set; } = 0;

    public bool AlertEnabled { get; set; } = true;
    public int AlertThreshold { get; set; } = 1800;

    /// <summary>Top-left corner of the alert on screen. Null = top centre.</summary>
    public Vector2? AlertPosition { get; set; }
    public float AlertScale { get; set; } = 1.3f;

    /// <summary>
    /// Shop rows the user has confirmed once ("Hismena:5:Unidentifiable Ore" -> true).
    /// A purchase whose detected row isn't confirmed yet asks first.
    /// </summary>
    public HashSet<string> ConfirmedRows { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
