# Multi-workspace identity foundation (development preview)

This branch begins the user-account and workspace-isolation work without rewriting your existing `outreachflow.db`.

## What's included
- ASP.NET Core Identity email/password login (first-owner setup at `/account/setup`).
- One shared database for outreach records and an **independent** local SQLite `outreachflow.auth.db` for Identity and workspace memberships.
- Workspace creation, switching and ownership via `/account/workspaces`.
- Protected Blazor pages and CSV export, including a server-side loopback guard.
- Every existing OutreachService read or write now constrains prospects or workspace settings to the authenticated workspace ID. Related child data is accessed only after the parent prospect authorization check.
- Offline smoke tests exercising denied cross-workspace reads/writes, scoped exports and independent imports.
- GitHub Actions .NET 10 restore/build/smoke test workflow.

## First local run
1. **Back up your existing `outreachflow.db`**, including any WAL/SHM files when the app is stopped. This branch does not intentionally modify its schema.
2. Checkout the PR branch and run `dotnet restore OutreachFlow.slnx`, `dotnet build OutreachFlow.slnx`, and `dotnet run --project OutreachFlow.SmokeTests`.
3. Start the app via `dotnet run --project OutreachFlow`; browse `https://localhost:7117/account/setup`.
4. Create the **first local owner** account with a strong unique password. It is assigned to the existing workspace.
5. Use `Switch business` to create/select a second business. Its prospect list should start empty.
6. Sign out and sign back in via `/account/login`.

**Important:** account setup works only while there are no users, and the app is still restricted to local Development mode. No real email is sent.

## Boundaries and limitations
- **Not production-ready.** The auth database currently uses `EnsureCreated`, not versioned EF migrations. Neither database has backups/restore automation. Account recovery, invitations, multi-member administration, email verification, and password resets have not been implemented.
- Workspace creation is owner-self-service only; there is no public sign-up after the first owner.
- An authenticated user is permitted only when an explicit `WorkspaceMembership` row matches the selected `ActiveWorkspaceId`. Workspace permissions are not inferred from a client-supplied ID.
- The original business database is not automatically upgraded to migrations. The next release should establish migrations with a non-destructive baseline, plus restore tests.
- Live sending, provider synchronization, scheduled jobs, and remote deployment **remain disabled**.
- This branch was edited remotely; **do not merge until CI passes and you run the local smoke tests**.

## Future build order
1. Review CI results and fix any build or auth-state issues.
2. Implement EF Core migrations for both databases and a tested backup/restore procedure.
3. Introduce normalized contact + suppression records with deliberate cross-workspace rules.
4. Add invitation/member management, workspace-aware operations for future background jobs, and audit history.
5. Add production security assessment before AWS hosting.

## Security note
A global opt-out applies to the sending context that made contact; current simulated suppression remains scoped to a workspace. Real delivery should use explicit sender/recipient policies and must never bypass opt-outs.
