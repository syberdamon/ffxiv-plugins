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
        if (ImGui.BeginTabItem("Rules")) { DrawRules(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ bags (preview)

    private void DrawBags(CharacterData me)
    {
        ImGui.TextWrapped("What's in your bags and where a stash run would put it. Include or exclude items here; the rules are on the Rules tab.");
        var showAll = ImGui.GetIO().KeyShift;
        ImGui.TextColored(Dim, "Hold Shift to also list items that don't qualify.");
        var stacks = Tracker.Stacks(Tracker.Bags);
        var rows = stacks.GroupBy(s => s.ItemId).Select(g => (Id: g.Key, Qty: g.Sum(s => s.Qty))).ToList();
        var changed = false;
        if (ImGui.BeginTable("##bags", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new Vector2(0, 0)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var (id, qty) in rows.OrderByDescending(r => Items.Stashable(r.Id, C)).ThenBy(r => Items.Info(r.Id)?.Category).ThenBy(r => Items.Info(r.Id)?.Name))
            {
                if (Items.IsCrystal(id)) continue;
                var info = Items.Info(id);
                if (info == null) continue;
                var stash = Items.Stashable(id, C);
                if (!stash && !showAll && !C.Exclude.Contains(id % 1_000_000)) continue;
                var nq = id % 1_000_000;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(info.Name + (id >= 1_000_000 ? " (HQ)" : ""));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(info.Category);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(qty.ToString("N0"));
                ImGui.TableNextColumn();
                var why = C.Exclude.Contains(nq) ? "excluded by you" : C.Include.Contains(nq) ? "included by you" : info.Why;
                ImGui.TextColored(stash ? Good : Dim, why);
                ImGui.TableNextColumn();
                if (stash)
                {
                    if (plugin.Planner.Best(id) is { } to) ImGui.TextUnformatted(plugin.Tracker.NameOf(to));
                    else ImGui.TextColored(Warn, "no room / no home");
                }
                else ImGui.TextColored(Dim, "stays");
                ImGui.TableNextColumn();
                ImGui.PushID((int)id);
                if (stash)
                {
                    if (ImGui.SmallButton("Exclude")) { C.Include.Remove(nq); C.Exclude.Add(nq); changed = true; }
                }
                else if (C.Exclude.Contains(nq))
                {
                    if (ImGui.SmallButton("Allow")) { C.Exclude.Remove(nq); changed = true; }
                }
                else if (ImGui.SmallButton("Include")) { C.Include.Add(nq); changed = true; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
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

    private void DrawCategories(CharacterData me)
    {
        ImGui.TextWrapped("Give a category its home retainers, first choice first. Items with no home (or whose homes are full) use the overflow order on the Retainers tab.");
        var changed = false;
        var cats = Items.Candidates()
            .Concat(C.Include.Select(id => Items.Info(id)).Where(i => i != null).Select(i => i!))
            .GroupBy(i => i.CategoryId)
            .Select(g => (Id: g.Key, Name: g.First().Category, Count: g.Count()))
            .OrderBy(c => c.Name)
            .ToList();
        var inBags = Tracker.Stacks(Tracker.Bags, id => Items.Stashable(id, C)).Select(s => Items.Info(s.ItemId)?.CategoryId ?? 0).ToHashSet();
        if (ImGui.BeginChild("##cats"))
        {
            foreach (var cat in cats)
            {
                if (!me.CategoryHomes.TryGetValue(cat.Id, out var homes)) homes = new List<ulong>();
                var summary = homes.Count == 0 ? "overflow" : string.Join(", ", homes.Select(plugin.Tracker.NameOf));
                var open = ImGui.TreeNode($"{cat.Name}  ({cat.Count} items) -> {summary}{(inBags.Contains(cat.Id) ? "   [in your bags]" : "")}###cat{cat.Id}");
                if (!open) continue;
                if (OrderedList($"cat{cat.Id}", homes, me))
                {
                    if (homes.Count == 0) me.CategoryHomes.Remove(cat.Id); else me.CategoryHomes[cat.Id] = homes;
                    changed = true;
                }
                if (ImGui.SmallButton("List its items"))
                {
                    var names = Items.Candidates().Where(i => i.CategoryId == cat.Id).Select(i => i.Name).OrderBy(n => n).Take(400);
                    Plugin.ChatGui.Print($"[Stash] {cat.Name}: {string.Join(", ", names)}");
                }
                ImGui.TreePop();
            }
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
                ImGui.TableNextColumn(); ImGui.TextUnformatted(n.Target.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, sources.GetValueOrDefault(n.ItemId, ""));
            }
            ImGui.EndTable();
        }
        else if (needs.Count == 0) ImGui.TextColored(Dim, "Nothing on the stock list yet: add items or recipes below.");

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
        ImGui.TextUnformatted($"Never stash ({C.Exclude.Count})");
        if (IdList(C.Exclude)) changed = true;
        ImGui.TextColored(Dim, "Add items from the 'Bags now' tab (hold Shift there to see everything).");
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
