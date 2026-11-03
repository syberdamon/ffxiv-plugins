using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace PoeticsSpender;

/// <summary>A topsoil you can get for Poetics, and the middle item it's traded for.</summary>
/// <param name="TokenName">Bought from Hismena for <see cref="Soils.PoeticsPrice"/> Poetics.</param>
/// <param name="SoilName">Traded 1:1 for the token at Bertana ("Uncanny Knickknacks").</param>
public sealed record SoilOption(string SoilName, string TokenName);

/// <summary>
/// Sources (checked 2026-11-02, FFXIV community wiki / Gamer Escape):
///   Hismena, Idyllshire (5.7, 5.2): Unidentifiable Ore / Shell, 150 Poetics each
///     (menu "Allagan Tomestones of Poetics (Special Arms)", needs "A Great New Nation").
///   Bertana, Idyllshire (5.9, 5.2), "Uncanny Knickknacks": 1 Ore -> Grade 3 Thanalan Topsoil,
///     1 Shell -> Grade 3 Shroud Topsoil.
/// No Poetics route to Grade 3 La Noscea Topsoil was found (Bone/Seeds don't trade for topsoil).
/// </summary>
public static unsafe class Soils
{
    public const uint PoeticsItemId = 28;
    public const int PoeticsPrice = 150;
    public const int PoeticsCap = 2000;

    public const uint IdyllshireTerritory = 478;
    public const uint IdyllshireAetheryte = 75;

    public static readonly IReadOnlyList<SoilOption> Options = new[]
    {
        new SoilOption("Grade 3 Thanalan Topsoil", "Unidentifiable Ore"),
        new SoilOption("Grade 3 Shroud Topsoil", "Unidentifiable Shell"),
    };

    private static Dictionary<string, uint>? itemIds;

    /// <summary>Item ID by English name (names above are English; the lookup works on any client).</summary>
    public static uint ItemId(string englishName)
    {
        itemIds ??= Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English)
            .Where(i => !i.Name.IsEmpty)
            .GroupBy(i => i.Name.ExtractText())
            .ToDictionary(g => g.Key, g => g.First().RowId);
        return itemIds.GetValueOrDefault(englishName);
    }

    /// <summary>Item name in the client's language (what shop menus and dialogs show).</summary>
    public static string LocalName(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";

    public static int Poetics()
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var direct = im->GetInventoryItemCount(PoeticsItemId);
        var currency = 0;
        var c = im->GetInventoryContainer(InventoryType.Currency);
        if (c != null)
            for (var i = 0; i < c->Size; i++)
                if (c->Items[i].ItemId == PoeticsItemId) currency += (int)c->Items[i].Quantity;
        return System.Math.Max(direct, currency);
    }

    public static int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId);
    }

    public static int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    public static int Affordable() => Poetics() / PoeticsPrice;
}
