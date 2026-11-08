using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace SaucerRun.Core;

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
