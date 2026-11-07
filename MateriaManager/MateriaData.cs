using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MateriaManager;

/// <summary>One materia item: its type ("Savage Might") and grade (1 = I ... 12 = XII).</summary>
public sealed record MateriaInfo(uint ItemId, string Type, int Grade, string Name, uint Icon)
{
    /// <summary>The highest grade this type comes in (transmuting it can't upgrade).</summary>
    public int TopGrade { get; init; } = Grade;
}

/// <summary>Which items are materia (from the game's Materia sheet), and how many of each sit where.</summary>
public static unsafe class MateriaData
{
    public static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    public static readonly InventoryType[] Saddlebag = { InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2 };
    public static readonly InventoryType[] Retainer =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };

    private static Dictionary<uint, MateriaInfo>? all;

    public static Dictionary<uint, MateriaInfo> All()
    {
        if (all != null) return all;
        var result = new Dictionary<uint, MateriaInfo>();
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var m in Plugin.DataManager.GetExcelSheet<Materia>())
        {
            var top = 0;
            var g0 = 0;
            foreach (var it in m.Item) { g0++; if (it.RowId != 0) top = g0; }
            var grade = 0;
            foreach (var it in m.Item)
            {
                grade++;
                if (it.RowId == 0 || items.GetRowOrDefault(it.RowId) is not { } row) continue;
                var name = row.Name.ExtractText();
                // "Savage Might Materia XII" -> type "Savage Might"
                var type = name.Contains(" Materia") ? name[..name.IndexOf(" Materia")] : name;
                result[it.RowId] = new MateriaInfo(it.RowId, type, grade, name, row.Icon) { TopGrade = top };
            }
        }
        return all = result;
    }

    public static bool IsMateria(uint itemId) => All().ContainsKey(itemId);

    /// <summary>Count of each materia item in these containers (only loaded ones).</summary>
    public static Dictionary<uint, int> Count(IEnumerable<InventoryType> containers)
    {
        var counts = new Dictionary<uint, int>();
        var im = InventoryManager.Instance();
        if (im == null) return counts;
        foreach (var type in containers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !IsMateria(s->ItemId)) continue;
                counts[s->ItemId] = counts.GetValueOrDefault(s->ItemId) + (int)s->Quantity;
            }
        }
        return counts;
    }

    public static bool Loaded(IEnumerable<InventoryType> containers)
    {
        var im = InventoryManager.Instance();
        return im != null && containers.Any(t => im->GetInventoryContainer(t) is var c && c != null && c->IsLoaded);
    }

    public static string Roman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        9 => "IX", 10 => "X", 11 => "XI", 12 => "XII", _ => n.ToString(),
    };
}
