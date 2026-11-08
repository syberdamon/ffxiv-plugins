using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MaterialStash.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MaterialStash;

/// <summary>
/// "Keep the look" items you approved in the Clean-up tab, put away for you: travels to an inn room
/// (Lifestream, "/li inn"), walks to the Armoire (vnavmesh) and stores what fits there (free), then
/// the Glamour Dresser for the rest (one Glamour Prism each, the game's rule).
/// Armoire: the game's own store call (Cabinet.StoreCabinetItem). Dresser: the same event the
/// Glamour Creation list sends when you pick an item, then its confirmation (as Spring Cleaning does).
/// Only items in your bags (the clean-up run brings them there). Stops on anything unexpected.
/// </summary>
public sealed unsafe class Wardrobe : IDisposable
{
    private enum Phase { Idle, Travel, Repair, Walk, Open, Store, Leave }

    public const uint GlamourPrism = 21800;
    private const string DresserName = "Glamour Dresser", ArmoireName = "Armoire";
    private const double PhaseTimeout = 30, TravelTimeout = 240;

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly Cleanup cleanup;
    private readonly VNavIpc vnav = new();
    private readonly LifestreamIpc lifestream = new();

    private Phase phase = Phase.Idle;
    private DateTime travelIdleSince = DateTime.MinValue;
    private DateTime next = DateTime.MinValue, phaseStart = DateTime.MinValue;
    private bool armoireTurn, travelAsked;
    private int stored, tries, repaired;
    private (InventoryType Type, int Slot, ushort Condition)? repairing;
    private (InventoryType Type, int Slot, uint Item)? current;
    private readonly HashSet<uint> skip = new();
    private readonly List<string> refused = new();

    public string Status { get; private set; } = "";
    public bool Busy => phase != Phase.Idle;

    public Wardrobe(Configuration config, Tracker tracker, Cleanup cleanup)
    {
        this.config = config;
        this.tracker = tracker;
        this.cleanup = cleanup;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) vnav.Stop();
    }

    public bool LifestreamReady => lifestream.IsLoaded;
    public bool VNavReady => vnav.IsLoaded;

    /// <summary>Approved "keep the look" stacks in your bags (and whether each goes in the Armoire).</summary>
    public List<(Tracker.Stack Stack, bool Armoire)> ToStore()
    {
        var me = tracker.Me();
        if (me == null) return new();
        return Tracker.Stacks(Tracker.Bags)
            .Where(s => !skip.Contains(s.ItemId) && cleanup.ApprovedAction(s.ItemId, me) == Verdict.Dresser)
            .Select(s => (s, cleanup.CabinetOf(s.ItemId) != 0))
            .ToList();
    }

    public static int Prisms() => InventoryManager.Instance() is var im && im != null ? im->GetInventoryItemCount(GlamourPrism) : 0;

    public static bool InInn()
    {
        var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        return row?.TerritoryIntendedUse.RowId == 2; // [Assumption] intended use 2 = inn room
    }

    public void Start()
    {
        if (Busy) return;
        skip.Clear(); refused.Clear(); stored = 0; repaired = 0; repairing = null;
        var list = ToStore();
        if (list.Count == 0) { Status = "No approved \"keep the look\" items in your bags (Run clean-up brings them out first)."; return; }
        var dresserCount = list.Count(x => !x.Armoire);
        if (dresserCount > 0 && Prisms() < dresserCount)
            Plugin.ChatGui.Print($"[Stash] {dresserCount} item(s) need the dresser but you have {Prisms()} Glamour Prism(s): the rest stay in your bags.");
        armoireTurn = list.Any(x => x.Armoire);
        travelAsked = false; travelIdleSince = DateTime.MinValue;
        if (InInn()) Set(Phase.Repair, "Repairing...");
        else if (lifestream.IsLoaded) Set(Phase.Travel, "Going to an inn room (Lifestream)...");
        else { Status = "Go to an inn room first (or install Lifestream so I can take you)."; return; }
    }

    public void Stop()
    {
        if (!Busy) return;
        vnav.Stop();
        phase = Phase.Idle;
        Status = $"Stopped ({stored} stored).";
    }

    private string Station => armoireTurn ? ArmoireName : DresserName;

    private void Set(Phase p, string status)
    {
        phase = p;
        phaseStart = DateTime.UtcNow;
        tries = 0;
        current = null;
        Status = status;
    }

    private void Fail(string why)
    {
        vnav.Stop();
        phase = Phase.Idle;
        Status = $"{why} ({stored} stored)";
        Plugin.ChatGui.PrintError($"[Stash] {Status}");
    }

    private void Done()
    {
        phase = Phase.Idle;
        var left = ToStore().Count;
        Status = $"Stored {stored} look(s)." + (refused.Count > 0 ? $" Not taken: {string.Join(", ", refused.Take(8))}." : "") +
                 (left > 0 ? $" {left} still in your bags." : "");
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (phase == Phase.Idle || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.5);
        try
        {
            var limit = phase == Phase.Travel ? TravelTimeout : PhaseTimeout;
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > limit) { Fail($"Timed out ({phase})."); return; }
            switch (phase)
            {
                case Phase.Travel: TickTravel(); break;
                case Phase.Repair: TickRepair(); break;
                case Phase.Walk: TickWalk(); break;
                case Phase.Open: TickOpen(); break;
                case Phase.Store: TickStore(); break;
                case Phase.Leave: TickLeave(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Material Stash: storing looks failed");
            Fail("Error (see /xllog).");
        }
    }

    private void TickTravel()
    {
        if (cleanup.Using) { phaseStart = DateTime.UtcNow; return; } // let unlearned items finish first
        if (GameUi.IsOpen("RetainerList")) { GameUi.Close("RetainerList"); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        if (GameUi.IsOpen("SelectString")) { GameUi.Close("SelectString"); return; }
        if (!travelAsked)
        {
            if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || Plugin.Condition[ConditionFlag.InCombat]) return;
            if (!lifestream.Execute("inn")) { Fail("Lifestream didn't take the request."); return; }
            travelAsked = true;
            next = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        // Lifestream works in steps (teleport, walk, talk, zone in) and can read idle for a moment between
        // them, so only give up after it has been idle for a while, measured from when it went idle.
        if (lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.ObjectTable.LocalPlayer == null)
        {
            travelIdleSince = DateTime.MinValue;
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > 200) phaseStart = DateTime.UtcNow.AddSeconds(-200); // still moving: keep the clock from running out
            return;
        }
        if (travelIdleSince == DateTime.MinValue) travelIdleSince = DateTime.UtcNow;
        if (!InInn()) { if ((DateTime.UtcNow - travelIdleSince).TotalSeconds > 25) Fail("Lifestream finished but this isn't an inn room."); return; }
        Set(Phase.Repair, "Repairing...");
        next = DateTime.UtcNow.AddSeconds(1.5);
    }

    /// <summary>
    /// The dresser and Armoire only take gear at 100% condition, so anything below gets self-repaired
    /// first (Dark Matter, and a crafter high enough for it, as with the game's Repair window).
    /// An item that won't repair is left in your bags and named at the end.
    /// [Assumption] the game's repair call works on items sitting in your bags, as the Repair window's
    /// own list doesn't show them; if it doesn't, those items are reported instead of stored.
    /// </summary>
    private void TickRepair()
    {
        var im = InventoryManager.Instance();
        var yn = GameUi.Get("SelectYesno");
        if (yn != null)
        {
            var text = GameUi.YesNoText();
            if (text.Contains("repair", StringComparison.OrdinalIgnoreCase) || text.Contains("Dark Matter", StringComparison.OrdinalIgnoreCase))
            {
                yn->FireCallbackInt(0);
                next = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }
            Fail($"Unexpected question while repairing: {text}");
            return;
        }
        if (repairing is { } r)
        {
            var slot = im->GetInventorySlot(r.Type, r.Slot);
            if (slot == null || slot->ItemId == 0 || slot->Condition >= 30000) { repaired++; repairing = null; tries = 0; phaseStart = DateTime.UtcNow; return; }
            if (++tries < 6) return;
            var key = slot->ItemId + (slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? 1_000_000u : 0u);
            skip.Add(key);
            refused.Add($"{Items.Info(slot->ItemId)?.Name ?? $"#{slot->ItemId}"} (needs repair: {slot->Condition / 300}%, couldn't self-repair)");
            repairing = null;
            tries = 0;
            return;
        }
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting]) return;
        foreach (var (st, _) in ToStore())
        {
            var slot = im->GetInventorySlot(st.Type, st.Slot);
            if (slot == null || slot->ItemId == 0 || slot->Condition >= 30000) continue;
            repairing = (st.Type, st.Slot, slot->Condition);
            tries = 0;
            RepairManager.Instance()->RepairItem(st.Type, (ushort)st.Slot, false);
            next = DateTime.UtcNow.AddSeconds(1.5);
            return;
        }
        if (ToStore().Count == 0) { Done(); return; }
        armoireTurn = ToStore().Any(x => x.Armoire);
        Set(Phase.Walk, (repaired > 0 ? $"Repaired {repaired}. " : "") + "Walking to the " + Station + "...");
    }

    private IGameObject? FindStation() =>
        Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventObj && o.IsTargetable && o.Name.TextValue == Station)
            .OrderBy(o => Plugin.ObjectTable.LocalPlayer is { } me ? Vector3.Distance(o.Position, me.Position) : 0)
            .FirstOrDefault();

    private void TickWalk()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var obj = FindStation();
        if (me == null) return;
        if (obj == null) { if ((DateTime.UtcNow - phaseStart).TotalSeconds > 8) Fail($"No {Station} in sight."); return; }
        if (Vector3.Distance(me.Position, obj.Position) <= 3.5f) { vnav.Stop(); Set(Phase.Open, $"Opening the {Station}..."); return; }
        if (!vnav.IsLoaded) { Fail($"Walk up to the {Station} and press Store again (or install vnavmesh)."); return; }
        if (!vnav.IsReady || vnav.IsBusy) return;
        var target = vnav.NearestPoint(obj.Position, 3, 3) ?? obj.Position;
        vnav.MoveCloseTo(target, 2f);
    }

    private bool StationOpen => armoireTurn
        ? GameUi.IsOpen("Cabinet") && UIState.Instance()->Cabinet.IsCabinetLoaded()
        : GameUi.IsOpen("MiragePrismPrismBox") && GameUi.IsOpen("MiragePrismPrismBoxCrystallize") && MirageManager.Instance()->PrismBoxLoaded;

    private void TickOpen()
    {
        if (StationOpen) { Set(Phase.Store, $"Storing in the {Station}..."); next = DateTime.UtcNow.AddSeconds(1); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        if (GameUi.Get("SelectString") is var ss && ss != null)
        {
            // In case the station asks first: take the store / open entry.
            var rows = GameUi.MenuEntries(ss, false);
            var row = rows.FindIndex(r => r.Contains("Store", StringComparison.OrdinalIgnoreCase) || r.Contains("Open", StringComparison.OrdinalIgnoreCase));
            ss->FireCallbackInt(row < 0 ? 0 : row);
            next = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (DateTime.UtcNow < next.AddSeconds(-0.5)) return;
        if (++tries > 4) { Fail($"The {Station} didn't open."); return; }
        var obj = FindStation();
        if (obj == null) { Fail($"Lost sight of the {Station}."); return; }
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)obj.Address, true);
        next = DateTime.UtcNow.AddSeconds(2.5);
    }

    private void TickStore()
    {
        if (!StationOpen) { Fail($"The {Station} closed."); return; }
        var im = InventoryManager.Instance();

        if (current is { } c)
        {
            var slot = im->GetInventorySlot(c.Type, c.Slot);
            if (slot == null || slot->ItemId != c.Item)
            {
                stored++;
                current = null;
                phaseStart = DateTime.UtcNow; // the time limit is per item, not for the whole batch
                return;
            }
            if (!armoireTurn && ConfirmDialog() is var dlg && dlg != null) { dlg->FireCallbackInt(0); next = DateTime.UtcNow.AddSeconds(1); return; }
            if (++tries <= 6) return;
            skip.Add(c.Item + (slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? 1_000_000u : 0u));
            refused.Add(Items.Info(c.Item)?.Name ?? $"#{c.Item}");
            current = null;
            phaseStart = DateTime.UtcNow;
            return;
        }

        var todo = ToStore().Where(x => x.Armoire == armoireTurn).ToList();
        if (todo.Count == 0)
        {
            if (armoireTurn && ToStore().Any(x => !x.Armoire) && Prisms() > 0)
            {
                CloseStation();
                armoireTurn = false;
                Set(Phase.Walk, "Walking to the Glamour Dresser...");
                next = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }
            Set(Phase.Leave, "Closing...");
            return;
        }
        var s = todo[0].Stack;
        var nq = s.ItemId % 1_000_000;
        tries = 0;
        if (armoireTurn)
        {
            if (!UIState.Instance()->Cabinet.StoreCabinetItem(cleanup.CabinetOf(nq))) { skip.Add(s.ItemId); refused.Add((Items.Info(nq)?.Name ?? $"#{nq}") + " (Armoire)"); return; }
        }
        else
        {
            if (Prisms() == 0) { Set(Phase.Leave, "Out of Glamour Prisms."); return; }
            var index = CrystallizeIndex(s.Type, s.Slot, nq);
            if (index < 0) { skip.Add(s.ItemId); refused.Add((Items.Info(nq)?.Name ?? $"#{nq}") + " (not in the dresser's list)"); return; }
            var agent = AgentMiragePrismPrismBox.Instance();
            var values = stackalloc AtkValue[3];
            values[0].SetUInt(14);
            values[1].SetUInt((uint)index);
            values[2].SetUInt(0);
            AtkValue ret = default;
            agent->ReceiveEvent(&ret, values, 3, 0);
        }
        current = (s.Type, s.Slot, nq);
        next = DateTime.UtcNow.AddSeconds(1.2);
    }

    /// <summary>The bag item's row in the Glamour Creation list (which lists your inventory), or -1.</summary>
    private static int CrystallizeIndex(InventoryType type, int slot, uint nq)
    {
        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent == null || agent->Data == null) return -1;
        var count = Math.Min((int)agent->Data->CrystallizeItemCount, 140);
        for (var i = 0; i < count; i++)
        {
            var row = agent->Data->CrystallizeItems[i];
            if (row.Inventory == type && row.Slot == slot && row.ItemId % 1_000_000 == nq) return i;
        }
        return -1;
    }

    /// <summary>The dresser's "store this?" confirmation, when it's up.</summary>
    private static AtkUnitBase* ConfirmDialog()
    {
        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent == null || agent->ContextAddonId is 0 or > ushort.MaxValue) return null;
        var a = RaptureAtkUnitManager.Instance()->AtkUnitManager.GetAddonById((ushort)agent->ContextAddonId);
        return a != null && a->IsVisible ? a : null;
    }

    private static void CloseStation()
    {
        foreach (var n in new[] { "MiragePrismPrismBoxCrystallize", "MiragePrismPrismBox", "Cabinet" }) GameUi.Close(n);
    }

    private void TickLeave()
    {
        if (GameUi.IsOpen("MiragePrismPrismBox") || GameUi.IsOpen("Cabinet")) { CloseStation(); return; }
        var why = Status;
        Done();
        if (why.StartsWith("Out of")) Status = why + " " + Status;
    }
}
