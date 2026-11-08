using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SaucerRun.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);

    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("Saucer Run###SaucerRunConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 300), MaximumSize = new Vector2(900, 900) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        var changed = false;
        if (!Runner.SaucyLoaded) ImGui.TextColored(Warn, "Saucy isn't loaded. It plays the Cactpots (its Mini and Jumbo Cactpot modules must be on).");
        if (!plugin.Runner.VNavLoaded) ImGui.TextColored(Warn, "vnavmesh isn't loaded: it's needed to walk to the NPCs.");
        if (plugin.Runner.Status.Length > 0) ImGui.TextColored(Dim, plugin.Runner.Status);

        var m = C.DoMini;
        if (ImGui.Checkbox("Mini Cactpot (3 tickets a day)", ref m)) { C.DoMini = m; changed = true; }
        var j = C.DoJumbo;
        if (ImGui.Checkbox("Jumbo Cactpot (collect winnings, then 3 tickets a week)", ref j)) { C.DoJumbo = j; changed = true; }
        var c = C.DoCards;
        if (ImGui.Checkbox("Sell Triple Triad cards from my bags for MGP", ref c)) { C.DoCards = c; changed = true; }
        var l = C.LearnCards;
        if (ImGui.Checkbox("First learn the ones I don't have yet", ref l)) { C.LearnCards = l; changed = true; }
        ImGui.TextColored(Dim, "Only cards you've learned are ever sold.");

        ImGui.Separator();
        var b = C.ShowButton;
        if (ImGui.Checkbox("Show the floating button", ref b)) { C.ShowButton = b; changed = true; }
        ImGui.SameLine();
        var lk = C.ButtonLocked;
        if (ImGui.Checkbox("Lock it", ref lk)) { C.ButtonLocked = lk; changed = true; }
        ImGui.SameLine();
        var al = C.ButtonAlways;
        if (ImGui.Checkbox("Also in combat / duties", ref al)) { C.ButtonAlways = al; changed = true; }

        ImGui.Separator();
        var s = plugin.Tracker.Me();
        if (s != null)
        {
            ImGui.TextUnformatted($"Mini Cactpot today: {s.MiniPlayed}/3.  Jumbo Cactpot this week: {s.JumboBought}/3, winnings {(s.JumboCashierVisited ? "checked" : "not checked")}.");
            ImGui.TextColored(Dim, "Counted when the games' windows open. Played before installing this? Fix it here:");
            if (ImGui.SmallButton("Mini done today")) plugin.Tracker.MarkMiniDone();
            ImGui.SameLine();
            if (ImGui.SmallButton("Jumbo done this week")) plugin.Tracker.MarkJumboDone();
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset counts")) plugin.Tracker.ResetToday();
        }
        ImGui.Separator();
        if (plugin.Runner.Busy) { if (ImGui.Button("Stop")) plugin.Runner.Stop(); }
        else if (ImGui.Button("Run now")) plugin.Runner.Start();
        ImGui.TextColored(Dim, "/saucer run | /saucer stop");
        if (changed) C.Save();
    }
}
