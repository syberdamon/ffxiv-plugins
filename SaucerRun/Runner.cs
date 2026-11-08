using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using SaucerRun.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace SaucerRun;

/// <summary>
/// The run: learn new Triple Triad cards in your bags, teleport to the Gold Saucer, Jumbo Cactpot
/// cashier (winnings) and broker (this week's tickets), Mini Cactpot broker (today's three tickets),
/// then sell spare cards to the Triple Triad Trader. Saucy plays the Cactpot games once each
/// conversation is open; this only walks (vnavmesh), talks to the NPC, and waits for Saucy to finish.
/// </summary>
public sealed unsafe class Runner : IDisposable
{
    public enum Step { Learn, Travel, Cashier, Jumbo, Mini, Cards }
    private enum Sub { Walk, Talk, Wait }

    public const ushort GoldSaucer = 144;
    private const uint MiniBroker = 1010445, JumboBroker = 1010446, Cashier = 1010451, CardTrader = 1016294;

    // Where they stand (x, z from Teamcraft's map positions converted to world units; the trader's from
    // AutoDuty). Heights are found on the navmesh. [Estimate] Only used until the NPC itself is in sight.
    private static readonly Dictionary<uint, Vector3> Spots = new()
    {
        [MiniBroker] = new(-49.7f, 1.6f, 21.7f),
        [CardTrader] = new(-56.1f, 1.6f, 16.6f),
        [JumboBroker] = new(122.6f, 13f, -9.7f),
        [Cashier] = new(124.4f, 13f, -20.7f),
    };

    private static readonly string[] Dialogs =
    {
        "Talk", "SelectString", "SelectIconString", "SelectYesno", "LotteryDaily", "LotteryWeeklyInput",
        "LotteryWeeklyRewardList", "TripleTriadCoinExchange", "ShopCardDialog",
    };

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly VNavIpc vnav = new();

    private readonly Queue<Step> plan = new();
    private Step step;
    private Sub sub;
    private bool running;
    private DateTime next = DateTime.MinValue, stepStart, talkedAt, idleSince;
    private float bestDistance;
    private DateTime lastProgress;
    private int tries, sold, learned;
    private uint lastCardCount = uint.MaxValue;
    private readonly Dictionary<uint, int> useTries = new();

    public string Status { get; private set; } = "";
    public bool Busy => running;
    public Step Current => step;

    public Runner(Configuration config, Tracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (running) vnav.Stop();
    }

    public static bool SaucyLoaded =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Saucy" && p.IsLoaded);

    public bool VNavLoaded => vnav.IsLoaded;

    // ================================================================ cards in your bags

    public static bool IsCard(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId % 1_000_000) is { } r && r.ItemUICategory.RowId == 86;

    /// <summary>1 learned, 2 not yet, 4 nothing to learn (the game's answer); 3 unknown.</summary>
    public static long Unlock(uint itemId)
    {
        var ui = UIState.Instance();
        var row = ExdModule.GetItemRowById(itemId % 1_000_000);
        return ui == null || row == null ? 3 : ui->IsItemActionUnlocked(row);
    }

    public static List<(InventoryType Type, int Slot, uint ItemId, int Qty)> CardsInBags()
    {
        var list = new List<(InventoryType, int, uint, int)>();
        var im = InventoryManager.Instance();
        if (im == null) return list;
        foreach (var t in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && IsCard(s->ItemId)) list.Add((t, i, s->ItemId, (int)s->Quantity));
            }
        }
        return list;
    }

    public int Unlearned() => CardsInBags().Count(c => Unlock(c.ItemId) == 2 && useTries.GetValueOrDefault(c.ItemId) < 3);

    // ================================================================ start / stop

    /// <summary>What a run would do now.</summary>
    public List<Step> Plan()
    {
        var p = new List<Step>();
        var saucy = SaucyLoaded;
        if (config.DoCards && config.LearnCards && CardsInBags().Any(c => Unlock(c.ItemId) == 2)) p.Add(Step.Learn);
        var s = tracker.Me();
        var jumbo = config.DoJumbo && saucy && s != null && (!s.JumboCashierVisited || s.JumboBought < 3);
        var mini = config.DoMini && saucy && s != null && s.MiniPlayed < 3;
        var cards = config.DoCards && CardsInBags().Count > 0;
        if (jumbo || mini || cards) p.Add(Step.Travel);
        if (jumbo && !s!.JumboCashierVisited) p.Add(Step.Cashier);
        if (jumbo && s!.JumboBought < 3) p.Add(Step.Jumbo);
        if (mini) p.Add(Step.Mini);
        if (cards) p.Add(Step.Cards);
        return p;
    }

    public string? CantStart()
    {
        if (running) return "Already running.";
        if (Plugin.ObjectTable.LocalPlayer == null) return "Log in first.";
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) return "Not in combat or a duty.";
        if (!vnav.IsLoaded) return "vnavmesh is needed for walking.";
        return null;
    }

    public void Start()
    {
        if (CantStart() is { } why) { Status = why; Plugin.ChatGui.Print($"[Saucer] {why}"); return; }
        plan.Clear();
        foreach (var s in Plan()) plan.Enqueue(s);
        if (plan.Count == 0) { Status = "Nothing to do: Cactpots done and no cards in your bags."; Plugin.ChatGui.Print($"[Saucer] {Status}"); return; }
        if ((config.DoMini || config.DoJumbo) && !SaucyLoaded)
            Plugin.ChatGui.Print("[Saucer] Saucy isn't loaded, so the Cactpots are skipped (it plays them).");
        sold = learned = 0;
        useTries.Clear();
        running = true;
        NextStep();
    }

    public void Stop(string? why = null)
    {
        if (!running) return;
        running = false;
        vnav.Stop();
        Status = why ?? "Stopped.";
        Plugin.ChatGui.Print($"[Saucer] {Status}");
    }

    private void Fail(string why)
    {
        running = false;
        vnav.Stop();
        Status = why;
        Plugin.ChatGui.PrintError($"[Saucer] {why}");
    }

    private void NextStep()
    {
        if (plan.Count == 0)
        {
            running = false;
            var s = tracker.Me();
            Status = $"Done. Mini Cactpot {s?.MiniPlayed ?? 0}/3, Jumbo Cactpot {s?.JumboBought ?? 0}/3" +
                     (learned > 0 ? $", {learned} card(s) learned" : "") + (sold > 0 ? $", {sold} card stack(s) sold" : "") + ".";
            Plugin.ChatGui.Print($"[Saucer] {Status}");
            return;
        }
        step = plan.Dequeue();
        sub = Sub.Walk;
        tries = 0;
        stepStart = lastProgress = DateTime.UtcNow;
        bestDistance = float.MaxValue;
        lastCardCount = uint.MaxValue;
        Status = step switch
        {
            Step.Learn => "Learning new cards...",
            Step.Travel => "Going to the Gold Saucer...",
            Step.Cashier => "Jumbo Cactpot: collecting winnings...",
            Step.Jumbo => "Jumbo Cactpot: buying tickets...",
            Step.Mini => "Mini Cactpot...",
            _ => "Selling cards...",
        };
    }

    // ================================================================ loop

    private void OnUpdate(IFramework framework)
    {
        if (!running || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.4);
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat]) { Fail("Combat: stopped."); return; }
            if ((DateTime.UtcNow - stepStart).TotalMinutes > 6) { Fail($"Took too long ({step})."); return; }
            switch (step)
            {
                case Step.Learn: TickLearn(); break;
                case Step.Travel: TickTravel(); break;
                case Step.Cashier: TickNpc(Cashier); break;
                case Step.Jumbo: TickNpc(JumboBroker); break;
                case Step.Mini: TickNpc(MiniBroker); break;
                case Step.Cards: TickCards(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Saucer Run failed");
            Fail("Error (see /xllog).");
        }
    }

    private void TickLearn()
    {
        if (GameUi.Get("SelectYesno") is var yn && yn != null) { yn->FireCallbackInt(0); next = DateTime.UtcNow.AddSeconds(1.5); return; }
        if (Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.OccupiedInEvent]) return;
        var card = CardsInBags().FirstOrDefault(c => Unlock(c.ItemId) == 2 && useTries.GetValueOrDefault(c.ItemId) < 3);
        if (card.ItemId == 0) { NextStep(); return; }
        useTries[card.ItemId] = useTries.GetValueOrDefault(card.ItemId) + 1;
        if (ActionManager.Instance()->UseAction(ActionType.Item, card.ItemId, 0xE000_0000, 65535)) learned++;
        next = DateTime.UtcNow.AddSeconds(3);
    }

    private void TickTravel()
    {
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.Casting]) return;
        if (Plugin.ClientState.TerritoryType == GoldSaucer)
        {
            if (Plugin.ObjectTable.LocalPlayer != null) { NextStep(); next = DateTime.UtcNow.AddSeconds(2); }
            return;
        }
        if (tries > 0 && (DateTime.UtcNow - talkedAt).TotalSeconds < 15) return;
        if (++tries > 3) { Fail("Couldn't teleport to the Gold Saucer."); return; }
        var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().FirstOrDefault(a => a.IsAetheryte && a.Territory.RowId == GoldSaucer);
        if (aetheryte.RowId == 0) { Fail("No Gold Saucer aetheryte found."); return; }
        Telepo.Instance()->Teleport(aetheryte.RowId, 0);
        talkedAt = DateTime.UtcNow;
    }

    private static IGameObject? Find(uint baseId) =>
        Plugin.ObjectTable.FirstOrDefault(o => o.BaseId == baseId && o.IsTargetable);

    /// <summary>Walk to the NPC. True once close enough to talk.</summary>
    private bool WalkTo(uint baseId)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;
        var npc = Find(baseId);
        var dest = npc?.Position ?? Spots[baseId];
        var dist = Vector3.Distance(me.Position, dest);
        if (npc != null && dist <= 3.5f) { vnav.Stop(); return true; }
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 15) { Fail("vnavmesh isn't getting closer; walk there and press again."); return false; }
        if (!vnav.IsReady || vnav.IsBusy) return false;
        var target = vnav.NearestPoint(dest, 3, 30) ?? dest;
        vnav.MoveCloseTo(target, 2f);
        return false;
    }

    private static bool Idle()
    {
        if (Plugin.Condition[ConditionFlag.OccupiedInEvent] || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        return Dialogs.All(d => !GameUi.IsOpen(d));
    }

    private void Interact(uint baseId)
    {
        var npc = Find(baseId);
        if (npc == null) return;
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
    }

    /// <summary>Cashier, Jumbo broker, Mini broker: walk, talk, let Saucy play, repeat until done.</summary>
    private void TickNpc(uint baseId)
    {
        var s = tracker.Me();
        if (s == null) return;
        bool Finished() => baseId switch
        {
            Cashier => s.JumboCashierVisited,
            JumboBroker => s.JumboBought >= 3,
            _ => s.MiniPlayed >= 3,
        };

        switch (sub)
        {
            case Sub.Walk:
                if (Finished()) { NextStep(); return; } // e.g. Saucy went on to the broker itself
                if (!Idle()) return;                     // Saucy may already be talking to them
                if (WalkTo(baseId)) sub = Sub.Talk;
                return;
            case Sub.Talk:
                if (++tries > 6) { Fail("They wouldn't talk (or Saucy didn't take over)."); return; }
                Interact(baseId);
                idleSince = DateTime.MinValue;
                sub = Sub.Wait;
                next = DateTime.UtcNow.AddSeconds(1.5);
                return;
            case Sub.Wait:
                if (!Idle()) { idleSince = DateTime.MinValue; return; }
                if (idleSince == DateTime.MinValue) { idleSince = DateTime.UtcNow; return; }
                if ((DateTime.UtcNow - idleSince).TotalSeconds < 2.5) return;
                // The conversation is over. Did it open the game?
                var opened = baseId switch
                {
                    MiniBroker => tracker.LastMiniOpen > talkedAt,
                    JumboBroker => tracker.LastJumboOpen > talkedAt,
                    _ => true,
                };
                if (baseId == Cashier) { s.JumboCashierVisited = true; config.Save(); }
                else if (!opened)
                {
                    // Talked, nothing opened: none left (or Saucy's module is off).
                    if (baseId == MiniBroker) tracker.MarkMiniDone(); else tracker.MarkJumboDone();
                    Plugin.ChatGui.Print($"[Saucer] {(baseId == MiniBroker ? "Mini" : "Jumbo")} Cactpot: no tickets left (counted as done). " +
                                         "If tickets were left, check Saucy's module is on.");
                }
                if (Finished()) { NextStep(); return; }
                sub = Sub.Talk;
                return;
        }
    }

    /// <summary>Sell cards to the Triple Triad Trader: menu, pick a card the game shows, confirm the amount.</summary>
    private void TickCards()
    {
        if (sub == Sub.Walk)
        {
            if (CardsInBags().Count == 0) { NextStep(); return; }
            if (!Idle()) return;
            if (WalkTo(CardTrader)) sub = Sub.Talk;
            return;
        }

        var dialog = GameUi.Get("ShopCardDialog");
        var exchange = GameUi.Get("TripleTriadCoinExchange");
        if (dialog != null && exchange != null)
        {
            var count = (int)Math.Max(1u, GameUi.UInt(exchange, 124 + PickIndex(exchange)));
            GameUi.Fire(dialog, 0, count);
            next = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (GameUi.Get("SelectYesno") is var yn && yn != null) { yn->FireCallbackInt(0); next = DateTime.UtcNow.AddSeconds(1); return; }
        if (exchange != null)
        {
            var entries = GameUi.UInt(exchange, 1);
            var pick = PickIndex(exchange);
            if (entries == 0 || pick < 0) { exchange->Close(true); plan.Clear(); NextStep(); return; }
            if (entries == lastCardCount && ++tries > 4) { Fail("The trader isn't taking the cards (dump saved)."); GameUi.Dump("TripleTriadCoinExchange"); return; }
            if (entries != lastCardCount) { if (lastCardCount != uint.MaxValue) sold++; lastCardCount = entries; tries = 0; }
            SelectCard(exchange, pick);
            next = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        var menu = GameUi.Get("SelectIconString");
        if (menu != null)
        {
            var rows = GameUi.MenuEntries(menu, true);
            var row = rows.FindIndex(r => r.Contains("Sell", StringComparison.OrdinalIgnoreCase) || r.Contains("Card", StringComparison.OrdinalIgnoreCase));
            menu->FireCallbackInt(row < 0 ? 1 : row);
            next = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        if (DateTime.UtcNow < talkedAt.AddSeconds(3)) return;
        if (++tries > 4) { Fail("The Triple Triad Trader's window didn't open."); return; }
        Interact(CardTrader);
    }

    /// <summary>
    /// The first listed card you've already learned (never sell one you haven't). Rows are read the way
    /// ECommons' reader does: row i's card id at value 164 + i, its count at 124 + i.
    /// </summary>
    private static int PickIndex(AtkUnitBase* exchange)
    {
        var n = (int)Math.Min(GameUi.UInt(exchange, 1), 40u);
        var ui = UIState.Instance();
        for (var i = 0; i < n; i++)
        {
            var id = GameUi.UInt(exchange, 164 + i);
            // [Assumption] a card id (under 1,000); if the game gives the item id instead, ask the item's unlock state.
            var learned = id == 0 || (id < 1000 ? ui != null && ui->IsTripleTriadCardUnlocked((ushort)id) : Unlock(id) == 1);
            if (learned) return i;
        }
        return -1;
    }

    private static void SelectCard(AtkUnitBase* exchange, int index)
    {
        var values = stackalloc AtkValue[2];
        values[0].SetInt(0);
        values[1].SetUInt((uint)index);
        exchange->FireCallback(2, values);
    }
}
