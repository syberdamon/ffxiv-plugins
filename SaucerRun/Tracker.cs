using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Lumina.Excel.Sheets;

namespace SaucerRun;

/// <summary>
/// Counts what's been done, the way DailyDuty does it: each time the Mini Cactpot board opens is a
/// ticket played; each time the Jumbo Cactpot number window opens is a ticket bought; opening the
/// cashier's reward list means winnings were collected. Counts reset at the daily reset
/// (15:00 UTC) and at the Jumbo Cactpot drawing for your region. If the brokers say there's
/// nothing left (the run sees no board/number window after talking to them) the count is set to 3.
/// </summary>
public sealed unsafe class Tracker : IDisposable
{
    private readonly Configuration config;

    public Tracker(Configuration config)
    {
        this.config = config;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryDaily", OnMini);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryWeeklyInput", OnJumbo);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryWeeklyRewardList", OnReward);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnMini);
        Plugin.AddonLifecycle.UnregisterListener(OnJumbo);
        Plugin.AddonLifecycle.UnregisterListener(OnReward);
    }

    public DateTime LastMiniOpen { get; private set; } = DateTime.MinValue;
    public DateTime LastJumboOpen { get; private set; } = DateTime.MinValue;

    private void OnMini(AddonEvent t, AddonArgs a)
    {
        LastMiniOpen = DateTime.UtcNow;
        if (Me() is { } s) { s.MiniPlayed = Math.Min(3, s.MiniPlayed + 1); config.Save(); }
    }

    private void OnJumbo(AddonEvent t, AddonArgs a)
    {
        LastJumboOpen = DateTime.UtcNow;
        if (Me() is { } s) { s.JumboBought = Math.Min(3, s.JumboBought + 1); config.Save(); }
    }

    /// <summary>The cashier's reward list opened (a claim). The run keeps talking to the cashier until one opens none.</summary>
    private void OnReward(AddonEvent t, AddonArgs a) => LastRewardOpen = DateTime.UtcNow;

    public DateTime LastRewardOpen { get; private set; } = DateTime.MinValue;

    /// <summary>This character's state, rolled over to the current periods.</summary>
    public CharacterState? Me()
    {
        var cid = ContentId();
        if (cid == 0) return null;
        if (!config.Characters.TryGetValue(cid, out var s)) config.Characters[cid] = s = new CharacterState();
        var day = LastDailyReset();
        if (s.MiniPeriod != day) { s.MiniPeriod = day; s.MiniPlayed = 0; config.Save(); }
        var week = LastJumboDrawing();
        if (week != DateTime.MinValue && s.JumboPeriod != week) { s.JumboPeriod = week; s.JumboBought = 0; s.JumboCashierVisited = false; config.Save(); }
        return s;
    }

    public static ulong ContentId()
    {
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return ps == null ? 0 : ps->ContentId;
    }

    public bool MiniDone => Me() is { MiniPlayed: >= 3 };
    public bool JumboDone => Me() is { JumboBought: >= 3 };

    public static DateTime LastDailyReset()
    {
        var now = DateTime.UtcNow;
        var today = now.Date.AddHours(15);
        return now >= today ? today : today.AddDays(-1);
    }

    /// <summary>
    /// The last Jumbo Cactpot drawing for your home world's region (times as DailyDuty has them, UTC):
    /// NA and Cloud Sunday 02:00, Europe Saturday 19:00, Japan and Korea Saturday 12:00, Oceania Saturday 09:00,
    /// China and Taiwan Saturday 13:00.
    /// </summary>
    public static DateTime LastJumboDrawing()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return DateTime.MinValue;
        var dc = Plugin.DataManager.GetExcelSheet<WorldDCGroupType>().GetRowOrDefault(me.HomeWorld.Value.DataCenter.RowId);
        var (day, hour) = (dc?.Region.RowId ?? 2) switch
        {
            1 => (DayOfWeek.Saturday, 12),
            3 => (DayOfWeek.Saturday, 19),
            4 => (DayOfWeek.Saturday, 9),
            5 => (DayOfWeek.Saturday, 13),
            6 => (DayOfWeek.Saturday, 12),
            8 => (DayOfWeek.Saturday, 13),
            _ => (DayOfWeek.Sunday, 2),
        };
        var now = DateTime.UtcNow;
        var d = now.Date.AddHours(hour);
        while (d.DayOfWeek != day || d > now) d = d.AddDays(-1);
        return d;
    }

    public static DateTime NextDailyReset() => LastDailyReset().AddDays(1);
    public static DateTime NextJumboDrawing() => LastJumboDrawing() == DateTime.MinValue ? DateTime.MinValue : LastJumboDrawing().AddDays(7);

    public void MarkMiniDone() { if (Me() is { } s) { s.MiniPlayed = 3; config.Save(); } }
    public void MarkJumboDone() { if (Me() is { } s) { s.JumboBought = 3; config.Save(); } }
    public void ResetToday() { if (Me() is { } s) { s.MiniPlayed = 0; s.JumboBought = 0; s.JumboCashierVisited = false; config.Save(); } }
}
