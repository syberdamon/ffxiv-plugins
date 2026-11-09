using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// ICE + Artisan stuck-craft recovery. Artisan's solver sometimes picks an action the game won't allow
/// (e.g. "[Artisan] Unable to use Prudent Touch: You have a Waste Not buff") and then sits in the craft
/// forever, so ICE waits forever too. When that error shows up while ICE is running and the craft doesn't
/// move on within a few seconds: ICE is turned off, Artisan's endurance is turned off, the craft is quit
/// (the window's Quit, then Yes), and ICE is turned back on. During an ICE break's "finish the mission"
/// wait, ICE is left off instead, so the break carries on to the retainers.
/// Quitting a craft can lose some of its materials (the game's rule).
/// </summary>
public sealed unsafe class ArtisanWatch : IDisposable
{
    private enum Phase { Idle, Armed, Quitting, Settle, Restart }

    private readonly Plugin plugin;
    private readonly IceIpc ice = new();
    private readonly ICallGateSubscriber<bool, object> artisanEndurance;
    private Configuration C => plugin.Configuration;

    private Phase phase = Phase.Idle;
    private DateTime phaseStart, next, freeSince;
    private bool sawAction, quitClicked, restartIce;
    private int tries;
    private string lastError = "";
    private readonly List<DateTime> recoveries = new();

    public string Status { get; private set; } = "";

    public ArtisanWatch(Plugin plugin)
    {
        this.plugin = plugin;
        artisanEndurance = Plugin.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus");
        Plugin.ChatGui.ChatMessage += OnChat;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChat;
        Plugin.Framework.Update -= OnUpdate;
    }

    private static bool Crafting => Plugin.Condition[ConditionFlag.Crafting] || GameUi.IsOpen("Synthesis");

    private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        try
        {
            if (!C.ArtisanStuckRecovery || phase != Phase.Idle) return;
            var text = chat.Message.TextValue;
            if (!text.Contains("Unable to use", StringComparison.OrdinalIgnoreCase)) return;
            // Artisan's errors read "[Artisan] Unable to use <action>: <reason>"; the stuck check below guards the rest.
            if (!ice.Running || !GameUi.IsOpen("Synthesis")) return;
            lastError = text.Trim();
            Set(Phase.Armed, "Artisan error: checking whether the craft is stuck...");
            sawAction = false;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: Artisan watch chat check failed"); }
    }

    private void Set(Phase p, string status)
    {
        phase = p;
        Status = status;
        phaseStart = DateTime.UtcNow;
        freeSince = DateTime.MinValue;
        quitClicked = false;
        tries = 0;
    }

    private double Elapsed => (DateTime.UtcNow - phaseStart).TotalSeconds;

    private void OnUpdate(IFramework _)
    {
        if (phase == Phase.Idle || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.5);
        try
        {
            switch (phase)
            {
                case Phase.Armed: TickArmed(); break;
                case Phase.Quitting: TickQuitting(); break;
                case Phase.Settle: TickSettle(); break;
                case Phase.Restart: TickRestart(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Material Stash: Artisan watch failed");
            End("error (see /xllog).", error: true);
        }
    }

    private void End(string why, bool error = false)
    {
        phase = Phase.Idle;
        Status = why;
        if (error) Plugin.ChatGui.PrintError($"[Stash] Artisan stuck: {why}");
        else Plugin.ChatGui.Print($"[Stash] Artisan stuck: {why}");
    }

    /// <summary>Wait a few seconds: if a crafting action goes through, or the craft ends, it wasn't stuck.</summary>
    private void TickArmed()
    {
        if (Plugin.Condition[ConditionFlag.ExecutingCraftingAction]) sawAction = true;
        if (sawAction || !GameUi.IsOpen("Synthesis")) { phase = Phase.Idle; Status = "Artisan carried on by itself."; return; }
        if (Elapsed < 6) return;

        // Loop guard: the same recipe can trip the solver again and again.
        recoveries.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 30);
        if (recoveries.Count >= 3)
        {
            End("3 stuck crafts in 30 min: leaving it for you (check Artisan's solver for this recipe). " +
                $"Last error: {lastError}. ICE is still on and waiting.", error: true);
            return;
        }
        recoveries.Add(DateTime.UtcNow);

        // During an ICE break's mission wait, leave ICE off so the break moves on to the retainers.
        restartIce = !plugin.IceBreak.Finishing;
        Plugin.ChatGui.Print($"[Stash] Artisan stuck ({lastError}): quitting the craft and {(restartIce ? "restarting ICE" : "letting the ICE break carry on")}.");
        ice.Disable();
        try { artisanEndurance.InvokeAction(false); } catch (Exception) { }
        Set(Phase.Quitting, "Quitting the craft...");
    }

    private void TickQuitting()
    {
        var synth = GameUi.Get("Synthesis");
        if (synth == null && !Plugin.Condition[ConditionFlag.ExecutingCraftingAction])
        {
            Set(Phase.Settle, "Craft quit: waiting for the character to be free...");
            return;
        }
        if (Elapsed > 25)
        {
            GameUi.Dump("Synthesis", "Artisan watch: couldn't quit the craft. Visible: " + string.Join(", ", GameUi.VisibleAddons()));
            End("couldn't quit the craft (dump saved): quit it yourself, then start ICE again.", error: true);
            return;
        }
        if (Plugin.Condition[ConditionFlag.ExecutingCraftingAction]) return; // let an action in progress land

        // "Quit synthesis?" yes/no.
        var yn = GameUi.Get("SelectYesno");
        if (yn != null)
        {
            var t = GameUi.YesNoText().ToLowerInvariant();
            if (t.Contains("quit") || t.Contains("synthesis") || t.Contains("material"))
            {
                yn->FireCallbackInt(0);
                next = DateTime.UtcNow.AddSeconds(1);
                return;
            }
            GameUi.Dump("SelectYesno", "Artisan watch: unexpected yes/no while quitting the craft");
            End($"unexpected question while quitting: \"{GameUi.YesNoText()}\" (left for you). ICE is off.", error: true);
            return;
        }

        if (synth == null) return;
        if (quitClicked && Elapsed < 3 + tries * 3) return;
        tries++;
        // The window's Quit button; if it can't be found by its label, the window's close callback [Assumption].
        var button = GameUi.FindButton(synth, "Quit");
        if (button != null) GameUi.ClickButton(synth, button);
        else synth->FireCallbackInt(-1);
        quitClicked = true;
        next = DateTime.UtcNow.AddSeconds(1);
    }

    private void TickSettle()
    {
        var busy = Plugin.Condition[ConditionFlag.ExecutingCraftingAction] || Plugin.Condition[ConditionFlag.Occupied]
                   || Plugin.Condition[ConditionFlag.Casting] || GameUi.IsOpen("Synthesis");
        if (busy) { freeSince = DateTime.MinValue; if (Elapsed > 30) End("the character stayed busy after the quit. ICE is off.", error: true); return; }
        if (freeSince == DateTime.MinValue) { freeSince = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - freeSince).TotalSeconds < 2) return;
        if (!restartIce) { End("craft quit; ICE left off so the ICE break goes on to the retainers."); return; }
        Set(Phase.Restart, "Starting ICE again...");
    }

    private void TickRestart()
    {
        if (ice.Running) { End("craft quit and ICE restarted."); return; }
        if (tries == 0 || (Elapsed > 15 && tries < 2))
        {
            if (!ice.Enable()) { End("ICE didn't answer. ICE is off.", error: true); return; }
            tries++;
            phaseStart = DateTime.UtcNow;
            return;
        }
        if (Elapsed > 15) End("ICE didn't start again (see ICE's window). ICE is off.", error: true);
    }
}
