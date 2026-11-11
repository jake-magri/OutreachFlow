using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OutreachFlow.Data;

namespace OutreachFlow.Services.Persistence;

/// <summary>
/// Explicit baseline adoption and backup-first upgrades for existing databases.
/// Adoption records history. Incremental migrations may rebuild existing tables.
/// Invoked by the recovery CLI while the application is stopped.
/// </summary>
public static class LegacyMigrationBaseline
{
    private const string BusinessFile = "outreachflow.db";
    private const string AccountsFile = "outreachflow.auth.db";
    private const string Version = "10.0.14";

    public sealed record DatabaseStatus(string File, string MigrationId,
        bool SchemaMatches, bool AlreadyAdopted, string Detail);

    public static IReadOnlyList<DatabaseStatus> Inspect(string databaseDirectory)
    {
        var directory = Path.GetFullPath(databaseDirectory);
        return [
            InspectOne(Path.Combine(directory, BusinessFile), isIdentity: false),
            InspectOne(Path.Combine(directory, AccountsFile), isIdentity: true)
        ];
    }

    /// <summary>
    /// Take a verified backup first. Then add migration-history metadata without
    /// applying the baseline's CREATE TABLE statements to existing tables.
    /// Two SQLite databases are separate transactions: a failure after stamping
    /// the first can be safely retried, but requires operator attention.
    /// </summary>
    public static async Task<IReadOnlyList<DatabaseStatus>> AdoptAsync(
        string databaseDirectory, string applicationContentDirectory,
        string newBackupDirectory, CancellationToken ct = default)
    {
        var states = Inspect(databaseDirectory);
        if (states.Any(x => !x.SchemaMatches))
            throw new InvalidOperationException(
                "Existing schema differs from its recorded migration version; adoption refused. " +
                string.Join("; ", states.Where(s => !s.SchemaMatches)
                    .Select(s => s.File + ": " + s.Detail)));

        await LocalRecoveryBundle.CreateAsync(databaseDirectory, applicationContentDirectory,
            newBackupDirectory, ct);

        var root = Path.GetFullPath(databaseDirectory);
        foreach (var state in states.Where(s => !s.AlreadyAdopted))
        {
            ct.ThrowIfCancellationRequested();
            Stamp(Path.Combine(root, state.File), state.MigrationId);
        }

        var final = Inspect(databaseDirectory);
        if (final.Any(x => !x.SchemaMatches || !x.AlreadyAdopted))
            throw new InvalidOperationException(
                "One or more database baselines were not adopted. Keep the backup, inspect, and retry.");
        return final;
    }

    private static DatabaseStatus InspectOne(string path, bool isIdentity)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path) || IsLink(path))
            throw new InvalidOperationException("Existing database file is missing or linked: " + name);
        using var current = Open(path, SqliteOpenMode.ReadOnly);
        VerifySqlite(current, name);

        var migrationId = InitialMigration(isIdentity);
        var history = ReadHistory(current);
        using var expected = new SqliteConnection("Data Source=:memory:");
        expected.Open();
        using (DbContext context = CreateContext(expected, isIdentity))
        {
            var known = context.Database.GetMigrations().ToArray();
            if (!history.Select(h => h.Migration).SequenceEqual(known.Take(history.Count)) ||
                history.Count > known.Length)
                throw new InvalidOperationException(name + " contains unknown or incomplete migration history.");
            // Reconstruct the version actually recorded on disk. Legacy databases
            // are compared to the frozen initial migration, even as the model evolves.
            context.GetService<IMigrator>().Migrate(history.LastOrDefault().Migration ?? migrationId);
        }
        var realSchema = Schema(current);
        var expectedSchema = Schema(expected);
        var matches = realSchema.SequenceEqual(expectedSchema, StringComparer.Ordinal);
        var detail = matches ? "Schema matches its recorded migration version (initial baseline for legacy files)."
            : SchemaDifference(realSchema, expectedSchema);
        return new DatabaseStatus(name, migrationId, matches, history.Count > 0, detail);
    }

    private static string InitialMigration(bool isIdentity)
    {
        // Generated migration ID is discovered from the compiled migration assembly,
        // not guessed or accepted as user-supplied input.
        using DbContext context = isIdentity
            ? new AccountDbContext(new DbContextOptionsBuilder<AccountDbContext>()
                .UseSqlite("Data Source=:memory:").Options)
            : new OutreachDbContext(new DbContextOptionsBuilder<OutreachDbContext>()
                .UseSqlite("Data Source=:memory:").Options);
        var ids = context.Database.GetMigrations().ToArray();
        var expectedSuffix = isIdentity ? "_InitialIdentitySchema" : "_InitialBusinessSchema";
        if (ids.Length == 0 || !ids[0].EndsWith(expectedSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Expected a compiled initial migration for " +
                (isIdentity ? "Identity" : "business") + " context. Migration baseline is not ready.");
        return ids[0];
    }

    private static DbContext CreateContext(SqliteConnection connection, bool isIdentity) =>
        isIdentity
            ? new AccountDbContext(new DbContextOptionsBuilder<AccountDbContext>().UseSqlite(connection).Options)
            : new OutreachDbContext(new DbContextOptionsBuilder<OutreachDbContext>().UseSqlite(connection).Options);

    public static async Task UpgradeAsync(string databaseDirectory,
        string applicationContentDirectory, string newBackupDirectory, CancellationToken ct = default)
    {
        // Inspect both files and back up the entire installation before stamping
        // either baseline or applying any incremental migration.
        await AdoptAsync(databaseDirectory, applicationContentDirectory, newBackupDirectory, ct);
        foreach (var file in new[] { BusinessFile, AccountsFile })
        {
            using var connection = Open(Path.Combine(Path.GetFullPath(databaseDirectory), file),
                SqliteOpenMode.ReadWrite);
            using var context = CreateContext(connection, file == AccountsFile);
            await context.Database.MigrateAsync(ct);
        }
        if (Inspect(databaseDirectory).Any(s => !s.SchemaMatches))
            throw new InvalidOperationException("Post-upgrade schema verification failed. Keep the backup and review recovery.");
    }

    private static IReadOnlyList<string> Schema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__EFMigrations%'
              AND tbl_name NOT LIKE '__EFMigrations%'
            ORDER BY type, name
            """;
        using var rows = command.ExecuteReader();
        var result = new List<string>();
        while (rows.Read())
        {
            var sql = Regex.Replace(rows.GetString(3), @"\s+", " ").Trim();
            result.Add(string.Join("|", rows.GetString(0), rows.GetString(1),
                rows.GetString(2), sql));
        }
        return result;
    }

    private static string SchemaDifference(IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
    {
        string Key(string row) => string.Join("|", row.Split('|').Take(3));
        var a = actual.Select(Key).ToHashSet(StringComparer.Ordinal);
        var e = expected.Select(Key).ToHashSet(StringComparer.Ordinal);
        var missing = e.Except(a).Take(5).ToArray();
        var unexpected = a.Except(e).Take(5).ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
            return "Missing schema objects: " + string.Join(",", missing) +
                   "; unexpected objects: " + string.Join(",", unexpected);
        return "Table/index/constraint SQL differs from expected schema; no changes made.";
    }

    private static List<(string Migration, string ProductVersion)> ReadHistory(
        SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name = '__EFMigrationsHistory'
                """;
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory"
            ORDER BY "MigrationId"
            """;
        using var rows = command.ExecuteReader();
        var result = new List<(string Migration, string ProductVersion)>();
        while (rows.Read()) result.Add((rows.GetString(0), rows.GetString(1)));
        return result;
    }

    private static void Stamp(string path, string migrationId)
    {
        // Reinspect immediately before writing metadata. The caller must stop
        // all application writers throughout adoption for cross-file consistency.
        var isIdentity = Path.GetFileName(path) == AccountsFile;
        var preflight = InspectOne(path, isIdentity);
        if (!preflight.SchemaMatches)
            throw new InvalidOperationException("Schema changed since preflight; adoption stopped.");
        if (preflight.AlreadyAdopted) return;

        using var conn = Open(path, SqliteOpenMode.ReadWrite);
        using var tx = conn.BeginTransaction();
        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """;
            create.ExecuteNonQuery();
        }
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $version)
                """;
            insert.Parameters.AddWithValue("$migrationId", migrationId);
            insert.Parameters.AddWithValue("$version", Version);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void VerifySqlite(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        if (cmd.ExecuteScalar() is not string value || value != "ok")
            throw new InvalidOperationException("SQLite integrity check failed for " + name);
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using var violations = cmd.ExecuteReader();
        if (violations.Read())
            throw new InvalidOperationException("SQLite foreign-key check failed for " + name);
    }
}
