using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PoeticsSpender;

/// <summary>
/// Small helpers for reading and clicking game windows ("addons").
/// [Assumption] Callback layouts below follow what community automation uses
/// (SelectString/SelectIconString: one int = row; shops: 0, row, quantity; SelectYesno: 0 = Yes,
/// 1 = No). They're checked in game before anything spends Poetics: see Runner.
/// </summary>
public static unsafe class GameUi
{
    public static AtkUnitBase* Get(string name)
    {
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null) return null;
        var addon = mgr->GetAddonByName(name);
        return addon != null && addon->IsVisible ? addon : null;
    }

    public static bool IsOpen(string name) => Get(name) != null;

    public static void Close(string name)
    {
        var a = Get(name);
        if (a != null) a->Close(true);
    }

    /// <summary>Rows of a SelectString / SelectIconString menu.</summary>
    public static List<string> MenuEntries(AtkUnitBase* addon, bool icon)
    {
        var result = new List<string>();
        PopupMenu* menu = icon
            ? &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu
            : &((AddonSelectString*)addon)->PopupMenu.PopupMenu;
        var names = (byte**)menu->EntryNames;
        for (var i = 0; i < menu->EntryCount; i++)
            result.Add(names[i] == null ? "" : MemoryHelper.ReadSeStringNullTerminated((nint)names[i]).TextValue);
        return result;
    }

    public static void ClickRow(AtkUnitBase* addon, int row) => addon->FireCallbackInt(row);

    /// <summary>Fire a callback made of ints (e.g. shop purchase: 0, row, quantity).</summary>
    public static void FireInts(AtkUnitBase* addon, params int[] ints)
    {
        var values = stackalloc AtkValue[ints.Length];
        for (var i = 0; i < ints.Length; i++)
        {
            values[i] = default;
            values[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            values[i].Int = ints[i];
        }
        addon->FireCallback((uint)ints.Length, values);
    }

    public static string YesNoText()
    {
        var a = (AddonSelectYesno*)Get("SelectYesno");
        if (a == null || a->PromptText == null) return "";
        return a->PromptText->NodeText.ToString();
    }

    /// <summary>Every string value the addon was given, with its position (shops list item names here).</summary>
    public static List<(int Index, string Text)> Strings(AtkUnitBase* addon)
    {
        var list = new List<(int, string)>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var v = &addon->AtkValues[i];
            if (!v->Type.ToString().Contains("String")) continue;
            var ptr = *(nint*)((byte*)v + 8); // AtkValue: 4-byte type, padding, then the value union at +8
            if (ptr == 0) continue;
            list.Add((i, MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue));
        }
        return list;
    }

    /// <summary>
    /// Shop row of an item, found by name. Shops pass item names as one run of consecutive
    /// string values, in display order; the row is the name's position in that run.
    /// [Assumption] about the layout, so the result is shown for confirmation the first time.
    /// </summary>
    public static int FindShopRow(AtkUnitBase* addon, string itemName, out string evidence)
    {
        var strings = Strings(addon);
        var hit = strings.FindIndex(s => s.Text.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (hit < 0) { evidence = $"'{itemName}' not found in the shop"; return -1; }

        var start = hit;
        while (start > 0 && strings[start - 1].Index == strings[start].Index - 1) start--;
        var row = hit - start;

        var sb = new StringBuilder();
        for (var i = start; i <= Math.Min(hit + 1, strings.Count - 1); i++)
            sb.Append($"{i - start}: {strings[i].Text}{(i == hit ? "  <--" : "")}\n");
        evidence = sb.ToString().TrimEnd();
        return row;
    }

    /// <summary>Log an addon's values, so a wrong layout guess can be fixed from /xllog.</summary>
    public static void Dump(string name)
    {
        var a = Get(name);
        if (a == null) { Plugin.Log.Info($"[dump] {name} not open"); return; }
        Plugin.Log.Info($"[dump] {name}: {a->AtkValuesCount} values");
        for (var i = 0; i < a->AtkValuesCount; i++)
        {
            var v = &a->AtkValues[i];
            var text = v->Type.ToString().Contains("String") && *(nint*)((byte*)v + 8) != 0
                ? MemoryHelper.ReadSeStringNullTerminated(*(nint*)((byte*)v + 8)).TextValue
                : v->Int.ToString();
            Plugin.Log.Info($"[dump]   {i,3} {v->Type,-14} {text}");
        }
    }
}
