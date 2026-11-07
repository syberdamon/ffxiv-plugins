using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RaiseCallouts;

public sealed class ConfigWindow : Window
{
    private readonly Configuration c;

    public ConfigWindow(Configuration config) : base("Raise Callouts###RaiseCalloutsConfig")
    {
        c = config;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 260), MaximumSize = new Vector2(900, 700) };
    }

    public override void Draw()
    {
        var changed = false;
        var on = c.Enabled;
        if (ImGui.Checkbox("Enabled", ref on)) { c.Enabled = on; changed = true; }

        var ch = (int)c.Channel;
        ImGui.TextUnformatted("Post to:");
        ImGui.SameLine(); if (ImGui.RadioButton("Party chat", ref ch, 0)) { c.Channel = Channel.Party; changed = true; }
        ImGui.SameLine(); if (ImGui.RadioButton("Echo (only you see it)", ref ch, 1)) { c.Channel = Channel.Echo; changed = true; }

        var t = c.Template;
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("Message", ref t, 200)) { c.Template = t; changed = true; }
        ImGui.TextDisabled("{target} = who, {spell} = the raise, {how} = \" (instant)\" for Swiftcast/Dualcast raises");
        ImGui.TextDisabled("Preview: " + Announcer.Format(c.Template, "Healer Name", "Raise", false));

        var party = c.OnlyInParty;
        if (ImGui.Checkbox("Only when I'm in a party", ref party)) { c.OnlyInParty = party; changed = true; }
        var duty = c.DutiesOnly;
        if (ImGui.Checkbox("Only in duties", ref duty)) { c.DutiesOnly = duty; changed = true; }
        var skip = c.SkipIfAlreadyRaised;
        if (ImGui.Checkbox("Say nothing if someone else's raise is already on them", ref skip)) { c.SkipIfAlreadyRaised = skip; changed = true; }
        var delay = c.CastDelayMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Wait into the cast (ms)", ref delay, 0, 1500)) { c.CastDelayMs = delay; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A cast you cancel before this says nothing.");

        if (changed) c.Save();
    }
}
