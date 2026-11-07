using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace RaiseCallouts;

/// <summary>
/// Hard-cast raises are announced from your cast bar (a short delay in, so an instant cancel says
/// nothing). Instant raises (Swiftcast, Dualcast) have no cast bar, so they're announced when the
/// spell lands, unless that target was just announced. Either way: once per raise.
/// </summary>
public sealed unsafe class Announcer : IDisposable
{
    // Raise, Resurrection, Ascend, Verraise, Egeiro, Angel Whisper (BLU).
    private static readonly Dictionary<uint, string> Raises = new()
    {
        [125] = "Raise", [173] = "Resurrection", [3603] = "Ascend", [7523] = "Verraise", [24287] = "Egeiro", [18317] = "Angel Whisper",
    };

    // [Assumption] Status 148 = "Raise" (resurrection pending) on the dead player.
    private const uint RaisePendingStatus = 148;

    private readonly Configuration config;
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly Dictionary<ulong, DateTime> announced = new();   // target -> when
    private DateTime castSeenAt = DateTime.MinValue;
    private (uint Action, ulong Target) castKey;
    private bool castAnnounced;

    public Announcer(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.MemberFunctionPointers.Receive, OnActionEffect);
            hook.Enable();
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "RaiseCallouts: couldn't hook action effects (instant raises won't be announced)"); }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        hook?.Dispose();
    }

    public static string Format(string template, string target, string spell, bool instant) =>
        template.Replace("{target}", target).Replace("{spell}", spell).Replace("{how}", instant ? " (instant)" : "");

    // ---- hard casts
    private void OnUpdate(IFramework framework)
    {
        try
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            if (me == null || !me.IsCasting || !Raises.TryGetValue(me.CastActionId, out var spell))
            {
                castKey = default; castAnnounced = false;
                return;
            }
            var key = (me.CastActionId, me.CastTargetObjectId);
            if (key != castKey) { castKey = key; castSeenAt = DateTime.UtcNow; castAnnounced = false; }
            if (castAnnounced || (DateTime.UtcNow - castSeenAt).TotalMilliseconds < config.CastDelayMs) return;
            castAnnounced = true;
            if (Plugin.ObjectTable.SearchById(me.CastTargetObjectId) is not { } target) return;
            Announce(target, spell, instant: false);
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "RaiseCallouts: update failed"); }
    }

    // ---- instant raises
    private void OnActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        try
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            if (me == null || header == null || casterEntityId != me.EntityId || header->NumTargets == 0) return;
            if (!Raises.TryGetValue(header->ActionId, out var spell)) return;
            var targetId = (uint)targetEntityIds[0].ObjectId;
            if (Plugin.ObjectTable.SearchByEntityId(targetId) is not { } target) return;
            if (announced.TryGetValue(target.GameObjectId, out var at) && (DateTime.UtcNow - at).TotalSeconds < 15) return; // hard cast already called
            Announce(target, spell, instant: true, landed: true);
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "RaiseCallouts: action effect failed"); }
    }

    private void Announce(IGameObject target, string spell, bool instant, bool landed = false)
    {
        if (!config.Enabled) return;
        if (config.DutiesOnly && !Plugin.Condition[ConditionFlag.BoundByDuty] && !Plugin.Condition[ConditionFlag.BoundByDuty56]) return;
        var inParty = Plugin.PartyList.Length > 1;
        if (config.OnlyInParty && !inParty) return;

        // Someone else's raise already on them (only checked before ours lands: after, it's ours).
        if (!landed && config.SkipIfAlreadyRaised && target is IBattleChara bc &&
            bc.StatusList.Any(s => s.StatusId == RaisePendingStatus))
            return;

        announced[target.GameObjectId] = DateTime.UtcNow;
        var text = Format(config.Template, target.Name.TextValue, spell, instant).Replace("\n", " ").Trim();
        if (text.Length == 0) return;
        if (config.Channel == Channel.Echo || !inParty) Plugin.ChatGui.Print(text);
        else Send("/p " + text);
    }

    /// <summary>Send a line through the chat box, like typing it (same as the game's own macro path).</summary>
    private static void Send(string line)
    {
        if (line.Length > 400) line = line[..400];
        var ui = UIModule.Instance();
        if (ui == null) return;
        var str = Utf8String.FromString(line);
        try { ui->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }
}
