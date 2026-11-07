using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MaterialStash;

/// <summary>
/// Home-world market data from Universalis (NQ only, since ventures bring back NQ):
///  - Lowest: the cheapest current listing.
///  - Median: median price per unit of the sales in the last 7 days.
///  - UnitsPerDay: items sold per day over those sales. Universalis's own "sale velocity" counts
///    sales, not items (one 99-stack = one sale), so this is worked out from the sale quantities.
/// Fetched in the background (100 items per request), cached for the configured time.
/// </summary>
public sealed class Market : IDisposable
{
    public sealed record Info(long Lowest, long Median, double UnitsPerDay, int Sales, DateTime Fetched)
    {
        /// <summary>The lower of the lowest listing and the median sale (0 = no data).</summary>
        public long Price => Lowest > 0 && Median > 0 ? Math.Min(Lowest, Median) : Math.Max(Lowest, Median);
    }

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ConcurrentDictionary<uint, Info> cache = new();
    private readonly ConcurrentDictionary<uint, byte> pending = new();
    private DateTime notBefore = DateTime.MinValue;
    public string Status { get; private set; } = "No prices yet.";
    public string World { get; private set; } = "";

    public Market() => http.DefaultRequestHeaders.UserAgent.ParseAdd("MaterialStash-Dalamud-plugin");

    public void Dispose() => http.Dispose();

    public Info? Get(uint itemId) => cache.TryGetValue(itemId % 1_000_000, out var i) ? i : null;

    public bool Fresh(uint itemId, int minutes) => Get(itemId) is { } i && (DateTime.UtcNow - i.Fetched).TotalMinutes <= minutes;

    /// <summary>Fetch these items for this world if missing or stale. Requests are spaced out.</summary>
    public void Request(IEnumerable<uint> itemIds, string world, int maxAgeMinutes)
    {
        if (string.IsNullOrEmpty(world) || DateTime.UtcNow < notBefore) return;
        if (world != World) { cache.Clear(); World = world; }
        var need = itemIds.Select(i => i % 1_000_000).Distinct()
            .Where(id => !Fresh(id, maxAgeMinutes) && pending.TryAdd(id, 0))
            .ToList();
        if (need.Count == 0) return;
        notBefore = DateTime.UtcNow.AddSeconds(5);
        _ = Task.Run(async () =>
        {
            foreach (var chunk in need.Chunk(100))
            {
                await Fetch(chunk, world);
                await Task.Delay(1500);
            }
        });
    }

    private async Task Fetch(uint[] ids, string world)
    {
        try
        {
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(world)}/{string.Join(",", ids)}" +
                      "?hq=false&listings=1&entries=100&entriesWithin=604800";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var many))
                foreach (var p in many.EnumerateObject()) Store(uint.Parse(p.Name), p.Value);
            else if (ids.Length == 1) Store(ids[0], root);
            Status = $"Prices: Universalis, {world}, {DateTime.Now:HH:mm} ({cache.Count} items)";
        }
        catch (Exception ex)
        {
            Status = "Universalis unavailable: " + ex.Message;
            Plugin.Log.Warning(ex, "Material Stash: Universalis request failed");
        }
        finally
        {
            foreach (var id in ids) pending.TryRemove(id, out _);
        }
    }

    private void Store(uint id, JsonElement e)
    {
        long lowest = 0;
        if (e.TryGetProperty("minPriceNQ", out var mp) && mp.ValueKind == JsonValueKind.Number) lowest = (long)mp.GetDouble();
        if (lowest == 0 && e.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray())
                if (l.TryGetProperty("pricePerUnit", out var pu)) { lowest = (long)pu.GetDouble(); break; }

        var prices = new List<long>();
        long units = 0;
        long oldest = long.MaxValue;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (e.TryGetProperty("recentHistory", out var hist) && hist.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hist.EnumerateArray())
            {
                if (h.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True) continue;
                var ts = h.TryGetProperty("timestamp", out var t) ? (long)t.GetDouble() : 0;
                if (ts <= 0 || now - ts > 7 * 86400) continue;
                var q = h.TryGetProperty("quantity", out var qq) ? (long)qq.GetDouble() : 0;
                var p = h.TryGetProperty("pricePerUnit", out var pp) ? (long)pp.GetDouble() : 0;
                if (q <= 0 || p <= 0) continue;
                units += q;
                prices.Add(p);
                oldest = Math.Min(oldest, ts);
            }
        }
        prices.Sort();
        var median = prices.Count == 0 ? 0 : prices[prices.Count / 2];
        // Over 7 days, unless the 100 most recent sales already span less: then over that span (at least a day).
        var days = prices.Count >= 100 && oldest != long.MaxValue ? Math.Max(1.0, (now - oldest) / 86400.0) : 7.0;
        cache[id] = new Info(lowest, median, units / days, prices.Count, DateTime.UtcNow);
    }
}
