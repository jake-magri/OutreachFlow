using OutreachFlow.Services.Persistence;

static string Usage() => """
OutreachFlow local recovery utility — no network access or database migrations.

Commands:
  backup  <database-directory> <application-content-directory> <new-bundle-directory>
  verify  <bundle-directory>
  restore <bundle-directory> <new-empty-restore-directory>

EXAMPLE from your repository root:
  dotnet run --project OutreachFlow.RecoveryCli -- backup . OutreachFlow ../OutreachFlow-backups/2026-11-11
  dotnet run --project OutreachFlow.RecoveryCli -- verify ../OutreachFlow-backups/2026-11-11
  dotnet run --project OutreachFlow.RecoveryCli -- restore ../OutreachFlow-backups/2026-11-11 ../OutreachFlow-restore-check

IMPORTANT: Stop OutreachFlow before running a backup or copying any restored
database into the live installation. Never restore over your existing files.
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
