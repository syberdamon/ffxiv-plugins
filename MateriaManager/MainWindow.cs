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
    private int keepAll;

    private FodderRule Rule(uint id)
    {
        if (!C.Rules.TryGetValue(id, out var r)) C.Rules[id] = r = new FodderRule();
        return r;
    }

    private void SetUse(IEnumerable<uint> ids, bool use)
    {
        foreach (var id in ids) { var r = Rule(id); r.Fodder = use; r.UserSet = true; }
        C.Save();
    }

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
        if (ImGui.BeginTabItem("Purge")) { DrawPurge(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ storage

    private void DrawStorage()
    {
        var s = plugin.Storage;
        var changed = false;

        ImGui.TextUnformatted("Saddlebag");
        var auto = C.AutoStoreSaddlebag;
        if (ImGui.Checkbox("Auto-store: opening the saddlebag moves materia from my bags into it", ref auto)) { C.AutoStoreSaddlebag = auto; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn off while melding. /materia store on|off");
        if (ImGui.Button("Pull all materia from the saddlebag")) s.PullAllFromSaddlebag();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Into your bags, for transmuting or the retainer. Held there: auto-store won't put it back. /materia pull");
        ImGui.SameLine();
        if (ImGui.Button("Store in saddlebag now")) s.StoreInSaddlebagNow();
        if (s.Holding)
        {
            ImGui.TextColored(Warn, "Bag materia is held for transmuting (auto-store to the saddlebag waits).");
            ImGui.SameLine(); if (ImGui.SmallButton("Release")) s.Release();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Pull by grade into my bags (melding: the top grades; clearing out: the low ones)");
        int gf = C.PullGradeFrom, gt = C.PullGradeTo;
        ImGui.SetNextItemWidth(110);
        if (ImGui.SliderInt("From grade", ref gf, 1, 12, MateriaData.Roman(gf))) { C.PullGradeFrom = gf; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(110);
        if (ImGui.SliderInt("to grade", ref gt, 1, 12, MateriaData.Roman(gt))) { C.PullGradeTo = gt; changed = true; }
        var ps = C.PullFromSaddlebag; var pr = C.PullFromRetainer;
        if (ImGui.Checkbox("from the saddlebag", ref ps)) { C.PullFromSaddlebag = ps; changed = true; }
        ImGui.SameLine();
        if (ImGui.Checkbox("from the open retainer", ref pr)) { C.PullFromRetainer = pr; changed = true; }
        ImGui.SameLine();
        if (ImGui.Button($"Pull {MateriaData.Roman(Math.Min(gf, gt))}-{MateriaData.Roman(Math.Max(gf, gt))}")) s.PullGrades(gf, gt);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Held in your bags afterwards (auto-store waits). /materia pull 11-12, /materia pull 1-6");

        ImGui.Separator();
        ImGui.TextUnformatted("Materia retainer");
        var retainers = Storage.Retainers();
        var names = new[] { "(none)" }.Concat(retainers.Select(r => r.Name)).ToArray();
        var idx = retainers.FindIndex(r => r.Id == C.RetainerId) + 1;
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Holds my materia", ref idx, names, names.Length))
        {
            C.RetainerId = idx == 0 ? 0 : retainers[idx - 1].Id;
            C.RetainerName = idx == 0 ? "" : retainers[idx - 1].Name;
            changed = true;
        }
        if (retainers.Count == 0) { ImGui.SameLine(); ImGui.TextColored(Dim, "(use a summoning bell once to list them)"); }
        var bal = C.AutoBalanceRetainer;
        if (ImGui.Checkbox("Opening that retainer's inventory balances it automatically", ref bal)) { C.AutoBalanceRetainer = bal; changed = true; }
        int min = C.DefaultMin, max = C.DefaultMax, grade = C.RetainerMinGrade;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Minimum of each", ref min, 50, 100)) { C.DefaultMin = Math.Max(0, min); changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Maximum of each", ref max, 50, 100)) { C.DefaultMax = Math.Max(0, max); changed = true; }
        ImGui.TextDisabled("Below the minimum: topped up to the maximum. Above the maximum: the extra comes back to your bags (for transmuting).");
        ImGui.SetNextItemWidth(110);
        if (ImGui.SliderInt("Only grades from", ref grade, 1, 12, MateriaData.Roman(grade))) { C.RetainerMinGrade = grade; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lower grades stay with you. Per-materia overrides are in the table below.");
        var fromSaddle = C.FillFromSaddlebag;
        if (ImGui.Checkbox("Also top up from the saddlebag when it's open (bags first)", ref fromSaddle)) { C.FillFromSaddlebag = fromSaddle; changed = true; }
        if (ImGui.Button("Balance now")) s.BalanceNow();
        ImGui.SameLine();
        if (s.Busy && ImGui.Button("Stop")) s.Stop();
        ImGui.TextDisabled(s.Status.Length > 0 ? s.Status : "Open the saddlebag or the materia retainer.");
        if (changed) C.Save();

        ImGui.Separator();
        var bags = MateriaData.Count(MateriaData.Bags);
        var saddleKnown = MateriaData.Loaded(MateriaData.Saddlebag);
        var saddle = MateriaData.Count(MateriaData.Saddlebag);
        var ret = plugin.Storage.MateriaRetainerOpen ? MateriaData.Count(MateriaData.Retainer) : C.RetainerSeen;
        ImGui.TextUnformatted($"Bags {bags.Values.Sum()}   Saddlebag {(saddleKnown ? saddle.Values.Sum().ToString() : "?")}   " +
                              $"{(C.RetainerName.Length > 0 ? C.RetainerName : "Retainer")} {ret.Values.Sum()}{(C.RetainerSeenAt is { } at && !plugin.Storage.MateriaRetainerOpen ? $" (as of {at:g})" : "")}");
        StorageTable(bags, saddle, ret);
    }

    private void StorageTable(Dictionary<uint, int> bags, Dictionary<uint, int> saddle, Dictionary<uint, int> ret)
    {
        var all = MateriaData.All();
        var ids = bags.Keys.Union(saddle.Keys).Union(ret.Keys).Union(C.RetainerRules.Where(r => r.Value.Custom).Select(r => r.Key))
            .Where(all.ContainsKey).Select(id => all[id]).OrderBy(m => m.Type).ThenByDescending(m => m.Grade).ToList();
        if (ids.Count == 0) { ImGui.TextColored(Dim, "No materia seen yet."); return; }
        if (!ImGui.BeginTable("##store", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(160, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Materia");
        ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Bags", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Saddle", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Own", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("Retainer target (min / max)", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableHeadersRow();
        foreach (var m in ids)
        {
            ImGui.PushID((int)m.ItemId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); IconText(m);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(MateriaData.Roman(m.Grade));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(bags.GetValueOrDefault(m.ItemId).ToString());
            ImGui.TableNextColumn(); ImGui.TextColored(Dim, saddle.GetValueOrDefault(m.ItemId).ToString());
            ImGui.TableNextColumn();
            var have = ret.GetValueOrDefault(m.ItemId);
            var target = C.TargetFor(m);
            ImGui.TextColored(target is { } tt && (have < tt.Min || have > tt.Max) ? Warn : Good, have.ToString());

            if (!C.RetainerRules.TryGetValue(m.ItemId, out var rule)) rule = new RetainerRule { Min = C.DefaultMin, Max = C.DefaultMax };
            ImGui.TableNextColumn();
            var custom = rule.Custom;
            if (ImGui.Checkbox("##own", ref custom)) { rule.Custom = custom; C.RetainerRules[m.ItemId] = rule; C.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Own target for this materia (instead of the defaults)");
            ImGui.TableNextColumn();
            if (!rule.Custom) ImGui.TextColored(Dim, target is { } d ? $"{d.Min} / {d.Max}" : "not kept");
            else
            {
                var skip = rule.Skip;
                if (ImGui.Checkbox("none##skip", ref skip)) { rule.Skip = skip; C.Save(); }
                if (!rule.Skip)
                {
                    int rmin = rule.Min, rmax = rule.Max;
                    ImGui.SameLine(); ImGui.SetNextItemWidth(50);
                    if (ImGui.InputInt("##min", ref rmin, 0, 0)) { rule.Min = Math.Max(0, rmin); C.Save(); }
                    ImGui.SameLine(); ImGui.SetNextItemWidth(50);
                    if (ImGui.InputInt("##max", ref rmax, 0, 0)) { rule.Max = Math.Max(0, rmax); C.Save(); }
                }
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static void IconText(MateriaInfo m)
    {
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(m.Icon)).GetWrapOrEmpty();
        ImGui.Image(tex.Handle, new Vector2(ImGui.GetTextLineHeight()));
        ImGui.SameLine(); ImGui.TextUnformatted(m.Type);
    }

    // ================================================================ transmute

    private void DrawTransmute()
    {
        ImGui.TextWrapped("Mutamix Bubblypots (Central Thanalan) trades 5 materia for 1 random one of a different type, of one of the grades you handed in; sometimes one grade higher. Tick which materia may be used and how many of each to keep. Only materia in your bags is used.");
        var same = C.SameGradeBatches;
        if (ImGui.Checkbox("Only batch five of the same grade (result is that grade or better)", ref same)) { C.SameGradeBatches = same; C.Save(); }
        ImGui.Checkbox("Show every materia (not just what's in my bags)", ref showAll);

        var bags = MateriaData.Count(MateriaData.Bags);
        var ids = (showAll ? MateriaData.All().Keys.Where(k => bags.ContainsKey(k) || C.Rules.ContainsKey(k)) : bags.Keys).ToList();
        if (ImGui.SmallButton("Use all")) SetUse(ids, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Use none")) SetUse(ids, false);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("##keepall", ref keepAll, 1, 5);
        ImGui.SameLine();
        if (ImGui.SmallButton("Set Keep for all")) { foreach (var id in ids) { var r = Rule(id); r.Keep = Math.Max(0, keepAll); } C.Save(); }
        Table(ids, id => (bags.GetValueOrDefault(id), 0), editable: true);

        ImGui.Separator();
        var plan = plugin.Transmuter.Plan();
        ImGui.TextUnformatted($"Planned: {plan.Count} batch(es) of five ({plan.Count * 5} materia in, {plan.Count} out), lowest grade first.");
        ImGui.TextDisabled("Lowest grade first (an upgrade can complete a batch of the next grade); smallest stacks first (frees bag slots). Updates as your bags change.");
        foreach (var g in plan.GroupBy(b => b.Grade))
            ImGui.BulletText($"Grade {MateriaData.Roman(g.Key)}: {g.Count()} batch(es) from " +
                string.Join(", ", g.SelectMany(b => b.Items).GroupBy(i => i.Type).Select(x => $"{x.Count()} {x.Key}")));
        if (plan.Count > 0 && plan.Count <= 6)
            for (var i = 0; i < plan.Count; i++)
                ImGui.TextDisabled($"  Batch {i + 1}: {string.Join(", ", plan[i].Items.Select(x => x.Name))}");

        var cap = C.MaxBatchesPerRun;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Batches per trip (safety cap)", ref cap, 1, 10)) { C.MaxBatchesPerRun = Math.Clamp(cap, 1, 999); C.Save(); }
        var roll = C.RollResults;
        if (ImGui.Checkbox("Keep rolling: use results at a grade I'm transmuting (unless I set that materia myself)", ref roll)) { C.RollResults = roll; C.Save(); }
        var reserve = C.ReserveForRetainer;
        if (ImGui.Checkbox("Don't use what my materia retainer still needs (up to its maximum)", ref reserve)) { C.ReserveForRetainer = reserve; C.Save(); }
        var t = plugin.Transmuter;
        if (t.Results.Count > 0)
            ImGui.TextDisabled($"This trip: {t.BatchesDone} batch(es); got {string.Join(", ", t.Results.GroupBy(r => r).Select(g => g.Count() > 1 ? $"{g.Count()}x {g.Key}" : g.Key))}");
        if (t.Busy) { if (ImGui.Button("Stop")) t.Stop(); }
        else
        {
            if (plan.Count == 0) ImGui.BeginDisabled();
            if (ImGui.Button("Go to Mutamix")) t.Start();
            if (plan.Count == 0) ImGui.EndDisabled();
        }
        ImGui.TextDisabled(t.Status);
        ImGui.TextDisabled("It goes to Mutamix, adds each batch through the items' menus, confirms, and repeats up to the cap. Stops (with a saved dump) on anything unexpected.");
    }

    // ================================================================ purge

    private void DrawPurge()
    {
        var p = plugin.Purger;
        ImGui.TextWrapped("Sells low-grade materia you have fewer than five of (not enough to transmute). Counts what's in your bags, saddlebag and materia retainer; only sells from your bags. Never sells materia you set a Keep or a retainer target for.");
        var changed = false;
        int below = C.PurgeBelowGrade, under = C.PurgeUnder;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Below grade", ref below, 2, 13, below > 12 ? "any" : MateriaData.Roman(below))) { C.PurgeBelowGrade = below; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Fewer than", ref under, 1, 1)) { C.PurgeUnder = Math.Clamp(under, 1, 999); changed = true; }
        var mix = C.PurgeSpareMixable;
        if (ImGui.Checkbox("Keep them if their grade still has five in total (they can be transmuted mixed)", ref mix)) { C.PurgeSpareMixable = mix; changed = true; }
        if (changed) C.Save();

        var list = p.Candidates();
        ImGui.TextUnformatted($"{list.Count} materia to sell ({list.Sum(x => x.InBags)} pieces).");
        if (ImGui.BeginTable("##purge", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(300, Math.Max(80, ImGui.GetContentRegionAvail().Y - 90)))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Materia"); ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 56); ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();
            foreach (var (m, inBags, owned) in list)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); IconText(m);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(MateriaData.Roman(m.Grade));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(inBags.ToString());
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, owned.ToString());
            }
            ImGui.EndTable();
        }
        if (p.Busy) { if (ImGui.Button("Stop")) p.Stop(); }
        else
        {
            if (list.Count == 0) ImGui.BeginDisabled();
            if (ImGui.Button("Go to the nearest vendor and sell")) p.GoToVendor();
            ImGui.SameLine();
            if (ImGui.Button("Sell here (shop or retainer open)")) p.SellHere();
            if (list.Count == 0) ImGui.EndDisabled();
        }
        ImGui.TextDisabled(p.Status.Length > 0 ? p.Status : "Materia in your saddlebag isn't sold: pull it into your bags first.");
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
                if (ImGui.Checkbox("##use", ref use)) { rule.Fodder = use; rule.UserSet = true; C.Rules[m.ItemId] = rule; C.Save(); }
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
