using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace MapClickNav;

/// <summary>
/// Thin wrapper over vnavmesh's IPC (names verified against awgil/ffxiv_navmesh IPCProvider.cs).
/// Every call is guarded: if vnavmesh isn't installed or loaded, calls return false/null.
/// </summary>
public sealed class VNavIpc
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<float> buildProgress;
    private readonly ICallGateSubscriber<Vector3?> flagToPoint;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;

    public VNavIpc()
    {
        var pi = Plugin.PluginInterface;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        buildProgress = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        flagToPoint = pi.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    }

    /// <summary>True when vnavmesh is loaded and has a navmesh for the current zone.</summary>
    public bool IsReady => Try(() => isReady.InvokeFunc(), false);

    /// <summary>True when vnavmesh is installed and answering IPC calls.</summary>
    public bool IsLoaded => Try(() => { isReady.InvokeFunc(); return true; }, false);

    /// <summary>True while vnavmesh is building (or loading) the current zone's mesh.</summary>
    public bool IsBuilding => Try(() => buildProgress.InvokeFunc(), -1f) >= 0;

    /// <summary>Closest walkable point to <paramref name="p"/> within the box, or null.</summary>
    public Vector3? NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY) =>
        Try(() => nearestPoint.InvokeFunc(p, halfExtentXZ, halfExtentY), null);

    public Vector3? FlagToPoint() => Try(() => flagToPoint.InvokeFunc(), null);

    public bool MoveTo(Vector3 dest, bool fly) => Try(() => pathfindAndMoveTo.InvokeFunc(dest, fly), false);

    /// <summary>True while vnavmesh is still computing the path or walking/flying it.</summary>
    public bool IsBusy => Try(() => pathfindInProgress.InvokeFunc(), false) || Try(() => pathIsRunning.InvokeFunc(), false);

    public void Stop() => Try(() => { pathStop.InvokeAction(); return true; }, false);

    private static T Try<T>(Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception) { return fallback; } // vnavmesh not installed / not loaded yet
    }
}
