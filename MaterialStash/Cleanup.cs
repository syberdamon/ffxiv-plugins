using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using MaterialStash.Core;
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
                var line = Judge(rid, key, qty, snap.Stacks.GetValueOrDefault(key, 1), f, me, inDresser);
                lines.Add(line);
                verdictOf[key] = line.Verdict;
            }
        }
        // Approved items already brought to your bags keep their verdict (the run and the inn trip act on them).
        foreach (var s in Tracker.Stacks(Tracker.Bags))
            if (me.CleanupApproved.Contains(s.ItemId) && !verdictOf.ContainsKey(s.ItemId) && Fact(s.ItemId) is { } bf)
                verdictOf[s.ItemId] = Judge(0, s.ItemId, s.Qty, 1, bf, me, inDresser).Verdict;
        if (ask.Count > 0) market.Request(ask, Selling.HomeWorld(), Math.Max(10, config.PriceRefreshMinutes));

        // Approvals for things that are gone (sold, moved, discarded) lapse; HQ/NQ keys as held.
        var held = me.Retainers.Values.SelectMany(s => s.Items.Keys).ToHashSet();
        foreach (var s in Tracker.Stacks(Tracker.Bags)) held.Add(s.ItemId);
        me.CleanupApproved.RemoveWhere(k => !held.Contains(k));

        Lines = lines;
        Analyzed = DateTime.UtcNow;
        return lines;
    }

    private CleanLine Judge(ulong rid, uint key, int qty, int stacks, Facts f, CharacterData me, HashSet<uint> inDresser)
    {
        var notes = new List<string>();
        var info = f.Marketable ? market.Get(f.Id) : null;
        var pending = f.Marketable && info == null;
        var unit = info?.Price ?? 0;
        var marketValue = (long)(unit * qty * MarketTax);
        var vendorValue = f.Vendor * qty;
        var value = Math.Max(marketValue, vendorValue);
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
            if (f.Marketable && marketValue >= config.CleanupMinSell && marketValue >= vendorValue)
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

    public void StopAll() { Using = false; Restoring = false; }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (Tracker.ContentId() == 0) return;
            var now = DateTime.UtcNow;
            if (now >= nextCapture) { nextCapture = now.AddSeconds(2); Capture(); }
            if (Using && now >= nextUse) { nextUse = now.AddSeconds(1); TickUse(); }
            if (Restoring && now >= nextRestore) { nextRestore = now.AddSeconds(1.5); TickRestore(); }
        }
        catch (Exception ex)
        {
            Using = Restoring = false;
            Status = "Error (see /xllog).";
            Plugin.Log.Warning(ex, "Material Stash: clean-up update failed");
        }
    }
}
