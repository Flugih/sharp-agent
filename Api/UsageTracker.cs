using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpAgent.Core;

namespace SharpAgent.Api;

public sealed record SpendSnapshot(
    long Balance,
    long FullBalance,
    long Today,
    long Week,
    long Month,
    long MonthToDate,
    bool HistoryComplete,
    DateTime RefreshedAt);

public sealed class UsageTracker(UiState state)
{
    private const int MaxPages = 40;
    private const int KeepDays = 62;
    private const string DayFormat = "yyyy-MM-dd";

    public SpendSnapshot? Last { get; private set; }

    public double? RateOf(string model) =>
        state.Usage != null && state.Usage.Rates.TryGetValue(model, out var rate) && rate > 0 ? rate : null;

    public static string KeyId(string apiKey, string baseUrl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + "|" + baseUrl)))[..16];

    public long AddLocal(string model, long totalTokens)
    {
        long cost = RateOf(model) is { } rate ? (long)Math.Ceiling(totalTokens * rate) : 0;
        if (Last != null)
        {
            Last = Last with
            {
                Balance = Math.Max(0, Last.Balance - cost),
                Today = Last.Today + cost,
                Week = Last.Week + cost,
                Month = Last.Month + cost,
                MonthToDate = Last.MonthToDate + cost,
            };
        }
        return cost;
    }

    public async Task<SpendSnapshot> RefreshAsync(ChatApiClient api, string keyId, CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        long balance = (await api.GetBalanceAsync(ct)).Balance;

        var cache = state.Usage is { } saved && saved.KeyId == keyId ? saved : new UsageCache { KeyId = keyId };
        bool firstRun = cache.CoveredFrom == null;

        var today = DateTime.Now.Date;
        var lastRefreshDay = cache.RefreshedUtc?.ToLocalTime().Date ?? today;
        var refetchFrom = firstRun ? DateTime.MinValue : (lastRefreshDay < today ? lastRefreshDay : today).AddDays(-1);

        var fresh = new Dictionary<string, UsageDay>();
        var freshRates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long spentSinceLast = 0, fetchedTotal = 0;
        DateTime? oldestDay = null;
        string? cursor = null;

        for (int page = 0; page < MaxPages; page++)
        {
            var result = await api.GetUsageAsync(cursor, ct);
            cursor = result.NextCursor;
            foreach (var entry in result.Entries)
            {
                var day = entry.CreatedUtc.ToLocalTime().Date;
                if (day < refetchFrom) { cursor = null; break; }

                oldestDay = day;
                fetchedTotal += entry.Cost;
                if (cache.RefreshedUtc is { } last && entry.CreatedUtc > last) spentSinceLast += entry.Cost;

                var key = FormatDay(day);
                if (!fresh.TryGetValue(key, out var bucket)) fresh[key] = bucket = new UsageDay();
                bucket.Cost += entry.Cost;

                if (entry.Success && entry.TotalTokens > 0 && entry.Cost > 0 && freshRates.Add(entry.Model))
                    cache.Rates[entry.Model] = (double)entry.Cost / entry.TotalTokens;
            }
            if (cursor == null) break;
        }
        bool capped = cursor != null;

        var pruneBefore = today.AddDays(-KeepDays);
        cache.Days = cache.Days.Where(d => ParseDay(d.Key) < refetchFrom).Concat(fresh)
            .Where(d => ParseDay(d.Key) >= pruneBefore).ToDictionary();

        if (firstRun)
        {
            cache.CoveredFrom = FormatDay(capped && oldestDay is { } oldest ? oldest.AddDays(1) : DateTime.MinValue);
            cache.FullBalance = balance + fetchedTotal;
        }
        else if (cache.LastBalance is { } previous)
        {
            long expected = previous - spentSinceLast;
            long tolerance = Math.Max(100_000, (cache.FullBalance ?? previous) / 50);
            if (balance - expected > tolerance) cache.FullBalance = balance;
        }
        cache.FullBalance = Math.Max(cache.FullBalance ?? balance, balance);

        cache.RefreshedUtc = startedUtc;
        cache.LastBalance = balance;
        state.Usage = cache;
        state.Save();

        long Sum(DateTime from) => cache.Days.Where(d => ParseDay(d.Key) >= from).Sum(d => d.Value.Cost);
        Last = new SpendSnapshot(
            Balance: balance,
            FullBalance: cache.FullBalance.Value,
            Today: cache.Days.GetValueOrDefault(FormatDay(today))?.Cost ?? 0,
            Week: Sum(today.AddDays(-6)),
            Month: Sum(today.AddDays(-29)),
            MonthToDate: Sum(new DateTime(today.Year, today.Month, 1)),
            HistoryComplete: !capped && ParseDay(cache.CoveredFrom) <= today.AddDays(-30),
            RefreshedAt: DateTime.Now);
        return Last;
    }

    public void Reset() => Last = null;

    private static string FormatDay(DateTime day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseDay(string? value) =>
        DateTime.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DateTime.MaxValue;
}
