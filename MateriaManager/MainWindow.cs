using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace MateriaManager;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);
    private readonly Plugin plugin;
    private bool showAll;

    public MainWindow(Plugin plugin) : base("Materia Manager###MateriaManagerMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 420), MaximumSize = new Vector2(1400, 1400) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##mm")) return;
        if (ImGui.BeginTabItem("Storage")) { DrawStorage(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Transmute")) { DrawTransmute(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ storage

    private void DrawStorage()
    {
        var s = plugin.Storage;
        var auto = C.AutoStore;
        if (ImGui.Checkbox("Auto-store: move all materia out of my bags when I open storage", ref auto)) { C.AutoStore = auto; C.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn off while melding, so materia stays in your bags. /materia store on|off");
        var to = (int)C.StoreTo;
        ImGui.TextUnformatted("Store to:");
        ImGui.SameLine(); if (ImGui.RadioButton("Saddlebag", ref to, 0)) { C.StoreTo = StoreTo.Saddlebag; C.Save(); }
        ImGui.SameLine(); if (ImGui.RadioButton("Retainer (whichever you open)", ref to, 1)) { C.StoreTo = StoreTo.Retainer; C.Save(); }

        ImGui.Spacing();
        if (ImGui.Button("Store now")) s.StoreNow();
        ImGui.SameLine();
        if (ImGui.Button("Move saddlebag materia to a retainer")) s.SaddlebagToRetainer();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("With a retainer open: moves it straight over if the game allows.\nWith the saddlebag open: pulls it into your bags; the next retainer you open takes it.");
        if (s.HoldingForRetainer)
        {
            ImGui.TextColored(Warn, "Saddlebag materia is in your bags, waiting for a retainer (auto-store to the saddlebag is paused).");
            ImGui.SameLine(); if (ImGui.SmallButton("Cancel")) s.CancelHold();
        }
        ImGui.TextDisabled(s.Status.Length > 0 ? s.Status : "Open your saddlebag or a retainer's inventory.");
        ImGui.TextDisabled($"Saddlebag {(Storage.SaddlebagOpen ? "open" : "closed")}, retainer inventory {(Storage.RetainerOpen ? "open" : "closed")}.");

        ImGui.Separator();
        var bags = MateriaData.Count(MateriaData.Bags);
        var saddle = MateriaData.Count(MateriaData.Saddlebag);
        var saddleKnown = MateriaData.Loaded(MateriaData.Saddlebag);
        ImGui.TextUnformatted($"In your bags: {bags.Values.Sum()}   In your saddlebag: {(saddleKnown ? saddle.Values.Sum().ToString() : "? (open it once)")}");
        Table(bags.Keys.Union(saddle.Keys), id => (bags.GetValueOrDefault(id), saddle.GetValueOrDefault(id)), editable: false);
    }

    // ================================================================ transmute

    private void DrawTransmute()
    {
        ImGui.TextWrapped("Mutamix Bubblypots (Central Thanalan) trades 5 materia for 1 random one of a different type, of one of the grades you handed in; sometimes one grade higher. Tick which materia may be used and how many of each to keep. Only materia in your bags is used.");
        var same = C.SameGradeBatches;
        if (ImGui.Checkbox("Only batch five of the same grade (result is that grade or better)", ref same)) { C.SameGradeBatches = same; C.Save(); }
        ImGui.Checkbox("Show every materia (not just what's in my bags)", ref showAll);

        var bags = MateriaData.Count(MateriaData.Bags);
        var ids = showAll ? MateriaData.All().Keys.Where(k => bags.ContainsKey(k) || C.Rules.ContainsKey(k)) : bags.Keys;
        Table(ids, id => (bags.GetValueOrDefault(id), 0), editable: true);

        ImGui.Separator();
        var plan = plugin.Transmuter.Plan();
        ImGui.TextUnformatted($"Planned: {plan.Count} batch(es) of five ({plan.Count * 5} materia in, {plan.Count} out).");
        foreach (var g in plan.GroupBy(b => b.Grade))
            ImGui.BulletText($"Grade {MateriaData.Roman(g.Key)}: {g.Count()} batch(es) from " +
                string.Join(", ", g.SelectMany(b => b.Items).GroupBy(i => i.Type).Select(x => $"{x.Count()} {x.Key}")));
        if (plan.Count > 0 && plan.Count <= 6)
            for (var i = 0; i < plan.Count; i++)
                ImGui.TextDisabled($"  Batch {i + 1}: {string.Join(", ", plan[i].Items.Select(x => x.Name))}");

        var t = plugin.Transmuter;
        if (t.Busy) { if (ImGui.Button("Stop")) t.Stop(); }
        else
        {
            if (plan.Count == 0) ImGui.BeginDisabled();
            if (ImGui.Button("Go to Mutamix")) t.Start();
            if (plan.Count == 0) ImGui.EndDisabled();
        }
        ImGui.TextDisabled(t.Status);
        ImGui.TextDisabled("First version: it takes you there and opens the window; you hand in the batches. The window's layout is saved so the hand-in can be automated next.");
    }

    // ================================================================ table

    private void Table(System.Collections.Generic.IEnumerable<uint> ids, Func<uint, (int Bags, int Saddle)> counts, bool editable)
    {
        var all = MateriaData.All();
        var rows = ids.Where(all.ContainsKey).Select(id => all[id]).OrderBy(m => m.Type).ThenByDescending(m => m.Grade).ToList();
        if (rows.Count == 0) { ImGui.TextColored(Dim, "No materia here."); return; }
        var cols = editable ? 5 : 4;
        if (!ImGui.BeginTable("##mat", cols, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(ImGui.GetContentRegionAvail().Y - (editable ? 120 : 0), 420)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Materia");
        ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Bags", ImGuiTableColumnFlags.WidthFixed, 44);
        if (editable) { ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 34); ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 90); }
        else ImGui.TableSetupColumn("Saddlebag", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();
        foreach (var m in rows)
        {
            var (b, sb) = counts(m.ItemId);
            ImGui.PushID((int)m.ItemId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(m.Icon)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine(); ImGui.TextUnformatted(m.Type);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(MateriaData.Roman(m.Grade));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(b.ToString());
            if (editable)
            {
                if (!C.Rules.TryGetValue(m.ItemId, out var rule)) rule = new FodderRule();
                ImGui.TableNextColumn();
                var use = rule.Fodder;
                if (ImGui.Checkbox("##use", ref use)) { rule.Fodder = use; C.Rules[m.ItemId] = rule; C.Save(); }
                ImGui.TableNextColumn();
                var keep = rule.Keep;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##keep", ref keep, 1, 5)) { rule.Keep = Math.Max(0, keep); C.Rules[m.ItemId] = rule; C.Save(); }
            }
            else { ImGui.TableNextColumn(); ImGui.TextColored(sb > 0 ? Good : Dim, sb.ToString()); }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
}
