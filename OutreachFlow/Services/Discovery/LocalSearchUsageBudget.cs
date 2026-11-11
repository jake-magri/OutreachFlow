using System.Text.Json;

namespace OutreachFlow.Services.Discovery;

/// <summary>
/// Conservative app-wide search REQUEST budget across all workspaces.
/// Reservation is persisted before a provider request; errors still consume budget.
/// This is a local single-process prototype, not distributed billing enforcement.
/// </summary>
public interface ISearchUsageBudget
{
    Task<SearchBudgetSnapshot> ReserveAsync(CancellationToken cancellationToken = default);
    Task<SearchBudgetSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record SearchBudgetSnapshot(int Used, int Limit, int Remaining, string MonthUtc);

public sealed class LocalSearchUsageBudget(string directory, int configuredLimit) : ISearchUsageBudget
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root = Path.GetFullPath(directory);
    private readonly int limit = Math.Clamp(configuredLimit, 1, 1000);

    public async Task<SearchBudgetSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<SearchBudgetSnapshot> ReserveAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(cancellationToken);
            if (current.Remaining == 0)
                throw new InvalidOperationException("Monthly web-search request budget reached. No search was made.");
            // Persist a reservation BEFORE the external operation to avoid silent overrun.
            var next = current with { Used = current.Used + 1, Remaining = current.Remaining - 1 };
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "search-requests.json");
            var temp = Path.Combine(root, "search-requests-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(
                    new UsageState(next.MonthUtc, next.Used)), cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return next;
        }
        finally { gate.Release(); }
    }

    private async Task<SearchBudgetSnapshot> ReadAsync(CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(root, "search-requests.json");
        if (!File.Exists(path)) return new SearchBudgetSnapshot(0, limit, limit, month);
        if (new FileInfo(path).Length > 4096)
            throw new InvalidOperationException("Search usage ledger is too large. Searches disabled.");
        try
        {
            var raw = await File.ReadAllTextAsync(path, ct);
            var state = JsonSerializer.Deserialize<UsageState>(raw)
                ?? throw new InvalidOperationException("Search usage ledger is invalid.");
            if (state.Used < 0 || state.Used > 1_000_000 || !System.Text.RegularExpressions.Regex.IsMatch(
                state.Month, @"^\d{4}-(0[1-9]|1[0-2])$"))
                throw new InvalidOperationException("Search usage ledger is invalid.");
            var used = state.Month == month ? state.Used : 0;
            return new SearchBudgetSnapshot(used, limit, Math.Max(0, limit - used), month);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Search usage ledger is invalid; search was stopped rather than resetting quota.", ex);
        }
    }

    private sealed record UsageState(string Month, int Used);
}
