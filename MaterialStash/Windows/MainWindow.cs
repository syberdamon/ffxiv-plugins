using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;
    private string allaganNote = "";

    public MainWindow(Plugin plugin) : base("Material Stash###MaterialStashMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 420), MaximumSize = new Vector2(1400, 1400) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        var me = plugin.Tracker.Me();
        if (me == null) { ImGui.TextUnformatted("Log in first."); return; }
        if (me.Order.Count == 0)
        {
            ImGui.TextColored(Warn, "Use a summoning bell once so the plugin can read your retainer list.");
            return;
        }
        if (plugin.Runner.Status.Length > 0) ImGui.TextColored(Dim, plugin.Runner.Status);
        if (!ImGui.BeginTabBar("##stash")) return;
        if (ImGui.BeginTabItem("Bags now")) { DrawBags(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Retainers")) { DrawRetainers(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Categories")) { DrawCategories(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Crystals")) { DrawCrystals(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Stock")) { DrawStock(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Selling")) { DrawSelling(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Clean-up")) { DrawCleanup(me); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Keep on me")) { DrawKeep(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Rules")) { DrawRules(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ bags (preview)

    private void DrawBags(CharacterData me)
    {
        ImGui.TextWrapped("Everything in your bags and what Stash would do with it. \"Keep\" puts an item on the Keep on me list.");
        var rows = Tracker.Stacks(Tracker.Bags).GroupBy(s => s.ItemId).Select(g => (Id: g.Key, Qty: g.Sum(s => s.Qty))).ToList();
        var changed = false;
        if (ImGui.BeginTable("##bags", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new Vector2(0, 0)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var (id, qty) in rows.OrderByDescending(r => Items.Depositable(r.Id, C, me)).ThenBy(r => Items.Info(r.Id)?.Category).ThenBy(r => Items.Info(r.Id)?.Name))
            {
                if (Items.IsCrystal(id)) continue;
                var info = Items.Info(id);
                if (info == null) continue;
                var nq = id % 1_000_000;
                var dep = Items.Depositable(id, C, me);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(info.Name + (id >= 1_000_000 ? " (HQ)" : ""));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{info.Category} ({C.GroupOf(info.CategoryId)?.Name ?? "no group"})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(qty.ToString("N0"));
                ImGui.TableNextColumn();
                var group = C.GroupOf(info.CategoryId);
                var why = C.KeepOnMe.Contains(nq) ? "keep on me"
                    : me.TidyCarry.Contains(nq) ? "carried by a tidy"
                    : C.Include.Contains(nq) ? "always stash (you)"
                    : Items.Stashable(id, C) ? info.Why
                    : group == null ? "no group"
                    : group.Name == Configuration.MaterialsGroup ? info.Why
                    : dep ? $"{group.Name}: stash from bags" : $"{group.Name}: stays (switch off)";
                ImGui.TextColored(dep ? Good : Dim, why);
                ImGui.TableNextColumn();
                if (dep)
                {
                    var to = me.TidyCarry.Contains(nq) ? plugin.Planner.TidyTarget(id, me) ?? plugin.Planner.Best(id) : plugin.Planner.Best(id);
                    if (to is { } t) ImGui.TextUnformatted(plugin.Tracker.NameOf(t));
                    else ImGui.TextColored(Warn, "no room / no home");
                }
                else ImGui.TextColored(Dim, "stays");
                ImGui.TableNextColumn();
                ImGui.PushID((int)id);
                if (C.KeepOnMe.Contains(nq)) { if (ImGui.SmallButton("Unkeep")) { C.KeepOnMe.Remove(nq); changed = true; } }
                else
                {
                    if (ImGui.SmallButton("Keep")) { C.KeepOnMe.Add(nq); C.Include.Remove(nq); changed = true; }
                    if (!dep) { ImGui.SameLine(); if (ImGui.SmallButton("Stash it")) { C.Include.Add(nq); changed = true; } }
                    else if (C.Include.Contains(nq)) { ImGui.SameLine(); if (ImGui.SmallButton("Undo")) { C.Include.Remove(nq); changed = true; } }
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (changed) C.Save();
    }

    // ================================================================ keep on me

    private string keepSearch = "";
    private List<(uint Id, string Name)> keepHits = new();

    private static List<(uint Id, string Name)> SearchAnyItem(string text)
    {
        var list = new List<(uint, string)>();
        var t = text.Trim();
        if (t.Length < 2) return list;
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>())
        {
            if (row.RowId == 0) continue;
            var n = row.Name.ExtractText();
            if (n.Length > 0 && n.Contains(t, StringComparison.OrdinalIgnoreCase)) list.Add((row.RowId, n));
            if (list.Count >= 25) break;
        }
        return list.OrderBy(x => x.Item2.Length).ToList();
    }

    private void DrawKeep()
    {
        ImGui.TextWrapped("Never taken out of your bags by Stash, a tidy or the saddlebag buttons: raid food, potions, dark matter, " +
                          "anything you want to hand. (Retainers can still hold them; this is only about your bags.)");
        var changed = false;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##keepsearch", "Add an item: type a name...", ref keepSearch, 64)) keepHits = SearchAnyItem(keepSearch);
        foreach (var (id, name) in keepHits.ToList())
            if (ImGui.Selectable($"{name}##kh{id}")) { C.KeepOnMe.Add(id); C.Include.Remove(id); keepHits.Clear(); keepSearch = ""; changed = true; }
        ImGui.Separator();
        foreach (var id in C.KeepOnMe.OrderBy(i => Items.Info(i)?.Name).ToList())
        {
            ImGui.PushID((int)id);
            ImGui.BulletText(Items.Info(id)?.Name ?? $"#{id}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) { C.KeepOnMe.Remove(id); changed = true; }
            ImGui.PopID();
        }
        if (C.KeepOnMe.Count == 0) ImGui.TextColored(Dim, "Nothing yet. The \"Keep\" button on the Bags now tab adds items too.");
        if (changed) C.Save();
    }

    // ================================================================ retainers

    private void DrawRetainers(CharacterData me)
    {
        var changed = false;
        ImGui.TextWrapped("Records are refreshed whenever a retainer's inventory is open. \"Scan all\" above the retainer list opens each one once.");
        if (ImGui.BeginTable("##ret", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Slots used", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Mats held", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableSetupColumn("Never use", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Keep its ventures", ImGuiTableColumnFlags.WidthFixed, 115);
            ImGui.TableHeadersRow();
            foreach (var id in me.Order)
            {
                var s = me.Retainers[id];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Name);
                ImGui.TableNextColumn();
                if (s.UsedSlots < 0) ImGui.TextColored(Dim, "?");
                else ImGui.TextColored(s.UsedSlots >= Tracker.RetainerSlots - C.ReserveSlots ? Warn : Good, $"{s.UsedSlots}/{Tracker.RetainerSlots}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Items.Keys.Count(k => Items.Stashable(k, C)).ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(s.Seen == null ? Warn : Dim, s.Seen == null ? "never" : $"{s.Seen:g} ({(s.Source == "allagan" ? "Allagan" : "opened")})");
                ImGui.TableNextColumn();
                var ex = me.Excluded.Contains(id);
                ImGui.PushID($"ex{id}");
                if (ImGui.Checkbox("##ex", ref ex)) { if (ex) me.Excluded.Add(id); else me.Excluded.Remove(id); changed = true; }
                ImGui.PopID();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Never stash on this retainer (e.g. your materia retainer).");
                ImGui.TableNextColumn();
                var nr = me.NoRedirect.Contains(id);
                ImGui.PushID($"nr{id}");
                if (ImGui.Checkbox("##nr", ref nr)) { if (nr) me.NoRedirect.Add(id); else me.NoRedirect.Remove(id); changed = true; }
                ImGui.PopID();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Never redirect this retainer's AutoRetainer ventures for stock\n(e.g. one you're levelling or keep on field explorations).");
            }
            ImGui.EndTable();
        }

        if (plugin.Allagan.IsReady)
        {
            if (ImGui.Button("Fill in unseen retainers from Allagan Tools"))
            {
                var n = plugin.Tracker.SeedFromAllagan(plugin.Allagan);
                allaganNote = n > 0 ? $"Seeded {n} retainer(s). Slot counts stay unknown until each is opened." : "Nothing seeded (all seen already, or Allagan Tools didn't answer).";
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Their item lists, so 'keep together' works before you've opened them.\nSlots used stay '?' until opened.");
            if (allaganNote.Length > 0) { ImGui.SameLine(); ImGui.TextColored(Dim, allaganNote); }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Overflow order");
        ImGui.TextColored(Dim, "Where anything goes once its category homes are full, or if it has none. Leave a retainer out to never overflow there.");
        if (OrderedList("overflow", me.Overflow, me)) changed = true;

        ImGui.Separator();
        var keep = C.KeepTogether;
        if (ImGui.Checkbox("Keep each item together: retainers already holding it come first", ref keep)) { C.KeepTogether = keep; changed = true; }
        var reserve = C.ReserveSlots;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Leave this many slots free on every retainer", ref reserve, 1, 5)) { C.ReserveSlots = Math.Clamp(reserve, 0, 170); changed = true; }

        ImGui.Separator();
        var show = C.ShowBellButtons;
        if (ImGui.Checkbox("Show the Stash / Scan buttons above the retainer list", ref show)) { C.ShowBellButtons = show; changed = true; }
        var showS = C.ShowSaddlebagButtons;
        if (ImGui.Checkbox("Show the mats -> saddlebag / -> bags buttons above the saddlebag", ref showS)) { C.ShowSaddlebagButtons = showS; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("/stash saddle in | /stash saddle out");
        float ox = C.BellOffsetX, oy = C.BellOffsetY;
        ImGui.SetNextItemWidth(110);
        if (ImGui.DragFloat("Nudge X", ref ox, 1f)) { C.BellOffsetX = ox; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(110);
        if (ImGui.DragFloat("Nudge Y", ref oy, 1f)) { C.BellOffsetY = oy; changed = true; }
        var vw = C.ShowVentureWindow;
        if (ImGui.Checkbox("Venture timer window (ready count, next venture)", ref vw)) { C.ShowVentureWindow = vw; changed = true; }
        ImGui.SameLine();
        var va = C.VentureWindowAlways;
        if (ImGui.Checkbox("also in combat / duties", ref va)) { C.VentureWindowAlways = va; changed = true; }
        var ts = C.TidySort;
        if (ImGui.Checkbox("After tidying a retainer, run the game's item sort on it", ref ts)) { C.TidySort = ts; changed = true; }
        var laps = C.TidyMaxLaps;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Most laps a tidy may take", ref laps, 1, 1)) { C.TidyMaxLaps = Math.Clamp(laps, 1, 10); changed = true; }
        var delay = C.StepDelay;
        ImGui.SetNextItemWidth(110);
        if (ImGui.SliderFloat("Seconds between moves", ref delay, 0.2f, 1.5f, "%.1f")) { C.StepDelay = delay; changed = true; }
        if (changed) C.Save();
    }

    /// <summary>An ordered list of retainers with up/down/remove, and a picker to add one. True if changed.</summary>
    private bool OrderedList(string key, List<ulong> list, CharacterData me)
    {
        var changed = false;
        ImGui.PushID(key);
        for (var i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.TextUnformatted($"{i + 1}. {plugin.Tracker.NameOf(list[i])}" + (me.Excluded.Contains(list[i]) ? " (never use)" : ""));
            ImGui.SameLine(220);
            if (ImGui.SmallButton("Up") && i > 0) { (list[i - 1], list[i]) = (list[i], list[i - 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i < list.Count - 1) { (list[i + 1], list[i]) = (list[i], list[i + 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) { list.RemoveAt(i); changed = true; ImGui.PopID(); break; }
            ImGui.PopID();
        }
        var missing = me.Order.Where(id => !list.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##add", "Add a retainer..."))
            {
                foreach (var id in missing)
                    if (ImGui.Selectable(plugin.Tracker.NameOf(id))) { list.Add(id); changed = true; }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Add all (list order)")) { list.AddRange(missing); changed = true; }
        }
        ImGui.PopID();
        return changed;
    }

    // ================================================================ categories

    private string catFilter = "", pinSearch = "";
    private List<(uint Id, string Name)> pinHits = new();
    private List<(uint Id, string Name)>? allCats;

    private List<(uint Id, string Name)> AllCategories() =>
        allCats ??= Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ItemUICategory>()
            .Where(c => c.RowId != 0 && c.RowId != 59 && c.Name.ExtractText().Length > 0)
            .Select(c => (c.RowId, c.Name.ExtractText())).OrderBy(c => c.Item2).ToList();

    private string CatName(uint id) => AllCategories().FirstOrDefault(c => c.Id == id).Name ?? $"#{id}";

    private string Summary(List<ulong>? homes) => homes == null || homes.Count == 0 ? "no home" : string.Join(", ", homes.Select(plugin.Tracker.NameOf));

    private void DrawCategories(CharacterData me)
    {
        ImGui.TextWrapped("Where things live, most specific first: item pins, then name rules, then the game category, then its group, " +
                          "then the overflow order (Retainers tab). Stash and Tidy both follow this.");
        var changed = false;
        if (!ImGui.BeginChild("##cats")) { ImGui.EndChild(); return; }

        // ---- groups
        if (ImGui.CollapsingHeader("Groups", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var g in C.Groups.ToList())
            {
                me.GroupHomes.TryGetValue(g.Name, out var homes);
                var flags = $"{(g.StashFromBags ? "stash from bags" : "bags: stays")}{(g.Tidy ? "" : ", no tidy")}";
                if (!ImGui.TreeNode($"{g.Name} -> {Summary(homes)}   ({flags})###grp{g.Name}")) continue;
                var sfb = g.StashFromBags;
                if (ImGui.Checkbox(g.Name == Configuration.MaterialsGroup ? "Stash from my bags (by the material rules, Rules tab)" : "Stash from my bags too", ref sfb)) { g.StashFromBags = sfb; changed = true; }
                ImGui.SameLine();
                var td = g.Tidy;
                if (ImGui.Checkbox("Tidy may move these", ref td)) { g.Tidy = td; changed = true; }
                homes ??= new List<ulong>();
                if (OrderedList($"g{g.Name}", homes, me)) { if (homes.Count == 0) me.GroupHomes.Remove(g.Name); else me.GroupHomes[g.Name] = homes; changed = true; }
                if (ImGui.TreeNode($"Game categories in it ({g.Categories.Count})###gc{g.Name}"))
                {
                    foreach (var cid in g.Categories.OrderBy(CatName).ToList())
                    {
                        ImGui.PushID((int)cid);
                        ImGui.BulletText(CatName(cid)); ImGui.SameLine();
                        if (ImGui.SmallButton("Remove")) { g.Categories.Remove(cid); changed = true; }
                        ImGui.PopID();
                    }
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.BeginCombo("##addcat", "Add a category (moves it here)..."))
                    {
                        foreach (var (cid, name) in AllCategories().Where(c => !g.Categories.Contains(c.Id)))
                            if (ImGui.Selectable($"{name}  ({C.GroupOf(cid)?.Name ?? "no group"})##ac{cid}"))
                            {
                                foreach (var other in C.Groups) other.Categories.Remove(cid);
                                g.Categories.Add(cid); changed = true;
                            }
                        ImGui.EndCombo();
                    }
                    ImGui.TreePop();
                }
                if (g.Name != Configuration.MaterialsGroup && ImGui.SmallButton("Delete this group"))
                {
                    C.Groups.Remove(g); me.GroupHomes.Remove(g.Name); changed = true;
                }
                ImGui.TreePop();
            }
            if (ImGui.SmallButton("New group"))
            {
                var n = 1;
                while (C.Groups.Any(x => x.Name == $"Group {n}")) n++;
                C.Groups.Add(new CategoryGroup { Name = $"Group {n}" });
                changed = true;
            }
        }

        // ---- game categories
        if (ImGui.CollapsingHeader("Game categories"))
        {
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##catfilter", "Filter (e.g. Rug, Head, Miscellany)", ref catFilter, 40);
            var inBags = Tracker.Stacks(Tracker.Bags).Select(x => Items.Info(x.ItemId)?.CategoryId ?? 0).ToHashSet();
            foreach (var (cid, name) in AllCategories())
            {
                if (catFilter.Length > 0 && !name.Contains(catFilter, StringComparison.OrdinalIgnoreCase)) continue;
                me.CategoryHomes.TryGetValue(cid, out var homes);
                var label = $"{name} -> {(homes is { Count: > 0 } ? Summary(homes) : $"group: {C.GroupOf(cid)?.Name ?? "none"}")}{(inBags.Contains(cid) ? "   [in your bags]" : "")}###cat{cid}";
                if (!ImGui.TreeNode(label)) continue;
                homes ??= new List<ulong>();
                if (OrderedList($"cat{cid}", homes, me)) { if (homes.Count == 0) me.CategoryHomes.Remove(cid); else me.CategoryHomes[cid] = homes; changed = true; }
                ImGui.TreePop();
            }
        }

        // ---- name rules
        if (ImGui.CollapsingHeader($"Name rules ({me.NameRules.Count})###namerules"))
        {
            ImGui.TextColored(Dim, "Items whose name contains the text go to these retainers (e.g. \"Totem\"). First matching rule wins.");
            for (var i = 0; i < me.NameRules.Count; i++)
            {
                var rule = me.NameRules[i];
                ImGui.PushID($"nr{i}");
                var text = rule.Text;
                ImGui.SetNextItemWidth(160);
                if (ImGui.InputText("##t", ref text, 40)) { rule.Text = text; changed = true; }
                ImGui.SameLine(); ImGui.TextColored(Dim, $"-> {Summary(rule.Homes)}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { me.NameRules.RemoveAt(i); changed = true; ImGui.PopID(); break; }
                ImGui.Indent();
                if (OrderedList($"nrl{i}", rule.Homes, me)) changed = true;
                ImGui.Unindent();
                ImGui.PopID();
            }
            if (ImGui.SmallButton("Add a name rule")) { me.NameRules.Add(new NameRule { Text = "Totem" }); changed = true; }
        }

        // ---- item pins
        if (ImGui.CollapsingHeader($"Item pins ({me.ItemPins.Count})###pins"))
        {
            ImGui.TextColored(Dim, "One item always goes to these retainers (usually just one; a second is the backup when the first is full).");
            foreach (var (id, homes) in me.ItemPins.OrderBy(p => Items.Info(p.Key)?.Name).ToList())
            {
                ImGui.PushID($"pin{id}");
                var open = ImGui.TreeNode($"{Items.Info(id)?.Name ?? $"#{id}"} -> {Summary(homes)}###p");
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { me.ItemPins.Remove(id); changed = true; if (open) ImGui.TreePop(); ImGui.PopID(); break; }
                if (open)
                {
                    if (OrderedList($"pl{id}", homes, me)) changed = true;
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputTextWithHint("##pinsearch", "Pin an item: type a name...", ref pinSearch, 64)) pinHits = SearchAnyItem(pinSearch);
            foreach (var (id, name) in pinHits.ToList())
                if (ImGui.Selectable($"{name}##ph{id}")) { me.ItemPins.TryAdd(id, new List<ulong>()); pinHits.Clear(); pinSearch = ""; changed = true; }
        }

        ImGui.EndChild();
        if (changed) C.Save();
    }

    // ================================================================ crystals

    private void DrawCrystals(CharacterData me)
    {
        var changed = false;
        var on = C.IncludeCrystals;
        if (ImGui.Checkbox("Stash crystals, shards and clusters", ref on)) { C.IncludeCrystals = on; changed = true; }
        ImGui.TextColored(Dim, $"Retainers fill in the bell's list order, each up to {Items.CrystalCap:N0} of a type before the next (\"never use\" retainers skipped).");
        var keep = C.CrystalKeep;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Keep this many of each on me", ref keep, 100, 1000)) { C.CrystalKeep = Math.Clamp(keep, 0, Items.CrystalCap); changed = true; }
        var wd = C.CrystalWithdrawToKeep;
        if (ImGui.Checkbox("Top me back up to that from the retainers when I'm short", ref wd)) { C.CrystalWithdrawToKeep = wd; changed = true; }

        var mine = Tracker.Crystals(InventoryType.Crystals);
        if (ImGui.BeginTable("##cry", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Crystal");
            ImGui.TableSetupColumn("On me", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("On retainers", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Own keep", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            for (var c = Items.FirstCrystal; c <= Items.LastCrystal; c++)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)c);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Items.Info(c)?.Name ?? $"#{c}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted((mine.TryGetValue(c, out var m) ? m.Qty : 0).ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(me.Retainers.Values.Sum(s => s.Crystals.GetValueOrDefault(c)).ToString("N0"));
                ImGui.TableNextColumn();
                var custom = C.CrystalKeepPer.ContainsKey(c);
                if (ImGui.Checkbox("##own", ref custom))
                {
                    if (custom) C.CrystalKeepPer[c] = C.CrystalKeep; else C.CrystalKeepPer.Remove(c);
                    changed = true;
                }
                ImGui.TableNextColumn();
                if (custom)
                {
                    var k = C.CrystalKeepPer[c];
                    ImGui.SetNextItemWidth(100);
                    if (ImGui.InputInt("##k", ref k, 0, 0)) { C.CrystalKeepPer[c] = Math.Clamp(k, 0, Items.CrystalCap); changed = true; }
                }
                else ImGui.TextColored(Dim, C.CrystalKeep.ToString("N0"));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (changed) C.Save();
    }

    // ================================================================ stock

    private string itemSearch = "", recipeSearch = "";
    private List<ItemInfo> itemHits = new();
    private List<RecipeInfo> recipeHits = new();
    private List<StockNeed> needs = new();
    private Dictionary<uint, string> sources = new();
    private DateTime nextNeeds = DateTime.MinValue;

    private void RefreshNeeds(CharacterData me)
    {
        if (DateTime.UtcNow < nextNeeds) return;
        nextNeeds = DateTime.UtcNow.AddSeconds(2);
        needs = Stock.Needs(C, me, plugin.Director.Incoming());
        var retainers = Director.Retainers();
        var stats = retainers.ToDictionary(r => r.Id, r => plugin.Director.Stats(r.Name));
        sources = new();
        foreach (var n in needs)
        {
            var tasks = Ventures.For(n.ItemId);
            if (tasks.Count == 0) { sources[n.ItemId] = "no venture: craft/buy/gather yourself"; continue; }
            var who = retainers.Where(r => !me.NoRedirect.Contains(r.Id) && tasks.Any(t => Director.CantTake(t, r, stats[r.Id]) == null)).Select(r => r.Name).ToList();
            var kinds = string.Join(", ", tasks.Select(t => $"{Ventures.CategoryName(t.Category)} {t.RetainerLevel}").Distinct());
            if (who.Count > 0) sources[n.ItemId] = $"{kinds}: {string.Join(", ", who)}";
            else
            {
                var reasons = retainers.SelectMany(r => tasks.Select(t => Director.CantTake(t, r, stats[r.Id]))).Where(x => x != null && x != "wrong class").Distinct().Take(2);
                sources[n.ItemId] = $"{kinds}: no retainer can yet" + (reasons.Any() ? $" ({string.Join("; ", reasons)})" : " (no retainer of that class)");
            }
        }
    }

    private void DrawStock(CharacterData me)
    {
        var changed = false;
        var redirect = C.RedirectVentures;
        if (ImGui.Checkbox("When AutoRetainer sends a retainer out, send it for the most-needed stock item it can bring back", ref redirect)) { C.RedirectVentures = redirect; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Uses AutoRetainer's venture override for that one send. Nothing short (or nothing this retainer can bring back)\nand AutoRetainer does what it would anyway. Opt retainers out on the Retainers tab.");
        var announce = C.AnnounceRedirects;
        ImGui.SameLine();
        if (ImGui.Checkbox("say so in chat", ref announce)) { C.AnnounceRedirects = announce; changed = true; }
        string[] modes = { "Spread evenly (lowest % of target first)", "Closest to its target first" };
        ImGui.SetNextItemWidth(280);
        if (ImGui.BeginCombo("Once every minimum is met", modes[Math.Clamp(C.AfterMinimumMode, 0, 1)]))
        {
            for (var i = 0; i < modes.Length; i++)
                if (ImGui.Selectable(modes[i], C.AfterMinimumMode == i)) { C.AfterMinimumMode = i; changed = true; }
            ImGui.EndCombo();
        }
        ImGui.TextColored(Dim, "Below-minimum items always go first, the furthest below (as a % of the minimum) first.\n" +
            $"Counts: bags + saddlebag (seen {(me.SaddlebagSeen?.ToString("g") ?? "never: open it once")}) + retainers as last seen, NQ and HQ together; plus ventures already out.");

        RefreshNeeds(me);
        ImGui.Separator();
        if (needs.Count > 0 && ImGui.BeginTable("##need", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new Vector2(0, Math.Min(32 + needs.Count * 24, 300))))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Coming", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Min", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Ventures (who can go)");
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var n in needs)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(n.BelowMin ? new Vector4(1f, 0.45f, 0.45f, 1f) : n.BelowTarget ? Warn : Good, n.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(n.Have.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, n.Incoming > 0 ? $"+{n.Incoming:N0}" : "");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(n.Min.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(n.Crystal && n.Target >= (me.Order.Count + 1) * Items.CrystalCap ? "no limit" : n.Target.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, sources.GetValueOrDefault(n.ItemId, ""));
            }
            ImGui.EndTable();
        }
        else if (needs.Count == 0) ImGui.TextColored(Dim, "Nothing on the stock list yet: add items or recipes below.");

        // ---- crystals
        if (ImGui.CollapsingHeader("Crystals, shards, clusters###stockcrystals"))
        {
            var on = C.CrystalStockOn;
            if (ImGui.Checkbox("Venture for crystals too", ref on)) { C.CrystalStockOn = on; changed = true; }
            ImGui.TextColored(Dim, "Counted across your crystal tab and every retainer's, combined. Below its minimum a type competes with the\n" +
                "other short items; above it, crystals come last of all, in the tier order below. Target 0 = no limit\n" +
                "(until every crystal tab is full). Crystals your recipes or items ask for are added on top.");
            if (on)
            {
                C.TierOrder();
                ImGui.TextUnformatted("Order once minimums are met:");
                for (var i = 0; i < C.CrystalTierOrder.Count; i++)
                {
                    var tier = C.CrystalTierOrder[i];
                    ImGui.PushID($"tier{i}");
                    ImGui.TextUnformatted($"{i + 1}. {Items.CrystalTierNames[tier]}");
                    ImGui.SameLine(130);
                    if (ImGui.SmallButton("Up") && i > 0) { (C.CrystalTierOrder[i - 1], C.CrystalTierOrder[i]) = (C.CrystalTierOrder[i], C.CrystalTierOrder[i - 1]); changed = true; }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Down") && i < C.CrystalTierOrder.Count - 1) { (C.CrystalTierOrder[i + 1], C.CrystalTierOrder[i]) = (C.CrystalTierOrder[i], C.CrystalTierOrder[i + 1]); changed = true; }
                    ImGui.SameLine(260);
                    var t = C.CrystalTiers[tier];
                    int mn = t.Min, tg = t.Target;
                    ImGui.SetNextItemWidth(90);
                    if (ImGui.InputInt("min each", ref mn, 0, 0)) { t.Min = Math.Max(0, mn); changed = true; }
                    ImGui.SameLine(); ImGui.SetNextItemWidth(90);
                    if (ImGui.InputInt("target each", ref tg, 0, 0)) { t.Target = Math.Max(0, tg); changed = true; }
                    ImGui.PopID();
                }
                if (ImGui.TreeNode("One element with its own numbers###crystalown"))
                {
                    for (var id = Items.FirstCrystal; id <= Items.LastCrystal; id++)
                    {
                        ImGui.PushID((int)id);
                        var own = C.CrystalStockPer.ContainsKey(id);
                        if (ImGui.Checkbox($"{Items.Info(id)?.Name}##own", ref own))
                        {
                            if (own) { var b = C.CrystalTiers[Items.CrystalTier(id)]; C.CrystalStockPer[id] = new CrystalStock { Min = b.Min, Target = b.Target }; }
                            else C.CrystalStockPer.Remove(id);
                            changed = true;
                        }
                        if (own)
                        {
                            var cs = C.CrystalStockPer[id];
                            int mn = cs.Min, tg = cs.Target;
                            ImGui.SameLine(200); ImGui.SetNextItemWidth(90);
                            if (ImGui.InputInt("min", ref mn, 0, 0)) { cs.Min = Math.Max(0, mn); changed = true; }
                            ImGui.SameLine(); ImGui.SetNextItemWidth(90);
                            if (ImGui.InputInt("target", ref tg, 0, 0)) { cs.Target = Math.Max(0, tg); changed = true; }
                        }
                        ImGui.PopID();
                    }
                    ImGui.TreePop();
                }
            }
        }

        // ---- items
        if (ImGui.CollapsingHeader($"Items ({C.StockItems.Count})###stockitems", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var it in C.StockItems.ToList())
            {
                ImGui.PushID($"si{it.ItemId}");
                ImGui.TextUnformatted(Items.Info(it.ItemId)?.Name ?? $"#{it.ItemId}");
                ImGui.SameLine(240);
                int mn = it.Min, tg = it.Target;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("min", ref mn, 0, 0)) { it.Min = Math.Max(0, mn); if (it.Target < it.Min) it.Target = it.Min; changed = true; }
                ImGui.SameLine(); ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("target", ref tg, 0, 0)) { it.Target = Math.Max(it.Min, tg); changed = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { C.StockItems.Remove(it); changed = true; }
                ImGui.PopID();
            }
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputTextWithHint("##itemsearch", "Add an item: type a name...", ref itemSearch, 64))
                itemHits = Stock.SearchItems(itemSearch).Where(i => C.StockItems.All(x => x.ItemId != i.ItemId)).ToList();
            foreach (var hit in itemHits.ToList())
            {
                var v = Ventures.For(hit.ItemId).Count > 0 ? " (venture)" : "";
                if (ImGui.Selectable($"{hit.Name}{v}##ih{hit.ItemId}"))
                {
                    C.StockItems.Add(new StockItem { ItemId = hit.ItemId, Min = 0, Target = 0 });
                    itemHits.Clear(); itemSearch = ""; changed = true;
                }
            }
        }

        // ---- recipes
        if (ImGui.CollapsingHeader($"Recipes ({C.StockRecipes.Count})###stockrecipes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextColored(Dim, "Min / target are items made (yield applied). Crafted ingredients are broken down to their materials unless ticked 'stock as is'.");
            foreach (var r in C.StockRecipes.ToList())
            {
                var info = Stock.Recipe(r.RecipeId);
                ImGui.PushID($"sr{r.RecipeId}");
                var label = info == null ? $"#{r.RecipeId}" : $"{info.Name} ({info.Job}, makes {info.Yield})";
                var open = ImGui.TreeNode($"{label}###t");
                ImGui.SameLine(300);
                int mn = r.Min, tg = r.Target;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("min", ref mn, 0, 0)) { r.Min = Math.Max(0, mn); if (r.Target < r.Min) r.Target = r.Min; changed = true; }
                ImGui.SameLine(); ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("target", ref tg, 0, 0)) { r.Target = Math.Max(r.Min, tg); changed = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { C.StockRecipes.Remove(r); changed = true; }
                if (open)
                {
                    if (info != null)
                    {
                        var crafted = Stock.CraftedIngredients(info);
                        foreach (var id in crafted)
                        {
                            var keep = r.KeepAsIs.Contains(id);
                            if (ImGui.Checkbox($"stock as is: {Items.Info(id)?.Name}##k{id}", ref keep)) { if (keep) r.KeepAsIs.Add(id); else r.KeepAsIs.Remove(id); changed = true; }
                        }
                        var mats = new Dictionary<uint, int>();
                        Stock.Expand(info, Math.Max(r.Min, r.Target), r.KeepAsIs, mats, 0);
                        ImGui.TextColored(Dim, $"For {Math.Max(r.Min, r.Target):N0}: " + string.Join(", ", mats.OrderBy(m => Items.IsCrystal(m.Key)).Select(m => $"{Items.Info(m.Key)?.Name} x{m.Value:N0}")));
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputTextWithHint("##recipesearch", "Add a recipe: type its name...", ref recipeSearch, 64))
                recipeHits = Stock.SearchRecipes(recipeSearch).ToList();
            foreach (var hit in recipeHits.ToList())
            {
                if (ImGui.Selectable($"{hit.Name} ({hit.Job}, makes {hit.Yield})##rh{hit.RecipeId}"))
                {
                    if (C.StockRecipes.All(x => x.RecipeId != hit.RecipeId)) C.StockRecipes.Add(new StockRecipe { RecipeId = hit.RecipeId });
                    recipeHits.Clear(); recipeSearch = ""; changed = true;
                }
            }
        }

        // ---- log
        if (plugin.Director.Log.Count > 0 && ImGui.CollapsingHeader("Recent redirects"))
            foreach (var d in plugin.Director.Log)
                ImGui.TextUnformatted($"{d.At:HH:mm} {d.Retainer} -> {d.Item} (~{d.Yield}; {d.Why})");

        if (changed) { C.Save(); nextNeeds = DateTime.MinValue; }
    }

    // ================================================================ selling

    private void DrawSelling(CharacterData me)
    {
        var changed = false;
        var mv = C.MoneyVentures;
        if (ImGui.Checkbox("Money ventures: when nothing on the stock list fits a retainer, send it for what sells best", ref mv)) { C.MoneyVentures = mv; changed = true; }
        ImGui.TextColored(Dim, $"Home world: {(Selling.HomeWorld() is { Length: > 0 } w ? w : "(log in)")}.  {plugin.Market.Status}");
        ImGui.TextColored(Dim, "Value = items the venture brings back x the lower of the lowest listing and the median sale (last 7 days), less 5% tax.\n" +
                               "Demand = items sold per day on your home world (from the sale history: Universalis's own velocity counts sales, not items).");
        var f = C.DemandFactor;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputFloat("Must sell at least this many ventures' worth a day", ref f, 0.5f, 1f, "%.1f")) { C.DemandFactor = Math.Clamp(f, 0.5f, 50f); changed = true; }
        var comb = C.DemandCombined;
        if (ImGui.Checkbox("Count what other retainers are already fetching, and what I hold to sell, against that", ref comb)) { C.DemandCombined = comb; changed = true; }
        var mvv = C.MinVentureValue;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Minimum value of a money venture (gil)", ref mvv, 50, 500)) { C.MinVentureValue = Math.Max(0, mvv); changed = true; }
        var pr = C.PriceRefreshMinutes;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Refresh prices after (minutes)", ref pr, 10, 30)) { C.PriceRefreshMinutes = Math.Clamp(pr, 10, 720); changed = true; }

        // What each retainer would go for right now
        if (ImGui.CollapsingHeader("What each retainer would go for now"))
        {
            var incoming = plugin.Director.Incoming();
            foreach (var r in Director.Retainers())
            {
                var why = me.NoRedirect.Contains(r.Id) ? "keeps its ventures" : null;
                var stats = plugin.Director.Stats(r.Name);
                var stock = why == null ? plugin.Director.Choose(r) : null;
                var pick = why == null && stock == null ? plugin.Selling.Choose(r, stats, incoming) : null;
                var line = why ?? (stock is { } s ? $"stock: {s.Need.Name} (~{s.Yield})"
                    : pick is { } p ? $"money: {Items.Info(p.Task.ItemId)?.Name} x{p.Yield} at {p.UnitPrice:N0} = ~{p.Value:N0} gil (sells {p.UnitsPerDay:N0}/day, needs {p.Demand:N0})"
                    : "nothing passes: AutoRetainer's own plan");
                ImGui.BulletText($"{r.Name}: {line}");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("When money-venture items come back");
        var snd = C.SellSound;
        if (ImGui.Checkbox("Play a sound", ref snd)) { C.SellSound = snd; changed = true; }
        ImGui.SameLine();
        var sid = C.SellSoundId;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("<se.#>", ref sid, 1, 1)) { C.SellSoundId = Math.Clamp(sid, 1, 16); changed = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Test")) unsafe { FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect((uint)C.SellSoundId); }
        var hl = C.SellHighlight;
        if (ImGui.Checkbox("Box them in my bags", ref hl)) { C.SellHighlight = hl; changed = true; }
        ImGui.TextColored(Dim, "They stay in your bags (Stash leaves them alone) and chat lists them.");

        ImGui.Separator();
        var auto = C.AutoSell;
        if (ImGui.Checkbox("Auto-sell: list them on the market at a selling retainer", ref auto)) { C.AutoSell = auto; changed = true; }
        if (auto)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("Selling retainer", me.SellRetainer == 0 ? "pick one..." : plugin.Tracker.NameOf(me.SellRetainer)))
            {
                foreach (var id in me.Order)
                    if (ImGui.Selectable(plugin.Tracker.NameOf(id), me.SellRetainer == id)) { me.SellRetainer = id; changed = true; }
                ImGui.EndCombo();
            }
            var after = C.SellAfterStash;
            if (ImGui.Checkbox("Also at the end of every Stash run", ref after)) { C.SellAfterStash = after; changed = true; }
            var uc = C.Undercut;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("Undercut the lowest home-world listing by (gil)", ref uc, 1, 10)) { C.Undercut = Math.Max(0, uc); changed = true; }
            var mpl = C.MaxPerListing;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("Most items per listing", ref mpl, 1, 10)) { C.MaxPerListing = Math.Clamp(mpl, 1, 9999); changed = true; }
            ImGui.TextColored(Dim, "Lists with the game's own call (no dialogs), up to the retainer's 20 market slots.\n" +
                                   "No listings on your world: the median sale price. Dagobert can keep re-pricing them as usual.");
        }

        ImGui.Separator();
        var queued = plugin.Selling.InBags();
        ImGui.TextUnformatted($"To sell ({me.SellQueue.Count} item(s) queued, {queued.Sum(q => q.Value)} in your bags)");
        foreach (var id in me.SellQueue.ToList())
        {
            ImGui.PushID((int)id);
            var info = plugin.Market.Get(id);
            ImGui.BulletText($"{Items.Info(id)?.Name}: {queued.GetValueOrDefault(id)} in bags" +
                             (info != null ? $", lowest {info.Lowest:N0}, median {info.Median:N0}, {info.UnitsPerDay:N0}/day" : ""));
            ImGui.SameLine();
            if (ImGui.SmallButton("Not for sale")) { me.SellQueue.Remove(id); changed = true; }
            ImGui.PopID();
        }
        if (changed) C.Save();
    }

    // ================================================================ clean-up

    private static readonly Verdict[] VerdictOrder =
    {
        Verdict.Use, Verdict.CardMgp, Verdict.Market, Verdict.Vendor, Verdict.GrandCompany, Verdict.Desynth,
        Verdict.Exchange, Verdict.Dresser, Verdict.Discard, Verdict.Keep,
    };

    private List<CleanLine> discardAsk = new();
    private bool openDiscard;

    private void DrawCleanup(CharacterData me)
    {
        var cu = plugin.Cleanup;
        var changed = false;
        if ((DateTime.UtcNow - cu.Analyzed).TotalSeconds > 5) cu.Analyze(me);
        var lines = cu.Lines;

        ImGui.TextWrapped("What your retainers hold (as last seen: Scan all at the bell to refresh), with a suggestion for each item. " +
                          "Approve what you agree with, then press Run clean-up at the summoning bell. Nothing happens to an item you haven't approved.");
        ImGui.TextColored(Dim, plugin.Market.Status);
        ImGui.TextColored(me.DresserSeen == null ? Warn : Dim, me.DresserSeen == null
            ? "Glamour dresser: not seen yet. Open it once (at an inn or a glamour dresser) so items you already stored can be spotted."
            : $"Glamour dresser: {me.Dresser.Count} item(s), seen {me.DresserSeen.Value.ToLocalTime():g}.");
        ImGui.TextColored(me.ArmoireSeen == null ? Warn : Dim, me.ArmoireSeen == null
            ? "Armoire: not seen yet. Open it once in an inn room."
            : $"Armoire: {me.Armoire.Count} item(s), seen {me.ArmoireSeen.Value.ToLocalTime():g}.");

        // ---- actions
        var approved = lines.Where(l => me.CleanupApproved.Contains(l.Key) && l.Verdict != Verdict.Keep).ToList();
        var why = plugin.Runner.CantStart();
        if (approved.Count == 0 || why != null) ImGui.BeginDisabled();
        if (ImGui.Button($"Run clean-up ({approved.Count} approved)")) plugin.Runner.Start(Runner.RunMode.Cleanup);
        if (approved.Count == 0 || why != null) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip((why ?? "") + "\nAt the summoning bell: takes the approved items out of each retainer into your bags. Vendor items are sold " +
                             "through the retainer, approved discards are discarded, market items are listed at your selling retainer " +
                             "(Selling tab) if you picked one. The rest (GC turn-in, desynthesis, cards, tokens, looks to store) wait in your bags.");
        ImGui.SameLine();
        var usable = cu.Usable().Count;
        if (usable == 0 || cu.Using) ImGui.BeginDisabled();
        if (ImGui.Button(cu.Using ? "Using..." : $"Use unlearned items in bags ({usable})")) cu.StartUse();
        if (usable == 0 || cu.Using) ImGui.EndDisabled();
        var dupes = cu.DresserDuplicates(me);
        if (dupes.Count > 0)
        {
            ImGui.SameLine();
            if (!Cleanup.DresserOpen || cu.Restoring) ImGui.BeginDisabled();
            if (ImGui.Button(cu.Restoring ? "Taking out..." : $"Take dresser duplicates out ({dupes.Sum(d => d.Copies - 1)})")) cu.StartRestore();
            if (!Cleanup.DresserOpen || cu.Restoring) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("With the glamour dresser open: takes the extra copies out to your bags (one copy stays).\n" +
                                 string.Join("\n", dupes.Select(d => $"{d.Name} x{d.Copies}")));
        }
        if (cu.Using || cu.Restoring) { ImGui.SameLine(); if (ImGui.SmallButton("Stop")) cu.StopAll(); }
        var wr = plugin.Wardrobe;
        var looks = wr.ToStore();
        if (looks.Count > 0 || wr.Busy)
        {
            var arm = looks.Count(x => x.Armoire);
            if (wr.Busy) { if (ImGui.Button("Stop storing")) wr.Stop(); }
            else if (ImGui.Button($"Store looks at an inn ({arm} Armoire, {looks.Count - arm} dresser)")) wr.Start();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Approved \"keep the look\" items in your bags: goes to an inn room (Lifestream \"/li inn\") unless you're in one,\n" +
                                 "walks to the Armoire (vnavmesh) for what fits there (free), then the Glamour Dresser for the rest\n" +
                                 $"(one Glamour Prism each: you have {Wardrobe.Prisms()}).\n" +
                                 (wr.LifestreamReady ? "" : "Lifestream isn't installed: go to an inn room yourself first.\n") +
                                 (wr.VNavReady ? "" : "vnavmesh isn't installed: stand next to the Armoire / dresser first.\n") +
                                 "/stash looks does the same.");
        }
        if (wr.Status.Length > 0) ImGui.TextColored(Dim, wr.Status);
        if (cu.Status.Length > 0) ImGui.TextColored(Dim, cu.Status);

        if (ImGui.CollapsingHeader("Clean-up settings"))
        {
            var ms = C.CleanupMinSell;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("Sell on the market only if a stack fetches at least (gil, after tax)", ref ms, 100, 1000)) { C.CleanupMinSell = Math.Max(0, ms); changed = true; }
            var rv = C.CleanupReview;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("Stacks worth more than this get their own look (gil)", ref rv, 1000, 5000)) { C.CleanupReview = Math.Max(0, rv); changed = true; }
            var sm = C.CleanupSkipMaterials;
            if (ImGui.Checkbox("Leave materials, materia, crystals and stock-list items alone", ref sm)) { C.CleanupSkipMaterials = sm; changed = true; }
            var pd = C.CleanupPreferDesynth;
            if (ImGui.Checkbox("Desynthesize rather than vendor when an item can be desynthesized", ref pd)) { C.CleanupPreferDesynth = pd; changed = true; }
            var au = C.CleanupAutoUse;
            if (ImGui.Checkbox("After a run, use the unlearned items it brought out (minions, cards, rolls...)", ref au)) { C.CleanupAutoUse = au; changed = true; }
            var sl = C.CleanupStoreLooks;
            if (ImGui.Checkbox("After a run, go to an inn and store the \"keep the look\" items (Armoire, then glamour dresser)", ref sl)) { C.CleanupStoreLooks = sl; changed = true; }
            ImGui.TextColored(Dim, "Value: home-world market (lower of the lowest listing and the 7-day median, less 5% tax) or the vendor price, whichever is higher.\n" +
                                   "Keep the look: gear whose look can't be had any more (no shop, duty, recipe or market source), or that fits the Armoire.\n" +
                                   "\"Easy to get again\": solo or small group unsynced at level 100, a rule of thumb (hover an item for its sources).");
            if (me.SellRetainer == 0) ImGui.TextColored(Warn, "No selling retainer picked (Selling tab): approved market items will wait in your bags, boxed.");
        }

        ImGui.Separator();
        var total = approved.Where(l => l.Verdict is Verdict.Market or Verdict.Vendor).Sum(l => l.Value);
        ImGui.TextUnformatted($"{lines.Count} item(s) on {me.Order.Count} retainer(s); {approved.Count} approved (~{total:N0} gil from sales).");
        if (lines.Any(l => l.PricePending)) ImGui.TextColored(Warn, "Some prices are still on their way from Universalis: those items show as Keep for now.");

        var review = lines.Where(l => cu.NeedsReview(l)).OrderByDescending(l => l.Value).ToList();
        if (review.Count > 0 && ImGui.CollapsingHeader($"Worth a look: {review.Count} stack(s) over {C.CleanupReview:N0} gil###review", ImGuiTreeNodeFlags.DefaultOpen))
            changed |= CleanTable("##review", review, me);

        foreach (var v in VerdictOrder)
        {
            var group = lines.Where(l => l.Verdict == v && !cu.NeedsReview(l)).OrderByDescending(l => l.Value).ToList();
            if (group.Count == 0) continue;
            var sum = group.Sum(l => l.Value);
            var ok = group.Count(l => me.CleanupApproved.Contains(l.Key));
            if (!ImGui.CollapsingHeader($"{Cleanup.Label(v)}: {group.Count} ({ok} approved){(sum > 0 ? $", ~{sum:N0} gil" : "")}###grp{(int)v}")) continue;
            ImGui.PushID((int)v);
            if (v != Verdict.Keep)
            {
                if (ImGui.SmallButton($"Approve all {group.Count}"))
                {
                    if (v == Verdict.Discard) AskDiscard(group);
                    else { foreach (var l in group) me.CleanupApproved.Add(l.Key); changed = true; }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear")) { foreach (var l in group) me.CleanupApproved.Remove(l.Key); changed = true; }
                ImGui.SameLine();
            }
            ImGui.TextColored(Dim, Hint(v));
            changed |= CleanTable("##t", group, me);
            ImGui.PopID();
        }

        DrawDiscardPopup(me, ref changed);
        if (changed) { plugin.Tracker.Save(force: true); C.Save(); }
    }

    private static string Hint(Verdict v) => v switch
    {
        Verdict.Use => "Unlearned minions, cards, orchestrion rolls and the like: the run brings them out and uses them.",
        Verdict.CardMgp => "Cards you already have: sell them to the Triple Triad Trader at the Gold Saucer for MGP (automatic next update).",
        Verdict.Market => "Listed at your selling retainer (lowest listing minus your undercut).",
        Verdict.Vendor => "Sold through the retainer ('Have Retainer Sell Items').",
        Verdict.GrandCompany => "Brought to your bags: Turn-In Manager takes them to your Grand Company for seals.",
        Verdict.Desynth => "Brought to your bags to desynthesize.",
        Verdict.Exchange => "Exchange tokens: hover to see what they buy. Brought to your bags.",
        Verdict.Dresser => "Brought to your bags, then taken to an inn and stored: Armoire if it fits there (free), else the glamour dresser (one Glamour Prism each).",
        Verdict.Discard => "No value found. Approving shows how you'd get each one back first.",
        _ => "Left where it is.",
    };

    /// <summary>The rows of one group. True if anything changed.</summary>
    private bool CleanTable(string id, List<CleanLine> rows, CharacterData me)
    {
        var changed = false;
        if (!ImGui.BeginTable(id, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return false;
        ImGui.TableSetupColumn("OK", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Verdict", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableHeadersRow();
        foreach (var l in rows)
        {
            ImGui.PushID($"{l.Retainer}:{l.Key}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var on = me.CleanupApproved.Contains(l.Key);
            if (l.Verdict == Verdict.Keep) ImGui.BeginDisabled();
            if (ImGui.Checkbox("##ok", ref on))
            {
                if (on && l.Verdict == Verdict.Discard) AskDiscard(new List<CleanLine> { l });
                else if (on) { me.CleanupApproved.Add(l.Key); changed = true; }
                else { me.CleanupApproved.Remove(l.Key); changed = true; }
            }
            if (l.Verdict == Verdict.Keep) ImGui.EndDisabled();

            ImGui.TableNextColumn();
            var easy = l.Easiest <= Regain.Effort.SmallGroup;
            ImGui.TextUnformatted(l.Name + (l.Qty > 1 ? $" x{l.Qty}" : "") + (l.Stacks > 1 ? $" ({l.Stacks} stacks)" : ""));
            if (ImGui.IsItemHovered())
            {
                var tip = "How to get it again:\n  " + string.Join("\n  ", l.Ways) + "\n(Right-click: more information, Allagan Tools, item sites.)";
                if (l.Verdict == Verdict.Exchange || plugin.Cleanup.TokenRewards(l.ItemId).Count > 0)
                {
                    var buys = plugin.Cleanup.TokenRewards(l.ItemId);
                    tip += $"\nExchanges for ({buys.Count}):\n  " + string.Join("\n  ", buys.Take(15)) + (buys.Count > 15 ? "\n  ..." : "");
                }
                ImGui.SetTooltip(tip);
            }
            ItemMenu(l.ItemId, l.Name);
            if (easy) { ImGui.SameLine(); ImGui.TextColored(Good, l.Easiest == Regain.Effort.Solo ? "[easy: solo]" : "[easy: group]"); }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plugin.Tracker.NameOf(l.Retainer));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.PricePending ? "..." : l.Value > 0 ? $"{l.Value:N0}" : "-");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##v", Cleanup.Label(l.Verdict) + (l.Overridden ? " *" : "")))
            {
                foreach (var v in VerdictOrder)
                {
                    var label = Cleanup.Label(v) + (v == l.Auto ? "  (suggested)" : "");
                    if (!ImGui.Selectable(label, v == l.Verdict)) continue;
                    if (v == l.Auto) C.CleanupVerdicts.Remove(l.ItemId); else C.CleanupVerdicts[l.ItemId] = v;
                    me.CleanupApproved.Remove(l.Key); // a new verdict needs a new OK
                    changed = true;
                }
                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            var text = l.Why + (l.Overridden ? $" (suggested: {Cleanup.Label(l.Auto)})" : "");
            ImGui.TextWrapped(text);
            if (l.Notes.Count > 0) ImGui.TextColored(Dim, string.Join("; ", l.Notes));
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (changed) plugin.Cleanup.Analyze(me);
        return changed;
    }

    /// <summary>
    /// Right-click menu on an item's name: try it on in game (gear), pictures of the look, Allagan Tools'
    /// "more information" window, item sites, copy the name.
    /// </summary>
    private static void ItemMenu(uint itemId, string name)
    {
        if (!ImGui.BeginPopupContextItem($"##item{itemId}")) return;
        var plain = name.Replace(" (HQ)", "");
        ImGui.TextDisabled(plain);
        ImGui.Separator();
        var gear = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId) is { } row && row.EquipSlotCategory.RowId != 0;
        if (gear)
        {
            if (ImGui.MenuItem("Try on (in game)")) unsafe { FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentTryon.TryOn(0, itemId); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Opens the game's Fitting Room with it on your character (works for items you don't hold).");
            if (ImGui.MenuItem("Pictures: Eorzea Collection")) Dalamud.Utility.Util.OpenLink($"https://ffxiv.eorzeacollection.com/glamours?search_options%5Bsearch%5D={Uri.EscapeDataString(plain)}&search_options%5Bapply%5D=");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Players' glamours, searched by the item's name.");
            if (ImGui.MenuItem("Pictures: Console Games Wiki")) Dalamud.Utility.Util.OpenLink($"https://ffxiv.consolegameswiki.com/wiki/{Uri.EscapeDataString(plain.Replace(' ', '_'))}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The item's page, with screenshots of it worn.");
            ImGui.Separator();
        }
        var allagan = Plugin.CommandManager.Commands.ContainsKey("/moreinfo");
        if (!allagan) ImGui.BeginDisabled();
        if (ImGui.MenuItem("More information (Allagan Tools)")) Plugin.CommandManager.ProcessCommand($"/moreinfo {itemId}");
        if (!allagan) ImGui.EndDisabled();
        if (!allagan && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Allagan Tools isn't loaded.");
        if (ImGui.MenuItem("Market board (Universalis)")) Dalamud.Utility.Util.OpenLink($"https://universalis.app/market/{itemId}");
        if (ImGui.MenuItem("Garland Tools")) Dalamud.Utility.Util.OpenLink($"https://www.garlandtools.org/db/#item/{itemId}");
        if (ImGui.MenuItem("Teamcraft")) Dalamud.Utility.Util.OpenLink($"https://ffxivteamcraft.com/db/en/item/{itemId}");
        if (ImGui.MenuItem("Console Games Wiki")) Dalamud.Utility.Util.OpenLink($"https://ffxiv.consolegameswiki.com/wiki/{Uri.EscapeDataString(plain.Replace(' ', '_'))}");
        ImGui.Separator();
        if (ImGui.MenuItem("Copy name")) ImGui.SetClipboardText(plain);
        ImGui.EndPopup();
    }

    private void AskDiscard(List<CleanLine> lines)
    {
        discardAsk = lines.Where(l => l.Verdict == Verdict.Discard).ToList();
        openDiscard = discardAsk.Count > 0;
    }

    private void DrawDiscardPopup(CharacterData me, ref bool changed)
    {
        if (openDiscard) { ImGui.OpenPopup("##discard"); openDiscard = false; }
        if (!ImGui.BeginPopup("##discard")) return;
        ImGui.TextColored(Warn, $"Discard {discardAsk.Count} item(s)? Discarded items are gone. Here's how you'd get each back:");
        ImGui.Separator();
        foreach (var l in discardAsk.Take(30))
        {
            ImGui.TextUnformatted($"{l.Name}{(l.Qty > 1 ? $" x{l.Qty}" : "")} ({plugin.Tracker.NameOf(l.Retainer)})");
            ItemMenu(l.ItemId, l.Name);
            ImGui.Indent();
            foreach (var w in l.Ways) ImGui.TextColored(Dim, w);
            ImGui.Unindent();
        }
        if (discardAsk.Count > 30) ImGui.TextColored(Dim, $"...and {discardAsk.Count - 30} more (same rules: no value found).");
        ImGui.Separator();
        if (ImGui.Button("Discard them (on the next run)"))
        {
            foreach (var l in discardAsk) me.CleanupApproved.Add(l.Key);
            changed = true;
            discardAsk = new List<CleanLine>();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { discardAsk = new List<CleanLine>(); ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ================================================================ rules

    private void DrawRules()
    {
        var changed = false;
        var g = C.IncludeGathered;
        if (ImGui.Checkbox("Gathered materials (mining, botany, fishing, spearfishing) used in recipes", ref g)) { C.IncludeGathered = g; changed = true; }
        var m = C.IncludeMobDrops;
        if (ImGui.Checkbox("Other recipe materials: tradeable, common rarity, not crafted (hides, bones, fleece, reagents...)", ref m)) { C.IncludeMobDrops = m; changed = true; }
        ImGui.TextColored(Dim, "Never: collectables, gear, crafted items, untradeable or green/blue items (boss and glamour-weapon mats).\nVendor-bought recipe materials also pass the second rule: exclude any you'd rather keep.");

        ImGui.Separator();
        ImGui.TextUnformatted($"Always stash ({C.Include.Count})");
        if (IdList(C.Include)) changed = true;
        ImGui.TextColored(Dim, "Add items from the 'Bags now' tab (\"Stash it\"). Items never to stash: the Keep on me tab.");
        if (changed) C.Save();
    }

    private static bool IdList(HashSet<uint> set)
    {
        var changed = false;
        foreach (var id in set.OrderBy(i => Items.Info(i)?.Name).ToList())
        {
            ImGui.PushID((int)id);
            ImGui.BulletText(Items.Info(id)?.Name ?? $"#{id}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) { set.Remove(id); changed = true; }
            ImGui.PopID();
        }
        return changed;
    }
}
