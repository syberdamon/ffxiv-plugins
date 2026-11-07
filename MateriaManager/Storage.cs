using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MateriaManager.Core;

namespace MateriaManager;

/// <summary>
/// Moves materia between containers, one stack at a time (a short pause between moves, like doing
/// it by hand). Auto-store: the saddlebag window ("InventoryBuddy") or a retainer's inventory
/// opening moves every materia stack in your bags there. Saddlebag -> retainer: moves directly when
/// the game allows it; otherwise "pull" brings saddlebag materia into your bags (auto-store to the
/// saddlebag pauses), and the next retainer you open takes it.
/// </summary>
public sealed unsafe class Storage : IDisposable
{
    private readonly Configuration config;
    private DateTime nextMove = DateTime.MinValue;
    private bool saddleWasOpen, retainerWasOpen;
    private Queue<(InventoryType[] From, InventoryType[] To, string What)> jobs = new();

    /// <summary>Pulled saddlebag materia is on its way to a retainer: don't push it back.</summary>
    public bool HoldingForRetainer { get; private set; }
    public string Status { get; private set; } = "";
    public int MovedThisRun { get; private set; }

    public Storage(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static bool SaddlebagOpen => GameUi.IsOpen("InventoryBuddy") || GameUi.IsOpen("InventoryBuddy2");
    public static bool RetainerOpen => GameUi.IsOpen("InventoryRetainer") || GameUi.IsOpen("InventoryRetainerLarge");
    public bool Busy => jobs.Count > 0;

    public void StoreNow()
    {
        if (SaddlebagOpen && !HoldingForRetainer && config.StoreTo == StoreTo.Saddlebag) Queue(MateriaData.Bags, MateriaData.Saddlebag, "bags -> saddlebag");
        else if (RetainerOpen && config.StoreTo == StoreTo.Retainer || RetainerOpen && HoldingForRetainer) Queue(MateriaData.Bags, MateriaData.Retainer, "bags -> retainer");
        else Status = config.StoreTo == StoreTo.Saddlebag ? "Open your saddlebag to store." : "Open a retainer's inventory to store.";
    }

    /// <summary>Saddlebag materia to the open retainer (directly if the game allows, else via your bags).</summary>
    public void SaddlebagToRetainer()
    {
        if (RetainerOpen && MateriaData.Loaded(MateriaData.Saddlebag))
            Queue(MateriaData.Saddlebag, MateriaData.Retainer, "saddlebag -> retainer");
        else if (SaddlebagOpen)
        {
            HoldingForRetainer = true;
            Queue(MateriaData.Saddlebag, MateriaData.Bags, "saddlebag -> bags (for a retainer)");
        }
        else Status = "Open your saddlebag (to pull its materia) or a retainer (to move it directly).";
    }

    public void CancelHold() { HoldingForRetainer = false; Status = "Hold cleared."; }

    private void Queue(InventoryType[] from, InventoryType[] to, string what)
    {
        if (jobs.Any(j => j.What == what)) return;
        jobs.Enqueue((from, to, what));
        MovedThisRun = 0;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var saddle = SaddlebagOpen;
            var retainer = RetainerOpen;
            if (config.AutoStore)
            {
                if (saddle && !saddleWasOpen && config.StoreTo == StoreTo.Saddlebag && !HoldingForRetainer)
                    Queue(MateriaData.Bags, MateriaData.Saddlebag, "bags -> saddlebag");
                if (retainer && !retainerWasOpen && (config.StoreTo == StoreTo.Retainer || HoldingForRetainer))
                    Queue(MateriaData.Bags, MateriaData.Retainer, "bags -> retainer");
            }
            saddleWasOpen = saddle;
            retainerWasOpen = retainer;

            if (jobs.Count == 0 || DateTime.UtcNow < nextMove) return;
            nextMove = DateTime.UtcNow.AddMilliseconds(250);
            var job = jobs.Peek();
            var stillOpen = (job.To == MateriaData.Saddlebag || job.From == MateriaData.Saddlebag && job.To == MateriaData.Bags ? saddle : true) &&
                            (job.To == MateriaData.Retainer ? retainer : true);
            if (!stillOpen) { jobs.Dequeue(); Status = $"Stopped ({job.What}): window closed after {MovedThisRun} move(s)."; return; }

            switch (MoveOne(job.From, job.To))
            {
                case MoveResult.Moved: MovedThisRun++; Status = $"{job.What}: {MovedThisRun} stack(s) moved..."; break;
                case MoveResult.Done:
                    jobs.Dequeue();
                    Status = $"{job.What}: done, {MovedThisRun} stack(s) moved.";
                    if (job.To == MateriaData.Retainer && HoldingForRetainer) HoldingForRetainer = false;
                    if (MovedThisRun > 0) Plugin.ChatGui.Print($"[Materia] {Status}");
                    break;
                case MoveResult.Full:
                    jobs.Dequeue();
                    Status = $"{job.What}: no room left after {MovedThisRun} stack(s).";
                    Plugin.ChatGui.Print($"[Materia] {Status}");
                    break;
            }
        }
        catch (Exception ex)
        {
            jobs.Clear();
            Status = "Error (see /xllog)";
            Plugin.Log.Error(ex, "Materia storage failed");
        }
    }

    private enum MoveResult { Moved, Done, Full }

    private MoveResult MoveOne(InventoryType[] from, InventoryType[] to)
    {
        var im = InventoryManager.Instance();
        if (im == null) return MoveResult.Done;
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var srcType in from)
        {
            var src = im->GetInventoryContainer(srcType);
            if (src == null || !src->IsLoaded) continue;
            for (var i = 0; i < src->Size; i++)
            {
                var s = src->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !MateriaData.IsMateria(s->ItemId)) continue;
                var stackMax = (int)(items.GetRowOrDefault(s->ItemId)?.StackSize ?? 999);
                if (FindSlot(im, to, s->ItemId, stackMax) is not { } dst) return MoveResult.Full;
                im->MoveItemSlot(srcType, (ushort)i, dst.Type, (ushort)dst.Slot, true);
                return MoveResult.Moved;
            }
        }
        return MoveResult.Done;
    }

    /// <summary>A stack of the same item with room, else an empty slot.</summary>
    private static (InventoryType Type, int Slot)? FindSlot(InventoryManager* im, InventoryType[] to, uint itemId, int stackMax)
    {
        (InventoryType, int)? empty = null;
        foreach (var t in to)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null) continue;
                if (s->ItemId == itemId && s->Quantity < stackMax) return (t, i);
                if (s->ItemId == 0 && empty == null) empty = (t, i);
            }
        }
        return empty;
    }
}
