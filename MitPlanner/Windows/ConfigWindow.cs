using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace MitPlanner.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private int fightIndex = -1;
    private uint lastAutoTerritory;
    private string? transferMessage;
    private string abilitySearch = "";
    private string fightSearch = "";
    private System.Collections.Generic.List<string> generatorNotes = new();
    private Configuration Config => plugin.Configuration;

    public ConfigWindow(Plugin plugin) : base("Mit Planner - Options###MPConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 460), MaximumSize = new Vector2(1600, 1600) };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##mptabs")) return;
        if (ImGui.BeginTabItem("Plan")) { DrawPlan(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Trash mode")) { DrawTrash(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Display")) { DrawDisplay(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Status")) { DrawStatus(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ Plan

    private void DrawPlan()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) { ImGui.TextUnformatted("Log in to edit plans."); return; }
        if (Fights.All.Count == 0) { ImGui.TextUnformatted("No fight timelines loaded (see /xllog)."); return; }

        // Fight picker: defaults to the fight you're in.
        // Follow the duty you're in: whenever you enter a zone with a timeline, select it.
        var here = plugin.Runner.Preview ? null : plugin.Runner.Fight;
        var hereTerritory = here?.TerritoryId ?? 0;
        if (fightIndex < 0 || hereTerritory != lastAutoTerritory)
        {
            lastAutoTerritory = hereTerritory;
            if (here != null) fightIndex = Math.Max(0, Fights.All.ToList().IndexOf(here));
            else if (fightIndex < 0) fightIndex = 0;
        }
        // Fight picker with a search box (matches name, category like "DT Raid", or zone ID).
        ImGui.SetNextItemWidth(340);
        if (ImGui.BeginCombo("Fight", Fights.All[fightIndex].Display, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing())
            {
                fightSearch = "";
                ImGui.SetKeyboardFocusHere();
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##fightsearch", "Search fights...", ref fightSearch, 64);
            ImGui.Separator();

            var any = false;
            for (var i = 0; i < Fights.All.Count; i++)
            {
                var f = Fights.All[i];
                if (fightSearch.Length > 0 &&
                    !f.Display.Contains(fightSearch, StringComparison.OrdinalIgnoreCase) &&
                    !f.TerritoryId.ToString().Contains(fightSearch)) continue;
                any = true;
                if (ImGui.Selectable($"{f.Display}  (zone {f.TerritoryId})##fight{i}", i == fightIndex)) fightIndex = i;
            }
            if (!any) ImGui.TextDisabled("No matching fights.");
            ImGui.EndCombo();
        }
        var fight = Fights.All[fightIndex];
        var timeline = Fights.Load(fight);

        var jobId = player.ClassJob.RowId;
        var jobName = player.ClassJob.ValueNullable?.Name.ExtractText() ?? "this job";
        ImGui.SameLine();
        ImGui.TextDisabled($"Plan for {jobName} (switch job to edit another job's plan)");

        // MT and OT plans are separate; this also sets which one runs in the fight.
        var role = (int)Config.Role;
        ImGui.TextUnformatted("Role:");
        ImGui.SameLine();
        var roleChanged = ImGui.RadioButton("MT plan", ref role, (int)TankRole.MainTank);
        ImGui.SameLine();
        roleChanged |= ImGui.RadioButton("OT plan", ref role, (int)TankRole.OffTank);
        if (roleChanged) { Config.Role = (TankRole)role; Config.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("(the selected role's plan is the one that runs)");

        var plan = Config.PlanFor(fight.TerritoryId, jobId, Config.Role);
        var catalog = ActionCatalog.ForFight(fight);
        if (fight.SyncLevel > 0 && fight.SyncLevel < player.Level)
            ImGui.TextColored(Style.Warning, $"Level sync {fight.SyncLevel}: planning with the abilities you'll have at level {fight.SyncLevel}.");

        // QC: every planned use must have its cooldown back by then.
        var conflicts = PlanCheck.Run(timeline, plan, catalog);
        if (plan.Count == 0) ImGui.TextDisabled("QC: no cooldowns planned yet.");
        else if (conflicts.Count == 0) ImGui.TextColored(Style.Ok, $"QC: all {plan.Count} planned cooldowns will be ready.");
        else ImGui.TextColored(Style.Warning, $"QC: {conflicts.Count} planned cooldown(s) won't be back in time (marked below).");
        ImGui.SameLine();
        ImGui.TextDisabled("  Times are fight time; '~' = estimated across a phase change.");

        if (ImGui.SmallButton("Copy plan to clipboard"))
        {
            ImGui.SetClipboardText(PlanTransfer.Export(fight, jobId, Config.Role, plan));
            transferMessage = $"Copied {plan.Count} entries.";
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Paste plan (replaces this role's plan for this fight)"))
        {
            var imported = PlanTransfer.Import(ImGui.GetClipboardText(), fight, jobId, Config.Role, out var error);
            if (imported != null)
            {
                Config.Plans[Configuration.PlanKey(fight.TerritoryId, jobId, Config.Role)] = imported;
                Config.Save();
                plan = imported;
                transferMessage = $"Pasted {imported.Count} entries.";
            }
            else transferMessage = error;
        }
        if (transferMessage != null) { ImGui.SameLine(); ImGui.TextDisabled(transferMessage); }

        // Rule-based starter plan for tanks (replaces this role's plan; Ctrl+click when one exists).
        var replacing = plan.Count > 0;
        if (ImGui.SmallButton(replacing ? "Generate starter plan (Ctrl+click: replaces this role's plan)" : "Generate starter plan") &&
            (!replacing || ImGui.GetIO().KeyCtrl))
        {
            var result = PlanGenerator.Generate(Config, fight, timeline, jobId, Config.Role, catalog);
            Config.Plans[Configuration.PlanKey(fight.TerritoryId, jobId, Config.Role)] = result.Plan;
            Config.Save();
            plan = result.Plan;
            conflicts = PlanCheck.Run(timeline, plan, catalog);
            generatorNotes = result.Notes;
            generatorNotes.Insert(0, $"Generated {result.Plan.Count} entries for {Configuration.RoleCode(Config.Role)}.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tank busters: MT gets Short + Big (or Rampart + Secondary if Big isn't back);\n" +
                             "OT sends its Short mit to whoever is targeted.\n" +
                             "Raid-wides alternate: MT takes #1, #3...; OT #2, #4... (Reprisal + party mit).\n" +
                             "No invulns. Edit freely afterwards.");
        if (generatorNotes.Count > 0)
        {
            foreach (var n in generatorNotes) ImGui.TextDisabled("  " + n);
            if (ImGui.SmallButton("Hide notes")) generatorNotes.Clear();
        }

        // Drag a header to reorder, drag a column edge to resize; ImGui remembers the layout.
        if (!ImGui.BeginTable("##plan", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.ScrollX,
                new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Fight time", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Cooldowns (press N seconds early; Auto = plugin presses it)", ImGuiTableColumnFlags.WidthFixed, 640);
        ImGui.TableHeadersRow();

        var multi = timeline.Events.Where(e => !e.Hidden).GroupBy(e => e.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        foreach (var e in timeline.Events.Where(e => !e.Hidden))
        {
            ImGui.TableNextRow();
            ImGui.PushID(e.Index);
            var type = Config.TagFor(fight, e.Label);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Style.FightTime(e));
            if (e.FightTimeEstimated && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Estimated: cactbot lists this at {e.Time:0.0}s on its own phase clock.\nPhase changes depend on your kill speed.");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Style.Color(type), multi.Contains(e.Label) ? $"{e.Label} #{e.Occurrence}" : e.Label);

            ImGui.TableNextColumn();
            var t = (int)type;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##type", ref t, Style.TypeNames, Style.TypeNames.Length))
            {
                Config.TagOverrides[Configuration.TagKey(fight.TerritoryId, e.Label)] = (MechanicType)t; // applies to every occurrence
                Config.Save();
            }

            ImGui.TableNextColumn();
            DrawEntries(plan, e, catalog, conflicts, timeline, type);

            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawEntries(System.Collections.Generic.List<PlanEntry> plan, TimelineEvent e, System.Collections.Generic.IReadOnlyList<CatalogEntry> catalog,
        System.Collections.Generic.Dictionary<Guid, PlanConflict> conflicts, Timeline timeline, MechanicType type)
    {
        PlanEntry? remove = null;
        foreach (var p in plan.Where(p => p.EventKey == e.Key))
        {
            ImGui.PushID(p.Id.ToString());
            var ability = catalog.FirstOrDefault(c => c.ActionId == p.ActionId);
            if (ability != null)
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(ability.Icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(20));
                ImGui.SameLine();
            }
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(ability?.Name ?? $"#{p.ActionId} (not on this job/level)");
            ImGui.SameLine();
            var lead = p.LeadSeconds;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##lead", ref lead, 0.1f, 0f, 30f, "%.1fs")) { p.LeadSeconds = lead; Config.Save(); }
            ImGui.SameLine();
            var cond = (int)p.Condition;
            ImGui.SetNextItemWidth(130);
            if (ImGui.Combo("##cond", ref cond, Style.ConditionNames, Style.ConditionNames.Length)) { p.Condition = (TargetCondition)cond; Config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Who this mechanic hits decides whether to call it out / auto-press.\n" +
                                 "Checked live: head marker, then the boss's cast target, then who the boss is attacking.");
            // Send only exists for abilities the game lets you cast on a party member.
            if (ability?.CanTargetAlly == true)
            {
                ImGui.SameLine();
                var send = p.SendToTarget;
                if (ImGui.Checkbox("Send", ref send))
                {
                    p.SendToTarget = send;
                    if (send && p.Condition == TargetCondition.Always) p.Condition = TargetCondition.IfNotTargeted;
                    Config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("OT Sent Mits: cast this on whoever the mechanic targets (normally the MT),\n" +
                                     "without targeting them. Use with 'If I'm NOT targeted'.");
            }
            else if (ability != null && p.SendToTarget)
            {
                p.SendToTarget = false; // self-only ability: can't be sent (e.g. from an older plan)
                Config.Save();
            }
            ImGui.SameLine();
            var auto = p.AutoUse;
            if (ImGui.Checkbox("Auto", ref auto)) { p.AutoUse = auto; Config.Save(); }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) remove = p;

            if (conflicts.TryGetValue(p.Id, out var c))
            {
                ImGui.SameLine();
                ImGui.TextColored(Style.Warning, $"! not back until {Style.ClockWithSeconds(c.ReadyAt)}");
                if (ImGui.IsItemHovered())
                {
                    var by = c.BlockedBy != null ? $"{c.BlockedBy.Label} #{c.BlockedBy.Occurrence}" : "an earlier use";
                    var charges = c.MaxCharges > 1 ? $" ({c.MaxCharges} charges, {c.RecastSeconds:0}s each)" : $" ({c.RecastSeconds:0}s cooldown)";
                    ImGui.SetTooltip($"Planned at {Style.ClockWithSeconds(c.PlannedAt)}, but it's still recharging from {by}{charges}.\n" +
                                     $"Ready again at {Style.ClockWithSeconds(c.ReadyAt)}. Move it, lower the lead time, or pick another cooldown.");
                }
            }
            ImGui.PopID();
        }
        if (remove != null) { plan.Remove(remove); Config.Save(); }

        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##add", "+ Add cooldown", ImGuiComboFlags.HeightLarge))
        {
            // Search box at the top; focused when the list opens.
            if (ImGui.IsWindowAppearing())
            {
                abilitySearch = "";
                ImGui.SetKeyboardFocusHere();
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Search...", ref abilitySearch, 64);

            var sort = (int)Config.AbilitySort;
            var sortChanged = ImGui.RadioButton("By cooldown", ref sort, (int)AbilitySort.Cooldown);
            ImGui.SameLine();
            sortChanged |= ImGui.RadioButton("A-Z", ref sort, (int)AbilitySort.Name);
            if (sortChanged) { Config.AbilitySort = (AbilitySort)sort; Config.Save(); }
            ImGui.Separator();

            var list = catalog.Where(c => abilitySearch.Length == 0 || c.Name.Contains(abilitySearch, StringComparison.OrdinalIgnoreCase));
            list = Config.AbilitySort == AbilitySort.Name
                ? list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                : list.OrderBy(c => c.RecastSeconds).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

            var any = false;
            foreach (var c in list)
            {
                any = true;
                if (ImGui.Selectable($"{c.Name}  ({Style.ClockWithSeconds(c.RecastSeconds)})##{c.ActionId}"))
                {
                    // Tank-buster mits default to "only if I'm targeted".
                    plan.Add(new PlanEntry
                    {
                        EventKey = e.Key,
                        ActionId = c.ActionId,
                        Condition = type == MechanicType.TankBuster ? TargetCondition.IfTargeted : TargetCondition.Always,  // shared busters hit both tanks
                        AutoUse = true, // still needs the Auto: ON switch
                    });
                    Config.Save();
                }
            }
            if (!any) ImGui.TextDisabled("No matching abilities.");
            ImGui.EndCombo();
        }
    }

    // ================================================================ Display

    private void DrawDisplay()
    {
        var show = Config.ShowTimeline;
        if (ImGui.Checkbox("Show timeline window", ref show)) { Config.ShowTimeline = show; Config.Save(); }

        var count = Config.UpcomingCount;
        if (ImGui.SliderInt("Upcoming mechanics shown", ref count, 1, 15)) { Config.UpcomingCount = count; Config.Save(); }

        var tagged = Config.ShowOnlyTagged;
        if (ImGui.Checkbox("Only show tagged mechanics (hide 'Other')", ref tagged)) { Config.ShowOnlyTagged = tagged; Config.Save(); }

        var locked = Config.LockWindows;
        if (ImGui.Checkbox("Lock window positions", ref locked)) { Config.LockWindows = locked; Config.Save(); }

        ImGui.Separator();
        var scale = Config.CalloutScale;
        if (ImGui.SliderFloat("Callout text size", ref scale, 1f, 3f, "%.1fx")) { Config.CalloutScale = scale; Config.Save(); }

        var heads = Config.CalloutHeadsUp;
        if (ImGui.SliderFloat("Callout heads-up before press time", ref heads, 0f, 10f, "%.0fs")) { Config.CalloutHeadsUp = heads; Config.Save(); }

        var chat = Config.ChatCallouts;
        if (ImGui.Checkbox("Also print callouts to chat", ref chat)) { Config.ChatCallouts = chat; Config.Save(); }

        var showCallouts = Config.ShowCallouts;
        if (ImGui.Checkbox("Show on-screen callouts", ref showCallouts)) { Config.ShowCallouts = showCallouts; Config.Save(); }
        ImGui.TextDisabled("Off hides the callout window only; auto-use still runs if it's on.");

        var headers = Config.ShowTimelineHeaders;
        if (ImGui.Checkbox("Show timeline column headers (drag to reorder, edges to resize)", ref headers)) { Config.ShowTimelineHeaders = headers; Config.Save(); }

        var place = plugin.ShowCalloutPlacement;
        if (ImGui.Checkbox("Show callout area (to move it)", ref place)) plugin.ShowCalloutPlacement = place;

        ImGui.Separator();
        var planOn = Config.PlanEnabled;
        if (ImGui.Checkbox("Run the plan (callouts and auto-use)", ref planOn)) { Config.PlanEnabled = planOn; Config.Save(); }
        ImGui.TextDisabled("Off = timeline only. Also on the timeline window and /mitplan on|off.");

        var allowAuto = Config.AllowAutoUse;
        if (ImGui.Checkbox("Allow auto-use (only for plan entries ticked 'Auto')", ref allowAuto)) { Config.AllowAutoUse = allowAuto; Config.Save(); }
        ImGui.TextDisabled("Auto-use presses combat actions for you. That's automation: same risk category as Tank Stance Manager.");

        var react = Config.ReactToBusterMarkers;
        if (ImGui.Checkbox("Cover unplanned tank busters (cactbot tank-buster markers)", ref react)) { Config.ReactToBusterMarkers = react; Config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tank-buster marker on you, or a shared-buster marker on a tank, and nothing planned covers it:\n" +
                             "call out the best mits off cooldown (Big, else Rampart + Secondary; plus Short).\n" +
                             "With auto-use allowed, it presses them too.");
        var autoPlan = Config.AutoPlanLearned;
        if (ImGui.Checkbox("Add learned tank busters to my plan", ref autoPlan)) { Config.AutoPlanLearned = autoPlan; Config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a head-marker tank buster is learned in a pull, mits for each hit of it are added\n" +
                             "to your current role's plan (hits that already have something planned are skipped).");

        ImGui.Separator();
        if (Fights.All.Count == 0) return;
        var fight = Fights.All[Math.Clamp(fightIndex, 0, Fights.All.Count - 1)];
        if (!plugin.Runner.Preview)
        {
            if (ImGui.Button($"Preview {fight.Name} timeline")) plugin.Runner.StartPreview(fight);
            ImGui.SameLine();
            ImGui.TextDisabled("Runs the clock from 0 anywhere, no syncing, no auto-use.");
        }
        else if (ImGui.Button("Stop preview")) plugin.Runner.Stop();
    }

    // ================================================================ Trash mode

    private void DrawTrash()
    {
        var t = Config.Trash;
        var changed = false;
        ImGui.TextWrapped("Automatic tank mitigation for dungeon trash pulls (rules: docs/DUNGEON_MIT_RULES.md). " +
                          "Presses cooldowns only when 'Allow auto-use' (Display tab) is on; otherwise it calls them out.");
        ImGui.TextColored(Config.AllowAutoUse ? Style.Ok : Style.Warning,
            Config.AllowAutoUse ? "Auto-use is ON: Trash mode will press mits." : "Auto-use is OFF: callouts only.");
        ImGui.TextDisabled($"Now: {plugin.Trash.Summary}");
        ImGui.Separator();

        bool B(string label, bool v, out bool nv) { nv = v; var c = ImGui.Checkbox(label, ref nv); changed |= c; return nv; }
        float F(string label, float v, float min, float max, string fmt) { var x = v; ImGui.SetNextItemWidth(160); if (ImGui.DragFloat(label, ref x, 0.1f, min, max, fmt)) { changed = true; return Math.Clamp(x, min, max); } return v; }
        int I(string label, int v, int min, int max) { var x = v; ImGui.SetNextItemWidth(160); if (ImGui.DragInt(label, ref x, 0.1f, min, max)) { changed = true; return Math.Clamp(x, min, max); } return v; }

        t.Enabled = B("Enabled (dungeons only, tank jobs only)", t.Enabled, out _);
        t.Log = B("Write a run log for tuning", t.Log, out _);
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Trash.Log.CurrentFile is { } f ? System.IO.Path.GetFileName(f) : plugin.Trash.Log.Folder);

        if (ImGui.CollapsingHeader("When a pull counts as set", ImGuiTreeNodeFlags.DefaultOpen))
        {
            t.MinMobs = I("Mobs on you", t.MinMobs, 1, 15);
            t.StationarySeconds = F("Standing still for (s)", t.StationarySeconds, 0, 5, "%.1f");
            t.GatherRadius = F("Mobs gathered within (y)", t.GatherRadius, 1, 15, "%.1f");
            t.GatherShare = F("Share of mobs gathered", t.GatherShare, 0, 1, "%.2f");
            t.MovingHpPercent = F("Act anyway below HP % (while moving/gathering)", t.MovingHpPercent, 0, 100, "%.0f");
            t.PullShortHpPercent = F("Short mit while gathering below HP %", t.PullShortHpPercent, 0, 100, "%.0f");
            t.PullRampartHpPercent = F("Rampart while gathering (no short mit) below HP %", t.PullRampartHpPercent, 0, 100, "%.0f");
            t.PullShortMinMobs = I("  ...with at least this many mobs on you", t.PullShortMinMobs, 1, 20);
            t.IdleHpPercent = F("No new big mit at or above HP %...", t.IdleHpPercent, 0, 100, "%.0f");
            t.IdleDamagePctPerSec = F("  ...while taking less than %/s", t.IdleDamagePctPerSec, 0, 20, "%.1f");
        }
        if (ImGui.CollapsingHeader("Rotation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            t.HeavyMinPullSeconds = F("40% only if the pull lasts another (s)", t.HeavyMinPullSeconds, 0, 60, "%.0f");
            t.GapMinPullSeconds = F("Refill an expired big mit if the pull lasts another (s)", t.GapMinPullSeconds, 0, 60, "%.0f");
            t.WindDownSeconds = F("No new big mits under (s) of pull left", t.WindDownSeconds, 0, 30, "%.0f");
            t.BigPullMobs = I("40% goes first at this many mobs", t.BigPullMobs, 2, 20);
            t.EscalateHpPercent = F("Escalate below HP %", t.EscalateHpPercent, 0, 100, "%.0f");
            t.HealHpPercent = F("Heal below HP %", t.HealHpPercent, 0, 100, "%.0f");
            t.PartyMitsAsHeals = B("Use party mits as heals (Shake It Off, Divine Veil, ...)", t.PartyMitsAsHeals, out _);
            t.WarRampartAfterBloodwhettingHp = F("WAR: Rampart waits for Bloodwhetting unless HP below %", t.WarRampartAfterBloodwhettingHp, 0, 100, "%.0f");
            t.WarBloodwhettingOnly = B("WAR: short heal only (Raw Intuition / Bloodwhetting carries pulls)", t.WarBloodwhettingOnly, out _);
            t.WarShortHealLevel = I("  ...from level (its heal-on-hit unlocked)", t.WarShortHealLevel, 1, 100);
            t.WarOtherMitsBelowHpPct = F("  ...other mits only after it wears off and HP below %", t.WarOtherMitsBelowHpPct, 0, 100, "%.0f");
            t.WarChainMinMobs = I("  ...but chain the next mit as it wears off with this many mobs", t.WarChainMinMobs, 1, 20);
            t.WarChainMinPullSeconds = F("  ...and this much pull left (s)", t.WarChainMinPullSeconds, 0, 60, "%.0f");
        }
        if (ImGui.CollapsingHeader("Emergency and invulns", ImGuiTreeNodeFlags.DefaultOpen))
        {
            t.EmergencyHpPercent = F("Emergency below HP %", t.EmergencyHpPercent, 0, 100, "%.0f");
            t.InvulnLethalSeconds = F("Invuln if this many seconds of incoming damage would kill (no major mit up/ready)", t.InvulnLethalSeconds, 0.5f, 6, "%.1f");
            t.InvulnPaladin = B("Paladin: Hallowed Ground", t.InvulnPaladin, out _);
            t.InvulnWarrior = B("Warrior: Holmgang", t.InvulnWarrior, out _);
            t.InvulnDarkKnight = B("Dark Knight: Living Dead (needs heals after)", t.InvulnDarkKnight, out _);
            t.InvulnGunbreaker = B("Gunbreaker: Superbolide (drops you to 1 HP)", t.InvulnGunbreaker, out _);
        }
        if (changed) Config.Save();
    }

    // ================================================================ Status

    private void DrawStatus()
    {
        var r = plugin.Runner;
        ImGui.TextUnformatted($"Zone ID: {Plugin.ClientState.TerritoryType}");
        ImGui.TextUnformatted($"Fight: {r.Fight?.Name ?? "none (no timeline for this zone)"}");
        ImGui.TextUnformatted($"Clock: {(r.Running ? Style.Clock(r.Now) : "stopped")}{(r.Preview ? " (preview)" : "")}");
        ImGui.TextUnformatted($"Last sync: {r.LastSync}");
        ImGui.TextUnformatted($"Active callouts: {plugin.Tracker.Active.Count}");
        ImGui.Spacing();
        ImGui.TextDisabled($"Bundled fights: {Fights.All.Count} (cactbot timelines, refreshed weekly)");
    }
}
