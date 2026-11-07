using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace RetainerGear;

/// <summary>
/// Keeps the retainer list (name, job, level) current, and records a retainer's equipped gear and
/// inventory while you have that retainer open (the game only shares them then). Also learns which
/// dungeon gear drops where: gear that lands in your bags inside a dungeon.
/// </summary>
public sealed unsafe class RetainerTracker : IDisposable
{
    private static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };

    private readonly Configuration config;
    private DateTime nextPoll = DateTime.MinValue;
    private string lastSaved = "";

    /// <summary>Bumped whenever recorded gear changes, so views can recompute.</summary>
    public int Revision { get; private set; }

    public RetainerTracker(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    public static ulong ContentId()
    {
        var ps = PlayerState.Instance();
        return ps == null ? 0 : ps->ContentId;
    }

    public List<RetainerRecord> Retainers()
    {
        var cid = ContentId();
        if (cid == 0) return new();
        if (!config.Characters.TryGetValue(cid, out var list)) config.Characters[cid] = list = new();
        return list;
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        try { Poll(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "RetainerGear poll failed"); }
    }

    private void Poll()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady || ContentId() == 0) return;
        var list = Retainers();
        var changed = false;

        // Roster: names, jobs, levels (available any time after the retainer bell has been used once).
        var count = rm->GetRetainerCount();
        var seen = new HashSet<ulong>();
        for (uint i = 0; i < count; i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            seen.Add(r->RetainerId);
            var rec = list.FirstOrDefault(x => x.Id == r->RetainerId);
            if (rec == null) { rec = new RetainerRecord { Id = r->RetainerId }; list.Add(rec); changed = true; }
            var name = MemoryHelper.ReadStringNullTerminated((nint)r + 8);
            if (rec.Name != name || rec.ClassJob != r->ClassJob || rec.Level != r->Level)
            {
                rec.Name = name; rec.ClassJob = r->ClassJob; rec.Level = r->Level;
                changed = true;
            }
        }
        if (seen.Count > 0 && list.RemoveAll(x => !seen.Contains(x.Id)) > 0) changed = true;

        // The open retainer: equipment and inventory.
        var active = rm->GetActiveRetainer();
        var im = InventoryManager.Instance();
        if (active != null && im != null && list.FirstOrDefault(x => x.Id == active->RetainerId) is { } open)
        {
            var eq = im->GetInventoryContainer(InventoryType.RetainerEquippedItems);
            if (eq != null && eq->IsLoaded)
            {
                var gear = new Dictionary<int, uint>();
                for (var s = 0; s < eq->Size; s++)
                {
                    var it = eq->GetInventorySlot(s);
                    if (it != null && it->ItemId != 0) gear[s] = it->ItemId % 1_000_000;
                }
                if (!gear.OrderBy(k => k.Key).SequenceEqual(open.Equipped.OrderBy(k => k.Key)))
                {
                    open.Equipped = gear;
                    changed = true;
                }
                open.GearSeen = DateTime.Now;
            }

            var items = new List<uint>();
            var pagesLoaded = false;
            foreach (var page in RetainerPages)
            {
                var c = im->GetInventoryContainer(page);
                if (c == null || !c->IsLoaded) continue;
                pagesLoaded = true;
                for (var s = 0; s < c->Size; s++)
                {
                    var it = c->GetInventorySlot(s);
                    if (it != null && it->ItemId != 0) items.Add(it->ItemId % 1_000_000);
                }
            }
            if (pagesLoaded)
            {
                items.Sort();
                if (!items.SequenceEqual(open.Inventory)) { open.Inventory = items; changed = true; }
                open.InventorySeen = DateTime.Now;
            }
        }

        if (changed)
        {
            Revision++;
            var snapshot = string.Join("|", list.Select(x => $"{x.Id}:{x.ClassJob}:{x.Level}:{string.Join(",", x.Equipped.Select(k => $"{k.Key}={k.Value}"))}:{x.Inventory.Count}"));
            if (snapshot != lastSaved) { lastSaved = snapshot; config.Save(); }
        }
    }

    /// <summary>Gear that lands in your bags inside a dungeon is remembered as dropping there.</summary>
    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        try
        {
            if (!Plugin.Condition[ConditionFlag.BoundByDuty]) return;
            var territory = Plugin.ClientState.TerritoryType;
            var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
            if (tt?.ContentFinderCondition.ValueNullable is not { } cfc || cfc.ContentType.RowId != 2) return; // dungeons only
            var items = Plugin.DataManager.GetExcelSheet<Item>();
            var changed = false;
            foreach (var e in events)
            {
                if (e.Type != GameInventoryEvent.Added) continue;
                var id = e.Item.ItemId % 1_000_000;
                if (items.GetRowOrDefault(id) is not { } row || row.EquipSlotCategory.RowId == 0) continue;
                if (config.LearnedDrops.TryGetValue(id, out var t) && t == territory) continue;
                config.LearnedDrops[id] = territory;
                changed = true;
            }
            if (changed) { config.Save(); Revision++; }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "RetainerGear: drop learning failed"); }
    }
}
