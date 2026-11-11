using System.Text.Json;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services;

/// <summary>
/// Local-only, separate JSON files scoped by a server-resolved workspace ID.
/// Existing EF Core databases and models are not modified.
/// </summary>
public interface IResearchProfileStore
{
    Task<ResearchProfile> LoadAsync(int workspaceId, CancellationToken ct = default);
    Task SaveAsync(int workspaceId, ResearchProfile profile, CancellationToken ct = default);
}

public sealed class JsonResearchProfileStore : IResearchProfileStore
{
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonResearchProfileStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<ResearchProfile> LoadAsync(int workspaceId, CancellationToken ct = default)
    {
        var path = PathFor(workspaceId);
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return ResearchProfileCatalog.Default();
            var info = new FileInfo(path);
            if (info.Length > 16_384)
                throw new InvalidOperationException("Saved research profile exceeds the supported file size. Review it manually.");
            try
            {
                await using var stream = File.OpenRead(path);
                var profile = await JsonSerializer.DeserializeAsync<ResearchProfile>(stream, JsonOptions, ct);
                return ResearchProfile.Validate(profile!);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException("Saved research profile is invalid. Restore its backup or repair it; defaults were not substituted.", ex);
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(int workspaceId, ResearchProfile profile, CancellationToken ct = default)
    {
        var path = PathFor(workspaceId);
        var validated = ResearchProfile.Validate(profile);
        var json = JsonSerializer.Serialize(validated, JsonOptions);
        if (json.Length > 16_384)
            throw new InvalidOperationException("Research profile is too large.");
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, "." + workspaceId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temp, json, ct);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally { gate.Release(); }
    }

    private string PathFor(int workspaceId)
    {
        if (workspaceId < 1) throw new InvalidOperationException("Invalid workspace.");
        return Path.Combine(directory, $"workspace-{workspaceId}.json");
    }
}
