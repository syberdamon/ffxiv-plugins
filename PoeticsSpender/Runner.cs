using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace PoeticsSpender;

public enum Phase
{
    Idle,
    Teleporting,
    WaitingForMesh,
    Walking,
    Interacting,
    Menu,
    AwaitingConfirm,
    Buying,
    Done,
}

/// <summary>
/// One run: teleport to Idyllshire, buy tokens from Hismena with all Poetics, trade them to
/// Bertana for topsoil. A per-frame state machine; any surprise stops the run and says why.
///
/// Safety before anything is spent:
///  - the shop row is found by item name and, the first time a row is used, shown to you to
///    confirm (it's remembered afterwards);
///  - a Yes/No that doesn't mention the item is answered No and the run stops;
///  - the first purchase is checked against your inventory count.
/// </summary>
public sealed unsafe class Runner : IDisposable
{
    private sealed record Vendor(string NpcName, string[] MenuKeywords, string ShopAddon);

    // [Assumption] menu texts (English client). If a menu has none of these, the run stops and
    // prints the menu rows so the keywords can be fixed.
    private static readonly Vendor Hismena = new("Hismena", new[] { "Special Arms", "Poetics" }, "ShopExchangeCurrency");
    private static readonly Vendor Bertana = new("Bertana", new[] { "Uncanny Knickknacks", "Knickknacks" }, "ShopExchangeItem");

    private const float InteractRange = 4.5f;

    private readonly Configuration config;
    private readonly VNavIpc vnav;

    public Phase Phase { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => Phase is not (Phase.Idle or Phase.Done);

    /// <summary>Set while waiting for the user to confirm a shop row.</summary>
    public string? ConfirmText { get; private set; }

    private SoilOption soil = Soils.Options[0];
    private int step;               // 0 = Hismena (Poetics -> token), 1 = Bertana (token -> soil)
    private int quantity;           // how many to buy at the current vendor
    private int bought;             // tokens bought this run
    private uint buyItemId;
    private string buyItemName = "";
    private int shopRow;
    private string confirmKey = "";
    private int countBefore;
    private bool yesNoAnswered;
    private int interactTries;
    private DateTime phaseStarted;
    private DateTime nextAction;
    private bool sawZoneLoad;
    private DateTime loadedAt;

    private Vendor CurrentVendor => step == 0 ? Hismena : Bertana;

    public Runner(Configuration config, VNavIpc vnav)
    {
        this.config = config;
        this.vnav = vnav;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) vnav.Stop();
    }

    // ================================================================ start / stop

    /// <summary>Why a run can't start right now, or null if it can.</summary>
    public string? Blocker()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) return "Not logged in.";
        if (Busy) return "Already running.";
        if (Plugin.Condition[ConditionFlag.InCombat]) return "In combat.";
        if (Plugin.Condition[ConditionFlag.BoundByDuty]) return "In a duty.";
        var poetics = Soils.Poetics();
        if (poetics < Soils.PoeticsPrice) return $"You have {poetics} Poetics; you need at least {Soils.PoeticsPrice} for one topsoil.";
        if (Soils.FreeSlots() < 2) return "You need at least 2 free inventory slots.";
        if (!vnav.IsLoaded) return "vnavmesh isn't installed or loaded.";
        var s = Soils.Options[Math.Clamp(config.SoilIndex, 0, Soils.Options.Count - 1)];
        if (Soils.ItemId(s.SoilName) == 0 || Soils.ItemId(s.TokenName) == 0) return "Couldn't find the items in game data.";
        return null;
    }

    public void Start()
    {
        if (Blocker() is { } why) { Notify(why); return; }

        soil = Soils.Options[Math.Clamp(config.SoilIndex, 0, Soils.Options.Count - 1)];
        step = 0;
        bought = 0;
        quantity = Soils.Affordable();
        Notify($"Buying {quantity} x {soil.SoilName} with {quantity * Soils.PoeticsPrice} Poetics.");

        if (Plugin.ClientState.TerritoryType == Soils.IdyllshireTerritory) { EnterWaitingForMesh(); return; }

        if (!Telepo.Instance()->Teleport(Soils.IdyllshireAetheryte, 0)) { Fail("Couldn't teleport to Idyllshire."); return; }
        Set(Phase.Teleporting, "Teleporting to Idyllshire...");
        sawZoneLoad = false;
        loadedAt = DateTime.MinValue;
    }

    public void Stop(string why = "Stopped.")
    {
        vnav.Stop();
        ConfirmText = null;
        Set(Phase.Idle, why);
    }

    public void Confirm()
    {
        if (Phase != Phase.AwaitingConfirm) return;
        config.ConfirmedRows.Add(confirmKey);
        config.Save();
        ConfirmText = null;
        Buy();
    }

    // ================================================================ per frame

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || Phase == Phase.AwaitingConfirm) return;
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat]) { Fail("Combat started."); return; }
            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > TimeoutFor(Phase)) { Fail($"Timed out ({Status})"); return; }
            if (DateTime.UtcNow < nextAction) return;

            switch (Phase)
            {
                case Phase.Teleporting: TickTeleporting(); break;
                case Phase.WaitingForMesh: TickWaitingForMesh(); break;
                case Phase.Walking: TickWalking(); break;
                case Phase.Interacting: TickInteracting(); break;
                case Phase.Menu: TickMenu(); break;
                case Phase.Buying: TickBuying(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Poetics Spender tick failed");
            Fail("Something went wrong (see /xllog).");
        }
    }

    private static double TimeoutFor(Phase p) => p switch
    {
        Phase.Teleporting => 90,
        Phase.WaitingForMesh => 90,
        Phase.Walking => 120,
        _ => 15,
    };

    private void TickTeleporting()
    {
        var betweenAreas = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (betweenAreas) sawZoneLoad = true;
        if (!sawZoneLoad && (DateTime.UtcNow - phaseStarted).TotalSeconds > 12) { Fail("Teleport didn't go through."); return; }

        var loaded = sawZoneLoad && !betweenAreas && Plugin.ObjectTable.LocalPlayer != null &&
                     GameMain.Instance()->TerritoryLoadState == 2 &&
                     Plugin.ClientState.TerritoryType == Soils.IdyllshireTerritory;
        if (!loaded) { loadedAt = DateTime.MinValue; return; }
        if (loadedAt == DateTime.MinValue) { loadedAt = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - loadedAt).TotalSeconds < 1.5) return; // let the area settle
        EnterWaitingForMesh();
    }

    private void EnterWaitingForMesh() => Set(Phase.WaitingForMesh, "Waiting for vnavmesh...");

    private void TickWaitingForMesh()
    {
        if (!vnav.IsReady) return;
        EnterWalking();
    }

    private void EnterWalking()
    {
        Set(Phase.Walking, $"Walking to {CurrentVendor.NpcName}...");
        interactTries = 0;
        bestDistance = float.MaxValue;
        lastProgress = DateTime.UtcNow;
        walkTarget = default;
    }

    private IGameObject? FindNpc() =>
        Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventNpc &&
                                               o.Name.TextValue.Equals(CurrentVendor.NpcName, StringComparison.OrdinalIgnoreCase));

    private static void LogNearbyNpcs()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        foreach (var o in Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventNpc)
                     .OrderBy(o => Vector3.Distance(o.Position, player.Position)).Take(15))
            Plugin.Log.Info($"nearby npc: '{o.Name.TextValue}' at {Vector3.Distance(o.Position, player.Position):0.0}y");
    }

    private Vector3 walkTarget;
    private float bestDistance;
    private DateTime lastProgress;

    private void TickWalking()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        var npc = FindNpc();

        // Close enough to talk (the NPC may stand behind a counter, off the walkable mesh).
        if (npc != null && Vector3.Distance(player.Position, npc.Position) <= InteractRange)
        {
            ArriveAtNpc();
            return;
        }

        // Where to walk: the NPC if loaded, else where game data says they stand
        // (NPCs only load when you're fairly near). Snapped to walkable ground.
        Vector3 goal;
        if (npc != null) goal = npc.Position;
        else if (NpcLocator.Find(CurrentVendor.NpcName, Soils.IdyllshireTerritory, player.Position.Y) is { } spot) goal = spot;
        else { Fail($"Don't know where {CurrentVendor.NpcName} is."); return; }
        var target = vnav.NearestPoint(goal, 6f, 6f) ?? goal;
        Status = npc != null ? $"Walking to {CurrentVendor.NpcName}..." : $"Walking to {CurrentVendor.NpcName}'s spot...";

        var toTarget = Vector3.Distance(player.Position, target);
        if (toTarget <= 1.5f)
        {
            // At the closest walkable point. Try talking if they're roughly in reach.
            if (npc != null && Vector3.Distance(player.Position, npc.Position) <= 8f) { ArriveAtNpc(); return; }
            if (npc == null)
            {
                LogNearbyNpcs();
                Fail($"Reached {CurrentVendor.NpcName}'s spot but can't see them (nearby NPC names are in /xllog).");
                return;
            }
        }

        // Progress check: vnavmesh silently does nothing if it can't find a path.
        if (toTarget < bestDistance - 0.5f) { bestDistance = toTarget; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 8)
        {
            Fail($"vnavmesh isn't getting closer to {CurrentVendor.NpcName} ({toTarget:0} yalms away).");
            return;
        }

        if (!vnav.IsBusy || Vector3.Distance(walkTarget, target) > 2f)
        {
            walkTarget = target;
            vnav.MoveCloseTo(target, 1f);
        }
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
    }

    private void ArriveAtNpc()
    {
        vnav.Stop();
        Set(Phase.Interacting, $"Talking to {CurrentVendor.NpcName}...");
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
    }

    private void TickInteracting()
    {
        if (MenuOrShopOpen()) { Set(Phase.Menu, Status); return; }
        if (GameUi.IsOpen("Talk")) { Fail($"{CurrentVendor.NpcName} started talking instead of opening a menu (not handled yet)."); return; }
        if (interactTries >= 3) { Fail($"{CurrentVendor.NpcName} didn't open a menu."); return; }

        var npc = FindNpc();
        if (npc == null) { Fail($"Lost sight of {CurrentVendor.NpcName}."); return; }
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        interactTries++;
        nextAction = DateTime.UtcNow.AddSeconds(2);
    }

    private bool MenuOrShopOpen() =>
        GameUi.IsOpen("SelectString") || GameUi.IsOpen("SelectIconString") || GameUi.IsOpen(CurrentVendor.ShopAddon);

    private void TickMenu()
    {
        var shop = GameUi.Get(CurrentVendor.ShopAddon);
        if (shop != null) { Detect(shop); return; }

        var icon = GameUi.Get("SelectIconString");
        var plain = GameUi.Get("SelectString");
        var menu = icon != null ? icon : plain;
        if (menu == null) return; // between menus

        var rows = GameUi.MenuEntries(menu, icon != null);
        var row = -1;
        foreach (var keyword in CurrentVendor.MenuKeywords)
        {
            row = rows.FindIndex(r => r.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (row >= 0) break;
        }
        if (row < 0) { Fail($"{CurrentVendor.NpcName}'s menu has none of [{string.Join(", ", CurrentVendor.MenuKeywords)}]: {string.Join(" | ", rows)}"); return; }

        Plugin.Log.Info($"{CurrentVendor.NpcName}: choosing '{rows[row]}' (row {row})");
        GameUi.ClickRow(menu, row);
        nextAction = DateTime.UtcNow.AddSeconds(1);
        phaseStarted = DateTime.UtcNow;
    }

    private void Detect(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* shop)
    {
        buyItemId = Soils.ItemId(step == 0 ? soil.TokenName : soil.SoilName);
        buyItemName = Soils.LocalName(buyItemId);
        if (step == 1) quantity = Math.Min(bought, Soils.Count(Soils.ItemId(soil.TokenName)));
        if (quantity <= 0) { Finish(); return; }

        shopRow = GameUi.FindShopRow(shop, buyItemName, out var evidence);
        Plugin.Log.Info($"{CurrentVendor.ShopAddon} rows near {buyItemName}:\n{evidence}");
        if (shopRow < 0) { GameUi.Dump(CurrentVendor.ShopAddon); Fail($"{buyItemName} isn't in {CurrentVendor.NpcName}'s shop."); return; }

        confirmKey = $"{CurrentVendor.NpcName}:{shopRow}:{buyItemName}";
        if (config.ConfirmedRows.Contains(confirmKey)) { Buy(); return; }

        ConfirmText = $"{CurrentVendor.NpcName}: buy {quantity} x {buyItemName}?\n" +
                      $"Detected as shop row {shopRow} (first time; check it's the right line):\n{evidence}";
        Set(Phase.AwaitingConfirm, "Waiting for you to confirm the purchase...");
    }

    private void Buy()
    {
        var shop = GameUi.Get(CurrentVendor.ShopAddon);
        if (shop == null) { Fail("The shop closed."); return; }

        countBefore = Soils.Count(buyItemId);
        yesNoAnswered = false;
        Plugin.Log.Info($"Buying {quantity} x {buyItemName} (row {shopRow}) from {CurrentVendor.NpcName}; have {countBefore}");
        GameUi.FireInts(shop, 0, shopRow, quantity);
        Set(Phase.Buying, $"Buying {quantity} x {buyItemName}...");
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
    }

    private void TickBuying()
    {
        var have = Soils.Count(buyItemId);
        if (have >= countBefore + quantity) { Bought(have - countBefore); return; }

        var yesNo = GameUi.Get("SelectYesno");
        if (yesNo != null && !yesNoAnswered)
        {
            var text = GameUi.YesNoText();
            yesNoAnswered = true;
            if (ConfirmationLooksRight(text))
            {
                GameUi.ClickRow(yesNo, 0); // Yes
                nextAction = DateTime.UtcNow.AddSeconds(0.5);
            }
            else
            {
                GameUi.ClickRow(yesNo, 1); // No
                Fail($"Unexpected confirmation, answered No: \"{text}\"");
            }
            return;
        }

        foreach (var dialog in new[] { "ShopExchangeCurrencyDialog", "ShopExchangeItemDialog", "InputNumeric" })
        {
            if (!GameUi.IsOpen(dialog)) continue;
            GameUi.Dump(dialog);
            Fail($"The game opened '{dialog}', which isn't handled yet. Nothing more was bought; details in /xllog.");
            return;
        }

        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 6)
        {
            var got = have - countBefore;
            if (got > 0) { Bought(got); return; }
            GameUi.Dump(CurrentVendor.ShopAddon);
            Fail($"The purchase didn't go through (no {buyItemName} added). Details in /xllog.");
        }
    }

    /// <summary>
    /// The exchange Yes/No reads "Exchange 1,950 Allagan tomestones of poetics for the following
    /// item?" with the item only as an icon. Accept it when it names the item, or when it names
    /// the currency we're paying with and the exact expected cost (price x quantity).
    /// </summary>
    private bool ConfirmationLooksRight(string text)
    {
        if (text.Contains(buyItemName, StringComparison.OrdinalIgnoreCase)) return true;
        var (currency, cost) = step == 0
            ? (Soils.LocalName(Soils.PoeticsItemId), (long)quantity * Soils.PoeticsPrice)
            : (Soils.LocalName(Soils.ItemId(soil.TokenName)), (long)quantity);
        var currencyWord = currency.Split(' ').Last(); // "poetics" / "Ore": tolerant of plural/case
        return text.Contains(currencyWord, StringComparison.OrdinalIgnoreCase) && GameUi.Numbers(text).Contains(cost);
    }

    private void Bought(int got)
    {
        Plugin.Log.Info($"Got {got} x {buyItemName}");
        if (step == 0) bought = got;
        GameUi.Close(CurrentVendor.ShopAddon);

        if (step == 0)
        {
            if (got < quantity) Notify($"Only got {got} of {quantity} {buyItemName}.");
            step = 1;
            EnterWalking();
            nextAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        Finish(got);
    }

    private void Finish(int soils = 0)
    {
        var msg = soils > 0
            ? $"Done: +{soils} {Soils.LocalName(Soils.ItemId(soil.SoilName))}. {Soils.Poetics()} Poetics left."
            : "Done.";
        Notify(msg);
        Set(Phase.Done, msg);
    }

    // ================================================================ helpers

    private void Set(Phase phase, string status)
    {
        Phase = phase;
        Status = status;
        phaseStarted = DateTime.UtcNow;
    }

    private void Fail(string why)
    {
        vnav.Stop();
        Notify(why);
        Set(Phase.Idle, why);
        ConfirmText = null;
    }

    private static void Notify(string msg) => Plugin.ChatGui.Print($"[Poetics] {msg}");
}
