using System;
using System.Collections.Generic;
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
