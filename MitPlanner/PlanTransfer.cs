using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MitPlanner;

/// <summary>
/// Copy/paste a plan as text, so it can move between PCs (plugin settings are stored per PC)
/// or be shared with someone else.
/// </summary>
public static class PlanTransfer
{
    private const string Prefix = "MITPLAN1:";

    private sealed record Payload(uint Territory, uint Job, List<Item> Entries);
    private sealed record Item(string Event, uint Action, float Lead, bool Auto);

    public static string Export(FightDefinition fight, uint jobId, IEnumerable<PlanEntry> plan)
    {
        var payload = new Payload(fight.TerritoryId, jobId,
            plan.Select(p => new Item(p.EventKey, p.ActionId, p.LeadSeconds, p.AutoUse)).ToList());
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    public static List<PlanEntry>? Import(string? text, FightDefinition fight, uint jobId, out string error)
    {
        error = "";
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix))
        {
            error = "Clipboard doesn't contain a Mit Planner plan.";
            return null;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(text[Prefix.Length..])));
        }
        catch (Exception)
        {
            error = "That plan text is damaged or incomplete.";
            return null;
        }

        if (payload == null) { error = "That plan text is empty."; return null; }
        if (payload.Territory != fight.TerritoryId) { error = "That plan is for a different fight."; return null; }
        if (payload.Job != jobId) { error = "That plan is for a different job. Switch to that job to paste it."; return null; }

        return payload.Entries
            .Select(i => new PlanEntry { EventKey = i.Event, ActionId = i.Action, LeadSeconds = i.Lead, AutoUse = i.Auto })
            .ToList();
    }
}
