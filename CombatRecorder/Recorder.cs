using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace CombatRecorder;

/// <summary>
/// Writes a JSON-lines file per duty (or per open-world session). One JSON object per line,
/// "e" = event type, "t" = seconds since the file started. Event types:
///   head  zone, duty, level sync, your job/level, party jobs by slot, versions
///   def   first time an action/status id is seen: name, cooldown group (58 = GCD), cast, recast
///   ent   first time an enemy is seen: id, name, max HP
///   act   an action resolved: who, action, targets with damage/heal amounts (crit/direct hit flags)
///   cast  someone started a cast: who, action, target, cast time
///   st    status gained (+) or lost (-) on you, your party, or enemies you're fighting
///   snap  every 0.5 s in combat: your HP/MP/position/cast/target, party HP, target HP/distance
///   tgt   your target changed;   combat  in/out of combat;   death  someone died
///   mark  /crec mark note        end   file closed
/// Who: "me", "p1".."p7" (party slot, never names), "e" + hex entity id (enemies), "o" + id (other).
/// Files are plain .jsonl while writing (crash-safe) and gzipped when closed.
/// </summary>
public sealed unsafe class Recorder : IDisposable
{
    private readonly Configuration config;
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private StreamWriter? writer;
    private string? path;
    private DateTime started;
    private uint fileTerritory;
    private bool inCombat;
    private DateTime nextSnap = DateTime.MinValue;
    private DateTime nextStatusScan = DateTime.MinValue;
    private ulong lastTarget;

    private readonly HashSet<uint> actionDefs = new();
    private readonly HashSet<uint> statusDefs = new();
    private readonly HashSet<uint> entities = new();
    private readonly Dictionary<uint, Dictionary<uint, int>> statuses = new();  // who -> status -> stacks
    private readonly Dictionary<uint, uint> casting = new();                     // who -> action being cast
    private readonly HashSet<uint> dead = new();

    public string Folder { get; } = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "logs");
    public string? CurrentFile => path;
    public int EventsWritten { get; private set; }

    public Recorder(Configuration config)
    {
        this.config = config;
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.MemberFunctionPointers.Receive, OnActionEffect);
            hook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't hook action effects; actions won't be recorded.");
        }
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        hook?.Dispose();
        Close("plugin unloaded");
    }

    // ================================================================ file lifecycle

    private bool ShouldRecord => config.Enabled && Plugin.ObjectTable.LocalPlayer != null &&
                                 (Plugin.Condition[ConditionFlag.BoundByDuty] || config.RecordOutsideDuties);

    private void OnTerritoryChanged(uint territory) => Close("zone change");

    private void EnsureOpen()
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (writer != null && fileTerritory == territory) return;
        Close("zone change");
        if (!ShouldRecord) return;

        try
        {
            Directory.CreateDirectory(Folder);
            var me = Plugin.ObjectTable.LocalPlayer!;
            var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
            var duty = tt?.ContentFinderCondition.ValueNullable;
            var zone = duty?.Name.ExtractText() is { Length: > 0 } d ? d : tt?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"zone{territory}";
            var job = Job(me.ClassJob.RowId);
            var safe = string.Concat(zone.Split(Path.GetInvalidFileNameChars()));
            path = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{safe}_{job}{me.Level}.jsonl");
            writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = false };
            started = DateTime.UtcNow;
            fileTerritory = territory;
            actionDefs.Clear(); statusDefs.Clear(); entities.Clear(); statuses.Clear(); casting.Clear(); dead.Clear();
            EventsWritten = 0;

            var party = Plugin.PartyList.Select((m, i) => $"{{\"slot\":\"{SlotOf(m.EntityId)}\",\"job\":{Q(Job(m.ClassJob.RowId))},\"lvl\":{m.Level}}}");
            Write($"\"e\":\"head\",\"zone\":{Q(zone)},\"territory\":{territory},\"cfc\":{duty?.RowId ?? 0}," +
                  $"\"sync\":{duty?.ClassJobLevelSync ?? 0}," +
                  $"\"job\":{Q(job)},\"lvl\":{me.Level},\"party\":[{string.Join(",", party)}]," +
                  $"\"local\":{Q(DateTime.Now.ToString("s"))},\"recorder\":{Q(typeof(Recorder).Assembly.GetName().Version?.ToString())}");
            if (config.ChatNotices) Plugin.ChatGui.Print($"[CombatRecorder] Recording {zone}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't start a recording");
            writer = null;
            path = null;
        }
    }

    public void Close(string why)
    {
        if (writer == null) return;
        try
        {
            Write($"\"e\":\"end\",\"why\":{Q(why)}");
            writer.Flush();
            writer.Dispose();
        }
        catch (Exception) { }
        var done = path;
        writer = null;
        path = null;
        fileTerritory = 0;
        if (done == null) return;

        if (config.ChatNotices) Plugin.ChatGui.Print($"[CombatRecorder] Saved {Path.GetFileName(done)}.gz ({EventsWritten} events).");
        var keep = config.MaxFiles;
        var folder = Folder;
        Task.Run(() =>
        {
            try
            {
                using (var src = File.OpenRead(done))
                using (var dst = File.Create(done + ".gz"))
                using (var gz = new GZipStream(dst, CompressionLevel.Optimal))
                    src.CopyTo(gz);
                File.Delete(done);

                foreach (var old in new DirectoryInfo(folder).GetFiles("*.jsonl.gz").OrderByDescending(f => f.Name).Skip(keep))
                    old.Delete();
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't compress a recording"); }
        });
    }

    public void Mark(string note)
    {
        EnsureOpen();
        if (writer == null) { Plugin.ChatGui.Print("[CombatRecorder] Not recording here (enable 'outside duties' to mark in the open world)."); return; }
        Write($"\"e\":\"mark\",\"note\":{Q(note)}");
        writer.Flush();
        Plugin.ChatGui.Print($"[CombatRecorder] Marked at {(DateTime.UtcNow - started).TotalSeconds:0.0}s: {note}");
    }

    // ================================================================ per frame

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            if (me == null) return;

            var combat = Plugin.Condition[ConditionFlag.InCombat];
            if (combat) EnsureOpen();
            if (writer == null) return;

            if (combat != inCombat)
            {
                inCombat = combat;
                Write($"\"e\":\"combat\",\"on\":{(combat ? "true" : "false")}");
                writer.Flush();
            }

            var now = DateTime.UtcNow;
            if (me.TargetObjectId != lastTarget)
            {
                lastTarget = me.TargetObjectId;
                var tgt = Plugin.ObjectTable.SearchById(lastTarget);
                Write($"\"e\":\"tgt\",\"who\":{Q(tgt != null ? Who(tgt.EntityId) : "none")}");
            }

            PollCasts();
            if (now >= nextStatusScan) { nextStatusScan = now.AddSeconds(0.25); ScanStatuses(); ScanDeaths(); }
            if (combat && now >= nextSnap) { nextSnap = now.AddSeconds(config.SnapshotSeconds); Snapshot(me); }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Combat recorder tick failed");
        }
    }

    private IEnumerable<IBattleChara> Watched()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) yield break;
        yield return me;
        foreach (var m in Plugin.PartyList)
            if (m.EntityId != me.EntityId && Plugin.ObjectTable.SearchByEntityId(m.EntityId) is IBattleChara c) yield return c;
        foreach (var o in Plugin.ObjectTable)
            if (o is IBattleNpc n && (n.StatusFlags & StatusFlags.Hostile) != 0 && (n.StatusFlags & StatusFlags.InCombat) != 0 &&
                Vector3.Distance(n.Position, me.Position) < 50)
                yield return n;
    }

    private void PollCasts()
    {
        foreach (var c in Watched())
        {
            var id = c.EntityId;
            if (!c.IsCasting) { casting.Remove(id); continue; }
            if (casting.TryGetValue(id, out var a) && a == c.CastActionId) continue;
            casting[id] = c.CastActionId;
            Entity(c);
            DefAction(c.CastActionId);
            var tgt = Plugin.ObjectTable.SearchById(c.CastTargetObjectId);
            Write($"\"e\":\"cast\",\"who\":{Q(Who(id))},\"act\":{c.CastActionId},\"tgt\":{Q(tgt != null ? Who(tgt.EntityId) : "")},\"time\":{F(c.TotalCastTime)}");
        }
    }

    private void ScanStatuses()
    {
        foreach (var c in Watched())
        {
            Entity(c);
            if (!statuses.TryGetValue(c.EntityId, out var before)) { before = new(); statuses[c.EntityId] = before; }
            var now = new Dictionary<uint, int>();
            foreach (var s in c.StatusList)
            {
                if (s.StatusId == 0) continue;
                now[s.StatusId] = s.Param;
                if (!before.TryGetValue(s.StatusId, out var stacks) || stacks != s.Param)
                {
                    DefStatus(s.StatusId);
                    var src = Plugin.ObjectTable.SearchByEntityId(s.SourceId);
                    Write($"\"e\":\"st\",\"who\":{Q(Who(c.EntityId))},\"id\":{s.StatusId},\"op\":\"+\",\"stacks\":{s.Param},\"dur\":{F(s.RemainingTime)},\"src\":{Q(src != null ? Who(src.EntityId) : "")}");
                }
            }
            foreach (var gone in before.Keys.Where(k => !now.ContainsKey(k)))
                Write($"\"e\":\"st\",\"who\":{Q(Who(c.EntityId))},\"id\":{gone},\"op\":\"-\"");
            statuses[c.EntityId] = now;
        }
    }

    private void ScanDeaths()
    {
        foreach (var c in Watched())
        {
            if (c.CurrentHp == 0 && dead.Add(c.EntityId)) Write($"\"e\":\"death\",\"who\":{Q(Who(c.EntityId))}");
            else if (c.CurrentHp > 0) dead.Remove(c.EntityId);
        }
    }

    private void Snapshot(IPlayerCharacter me)
    {
        var sb = new StringBuilder();
        sb.Append($"\"e\":\"snap\",\"hp\":{me.CurrentHp},\"mhp\":{me.MaxHp},\"mp\":{me.CurrentMp},\"x\":{F(me.Position.X)},\"y\":{F(me.Position.Y)},\"z\":{F(me.Position.Z)},\"rot\":{F(me.Rotation)}");
        if (me.IsCasting) sb.Append($",\"casting\":{me.CastActionId}");
        var tgt = Plugin.ObjectTable.SearchById(me.TargetObjectId) as IBattleChara;
        if (tgt != null)
        {
            var dist = Vector3.Distance(me.Position, tgt.Position) - tgt.HitboxRadius - me.HitboxRadius;
            sb.Append($",\"tgt\":{Q(Who(tgt.EntityId))},\"thp\":{F(tgt.MaxHp > 0 ? 100f * tgt.CurrentHp / tgt.MaxHp : 0)},\"tdist\":{F(dist)},\"ttargetable\":{(tgt.IsTargetable ? "true" : "false")}");
        }
        var party = Plugin.PartyList.Where(m => m.EntityId != me.EntityId)
            .Select(m => $"{{\"who\":{Q(SlotOf(m.EntityId))},\"hp\":{m.CurrentHP},\"mhp\":{m.MaxHP},\"mp\":{m.CurrentMP}}}");
        sb.Append($",\"party\":[{string.Join(",", party)}]");
        var enemies = Watched().OfType<IBattleNpc>().ToList();
        sb.Append($",\"enemies\":{enemies.Count},\"enemyHp\":[{string.Join(",", enemies.Select(n => $"{{\"who\":{Q(Who(n.EntityId))},\"hp\":{F(n.MaxHp > 0 ? 100f * n.CurrentHp / n.MaxHp : 0)}}}"))}]");
        Write(sb.ToString());
    }

    // ================================================================ action effects

    /// <summary>
    /// [Assumption] TargetEffects = 8 effects x 8 bytes per target:
    /// type, param0 (flags: 0x20 crit, 0x40 direct hit for damage), param1, param2, param3, param4, value (u16).
    /// Big numbers: if param4 &amp; 0x40, value += param3 &lt;&lt; 16. Types: 3 damage, 4 heal, 5 blocked, 6 parried, 1 miss.
    /// </summary>
    private void OnActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        try
        {
            if (writer == null || header == null) return;
            var caster = Plugin.ObjectTable.SearchByEntityId(casterEntityId);
            if (caster is IBattleChara bc) Entity(bc);
            var actionId = header->ActionId;
            DefAction(actionId);

            var sb = new StringBuilder();
            sb.Append($"\"e\":\"act\",\"who\":{Q(Who(casterEntityId))},\"act\":{actionId},\"hits\":[");
            var n = Math.Min((int)header->NumTargets, 32);
            var first = true;
            for (var i = 0; i < n; i++)
            {
                var targetId = (uint)targetEntityIds[i].ObjectId;
                var baseByte = (byte*)effects + i * 64;
                var parts = new List<string>();
                for (var k = 0; k < 8; k++)
                {
                    var eff = baseByte + k * 8;
                    var type = eff[0];
                    if (type is not (1 or 3 or 4 or 5 or 6)) continue;
                    long value = *(ushort*)(eff + 6);
                    if ((eff[5] & 0x40) != 0) value += (long)eff[4] << 16;
                    var kind = type switch { 1 => "miss", 3 => "dmg", 4 => "heal", 5 => "block", _ => "parry" };
                    var flags = (type == 3 && (eff[1] & 0x20) != 0 ? "c" : "") + (type == 3 && (eff[1] & 0x40) != 0 ? "d" : "");
                    parts.Add($"{{\"k\":\"{kind}\",\"v\":{value}{(flags.Length > 0 ? $",\"f\":\"{flags}\"" : "")}}}");
                }
                sb.Append(first ? "" : ",");
                first = false;
                sb.Append($"{{\"to\":{Q(Who(targetId))},\"fx\":[{string.Join(",", parts)}]}}");
            }
            sb.Append(']');
            Write(sb.ToString());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Recording an action failed");
        }
    }

    // ================================================================ definitions / names

    private void DefAction(uint id)
    {
        if (writer == null || id == 0 || !actionDefs.Add(id)) return;
        var a = Plugin.DataManager.GetExcelSheet<Action>(Dalamud.Game.ClientLanguage.English).GetRowOrDefault(id);
        if (a == null) { Write($"\"e\":\"def\",\"act\":{id}"); return; }
        var r = a.Value;
        Write($"\"e\":\"def\",\"act\":{id},\"name\":{Q(r.Name.ExtractText())},\"cdg\":{r.CooldownGroup},\"cdg2\":{r.AdditionalCooldownGroup}," +
              $"\"cast\":{F(r.Cast100ms / 10f)},\"recast\":{F(r.Recast100ms / 10f)},\"player\":{(r.IsPlayerAction ? "true" : "false")},\"lvl\":{r.ClassJobLevel}");
    }

    private void DefStatus(uint id)
    {
        if (writer == null || !statusDefs.Add(id)) return;
        var s = Plugin.DataManager.GetExcelSheet<LuminaStatus>(Dalamud.Game.ClientLanguage.English).GetRowOrDefault(id);
        Write($"\"e\":\"def\",\"st\":{id},\"name\":{Q(s?.Name.ExtractText() ?? "")}");
    }

    private void Entity(IGameObject o)
    {
        if (writer == null || o.ObjectKind != ObjectKind.BattleNpc || o is not IBattleNpc n || !entities.Add(n.EntityId)) return;
        Write($"\"e\":\"ent\",\"who\":{Q(Who(n.EntityId))},\"name\":{Q(n.Name.TextValue)},\"mhp\":{n.MaxHp},\"hitbox\":{F(n.HitboxRadius)}");
    }

    private static string Who(uint entityId)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me != null && entityId == me.EntityId) return "me";
        var slot = SlotOf(entityId);
        if (slot.Length > 0) return slot;
        var o = Plugin.ObjectTable.SearchByEntityId(entityId);
        if (o?.ObjectKind == ObjectKind.BattleNpc) return $"e{entityId:X}";
        return entityId == 0xE0000000 || entityId == 0 ? "" : $"o{entityId:X}"; // other players, pets, trusts
    }

    private static string SlotOf(uint entityId)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me != null && entityId == me.EntityId) return "me";
        var i = 0;
        foreach (var m in Plugin.PartyList)
        {
            if (m.EntityId == me?.EntityId) continue;
            i++;
            if (m.EntityId == entityId) return $"p{i}";
        }
        return "";
    }

    private static string Job(uint id) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(id)?.Abbreviation.ExtractText() ?? id.ToString();

    // ================================================================ writing

    private void Write(string body)
    {
        if (writer == null) return;
        var t = (DateTime.UtcNow - started).TotalSeconds;
        writer.Write('{');
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"\"t\":{t:0.000},"));
        writer.Write(body);
        writer.Write("}\n");
        EventsWritten++;
        if (EventsWritten % 200 == 0) writer.Flush();
    }

    private static string Q(string? s) => JsonSerializer.Serialize(s ?? "");
    private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
}
