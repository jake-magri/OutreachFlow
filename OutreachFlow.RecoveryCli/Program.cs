using OutreachFlow.Services.Persistence;

static string Usage() => """
OutreachFlow local recovery + explicit EF migration baseline utility — no network access.

Commands:
  backup  <database-directory> <application-content-directory> <new-bundle-directory>
  verify  <bundle-directory>
  restore <bundle-directory> <new-empty-restore-directory>
  baseline inspect <database-directory>
  baseline adopt <database-directory> <application-content-directory> <new-backup-directory>

EXAMPLE from your repository root:
  dotnet run --project OutreachFlow.RecoveryCli -- backup . OutreachFlow ../OutreachFlow-backups/2026-11-11
  dotnet run --project OutreachFlow.RecoveryCli -- verify ../OutreachFlow-backups/2026-11-11
  dotnet run --project OutreachFlow.RecoveryCli -- restore ../OutreachFlow-backups/2026-11-11 ../OutreachFlow-restore-check

IMPORTANT: Stop OutreachFlow before taking a consistent backup, adopting
a migration baseline, or copying any restored database into the live installation.
Baseline adoption requires a new verified backup first; it only creates the EF
migration-history table, and never replays CREATE TABLE over existing records.
Never restore over your existing files.
This tool includes both SQLite DBs and auxiliary local state. It contains
private customer data and Google OAuth material; encrypt and protect it.
Windows DPAPI-bound mailbox tokens may require reconnect after a move.
""";

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine(Usage());
        return;
    }

    LocalRecoveryBundle.Manifest bundle;
    switch (args[0].ToLowerInvariant())
    {
        case "backup" when args.Length == 4:
            bundle = await LocalRecoveryBundle.CreateAsync(args[1], args[2], args[3]);
            Console.WriteLine($"Recovery bundle created with {bundle.Files.Count} verified files.");
            break;
        case "verify" when args.Length == 2:
            bundle = await LocalRecoveryBundle.VerifyAsync(args[1]);
            Console.WriteLine($"Recovery bundle verified: {bundle.Files.Count} files, both databases pass integrity_check.");
            break;
        case "restore" when args.Length == 3:
            bundle = await LocalRecoveryBundle.RestoreAsync(args[1], args[2]);
            Console.WriteLine($"Recovery bundle restored to NEW destination: {bundle.Files.Count} verified files.");
            Console.WriteLine("Keep the restored data separate until you intentionally test recovery.");
            break;
        case "baseline" when args.Length == 3 &&
            args[1].Equals("inspect", StringComparison.OrdinalIgnoreCase):
            var inspected = LegacyMigrationBaseline.Inspect(args[2]);
            foreach (var state in inspected)
            {
                Console.WriteLine($"{state.File}: compatible={state.SchemaMatches}, adopted={state.AlreadyAdopted}, migration={state.MigrationId}");
                Console.WriteLine("  " + state.Detail);
            }
            if (inspected.Any(x => !x.SchemaMatches))
            {
                Console.Error.WriteLine("Schema mismatch. Baseline adoption is blocked.");
                Environment.ExitCode = 1;
            }
            break;
        case "baseline" when args.Length == 5 &&
            args[1].Equals("adopt", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine("Validating both existing schemas and creating a verified backup first...");
            var adopted = await LegacyMigrationBaseline.AdoptAsync(args[2], args[3], args[4]);
            foreach (var state in adopted)
                Console.WriteLine($"{state.File}: initial migration {state.MigrationId} recorded, existing tables unchanged.");
            Console.WriteLine("Baseline adopted. Restart and inspect the app; do not delete your backup.");
            break;
        default:
            Console.Error.WriteLine(Usage());
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or
    UnauthorizedAccessException or IOException or ArgumentException or
    Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine("Recovery operation failed: " + ex.Message);
    Environment.ExitCode = 1;
}
