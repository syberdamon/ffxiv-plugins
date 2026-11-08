using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace MaterialStash.Core;

/// <summary>AutoRetainer: only asked whether it's busy, so a stash run never starts in the middle of its run.</summary>
public sealed class AutoRetainerIpc
{
    private readonly ICallGateSubscriber<bool> isBusy;

    public AutoRetainerIpc()
    {
        isBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
    }

    public bool IsLoaded { get { try { isBusy.InvokeFunc(); return true; } catch (Exception) { return false; } } }
    public bool IsBusy { get { try { return isBusy.InvokeFunc(); } catch (Exception) { return false; } } }
}

/// <summary>
/// Allagan Tools keeps a cache of every retainer's items. Used only to seed what we know about a
/// retainer you haven't opened since installing this plugin. Every call guarded.
/// Unverified: GetCharacterItems returns one entry per stack as [itemId, flags, ownerId, quantity]
/// (flags bit 1 = HQ), for the retainer's whole inventory.
/// </summary>
public sealed class AllaganIpc
{
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> getCharacterItems;

    public AllaganIpc()
    {
        var pi = Plugin.PluginInterface;
        isInitialized = pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        getCharacterItems = pi.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
    }

    public bool IsReady { get { try { return isInitialized.InvokeFunc(); } catch (Exception) { return false; } } }

    /// <summary>(itemId incl. HQ offset, quantity) per stack, or null if Allagan Tools isn't there / doesn't answer.</summary>
    public List<(uint ItemId, int Qty)>? Items(ulong ownerId)
    {
        try
        {
            var set = getCharacterItems.InvokeFunc(ownerId);
            if (set == null) return null;
            var list = new List<(uint, int)>();
            foreach (var e in set)
            {
                if (e == null || e.Length < 4 || e[0] == 0) continue;
                var id = (uint)e[0];
                if ((e[1] & 1) != 0 && id < 1_000_000) id += 1_000_000;
                list.Add((id, (int)e[3]));
            }
            return list;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Allagan Tools GetCharacterItems failed");
            return null;
        }
    }
}


/// <summary>vnavmesh (walking). Every call guarded: not installed = false/null.</summary>
public sealed class VNavIpc
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;

    public VNavIpc()
    {
        var pi = Plugin.PluginInterface;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsLoaded => Try(() => { isReady.InvokeFunc(); return true; }, false);
    public bool IsReady => Try(() => isReady.InvokeFunc(), false);
    public bool MoveCloseTo(Vector3 dest, float range) => Try(() => moveCloseTo.InvokeFunc(dest, false, range), false);
    public Vector3? NearestPoint(Vector3 p, float xz, float y) => Try(() => nearestPoint.InvokeFunc(p, xz, y), null);
    public bool IsBusy => Try(() => pathfindInProgress.InvokeFunc(), false) || Try(() => pathIsRunning.InvokeFunc(), false);
    public void Stop() => Try(() => { pathStop.InvokeAction(); return true; }, false);

    private static T Try<T>(Func<T> call, T fallback)
    {
        try { return call(); } catch (Exception) { return fallback; }
    }
}

/// <summary>Lifestream (travel: "/li inn" goes to an inn room). Optional.</summary>
public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;

    public LifestreamIpc()
    {
        var pi = Plugin.PluginInterface;
        executeCommand = pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool IsLoaded { get { try { isBusy.InvokeFunc(); return true; } catch (Exception) { return false; } } }
    public bool IsBusy { get { try { return isBusy.InvokeFunc(); } catch (Exception) { return false; } } }
    public bool Execute(string args) { try { executeCommand.InvokeAction(args); return true; } catch (Exception) { return false; } }
    public void Abort() { try { abort.InvokeAction(); } catch (Exception) { } }
}
