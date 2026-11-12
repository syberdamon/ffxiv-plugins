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
    int Held, int HeldValue, CollectableRecipe? Recipe, string? RecipeWhy, int CraftsNeeded, CollectableReward? Gather = null,
    IReadOnlyList<CollectableReward>? GatherAll = null);

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
    private enum Step { Idle, TurnInStart, TurnIn, ToBell, Fetching, LeaveBell, Pricing, WaitMarket, Crafting, Gathering, Cosmo, Earning, ToMoon }

    private readonly Plugin plugin;
    private Configuration C => plugin.Configuration;
    public readonly TurnInIpc TurnIn = new();
    public readonly ArtisanIpc Artisan = new();
    public readonly GatherBuddyIpc GatherBuddy = new();
    private readonly VNavIpc vnav = new();
    public readonly GatherBuddyLists Lists = new();
    public readonly IceIpc Ice = new();
    public CosmoShop Cosmo { get; private set; } = null!;
    // cosmocredits: tried this batch (goal item / materials), ICE run once per goal
    private bool cosmoGoalTried, cosmoMatTried, iceTried;
    private Dictionary<uint, int> cosmoWant = new();
    private int iceTarget;
    private int iceGoal = -1;
    private readonly LifestreamIpc lifestream = new();
    private bool moonTried;
    /// <summary>The cosmocredit exchange failed for this goal: don't keep going back to it.</summary>
    private bool cosmoBroken;
    private TurnInStarter starter = null!;
    private (int Held, int Have) turnInBefore;
    private DateTime arrived = DateTime.MinValue;
    private string cosmoFor = "";
    private CollectableReward? gatherItem;
    /// <summary>The collectables in our GatherBuddy list this batch, and what the batch should earn.</summary>
    private List<CollectableReward> gatherSet = new();
    private int startValue, batchScrips;
    private uint gatherCurrency;
    private bool listsTouched;

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
    /// <summary>ICE is running for this scrip goal (an ICE break may pause it for the retainers).</summary>
    public bool Earning => step == Step.Earning;
    public bool WaitingForMarket => step == Step.WaitMarket;
    public List<(uint ItemId, string Name, int Qty, long Price)> MarketList { get; private set; } = new();

    public ScripGoals(Plugin plugin)
    {
        this.plugin = plugin;
        Cosmo = new CosmoShop(plugin.Configuration);
        starter = new TurnInStarter(TurnIn, GatherBuddy, vnav);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) Stop("Plugin unloaded.");
        Cosmo.Dispose();
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
        CollectableRecipe? r = null;
        string? why;
        var shortScrips0 = need * shop.Cost - scrips - value;
        if (!ScripData.IsCrafterScrip(shop.CurrencyItemId))
        {
            // Gatherer scrips: gather a collectable with GatherBuddy's auto-gather.
            // Default (GatherItemId 0): every collectable you can gather that pays this scrip, so GatherBuddy
            // cycles to whichever node is up instead of waiting on one timed node. Or one you picked.
            var options = ScripData.Gatherables(Data, shop.CurrencyItemId);
            var usable = options.Where(x => ScripData.CantGather(Data, x) == null).ToList();
            var pick = g.GatherItemId != 0 ? options.FirstOrDefault(x => x.ItemId == g.GatherItemId) : usable.FirstOrDefault();
            var all = g.GatherItemId != 0 ? (pick == null ? new List<CollectableReward>() : new List<CollectableReward> { pick }) : usable;
            why = pick == null ? $"no collectable you can gather pays {ScripData.ScripNames[shop.CurrencyItemId]}" : g.GatherItemId != 0 ? ScripData.CantGather(Data, pick) : null;
            // Count estimate from the lowest top-tier payout in the set (several kinds get mixed). [Estimate]
            var per = all.Count == 0 ? 0 : all.Min(x => x.High);
            var n = per <= 0 || shortScrips0 <= 0 ? 0 : (shortScrips0 + per - 1) / per;
            return new ScripPlan(g, shop, have, need, scrips, held.Count, value, null, why, n, pick, all);
        }
        if (g.RecipeId != 0)
        {
            r = ScripData.Recipes(Data).FirstOrDefault(x => x.RecipeId == g.RecipeId);
            why = r == null ? "the chosen recipe wasn't found" : ScripData.CantUse(Data, r);
        }
        else
        {
            r = ScripData.Best(Data, shop.CurrencyItemId).FirstOrDefault();
            why = r != null ? null : NoneUsable(shop.CurrencyItemId);
        }
        var shortScrips = need * shop.Cost - scrips - value;
        var crafts = r == null || r.Reward.High <= 0 || shortScrips <= 0 ? 0 : (shortScrips + r.Reward.High - 1) / r.Reward.High;
        return new ScripPlan(g, shop, have, need, scrips, held.Count, value, r, why, crafts);
    }

    /// <summary>Why no crafted collectable fits: how many pay this scrip and the commonest reason they're refused.</summary>
    private static string NoneUsable(uint currency)
    {
        var all = ScripData.Recipes(Data).Where(x => x.Reward.CurrencyItemId == currency).ToList();
        var name = ScripData.ScripNames[currency];
        if (all.Count == 0) return $"no collectable recipes found paying {name} (game data not read? see /xllog)";
        var top = all.Select(x => ScripData.CantUse(Data, x) ?? "").GroupBy(x => x).OrderByDescending(g => g.Count()).First();
        return $"none of the {all.Count} collectables paying {name} is usable; most: {top.Key}";
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
        iceGoal = -1;
        moonTried = false;
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
        if (step == Step.Gathering) GatherBuddy.StopAutoGather();
        if (step == Step.Cosmo) Cosmo.Stop();
        if (step == Step.Earning) Ice.StopAndReset();
        if (step == Step.ToMoon && lifestream.IsBusy) lifestream.Abort();
        RestoreLists();
        if (step == Step.Crafting && viaVulcan)
            why += " Vulcan has no stop command: stop its crafting list in its own window if it's still going.";
        step = Step.Idle;
        Status = why;
        Log("STOP " + why);
        SaveLog();
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
        cosmoGoalTried = cosmoMatTried = false;
        moonTried = false; // a later batch may go back (e.g. scrips bought some, cosmocredits buy the rest); cosmoBroken stops repeats
        fetchList = new();
        MarketList = new();
    }

    private static readonly List<string> RunLog = new();

    private static void Log(string msg)
    {
        Plugin.Log.Info($"[Stash/Scrips] {msg}");
        lock (RunLog) RunLog.Add($"{DateTime.Now:HH:mm:ss.f} {msg}");
    }

    /// <summary>The run's lines to pluginConfigs/MaterialStash/dumps/&lt;time&gt;_scripgoals.txt.</summary>
    private static void SaveLog()
    {
        try
        {
            string text;
            lock (RunLog) { text = string.Join(Environment.NewLine, RunLog); RunLog.Clear(); }
            if (text.Length == 0) return;
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dumps");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_scripgoals.txt"), text);
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't write the scrip goals log"); }
    }

    private void Set(Step s, string status)
    {
        Log($"{s}: {status}");
        step = s;
        Status = status;
        stepStart = DateTime.UtcNow;
        next = DateTime.UtcNow.AddSeconds(1);
    }

    private void Note(string text)
    {
        Log("NOTE " + text);
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
            if (gi != iceGoal) { iceTried = false; moonTried = false; cosmoBroken = false; iceGoal = gi; }
            var me = plugin.Tracker.Me();
            if (me == null) { Stop("Logged out."); return; }
            var g = Goal;
            var plan = Plan(g, me);
            var name = ItemName(g.ItemId);
            if (plan == null) { Note($"{name}: not sold for scrips, skipped."); gi++; ResetBatch(); continue; }
            if (plan.Need <= 0) { Note($"{name}: goal reached ({plan.Have}/{g.Target})."); gi++; ResetBatch(); continue; }

            if (plan.Held > 0 || plan.Scrips >= plan.Shop.Cost)
            {
                // Started once you're free to travel (off the mount, GatherBuddy stopped), retrying refusals that pass.
                starter.Begin(g.ItemId, plan.Need);
                turnInBefore = (plan.Held, plan.Have);
                Set(Step.TurnInStart, $"{name}: getting ready to turn in {plan.Held} collectable(s) and buy (Turn-In Manager)...");
                return;
            }

            // Cosmocredits first for the item itself (no materials spent).
            if (C.ScripUseCosmo && !cosmoGoalTried && !cosmoBroken && CosmoData.Sells(g.ItemId))
            {
                cosmoGoalTried = true;
                if (TryCosmo(new Dictionary<uint, int> { [g.ItemId] = plan.Need }, name)) return;
            }

            if (plan.Gather != null && plan.RecipeWhy == null)
            {
                if (StartGather(plan)) return;
                gi++; ResetBatch(); continue;
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

        // 2. cosmocredits (no materials spent), for what the exchange sells
        var cosmoMissing = mats.Missing.Where(kv => CosmoData.Sells(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (cosmoMissing.Count > 0 && C.ScripUseCosmo && !cosmoMatTried && !cosmoBroken)
        {
            cosmoMatTried = true;
            if (TryCosmo(cosmoMissing, name)) return true;
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
        RestoreLists();
        step = Step.Idle;
        Status = notes.Count == 0 ? "Scrip goals: nothing to do." : "Scrip goals done: " + string.Join(" ", notes);
        Log("FINISHED");
        SaveLog();
        Plugin.ChatGui.Print("[Stash] Scrip goals finished.");
    }

    // ================================================================ cosmocredits

    /// <summary>
    /// Buy these with cosmocredits (above the reserve) at Mesouaidonque; short of credits, run ICE
    /// until there are enough (once per goal), then buy. Only in a Cosmic Exploration zone.
    /// True when a step started.
    /// </summary>
    private bool TryCosmo(Dictionary<uint, int> want, string name)
    {
        var spendable = CosmoData.Credits - C.ScripCosmoReserve;
        var cheapest = want.Keys.Min(id => CosmoData.Costs[id % 1_000_000]);
        if (!CosmoData.InZone)
        {
            // Worth the trip only if credits can be spent now, or earned there with ICE.
            var worth = spendable >= cheapest || (C.ScripCosmoEarn && !iceTried && Ice.IsLoaded);
            if (!worth || !C.ScripCosmoTravel || moonTried || !lifestream.IsLoaded || lifestream.IsBusy) return false;
            moonTried = true;
            if (!lifestream.Execute("cosmic")) return false;
            cosmoWant = want;
            cosmoFor = name;
            arrived = DateTime.MinValue;
            Set(Step.ToMoon, $"{name}: going to Cosmic Exploration (Lifestream /li cosmic) for cosmocredits...");
            return true;
        }
        var total = want.Sum(kv => CosmoData.Costs[kv.Key % 1_000_000] * kv.Value);
        if (spendable >= cheapest)
        {
            var msg = Cosmo.Start(want);
            if (msg.Length > 0) { Note($"{name}: cosmocredits: {msg}"); return false; }
            cosmoWant = want;
            Set(Step.Cosmo, $"{name}: buying with cosmocredits ({CosmoData.Credits:N0}, keeping {C.ScripCosmoReserve:N0})...");
            return true;
        }
        if (C.ScripCosmoEarn && !iceTried && Ice.IsLoaded && !Ice.Running)
        {
            iceTried = true;
            iceTarget = Math.Min(CosmoData.Cap, C.ScripCosmoReserve + total);
            if (iceTarget <= CosmoData.Credits) return false;
            if (!Ice.EarnTo(iceTarget)) { Note($"{name}: couldn't start ICE."); return false; }
            cosmoWant = want;
            Set(Step.Earning, $"{name}: ICE earning cosmocredits to {iceTarget:N0}...");
            return true;
        }
        return false;
    }

    private void TickToMoon()
    {
        var moving = lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
                     || Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent];
        if (moving) { arrived = DateTime.MinValue; Status = $"{cosmoFor}: travelling (Lifestream {(lifestream.IsBusy ? "busy" : "done")})..."; return; }
        if (CosmoData.InZone && Plugin.ObjectTable.LocalPlayer != null)
        {
            if (arrived == DateTime.MinValue) arrived = DateTime.UtcNow;
            if ((DateTime.UtcNow - arrived).TotalSeconds < 4) return; // let the zone settle
            if (TryCosmo(cosmoWant, cosmoFor)) return;
            cosmoBroken = true; // made the trip and nothing to do there: don't come back for this goal
            Advance();
            return;
        }
        if ((DateTime.UtcNow - stepStart).TotalSeconds > 240) { cosmoBroken = true; Note("Lifestream didn't reach Cosmic Exploration: skipping cosmocredits."); Advance(); }
    }

    private void TickCosmo()
    {
        if (Cosmo.Busy) { Status = $"{ItemName(Goal.ItemId)}: {Cosmo.Status}"; return; }
        if (Cosmo.Failed) { cosmoBroken = true; Note($"{ItemName(Goal.ItemId)}: {Cosmo.Status} (not trying the exchange again for this goal)"); }
        else if (Cosmo.Bought.Count == 0) cosmoBroken = true; // nothing it could buy: no more trips for this goal
        Advance();
    }

    private void TickEarning()
    {
        if (plugin.IceBreak.Active)
        {
            stepStart = DateTime.UtcNow; // ICE is off on purpose: wait for the break to bring it back
            Status = $"{ItemName(Goal.ItemId)}: ICE paused for the retainers ({plugin.IceBreak.Status})";
            return;
        }
        var credits = CosmoData.Credits;
        var running = Ice.Running;
        Status = $"{ItemName(Goal.ItemId)}: ICE earning cosmocredits {credits:N0} / {iceTarget:N0}{(running ? $" ({Ice.State})" : "")}...";
        if (credits >= iceTarget || (!running && (DateTime.UtcNow - stepStart).TotalSeconds > 20))
        {
            Ice.StopAndReset();
            if (credits - C.ScripCosmoReserve >= cosmoWant.Keys.Min(id => CosmoData.Costs[id % 1_000_000]) && CosmoData.InZone)
            {
                var msg = Cosmo.Start(cosmoWant);
                if (msg.Length == 0) { Set(Step.Cosmo, $"{ItemName(Goal.ItemId)}: buying with cosmocredits..."); return; }
                Note($"{ItemName(Goal.ItemId)}: cosmocredits: {msg}");
            }
            else Note($"{ItemName(Goal.ItemId)}: ICE stopped with {credits:N0} cosmocredits (wanted {iceTarget:N0}).");
            Advance();
            return;
        }
    }

    // ================================================================ gathering (gatherer scrips)

    /// <summary>
    /// Gather a batch of a collectable with GatherBuddy's auto-gather: switch your lists off (noted
    /// so they come back on), put the collectable in our own list, switch it on, start auto-gather.
    /// </summary>
    private bool StartGather(ScripPlan plan)
    {
        var name = ItemName(plan.Goal.ItemId);
        var set = (plan.GatherAll ?? (plan.Gather != null ? new[] { plan.Gather } : Array.Empty<CollectableReward>())).ToList();
        if (set.Count == 0) { Note($"{name}: no collectable to gather."); return false; }
        var label = set.Count == 1 ? set[0].Name : $"{set.Count} collectables";
        if (!GatherBuddy.IsLoaded) { Note($"{name}: GatherBuddy Reborn isn't loaded (it gathers {label})."); return false; }
        if (GatherBuddy.AutoGathering) { Note($"{name}: GatherBuddy's auto-gather is already running: stop it first."); return false; }
        if (!Lists.Connect()) { Note($"{name}: {Lists.Error}"); return false; }

        var free = FreeSlots();
        var wanted = Math.Max(1, Math.Min(plan.CraftsNeeded, Math.Max(1, C.ScripMaxBatch)));
        batch = Math.Min(wanted, free - C.ScripReserveSlots);
        if (batch <= 0) { Note($"{name}: not enough bag room for a batch ({free} free, keeping {C.ScripReserveSlots} open)."); return false; }
        // Scrips this batch should bring in: what the goal is still short, capped by what fits in the bags.
        var shortScrips = Math.Max(1, plan.Need * plan.Shop.Cost - plan.Scrips - plan.HeldValue);
        batchScrips = Math.Min(shortScrips, batch * set.Max(x => x.High));

        var skipped = new List<string>();
        try
        {
            listsTouched = true;
            // Your lists off, remembered (kept across a crash; the tab offers to switch them back).
            foreach (var n in Lists.EnabledLists())
            {
                var l = Lists.Find(n);
                if (l == null) continue;
                if (!C.ScripRestoreLists.Contains(n)) C.ScripRestoreLists.Add(n);
                C.Save();
                if (!Lists.SetEnabled(l, false)) { Note($"{name}: couldn't switch off your GatherBuddy list '{n}'."); return false; }
            }
            // Each kind's quantity covers the whole batch on its own: GatherBuddy goes to whichever node is up,
            // and we stop it once the batch's scrips are in (or the bags are full).
            var wants = set.Select(x => (x.ItemId, CollectableCount(x.ItemId) + Math.Max(1, (batchScrips + x.High - 1) / Math.Max(1, x.High)))).ToList();
            var ours = Lists.SetOurList(wants, out var unknown);
            if (ours == null) { Note($"{name}: {Lists.Error}"); return false; }
            if (!Lists.SetEnabled(ours, true)) { Note($"{name}: GatherBuddy wouldn't switch on the list (bait / perception check?)."); return false; }
            var kept = set.Where(x => !unknown.Contains(x.ItemId) && Lists.ItemEnabled(ours, x.ItemId)).ToList();
            skipped = set.Except(kept).Select(x => x.Name).ToList();
            if (kept.Count == 0) { Note($"{name}: GatherBuddy switched every collectable off in the list (perception too low?)."); return false; }
            set = kept;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Material Stash: setting up GatherBuddy's list failed");
            Note($"{name}: setting up GatherBuddy's list failed ({ex.Message}).");
            return false;
        }

        if (!GatherBuddy.StartAutoGather()) { Note($"{name}: couldn't start GatherBuddy's auto-gather."); return false; }
        gatherSet = set;
        gatherItem = set[0];
        gatherCurrency = plan.Shop.CurrencyItemId;
        startCount = lastMade = SetCount();
        startValue = SetValue();
        sawActive = false;
        lastActive = DateTime.UtcNow;
        if (skipped.Count > 0) Log($"GatherBuddy left out: {string.Join(", ", skipped)}");
        Log($"gathering for {batchScrips} scrips (max {batch} items) from: {string.Join(", ", set.Select(x => $"{x.Name} ({ScripData.GatherJob(x.JobTab)}, {x.High})"))}");
        Set(Step.Gathering, $"{name}: GatherBuddy gathering ~{batchScrips} scrips' worth from {(set.Count == 1 ? set[0].Name : $"{set.Count} collectables (whichever node is up)")}...");
        return true;
    }

    /// <summary>Collectables of this batch's kinds in your bags.</summary>
    private int SetCount()
    {
        var ids = gatherSet.Select(x => x.ItemId).ToHashSet();
        return ScripData.CollectablesInBags(Data).Count(c => ids.Contains(c.ItemId));
    }

    /// <summary>What the collectables of this batch's kinds in your bags pay.</summary>
    private int SetValue()
    {
        var ids = gatherSet.Select(x => x.ItemId).ToHashSet();
        var rw = ScripData.Rewards(Data);
        return ScripData.CollectablesInBags(Data, gatherCurrency).Where(c => ids.Contains(c.ItemId)).Sum(c => ScripData.Pays(rw[c.ItemId], c.Collectability));
    }

    private void TickGathering()
    {
        var made = SetCount() - startCount;
        var earned = SetValue() - startValue;
        var on = GatherBuddy.AutoGathering;
        if (made != lastMade || on) { lastActive = DateTime.UtcNow; sawActive = true; }
        lastMade = made;
        Status = $"{ItemName(Goal.ItemId)}: GatherBuddy {Math.Max(0, made)} gathered, {Math.Max(0, earned)}/{batchScrips} scrips...";

        var full = FreeSlots() <= C.ScripReserveSlots;
        if (earned >= batchScrips || made >= batch || full)
        {
            GatherBuddy.StopAutoGather();
            if (full && earned < batchScrips) Note($"Bags down to {C.ScripReserveSlots} free slots: stopped gathering.");
            EndGather(made);
            return;
        }
        if (!sawActive && (DateTime.UtcNow - stepStart).TotalSeconds > 60) { Stop("GatherBuddy's auto-gather didn't start."); return; }
        // Auto-gather switched itself off (list done, or it gave up): wait a little in case it's between steps.
        if (sawActive && !on && (DateTime.UtcNow - lastActive).TotalSeconds > 20) EndGather(made);
    }

    private void EndGather(int made)
    {
        RestoreLists();
        if (made <= 0)
        {
            Note($"{ItemName(Goal.ItemId)}: gathered nothing (see GatherBuddy's status: {GatherBuddy.Status}).");
            gi++;
        }
        ResetBatch();
        Advance();
    }

    /// <summary>Our list off, and your lists back on as they were.</summary>
    public void RestoreLists()
    {
        if (!listsTouched && C.ScripRestoreLists.Count == 0) return;
        listsTouched = false;
        try
        {
            if (!Lists.Connect()) { Plugin.ChatGui.PrintError($"[Stash] Couldn't switch your GatherBuddy lists back on: {Lists.Error}"); return; }
            if (Lists.Find(GatherBuddyLists.OurList) is { } ours) Lists.SetEnabled(ours, false);
            var failed = new List<string>();
            foreach (var n in C.ScripRestoreLists.ToList())
                if (Lists.Find(n) is not { } l || !Lists.SetEnabled(l, true)) failed.Add(n);
            C.ScripRestoreLists = failed;
            C.Save();
            if (failed.Count > 0) Plugin.ChatGui.PrintError($"[Stash] Couldn't switch these GatherBuddy lists back on: {string.Join(", ", failed)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Material Stash: restoring GatherBuddy lists failed");
            Plugin.ChatGui.PrintError("[Stash] Couldn't switch your GatherBuddy lists back on (see /xllog); the Scrip goals tab can retry.");
        }
    }

    // ================================================================ tick

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(1);
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat] && step is not (Step.Crafting or Step.Gathering or Step.Earning or Step.TurnInStart)) { Stop("Combat started."); return; }
            switch (step)
            {
                case Step.TurnInStart: TickTurnInStart(); break;
                case Step.TurnIn: TickTurnIn(); break;
                case Step.ToBell: TickToBell(); break;
                case Step.Fetching: TickFetching(); break;
                case Step.LeaveBell: TickLeaveBell(); break;
                case Step.Pricing: TickPricing(); break;
                case Step.Crafting: TickCrafting(); break;
                case Step.Gathering: TickGathering(); break;
                case Step.Cosmo: TickCosmo(); break;
                case Step.Earning: TickEarning(); break;
                case Step.ToMoon: TickToMoon(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Scrip goals failed");
            Stop("Error (see /xllog).");
        }
    }

    private void TickTurnInStart()
    {
        var name = ItemName(Goal.ItemId);
        var r = starter.Tick();
        if (r == null) { Status = $"{name}: {(starter.Waiting.Length > 0 ? $"waiting to travel ({starter.Waiting})" : "starting the turn-in")}..."; return; }
        var (why, stuck) = r.Value;
        if (why.Length == 0)
        {
            heldBefore = turnInBefore.Held;
            haveBefore = turnInBefore.Have;
            Set(Step.TurnIn, $"{name}: turning in {heldBefore} collectable(s) and buying (Turn-In Manager)...");
            return;
        }
        // Stop (don't quietly skip the goal): the collectables are still in your bags.
        if (stuck) { Stop($"{name}: Turn-In Manager couldn't start ({why}). Your collectables are still in your bags; Run again or use Turn in collectables."); return; }
        Note($"{name}: {why}");
        gi++;
        ResetBatch();
        Advance();
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
