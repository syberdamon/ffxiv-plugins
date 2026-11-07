using System;
using Dalamud.Configuration;

namespace RaiseCallouts;

public enum Channel { Party = 0, Echo = 1 }

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public Channel Channel { get; set; } = Channel.Party;

    /// <summary>{target} = who you're raising, {spell} = the raise, {how} = " (instant)" for Swiftcast/Dualcast raises.</summary>
    public string Template { get; set; } = "Raising {target}{how}";

    /// <summary>Only post when you're in a party (otherwise nothing, or an echo if Channel is Echo).</summary>
    public bool OnlyInParty { get; set; } = true;

    /// <summary>Only inside duties.</summary>
    public bool DutiesOnly { get; set; } = false;

    /// <summary>Don't post if someone else's raise is already on the target.</summary>
    public bool SkipIfAlreadyRaised { get; set; } = true;

    /// <summary>Wait this long into the cast before posting, so a cast you cancel at once says nothing.</summary>
    public int CastDelayMs { get; set; } = 300;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
