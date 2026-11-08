using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using MaterialStash.Core;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ActionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager;
using ActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using CabinetRow = Lumina.Excel.Sheets.Cabinet;

namespace MaterialStash;

/// <summary>What to do with a stack on a retainer.</summary>
public enum Verdict { Keep, Use, CardMgp, Dresser, Exchange, Market, Vendor, GrandCompany, Desynth, Discard }

/// <summary>One item on one retainer (all its stacks), with the suggested verdict and why.</summary>
public sealed record CleanLine(
    ulong Retainer, uint Key, string Name, int Qty, int Stacks, long Unit, long Value, Verdict Auto, Verdict Verdict,
    bool Overridden, string Why, List<string> Notes, Regain.Effort Easiest, List<string> Ways, bool PricePending)
{
    public uint ItemId => Key % 1_000_000;
    public bool Hq => Key >= 1_000_000;
    public long StackValue => Stacks <= 1 ? Value : Value / Stacks;
}

/// <summary>
/// The Clean-up tab's engine: goes through the retainer snapshots (retainers only, not bags) and
/// suggests a verdict per item: learn it, sell it, vendor it, turn it in, desynth it, keep its look,
/// exchange it, or discard it, judged by value (home-world market, vendor price) and by whether the
/// look or the unlock is still needed. Also records the glamour dresser (when you open it) and the
/// Armoire (when it's loaded at an inn), takes dresser duplicates out, and uses unlearned items.
/// </summary>
public sealed unsafe class Cleanup : IDisposable
{
    public const double MarketTax = 0.95;

    private sealed record Facts(uint Id, string Name, uint Category, bool Marketable, long Vendor, bool Equip, uint SlotCat, byte Rarity,
        bool Unique, bool Untradable, bool Indisposable, bool Desynth, ulong Model, byte Dyes, bool Card, bool HasAction, uint CabinetId, int Ilvl);

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly Market market;
    private readonly Dictionary<uint, Facts> facts = new();
    private Dictionary<(ulong, uint), List<uint>>? looks;
    private Dictionary<uint, uint>? cabinetOf;
    private readonly Dictionary<uint, Verdict> verdictOf = new();
    private DateTime nextCapture = DateTime.MinValue, nextUse = DateTime.MinValue, nextRestore = DateTime.MinValue;

    public List<CleanLine> Lines { get; private set; } = new();
    public DateTime Analyzed { get; private set; } = DateTime.MinValue;

    // ---- use loop
    public bool Using { get; private set; }
    private readonly Dictionary<uint, int> useTries = new();
    private int used;
    // ---- desynthesis loop
    public bool Desynthing { get; private set; }
    private DateTime nextDesynth = DateTime.MinValue;
    private (InventoryType Type, int Slot, uint Item, int Qty)? desynthCur;
    private int desynthDone, desynthTries;
    private bool desynthDumped;
    private readonly HashSet<uint> desynthSkip = new();
    private readonly List<string> desynthRefused = new();
    // ---- dresser duplicates
    public bool Restoring { get; private set; }
    private int restored, restoreWaits;
    private uint lastRestoredId;
    public string Status { get; private set; } = "";

    public Cleanup(Configuration config, Tracker tracker, Market market)
    {
        this.config = config;
        this.tracker = tracker;
        this.market = market;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static string Label(Verdict v) => v switch
    {
        Verdict.Keep => "Keep",
        Verdict.Use => "Use (not learned yet)",
        Verdict.CardMgp => "Card: sell for MGP",
        Verdict.Dresser => "Keep the look (dresser/armoire)",
        Verdict.Exchange => "Exchange (token)",
        Verdict.Market => "Sell on the market",
        Verdict.Vendor => "Vendor (retainer sells it)",
        Verdict.GrandCompany => "Grand Company turn-in",
        Verdict.Desynth => "Desynthesize",
        Verdict.Discard => "Discard",
        _ => v.ToString(),
    };

    // ================================================================ item facts

    private Facts? Fact(uint id)
    {
        id %= 1_000_000;
        if (facts.TryGetValue(id, out var f)) return f;
        var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
        if (row == null || id == 0) return null;
        var r = row.Value;
        cabinetOf ??= BuildCabinet();
        f = new Facts(id, r.Name.ExtractText(), r.ItemUICategory.RowId, r.ItemSearchCategory.RowId != 0 && !r.IsUntradable, r.PriceLow,
            r.EquipSlotCategory.RowId != 0, r.EquipSlotCategory.RowId, r.Rarity, r.IsUnique, r.IsUntradable, r.IsIndisposable, r.Desynth != 0,
            r.ModelMain, r.DyeCount, r.ItemUICategory.RowId == 86, r.ItemAction.RowId != 0, cabinetOf.GetValueOrDefault(id), (int)r.LevelItem.RowId);
        facts[id] = f;
        return f;
    }

    private static Dictionary<uint, uint> BuildCabinet()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var c in Plugin.DataManager.GetExcelSheet<CabinetRow>())
            if (c.Item.RowId != 0) map[c.Item.RowId] = c.RowId;
        return map;
    }

    /// <summary>Equippable items by look (model + slot).</summary>
    private Dictionary<(ulong, uint), List<uint>> Looks()
    {
        if (looks != null) return looks;
        looks = new();
        foreach (var r in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (r.EquipSlotCategory.RowId == 0 || r.ModelMain == 0) continue;
            var k = (r.ModelMain, r.EquipSlotCategory.RowId);
            if (!looks.TryGetValue(k, out var l)) looks[k] = l = new List<uint>();
            l.Add(r.RowId);
        }
        return looks;
    }

    /// <summary>1 learned, 2 not learned yet, 4 nothing to learn (the game's own answer); 3 = not known yet.</summary>
    public static long Unlock(uint itemId)
    {
        var ui = UIState.Instance();
        var row = ExdModule.GetItemRowById(itemId % 1_000_000);
        return ui == null || row == null ? 3 : ui->IsItemActionUnlocked(row);
    }

    private bool Obtainable(uint id, Facts f) =>
        f.Marketable || Regain.HasSource(id) || Stock.RecipeMaking(id) != null;

    private string NameOf(uint id) => Fact(id)?.Name ?? $"#{id}";

    /// <summary>The item's Armoire (Cabinet) row, 0 if it doesn't go in the Armoire.</summary>
    public uint CabinetOf(uint id) => Fact(id)?.CabinetId ?? 0;

    public (Regain.Effort Easiest, List<string> Lines) Ways(uint id)
    {
        var f = Fact(id);
        return Regain.Ways(id % 1_000_000, f?.Marketable == true, Stock.RecipeMaking(id) != null, NameOf);
    }

    public List<string> TokenRewards(uint id) => Regain.TokenRewards(id).Select(NameOf).Where(n => n.Length > 0).ToList();

    // ================================================================ analysis

    /// <summary>Items Clean-up leaves alone: crystals, materia, the Materials group, stock-list items, keep-on-me.</summary>
    private bool Skipped(Facts f, HashSet<uint> wanted)
    {
        if (Items.IsCrystal(f.Id) || config.KeepOnMe.Contains(f.Id)) return true;
        if (!config.CleanupSkipMaterials) return false;
        if (f.Category == 58 || wanted.Contains(f.Id)) return true;
        return config.GroupOf(f.Category)?.Name == Configuration.MaterialsGroup;
    }

    /// <summary>Recompute every line from the retainer snapshots. Asks Universalis for missing prices.</summary>
    public List<CleanLine> Analyze(CharacterData me)
    {
        var wanted = Stock.Wanted(config).Keys.Select(k => k % 1_000_000).ToHashSet();
        var dresser = me.Dresser.Select(i => i % 1_000_000).ToList();
        var inDresser = dresser.ToHashSet();
        var lines = new List<CleanLine>();
        var ask = new HashSet<uint>();
        verdictOf.Clear();

        foreach (var rid in me.Order)
        {
            if (!me.Retainers.TryGetValue(rid, out var snap)) continue;
            foreach (var (key, qty) in snap.Items)
            {
                if (qty <= 0 || Fact(key) is not { } f || Skipped(f, wanted)) continue;
                if (f.Marketable) ask.Add(f.Id);
                if (f.Desynth) foreach (var d in Regain.DesynthResults(f.Id)) if (!Items.IsCrystal(d)) ask.Add(d);
                var line = Judge(rid, key, qty, snap.Stacks.GetValueOrDefault(key, 1), f, me, inDresser,
                                 snap.Bound.GetValueOrDefault(key), snap.BoundKnown);
                lines.Add(line);
                verdictOf[key] = line.Verdict;
            }
        }
        // Approved items already brought to your bags keep their verdict (the run and the inn trip act on them).
        foreach (var s in Tracker.Stacks(Tracker.Bags))
            if (me.CleanupApproved.Contains(s.ItemId) && !verdictOf.ContainsKey(s.ItemId) && Fact(s.ItemId) is { } bf)
                verdictOf[s.ItemId] = Judge(0, s.ItemId, s.Qty, 1, bf, me, inDresser, Tracker.Spiritbound(s.Type, s.Slot) ? s.Qty : 0, true).Verdict;
        if (ask.Count > 0) market.Request(ask, Selling.HomeWorld(), Math.Max(10, config.PriceRefreshMinutes));

        // Approvals for things that are gone (sold, moved, discarded) lapse; HQ/NQ keys as held.
        var held = me.Retainers.Values.SelectMany(s => s.Items.Keys).ToHashSet();
        foreach (var s in Tracker.Stacks(Tracker.Bags)) held.Add(s.ItemId);
        me.CleanupApproved.RemoveWhere(k => !held.Contains(k));

        Lines = lines;
        Analyzed = DateTime.UtcNow;
        return lines;
    }

    /// <summary>
    /// [Estimate] What one desynthesis is worth: the average home-world price of the results players have
    /// reported for it (crystals left out), one of them per desynthesis. 0 if no results are known.
    /// </summary>
    public long DesynthUnitValue(uint id)
    {
        var results = Regain.DesynthResults(id).Where(r => !Items.IsCrystal(r)).ToList();
        if (results.Count == 0) return 0;
        var prices = results.Select(r => market.Get(r)?.Price ?? 0).ToList();
        return (long)(prices.Average() * MarketTax);
    }

    private CleanLine Judge(ulong rid, uint key, int qty, int stacks, Facts f, CharacterData me, HashSet<uint> inDresser, int bound, bool boundKnown)
    {
        var notes = new List<string>();
        var info = f.Marketable ? market.Get(f.Id) : null;
        var pending = f.Marketable && info == null;
        var unit = info?.Price ?? 0;
        // Spiritbound copies can't go on the market board: no market value for them.
        bound = Math.Clamp(bound, 0, qty);
        var sellable = qty - bound;
        var marketValue = (long)(unit * sellable * MarketTax);
        if (bound > 0) notes.Add(bound == qty ? "spiritbound: can't be sold on the market" : $"{bound} of {qty} spiritbound (can't be sold on the market)");
        else if (f.Marketable && f.Equip && !boundKnown) notes.Add("spiritbond unknown (open this retainer once to check)");
        var desynthUnit = f.Desynth ? DesynthUnitValue(f.Id) : 0;
        var desynthValue = desynthUnit * qty;
        if (desynthValue > 0) notes.Add($"desynth results ~{desynthValue:N0} [est.]");
        else if (f.Desynth && f.Equip) notes.Add("desynth results unknown");
        if (bound == qty && f.Marketable) pending = false; // no market price needed
        var vendorValue = f.Vendor * qty;
        var value = Math.Max(Math.Max(marketValue, vendorValue), desynthValue);
        var (easiest, ways) = Ways(f.Id);
        var unlock = f.HasAction ? Unlock(f.Id) : 4;

        var inArmoire = f.CabinetId != 0 && me.Armoire.Contains(f.Id);
        if (inDresser.Contains(f.Id)) notes.Add("already in your glamour dresser");
        if (inArmoire) notes.Add("already in your Armoire");
        if (f.CabinetId != 0 && !inArmoire && me.ArmoireSeen != null) notes.Add("fits the Armoire (free storage)");

        string why;
        var auto = Decide(out why);

        Verdict Decide(out string reason)
        {
            if (unlock == 2) { reason = "not learned yet: use it"; return Verdict.Use; }
            if (f.Card && unlock == 1) { reason = "card already in your collection"; return Verdict.CardMgp; }
            if (unlock == 1) notes.Add("already learned");

            if (f.Equip && !inDresser.Contains(f.Id) && !inArmoire)
            {
                if (f.CabinetId != 0) { reason = "goes in the Armoire (free, keeps the look)"; return Verdict.Dresser; }
                if (f.Rarity != 1 || f.Dyes > 0)
                {
                    var same = Looks().GetValueOrDefault((f.Model, f.SlotCat)) ?? new List<uint> { f.Id };
                    var unique = same.Count <= 1;
                    var gone = !same.Any(i => Fact(i) is { } o && Obtainable(i, o));
                    if (gone || (unique && !Obtainable(f.Id, f)))
                    {
                        reason = gone ? "look no longer obtainable" + (unique ? " (unique model)" : "") : "unique look, no other source";
                        if (f.Dyes > 0) reason += ", dyeable";
                        return Verdict.Dresser;
                    }
                    if (unique) notes.Add("unique look (still obtainable)");
                }
            }

            var rewards = Regain.TokenRewards(f.Id);
            if (rewards.Length > 0)
            {
                reason = $"exchange token: buys {rewards.Length} item(s)";
                if (marketValue >= config.CleanupMinSell) { reason += $"; or sells for {marketValue:N0}"; return Verdict.Market; }
                return Verdict.Exchange;
            }

            if (pending) { reason = "waiting for a price"; return Verdict.Keep; }
            if (desynthValue >= config.CleanupMinSell && desynthValue > marketValue && desynthValue > vendorValue)
            {
                reason = (bound > 0 ? "spiritbound; " : "") + $"desynthesis worth ~{desynthValue:N0} [est., average of reported results]";
                return Verdict.Desynth;
            }
            if (f.Marketable && sellable > 0 && marketValue >= config.CleanupMinSell && marketValue >= vendorValue)
            {
                reason = $"sells for ~{marketValue:N0} after tax" + (info!.UnitsPerDay < 1 ? " (slow seller)" : "");
                if (f.Equip) notes.Add("popular look? it sells: your call");
                return Verdict.Market;
            }
            if (f.Equip && f.Rarity is 2 or 3 && !f.Untradable)
            {
                reason = "green/blue gear: seals at your Grand Company (Turn-In Manager)";
                return Verdict.GrandCompany;
            }
            if (config.CleanupPreferDesynth && f.Desynth) { reason = "desynthesis (your setting)"; return Verdict.Desynth; }
            if (vendorValue > 0) { reason = $"vendor pays {vendorValue:N0}" + (f.Marketable ? $" (market ~{marketValue:N0})" : ""); return Verdict.Vendor; }
            if (f.Desynth) { reason = "no gil value: desynthesize for materials"; return Verdict.Desynth; }
            if (f.Indisposable) { reason = "can't be sold or discarded"; return Verdict.Keep; }
            reason = "no value and no use found";
            return Verdict.Discard;
        }

        var overridden = config.CleanupVerdicts.TryGetValue(f.Id, out var mine);
        var verdict = overridden ? mine : auto;
        if (verdict == Verdict.Discard && f.Indisposable) verdict = Verdict.Keep;
        if (easiest <= Regain.Effort.SmallGroup) notes.Add(easiest == Regain.Effort.Solo ? "easy to get again (solo)" : "easy to get again (small group)");
        var name = f.Name + (key >= 1_000_000 ? " (HQ)" : "");
        return new CleanLine(rid, key, name, qty, Math.Max(1, stacks), unit, value, auto, verdict, overridden, why, notes, easiest, ways, pending);
    }

    /// <summary>The verdict for a held item if you approved it (null if not approved or not analysed).</summary>
    public Verdict? ApprovedAction(uint key, CharacterData me) =>
        me.CleanupApproved.Contains(key) && verdictOf.TryGetValue(key, out var v) ? v : null;

    /// <summary>Retainers holding an approved item that needs moving.</summary>
    public List<ulong> RetainersToVisit(CharacterData me) =>
        me.Order.Where(r => me.Retainers.TryGetValue(r, out var s) &&
                            s.Items.Keys.Any(k => ApprovedAction(k, me) is { } v && v != Verdict.Keep)).ToList();

    public bool NeedsReview(CleanLine l) => l.StackValue > config.CleanupReview;

    // ================================================================ dresser / armoire

    /// <summary>The dresser's contents when it's loaded (opened), and the Armoire when loaded at an inn.</summary>
    private void Capture()
    {
        var me = tracker.Me();
        if (me == null) return;
        var changed = false;
        var mm = MirageManager.Instance();
        if (mm != null && mm->PrismBoxLoaded)
        {
            var ids = new List<uint>();
            foreach (var id in mm->PrismBoxItemIds) if (id != 0) ids.Add(id);
            if (!ids.SequenceEqual(me.Dresser)) { me.Dresser = ids; changed = true; }
            if (me.DresserSeen == null || (DateTime.UtcNow - me.DresserSeen.Value).TotalMinutes > 5) { me.DresserSeen = DateTime.UtcNow; changed = true; }
        }
        var ui = UIState.Instance();
        if (ui != null && ui->Cabinet.IsCabinetLoaded())
        {
            cabinetOf ??= BuildCabinet();
            var set = new HashSet<uint>();
            foreach (var (item, cab) in cabinetOf)
                if (ui->Cabinet.IsItemInCabinet(cab)) set.Add(item);
            if (!set.SetEquals(me.Armoire)) { me.Armoire = set; changed = true; }
            if (me.ArmoireSeen == null || (DateTime.UtcNow - me.ArmoireSeen.Value).TotalMinutes > 5) { me.ArmoireSeen = DateTime.UtcNow; changed = true; }
        }
        if (changed) tracker.Save(force: true);
    }

    /// <summary>Items in the dresser more than once (NQ and HQ counted together): name -> copies.</summary>
    public List<(uint Id, string Name, int Copies)> DresserDuplicates(CharacterData me) =>
        me.Dresser.GroupBy(i => i % 1_000_000).Where(g => g.Count() > 1)
            .Select(g => (g.Key, NameOf(g.Key), g.Count())).OrderBy(x => x.Item2).ToList();

    public static bool DresserOpen => GameUi.IsOpen("MiragePrismPrismBox");

    public void StartRestore()
    {
        if (!DresserOpen) { Status = "Open your glamour dresser first."; return; }
        Restoring = true; restored = 0; restoreWaits = 0; lastRestoredId = 0;
        Status = "Taking duplicates out of the dresser...";
    }

    private void TickRestore()
    {
        var mm = MirageManager.Instance();
        if (!DresserOpen || mm == null || !mm->PrismBoxLoaded) { StopRestore("The dresser closed."); return; }
        var ids = new List<uint>();
        foreach (var id in mm->PrismBoxItemIds) ids.Add(id);
        // Wait for the last one to leave (the list updates when the server answers).
        if (lastRestoredId != 0 && ids.Count(i => i % 1_000_000 == lastRestoredId) > 1 && ++restoreWaits < 5) return;
        restoreWaits = 0;
        var seen = new HashSet<uint>();
        for (var i = 0; i < ids.Count; i++)
        {
            var nq = ids[i] % 1_000_000;
            if (nq == 0 || seen.Add(nq)) continue;
            if (InventoryManager.Instance()->GetEmptySlotsInBag() == 0) { StopRestore("Bags full."); return; }
            if (!mm->RestorePrismBoxItem((uint)i)) { StopRestore($"The game refused to take out {NameOf(nq)} (unique already held?)."); return; }
            lastRestoredId = nq;
            restored++;
            return;
        }
        StopRestore("No duplicates left.");
    }

    private void StopRestore(string why)
    {
        Restoring = false;
        Status = $"{why} {restored} duplicate(s) taken out to your bags: sell or discard them.";
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    // ================================================================ use unlearned items

    /// <summary>Unlearned unlock items in your bags (minions, cards, orchestrion rolls...).</summary>
    public List<Tracker.Stack> Usable() =>
        Tracker.Stacks(Tracker.Bags, id => Fact(id) is { HasAction: true } && Unlock(id) == 2 && useTries.GetValueOrDefault(id % 1_000_000) < 3);

    public void StartUse()
    {
        useTries.Clear(); used = 0;
        if (Usable().Count == 0) { Status = "Nothing unlearned in your bags."; return; }
        Using = true;
        Status = "Using unlearned items...";
    }

    private void TickUse()
    {
        if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) return;
        var yn = GameUi.Get("SelectYesno");
        if (yn != null) { yn->FireCallbackInt(0); nextUse = DateTime.UtcNow.AddSeconds(1.5); return; }
        if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting] ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent]) return;
        var next = Usable().FirstOrDefault();
        if (next.ItemId == 0)
        {
            Using = false;
            Status = $"Used {used} item(s).";
            Plugin.ChatGui.Print($"[Stash] {Status}");
            return;
        }
        var nq = next.ItemId % 1_000_000;
        useTries[nq] = useTries.GetValueOrDefault(nq) + 1;
        var id = next.ItemId >= 1_000_000 ? nq + 1_000_000 : nq;
        if (ActionManager.Instance()->UseAction(ActionType.Item, id, 0xE000_0000, 65535)) used++;
        nextUse = DateTime.UtcNow.AddSeconds(3);
    }

    public void StopAll() { Using = false; Restoring = false; Desynthing = false; }

    // ================================================================ desynthesize approved items

    /// <summary>Approved "Desynthesize" stacks in your bags.</summary>
    public List<Tracker.Stack> Desynthable()
    {
        var me = tracker.Me();
        if (me == null) return new();
        return Tracker.Stacks(Tracker.Bags).Where(s => !desynthSkip.Contains(s.ItemId) && ApprovedAction(s.ItemId, me) == Verdict.Desynth).ToList();
    }

    public void StartDesynth()
    {
        desynthSkip.Clear(); desynthRefused.Clear(); desynthDone = 0; desynthCur = null; desynthTries = 0; desynthDumped = false;
        if (Desynthable().Count == 0) { Status = "Nothing approved for desynthesis in your bags."; return; }
        Desynthing = true;
        Status = "Desynthesizing...";
    }

    private void EndDesynth(string? why = null)
    {
        Desynthing = false;
        Status = why ?? $"Desynthesized {desynthDone} item(s)." + (desynthRefused.Count > 0 ? $" Not desynthesized: {string.Join("; ", desynthRefused.Take(6))}." : "");
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    /// <summary>
    /// One step: close the bell, then per item the Salvage agent's SalvageItem plus its confirmation
    /// (AutoRetainer's TaskDesynthItems), and close SalvageResult; repeat until the stack is gone.
    /// </summary>
    private void TickDesynth()
    {
        var cond = Plugin.Condition;
        if (Using || cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) return;
        // The bell first: desynthesis isn't offered while a retainer is being dealt with.
        foreach (var n in new[] { "RetainerList", "SelectString" })
            if (GameUi.Get(n) is var w && w != null) { w->Close(true); nextDesynth = DateTime.UtcNow.AddSeconds(1); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        if (cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell] ||
            cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting] ||
            cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39]) return;

        var result = GameUi.Get("SalvageResult");
        if (result != null) { result->Close(true); nextDesynth = DateTime.UtcNow.AddSeconds(0.6); return; }

        var dialog = GameUi.Get("SalvageDialog");
        if (dialog != null)
        {
            // Clicking the dialog's Desynthesize button didn't take (0.8.12): answer it through the
            // Salvage agent instead, as AutoRetainer does ([0, true]; true also covers the
            // "desynthesize unique/untradable item" box).
            if (++desynthTries > 6)
            {
                dialog->Close(true);
                if (desynthCur is { } stuck) DesynthSkip(stuck, "its Desynthesis window wouldn't confirm");
                else EndDesynth("A Desynthesis window wouldn't confirm: stopped.");
                return;
            }
            ConfirmSalvage();
            nextDesynth = DateTime.UtcNow.AddSeconds(2.5); // the desynthesis animation
            return;
        }

        var im = InventoryManager.Instance();
        if (desynthCur is { } c)
        {
            var slot = im->GetInventorySlot(c.Type, c.Slot);
            var qty = slot == null || slot->ItemId != c.Item ? 0 : (int)slot->Quantity;
            if (qty < c.Qty)
            {
                desynthDone += c.Qty - qty;
                desynthTries = 0;
                desynthCur = qty > 0 ? (c.Type, c.Slot, c.Item, qty) : null;
                return;
            }
        }

        var menu = GameUi.Get("ContextMenu");

        if (im->GetEmptySlotsInBag() < 2) { EndDesynth($"Bags nearly full: stopped desynthesizing ({desynthDone} done)."); return; }

        if (desynthCur == null)
        {
            var nextStack = Desynthable().FirstOrDefault();
            if (nextStack.ItemId == 0) { EndDesynth(); return; }
            var s = im->GetInventorySlot(nextStack.Type, nextStack.Slot);
            desynthCur = (nextStack.Type, nextStack.Slot, nextStack.ItemId % 1_000_000, s == null ? 1 : (int)s->Quantity);
            desynthTries = 0;
        }
        var cur = desynthCur.Value;
        if (!CanDesynth(cur.Item, out var cant)) { DesynthSkip(cur, cant); return; }
        if (++desynthTries > 5) { DesynthSkip(cur, "the game didn't desynthesize it (class level too low?)"); return; }
        if (menu != null) menu->Close(true);
        var item = im->GetInventorySlot(cur.Type, cur.Slot);
        if (item == null || item->ItemId == 0) { desynthCur = null; return; }
        // AutoRetainer's route: the Salvage agent's SalvageItem for the slot, then its confirmation.
        AgentSalvage.Instance()->SalvageItem(item);
        ConfirmSalvage();
        nextDesynth = DateTime.UtcNow.AddSeconds(2.5);
    }

    /// <summary>Desynthesis unlocked (quest 65688) and the item's crafter at 30+ (AutoRetainer's checks).</summary>
    public static bool CanDesynth(uint id, out string why)
    {
        why = "";
        if (!QuestManager.IsQuestComplete(65688)) { why = "desynthesis isn't unlocked"; return false; }
        var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
        if (row == null || row.Value.Desynth == 0) { why = "can't be desynthesized"; return false; }
        var job = row.Value.ClassJobRepair.ValueNullable;
        if (job == null || job.Value.ExpArrayIndex < 0) { why = "no crafter for it"; return false; }
        if (PlayerState.Instance()->ClassJobLevels[job.Value.ExpArrayIndex] < 30) { why = $"{job.Value.Abbreviation} is below 30"; return false; }
        return true;
    }

    private static void ConfirmSalvage()
    {
        var agent = AgentSalvage.Instance();
        var ret = new AtkValue();
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int; values[0].Int = 0;
        values[1].Type = AtkValueType.Bool; values[1].Byte = 1;
        agent->AgentInterface.ReceiveEvent(&ret, values, 2, 1);
    }

    private void DesynthSkip((InventoryType Type, int Slot, uint Item, int Qty) c, string why)
    {
        var slot = InventoryManager.Instance()->GetInventorySlot(c.Type, c.Slot);
        var hq = slot != null && (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        desynthSkip.Add(c.Item + (hq ? 1_000_000u : 0u));
        desynthRefused.Add($"{Items.Info(c.Item)?.Name}: {why}");
        desynthCur = null;
        desynthTries = 0;
    }

    private static uint InventoryAddon()
    {
        foreach (var n in new[] { "InventoryLarge", "Inventory", "InventoryExpansion" })
            if (GameUi.Get(n) is var a && a != null) return a->Id;
        return 0;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (Tracker.ContentId() == 0) return;
            var now = DateTime.UtcNow;
            if (now >= nextCapture) { nextCapture = now.AddSeconds(2); Capture(); }
            if (Using && now >= nextUse) { nextUse = now.AddSeconds(1); TickUse(); }
            if (Restoring && now >= nextRestore) { nextRestore = now.AddSeconds(1.5); TickRestore(); }
            if (Desynthing && now >= nextDesynth) { nextDesynth = now.AddSeconds(0.6); TickDesynth(); }
        }
        catch (Exception ex)
        {
            Using = Restoring = Desynthing = false;
            Status = "Error (see /xllog).";
            Plugin.Log.Warning(ex, "Material Stash: clean-up update failed");
        }
    }
}
