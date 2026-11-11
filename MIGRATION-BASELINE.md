# EF Core 10 migration baseline — non-destructive adoption

This is the **second, isolated slice** of [issue #16](https://github.com/jake-magri/OutreachFlow/issues/16), after the verified local backup/restore CLI in PR #23.

## What is now in source control

- Generated (not hand-written) baseline migrations and model snapshots for both independently managed EF contexts:
  - `OutreachDbContext` → `InitialBusinessSchema`
  - `AccountDbContext` → `InitialIdentitySchema`
- A pinned `dotnet-ef` local tool and **disconnected design-time factories** (never point scaffolding at your existing `.db` files).
- An operator-only `baseline inspect` that compares both existing SQLite DDL schemas to a fresh `EnsureCreated` schema generated from the same EF models. It also checks SQLite integrity and existing migration history. A mismatch **blocks adoption** rather than guessing how to repair it.
- An explicit `baseline adopt` that requires a **new verified backup** and then inserts the current baseline migration ID into each existing DB's `__EFMigrationsHistory` table. This does **not** execute the baseline migrations' `CreateTable` code on legacy data.
- Offline tests for both paths: fresh DBs built by `MigrateAsync`, legacy `EnsureCreatedAsync` databases with real rows, history adoption, unchanged rows, migration no-ops after adoption and drift rejection.

## What startup does

- A missing or empty database is initialized from its compiled EF migrations.
- An existing database with current migration history opens as-is.
- A legacy database with no migration history can still open while the application ships only the initial migration for its context. To record the baseline, stop the app and run the backed-up adoption command below.
- Startup blocks unexpected or pending migration history on an existing database. Future schema upgrades require a reviewed apply procedure.

Each context follows these rules independently, including when one database has been adopted and the other is still legacy.

## Why this stage exists

EF Core uses `__EFMigrationsHistory` to track applied migrations. Earlier OutreachFlow builds used `EnsureCreatedAsync`, which creates tables but does not write migration history. Applying an initial migration directly against those populated legacy files would try to create tables that already exist.

To avoid data loss or a failed upgrade, the adopter first compares schema definitions, backs up all local state, then marks the matching initial migration as already applied. It refuses unknown history, unexpected tables or changed index/column definitions. A database with a **different** model is not silently upgraded: investigate and plan a separate migration.

This is a deliberately strict same-model baseline, not a generic schema-repair tool.

## Safe commands from PowerShell

From the repository root:

```powershell
git fetch origin
git switch --track origin/feature/persistence-startup-migrations
dotnet build OutreachFlow.slnx
dotnet run --project OutreachFlow.SmokeTests
```

For an **inspection only**, stop OutreachFlow and first identify the actual directory containing BOTH `outreachflow.db` and `outreachflow.auth.db`. For a typical repository-root launch, they're in the repo root:

```powershell
dotnet run --project OutreachFlow.RecoveryCli -- baseline inspect .
```

You will see each database's schema compatibility, its migration ID, and whether that version has already been recorded. Inspection is read-only. If either schema doesn't match, **do not proceed**; the tool is intentionally conservative.

For a **test adoption**, the preferred path is to first use PR #23's recovery tool to restore the last backup into a **new, disposable directory**, and then adopt that copy. The CLI expects both default SQLite filenames inside the given database directory.

```powershell
# Example: adopt a disposable database pair, NOT your active originals
dotnet run --project OutreachFlow.RecoveryCli -- baseline inspect "$HOME\OutreachFlowRestoreChecks\pilot-001"
dotnet run --project OutreachFlow.RecoveryCli -- baseline adopt "$HOME\OutreachFlowRestoreChecks\pilot-001" OutreachFlow "$HOME\OutreachFlowBackups\baseline-copy-test-001"
```

**If** the disposable copy passes and you subsequently decide to adopt the original pair, stop all app processes first, choose a **new unused backup destination outside the repository** and run:

```powershell
dotnet run --project OutreachFlow.RecoveryCli -- baseline inspect .
dotnet run --project OutreachFlow.RecoveryCli -- baseline adopt . OutreachFlow "$HOME\OutreachFlowBackups\baseline-original-001"
```

The adopter creates and verifies the backup **before** changing either SQLite file. The two files are independent transactions: if one history stamp succeeds and the other fails, keep the backup, inspect, and retry after resolving the error. Do not delete the history manually.

**Do not run `dotnet ef database update` on any populated legacy database to apply the initial migration.** The design-time factories intentionally target `:memory:` and are for scaffolding; they are not your application's configured runtime connections. Use the recovery CLI to adopt the initial baseline on populated legacy databases. Existing databases with pending schema upgrades need a separate reviewed apply procedure.

## Future migration workflow

After adoption is accepted, future schema changes should:

1. Update entity/model configuration and generate an incremental migration with the pinned `dotnet ef` tool.
2. Review its Up/Down code, SQL operations, constraints, tenant ownership implications and compatibility with existing records.
3. Test migration on both a fresh DB and a restored copy of a real legacy DB, including verifying data counts, foreign keys and history.
4. Release with a reviewed upgrade procedure for existing databases. Fresh databases are initialized from the compiled migrations.

No new FK/uniqueness constraints or implicit `WorkspaceId = 1` changes are included here. Those need a follow-on migration and historical-data cleanup checks. Issue #16 remains open.

## Merge acceptance

- [ ] CI compiles generated migrations and reports no model drift.
- [ ] Both legacy `EnsureCreated` and new `Migrate` test databases show the same expected tables and indexes.
- [ ] A legacy record and original Identity membership survive baseline adoption without changing contents.
- [ ] Unknown schema changes and unknown migrations block adoption.
- [ ] App still works normally with existing data on Windows, with no automatic schema changes.
- [ ] On a **copy** of your current local databases, `baseline inspect` reports two matching schemas; adoption/verify succeeds if you choose to test.
- [ ] You have a verified, encrypted, off-repo backup before touching any original data.
