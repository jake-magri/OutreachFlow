# OutreachFlow

**Local-first B2B opportunity discovery, source-backed research, prospect tracking and human handoff.**

OutreachFlow is a .NET 10 Blazor Server interactive web app using EF Core, ASP.NET Core Identity, SQLite, Tavily Search, bounded public HTML research, optional Playwright rendering and optional Google/Gmail OAuth. It is an **early local prototype**, not a hosted SaaS product. Simulation uses **`DryRunEmail`**. A separate, disabled-by-default Windows pilot **has completed a controlled live Gmail send-and-reply acceptance test**: one owner-approved initial inquiry, one imported genuine reply and persistent history. It is not an unattended sender. See [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md).

## Current functionality

- **Identity and workspaces:** local password login or Google sign-in, explicit link of existing identities, Google-first private workspace registration, Owner/Member membership records, local workspace switching and server-scoped prospect operations. The initial password-owner setup creates a workspace when needed.
- **Discover:** user-initiated Tavily basic search with per-workspace research profile and geography; two basic searches per click with a shared, persistent 100-requests/month default budget; indexed results are ranked and deterministically triaged into application review, inquiry review, research further or possible false match. The user decides whether to record a result.
- **Research:** read-only, up to six same-site public HTML pages, bounded requests and robot rules, SSRF and cross-host defenses, source-linked evidence, public email candidates and potential portal links. Experimental browser rendering is explicitly opt-in and may remain inconclusive.
- **Prospect workflow:** research decisions, template-based drafts, simulated sequences, owner-attested external sends, reviewed replies, contact suppression, activity history and CSV metadata export. A new **bounded batch import-to-inquiry pilot** creates drafts and simulates initial sends from one pasted string, or processes up to three explicitly owner-approved Gmail inquiries through the existing outbox; see [BATCH-INQUIRY-PILOT.md](BATCH-INQUIRY-PILOT.md).
- **Mailbox:** optional, owner-authorized Gmail `gmail.readonly` connection in Windows Development mode; encrypted local refresh token; user-initiated limited inbox review and manual classification/import. The pending issue #13 branch strengthens matches to one provider-confirmed sent thread and matching reply headers; this is still **not unattended inbox synchronization or authentication of a human sender**. A separate optional `gmail.send` consent upgrade enables explicitly approved initial inquiries; automatic sending remains off.
- **Tests:** the offline .NET smoke suite covers discovery/triage, security constraints, Gmail identity and lifecycle invariants. A separate Playwright Chromium harness now drives real authentication, prospect and workspace UI flows against disposable databases, with screenshot/trace artifacts on failure. GitHub Actions runs both suites on pull requests; see [BROWSER-ACCEPTANCE.md](BROWSER-ACCEPTANCE.md).

## Current product boundary

OutreachFlow is **research-first, not a bulk email or autonomous sales agent**. Its new batch pilot is user-triggered, capped at three rows and requires an explicit per-batch owner attestation for real sends; it does not automatically verify official company processes or run scheduled follow-ups. A search hit, public email address or unofficial link is never treated as a verified invitation to send. Once issue #13's branch is accepted, Gmail candidate matching will require a unique provider-confirmed outbound thread, reply references, the expected recipient and sender address. These signals support conversation correlation but cannot authenticate the human sender or prove contractual interest. Always review the message in Gmail and manually confirm relevance before importing it.

**Not yet implemented:** production-grade Gmail history synchronization, reliable bounce/complaint classification, buyer-first account discovery (issue #10), production operational recovery, account recovery, cloud hosting or billing. Local migration/recovery tools, provider identity and a **manually approved first Gmail send** are implemented and locally accepted. Offline provider-failure tests are not substitutes for real-world reliability evidence.

## Source layout

| Area | Responsibility |
| --- | --- |
| `OutreachFlow/Program.cs` | Startup, DI, local-only protections, service registration |
| `Models/`, `Data/` | Workspace, prospect and activity models; separate business and Identity EF contexts |
| `Services/AccountEndpoints.cs`, `GoogleSignInEndpoints.cs`, `GoogleUserProvisioner.cs` | Local login, Google identity and workspace provisioning |
| `Services/Mailbox/` | Google OAuth connection, local protected token store, Gmail review, explicit send consent, approved inquiry dispatch and reconciliation |
| `Services/Discovery/` | Tavily requests, search budget, query planning, triage, reviewed imports |
| `Services/Research/` | Safe public-site HTML analysis, browser experiment, contacts and evidence |
| `Services/OutreachService.cs` | Prospect state transitions, drafts, manual/simulated history, exports |
| `Components/Pages/` | Dashboard, prospects, discovery, settings, mailbox |
| `OutreachFlow.SmokeTests/` | Offline regression suite |
| `OutreachFlow.BrowserTests/` | Isolated Playwright browser journeys with temporary SQLite, no external providers |
| `.github/workflows/build.yml` | CI and localhost startup check |
| `.github/workflows/browser-tests.yml` | Isolated Chromium acceptance on pull requests |

## Run locally

Prerequisites: .NET 10 SDK, Visual Studio or a terminal, and a trusted development HTTPS certificate. Gmail's current connection implementation is Windows-only due to DPAPI key protection.

```powershell
git switch main
git pull --ff-only origin main
dotnet restore OutreachFlow.slnx
dotnet build OutreachFlow.slnx
dotnet run --project OutreachFlow.SmokeTests
dotnet dev-certs https --trust
dotnet run --project OutreachFlow
```

Visit `https://localhost:7117`. On a fresh database, set up the first password owner at `/account/setup`, or configure Google and use **Continue with Google** for a new private workspace. To keep an existing local account's data, sign in with its password and **link** Google under Workspaces before using Google login for that account. Never merge users automatically by email.

### Optional services

- Tavily: `dotnet user-secrets set "Tavily:ApiKey" "YOUR_KEY" --project OutreachFlow`.
- Google sign-in and Gmail: configure an OAuth 2.0 **Web application** with `https://localhost:7117/signin-google` and `https://localhost:7117/mailbox/google/callback`, then follow [MAILBOX-PILOT.md](MAILBOX-PILOT.md). Basic Google identity scopes and Gmail read access require separate consent.
- Google `gmail.readonly` is a restricted OAuth scope; a public SaaS release requires additional provider verification/security planning. An optional, separate **Grant Gmail sending permission** action requests `gmail.send` alongside the read scope; it is never granted by basic Google login or a read-only connection. `Gmail:EnableSending` defaults to `false`, and an eligible owner must still approve each initial inquiry.
- Optional browser research: see [BROWSER-RESEARCH-PILOT.md](BROWSER-RESEARCH-PILOT.md).

Secrets must remain in local development secret storage; never commit client secrets, refresh tokens, screenshots containing tokens, or local business records.

## Storage and safety

- `outreachflow.db`: workspaces, prospects, research evidence, messages and activities.
- `outreachflow.auth.db`: Identity users, Google logins, workspace memberships.
- `research-profiles/`: per-workspace configuration JSON.
- `search-usage/`: app-wide on-disk Tavily budget ledger.
- `mailbox-connections/`: encrypted workspace Gmail refresh tokens.
- `mailbox-keys/`: DataProtection key ring, protected with current-user Windows DPAPI.

**Local backup/restore and EF migration foundation (issue #16, partial):** after stopping the app, use `OutreachFlow.RecoveryCli` to snapshot both SQLite databases through SQLite's backup API and capture auxiliary local state, then verify checksums and restore into a new directory. See [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md) for commands, privacy safeguards and manual acceptance. This is **operator-invoked**, not automated/production-ready. The bundle contains sensitive business records and OAuth key material and must be encrypted and protected. DPAPI-protected keys and tokens are not portable to a different Windows account/machine without deliberate recovery or reconnect. Fresh business and Identity databases are initialized with EF migrations. Existing installations now require the recovery CLI `upgrade` command before startup to adopt the baseline and apply the provider identity migration. The command creates a verified recovery bundle first. See [MIGRATION-BASELINE.md](MIGRATION-BASELINE.md). Do not delete databases to try a new branch.

The app fails closed outside ASP.NET Core Development and refuses non-loopback requests. It is **not** safe to expose publicly or deploy using `Dockerfile.future`.

## Project management and acceptance

- [ROADMAP.md](ROADMAP.md): current milestones and definitions of done.
- [AUDIT-2026-11-11.md](AUDIT-2026-11-11.md): repository-wide code audit, confirmed findings and prioritized risks.
- [TEST-CHECKLIST.md](TEST-CHECKLIST.md): manual acceptance checklist.
- [TRIAGE-PILOT.md](TRIAGE-PILOT.md): pilot classification and manual route-approval tests.
- [BROWSER-ACCEPTANCE.md](BROWSER-ACCEPTANCE.md): one-command Playwright acceptance, isolation and CI failure artifacts.
- [GMAIL-CORRELATION-ACCEPTANCE.md](GMAIL-CORRELATION-ACCEPTANCE.md): offline Gmail payload/thread regressions, provider boundaries and optional real-provider troubleshooting.
- [BATCH-INQUIRY-PILOT.md](BATCH-INQUIRY-PILOT.md): one-input simulation and owner-approved live Gmail batch, limits and automated acceptance.
- [SCHEDULED-GMAIL-PILOT.md](SCHEDULED-GMAIL-PILOT.md): opt-in, local scheduled follow-ups, inbox safety checks, pause/resume/cancel and limitations.
- [MAILBOX-PILOT.md](MAILBOX-PILOT.md): historical Google OAuth and read-only mailbox pilot; [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md) documents the now-accepted one-message sending pilot.
- [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md): local backup, integrity verification and restore-to-new-directory procedures.
- [MIGRATION-BASELINE.md](MIGRATION-BASELINE.md): generated EF migrations, schema inspection and optional backed-up adoption.
- [GitHub issues](https://github.com/jake-magri/OutreachFlow/issues): planned work and tracked technical debt.

The historical PR-specific pilot notes describe how features were built at the time and are **not** the current product readiness checklist. The source of truth for remaining acceptance is [ROADMAP.md](ROADMAP.md), current open [GitHub issues](https://github.com/jake-magri/OutreachFlow/issues), and the latest [TEST-CHECKLIST.md](TEST-CHECKLIST.md).
