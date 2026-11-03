using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace TurnInManager.GrandCompany;

/// <summary>A piece of gear in your bags/armoury and whether the guardrails allow turning it in.</summary>
public sealed record GearCandidate(
    InventoryType Container, int Slot, uint ItemId, string Name, int ItemLevel, byte Rarity, bool Hq,
    int MateriaCount, bool InGearset, string? Blocked)
{
    public bool Allowed => Blocked == null;
}

public static unsafe class GcData
{
    public sealed record GcInfo(byte Id, string Name, string Personnel, string Quartermaster);

    // NPC names are the English names in game data (used to find them on the map).
    public static readonly GcInfo[] Companies =
    {
        new(1, "Maelstrom", "Storm Personnel Officer", "Storm Quartermaster"),
        new(2, "Order of the Twin Adder", "Serpent Personnel Officer", "Serpent Quartermaster"),
        new(3, "Immortal Flames", "Flame Personnel Officer", "Flame Quartermaster"),
    };

    public const uint VentureId = 21072;

    public static GcInfo? Current()
    {
        var ps = PlayerState.Instance();
        if (ps == null) return null;
        return Companies.FirstOrDefault(c => c.Id == ps->GrandCompany);
    }

    public static int Rank()
    {
        var ps = PlayerState.Instance();
        if (ps == null) return 0;
        return ps->GrandCompany == 0 ? 0 : ps->GetGrandCompanyRank();
    }

    public static int Seals()
    {
        var gc = Current();
        var im = InventoryManager.Instance();
        return gc == null || im == null ? 0 : (int)im->GetCompanySeals(gc.Id);
    }

    public static int SealCap()
    {
        var row = Plugin.DataManager.GetExcelSheet<GrandCompanyRank>().GetRowOrDefault((uint)Rank());
        return row == null ? 0 : (int)row.Value.MaxSeals;
    }

    /// <summary>Seal Sweetener etc. raise rewards: [Assumption] statuses 414 / 1078, param = percent.</summary>
    public static float SealMultiplier()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return 1f;
        foreach (var s in me.StatusList)
            if (s.StatusId is 414 or 1078) return 1f + s.Param / 100f;
        return 1f;
    }

    public static int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId, false, true, true);
    }

    public static int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    // ================================================================ candidates

    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    private static readonly InventoryType[] Armoury =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    };

    /// <summary>All gear in bags (and armoury), with the guardrail verdict for each.</summary>
    public static List<GearCandidate> Scan(Configuration c)
    {
        var list = new List<GearCandidate>();
        var im = InventoryManager.Instance();
        if (im == null) return list;
        var gearsetIds = GearsetItemIds();
        var items = Plugin.DataManager.GetExcelSheet<Item>();

        foreach (var type in c.IncludeArmoury ? Bags.Concat(Armoury) : Bags)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                var row = items.GetRowOrDefault(slot->ItemId);
                if (row == null || row.Value.EquipSlotCategory.RowId == 0) continue; // gear only

                var r = row.Value;
                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var materia = 0;
                for (var m = 0; m < 5; m++) if (slot->Materia[m] != 0) materia++;
                var inSet = gearsetIds.Contains(slot->ItemId);
                var ilvl = (int)r.LevelItem.RowId;

                string? blocked = null;
                if (c.NeverTurnIn.Contains(slot->ItemId)) blocked = "protected";
                else if (c.SkipGearsetItems && inSet) blocked = "in a gearset";
                else if (r.Rarity is not (2 or 3)) blocked = "not green/blue";
                else if (r.Rarity == 2 && !c.AllowGreen) blocked = "green not allowed";
                else if (r.Rarity == 3 && !c.AllowBlue) blocked = "blue not allowed";
                else if (hq && !c.AllowHq) blocked = "HQ not allowed";
                else if (!hq && !c.AllowNq) blocked = "NQ not allowed";
                else if (ilvl > c.MaxItemLevel) blocked = $"ilvl {ilvl} > {c.MaxItemLevel}";
                else if (materia > 0 && !c.RetrieveMateria && c.SkipItemsWithMateria) blocked = "has materia";

                list.Add(new GearCandidate(type, i, slot->ItemId, r.Name.ExtractText(), ilvl, r.Rarity, hq, materia, inSet, blocked));
            }
        }
        return list;
    }

    /// <summary>Base item ids used by any gearset (HQ ids in gearsets carry +1,000,000).</summary>
    public static HashSet<uint> GearsetItemIds()
    {
        var ids = new HashSet<uint>();
        var gs = RaptureGearsetModule.Instance();
        if (gs == null) return ids;
        for (var i = 0; i < 100; i++)
        {
            var entry = gs->GetGearset(i);
            if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            foreach (var item in entry->Items)
                if (item.ItemId != 0) ids.Add(item.ItemId % 1_000_000);
        }
        return ids;
    }

    /// <summary>Copies of a piece of gear in bags + armoury, NQ and HQ alike (filters ignored).</summary>
    public static int HeldCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var n = 0;
        foreach (var type in Bags.Concat(Armoury))
        {
            var c = im->GetInventoryContainer(type);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var it = c->GetInventorySlot(i);
                if (it != null && it->ItemId == itemId) n++;
            }
        }
        return n;
    }

    public static InventoryItem* Slot(InventoryType type, int slot)
    {
        var im = InventoryManager.Instance();
        var c = im == null ? null : im->GetInventoryContainer(type);
        return c == null ? null : c->GetInventorySlot(slot);
    }

    public static int MateriaCount(InventoryType type, int slot)
    {
        var s = Slot(type, slot);
        if (s == null) return 0;
        var n = 0;
        for (var m = 0; m < 5; m++) if (s->Materia[m] != 0) n++;
        return n;
    }
}
