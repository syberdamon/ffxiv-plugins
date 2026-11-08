using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Syberdamon.Shared;
using TurnInManager.GrandCompany;

namespace TurnInManager.Windows;

/// <summary>Grand Company tab: guardrails, preview of exactly what would be turned in, purchase list.</summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Ok = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;
    private string addName = "";
    private string addError = "";
    private bool showBlocked;

    public MainWindow(Plugin plugin) : base("Turn-In Manager###TurnInMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 420), MaximumSize = new Vector2(1400, 1600) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##tabs")) return;
        if (ImGui.BeginTabItem("Grand Company")) { DrawGc(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Scrips")) { DrawScrips(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private void DrawGc()
    {
        var gc = GcData.Current();
        if (Plugin.ObjectTable.LocalPlayer == null) { ImGui.TextUnformatted("Log in first."); return; }
        if (gc == null) { ImGui.TextUnformatted("You're not in a Grand Company."); return; }

        var seals = GcData.Seals();
        var cap = GcData.SealCap();
        ImGui.TextUnformatted($"{gc.Name}, rank {GcData.Rank()}: {seals:N0} / {cap:N0} seals");

        var r = plugin.GcRunner;
        if (r.Busy) { if (ImGui.Button("Stop")) r.Stop(); ImGui.SameLine(); ImGui.TextUnformatted(r.Status); }
        else
        {
            var blocker = r.Blocker();
            if (blocker != null) ImGui.BeginDisabled();
            if (ImGui.Button("Run: retrieve materia, deliver, buy before cap")) r.Start();
            ImGui.SameLine();
            if (ImGui.Button("Buy only")) r.Start(GcMode.BuyOnly);
            ImGui.SameLine();
            if (ImGui.Button("Strip materia only")) r.Start(GcMode.StripOnly);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retrieve materia from every item that passes your filters, then stop. Works anywhere.");
            if (blocker != null) { ImGui.EndDisabled(); ImGui.SameLine(); ImGui.TextColored(Warn, blocker); }
            if (r.Phase == GcPhase.Done || r.Status.Length > 0) ImGui.TextDisabled(r.Status);
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("What may be turned in", ImGuiTreeNodeFlags.DefaultOpen)) DrawGuardrails();
        if (ImGui.CollapsingHeader("Spending seals", ImGuiTreeNodeFlags.DefaultOpen)) DrawPurchases();
        if (ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen)) DrawPreview();
    }

    private void DrawGuardrails()
    {
        var changed = false;
        bool Box(string label, bool v, Action<bool> set) { var x = v; if (ImGui.Checkbox(label, ref x)) { set(x); changed = true; } return x; }

        Box("Never turn in items that are in a gearset", C.SkipGearsetItems, v => C.SkipGearsetItems = v);
        var ilvl = C.MaxItemLevel;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Max item level", ref ilvl, 5, 50)) { C.MaxItemLevel = Math.Max(1, ilvl); changed = true; }
        Box("Green", C.AllowGreen, v => C.AllowGreen = v); ImGui.SameLine();
        Box("Blue", C.AllowBlue, v => C.AllowBlue = v); ImGui.SameLine();
        Box("NQ", C.AllowNq, v => C.AllowNq = v); ImGui.SameLine();
        Box("HQ", C.AllowHq, v => C.AllowHq = v);
        Box("Include the armoury chest", C.IncludeArmoury, v => C.IncludeArmoury = v);
        Box("Retrieve materia first (gear turned in with materia loses it)", C.RetrieveMateria, v => C.RetrieveMateria = v);
        if (!C.RetrieveMateria) Box("  Skip gear with materia instead", C.SkipItemsWithMateria, v => C.SkipItemsWithMateria = v);
        Box("Use Lifestream to travel to your Grand Company", C.UseLifestream, v => C.UseLifestream = v);
        if (C.NeverTurnIn.Count > 0)
        {
            ImGui.TextDisabled($"Protected items: {C.NeverTurnIn.Count}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear protected")) { C.NeverTurnIn.Clear(); changed = true; }
        }
        if (changed) C.Save();
    }

    private void DrawPurchases()
    {
        var changed = false;
        var x = C.BuyBeforeCap;
        if (ImGui.Checkbox("Buy before a delivery would push seals over the cap", ref x)) { C.BuyBeforeCap = x; changed = true; }
        x = C.SpendAtEnd;
        if (ImGui.Checkbox("Also spend down at the end of a run", ref x)) { C.SpendAtEnd = x; changed = true; }
        var reserve = C.SealReserve;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Keep at least (seals)", ref reserve, 1000, 10000)) { C.SealReserve = Math.Max(0, reserve); changed = true; }

        ImGui.TextDisabled("Bought top to bottom, each up to its target (items you already hold count):");
        if (ImGui.BeginTable("##buy", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            for (var i = 0; i < C.Purchases.Count; i++)
            {
                var p = C.Purchases[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var on = p.Enabled;
                if (ImGui.Checkbox("##on", ref on)) { p.Enabled = on; changed = true; }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(GcData.Count(p.ItemId).ToString("N0"));
                ImGui.TableNextColumn();
                var t = p.Target;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##t", ref t, 0, 0)) { p.Target = Math.Max(0, t); changed = true; }
                ImGui.TableNextColumn();
                if (i > 0 && ImGui.ArrowButton("##up", ImGuiDir.Up)) { (C.Purchases[i - 1], C.Purchases[i]) = (C.Purchases[i], C.Purchases[i - 1]); changed = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) { C.Purchases.RemoveAt(i); changed = true; ImGui.PopID(); break; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##add", "Item name (exact, e.g. Venture)", ref addName, 100);
        ImGui.SameLine();
        if (ImGui.Button("Add"))
        {
            var row = Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English)
                .FirstOrDefault(it => it.Name.ExtractText().Equals(addName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (row.RowId == 0) addError = $"No item called \"{addName}\".";
            else if (C.Purchases.Any(p => p.ItemId == row.RowId)) addError = $"{row.Name.ExtractText()} is already on the list.";
            else { C.Purchases.Add(new PurchaseRule { ItemId = row.RowId, Name = row.Name.ExtractText(), Target = 1 }); addName = ""; addError = ""; changed = true; }
        }
        if (addError.Length > 0) ImGui.TextColored(Warn, addError);
        if (changed) C.Save();
    }

    private void DrawPreview()
    {
        var all = GcData.Scan(C);
        var allowed = all.Where(c => c.Allowed).ToList();
        ImGui.TextUnformatted($"{allowed.Count} item(s) would be offered for turn-in" +
                              (allowed.Any(a => a.MateriaCount > 0) ? $"; {allowed.Count(a => a.MateriaCount > 0)} have materia to retrieve first." : "."));
        ImGui.TextDisabled("Only items the game also lists under Expert Delivery are turned in. Right-click a row to protect it.");
        ImGui.Checkbox("Show items that are blocked, and why", ref showBlocked);

        if (!ImGui.BeginTable("##preview", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 300))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("ilvl", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Q", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();
        foreach (var c in (showBlocked ? all : allowed).OrderBy(c => c.Allowed ? 0 : 1).ThenBy(c => c.Name))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(c.Rarity == 3 ? new Vector4(0.5f, 0.7f, 1f, 1f) : c.Rarity == 2 ? new Vector4(0.5f, 1f, 0.5f, 1f) : Dim, c.Name);
            if (ImGui.BeginPopupContextItem($"##ctx{c.Container}{c.Slot}"))
            {
                var prot = C.NeverTurnIn.Contains(c.ItemId);
                if (ImGui.MenuItem(prot ? "Unprotect" : "Never turn this in"))
                {
                    if (prot) C.NeverTurnIn.Remove(c.ItemId); else C.NeverTurnIn.Add(c.ItemId);
                    C.Save();
                }
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn(); ImGui.TextUnformatted(c.ItemLevel.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Hq ? "HQ" : "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(c.MateriaCount > 0 ? c.MateriaCount.ToString() : "");
            ImGui.TableNextColumn(); ImGui.TextDisabled(c.Container.ToString().Replace("Inventory", "Bag ").Replace("Armory", "Armoury "));
            ImGui.TableNextColumn();
            if (c.Allowed) ImGui.TextColored(Ok, c.MateriaCount > 0 ? "retrieve, then turn in" : "turn in");
            else ImGui.TextColored(Dim, c.Blocked!);
        }
        ImGui.EndTable();
    }

    // ================================================================ scrips

    private string scripItem = "";
    private int scripCount = 1;
    private string scripMsg = "";

    private static readonly (string Name, uint Territory, uint Aetheryte, uint Aethernet)[] Hubs =
    {
        ("Solution Nine (Nexus Arcade)", 1186, 217, 235),
        ("Old Gridania", 132, 2, 26),
    };

    private void DrawScrips()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) { ImGui.TextUnformatted("Log in first."); return; }
        ImGui.TextWrapped("Turns in the collectables in your bags at the Collectable Appraiser and buys an item at the Scrip Exchange, " +
                          "buying before a turn-in would push scrips over 4,000. Material Stash's Scrip goals use this; you can also run it here " +
                          "or with /turnin scrips <count> <item>.");
        foreach (var (id, name) in ScripData.ScripNames)
        {
            ImGui.TextUnformatted($"{name}: {ScripData.Scrips(id):N0} / {ScripData.ScripCap:N0}");
            var held = ScripData.CollectablesInBags(Plugin.DataManager, id).Count;
            if (held > 0) { ImGui.SameLine(); ImGui.TextColored(Ok, $"({held} collectable(s) to turn in)"); }
        }

        var r = plugin.ScripRunner;
        ImGui.Separator();
        if (r.Busy) { if (ImGui.Button("Stop##scrip")) r.Stop(); ImGui.SameLine(); ImGui.TextUnformatted(r.Status); }
        else
        {
            ImGui.SetNextItemWidth(240);
            ImGui.InputTextWithHint("##scripitem", "Item to buy (exact name)", ref scripItem, 100);
            ImGui.SameLine(); ImGui.SetNextItemWidth(90);
            ImGui.InputInt("how many##scripcount", ref scripCount, 1, 10);
            ImGui.SameLine();
            if (ImGui.Button("Run"))
            {
                var hit = ScripData.Shop(Plugin.DataManager).Values.FirstOrDefault(e => e.Name.Equals(scripItem.Trim(), StringComparison.OrdinalIgnoreCase));
                scripMsg = hit == null ? $"\"{scripItem}\" isn't sold for scrips." : r.Start(hit.ItemId, Math.Max(0, scripCount));
            }
            var e2 = ScripData.Shop(Plugin.DataManager).Values.FirstOrDefault(e => e.Name.Equals(scripItem.Trim(), StringComparison.OrdinalIgnoreCase));
            if (e2 != null) ImGui.TextDisabled($"{e2.Cost} {ScripData.ScripNames[e2.CurrencyItemId]} each.");
            if (scripMsg.Length > 0) ImGui.TextColored(Warn, scripMsg);
            else if (r.Status.Length > 0) ImGui.TextDisabled(r.Status);
        }

        ImGui.Separator();
        var changed = false;
        var hubIndex = Array.FindIndex(Hubs, h => h.Territory == C.HubTerritory && h.Aetheryte == C.HubAetheryte && h.Aethernet == C.HubAethernet);
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Scrip hub", hubIndex >= 0 ? Hubs[hubIndex].Name : $"Custom (territory {C.HubTerritory})"))
        {
            foreach (var h in Hubs)
                if (ImGui.Selectable(h.Name, h.Territory == C.HubTerritory))
                { C.HubTerritory = h.Territory; C.HubAetheryte = h.Aetheryte; C.HubAethernet = h.Aethernet; changed = true; }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lifestream teleports here and takes the aethernet shard next to the appraiser. If the NPCs are already in sight, it walks straight to them.");
        var an = C.AppraiserName; ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Appraiser NPC name", ref an, 64)) { C.AppraiserName = an; changed = true; }
        var en = C.ExchangeName; ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Scrip exchange NPC name", ref en, 64)) { C.ExchangeName = en; changed = true; }
        var d = C.ScripStepDelay; ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Pause between clicks (s)", ref d, 0.1f, 0.5f, "%.1f")) { C.ScripStepDelay = Math.Clamp(d, 0.2f, 3f); changed = true; }
        if (C.ShopSpots.Count > 0 && ImGui.SmallButton($"Forget {C.ShopSpots.Count} remembered shop page(s)")) { C.ShopSpots.Clear(); changed = true; }
        if (changed) C.Save();
    }
}
