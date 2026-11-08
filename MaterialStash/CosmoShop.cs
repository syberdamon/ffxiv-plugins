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
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MaterialStash.Core;
using Syberdamon.Shared;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace MaterialStash;

/// <summary>
/// Cosmic Exploration's cosmocredit exchange (Mesouaidonque's materials list). Item list, costs and
/// vendor spots come from Ices Cosmic Exploration (ICE); the window layout (row count at value 4,
/// item ids from 1066, costs from 456, row index from 1310; buy = 0, row, amount, then a yes/no)
/// from ECommons' ShopExchangeCurrency helper. See THIRD_PARTY_NOTICES. Our code is our own.
/// </summary>
public static class CosmoData
{
    public const uint Cosmocredit = 45690;
    public const int Cap = 30000;
    public const string Vendor = "Mesouaidonque";

    /// <summary>[Assumption] the materials list as of Nov 2026 (ICE's table); the live window decides what's really there.</summary>
    public static readonly Dictionary<uint, int> Costs = new()
    {
        [12669] = 40,   // Hi-Cordial
        [45993] = 30, [45994] = 30, [45990] = 30, [45991] = 30, [45992] = 30, // Queso Fresco, Woolback Loin, Cassava, Splendid Mate Leaves, Aji Amarillo
        [46252] = 1000, // Mason's Abrasive
        [44035] = 200,  // Sungilt Aethersand
        [44036] = 400, [44037] = 400, [44038] = 400, // Mythloam / Mythroot / Mythbrine Aethersand
        [46246] = 600,  // Levinchrome Aethersand
        [44848] = 250,  // Condensed Solution
        [28724] = 30,   // Crafter's Delineation
        [41775] = 900, [41776] = 900, [41777] = 900, [41778] = 900, [41779] = 900, [41780] = 900, // materia XII
        [41762] = 450, [41763] = 450, [41764] = 450, [41765] = 450, [41766] = 450, [41767] = 450, // materia XI
    };

    /// <summary>Cosmic Exploration zones (territory id) -> where Mesouaidonque stands.</summary>
    public static readonly Dictionary<uint, Vector3> VendorSpots = new()
    {
        [1237] = new(18.23f, 1.69f, 19.42f),        // Sinus Ardorum
        [1291] = new(358.33f, 52.75f, -400.44f),    // Phaenna
        [1310] = new(-202.20f, 0.65f, 152.54f),     // Oizys
        [1319] = new(317.73f, 205.75f, 376.69f),    // Auxesia
    };

    public static bool InZone => VendorSpots.ContainsKey(Plugin.ClientState.TerritoryType);
    public static int Credits => ScripData.Scrips(Cosmocredit);
    public static bool Sells(uint itemId) => Costs.ContainsKey(itemId % 1_000_000);
}

/// <summary>Walk to Mesouaidonque (in a Cosmic Exploration zone) and buy items with cosmocredits, keeping a reserve.</summary>
public sealed unsafe class CosmoShop : IDisposable
{
    private enum Phase { Idle, Walk, Talk, Buy }

    private readonly Configuration config;
    private readonly VNavIpc vnav = new();
    private Phase phase = Phase.Idle;
    private DateTime next, phaseStart, lastProgress, talkedAt;
    private float best;
    private int tries;
    private bool menuPicked;
    private readonly Dictionary<uint, int> left = new();
    private (uint Item, int Before, int Qty, int Credits, DateTime At)? pending;

    public string Status { get; private set; } = "";
    public bool Busy => phase != Phase.Idle;
    public bool Failed { get; private set; }
    public Dictionary<uint, int> Bought { get; } = new();

    public CosmoShop(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) vnav.Stop();
    }

    /// <summary>Buy up to these amounts (item -> count). Returns "" when started, else why not.</summary>
    public string Start(IReadOnlyDictionary<uint, int> want)
    {
        if (Busy) return "Already buying.";
        if (!CosmoData.InZone) return "Not in a Cosmic Exploration zone.";
        if (!vnav.IsLoaded) return "vnavmesh isn't loaded.";
        left.Clear(); Bought.Clear(); pending = null; Failed = false;
        foreach (var (id, n) in want) if (n > 0) left[id % 1_000_000] = n;
        lock (RunLog) RunLog.Clear();
        Log($"start in territory {Plugin.ClientState.TerritoryType}: {string.Join(", ", left.Select(kv => $"{kv.Value} {Name(kv.Key)}"))}; credits {CosmoData.Credits}, keeping {config.ScripCosmoReserve}");
        if (left.Count == 0) return "Nothing to buy.";
        tries = 0; menuPicked = false; talkedAt = DateTime.MinValue;
        best = float.MaxValue; lastProgress = DateTime.UtcNow;
        Set(Phase.Walk, $"Walking to {CosmoData.Vendor}...");
        return "";
    }

    public void Stop(string why = "Stopped.")
    {
        if (!Busy) return;
        vnav.Stop();
        Close();
        Set(Phase.Idle, why);
    }

    private void Set(Phase p, string status)
    {
        phase = p;
        Status = status;
        phaseStart = DateTime.UtcNow;
    }

    private void Fail(string why)
    {
        vnav.Stop();
        Failed = true;
        Log("STOP: " + why + " | visible windows: " + string.Join(", ", GameUi.VisibleAddons().OrderBy(x => x)));
        if (phase == Phase.Talk) foreach (var n in new[] { "SelectIconString", "SelectString", "Talk" }) if (GameUi.IsOpen(n)) Dump(n);
        SaveLog(true);
        Plugin.ChatGui.PrintError($"[Stash] Cosmocredits: {why}");
        Set(Phase.Idle, why);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.5);
        try
        {
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > (phase == Phase.Walk ? 120 : 30)) { Fail($"Timed out: {Status}"); return; }
            switch (phase)
            {
                case Phase.Walk: TickWalk(); break;
                case Phase.Talk: TickTalk(); break;
                case Phase.Buy: TickBuy(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Cosmocredit shopping failed");
            Fail("Error (see /xllog).");
        }
    }

    private static IGameObject? Npc() =>
        Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable &&
                                               o.Name.TextValue.Equals(CosmoData.Vendor, StringComparison.OrdinalIgnoreCase));

    private void TickWalk()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;
        if (!vnav.IsReady) { Status = "Waiting for vnavmesh to finish this zone's mesh (first visit can take a while)..."; phaseStart = DateTime.UtcNow; return; }
        if (!CosmoData.VendorSpots.TryGetValue(Plugin.ClientState.TerritoryType, out var spot)) { Fail("Left the Cosmic Exploration zone."); return; }
        var npc = Npc();
        var goal = npc?.Position ?? spot;
        if (npc != null && Vector3.Distance(me.Position, npc.Position) <= 4.5f) { vnav.Stop(); Set(Phase.Talk, $"Talking to {CosmoData.Vendor}..."); return; }
        var dest = vnav.NearestPoint(goal, 6, 6) ?? goal;
        var dist = Vector3.Distance(me.Position, dest);
        if (dist < best - 0.5f) { best = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 10) { Fail($"vnavmesh isn't getting closer to {CosmoData.Vendor}."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(dest, 2f);
    }

    private DateTime menuSeen = DateTime.MinValue;

    private void TickTalk()
    {
        if (Ready("ShopExchangeCurrency")) { Log("exchange window open"); Set(Phase.Buy, "Buying with cosmocredits..."); next = DateTime.UtcNow.AddSeconds(0.8); return; }
        if (Ready("ShopExchangeItem")) { Fail("A different exchange window opened (ShopExchangeItem): the menu row guess was wrong."); Dump("ShopExchangeItem"); return; }
        // The vendor's menu: an icon menu (as ICE handles it) or a plain one.
        foreach (var (name, icon) in new[] { ("SelectIconString", true), ("SelectString", false) })
        {
            var menu = GameUi.Get(name);
            if (menu == null) continue;
            if (menuPicked) return;
            if (menuSeen == DateTime.MinValue) menuSeen = DateTime.UtcNow;
            var rows = GameUi.MenuEntries(menu, icon);
            // The popup's own row list can still be empty while the window is up (seen in Saucer Run):
            // fall back to the window's strings, then after a moment to row 1 (ICE's choice).
            if (rows.Count == 0 || rows.All(r => r.Length == 0)) rows = GameUi.Texts(menu).Distinct().ToList();
            var row = rows.FindIndex(r => r.Contains("Material", StringComparison.OrdinalIgnoreCase));
            if (row < 0 && (DateTime.UtcNow - menuSeen).TotalSeconds < 1.5) return;
            if (row < 0) row = 1; // [Assumption] second row is the materials / materia / items exchange (ICE picks row 1)
            Log($"{name}: [{string.Join(" | ", rows)}] -> row {row}");
            menu->FireCallbackInt(row);
            menuPicked = true;
            talkedAt = DateTime.UtcNow;
            return;
        }
        var talk = GameUi.Get("Talk");
        if (talk != null) { GameUi.ClickTalk(talk); return; }
        if (menuPicked && (DateTime.UtcNow - talkedAt).TotalSeconds < 6) return;
        if (++tries > 3) { Dump("SelectIconString"); Fail($"{CosmoData.Vendor} didn't open the exchange."); return; }
        var npc = Npc();
        if (npc == null) { Fail($"Can't see {CosmoData.Vendor}."); return; }
        menuPicked = false;
        menuSeen = DateTime.MinValue;
        Log($"talking to {CosmoData.Vendor} (try {tries})");
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
        next = DateTime.UtcNow.AddSeconds(1);
    }

    private void TickBuy()
    {
        var yn = GameUi.Get("SelectYesno");
        if (yn != null) { yn->FireCallbackInt(0); next = DateTime.UtcNow.AddSeconds(0.8); return; }
        var shop = GameUi.Get("ShopExchangeCurrency");
        if (shop == null || !IsReady(shop)) { Fail("The exchange window closed."); return; }

        if (pending is { } p)
        {
            var have = Count(p.Item);
            if (have >= p.Before + p.Qty)
            {
                Bought[p.Item] = Bought.GetValueOrDefault(p.Item) + p.Qty;
                left[p.Item] -= p.Qty;
                pending = null;
                phaseStart = DateTime.UtcNow;
                Status = $"Bought {string.Join(", ", Bought.Select(kv => $"{kv.Value} {Name(kv.Key)}"))}...";
                return;
            }
            if ((DateTime.UtcNow - p.At).TotalSeconds > 8) { GameUi.Dump("ShopExchangeCurrency", "cosmocredit purchase not seen"); Fail($"Asked for {p.Qty} {Name(p.Item)} but they didn't arrive."); }
            return;
        }

        var credits = (int)GameUi.UInt(shop, 86);
        if (credits <= 0) credits = CosmoData.Credits;
        var spendable = credits - config.ScripCosmoReserve;
        var rows = Rows(shop);
        foreach (var (item, n) in left.Where(kv => kv.Value > 0).ToList())
        {
            var row = rows.FirstOrDefault(r => r.Item == item);
            if (row.Item == 0) { left[item] = 0; Plugin.Log.Info($"[Stash] {Name(item)} isn't in this exchange."); continue; }
            var stack = Math.Max(1, (int)(Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(item)?.StackSize ?? 1));
            var room = FreeSlots() * stack + (stack - Count(item) % stack) % stack;
            var qty = Math.Min(Math.Min(n, row.Cost == 0 ? 0 : spendable / (int)row.Cost), Math.Min(99, room));
            if (qty <= 0) { left[item] = 0; continue; }
            pending = (item, Count(item), qty, credits, DateTime.UtcNow);
            Log($"buying {qty} {Name(item)} (row {row.Index}, {row.Cost} each, credits {credits})");
            Fire(shop, 0, row.Index, qty);
            next = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }
        Close();
        SaveLog(false);
        Set(Phase.Idle, Bought.Count == 0 ? $"Nothing bought (cosmocredits {CosmoData.Credits:N0}, keeping {config.ScripCosmoReserve:N0})."
                                          : $"Bought {string.Join(", ", Bought.Select(kv => $"{kv.Value} {Name(kv.Key)}"))} with cosmocredits.");
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private static List<(uint Item, uint Cost, uint Index)> Rows(AtkUnitBase* a)
    {
        var list = new List<(uint, uint, uint)>();
        var n = (int)GameUi.UInt(a, 4);
        for (var i = 0; i < n && i < 120; i++)
        {
            var id = GameUi.UInt(a, 1066 + i);
            if (id != 0) list.Add((id, GameUi.UInt(a, 456 + i), GameUi.UInt(a, 1310 + i)));
        }
        return list;
    }

    private static void Fire(AtkUnitBase* a, int first, uint index, int amount)
    {
        var v = stackalloc AtkValue[3];
        v[0] = default; v[0].Type = ValueType.Int; v[0].Int = first;
        v[1] = default; v[1].Type = ValueType.UInt; v[1].UInt = index;
        v[2] = default; v[2].Type = ValueType.Int; v[2].Int = amount;
        a->FireCallback(3, v, true);
    }

    private static bool IsReady(AtkUnitBase* a) => a->IsVisible && a->UldManager.LoadedState == AtkLoadState.Loaded;
    private static bool Ready(string name) { var a = GameUi.Get(name); return a != null && IsReady(a); }

    private static void Close()
    {
        foreach (var n in new[] { "ShopExchangeCurrency", "SelectIconString" })
        {
            var a = GameUi.Get(n);
            if (a != null) a->Close(true);
        }
    }

    private static int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId) + im->GetInventoryItemCount(itemId, true);
    }

    private static int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    private static void Dump(string window) => GameUi.Dump(window, "cosmocredit exchange");

    private static readonly List<string> RunLog = new();
    private static void Log(string msg)
    {
        Plugin.Log.Info($"[Stash/Cosmo] {msg}");
        lock (RunLog) RunLog.Add($"{DateTime.Now:HH:mm:ss.f} {msg}");
    }

    /// <summary>The run's lines to pluginConfigs/MaterialStash/dumps/&lt;time&gt;_cosmo.txt.</summary>
    private static void SaveLog(bool failed)
    {
        try
        {
            string text;
            lock (RunLog) { text = string.Join(Environment.NewLine, RunLog); RunLog.Clear(); }
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dumps");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_cosmo{(failed ? "_FAILED" : "")}.txt"), text);
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't write the cosmocredit log"); }
    }

    private static string Name(uint id) => Items.Info(id)?.Name ?? Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText() ?? $"#{id}";
}
