using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MaterialStash.Core;
using Syberdamon.Shared;

namespace MaterialStash;

/// <summary>Where a scrip goal stands right now.</summary>
public sealed record ScripPlan(
    ScripGoal Goal, ScripShopEntry Shop, int Have, int Need, int Scrips,
    int Held, int HeldValue, CollectableRecipe? Recipe, string? RecipeWhy, int CraftsNeeded);

/// <summary>Materials for one batch: what to take out of retainers, and what isn't anywhere.</summary>
public sealed record BatchMaterials(Dictionary<uint, int> Fetch, Dictionary<uint, int> Missing, int NewSlots);

/// <summary>
/// Scrip goals: "I want 20 Mason's Abrasive". Repeats until you hold the target (bags + saddlebag +
/// retainers), one goal after another:
///  1. Collectables in your bags, or scrips enough for one: Turn-In Manager turns them in and buys
///     the item (buying before scrips would cap).
///  2. Otherwise a batch of the collectable paying the most scrips per craft (or the recipe you
///     picked), sized to leave the reserve of free bag slots (collectables take a slot each).
///     Materials: your bags, then your retainers (walks to a bell), then the market board (stops so
///     you can buy, if on), then gathering (Vulcan gathers what's missing, then crafts).
///  3. Craft the batch with Vulcan (/vulcan craft) or Artisan (IPC), watch it, then back to 1.
/// Stops when a goal can't move: no usable recipe, materials that can't be gathered, no bag room,
/// a batch that made nothing, or a turn-in run that failed.
/// </summary>
public sealed unsafe class ScripGoals : IDisposable
{
    private enum Step { Idle, TurnIn, ToBell, Fetching, LeaveBell, Pricing, WaitMarket, Crafting }

    private readonly Plugin plugin;
    private Configuration C => plugin.Configuration;
    public readonly TurnInIpc TurnIn = new();
    public readonly ArtisanIpc Artisan = new();
    public readonly GatherBuddyIpc GatherBuddy = new();
    private readonly VNavIpc vnav = new();

    private Step step = Step.Idle;
    private DateTime next, stepStart;
    private readonly List<ScripGoal> goals = new();
    private int gi;
    private readonly List<string> notes = new();

    // per batch
    private bool fetchTried, marketTried;
    private Dictionary<uint, int> fetchList = new();
    // turn-in
    private int heldBefore, haveBefore;
    // crafting
    private CollectableRecipe? recipe;
    private int batch, startCount, lastMade;
    private bool viaVulcan, sawActive, artisanStopped;
    private DateTime lastActive;
    private int closeTries;

    public string Status { get; private set; } = "";
    public bool Busy => step != Step.Idle;
    public bool WaitingForMarket => step == Step.WaitMarket;
    public List<(uint ItemId, string Name, int Qty, long Price)> MarketList { get; private set; } = new();

    public ScripGoals(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) Stop("Plugin unloaded.");
    }

    private static IDataManager Data => Plugin.DataManager;

    // ================================================================ planning (also used by the window)

    public static ScripPlan? Plan(ScripGoal g, CharacterData me)
    {
        if (!ScripData.Shop(Data).TryGetValue(g.ItemId, out var shop)) return null;
        var have = Stock.Have(g.ItemId, me);
        var need = Math.Max(0, g.Target - have);
        var scrips = ScripData.Scrips(shop.CurrencyItemId);
        var rw = ScripData.Rewards(Data);
        // Only collectables that meet the lowest tier count (the appraiser refuses the rest).
        var held = ScripData.CollectablesInBags(Data, shop.CurrencyItemId)
            .Where(c => ScripData.Pays(rw[c.ItemId], c.Collectability) > 0).ToList();
        var value = held.Sum(c => ScripData.Pays(rw[c.ItemId], c.Collectability));
        CollectableRecipe? r;
        string? why;
        if (g.RecipeId != 0)
        {
            r = ScripData.Recipes(Data).FirstOrDefault(x => x.RecipeId == g.RecipeId);
            why = r == null ? "the chosen recipe wasn't found" : ScripData.CantUse(Data, r);
        }
        else
        {
            r = ScripData.Best(Data, shop.CurrencyItemId).FirstOrDefault();
            why = r != null ? null
                : ScripData.IsCrafterScrip(shop.CurrencyItemId) ? $"no collectable you can craft pays {ScripData.ScripNames[shop.CurrencyItemId]}"
                : "gatherer scrips: gather the collectables yourself (GatherBuddy), this turns them in and buys";
        }
        var shortScrips = need * shop.Cost - scrips - value;
        var crafts = r == null || r.Reward.High <= 0 || shortScrips <= 0 ? 0 : (shortScrips + r.Reward.High - 1) / r.Reward.High;
        return new ScripPlan(g, shop, have, need, scrips, held.Count, value, r, why, crafts);
    }

    private static HashSet<uint>? gatherable;

    /// <summary>Items that grow on a gathering node (crystals included), which Vulcan can gather.</summary>
    public static bool Gatherable(uint itemId)
    {
        if (gatherable == null)
        {
            gatherable = new HashSet<uint>();
            try
            {
                foreach (var g in Data.GetExcelSheet<GatheringItem>())
                    if (g.Item.RowId != 0) gatherable.Add(g.Item.RowId);
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: reading gathering items failed"); }
        }
        return gatherable.Contains(itemId % 1_000_000);
    }

    private static int InBags(uint id)
    {
        id %= 1_000_000;
        if (Items.IsCrystal(id)) return Tracker.Crystals(InventoryType.Crystals).TryGetValue(id, out var c) ? c.Qty : 0;
        var hq = id + 1_000_000;
        return Tracker.Stacks(Tracker.Bags, x => x == id || x == hq).Sum(s => s.Qty);
    }

    private static int OnRetainers(uint id, CharacterData me)
    {
        id %= 1_000_000;
        if (Items.IsCrystal(id)) return 0; // crystals aren't fetched (the crystal "keep on me" setting covers them)
        var hq = id + 1_000_000;
        return me.Retainers.Values.Sum(r => r.Items.GetValueOrDefault(id) + r.Items.GetValueOrDefault(hq));
    }

    /// <summary>
    /// Materials for a batch. Each ingredient comes from bags first, then retainers; what's left is
    /// broken down into its own recipe's materials when expand is on (Vulcan crafts the
    /// intermediate steps), otherwise it's missing.
    /// </summary>
    public static BatchMaterials Materials(CollectableRecipe r, int crafts, CharacterData me, bool useRetainers, bool expand)
    {
        var fetch = new Dictionary<uint, int>();
        var missing = new Dictionary<uint, int>();
        var bags = new Dictionary<uint, int>();
        var rets = new Dictionary<uint, int>();
        int Bag(uint id) { if (!bags.TryGetValue(id, out var n)) bags[id] = n = InBags(id); return n; }
        int Ret(uint id) { if (!rets.TryGetValue(id, out var n)) rets[id] = n = useRetainers ? OnRetainers(id, me) : 0; return n; }

        void Walk(uint id, int qty, int depth)
        {
            id %= 1_000_000;
            var fromBags = Math.Min(Bag(id), qty);
            bags[id] -= fromBags;
            qty -= fromBags;
            if (qty <= 0) return;
            var fromRets = Math.Min(Ret(id), qty);
            if (fromRets > 0) { rets[id] -= fromRets; fetch[id] = fetch.GetValueOrDefault(id) + fromRets; qty -= fromRets; }
            if (qty <= 0) return;
            if (expand && depth < 6 && !Items.IsCrystal(id) && Stock.RecipeMaking(id) is { } sub)
            {
                var n = (qty + sub.Yield - 1) / sub.Yield;
                foreach (var (ing, amount) in sub.Ingredients) Walk(ing, n * amount, depth + 1);
                return;
            }
            missing[id] = missing.GetValueOrDefault(id) + qty;
        }

        if (Stock.Recipe(r.RecipeId) is { } info)
            foreach (var (ing, amount) in info.Ingredients) Walk(ing, crafts * amount, 0);
        var inBags = Tracker.Stacks(Tracker.Bags).Select(s => s.ItemId % 1_000_000).ToHashSet();
        var newSlots = fetch.Keys.Concat(missing.Keys).Distinct().Count(id => !Items.IsCrystal(id) && !inBags.Contains(id));
        return new BatchMaterials(fetch, missing, newSlots);
    }

    public static int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    private static int CollectableCount(uint itemId) => ScripData.CollectablesInBags(Data).Count(c => c.ItemId == itemId);

    // ================================================================ start / stop

    /// <summary>Why a run can't start, or null.</summary>
    public string? CantStart()
    {
        if (Busy) return "Already running.";
        if (plugin.Tracker.Me() == null) return "Log in first.";
        if (!TurnIn.IsLoaded) return "Turn-In Manager isn't installed or loaded (it does the turn-ins and buying).";
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) return "Not in combat or a duty.";
        if (plugin.Runner.Busy) return "A stash run is going.";
        return null;
    }

    /// <summary>Work through the enabled goals (or just this one).</summary>
    public void Start(ScripGoal? only = null)
    {
        if (CantStart() is { } why) { Status = why; Plugin.ChatGui.PrintError($"[Stash] {why}"); return; }
        goals.Clear();
        goals.AddRange(only != null ? new[] { only } : C.ScripGoals.Where(g => g.Enabled && g.Target > 0));
        if (goals.Count == 0) { Status = "No scrip goals switched on."; return; }
        gi = 0;
        notes.Clear();
        ResetBatch();
        Advance();
    }

    public void Stop(string why = "Stopped.")
    {
        if (!Busy) return;
        if (step == Step.TurnIn && TurnIn.Busy) TurnIn.Stop();
        if (step == Step.Crafting && !viaVulcan) Artisan.Stop();
        if (step is Step.ToBell) plugin.Bell.Stop();
        if (step is Step.Fetching) plugin.Runner.Stop();
        if (step == Step.Crafting && viaVulcan)
            why += " Vulcan has no stop command: stop its crafting list in its own window if it's still going.";
        step = Step.Idle;
        Status = why;
        Plugin.ChatGui.Print($"[Stash] Scrip goals: {why}");
    }

    /// <summary>After buying on the market board: carry on (anything still missing is gathered).</summary>
    public void Resume()
    {
        if (step != Step.WaitMarket) return;
        Advance();
    }

    private void ResetBatch()
    {
        fetchTried = marketTried = false;
        fetchList = new();
        MarketList = new();
    }

    private void Set(Step s, string status)
    {
        step = s;
        Status = status;
        stepStart = DateTime.UtcNow;
        next = DateTime.UtcNow.AddSeconds(1);
    }

    private void Note(string text)
    {
        notes.Add(text);
        Plugin.ChatGui.Print($"[Stash] {text}");
    }

    private ScripGoal Goal => goals[gi];
    private string ItemName(uint id) => Items.Info(id)?.Name ?? ScripData.Shop(Data).GetValueOrDefault(id)?.Name ?? $"#{id}";

    // ================================================================ the loop

    /// <summary>Decide the next thing to do for the current goal (moving on to the next goal when it's done or stuck).</summary>
    private void Advance()
    {
        while (gi < goals.Count)
        {
            var me = plugin.Tracker.Me();
            if (me == null) { Stop("Logged out."); return; }
            var g = Goal;
            var plan = Plan(g, me);
            var name = ItemName(g.ItemId);
            if (plan == null) { Note($"{name}: not sold for scrips, skipped."); gi++; ResetBatch(); continue; }
            if (plan.Need <= 0) { Note($"{name}: goal reached ({plan.Have}/{g.Target})."); gi++; ResetBatch(); continue; }

            if (plan.Held > 0 || plan.Scrips >= plan.Shop.Cost)
            {
                var msg = TurnIn.Start(g.ItemId, plan.Need);
                if (msg.Length > 0) { Note($"{name}: {msg}"); gi++; ResetBatch(); continue; }
                heldBefore = plan.Held;
                haveBefore = plan.Have;
                Set(Step.TurnIn, $"{name}: turning in {plan.Held} collectable(s) and buying (Turn-In Manager)...");
                return;
            }

            if (plan.Recipe == null || plan.RecipeWhy != null)
            {
                Note($"{name}: {plan.RecipeWhy ?? "no recipe"} ({plan.Have}/{g.Target}).");
                gi++; ResetBatch(); continue;
            }
            if (StartBatch(plan, me)) return;
            gi++; ResetBatch();
        }
        Finish();
    }

    /// <summary>Line up materials for the next batch and start it. False (with a note) when the goal is stuck.</summary>
    private bool StartBatch(ScripPlan plan, CharacterData me)
    {
        var r = plan.Recipe!;
        var name = ItemName(plan.Goal.ItemId);
        var engineVulcan = C.ScripEngine == 0;
        var wanted = Math.Max(1, Math.Min(plan.CraftsNeeded, Math.Max(1, C.ScripMaxBatch)));
        var mats = Materials(r, wanted, me, C.ScripUseRetainers, engineVulcan);
        var free = FreeSlots();
        var size = Math.Min(wanted, free - C.ScripReserveSlots - mats.NewSlots);
        if (size <= 0)
        {
            Note($"{name}: not enough bag room for a batch ({free} free, keeping {C.ScripReserveSlots} open, {mats.NewSlots} for new materials).");
            return false;
        }
        if (size < wanted) mats = Materials(r, size, me, C.ScripUseRetainers, engineVulcan);
        batch = size;
        recipe = r;

        // 1. retainers
        if (mats.Fetch.Count > 0 && C.ScripUseRetainers && !fetchTried)
        {
            fetchTried = true;
            fetchList = mats.Fetch;
            closeTries = 0;
            if (Runner.BellListOpen) { plugin.Runner.StartFetch(fetchList); Set(Step.Fetching, $"{name}: taking materials out of your retainers..."); return true; }
            plugin.Bell.Start();
            Set(Step.ToBell, $"{name}: going to a summoning bell for materials on your retainers...");
            return true;
        }

        // Artisan crafts only the recipe itself: missing intermediate crafts need Vulcan.
        if (!engineVulcan && mats.Missing.Count > 0)
        {
            var deep = Materials(r, size, me, C.ScripUseRetainers, true);
            if (deep.Missing.Keys.All(Gatherable) && C.ScripGather && GatherBuddy.IsLoaded)
            {
                Note($"{name}: materials missing; Vulcan gathers and crafts this batch (Artisan can't gather).");
                engineVulcan = true;
                mats = deep;
            }
        }

        // 2. market board
        if (mats.Missing.Count > 0 && C.ScripBuyMarket && !marketTried)
        {
            plugin.Market.Request(mats.Missing.Keys, Selling.HomeWorld(), Math.Max(10, C.PriceRefreshMinutes));
            fetchList = mats.Missing; // remembered for the pricing step
            Set(Step.Pricing, $"{name}: checking market prices for {mats.Missing.Count} missing material(s)...");
            return true;
        }

        // 3. gathering
        if (mats.Missing.Count > 0)
        {
            var notGatherable = mats.Missing.Where(kv => !Gatherable(kv.Key)).ToList();
            if (notGatherable.Count > 0)
            {
                Note($"{name}: short of {Describe(notGatherable)} (not gatherable: buy or make it, then run again).");
                return false;
            }
            if (!C.ScripGather)
            {
                Note($"{name}: out of {Describe(mats.Missing)} (gathering is off).");
                return false;
            }
            engineVulcan = true;
        }

        // 4. craft
        viaVulcan = engineVulcan;
        startCount = lastMade = CollectableCount(r.ResultId);
        sawActive = artisanStopped = false;
        lastActive = DateTime.UtcNow;
        var started = viaVulcan
            ? Plugin.CommandManager.ProcessCommand($"/vulcan craft {r.RecipeId} {batch}")
            : Artisan.Craft(r.RecipeId, batch);
        if (!started)
        {
            Note($"{name}: couldn't start {(viaVulcan ? "Vulcan (is GatherBuddy Reborn loaded?)" : "Artisan (is it loaded?)")}.");
            return false;
        }
        Set(Step.Crafting, $"{name}: {(viaVulcan ? "Vulcan" : "Artisan")} making {batch} {r.Name} ({r.Job}, up to {r.Reward.High} scrips each)" +
                           (mats.Missing.Count > 0 ? $", gathering {Describe(mats.Missing)} first" : "") + "...");
        return true;
    }

    private string Describe(IEnumerable<KeyValuePair<uint, int>> items) =>
        string.Join(", ", items.Take(6).Select(kv => $"{kv.Value} {ItemName(kv.Key)}")) + (items.Count() > 6 ? $" and {items.Count() - 6} more" : "");

    private void Finish()
    {
        step = Step.Idle;
        Status = notes.Count == 0 ? "Scrip goals: nothing to do." : "Scrip goals done: " + string.Join(" ", notes);
        Plugin.ChatGui.Print("[Stash] Scrip goals finished.");
    }

    // ================================================================ tick

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(1);
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat] && step != Step.Crafting) { Stop("Combat started."); return; }
            switch (step)
            {
                case Step.TurnIn: TickTurnIn(); break;
                case Step.ToBell: TickToBell(); break;
                case Step.Fetching: TickFetching(); break;
                case Step.LeaveBell: TickLeaveBell(); break;
                case Step.Pricing: TickPricing(); break;
                case Step.Crafting: TickCrafting(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Scrip goals failed");
            Stop("Error (see /xllog).");
        }
    }

    private void TickTurnIn()
    {
        if (TurnIn.Busy) { Status = $"{ItemName(Goal.ItemId)}: {TurnIn.Status}"; return; }
        if ((DateTime.UtcNow - stepStart).TotalSeconds < 2) return;
        if (TurnIn.Failed) { Stop($"Turn-In Manager stopped: {TurnIn.Status}"); return; }
        var me = plugin.Tracker.Me();
        var plan = me == null ? null : Plan(Goal, me);
        if (plan != null && plan.Held >= heldBefore && plan.Have <= haveBefore && TurnIn.Bought == 0)
        {
            Note($"{ItemName(Goal.ItemId)}: the turn-in run changed nothing ({TurnIn.Status}).");
            gi++;
        }
        ResetBatch();
        Advance();
    }

    private void TickToBell()
    {
        if (Runner.BellListOpen && !plugin.Bell.Busy)
        {
            if ((DateTime.UtcNow - stepStart).TotalSeconds < 2) return; // let the list be read
            plugin.Runner.StartFetch(fetchList);
            if (!plugin.Runner.Busy)
            {
                if ((DateTime.UtcNow - stepStart).TotalSeconds < 10) return;
                Note($"{ItemName(Goal.ItemId)}: couldn't take materials out ({plugin.Runner.Status}).");
                Set(Step.LeaveBell, "Leaving the bell...");
                return;
            }
            Set(Step.Fetching, $"{ItemName(Goal.ItemId)}: taking materials out of your retainers...");
            return;
        }
        if (plugin.Bell.Busy) { Status = plugin.Bell.Status; stepStart = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - stepStart).TotalSeconds > 8) Stop($"Couldn't reach a summoning bell: {plugin.Bell.Status}");
    }

    private void TickFetching()
    {
        if (plugin.Runner.Busy) { Status = plugin.Runner.Status; return; }
        Set(Step.LeaveBell, "Leaving the bell...");
    }

    private void TickLeaveBell()
    {
        if (Runner.BellListOpen)
        {
            if (++closeTries > 6) { Stop("Couldn't close the retainer list."); return; }
            GameUi.Close("RetainerList");
            return;
        }
        if ((DateTime.UtcNow - stepStart).TotalSeconds < 2) return;
        Advance();
    }

    private void TickPricing()
    {
        var missing = fetchList;
        var known = missing.Keys.All(id => plugin.Market.Get(id) != null);
        if (!known && (DateTime.UtcNow - stepStart).TotalSeconds < 15) return;
        marketTried = true;
        MarketList = missing
            .Select(kv => (kv.Key, ItemName(kv.Key), kv.Value, plugin.Market.Get(kv.Key)?.Lowest ?? 0))
            .Where(x => x.Item4 > 0 && x.Item4 <= C.ScripMaxPrice)
            .Select(x => (x.Key, x.Item2, x.Value, x.Item4))
            .ToList();
        fetchList = new();
        if (MarketList.Count == 0) { Advance(); return; }
        Set(Step.WaitMarket, $"{ItemName(Goal.ItemId)}: buy these on the market board, then press Resume: " +
                             string.Join(", ", MarketList.Select(m => $"{m.Qty} {m.Name} (~{m.Price:N0} gil)")));
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private static readonly ConditionFlag[] Activity =
    {
        ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction,
        ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.Mounted, ConditionFlag.InFlight,
        ConditionFlag.Casting, ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
    };

    private void TickCrafting()
    {
        var made = CollectableCount(recipe!.ResultId) - startCount;
        var active = made != lastMade || Activity.Any(f => Plugin.Condition[f]) || vnav.IsBusy
                     || (viaVulcan ? GatherBuddy.AutoGathering : Artisan.Busy);
        lastMade = made;
        if (active) { lastActive = DateTime.UtcNow; sawActive = true; }
        Status = $"{ItemName(Goal.ItemId)}: {(viaVulcan ? "Vulcan" : "Artisan")} {Math.Max(0, made)}/{batch} {recipe.Name}...";

        // Keep the reserve: Artisan can be told to stop (Vulcan's batch was sized to fit).
        if (!viaVulcan && !artisanStopped && FreeSlots() < C.ScripReserveSlots) { Artisan.Stop(); artisanStopped = true; Note("Bags nearly full: asked Artisan to stop."); }

        var idleFor = (DateTime.UtcNow - lastActive).TotalSeconds;
        var done = made >= batch || (sawActive && idleFor > 30) || artisanStopped && idleFor > 5;
        if (!sawActive && (DateTime.UtcNow - stepStart).TotalSeconds > 90)
        {
            Stop($"{(viaVulcan ? "Vulcan" : "Artisan")} didn't start crafting {recipe.Name}.");
            return;
        }
        if (!done) return;
        if (made <= 0)
        {
            Note($"{ItemName(Goal.ItemId)}: the batch made no {recipe.Name} (out of materials, or the craft missed the collectability? see {(viaVulcan ? "Vulcan" : "Artisan")}'s messages).");
            gi++;
        }
        ResetBatch();
        Advance();
    }
}
