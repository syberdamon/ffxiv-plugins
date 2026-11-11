using System;
using System.Collections.Generic;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MaterialStash.Core;

/// <summary>
/// Reading and clicking game windows ("addons"). Callback layouts are the ones community
/// automation uses for these windows (sources in README / THIRD_PARTY_NOTICES); anything that
/// spends something is verified against inventory/seals afterwards.
/// </summary>
public static unsafe class GameUi
{
    public static AtkUnitBase* Get(string name)
    {
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null) return null;
        // Several windows can share a name (a hidden one first); return the one on screen.
        for (var i = 1; i < 10; i++)
        {
            var addon = mgr->GetAddonByName(name, i);
            if (addon == null) break;
            if (addon->IsVisible) return addon;
        }
        return null;
    }

    public static bool IsOpen(string name) => Get(name) != null;

    public static void Close(string name)
    {
        var a = Get(name);
        if (a != null) a->FireCallbackInt(-1);
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

    /// <summary>A callback value: int, bool, or the "zero" placeholder (type 0).</summary>
    public readonly record struct V(int Int, bool? Bool = null, bool Zero = false)
    {
        public static implicit operator V(int i) => new(i);
        public static implicit operator V(bool b) => new(0, b);
        public static readonly V ZeroValue = new(0, null, true);
    }

    public static void Fire(AtkUnitBase* addon, params V[] args)
    {
        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            values[i] = default;
            if (args[i].Zero) { values[i].Type = 0; values[i].Int = 0; }
            else if (args[i].Bool is { } b) { values[i].Type = AtkValueType.Bool; values[i].Int = b ? 1 : 0; }
            else { values[i].Type = AtkValueType.Int; values[i].Int = args[i].Int; }
        }
        addon->FireCallback((uint)args.Length, values);
    }

    /// <summary>
    /// Click a button/radio button node the way the game does: send the node's own registered
    /// event to the addon (same technique as ECommons' ClickAddonButton).
    /// </summary>
    public static bool ClickNode(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null) return false;
        var comp = (AtkComponentNode*)node;
        if (comp->Component == null) return false;
        var res = comp->Component->OwnerNode;
        if (res == null) return false;
        var evt = res->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null) return false;
        var res = button->AtkComponentBase.OwnerNode;
        if (res == null) return false;
        var evt = res->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    /// <summary>
    /// Advance an NPC's dialogue box ("Talk"): a mouse click on the window, which is what the game
    /// listens for (FireCallback doesn't advance it).
    /// </summary>
    /// <remarks>
    /// The event must name the window as its listener and the UI stage as its target: the game's
    /// handler reads them, and a blank event crashed the game (null read in Talk's ReceiveEvent).
    /// Same event ECommons' AddonMaster.Talk.Click sends: flags 132, mouse down then click.
    /// </remarks>
    public static void ClickTalk(AtkUnitBase* talk)
    {
        if (talk == null) return;
        var stage = AtkStage.Instance();
        if (stage == null) return;
        var evt = stackalloc AtkEvent[1];
        evt[0] = default;
        evt[0].Listener = (AtkEventListener*)talk;
        evt[0].Target = &stage->AtkEventTarget;
        evt[0].State.StateFlags = (AtkEventStateFlags)132;
        var data = stackalloc AtkEventData[1];
        data[0] = default;
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
    }

    /// <summary>A button in a window whose label is this text (e.g. "Confirm").</summary>
    public static AtkComponentButton* FindButton(AtkUnitBase* addon, string label)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null || comp->GetComponentType() != ComponentType.Button) continue;
            for (var k = 0; k < comp->UldManager.NodeListCount; k++)
            {
                var child = comp->UldManager.NodeList[k];
                if (child == null || child->Type != NodeType.Text) continue;
                var ptr = *(nint*)&((AtkTextNode*)child)->NodeText;
                if (ptr != 0 && MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue.Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
                    return (AtkComponentButton*)comp;
            }
        }
        return null;
    }

    public static string Str(AtkUnitBase* addon, int index)
    {
        if (index < 0 || index >= addon->AtkValuesCount) return "";
        var v = &addon->AtkValues[index];
        if (!v->Type.ToString().Contains("String")) return "";
        var ptr = *(nint*)((byte*)v + 8);
        return ptr == 0 ? "" : MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue;
    }

    public static int Int(AtkUnitBase* addon, int index) =>
        index >= 0 && index < addon->AtkValuesCount ? addon->AtkValues[index].Int : 0;

    public static uint UInt(AtkUnitBase* addon, int index) =>
        index >= 0 && index < addon->AtkValuesCount ? addon->AtkValues[index].UInt : 0;

    public static bool Bool(AtkUnitBase* addon, int index) =>
        index >= 0 && index < addon->AtkValuesCount && (byte)addon->AtkValues[index].Int != 0;

    public static string YesNoText()
    {
        var a = (AddonSelectYesno*)Get("SelectYesno");
        if (a == null || a->PromptText == null) return "";
        var ptr = *(nint*)&a->PromptText->NodeText;
        return ptr == 0 ? "" : MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue;
    }

    /// <summary>
    /// Write an addon's values and texts to /xllog and to a file in the plugin's folder
    /// (pluginConfigs/MaterialStash/dumps/), so a wrong layout guess can be fixed from it.
    /// </summary>
    public static void Dump(string name, string? extra = null)
    {
        var sb = new System.Text.StringBuilder();
        var a = Get(name);
        if (a == null) sb.AppendLine($"{name} not open");
        else
        {
            sb.AppendLine($"{name}: {a->AtkValuesCount} values");
            for (var i = 0; i < a->AtkValuesCount; i++)
            {
                var v = &a->AtkValues[i];
                var text = v->Type.ToString().Contains("String") ? Str(a, i) : $"{v->Int} (u {v->UInt})";
                sb.AppendLine($"  {i,3} {v->Type,-14} {text}");
            }
            sb.AppendLine("text nodes:");
            foreach (var t in Texts(a)) sb.AppendLine($"  {t}");
        }
        if (extra != null) sb.AppendLine(extra);
        Plugin.Log.Info($"[dump] {sb}");
        try
        {
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dumps");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}.txt"), sb.ToString());
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't write dump file"); }
    }

    /// <summary>Names of every visible game window.</summary>
    public static HashSet<string> VisibleAddons()
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

    /// <summary>Every text shown in a window: its string values and its text nodes.</summary>
    public static List<string> Texts(AtkUnitBase* addon)
    {
        var list = new List<string>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var s = Str(addon, i);
            if (s.Length > 0) list.Add(s);
        }
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var ptr = *(nint*)&((AtkTextNode*)node)->NodeText;
            if (ptr == 0) continue;
            var s = MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue;
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }
}
