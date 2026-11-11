using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OutreachFlow.Services.Persistence;

/// <summary>
/// A local, operator-invoked recovery bundle. Never modifies source databases and
/// never overwrites a restore destination. Not a scheduled backup service.
/// </summary>
public static class LocalRecoveryBundle
{
    private const int FormatVersion = 1;
    private const long MaxAuxFileBytes = 25 * 1024 * 1024;
    private const int MaxFiles = 1000;
    private static readonly string[] Databases = ["outreachflow.db", "outreachflow.auth.db"];
    private static readonly string[] AuxiliaryDirectories =
        ["research-profiles", "search-usage", "mailbox-connections", "mailbox-keys"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record FileRecord(string RelativePath, long Bytes, string Sha256);
    public sealed record Manifest(int FormatVersion, DateTime CreatedUtc,
        IReadOnlyList<FileRecord> Files);

    public static async Task<Manifest> CreateAsync(
        string databaseRoot, string contentRoot, string destination,
        CancellationToken ct = default)
    {
        var dbRoot = Path.GetFullPath(databaseRoot);
        var appRoot = Path.GetFullPath(contentRoot);
        var output = Path.GetFullPath(destination);
        EnsureOutsideSources(output, dbRoot, appRoot);
        if (Path.Exists(output))
            throw new InvalidOperationException("Backup destination already exists; no files were overwritten.");
        foreach (var name in Databases)
        {
            var path = Path.Combine(dbRoot, name);
            if (!File.Exists(path) || IsLink(path))
                throw new InvalidOperationException("Both existing local SQLite databases are required before backup. Missing or linked: " + name);
        }

        // All work is staged beside the requested destination; a partially failed
        // bundle is never given the final destination name.
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            var records = new List<FileRecord>();
            foreach (var name in Databases)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(dbRoot, name);
                var dest = Path.Combine(staging, name);
                BackupSqlite(source, dest);
                VerifySqlite(dest);
                records.Add(await RecordAsync(dest, name, ct));
            }
            foreach (var folder in AuxiliaryDirectories)
            {
                var sourceDir = Path.Combine(appRoot, folder);
                if (!Directory.Exists(sourceDir)) continue;
                if (IsLink(sourceDir))
                    throw new InvalidOperationException("Linked auxiliary directories cannot be backed up.");
                var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories);
                foreach (var source in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsLink(source)) throw new InvalidOperationException("Linked auxiliary files cannot be backed up.");
                    var relative = Path.GetRelativePath(appRoot, source);
                    if (!AllowedRelativePath(relative))
                        throw new InvalidOperationException("Auxiliary file is outside supported recovery paths.");
                    if (new FileInfo(source).Length > MaxAuxFileBytes)
                        throw new InvalidOperationException("Auxiliary file is too large for local recovery.");
                    if (records.Count >= MaxFiles)
                        throw new InvalidOperationException("Recovery bundle has too many files.");
                    var destinationFile = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                    File.Copy(source, destinationFile, overwrite: false);
                    records.Add(await RecordAsync(destinationFile, relative, ct));
                }
            }
            records.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
            var manifest = new Manifest(FormatVersion, DateTime.UtcNow, records);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, Json), ct);
            await VerifyAsync(staging, ct);
            Directory.Move(staging, output);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Copy a verified bundle to a NEW empty destination only. Never touches the
    /// source installation or replaces an existing app database.
    /// </summary>
    public static async Task<Manifest> RestoreAsync(string bundle, string newDestination,
        CancellationToken ct = default)
    {
        var source = Path.GetFullPath(bundle);
        var destination = Path.GetFullPath(newDestination);
        EnsureOutsideSources(destination, source);
        if (Path.Exists(destination))
            throw new InvalidOperationException("Restore destination already exists; restore never overwrites it.");
        var manifest = await VerifyAsync(source, ct);
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(source, file.RelativePath);
                var dest = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: false);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, Json), ct);
            await VerifyAsync(staging, ct);
            Directory.Move(staging, destination);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static async Task<Manifest> VerifyAsync(string bundle, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(bundle);
        if (!Directory.Exists(root) || IsLink(root))
            throw new InvalidOperationException("Recovery bundle directory not found or is linked.");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath) || IsLink(manifestPath) ||
            new FileInfo(manifestPath).Length > 256_000)
            throw new InvalidOperationException("Missing, linked or oversized recovery manifest.");
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(
                await File.ReadAllTextAsync(manifestPath, ct));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Recovery manifest is invalid.", ex);
        }
        if (manifest is null || manifest.FormatVersion != FormatVersion ||
            manifest.Files is null || manifest.Files.Count is < 2 or > MaxFiles)
            throw new InvalidOperationException("Unsupported or invalid recovery manifest.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!AllowedRelativePath(file.RelativePath) ||
                !seen.Add(file.RelativePath) ||
                file.Bytes < 0 || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Recovery manifest contains an invalid path, digest or duplicate.");
            var path = Path.Combine(root, file.RelativePath);
            // Do not traverse symlinked parent directories or linked files.
            var subdir = Path.GetDirectoryName(file.RelativePath);
            if (!string.IsNullOrEmpty(subdir) &&
                IsLink(Path.Combine(root, subdir)))
                throw new InvalidOperationException("Linked backup folders are not accepted.");
            if (!File.Exists(path) || IsLink(path) ||
                new FileInfo(path).Length != file.Bytes)
                throw new InvalidOperationException("Backup file is missing or has changed: " + file.RelativePath);
            var digest = await DigestAsync(path, ct);
            if (!digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup digest mismatch: " + file.RelativePath);
        }
        if (!Databases.All(name => seen.Contains(name)))
            throw new InvalidOperationException("Both SQLite database backups are required.");
        // Reject unmanifested payloads, even if a manifest's own checksums match.
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => f != "manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(seen))
            throw new InvalidOperationException("Recovery bundle contains unlisted files.");
        foreach (var name in Databases) VerifySqlite(Path.Combine(root, name));
        return manifest;
    }

    private static bool AllowedRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(s => s is "" or "." or ".."))
            return false;
        var normalized = relative.Replace('\\', '/');
        if (Databases.Contains(normalized, StringComparer.Ordinal)) return true;
        var segments = normalized.Split('/');
        return segments.Length >= 2 && segments.All(s => s.Length <= 128) &&
            AuxiliaryDirectories.Contains(segments[0], StringComparer.Ordinal);
    }

    private static void BackupSqlite(string source, string dest)
    {
        using var origin = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dest, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        origin.Open();
        backup.Open();
        origin.BackupDatabase(backup);
    }

    private static void VerifySqlite(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (command.ExecuteScalar() is not string result || result != "ok")
            throw new InvalidOperationException("SQLite integrity_check failed for " + Path.GetFileName(database));
    }

    private static async Task<FileRecord> RecordAsync(string path, string relative, CancellationToken ct)
        => new(relative.Replace('\\', '/'), new FileInfo(path).Length, await DigestAsync(path, ct));

    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void EnsureOutsideSources(string target, params string[] roots)
    {
        var t = Path.TrimEndingDirectorySeparator(target);
        foreach (var root in roots)
        {
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (t.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(r + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recovery destination must be outside the source location.");
        }
    }
}
