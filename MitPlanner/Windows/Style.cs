using System.Numerics;

namespace MitPlanner.Windows;

public static class Style
{
    public static readonly string[] TypeNames = { "Other", "Tank buster", "Raid-wide", "Stack", "Mechanic" };

    public static Vector4 Color(MechanicType t) => t switch
    {
        MechanicType.TankBuster => new(1.00f, 0.45f, 0.35f, 1f),
        MechanicType.Raidwide => new(0.95f, 0.75f, 0.30f, 1f),
        MechanicType.Stack => new(0.45f, 0.80f, 1.00f, 1f),
        MechanicType.Mechanic => new(0.75f, 0.65f, 1.00f, 1f),
        _ => new(0.70f, 0.70f, 0.70f, 1f),
    };

    public static string Badge(MechanicType t) => t switch
    {
        MechanicType.TankBuster => "TB",
        MechanicType.Raidwide => "AOE",
        MechanicType.Stack => "STK",
        MechanicType.Mechanic => "MECH",
        _ => "",
    };

    /// <summary>"1:23 (83s)" over a minute, "8.4s" under.</summary>
    public static string ClockWithSeconds(float seconds)
    {
        if (seconds < 0) seconds = 0;
        return seconds >= 60 ? $"{(int)seconds / 60}:{(int)seconds % 60:00} ({seconds:0}s)" : $"{seconds:0.0}s";
    }

    /// <summary>Plan-table fight time: "~6:47 (408s)", with "~" when estimated across a phase change.</summary>
    public static string FightTime(TimelineEvent e) =>
        $"{(e.FightTimeEstimated ? "~" : "")}{(int)e.FightTime / 60}:{(int)e.FightTime % 60:00} ({e.FightTime:0}s)";

    public static readonly Vector4 Warning = new(1f, 0.35f, 0.30f, 1f);
    public static readonly Vector4 Ok = new(0.45f, 0.85f, 0.45f, 1f);

    public static string Clock(float seconds)
    {
        if (seconds < 0) seconds = 0;
        return seconds >= 60 ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : $"{seconds:0.0}";
    }
}
