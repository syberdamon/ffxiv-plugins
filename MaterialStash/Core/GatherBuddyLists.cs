using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;

namespace MaterialStash.Core;

/// <summary>
/// GatherBuddy Reborn's auto-gather lists, reached through reflection (it has no IPC for lists):
/// read which lists are on, switch lists on/off, and keep one list of our own ("Material Stash:
/// scrip goals") holding the collectable to gather and how many. Uses its own list manager's public
/// methods (Lists, AddList, ToggleList, AddItem, RemoveItem, ChangeQuantity), so its validation and
/// saving run as if you'd clicked in its window. Same technique GatherBuddy Reborn uses to read
/// Artisan's lists (see THIRD_PARTY_NOTICES).
/// [Assumption] internal names as of GatherBuddy Reborn's source in Nov 2026; if any is missing,
/// every call fails with a message and nothing is changed.
/// </summary>
public sealed class GatherBuddyLists
{
    public const string OurList = "Material Stash: scrip goals";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private object? manager;
    private Type? listType;
    private object? gatherables;
    public string Error { get; private set; } = "";

    /// <summary>Find GatherBuddy Reborn's list manager. False (Error says why) if it isn't loaded or has changed.</summary>
    public bool Connect()
    {
        try
        {
            var plugin = FindPlugin("GatherBuddyReborn");
            if (plugin == null) return Fail("GatherBuddy Reborn isn't loaded.");
            var t = plugin.GetType();
            manager = Get(plugin, "AutoGatherListsManager");
            if (manager == null) return Fail("Couldn't find GatherBuddy's list manager (it may have changed in an update).");
            listType = t.Assembly.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList");
            if (listType == null) return Fail("Couldn't find GatherBuddy's list type.");
            var gameData = t.GetProperty("GameData", All)?.GetValue(null);
            gatherables = gameData == null ? null : Get(gameData, "Gatherables");
            if (gatherables == null) return Fail("Couldn't read GatherBuddy's gatherables.");
            Error = "";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Material Stash: reaching GatherBuddy's lists failed");
            return Fail($"Couldn't reach GatherBuddy's lists: {ex.Message}");
        }
    }

    private bool Fail(string why) { Error = why; manager = null; return false; }

    public IEnumerable<object> Lists() => manager == null ? Enumerable.Empty<object>() : ((IEnumerable)Get(manager, "Lists")!).Cast<object>().ToList();

    public static string Name(object list) => Get(list, "Name") as string ?? "";
    public static bool Enabled(object list) => Get(list, "Enabled") is true;

    /// <summary>Names of your switched-on lists (not ours).</summary>
    public List<string> EnabledLists() => Lists().Where(l => Enabled(l) && Name(l) != OurList).Select(Name).ToList();

    /// <summary>Switch a list on or off (through GatherBuddy's own toggle). True if it ends up as asked.</summary>
    public bool SetEnabled(object list, bool on)
    {
        if (Enabled(list) != on) Call(manager!, "ToggleList", list);
        return Enabled(list) == on;
    }

    public object? Find(string name) => Lists().FirstOrDefault(l => Name(l) == name);

    /// <summary>GatherBuddy's gatherable for this item (Miner / Botanist), or null.</summary>
    public object? Gatherable(uint itemId)
    {
        if (gatherables == null) return null;
        var tryGet = gatherables.GetType().GetMethods().FirstOrDefault(m => m.Name == "TryGetValue" && !m.IsGenericMethod && m.GetParameters().Length == 2);
        if (tryGet == null) return null;
        var args = new object?[] { itemId, null };
        return tryGet.Invoke(gatherables, args) is true ? args[1] : null;
    }

    /// <summary>
    /// Make our list hold exactly this item with this quantity (GatherBuddy gathers until you hold
    /// that many), creating the list if needed. Doesn't switch it on.
    /// </summary>
    public object? SetOurList(uint itemId, int quantity) => SetOurList(new[] { (itemId, quantity) }, out _);

    /// <summary>
    /// Our list with exactly these items and quantities (several: GatherBuddy goes to whichever node is up).
    /// Items GatherBuddy doesn't know are left out and returned in <paramref name="unknown"/>.
    /// </summary>
    public object? SetOurList(IEnumerable<(uint ItemId, int Quantity)> wants, out List<uint> unknown)
    {
        unknown = new List<uint>();
        if (manager == null || listType == null) { Error = "Not connected to GatherBuddy."; return null; }
        var known = new List<(object G, int Quantity)>();
        foreach (var (id, q) in wants)
        {
            var gi = Gatherable(id);
            if (gi == null) unknown.Add(id); else known.Add((gi, q));
        }
        if (known.Count == 0) { Error = "GatherBuddy doesn't know any of these items as gatherable."; return null; }
        var list = Find(OurList);
        if (list == null)
        {
            list = Activator.CreateInstance(listType)!;
            listType.GetProperty("Name")!.SetValue(list, OurList);
            listType.GetProperty("Description")?.SetValue(list, "Made and switched on/off by Material Stash's scrip goals.");
            Call(manager, "AddList", list, null);
            list = Find(OurList);
            if (list == null) { Error = "GatherBuddy didn't keep the new list."; return null; }
        }
        bool Wanted(object x) => known.Any(k => ReferenceEquals(x, k.G) || x.Equals(k.G));
        // Remove anything else in it, newest first.
        var items = ((IEnumerable)Get(list, "Items")!).Cast<object>().ToList();
        for (var i = items.Count - 1; i >= 0; i--)
            if (!Wanted(items[i])) Call(manager, "RemoveItem", list, i);
        foreach (var (g, q) in known)
        {
            items = ((IEnumerable)Get(list, "Items")!).Cast<object>().ToList();
            if (!items.Any(x => ReferenceEquals(x, g) || x.Equals(g))) Call(manager, "AddItem", list, g);
            Call(manager, "ChangeQuantity", list, g, (uint)Math.Max(1, q));
        }
        return list;
    }

    /// <summary>Whether GatherBuddy left our item switched on in the list (it switches off items it judges you can't gather, e.g. perception too low).</summary>
    public bool ItemEnabled(object list, uint itemId)
    {
        var g = Gatherable(itemId);
        if (g == null || Get(list, "EnabledItems") is not IDictionary d) return false;
        return d.Contains(g) && d[g] is true;
    }

    // ------------------------------------------------------------------ reflection helpers

    private static object? Get(object o, string name)
    {
        var t = o.GetType();
        while (t != null)
        {
            var p = t.GetProperty(name, All);
            if (p != null) return p.GetValue(p.GetMethod?.IsStatic == true ? null : o);
            var f = t.GetField(name, All);
            if (f != null) return f.GetValue(f.IsStatic ? null : o);
            t = t.BaseType;
        }
        return null;
    }

    private static object? Call(object o, string method, params object?[] args)
    {
        var m = o.GetType().GetMethods(All).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length
                    && x.GetParameters().Zip(args).All(p => p.Second == null ? !p.First.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.First.ParameterType) != null
                                                                             : p.First.ParameterType.IsInstanceOfType(p.Second)));
        if (m == null) throw new MissingMethodException($"GatherBuddy has no {method} taking {args.Length} argument(s).");
        return m.Invoke(o, args);
    }

    /// <summary>A loaded plugin's instance, from Dalamud's plugin manager (as GatherBuddy Reborn / ECommons do it).</summary>
    private static IDalamudPlugin? FindPlugin(string internalName)
    {
        var dalamud = Plugin.PluginInterface.GetType().Assembly;
        var service = dalamud.GetType("Dalamud.Service`1");
        var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
        if (service == null || pmType == null) return null;
        var pm = service.MakeGenericType(pmType).GetMethod("Get")?.Invoke(null, null);
        if (pm == null || pm.GetType().GetProperty("InstalledPlugins")?.GetValue(pm) is not IEnumerable installed) return null;
        foreach (var p in installed)
        {
            if (!string.Equals(Get(p, "InternalName") as string, internalName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Get(p, "IsLoaded") is false) continue;
            foreach (var f in new[] { "instance", "Instance", "pluginInstance", "Plugin" })
                if (Get(p, f) is IDalamudPlugin inst) return inst;
        }
        return null;
    }
}
