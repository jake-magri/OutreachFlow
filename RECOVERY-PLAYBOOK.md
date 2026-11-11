# OutreachFlow local recovery foundation

This is the **first, intentionally non-destructive slice** of GitHub issue [#16](https://github.com/jake-magri/OutreachFlow/issues/16). It provides operator-run backups and verified restores of the local state. It **does not** introduce EF Core migrations, schema changes, transaction coordination between the two databases, or production hosting. Do not close #16 on the strength of this PR alone.

## What is preserved

The recovery command snapshots both local SQLite files using SQLite's own online backup API, rather than naively copying a database file while its write-ahead log (WAL) may contain uncheckpointed changes:

- `outreachflow.db` — business workspaces, prospects, source evidence, messages and activity history.
- `outreachflow.auth.db` — Identity users, Google external login associations and memberships.

It also copies existing, flat local auxiliary state from the app's **content root**:

- `research-profiles/` — per-workspace keyword profiles
- `search-usage/` — search provider request budget ledger
- `mailbox-connections/` — locally protected Google refresh tokens
- `mailbox-keys/` — the ASP.NET Core DataProtection key ring

A `manifest.json` records the snapshot's UTC timestamp, file lengths and SHA-256 hashes. Verification checks each file against the manifest and runs SQLite `PRAGMA integrity_check` on both DB snapshots.

**SHA-256 checks detect accidental changes after the manifest was made. A manifest in the same folder is not a digital signature and does NOT protect against a malicious attacker who can rewrite the entire bundle.** Store the bundle in a trusted, access-controlled, encrypted location.

## Before you back up your actual workspace

1. Stop the OutreachFlow server and any other processes accessing its databases. This is the simplest way to get a coherent snapshot **across both different databases and JSON/token files**. The backup API makes each individual SQLite file consistent, but does not make the entire multi-file bundle a single atomic snapshot if any writers remain active.
2. Make sure you have checked out the PR feature branch and built the solution. Do not delete or reset your existing databases.
3. Confirm the working directory where the app stores `outreachflow.db` and `outreachflow.auth.db`. When launching from the repository root with `dotnet run --project OutreachFlow`, that is ordinarily the repository root. The auxiliary folders are beneath `OutreachFlow/` (the app content root).
4. Choose a new **outside-the-repository** backup directory. The tool refuses existing destinations and destinations nested under either source directory. Choose a location with restricted permissions and disk encryption.
5. Do not run `restore` against your working project. Restore always creates a new directory and refuses overwriting any existing destination.

## Example commands — from the repository root in PowerShell

Run the following only when your database files are directly inside the repository root. If you use custom connection strings or a different working directory, substitute the actual database directory. The tool expects the two default database filenames.

```powershell
git switch feature/persistence-recovery-foundation
git pull --ff-only
dotnet build OutreachFlow.slnx
dotnet run --project OutreachFlow.SmokeTests

# Check the database locations before backing up (don't delete anything):
Get-ChildItem . -Filter "outreachflow*.db"
Get-ChildItem .\OutreachFlow -Directory | Where-Object { $_.Name -like "mailbox-*" -or $_.Name -like "research-*" -or $_.Name -like "search-*" }

# Use a never-before-used backup directory outside the repository:
dotnet run --project OutreachFlow.RecoveryCli -- backup . OutreachFlow "$HOME\OutreachFlowBackups\pilot-001"

# Verify again independently:
dotnet run --project OutreachFlow.RecoveryCli -- verify "$HOME\OutreachFlowBackups\pilot-001"

# Restore to a disposable NEW folder (without touching the running installation):
dotnet run --project OutreachFlow.RecoveryCli -- restore "$HOME\OutreachFlowBackups\pilot-001" "$HOME\OutreachFlowRestoreChecks\pilot-001"
```

For a real recovery scenario, stop the server, establish the intended application/database locations, and **manually** decide which restored files should replace which files only after verifying that you have the right data and a separate copy of the originals. The CLI deliberately does not offer an in-place restore command.

## Important limits

- **Sensitive data:** The SQLite snapshots include personal and business data; the mailbox state/key ring may allow authorized Gmail access on its original Windows user account. Protect the full bundle with encryption and restrictive access permissions. Never commit it to GitHub, email it or upload it to an untrusted service.
- **Portability:** Gmail/DataProtection keys use Windows **current-user DPAPI**. A backup restored to a different account or machine may contain token files that cannot be decrypted. The safe recovery route is to reconnect Gmail via Google OAuth; it is not safe to promise portable token restoration.
- **No user secrets:** ASP.NET Core development `user-secrets` (Google client secret, Tavily key) are stored separately outside the repository and are intentionally *not* in this bundle. Reconfigure them securely on recovery. Do not copy plaintext developer secrets into a Git repo or a public backup.
- **No exact cross-file atomicity during writes:** Backup the full app while stopped. `SQLite BackupDatabase` makes each DB internally consistent, but concurrent app writes can still produce mismatched identity/business/profile/token state.
- **No scheduled backups, cloud storage or rotation:** This is a local CLI and verification gate. An operator must manage retention, redundant copies and a recovery drill.
- **Flat auxiliary directories:** Nested directories or filesystem links are rejected. The utility refuses restoring to a preexisting destination. Any corruption/integrity failure stops the operation instead of silently falling back.
- **No EF schema upgrade:** Existing `EnsureCreatedAsync` startup behavior remains unchanged in this PR. The next issue #16 slice must establish a migration baseline on disposable copies of actual legacy data, then introduce constraints only after proving a lossless upgrade.

## Acceptance for this PR

- [ ] GitHub Actions builds all three projects and the offline smoke suite passes, including bundle verification and tampering tests.
- [ ] Local Windows CLI backup succeeds with the real app fully stopped; output has both SQLite DBs, applicable auxiliary files, and a manifest.
- [ ] Verify succeeds and restore to a **new** destination succeeds; inspect row counts/important records using a separate read-only check or the recovery copy.
- [ ] Existing local app still starts and Google sign-in, workspace switching, prospect history and Gmail connection still work.
- [ ] Bundle contents are stored safely off-repository, do not appear in `git status`, and no actual credentials are pasted into review comments.
- [ ] The operator understands that the OAuth client secrets must be reconfigured separately and cross-machine Gmail token recovery may require reconnect.
