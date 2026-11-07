using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace RetainerGear;

/// <summary>One slot on one retainer: what they wear, the best available, and whether you already own it.</summary>
public sealed record SlotPlan(RetainerRecord Retainer, int Slot, string SlotName, uint CurrentId, string CurrentName, int CurrentIlvl,
    GearOption? Best, string? OwnedWhere)
{
    public bool NeedsUpgrade => Best != null && Best.ItemLevel > CurrentIlvl;
}

public sealed record RetainerPlan(RetainerRecord Retainer, string Job, bool Gatherer, List<SlotPlan> Slots)
{
    public double CurrentAvg => Slots.Count == 0 ? 0 : Slots.Average(s => (double)s.CurrentIlvl);
    public double TargetAvg => Slots.Count == 0 ? 0 : Slots.Average(s => (double)Math.Max(s.CurrentIlvl, s.Best?.ItemLevel ?? 0));
    public bool GearKnown => Retainer.GearSeen != null;
}

/// <summary>Works out every retainer's upgrades and which of them you already own.</summary>
public static unsafe class Planner
{
    private static readonly int[] Slots = { 0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12 };
    private static readonly InventoryType[] PlayerContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    public static List<RetainerPlan> Build(Plugin plugin)
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var retainers = plugin.Tracker.Retainers();

        // Spare copies you own: your bags/armoury/saddlebag (live) + retainers' inventories (as last seen).
        var owned = new Dictionary<uint, List<string>>();
        void Own(uint id, string where) { if (!owned.TryGetValue(id, out var l)) owned[id] = l = new(); l.Add(where); }
        var im = InventoryManager.Instance();
        if (im != null)
            foreach (var type in PlayerContainers)
            {
                var c = im->GetInventoryContainer(type);
                if (c == null || !c->IsLoaded) continue;
                var where = type.ToString().StartsWith("Armory") ? "your armoury" : type.ToString().Contains("Saddle") ? "your saddlebag" : "your bags";
                for (var i = 0; i < c->Size; i++)
                {
                    var it = c->GetInventorySlot(i);
                    if (it != null && it->ItemId != 0) Own(it->ItemId % 1_000_000, where);
                }
            }
        foreach (var r in retainers)
            foreach (var id in r.Inventory) Own(id, $"{r.Name}'s inventory");

        var plans = new List<RetainerPlan>();
        foreach (var r in retainers.Where(r => r.ClassJob != 0))
        {
            var job = jobs.GetRowOrDefault(r.ClassJob);
            var gatherer = r.ClassJob is >= 16 and <= 18;   // MIN, BTN, FSH
            var options = plugin.Sources.For(r.ClassJob, r.Level);
            var slots = new List<SlotPlan>();

            // Rings: the two best (a second copy only if the item isn't unique).
            var rings = options.Where(o => o.Slot == 12).ToList();
            var ring1 = rings.FirstOrDefault();
            var ring2 = ring1 == null ? null : ring1.Unique ? rings.FirstOrDefault(o => o.ItemId != ring1.ItemId) : ring1;
            var currentRings = new[] { 11, 12 }.Select(s => r.Equipped.TryGetValue(s, out var id) ? id : 0u)
                .OrderByDescending(id => Ilvl(items, id)).ToArray();

            var bestMain = options.FirstOrDefault(o => o.Slot == 0);
            var curMain = r.Equipped.TryGetValue(0, out var mid) ? items.GetRowOrDefault(mid) : null;
            var twoHanded = (bestMain?.TwoHanded ?? false) && (curMain == null || curMain.Value.EquipSlotCategory.ValueNullable?.OffHand == -1);

            for (var i = 0; i < Slots.Length; i++)
            {
                var slot = Slots[i];
                if (slot == 1 && twoHanded) continue;
                uint cur; GearOption? best;
                if (slot == 11) { cur = currentRings[0]; best = ring1; }
                else if (slot == 12) { cur = currentRings[1]; best = ring2; }
                else { cur = r.Equipped.TryGetValue(slot, out var id) ? id : 0; best = options.FirstOrDefault(o => o.Slot == slot); }
                if (slot == 1 && best == null && cur == 0) continue; // no off hand for this job

                var curRow = cur == 0 ? (Item?)null : items.GetRowOrDefault(cur);
                var curIlvl = Ilvl(items, cur);
                string? where = null;
                if (best != null && best.ItemLevel > curIlvl && owned.TryGetValue(best.ItemId, out var spots) && spots.Count > 0)
                {
                    where = spots[0];
                    spots.RemoveAt(0);   // each spare copy goes to one retainer
                }
                slots.Add(new SlotPlan(r, slot, GearSources.SlotNames[slot], cur, curRow?.Name.ExtractText() ?? "(empty)", curIlvl, best, where));
            }
            plans.Add(new RetainerPlan(r, job?.Abbreviation.ExtractText() ?? "?", gatherer, slots));
        }
        return plans;
    }

    private static int Ilvl(Lumina.Excel.ExcelSheet<Item> items, uint id) =>
        id == 0 ? 0 : (int)(items.GetRowOrDefault(id)?.LevelItem.RowId ?? 0);
}
