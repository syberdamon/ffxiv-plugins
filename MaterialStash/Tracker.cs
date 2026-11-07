using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// Inventory helpers, and a record of what each retainer holds. The game only shares a retainer's
/// inventory while it's open, so: one scan (or Allagan Tools) to start, then every time a retainer
/// is open (by you, AutoRetainer or a stash run) its record is refreshed.
/// </summary>
public sealed unsafe class Tracker : IDisposable
{
    public static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    public static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };
    public const int RetainerSlots = 175;

    private readonly Configuration config;
    private DateTime nextPoll = DateTime.MinValue;
    private string lastSaved = "";

    public Tracker(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    // ------------------------------------------------------------------ character / retainers

    public static ulong ContentId()
    {
        var ps = PlayerState.Instance();
        return ps == null ? 0 : ps->ContentId;
    }

    /// <summary>This character's data (null before login).</summary>
    public CharacterData? Me()
    {
        var cid = ContentId();
        if (cid == 0) return null;
        if (!config.Characters.TryGetValue(cid, out var d)) config.Characters[cid] = d = new CharacterData();
        return d;
    }

    public static bool RetainerInventoryOpen => GameUi.IsOpen("InventoryRetainer") || GameUi.IsOpen("InventoryRetainerLarge");

    /// <summary>The retainer whose inventory is loaded right now.</summary>
    public static (ulong Id, string Name)? ActiveRetainer()
    {
        var rm = RetainerManager.Instance();
        var r = rm == null ? null : rm->GetActiveRetainer();
        return r == null || r->RetainerId == 0 ? null : (r->RetainerId, MemoryHelper.ReadStringNullTerminated((nint)r + 8));
    }

    public string NameOf(ulong id) => Me()?.Retainers.TryGetValue(id, out var s) == true && s.Name.Length > 0 ? s.Name : $"#{id:X}";

    // ------------------------------------------------------------------ inventory reads

    public readonly record struct Stack(InventoryType Type, int Slot, uint ItemId, int Qty);

    public static List<Stack> Stacks(IEnumerable<InventoryType> containers, Func<uint, bool>? which = null)
    {
        var result = new List<Stack>();
        var im = InventoryManager.Instance();
        if (im == null) return result;
        foreach (var t in containers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0) continue;
                var id = s->ItemId + (s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? 1_000_000u : 0u);
                if (which == null || which(id)) result.Add(new Stack(t, i, id, (int)s->Quantity));
            }
        }
        return result;
    }

    public static bool Loaded(IEnumerable<InventoryType> containers)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (var t in containers)
        {
            var c = im->GetInventoryContainer(t);
            if (c != null && c->IsLoaded) return true;
        }
        return false;
    }

    public static int UsedSlots(IEnumerable<InventoryType> containers)
    {
        var im = InventoryManager.Instance();
        var n = 0;
        if (im == null) return 0;
        foreach (var t in containers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
                if (c->GetInventorySlot(i) is var s && s != null && s->ItemId != 0) n++;
        }
        return n;
    }

    /// <summary>Crystal id -> (slot, quantity) in a crystal container.</summary>
    public static Dictionary<uint, (int Slot, int Qty)> Crystals(InventoryType container)
    {
        var result = new Dictionary<uint, (int, int)>();
        var im = InventoryManager.Instance();
        var c = im == null ? null : im->GetInventoryContainer(container);
        if (c == null || !c->IsLoaded) return result;
        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s != null && s->ItemId != 0 && Items.IsCrystal(s->ItemId)) result[s->ItemId] = (i, (int)s->Quantity);
        }
        return result;
    }

    /// <summary>The slot index a crystal uses in a crystal container (they're in a fixed order).</summary>
    public static int CrystalSlot(uint crystalId) => (int)(crystalId - Items.FirstCrystal);

    public static int StackMax(uint itemId) =>
        (int)(Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId % 1_000_000)?.StackSize ?? 999);

    // ------------------------------------------------------------------ recording

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        try { Poll(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash poll failed"); }
    }

    private void Poll()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return;
        var me = Me();
        if (me == null) return;
        var changed = false;

        // Roster, in the bell's order. Available after the bell has been used once this session.
        var order = new List<ulong>();
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            order.Add(r->RetainerId);
            var name = MemoryHelper.ReadStringNullTerminated((nint)r + 8);
            if (!me.Retainers.TryGetValue(r->RetainerId, out var snap)) { me.Retainers[r->RetainerId] = snap = new RetainerSnap(); changed = true; }
            if (snap.Name != name) { snap.Name = name; changed = true; }
        }
        if (order.Count > 0 && !order.SequenceEqual(me.Order))
        {
            me.Order = order;
            foreach (var gone in me.Retainers.Keys.Where(k => !order.Contains(k)).ToList()) me.Retainers.Remove(gone);
            changed = true;
        }

        if (RecordOpen()) changed = true;
        if (changed) Save();
    }

    /// <summary>Refresh the open retainer's record from the game. True if anything changed.</summary>
    public bool RecordOpen()
    {
        var me = Me();
        if (me == null || ActiveRetainer() is not { } a || !Loaded(RetainerPages)) return false;
        if (!me.Retainers.TryGetValue(a.Id, out var snap)) me.Retainers[a.Id] = snap = new RetainerSnap { Name = a.Name };
        var items = new Dictionary<uint, int>();
        var stacks = new Dictionary<uint, int>();
        var used = 0;
        foreach (var s in Stacks(RetainerPages))
        {
            used++;
            items[s.ItemId] = items.GetValueOrDefault(s.ItemId) + s.Qty;
            stacks[s.ItemId] = stacks.GetValueOrDefault(s.ItemId) + 1;
        }
        var crystals = Loaded(new[] { InventoryType.RetainerCrystals })
            ? Crystals(InventoryType.RetainerCrystals).ToDictionary(k => k.Key, v => v.Value.Qty)
            : snap.Crystals;
        var same = snap.Source == "opened" && snap.UsedSlots == used && Same(snap.Items, items) && Same(snap.Crystals, crystals);
        snap.Items = items; snap.Stacks = stacks; snap.UsedSlots = used; snap.Crystals = crystals;
        snap.Seen = DateTime.Now; snap.Source = "opened";
        return !same;
    }

    private static bool Same(Dictionary<uint, int> a, Dictionary<uint, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Fill in retainers we have no record of from Allagan Tools. Returns how many were seeded.</summary>
    public int SeedFromAllagan(AllaganIpc allagan, bool overwrite = false)
    {
        var me = Me();
        if (me == null || !allagan.IsReady) return 0;
        var n = 0;
        foreach (var id in me.Order)
        {
            var snap = me.Retainers[id];
            if (!overwrite && snap.Seen != null) continue;
            if (allagan.Items(id) is not { } list) continue;
            var items = new Dictionary<uint, int>();
            var stacks = new Dictionary<uint, int>();
            var crystals = new Dictionary<uint, int>();
            foreach (var (item, qty) in list)
            {
                if (Items.IsCrystal(item)) { crystals[item % 1_000_000] = crystals.GetValueOrDefault(item % 1_000_000) + qty; continue; }
                items[item] = items.GetValueOrDefault(item) + qty;
                stacks[item] = stacks.GetValueOrDefault(item) + 1;
            }
            snap.Items = items; snap.Stacks = stacks; snap.Crystals = crystals;
            snap.UsedSlots = -1; // Allagan's list may include equipped gear and market listings: slot count unknown
            snap.Seen = DateTime.Now; snap.Source = "allagan";
            n++;
        }
        if (n > 0) Save(force: true);
        return n;
    }

    public void Save(bool force = false)
    {
        var me = Me();
        var key = me == null ? "" : string.Join("|", me.Retainers.Select(r => $"{r.Key}:{r.Value.UsedSlots}:{r.Value.Items.Count}:{r.Value.Items.Values.Sum()}:{r.Value.Crystals.Values.Sum()}:{r.Value.Name}")) + string.Join(",", me.Order);
        if (!force && key == lastSaved) return;
        lastSaved = key;
        config.Save();
    }
}
