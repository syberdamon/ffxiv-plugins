using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace SaucerRun;

/// <summary>What one character has done since the last resets.</summary>
[Serializable]
public class CharacterState
{
    /// <summary>The daily reset (15:00 UTC) the Mini Cactpot count belongs to.</summary>
    public DateTime MiniPeriod { get; set; }
    /// <summary>Mini Cactpot tickets played since then (3 a day).</summary>
    public int MiniPlayed { get; set; }
    /// <summary>The Jumbo Cactpot drawing (weekly, by region) the ticket count belongs to.</summary>
    public DateTime JumboPeriod { get; set; }
    /// <summary>Jumbo Cactpot tickets bought for this week's drawing (3 a week).</summary>
    public int JumboBought { get; set; }
    /// <summary>Visited the cashier since the last drawing (winnings collected, or none).</summary>
    public bool JumboCashierVisited { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool DoMini { get; set; } = true;
    public bool DoJumbo { get; set; } = true;
    public bool DoCards { get; set; } = true;
    /// <summary>Use (learn) Triple Triad cards you don't have yet before selling the rest.</summary>
    public bool LearnCards { get; set; } = true;

    public bool ShowButton { get; set; } = true;
    public bool ButtonLocked { get; set; } = false;
    /// <summary>Show the button in combat and duties too.</summary>
    public bool ButtonAlways { get; set; } = false;

    public Dictionary<ulong, CharacterState> Characters { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
