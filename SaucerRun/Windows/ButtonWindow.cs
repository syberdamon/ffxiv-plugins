using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;

namespace SaucerRun.Windows;

/// <summary>
/// The floating button: green when today's Mini Cactpot and this week's Jumbo Cactpot are done, amber
/// when not. Click to run (or stop); right-click for settings. Hover for the details.
/// </summary>
public sealed class ButtonWindow : Window
{
    private static readonly Vector4 Done = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Todo = new(1f, 0.75f, 0.3f, 1f);

    private readonly Plugin plugin;

    public ButtonWindow(Plugin plugin) : base("Saucer###SaucerRunButton")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    private Configuration C => plugin.Configuration;

    public override bool DrawConditions()
    {
        if (!C.ShowButton || Plugin.ObjectTable.LocalPlayer == null) return false;
        if (C.ButtonAlways || plugin.Runner.Busy) return true;
        var cond = Plugin.Condition;
        return !cond[ConditionFlag.InCombat] && !cond[ConditionFlag.BoundByDuty] && !cond[ConditionFlag.BetweenAreas];
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing;
        if (C.ButtonLocked) Flags |= ImGuiWindowFlags.NoMove;
        ShowCloseButton = false;
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetMainViewport().Size.X - 260, 160), ImGuiCond.FirstUseEver);
    }

    public override void Draw()
    {
        var t = plugin.Tracker;
        var s = t.Me();
        var mini = s?.MiniPlayed ?? 0;
        var jumbo = s?.JumboBought ?? 0;
        var miniDone = !C.DoMini || mini >= 3;
        var jumboDone = !C.DoJumbo || (jumbo >= 3 && (s?.JumboCashierVisited ?? false));
        var cards = Runner.CardsInBags().Sum(c => c.Qty);
        var runner = plugin.Runner;

        string label;
        if (runner.Busy) label = "Saucer: running (click to stop)";
        else if (miniDone && jumboDone) label = cards > 0 && C.DoCards ? $"Saucer: done · {cards} card(s)" : "Saucer: done";
        else label = "Saucer:" + (C.DoMini ? $" Mini {mini}/3" : "") + (C.DoJumbo ? $" · Jumbo {jumbo}/3" : "");

        ImGui.PushStyleColor(ImGuiCol.Text, miniDone && jumboDone ? Done : Todo);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor();
        if (clicked) { if (runner.Busy) runner.Stop(); else runner.Start(); }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) plugin.ToggleConfig();
        if (ImGui.IsItemHovered())
        {
            var next = Tracker.NextJumboDrawing();
            var tip = $"Mini Cactpot today: {mini}/3 (resets {Tracker.NextDailyReset().ToLocalTime():ddd h:mm tt})\n" +
                      $"Jumbo Cactpot this week: {jumbo}/3 tickets, winnings {(s?.JumboCashierVisited == true ? "checked" : "not checked yet")}" +
                      (next != DateTime.MinValue ? $" (drawing {next.ToLocalTime():ddd h:mm tt})" : "") + "\n" +
                      $"Triple Triad cards in your bags: {cards} ({runner.Unlearned()} not learned yet)\n";
            tip += runner.Busy ? $"\n{runner.Status}" : "\nClick: " + Describe() + "\nRight-click: settings.";
            if (!Runner.SaucyLoaded) tip += "\n\nSaucy isn't loaded: the Cactpots are skipped (it plays them).";
            ImGui.SetTooltip(tip);
        }
    }

    private string Describe()
    {
        var plan = plugin.Runner.Plan();
        if (plan.Count == 0) return "nothing to do right now.";
        return string.Join(", ", plan.Select(p => p switch
        {
            Runner.Step.Learn => "learn new cards",
            Runner.Step.Travel => "go to the Gold Saucer",
            Runner.Step.Cashier => "collect Jumbo winnings",
            Runner.Step.Jumbo => "buy Jumbo tickets",
            Runner.Step.Mini => "play the Mini Cactpot",
            _ => "sell cards for MGP",
        })) + ".";
    }
}
