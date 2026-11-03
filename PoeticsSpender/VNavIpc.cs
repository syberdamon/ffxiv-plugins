using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace PoeticsSpender;

/// <summary>vnavmesh IPC (same calls as Map Click Nav). Every call is guarded.</summary>
public sealed class VNavIpc
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;

    public VNavIpc()
    {
        var pi = Plugin.PluginInterface;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsLoaded => Try(() => { isReady.InvokeFunc(); return true; }, false);
    public bool IsReady => Try(() => isReady.InvokeFunc(), false);
    public bool MoveTo(Vector3 dest) => Try(() => pathfindAndMoveTo.InvokeFunc(dest, false), false);
    public bool IsBusy => Try(() => pathfindInProgress.InvokeFunc(), false) || Try(() => pathIsRunning.InvokeFunc(), false);
    public void Stop() => Try(() => { pathStop.InvokeAction(); return true; }, false);

    private static T Try<T>(Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception) { return fallback; }
    }
}
