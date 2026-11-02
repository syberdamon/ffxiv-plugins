using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace MitPlanner.Windows;

/// <summary>Big "Rampart in 2.0 -> Stamp" callouts for planned cooldowns coming due.</summary>
public sealed class CalloutWindow : Window
{
    private static readonly Vector4 Soon = new(1f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 Now = new(0.35f, 1f, 0.45f, 1f);
    private static readonly Vector4 Late = new(1f, 0.35f, 0.30f, 1f);
    private static readonly Vector4 Done = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;

    public CalloutWindow(Plugin plugin) : base("Mit Planner Callouts###MPCallouts")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() => plugin.Tracker.Active.Count > 0 || plugin.ShowCalloutPlacement;

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar;
        if (plugin.Configuration.LockWindows) Flags |= ImGuiWindowFlags.NoMove;
        if (!plugin.ShowCalloutPlacement) Flags |= ImGuiWindowFlags.NoBackground;
        ShowCloseButton = false;
    }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(plugin.Configuration.CalloutScale);

        if (plugin.Tracker.Active.Count == 0)
        {
            ImGui.TextColored(Soon, "Callouts appear here (drag to place)");
            ImGui.SetWindowFontScale(1f);
            return;
        }

        foreach (var p in plugin.Tracker.Active)
        {
            var name = p.Ability?.Name ?? $"Action {p.Entry.ActionId}";
            if (p.Ability != null)
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(p.Ability.Icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(ImGui.GetTextLineHeight() * 1.3f));
                ImGui.SameLine();
            }

            string text;
            Vector4 color;
            if (p.Done) { text = p.AutoPresses > 0 ? $"{name}  (done, auto)" : $"{name}  (done)"; color = Done; }
            else if (p.NotReady) { text = $"{name}  still on cooldown!  ({p.Event.Label} in {Style.Clock(p.SecondsToHit)})"; color = Late; }
            else if (p.SecondsToPress > 0) { text = $"{name} in {Style.Clock(p.SecondsToPress)}  ->  {p.Event.Label}"; color = Soon; }
            else { text = $"USE {name} NOW  ->  {p.Event.Label} in {Style.Clock(p.SecondsToHit)}"; color = Now; }

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, text);
        }

        ImGui.SetWindowFontScale(1f);
    }
}
