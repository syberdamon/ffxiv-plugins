using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MateriaManager.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MateriaManager;

/// <summary>Five materia handed in together.</summary>
public sealed record Batch(int Grade, List<MateriaInfo> Items);

/// <summary>
/// Plans transmutation batches from your bags (fodder types only, never below each Keep), and takes
/// you to Mutamix Bubblypots (Central Thanalan): teleport, walk, talk, pick the transmute option.
/// The transmute window itself isn't automated yet: when it opens, its layout is dumped (to
/// pluginConfigs/MateriaManager/dumps) so the hand-in can be automated, and you hand in the planned
/// batches yourself.
/// </summary>
public sealed unsafe class Transmuter : IDisposable
{
    public const string Npc = "Mutamix Bubblypots";
    private const string Aetheryte = "Black Brush Station";

    public enum Phase { Idle, Teleporting, Walking, Talking, WindowOpen, Done }

    private readonly Configuration config;
    private readonly VNavIpc vnav = new();
    private readonly LifestreamIpc lifestream = new();
    private DateTime nextAction = DateTime.MinValue, phaseStarted, lastProgress, talkedAt = DateTime.MinValue;
    private float bestDistance = float.MaxValue;
    private int tries;
    private HashSet<string> visibleBefore = new();

    public Phase State { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => State is Phase.Teleporting or Phase.Walking or Phase.Talking;

    public Transmuter(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    // ================================================================ planning

    /// <summary>Batches of five from your bags, by grade, spending the biggest surpluses first.</summary>
    public List<Batch> Plan()
    {
        var bags = MateriaData.Count(MateriaData.Bags);
        var all = MateriaData.All();
        var spare = new List<(MateriaInfo Info, int Count)>();
        foreach (var (id, count) in bags)
        {
            if (!config.Rules.TryGetValue(id, out var rule) || !rule.Fodder) continue;
            var n = count - Math.Max(0, rule.Keep);
            if (n > 0) spare.Add((all[id], n));
        }
        var batches = new List<Batch>();
        var groups = config.SameGradeBatches
            ? spare.GroupBy(s => s.Info.Grade).Select(g => (Grade: g.Key, Items: g.ToList()))
            : new[] { (Grade: 0, Items: spare) };
        foreach (var (grade, items) in groups.OrderByDescending(g => g.Grade))
        {
            // Expand to single pieces, biggest surplus first, so each batch mixes types where it can.
            var pool = items.OrderByDescending(i => i.Count).ToList();
            var pieces = new List<MateriaInfo>();
            var left = pool.Select(p => p.Count).ToArray();
            while (left.Any(n => n > 0))
                for (var i = 0; i < pool.Count; i++)
                    if (left[i] > 0) { pieces.Add(pool[i].Info); left[i]--; }
            for (var i = 0; i + 5 <= pieces.Count; i += 5)
                batches.Add(new Batch(grade == 0 ? pieces[i].Grade : grade, pieces.GetRange(i, 5)));
        }
        return batches;
    }

    // ================================================================ going there

    public void Start()
    {
        if (Busy) return;
        tries = 0; talkedAt = DateTime.MinValue; bestDistance = float.MaxValue;
        Set(FindNpc() != null || NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType) != null ? Phase.Walking : Phase.Teleporting,
            "Heading to Mutamix Bubblypots...");
        lastProgress = DateTime.UtcNow;
        if (State == Phase.Teleporting) Teleport();
    }

    public void Stop() { vnav.Stop(); Set(Phase.Idle, "Stopped."); }

    private void Set(Phase p, string status) { State = p; Status = status; phaseStarted = DateTime.UtcNow; }

    private void Fail(string why) { vnav.Stop(); Set(Phase.Idle, why); Plugin.ChatGui.PrintError($"[Materia] {why}"); }

    private void Teleport()
    {
        if (config.UseLifestream && lifestream.IsLoaded && lifestream.Execute(Aetheryte)) return;
        var row = Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English)
            .FirstOrDefault(a => a.IsAetheryte && a.PlaceName.ValueNullable?.Name.ExtractText() == Aetheryte);
        if (row.RowId == 0 || Telepo.Instance() == null || !Telepo.Instance()->Teleport(row.RowId, 0))
            Fail($"Couldn't teleport to {Aetheryte}.");
    }

    private IGameObject? FindNpc() =>
        Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventNpc && o.Name.TextValue.Equals(Npc, StringComparison.OrdinalIgnoreCase));

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < nextAction) return;
        try
        {
            switch (State)
            {
                case Phase.Teleporting: TickTeleport(); break;
                case Phase.Walking: TickWalk(); break;
                case Phase.Talking: TickTalk(); break;
            }
        }
        catch (Exception ex) { Fail("Error: " + ex.Message); Plugin.Log.Error(ex, "Transmuter failed"); }
    }

    private void TickTeleport()
    {
        nextAction = DateTime.UtcNow.AddSeconds(1);
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.Casting] || lifestream.IsBusy) return;
        if (NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType) != null) { Set(Phase.Walking, "Walking to Mutamix..."); lastProgress = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 30) Fail("Teleport didn't arrive in Central Thanalan.");
    }

    private void TickWalk()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || !vnav.IsReady) return;
        var npc = FindNpc();
        if (npc != null && Vector3.Distance(me.Position, npc.Position) <= 4.5f) { vnav.Stop(); Set(Phase.Talking, "Talking to Mutamix..."); return; }
        var goal = npc?.Position ?? NpcLocator.InTerritory(Npc, Plugin.ClientState.TerritoryType);
        if (goal == null) { Fail("Can't find Mutamix here."); return; }
        var target = vnav.NearestPoint(goal.Value, 6, 6) ?? goal.Value;
        var dist = Vector3.Distance(me.Position, target);
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 10) { Fail("vnavmesh isn't getting closer to Mutamix."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(target, 1f);
    }

    private void TickTalk()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        var menu = GameUi.Get("SelectString") ?? GameUi.Get("SelectIconString");
        if (menu != null && talkedAt != DateTime.MinValue)
        {
            var icon = GameUi.IsOpen("SelectIconString") && GameUi.Get("SelectString") == null;
            var rows = GameUi.MenuEntries(menu, icon);
            var row = rows.FindIndex(r => r.Contains("transmut", StringComparison.OrdinalIgnoreCase) || r.Contains("Materia", StringComparison.OrdinalIgnoreCase));
            if (row < 0) { GameUi.Dump(icon ? "SelectIconString" : "SelectString"); Fail("No transmute option in Mutamix's menu (dump saved): " + string.Join(" | ", rows)); return; }
            menu->FireCallbackInt(row);
            nextAction = DateTime.UtcNow.AddSeconds(1.5);
            return;
        }

        // After talking: a new window that isn't the menu/dialogue is the transmute window. Dump it.
        if (talkedAt != DateTime.MinValue && (DateTime.UtcNow - talkedAt).TotalSeconds > 1.5)
        {
            var opened = VisibleAddons().Except(visibleBefore).Where(n => n is not ("Talk" or "SelectString" or "SelectIconString")).ToList();
            if (opened.Count > 0)
            {
                foreach (var name in opened) GameUi.Dump(name, "opened after talking to Mutamix");
                Set(Phase.WindowOpen, $"Transmute window open ({string.Join(", ", opened)}). Hand in the planned batches; layout saved for automating next version.");
                Plugin.ChatGui.Print($"[Materia] {Status}");
                return;
            }
            if ((DateTime.UtcNow - talkedAt).TotalSeconds > 8) { Fail("Talked to Mutamix but no transmute window appeared."); return; }
            return;
        }

        if (++tries > 3) { Fail("Couldn't talk to Mutamix."); return; }
        var npc = FindNpc();
        if (npc == null) { Fail("Lost sight of Mutamix."); return; }
        visibleBefore = VisibleAddons();
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(1.5);
    }

    /// <summary>Names of every visible game window (to spot the one that opened).</summary>
    private static HashSet<string> VisibleAddons()
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
}
