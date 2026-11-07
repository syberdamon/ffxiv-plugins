using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RetainerGear;

/// <summary>Lowest market board prices on your data center, from Universalis (cached 30 minutes).</summary>
public sealed class Prices : IDisposable
{
    public sealed record Price(long Nq, long Hq, DateTime Fetched);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<uint, Price> cache = new();
    private readonly ConcurrentDictionary<uint, byte> pending = new();
    public string Status { get; private set; } = "";

    public Prices() => http.DefaultRequestHeaders.UserAgent.ParseAdd("RetainerGear-Dalamud-plugin");

    public void Dispose() => http.Dispose();

    public Price? Get(uint itemId) => cache.TryGetValue(itemId, out var p) ? p : null;

    /// <summary>Fetch prices for these items if missing or older than 30 minutes (one request per 50 items).</summary>
    public void Request(IEnumerable<uint> itemIds, string dataCenter)
    {
        if (string.IsNullOrEmpty(dataCenter)) return;
        var need = itemIds.Distinct()
            .Where(id => (!cache.TryGetValue(id, out var p) || (DateTime.UtcNow - p.Fetched).TotalMinutes > 30) && pending.TryAdd(id, 0))
            .ToList();
        foreach (var chunk in need.Chunk(50))
            _ = Task.Run(() => Fetch(chunk, dataCenter));
    }

    private async Task Fetch(uint[] ids, string dc)
    {
        try
        {
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dc)}/{string.Join(",", ids)}?listings=0&entries=0";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var many))
                foreach (var p in many.EnumerateObject()) Store(uint.Parse(p.Name), p.Value);
            else if (ids.Length == 1) Store(ids[0], root);
            Status = $"Prices from Universalis ({dc}), {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            Status = "Universalis unavailable: " + ex.Message;
            Plugin.Log.Warning(ex, "RetainerGear: Universalis request failed");
        }
        finally
        {
            foreach (var id in ids) pending.TryRemove(id, out _);
        }
    }

    private void Store(uint id, JsonElement e)
    {
        long Get(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        cache[id] = new Price(Get("minPriceNQ"), Get("minPriceHQ"), DateTime.UtcNow);
    }
}
