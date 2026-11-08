using System;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// Puts what ventures brought back into its home retainers once AutoRetainer is done.
/// Venture rewards land in the retainer that went, and AutoRetainer doesn't open its inventory, so
/// the Director notes which retainers came back with an item that lives elsewhere; a tidy run then
/// visits those (plus anything the usual tidy plan wants) and moves the items home.
///
/// Two ways in:
///  - Multi mode: AutoRetainer's character post-processing (the IPC other plugins use for work
///    after a character's retainers are done, before it moves on). We ask for a turn only when
///    something's waiting; in it we walk to the bell if needed, tidy, then hand control back.
///  - Single character at the bell: when AutoRetainer goes from busy to idle with the retainer list
///    still open, the tidy starts there.
/// </summary>
public sealed class AfterAutoRetainer : IDisposable
{
    private const string Me = "MaterialStash";
    private readonly Plugin plugin;
    private readonly ICallGateSubscriber<object> onAdditional;
    private readonly ICallGateSubscriber<string, object> onReady;
    private readonly ICallGateSubscriber<string, object> request;
    private readonly ICallGateSubscriber<object> finish;

    private bool wasBusy;
    private DateTime idleSince = DateTime.MinValue;
    private enum Step { Idle, Bell, Run, Running }
    private Step step;
    private bool inPostprocess;
    private DateTime stepStart;

    public string Status { get; private set; } = "";

    public AfterAutoRetainer(Plugin plugin)
    {
        this.plugin = plugin;
        var pi = Plugin.PluginInterface;
        onAdditional = pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask");
        onReady = pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess");
        request = pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess");
        finish = pi.GetIpcSubscriber<object>("AutoRetainer.FinishCharacterPostprocessRequest");
        onAdditional.Subscribe(OnAdditional);
        onReady.Subscribe(OnReady);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        try { onAdditional.Unsubscribe(OnAdditional); } catch (Exception) { }
        try { onReady.Unsubscribe(OnReady); } catch (Exception) { }
        if (inPostprocess) Done("plugin unloaded");
    }

    private bool Waiting => plugin.Configuration.TidyAfterAutoRetainer && plugin.Tracker.Me() is { VentureReturns.Count: > 0 };

    private void OnAdditional()
    {
        try { if (Waiting) request.InvokeAction(Me); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: couldn't ask AutoRetainer for a post-processing turn"); }
    }

    private void OnReady(string who)
    {
        if (who != Me) return;
        inPostprocess = true;
        if (!Waiting) { Done(null); return; }
        Begin();
    }

    private void Begin()
    {
        step = Runner.BellListOpen ? Step.Run : Step.Bell;
        stepStart = DateTime.UtcNow;
        if (step == Step.Bell) plugin.Bell.Start();
        Status = "Tidying venture returns...";
    }

    private void Done(string? why)
    {
        step = Step.Idle;
        if (why != null) Plugin.ChatGui.Print($"[Stash] Venture returns: {why}");
        if (!inPostprocess) return;
        inPostprocess = false;
        try { finish.InvokeAction(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: couldn't hand control back to AutoRetainer"); }
    }

    private void OnUpdate(IFramework _)
    {
        var ar = plugin.AutoRetainer.IsBusy;
        try
        {
            switch (step)
            {
                case Step.Idle:
                    // Single character: AutoRetainer just finished with the list still open.
                    if (wasBusy && !ar) idleSince = DateTime.UtcNow;
                    if (!ar && idleSince != DateTime.MinValue && (DateTime.UtcNow - idleSince).TotalSeconds > 2)
                    {
                        idleSince = DateTime.MinValue;
                        if (Waiting && Runner.BellListOpen && !plugin.Runner.Busy) { step = Step.Run; stepStart = DateTime.UtcNow; }
                    }
                    break;
                case Step.Bell:
                    if (Runner.BellListOpen) { step = Step.Run; stepStart = DateTime.UtcNow; break; }
                    if (!plugin.Bell.Busy && (DateTime.UtcNow - stepStart).TotalSeconds > 5 || (DateTime.UtcNow - stepStart).TotalMinutes > 3)
                        Done("couldn't get to a summoning bell; they wait for your next tidy.");
                    break;
                case Step.Run:
                    if (plugin.Tracker.Me() is not { } me) { Done(null); break; }
                    plugin.Runner.Start(Runner.RunMode.Tidy, me.VentureReturns.ToList(), inPostprocess);
                    if (plugin.Runner.Busy) { step = Step.Running; Plugin.ChatGui.Print("[Stash] Putting venture returns in their home retainers..."); }
                    else if ((DateTime.UtcNow - stepStart).TotalSeconds > 10) Done(plugin.Runner.Status);
                    break;
                case Step.Running:
                    if (!plugin.Runner.Busy)
                    {
                        // Retainers the run opened are off the list already; drop ones no longer hired.
                        if (plugin.Tracker.Me() is { } m) m.VentureReturns.RemoveWhere(id => !m.Order.Contains(id));
                        if (inPostprocess) GameUi.Close("RetainerList"); // AutoRetainer moves on from a closed bell
                        Done(null);
                    }
                    else if ((DateTime.UtcNow - stepStart).TotalMinutes > 15) { plugin.Runner.Stop(); Done("took too long: stopped."); }
                    break;
            }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash: after-AutoRetainer tidy failed"); Done(null); }
        wasBusy = ar;
    }
}
