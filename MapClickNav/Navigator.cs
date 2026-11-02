using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace MapClickNav;

public enum TripState
{
    Idle,
    Mounting,
    Traveling,
}

/// <summary>
/// Watches for modifier + left-click on the area map, then mounts and hands off to vnavmesh.
/// </summary>
public sealed unsafe partial class Navigator : IDisposable
{
    // [Assumption] GeneralAction row 9 is "Mount Roulette" (from memory, not verified against game data).
    private const uint MountRouletteGeneralAction = 9;

    private const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    private readonly Configuration config;
    private readonly VNavIpc vnav;
    private bool leftWasDown;

    private Vector3 destination;
    private bool wantFly;
    private DateTime mountStarted;
    private DateTime lastMountAttempt;
    private DateTime travelStarted;

    public TripState State { get; private set; } = TripState.Idle;
    public string StatusText { get; private set; } = "Idle";

    public Navigator(Configuration config, VNavIpc vnav)
    {
        this.config = config;
        this.vnav = vnav;
        Plugin.Framework.Update += OnUpdate;
        Plugin.PluginInterface.UiBuilder.Draw += CheckForMapClick;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.PluginInterface.UiBuilder.Draw -= CheckForMapClick;
    }

    // ---------------------------------------------------------------- click detection

    private void CheckForMapClick()
    {
        var leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var clicked = leftDown && !leftWasDown;
        leftWasDown = leftDown;

        if (!clicked || !config.Enabled || !ModifierHeld()) return;
        if (ImGui.GetIO().WantCaptureMouse) return; // clicking a Dalamud window, not the game

        try
        {
            if (TryGetHoveredWorldXZ(out var worldX, out var worldZ))
                StartTrip(worldX, worldZ);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Map click failed");
        }
    }

    private bool ModifierHeld()
    {
        var vk = config.Modifier switch
        {
            ModifierKey.Alt => VK_MENU,
            ModifierKey.Shift => VK_SHIFT,
            _ => VK_CONTROL,
        };
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>
    /// The map window shows the cursor's map coordinates (e.g. "X: 21.3 Y: 18.6") only while the
    /// cursor is over the map image. Read that and convert map coordinates to world X/Z.
    /// </summary>
    private bool TryGetHoveredWorldXZ(out float worldX, out float worldZ)
    {
        worldX = worldZ = 0;

        var addon = Plugin.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon == null || !addon->AtkUnitBase.IsVisible) return false;
        if (addon->CursorPositionContainerNode == null || !addon->CursorPositionContainerNode->IsVisible()) return false;
        if (addon->CursorPositionTextNode == null) return false;

        var agent = AgentMap.Instance();
        if (agent == null) return false;
        if (agent->SelectedMapId != agent->CurrentMapId)
        {
            Notify("That map isn't the zone you're in. vnavmesh can only travel within your current zone.");
            return false;
        }

        var text = addon->CursorPositionTextNode->NodeText.ToString();
        var numbers = NumberRegex().Matches(text);
        if (numbers.Count < 2) return false;

        var mapX = float.Parse(numbers[0].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var mapY = float.Parse(numbers[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);

        // Inverse of Dalamud's MapUtil.ConvertWorldCoordXZToMapCoord:
        // map = 0.02*offset + 2048/scale + 0.02*world + 1
        float scale = agent->SelectedMapSizeFactor;
        if (scale <= 0) return false;
        worldX = (mapX - 1f - 2048f / scale) / 0.02f - agent->SelectedOffsetX;
        worldZ = (mapY - 1f - 2048f / scale) / 0.02f - agent->SelectedOffsetY;
        return true;
    }

    // ---------------------------------------------------------------- trip

    private void StartTrip(float worldX, float worldZ)
    {
        if (!vnav.IsReady)
        {
            Notify("vnavmesh isn't ready (not installed, or still building this zone's mesh).");
            return;
        }

        // Drop the flag at the clicked spot, then let vnavmesh find the ground height there.
        var agent = AgentMap.Instance();
        agent->SetFlagMapMarker(agent->CurrentTerritoryId, agent->CurrentMapId, new Vector3(worldX, 0, worldZ));

        var point = vnav.FlagToPoint();
        if (point == null)
        {
            Notify("vnavmesh couldn't find walkable ground at that spot.");
            return;
        }

        vnav.Stop(); // cancel any trip already running
        destination = point.Value;
        wantFly = PlayerState.Instance()->CanFly;

        var shouldMount = config.AutoMount && (wantFly || config.MountForGroundTravel);
        if (shouldMount && !IsMounted() && !Plugin.Condition[ConditionFlag.InCombat])
        {
            State = TripState.Mounting;
            mountStarted = DateTime.UtcNow;
            lastMountAttempt = DateTime.MinValue;
            StatusText = "Mounting...";
        }
        else
        {
            BeginTravel();
        }
    }

    public void StopTrip()
    {
        vnav.Stop();
        State = TripState.Idle;
        StatusText = "Stopped";
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            switch (State)
            {
                case TripState.Mounting: TickMounting(); break;
                case TripState.Traveling: TickTraveling(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "MapClickNav tick failed");
            State = TripState.Idle;
        }
    }

    private void TickMounting()
    {
        if (IsMounted())
        {
            BeginTravel();
            return;
        }

        if ((DateTime.UtcNow - mountStarted).TotalSeconds > config.MountTimeoutSeconds)
        {
            Notify("Couldn't mount; traveling on foot.");
            BeginTravel();
            return;
        }

        // Press Mount Roulette, retrying every 2s if the press didn't take (e.g. mid-animation).
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.IsCasting) return;
        if ((DateTime.UtcNow - lastMountAttempt).TotalSeconds < 2) return;

        var am = ActionManager.Instance();
        if (am == null || am->AnimationLock > 0) return;
        if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralAction) != 0) return;

        am->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
        lastMountAttempt = DateTime.UtcNow;
    }

    private void BeginTravel()
    {
        // vnavmesh only flies if you're mounted; otherwise send a ground path.
        var fly = wantFly && IsMounted();
        if (!vnav.MoveTo(destination, fly))
        {
            Notify("vnavmesh refused the trip.");
            State = TripState.Idle;
            return;
        }

        State = TripState.Traveling;
        travelStarted = DateTime.UtcNow;
        StatusText = fly ? "Flying to flag" : "Traveling to flag";
        Notify($"{StatusText} (X {destination.X:F0}, Z {destination.Z:F0}).");
    }

    private void TickTraveling()
    {
        // Give vnavmesh a moment to start pathfinding before treating "not busy" as arrived.
        if ((DateTime.UtcNow - travelStarted).TotalSeconds < 1.5) return;
        if (!vnav.IsBusy)
        {
            State = TripState.Idle;
            StatusText = "Arrived";
        }
    }

    private static bool IsMounted() =>
        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.InFlight];

    private void Notify(string message)
    {
        StatusText = message;
        if (config.ChatFeedback) Plugin.ChatGui.Print($"[MapClickNav] {message}");
    }
}
