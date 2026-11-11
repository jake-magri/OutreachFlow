# Database versions and explicit upgrades

OutreachFlow ships generated EF Core migrations for the business and Identity SQLite databases. Fresh installations initialize through migrations. Existing installations are checked at startup and must apply pending changes through the recovery CLI.

The provider identity migration adds `ProviderMessages` with mailbox, Gmail message and conversation IDs. Unique indexes protect against repeat imports within a workspace and mailbox. Foreign keys keep the workspace, prospect and recorded message consistent. The migration adds alternate keys to the existing prospect and message tables, which SQLite implements through table rebuilds.

## Upgrade an existing installation

Stop all OutreachFlow processes first. From the repository root, using the database location confirmed in the November 11 pilot, run these commands individually.

```powershell
dotnet build OutreachFlow.slnx
dotnet run --project .\OutreachFlow.SmokeTests
dotnet run --project .\OutreachFlow.RecoveryCli -- baseline inspect .\OutreachFlow
dotnet run --project .\OutreachFlow.RecoveryCli -- upgrade .\OutreachFlow .\OutreachFlow "$HOME\OutreachFlowBackups\provider-identity-001"
```

Choose a new backup directory outside the repository. The arguments are the directory containing both databases, the application's content directory, and the new backup directory. If your connection strings place the databases elsewhere, use that actual directory. The CLI expects `outreachflow.db` and `outreachflow.auth.db` together.

The upgrade command performs these steps.

1. Inspect both databases, their integrity, foreign keys and migration histories. Reconstruct each recorded schema from the compiled migrations and compare its SQLite definitions. A legacy database with no history is compared to the frozen initial migration.
2. Create and verify a recovery bundle containing both databases and auxiliary local state.
3. Record the initial baseline for compatible legacy databases.
4. Apply pending migrations independently to both contexts and verify the resulting schemas.

The databases use independent transactions. If the command stops after one file advances, preserve the backup, address the reported problem and rerun with a new backup destination. Inspection supports a pair with different recorded versions. Unknown history, schema drift and foreign-key violations stop the operation for investigation.

After success, run these commands individually.

```powershell
dotnet run --project .\OutreachFlow.RecoveryCli -- baseline inspect .\OutreachFlow
dotnet run --project .\OutreachFlow
```

Both inspection results should report `compatible=True, adopted=True`. `baseline inspect` verifies the schema at the recorded version. Startup also checks that all compiled migrations have been applied.

## Existing Gmail records

Existing message text and `gmail:<id>` activity markers are preserved. Those records lack verified mailbox and conversation identity, so the migration leaves structured identity empty for historical messages. The importer continues checking legacy markers throughout the workspace to avoid reimporting them onto another prospect.

New reviewed imports store a `ProviderMessages` row linked to the existing message history. Mailbox identity uses the Gmail account address, with case-insensitive comparison, so reconnecting OAuth preserves duplicate detection. Sender-based candidate matching still requires owner review. Conversation IDs are stored for subsequent correlation work.

## Recovery and validation

Recovery bundles contain customer data and OAuth key material. Protect and encrypt them. Windows DPAPI-bound credentials may require reconnection on another account or machine. See [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md).

For a rehearsal, restore a verified bundle to a new directory, then run `upgrade` against that copy with another new backup destination. Restore also puts auxiliary folders in the restored directory, so supply it as both the database and content directory when rehearsing an upgrade of that restored installation.

If recovery is needed, stop the app and restore the pre-upgrade bundle into a new directory. Verify its contents and use the matching application version before intentionally replacing the active installation. Keep the original files and recovery bundle available while diagnosing a failed upgrade.

Automated coverage exercises legacy baseline adoption, direct upgrade from restored legacy files, mixed-version retry, preserved workspace/Identity/Gmail history, fresh creation, schema-drift rejection and provider import concurrency. Local acceptance should confirm existing sign-in, workspace records, the previously imported reply and a new reviewed Gmail reply.

## Baseline-only commands

`baseline adopt <database-directory> <application-content-directory> <new-backup-directory>` remains available to record an initial baseline with a verified backup. Use `upgrade` to apply the provider identity migration as well.

Design-time factories target in-memory databases for scaffolding. Use the recovery CLI to apply changes to the local installation. Future migrations should be generated with the pinned EF tool, reviewed for data preservation, and tested against both fresh and upgraded databases.

## Approved Gmail dispatch upgrade

The `ApprovedGmailDispatch` migration adds the durable initial-inquiry outbox. Use the same backup-first `upgrade` command with a new backup directory before starting this version against an existing installation. See [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md). The upgrade itself does not enable sending or change Google grants.
