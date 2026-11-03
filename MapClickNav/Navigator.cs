using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    Teleporting,
    WaitingForMesh,
    Mounting,
    Traveling,
}

/// <summary>
/// Modifier + left-click on the area map: drop the flag there, teleport to the nearest attuned
/// aetheryte when that's faster (always, for another zone), mount, and let vnavmesh travel.
/// Trip flow modeled on CBT/Automaton (Puni.sh, BSD-3-Clause): Tasks/CommonTasks.cs MoveTo/TeleportTo.
/// </summary>
public sealed unsafe partial class Navigator : IDisposable
{
    // GeneralAction used to mount. CBT uses 24 (PlayerEx.Mount); one of 9/24 is Mount Roulette and
    // the other Flying Mount Roulette, and either works for ground travel. [Assumption] which is which.
    private const uint MountGeneralAction = 24;

    private const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    private readonly Configuration config;
    private readonly VNavIpc vnav;
    private bool leftWasDown;

    // Current trip
    private uint targetTerritory;
    private Vector3 flatDestination; // world X/Z of the click, Y unknown
    private Vector3 destination;     // on the floor, from vnavmesh
    private bool wantFly;
    private DateTime phaseStarted;
    private DateTime lastAttempt;
    private bool sawZoneLoad;
    private DateTime loadedAt;

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

    // ================================================================ click detection

    private void CheckForMapClick()
    {
        SampleCursor();
        var leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var clicked = leftDown && !leftWasDown;
        leftWasDown = leftDown;

        if (!clicked || !config.Enabled || !ModifierHeld()) return;
        if (ImGui.GetIO().WantCaptureMouse) return; // clicking a Dalamud window, not the game

        try
        {
            if (TryGetHoveredTarget(out var territory, out var mapId, out var worldX, out var worldZ))
                StartTrip(territory, mapId, worldX, worldZ);
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
    /// cursor is over the map image. Read that and convert map coordinates to world X/Z for the
    /// map being viewed (which may be another zone).
    /// </summary>
    private bool TryGetHoveredTarget(out uint territory, out uint mapId, out float worldX, out float worldZ)
    {
        territory = mapId = 0;
        worldX = worldZ = 0;

        var addon = Plugin.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon == null || !addon->AtkUnitBase.IsVisible) return false;
        if (addon->CursorPositionContainerNode == null || !addon->CursorPositionContainerNode->IsVisible()) return false;
        if (addon->CursorPositionTextNode == null) return false;

        var agent = AgentMap.Instance();
        if (agent == null || agent->SelectedTerritoryId == 0) return false; // region/world overview

        var numbers = NumberRegex().Matches(addon->CursorPositionTextNode->NodeText.ToString());
        if (numbers.Count < 2) return false;

        var mapX = float.Parse(numbers[0].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var mapY = float.Parse(numbers[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);

        // Over a map pin (aetheryte, NPC, quest icon) the game stops updating the cursor
        // coordinates, so they still show wherever the cursor last crossed open map. If the mouse
        // has moved since they last changed, work the position out from recent cursor movement.
        var fitted = ExtrapolateFromSamples(addon);

        // The text is rounded to 0.1 (about 5 yalms). The addon also keeps the cursor position to
        // 0.01 (about half a yalm); use that when it agrees with the text.
        var precise = addon->MouseCoords;
        if (fitted is { } f)
        {
            Plugin.Log.Debug($"Map click away from the live readout (pin?): {mapX:0.00},{mapY:0.00} -> fitted {f.X:0.00},{f.Y:0.00}");
            mapX = f.X;
            mapY = f.Y;
        }
        else if (precise.X > 0 && precise.Y > 0 && Math.Abs(precise.X - mapX) < 0.15f && Math.Abs(precise.Y - mapY) < 0.15f)
        {
            mapX = precise.X;
            mapY = precise.Y;
        }

        // Inverse of Dalamud's MapUtil.ConvertWorldCoordXZToMapCoord:
        // map = 0.02*offset + 2048/scale + 0.02*world + 1
        float scale = agent->SelectedMapSizeFactor;
        if (scale <= 0) return false;
        worldX = (mapX - 1f - 2048f / scale) / 0.02f - agent->SelectedOffsetX;
        worldZ = (mapY - 1f - 2048f / scale) / 0.02f - agent->SelectedOffsetY;
        territory = agent->SelectedTerritoryId;
        mapId = agent->SelectedMapId;
        return true;
    }

    // ================================================================ cursor calibration

    private readonly List<(Vector2 Screen, Vector2 Map)> samples = new();
    private Vector2 lastMap;
    private Vector2 lastChangeScreen;
    private uint sampleMapId;

    /// <summary>
    /// Every frame the map is hovered: remember (screen position, map coordinate) pairs whenever
    /// the coordinate changes. At a fixed zoom/pan the map is linear in screen space, so a few
    /// pairs give an exact screen -> map conversion. Pairs that stop fitting (zoom, pan, another
    /// map) clear the history.
    /// </summary>
    private void SampleCursor()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        var agent = AgentMap.Instance();
        if (addon == null || !addon->AtkUnitBase.IsVisible || agent == null) { samples.Clear(); return; }
        if (agent->SelectedMapId != sampleMapId) { samples.Clear(); sampleMapId = agent->SelectedMapId; }

        var map = addon->MouseCoords;
        if (map.X <= 0 || map.Y <= 0 || map == lastMap) return;
        var screen = ImGui.GetMousePos();
        lastMap = map;
        lastChangeScreen = screen;

        if (Fit(out var fx, out var fy) && (Math.Abs(fx.A * screen.X + fx.B - map.X) > 0.06f || Math.Abs(fy.A * screen.Y + fy.B - map.Y) > 0.06f))
            samples.Clear(); // zoomed or panned: start over
        samples.Add((screen, map));
        if (samples.Count > 40) samples.RemoveAt(0);
    }

    private Vector2? ExtrapolateFromSamples(AddonAreaMap* addon)
    {
        var mouse = ImGui.GetMousePos();
        if (Vector2.Distance(mouse, lastChangeScreen) < 4 || addon->MouseCoords != lastMap) return null; // readout is live
        if (!Fit(out var fx, out var fy)) return null;
        return new Vector2(fx.A * mouse.X + fx.B, fy.A * mouse.Y + fy.B);
    }

    /// <summary>Least-squares map = A * screen + B per axis; needs enough spread to trust.</summary>
    private bool Fit(out (float A, float B) x, out (float A, float B) y)
    {
        x = y = default;
        if (samples.Count < 3) return false;
        return FitAxis(samples.Select(s => (s.Screen.X, s.Map.X)).ToList(), out x) &&
               FitAxis(samples.Select(s => (s.Screen.Y, s.Map.Y)).ToList(), out y);
    }

    private static bool FitAxis(List<(float S, float M)> pts, out (float A, float B) fit)
    {
        fit = default;
        var meanS = pts.Average(p => p.S);
        var meanM = pts.Average(p => p.M);
        var varS = pts.Sum(p => (p.S - meanS) * (p.S - meanS));
        if (pts.Max(p => p.S) - pts.Min(p => p.S) < 15 || varS <= 0) return false; // not enough movement
        var a = pts.Sum(p => (p.S - meanS) * (p.M - meanM)) / varS;
        if (a <= 0) return false; // map coordinates grow right/down
        fit = (a, meanM - a * meanS);
        return true;
    }

    // ================================================================ starting a trip

    private void StartTrip(uint territory, uint mapId, float worldX, float worldZ)
    {
        if (!vnav.IsLoaded)
        {
            Notify("vnavmesh isn't installed or enabled.");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        vnav.Stop(); // cancel any trip already running
        targetTerritory = territory;
        flatDestination = new Vector3(worldX, 0, worldZ);

        // Drop the flag where you clicked (also how vnavmesh's FlagToPoint finds it later).
        AgentMap.Instance()->SetFlagMapMarker(territory, mapId, flatDestination);

        var inZone = territory == Plugin.ClientState.TerritoryType;
        if (!inZone)
        {
            if (!config.CrossZoneTeleport)
            {
                Notify("That's another zone. Turn on cross-zone teleport in /mapnav to travel there.");
                return;
            }
            TryTeleport(required: true);
            return;
        }

        if (config.TeleportIfFaster && AetheryteFinder.FindClosest(territory, flatDestination) is { } a &&
            AetheryteFinder.IsTeleportFaster(Flat(player.Position), flatDestination, Flat(a.Position), config.TeleportMargin))
        {
            TryTeleport(required: false);
            return;
        }

        EnterWaitingForMesh();
    }

    private void TryTeleport(bool required)
    {
        var aetheryte = AetheryteFinder.FindClosest(targetTerritory, flatDestination);
        if (aetheryte == null)
        {
            if (required) { Notify("No attuned aetheryte in that zone."); State = TripState.Idle; }
            else EnterWaitingForMesh();
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat] || !Telepo.Instance()->Teleport(aetheryte.Value.AetheryteId, 0))
        {
            if (required) { Notify($"Couldn't teleport to {aetheryte.Value.Name}."); State = TripState.Idle; }
            else EnterWaitingForMesh(); // same zone: just travel the long way
            return;
        }

        Notify($"Teleporting to {aetheryte.Value.Name}...");
        State = TripState.Teleporting;
        phaseStarted = DateTime.UtcNow;
        sawZoneLoad = false;
        loadedAt = DateTime.MinValue;
    }

    public void StopTrip()
    {
        vnav.Stop();
        State = TripState.Idle;
        StatusText = "Stopped";
    }

    // ================================================================ per-frame state machine

    private void OnUpdate(IFramework framework)
    {
        try
        {
            switch (State)
            {
                case TripState.Teleporting: TickTeleporting(); break;
                case TripState.WaitingForMesh: TickWaitingForMesh(); break;
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

    private void TickTeleporting()
    {
        var elapsed = (DateTime.UtcNow - phaseStarted).TotalSeconds;
        var betweenAreas = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (betweenAreas) sawZoneLoad = true;

        // The cast never finished (moved, got hit, ran out of gil...).
        if (!sawZoneLoad && elapsed > 12)
        {
            Notify("Teleport didn't go through.");
            State = TripState.Idle;
            return;
        }
        if (elapsed > 90)
        {
            Notify("Gave up waiting for the zone to load.");
            State = TripState.Idle;
            return;
        }

        var loaded = sawZoneLoad && !betweenAreas && Plugin.ObjectTable.LocalPlayer != null &&
                     GameMain.Instance()->TerritoryLoadState == 2 &&
                     Plugin.ClientState.TerritoryType == targetTerritory;
        if (!loaded) { loadedAt = DateTime.MinValue; return; }

        // Let the zone settle for a moment before acting (same idea as CBT's "Interactable" wait).
        if (loadedAt == DateTime.MinValue) loadedAt = DateTime.UtcNow;
        if ((DateTime.UtcNow - loadedAt).TotalSeconds < 1.5) return;

        EnterWaitingForMesh();
    }

    private void EnterWaitingForMesh()
    {
        State = TripState.WaitingForMesh;
        phaseStarted = DateTime.UtcNow;
        StatusText = "Waiting for vnavmesh...";
    }

    private void TickWaitingForMesh()
    {
        if (vnav.IsBuilding || !vnav.IsReady)
        {
            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 180)
            {
                Notify("vnavmesh never finished building this zone's mesh.");
                State = TripState.Idle;
            }
            else
            {
                StatusText = vnav.IsBuilding ? "vnavmesh is building this zone's mesh..." : "Waiting for vnavmesh...";
            }
            return;
        }

        var point = PickGround(Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0);
        if (point == null)
        {
            Notify("vnavmesh couldn't find walkable ground at that spot.");
            State = TripState.Idle;
            return;
        }

        destination = point.Value;
        var off = Vector2.Distance(new(destination.X, destination.Z), new(flatDestination.X, flatDestination.Z));
        if (off > 5) Notify($"No walkable ground right there; heading to the nearest spot ({off:0} yalms from your click).");
        wantFly = PlayerState.Instance()->CanFly;

        var shouldMount = config.AutoMount && (wantFly || config.MountForGroundTravel);
        if (shouldMount && !IsMounted() && !Plugin.Condition[ConditionFlag.InCombat])
        {
            State = TripState.Mounting;
            phaseStarted = DateTime.UtcNow;
            lastAttempt = DateTime.MinValue;
            StatusText = "Mounting...";
        }
        else
        {
            BeginTravel();
        }
    }

    /// <summary>
    /// The map is 2D, so a click doesn't say which floor. vnavmesh's FlagToPoint takes the top
    /// surface (rooftops, upper walkways in towns). Prefer the walkable point nearest your own
    /// height; fall back to the top surface (e.g. when flying high above the ground).
    /// </summary>
    private Vector3? PickGround(float playerY)
    {
        var atMyLevel = new Vector3(flatDestination.X, playerY, flatDestination.Z);
        return vnav.NearestPoint(atMyLevel, 2f, 12f)
               ?? vnav.NearestPoint(atMyLevel, 8f, 12f)
               ?? vnav.FlagToPoint();
    }

    private void TickMounting()
    {
        if (IsMounted())
        {
            BeginTravel();
            return;
        }

        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > config.MountTimeoutSeconds)
        {
            Notify("Couldn't mount; traveling on foot.");
            BeginTravel();
            return;
        }

        // Press the mount action, retrying every 2s if the press didn't take (e.g. mid-animation).
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.IsCasting) return;
        if ((DateTime.UtcNow - lastAttempt).TotalSeconds < 2) return;

        var am = ActionManager.Instance();
        if (am == null || am->AnimationLock > 0) return;
        if (am->GetActionStatus(ActionType.GeneralAction, MountGeneralAction) != 0) return;

        am->UseAction(ActionType.GeneralAction, MountGeneralAction);
        lastAttempt = DateTime.UtcNow;
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
        phaseStarted = DateTime.UtcNow;
        StatusText = fly ? "Flying to flag" : "Traveling to flag";
        Notify($"{StatusText}.");
    }

    private void TickTraveling()
    {
        // Give vnavmesh a moment to start pathfinding before treating "not busy" as arrived.
        if ((DateTime.UtcNow - phaseStarted).TotalSeconds < 1.5) return;
        if (!vnav.IsBusy)
        {
            State = TripState.Idle;
            StatusText = "Arrived";
        }
    }

    // ================================================================ helpers

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    private static bool IsMounted() =>
        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.InFlight];

    private void Notify(string message)
    {
        StatusText = message;
        if (config.ChatFeedback) Plugin.ChatGui.Print($"[MapClickNav] {message}");
    }
}
