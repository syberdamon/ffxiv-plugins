using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace RetainerGear.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;
    private List<RetainerPlan> plans = new();
    private int builtRevision = -1;
    private DateTime builtAt = DateTime.MinValue;
    private string newCurrency = "";

    public MainWindow(Plugin plugin) : base("Retainer Gear###RetainerGearMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 400), MaximumSize = new Vector2(1600, 1400) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) { ImGui.TextUnformatted("Log in first."); return; }

        // Rebuild when gear changed, or every 5 s (your own bags change too).
        if (builtRevision != plugin.Tracker.Revision || (DateTime.UtcNow - builtAt).TotalSeconds > 5)
        {
            plans = Planner.Build(plugin);
            builtRevision = plugin.Tracker.Revision;
            builtAt = DateTime.UtcNow;
            if (C.ShowPrices)
                plugin.Prices.Request(plans.SelectMany(p => p.Slots).Where(s => s.NeedsUpgrade && s.OwnedWhere == null &&
                    s.Best!.Sources.Any(x => x.Kind == SourceKind.Crafted)).Select(s => s.Best!.ItemId), DataCenter());
        }

        if (plans.Count == 0)
        {
            ImGui.TextWrapped("No retainers recorded yet. Use a summoning bell once (open the retainer list) so the plugin can read your retainers, then open each retainer to record their gear.");
            return;
        }

        if (!ImGui.BeginTabBar("##rg")) return;
        if (ImGui.BeginTabItem("Retainers")) { DrawRetainers(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Shopping list")) { DrawShopping(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private static string DataCenter()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        return me?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? "";
    }

    // ================================================================ retainers

    private void DrawRetainers()
    {
        ImGui.TextDisabled("Open a retainer at a summoning bell to record (or refresh) their gear. Upgrades update as you equip them.");
        foreach (var p in plans)
        {
            var r = p.Retainer;
            var need = p.Slots.Count(s => s.NeedsUpgrade);
            var head = $"{r.Name}  {p.Job} {r.Level}  avg ilvl {p.CurrentAvg:0} -> {p.TargetAvg:0}  " +
                       (!p.GearKnown ? "(gear not seen yet)" : need == 0 ? "(fully upgraded)" : $"({need} upgrade{(need == 1 ? "" : "s")})");
            ImGui.PushID((int)(r.Id & 0x7FFFFFFF));
            if (ImGui.CollapsingHeader(head + $"###r{r.Id}", need > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                if (!p.GearKnown) ImGui.TextColored(Warn, "Open this retainer once so their equipped gear can be read.");
                else ImGui.TextDisabled($"Gear seen {r.GearSeen:g}");
                if (ImGui.BeginTable("##slots", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Wearing");
                    ImGui.TableSetupColumn("Best available");
                    ImGui.TableSetupColumn("From");
                    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 160);
                    ImGui.TableHeadersRow();
                    foreach (var s in p.Slots)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(s.SlotName);
                        ImGui.TableNextColumn(); ImGui.TextColored(s.CurrentId == 0 ? Dim : Vector4.One, s.CurrentId == 0 ? "(empty)" : $"{s.CurrentName} ({s.CurrentIlvl})");
                        ImGui.TableNextColumn();
                        if (s.Best == null) ImGui.TextColored(Dim, "nothing found");
                        else { Icon(s.Best.Icon); ImGui.SameLine(); ImGui.TextUnformatted($"{s.Best.Name} ({s.Best.ItemLevel})"); }
                        ImGui.TableNextColumn(); ImGui.TextColored(Dim, s.Best == null ? "" : SourceText(s.Best));
                        ImGui.TableNextColumn();
                        if (!s.NeedsUpgrade) ImGui.TextColored(Good, "best available");
                        else if (s.OwnedWhere != null) ImGui.TextColored(Good, $"you have it ({s.OwnedWhere})");
                        else ImGui.TextColored(Warn, PriceText(s.Best!) ?? "needed");
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.PopID();
        }
    }

    // ================================================================ shopping list

    private void DrawShopping()
    {
        var needs = plans.SelectMany(p => p.Slots).Where(s => s.NeedsUpgrade).ToList();
        var toBuy = needs.Where(s => s.OwnedWhere == null).GroupBy(s => s.Best!.ItemId).ToList();
        var toHand = needs.Where(s => s.OwnedWhere != null).ToList();

        ImGui.TextUnformatted($"{needs.Count} upgrades: {toHand.Count} you already own, {toBuy.Sum(g => g.Count())} to get.");
        if (C.ShowPrices) ImGui.TextDisabled(plugin.Prices.Status);

        if (toHand.Count > 0 && ImGui.CollapsingHeader($"Hand these over ({toHand.Count})###hand", ImGuiTreeNodeFlags.DefaultOpen) &&
            ImGui.BeginTable("##hand", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Give to"); ImGui.TableSetupColumn("It's in");
            ImGui.TableHeadersRow();
            foreach (var s in toHand.OrderBy(s => s.Retainer.Name))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); Icon(s.Best!.Icon); ImGui.SameLine(); ImGui.TextUnformatted($"{s.Best.Name} ({s.Best.ItemLevel})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{s.Retainer.Name} ({s.SlotName})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(s.OwnedWhere!);
            }
            ImGui.EndTable();
        }

        if (toBuy.Count > 0 && ImGui.CollapsingHeader($"To get ({toBuy.Sum(g => g.Count())})###buy", ImGuiTreeNodeFlags.DefaultOpen) &&
            ImGui.BeginTable("##buy", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable))
        {
            ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 32);
            ImGui.TableSetupColumn("For"); ImGui.TableSetupColumn("From"); ImGui.TableSetupColumn("Price (lowest)", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableHeadersRow();
            long total = 0;
            foreach (var g in toBuy.OrderBy(g => g.First().Best!.Sources[0].Kind).ThenBy(g => g.First().Best!.Name))
            {
                var o = g.First().Best!;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); Icon(o.Icon); ImGui.SameLine(); ImGui.TextUnformatted($"{o.Name} ({o.ItemLevel})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(g.Count().ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Join(", ", g.Select(s => s.Retainer.Name).Distinct()));
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, SourceText(o));
                ImGui.TableNextColumn();
                var each = PriceValue(o);
                if (each > 0) total += each * g.Count();
                ImGui.TextUnformatted(PriceText(o) ?? "-");
            }
            ImGui.EndTable();
            if (total > 0) ImGui.TextDisabled($"Crafted pieces at market price: about {total:N0} gil (lowest listings, NQ unless HQ is cheaper).");
        }
    }

    // ================================================================ settings

    private void DrawSettings()
    {
        var changed = false;
        bool Box(string label, bool v, Action<bool> set) { var x = v; if (ImGui.Checkbox(label, ref x)) { set(x); changed = true; } return x; }
        ImGui.TextUnformatted("Gear sources (raid and savage gear are never included)");
        Box("Crafted", C.UseCrafted, v => C.UseCrafted = v);
        Box("Dungeon drops", C.UseDungeon, v => C.UseDungeon = v);
        Box("Uncapped tomestones / currencies", C.UseTomestones, v => C.UseTomestones = v);
        Box("Market board prices for crafted gear (Universalis)", C.ShowPrices, v => C.ShowPrices = v);

        ImGui.Spacing();
        ImGui.TextUnformatted("Currencies that count (exact English item names):");
        for (var i = 0; i < C.Currencies.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.BulletText(C.Currencies[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) { C.Currencies.RemoveAt(i); changed = true; ImGui.PopID(); break; }
            ImGui.PopID();
        }
        ImGui.SetNextItemWidth(280);
        ImGui.InputTextWithHint("##cur", "e.g. Allagan Tomestone of Mathematics", ref newCurrency, 80);
        ImGui.SameLine();
        if (ImGui.Button("Add") && newCurrency.Trim().Length > 0) { C.Currencies.Add(newCurrency.Trim()); newCurrency = ""; changed = true; }

        ImGui.Spacing();
        ImGui.TextDisabled($"Data: {plugin.Sources.DataNote}; {C.LearnedDrops.Count} dungeon drops learned as you play.");
        ImGui.TextDisabled("Dungeon drop lists come from Teamcraft (MIT). New dungeons fill in as you loot gear there.");

        if (changed) { C.Save(); plugin.Sources.Invalidate(); builtRevision = -1; }
    }

    // ================================================================ helpers

    private static string SourceText(GearOption o) => string.Join("; ", o.Sources.Select(s => s.Text).Distinct().Take(3));

    private long PriceValue(GearOption o)
    {
        if (!C.ShowPrices || o.Sources.All(s => s.Kind != SourceKind.Crafted)) return 0;
        var p = plugin.Prices.Get(o.ItemId);
        if (p == null) return 0;
        var options = new[] { p.Nq, p.Hq }.Where(x => x > 0).ToArray();
        return options.Length == 0 ? 0 : options.Min();
    }

    private string? PriceText(GearOption o)
    {
        if (o.Sources.FirstOrDefault(s => s.Kind == SourceKind.Tomestone) is { } tome && o.Sources.All(s => s.Kind != SourceKind.Crafted))
            return tome.Text;
        if (!C.ShowPrices || o.Sources.All(s => s.Kind != SourceKind.Crafted)) return null;
        var p = plugin.Prices.Get(o.ItemId);
        if (p == null) return "price...";
        if (p.Nq == 0 && p.Hq == 0) return "none listed";
        return p.Hq > 0 && (p.Nq == 0 || p.Hq <= p.Nq) ? $"{p.Hq:N0} (HQ)" : $"{p.Nq:N0}";
    }

    private static void Icon(uint icon)
    {
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        ImGui.Image(tex.Handle, new Vector2(ImGui.GetTextLineHeight()));
    }
}
