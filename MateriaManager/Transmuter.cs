using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MateriaManager.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MateriaManager;

/// <summary>Five materia handed in together.</summary>
public sealed record Batch(int Grade, List<MateriaInfo> Items);

/// <summary>
/// Plans transmutation batches from your bags (fodder types only, never below each Keep), and takes
/// you to Mutamix Bubblypots (Central Thanalan): teleport, walk, talk, pick the transmute option.
/// The transmute window itself isn't automated yet: when it opens, its layout is dumped (to
/// pluginConfigs/MateriaManager/dumps) so the hand-in can be automated, and you hand in the planned
/// batches yourself.
/// </summary>
public sealed unsafe class Transmuter : IDisposable
{
    public const string Npc = "Mutamix Bubblypots";
    private const string Aetheryte = "Black Brush Station";

    public enum Phase { Idle, Teleporting, Walking, Talking, Adding, Confirming, Done }

    private readonly Configuration config;
    private readonly VNavIpc vnav = new();
    private readonly LifestreamIpc lifestream = new();
    private DateTime nextAction = DateTime.MinValue, phaseStarted, lastProgress, talkedAt = DateTime.MinValue;
    private float bestDistance = float.MaxValue;
    private int tries;
    private HashSet<string> visibleBefore = new();

    public Phase State { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => State is Phase.Teleporting or Phase.Walking or Phase.Talking or Phase.Adding or Phase.Confirming;
    public int BatchesDone { get; private set; }
    public List<string> Results { get; } = new();
    private Batch? batch;
    private int added, stepTries;
    private string lastMenu = "";
    private readonly Dictionary<uint, int> selectedOf = new();
    private uint pendingItem;
    private Dictionary<uint, int> bagsBefore = new();

    public Transmuter(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    // ================================================================ planning

    /// <summary>Batches of five from your bags, lowest grade first, spending the biggest surpluses first.</summary>
    public List<Batch> Plan()
    {
        var bags = MateriaData.Count(MateriaData.Bags);
        var all = MateriaData.All();
        var spare = new List<(MateriaInfo Info, int Count)>();
        foreach (var (id, count) in bags)
        {
            if (!config.Rules.TryGetValue(id, out var rule) || !rule.Fodder) continue;
            var mi = all[id];
            if (mi.Grade > config.MaxTransmuteGrade) continue;                    // master cap
            if (config.SkipTopGrade && mi.Grade >= mi.TopGrade) continue;         // can't upgrade
            var n = count - Math.Max(0, rule.Keep);
            if (config.ReserveForRetainer && config.TargetFor(all[id]) is { } t && config.RetainerId != 0)
                n -= Math.Max(0, t.Max - config.RetainerSeen.GetValueOrDefault(id));   // the retainer still needs these
            if (n > 0) spare.Add((all[id], n));
        }
        var batches = new List<Batch>();
        var groups = config.SameGradeBatches
            ? spare.GroupBy(s => s.Info.Grade).Select(g => (Grade: g.Key, Items: g.ToList()))
            : new[] { (Grade: 0, Items: spare) };
        foreach (var (grade, items) in groups.OrderBy(g => g.Grade))   // lowest first: an upgrade can complete a batch of the next grade
        {
            // Smallest stacks first, used up completely: each emptied stack frees a bag slot
            // (3 Gatherer's Guile + 2 of a 48 stack before touching the rest of the 48).
            var pieces = new List<MateriaInfo>();
            foreach (var (info, count) in items.OrderBy(i => i.Count).ThenBy(i => i.Info.Type))
                for (var k = 0; k < count; k++) pieces.Add(info);
            for (var i = 0; i + 5 <= pieces.Count; i += 5)
                batches.Add(new Batch(grade == 0 ? pieces[i].Grade : grade, pieces.GetRange(i, 5)));
        }
        return batches;
    }

    // ================================================================ going there

    public void Start()
    {
        if (Busy) return;
        BatchesDone = 0; Results.Clear(); batch = null;
        tries = 0; talkedAt = DateTime.MinValue; bestDistance = float.MaxValue;
        Set(FindNpc() != null || NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType) != null ? Phase.Walking : Phase.Teleporting,
            "Heading to Mutamix Bubblypots...");
        lastProgress = DateTime.UtcNow;
        if (State == Phase.Teleporting) Teleport();
    }

    public void Stop() { vnav.Stop(); Set(Phase.Idle, "Stopped."); }

    private void Set(Phase p, string status) { State = p; Status = status; phaseStarted = DateTime.UtcNow; }

    private void Fail(string why) { vnav.Stop(); Set(Phase.Idle, why); Plugin.ChatGui.PrintError($"[Materia] {why}"); }

    private void Teleport()
    {
        if (config.UseLifestream && lifestream.IsLoaded && lifestream.Execute(Aetheryte)) return;
        var row = Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English)
            .FirstOrDefault(a => a.IsAetheryte && a.PlaceName.ValueNullable?.Name.ExtractText() == Aetheryte);
        if (row.RowId == 0 || Telepo.Instance() == null || !Telepo.Instance()->Teleport(row.RowId, 0))
            Fail($"Couldn't teleport to {Aetheryte}.");
    }

    private IGameObject? FindNpc() =>
        Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventNpc && o.Name.TextValue.Equals(Npc, StringComparison.OrdinalIgnoreCase));

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < nextAction) return;
        try
        {
            switch (State)
            {
                case Phase.Teleporting: TickTeleport(); break;
                case Phase.Walking: TickWalk(); break;
                case Phase.Talking: TickTalk(); break;
                case Phase.Adding: TickAdd(); break;
                case Phase.Confirming: TickConfirm(); break;
            }
        }
        catch (Exception ex) { Fail("Error: " + ex.Message); Plugin.Log.Error(ex, "Transmuter failed"); }
    }

    private void TickTeleport()
    {
        nextAction = DateTime.UtcNow.AddSeconds(1);
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.Casting] || lifestream.IsBusy) return;
        if (NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType) != null) { Set(Phase.Walking, "Walking to Mutamix..."); lastProgress = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 30) Fail("Teleport didn't arrive in Central Thanalan.");
    }

    private void TickWalk()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || !vnav.IsReady) return;
        var npc = FindNpc();
        if (npc != null && Vector3.Distance(me.Position, npc.Position) <= 4.5f) { vnav.Stop(); Set(Phase.Talking, "Talking to Mutamix..."); return; }
        var goal = npc?.Position ?? NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType);
        if (goal == null) { Fail("Can't find Mutamix here."); return; }
        var target = vnav.NearestPoint(goal.Value, 6, 6) ?? goal.Value;
        var dist = Vector3.Distance(me.Position, target);
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 10) { Fail("vnavmesh isn't getting closer to Mutamix."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(target, 1f);
    }

    private void TickTalk()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        var menu = GameUi.Get("SelectString");
        var icon = false;
        if (menu == null) { menu = GameUi.Get("SelectIconString"); icon = menu != null; }
        if (menu != null && talkedAt != DateTime.MinValue)
        {
            var rows = GameUi.MenuEntries(menu, icon);
            var row = rows.FindIndex(r => r.Contains("transmut", StringComparison.OrdinalIgnoreCase) || r.Contains("Materia", StringComparison.OrdinalIgnoreCase));
            if (row < 0) { GameUi.Dump(icon ? "SelectIconString" : "SelectString"); Fail("No transmute option in Mutamix's menu (dump saved): " + string.Join(" | ", rows)); return; }
            menu->FireCallbackInt(row);
            nextAction = DateTime.UtcNow.AddSeconds(1.5);
            return;
        }

        // The transmute window is the game's multi-item hand-in window.
        if (GameUi.IsOpen(TradeWindow)) { NextBatch(); return; }
        if (talkedAt != DateTime.MinValue && (DateTime.UtcNow - talkedAt).TotalSeconds < 8) return;

        if (++tries > 3) { Fail("Couldn't talk to Mutamix."); return; }
        var npc = FindNpc();
        if (npc == null) { Fail("Lost sight of Mutamix."); return; }
        visibleBefore = VisibleAddons();
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(1.5);
    }

    // ================================================================ hand-in
    // Window: "TradeMultiple" (value [0] = "n/5", Confirm button). Materia goes in through each
    // item's right-click menu while the window is open; the inventory shows "Transmutation".

    private const string TradeWindow = "TradeMultiple";

    private void NextBatch()
    {
        if (BatchesDone >= config.MaxBatchesPerRun) { Finish($"Stopped after {BatchesDone} batch(es) (the per-trip cap)."); return; }
        batch = Plan().FirstOrDefault();
        if (batch == null) { Finish($"Done: {BatchesDone} batch(es) transmuted."); return; }
        added = 0; stepTries = 0; selectedOf.Clear(); pendingItem = 0;
        bagsBefore = MateriaData.Count(MateriaData.Bags);
        Set(Phase.Adding, $"Batch {BatchesDone + 1}: adding {string.Join(", ", batch.Items.GroupBy(i => i.Name).Select(g => $"{g.Count()} {g.Key}"))}");
    }

    private void Finish(string msg)
    {
        Set(Phase.Done, msg);
        Plugin.ChatGui.Print($"[Materia] {msg}");
        var w = GameUi.Get(TradeWindow);
        if (w != null) w->FireCallbackInt(-1);
    }

    private static int Selected(AtkUnitBase* w)
    {
        var s = GameUi.Str(w, 0);   // "3/5"
        var slash = s.IndexOf('/');
        return slash > 0 && int.TryParse(s[..slash], out var n) ? n : -1;
    }

    private void TickAdd()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        var w = GameUi.Get(TradeWindow);
        if (w == null) { Fail("The transmute window closed."); return; }
        var yesno = GameUi.Get("SelectYesno");
        if (yesno != null) { yesno->FireCallbackInt(0); return; }
        // How many to add from this stack: all of this materia the batch still needs, in one go.
        var numeric = GameUi.Get("InputNumeric");
        if (numeric != null)
        {
            var need = Math.Max(1, NeededOf(pendingItem));
            numeric->FireCallbackInt(need);
            nextAction = DateTime.UtcNow.AddSeconds(0.6);
            return;
        }

        var n = Selected(w);
        if (n >= 5) { Set(Phase.Confirming, $"Batch {BatchesDone + 1}: confirming..."); stepTries = 0; return; }
        if (n > added) { selectedOf[pendingItem] = selectedOf.GetValueOrDefault(pendingItem) + (n - added); added = n; stepTries = 0; }

        var menu = GameUi.Get("ContextMenu");
        if (menu != null)
        {
            // The add option; never "Remove from ..." / "Return" (once a stack is in the window, its
            // menu also offers removing it, which also mentions transmutation).
            var index = -1;
            var entries = new List<string>();
            for (var i = 7; i < menu->AtkValuesCount; i++)
            {
                var t = GameUi.Str(menu, i);
                if (t.Length == 0) continue;
                entries.Add(t);
                if (index >= 0 || t.Contains("Remove", StringComparison.OrdinalIgnoreCase) || t.Contains("Return", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("Cancel", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Contains("Move to", StringComparison.OrdinalIgnoreCase) || t.Contains("Transmut", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Select", StringComparison.OrdinalIgnoreCase) || t.Equals("Add", StringComparison.OrdinalIgnoreCase)) index = i - 8;
            }
            lastMenu = string.Join(" | ", entries);
            if (index < 0) { GameUi.Dump("ContextMenu", "item menu with the transmute window open"); menu->FireCallbackInt(-1); Fail($"No add option in the item menu: {lastMenu} (dump saved)."); return; }
            GameUi.Fire(menu, 0, index, 0);
            nextAction = DateTime.UtcNow.AddSeconds(0.7);
            return;
        }

        if (++stepTries > 6) { GameUi.Dump(TradeWindow, $"stuck at {n}/5; last item menu: {lastMenu}"); Fail($"Couldn't add materia ({n}/5 selected; last menu: {lastMenu}; dump saved)."); return; }
        // Next: the first materia in the batch that still needs pieces.
        var want = batch!.Items.Select(i => i.ItemId).Distinct().FirstOrDefault(id => NeededOf(id) > 0);
        if (want == 0) { Set(Phase.Confirming, $"Batch {BatchesDone + 1}: confirming..."); stepTries = 0; return; }
        pendingItem = want;
        if (FindStack(want) is not { } slot) { Fail($"{MateriaData.All()[want].Name} isn't in your bags anymore."); return; }
        AgentInventoryContext.Instance()->OpenForItemSlot(slot.Type, slot.Slot, 0, InventoryOwnerId());
        nextAction = DateTime.UtcNow.AddSeconds(0.7);
    }

    private int NeededOf(uint itemId) =>
        batch == null ? 0 : batch.Items.Count(i => i.ItemId == itemId) - selectedOf.GetValueOrDefault(itemId);

    private void TickConfirm()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.6);
        var yesno = GameUi.Get("SelectYesno");
        if (yesno != null) { yesno->FireCallbackInt(0); nextAction = DateTime.UtcNow.AddSeconds(1); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }

        // Done when five materia left the bags.
        var now = MateriaData.Count(MateriaData.Bags);
        var gone = bagsBefore.Sum(kv => Math.Max(0, kv.Value - now.GetValueOrDefault(kv.Key)));
        if (gone >= 5)
        {
            var got = now.Where(kv => kv.Value > bagsBefore.GetValueOrDefault(kv.Key)).Select(kv => MateriaData.All()[kv.Key]).FirstOrDefault();
            BatchesDone++;
            if (got != null)
            {
                // Keep rolling: a result at a grade you're transmuting is used too, unless you set it yourself.
                if (config.RollResults && !(config.Rules.TryGetValue(got.ItemId, out var gr) && gr.UserSet) &&
                    config.Rules.Any(r => r.Value.Fodder && MateriaData.All().TryGetValue(r.Key, out var mi) && mi.Grade == got.Grade))
                {
                    if (!config.Rules.TryGetValue(got.ItemId, out var nr)) config.Rules[got.ItemId] = nr = new FodderRule();
                    nr.Fodder = true;
                    config.Save();
                }
                var up = got.Grade > batch!.Grade ? " (upgrade!)" : "";
                Results.Add($"{got.Name}{up}");
                Plugin.ChatGui.Print($"[Materia] Batch {BatchesDone}: got {got.Name}{up}");
            }
            if (GameUi.IsOpen(TradeWindow)) { NextBatch(); return; }
            // Window closed after the trade: talk to him again.
            tries = 0; talkedAt = DateTime.MinValue;
            Set(Phase.Talking, "Talking to Mutamix again...");
            return;
        }

        var w = GameUi.Get(TradeWindow);
        if (w == null) return;   // a cutscene/dialogue in between
        if (++stepTries > 8) { GameUi.Dump(TradeWindow, "confirm didn't go through"); Fail("Confirm didn't go through (dump saved)."); return; }
        var btn = GameUi.FindButton(w, "Confirm");
        if (btn != null) GameUi.ClickButton(w, btn);
        else { GameUi.Dump(TradeWindow, "no Confirm button found"); Fail("No Confirm button found (dump saved)."); }
        nextAction = DateTime.UtcNow.AddSeconds(1.2);
    }

    private static (FFXIVClientStructs.FFXIV.Client.Game.InventoryType Type, int Slot)? FindStack(uint itemId)
    {
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        foreach (var t in MateriaData.Bags)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId == itemId && s->Quantity > 0) return (t, i);
            }
        }
        return null;
    }

    private static uint InventoryOwnerId()
    {
        var mgr = RaptureAtkUnitManager.Instance();
        foreach (var name in new[] { "InventoryExpansion", "InventoryLarge", "Inventory" })
        {
            var a = mgr->GetAddonByName(name);
            if (a != null && a->IsVisible) return a->Id;
        }
        return 0;
    }

    /// <summary>Names of every visible game window (to spot the one that opened).</summary>
    private static HashSet<string> VisibleAddons()
    {
        var result = new HashSet<string>();
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null) return result;
        var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var u = list->Entries[i].Value;
            if (u != null && u->IsVisible) result.Add(u->NameString);
        }
        return result;
    }
}
