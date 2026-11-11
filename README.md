# OutreachFlow — consolidated local workflow prototype

**OutreachFlow** is a .NET 10 / Blazor Server / Entity Framework Core / SQLite app for **research-first, email-only vendor outreach**. It is a local prototype built for a single owner, with configurable business context that can later support other workspaces.

> **Do not deploy this build to a publicly accessible server.** It has no user authentication, no real mailbox connection, and on-demand bounded public-website research. It deliberately refuses non-Development startup and rejects non-loopback connections. All outbound and inbound email is simulated by buttons inside the UI; no email is sent. Explicitly clicking Research company website DOES make read-only HTTP(S) requests to public sites.

## Quick start (Visual Studio 2026)

1. **Close Visual Studio first**. Extract this ZIP into **`C:\Dev`** (or another new location). The ZIP itself contains the top-level folder **`OutreachFlow`**, so the solution will be at `C:\Dev\OutreachFlow\OutreachFlow.slnx`. Do not paste or merge its contents into an old `OutreachFlow` directory; earlier files cause the reported CS1061/CS0103 mismatches.
2. Open **`OutreachFlow.slnx`** from that new folder in Visual Studio 2026. Install the **ASP.NET and web development** workload and **.NET 10 SDK**.
3. Let Visual Studio restore packages. The web project uses `Microsoft.EntityFrameworkCore.Sqlite` version `10.0.14` and pins SQLitePCLRaw `2.1.14` to avoid the old vulnerable `2.1.11` transitively resolved before. Run `dotnet list OutreachFlow/OutreachFlow.csproj package --include-transitive` if necessary to verify that `SQLitePCLRaw.lib.e_sqlite3` is not `2.1.11`.
4. Select **OutreachFlow** as startup project and start with F5. Browse the local URL shown in the output; the supplied launch profile uses `https://localhost:7117`.
5. Use Dashboard → Prospects to import prospects. Two **fictional** `.example` records are seeded on a new database. No business is contacted.
6. To exercise the first workflow: choose a prospect → manually record official-process research → generate draft → simulate initial outreach → simulate interested reply. Return to dashboard to see the warm lead.
7. To exercise the no-response workflow: simulate the configured one or two follow-ups and then press **Simulate no-response handoff**. This is **not** a warm lead. The UI shows the next due-time preview but simulation buttons intentionally bypass the clock.
8. Try **Manual follow-up & ownership** to log a call, an application submission, a note, a next action, and a date. Manual takeover blocks further simulated follow-ups.
9. Open **Settings** to change the business name, service area, contact details, templates, timezone, and cadence. Active simulated sequences keep the policy they started with.
10. From Prospects, **Export CSV** to save your pipeline metadata. Message bodies are not included.

### Run locally from CLI

```bash
dotnet restore OutreachFlow.slnx
dotnet run --project OutreachFlow/OutreachFlow.csproj --launch-profile OutreachFlow
```

### Run offline smoke tests (no mail or API keys)

```bash
dotnet run --project OutreachFlow.SmokeTests/OutreachFlow.SmokeTests.csproj
```

The smoke test uses a temporary SQLite database and a fake research provider (no HTTP requests). It exercises website research review-gating, deduplication, URL safety, portal-first routing, draft suppression, simulated sending, snapshotted cadence, the no-response queue, late replies, opt-outs, manual takeover, DST-aware weekday scheduling, and CSV formula safety.

**Verification status:** Files and the ZIP have been statically validated, but this environment lacks the .NET SDK and NuGet access. **The solution has not been compiled or run here**. Run both commands above on your Windows development machine and resolve any build/runtime issues before relying on this code.

## Consolidation notes

The supplied archive included an empty `OutreachFlow/OutreachFlow` folder and a second `OutreachFlow/OutreachFlow (2)` folder containing the real app. The second folder has been normalized to the **single** `OutreachFlow/OutreachFlow` project path required by `OutreachFlow.slnx`.

Removed from the distributable: Visual Studio `.vs` caches; build-generated `bin`/`obj` files and `*.user`; obsolete Blazor template pages (`Counter`, `Weather`, unused `NavMenu`, sample CSS/Bootstrap bundle, etc.); duplicate Dockerfile and duplicate setup notes. A single `Dockerfile.future` remains for future reference only, **not for deployment**.

The old `outreachflow.db` / `-wal` / `-shm` trio was **not** carried into this clean source archive. Examination of the uploaded database found only two demo prospects, no messages, no activities, and no research evidence; its schema also predates this version. A fresh demo database will be seeded on first run. If you have separate real data in another local database, preserve that separately and do not overwrite it.

**The original uploaded ZIP is unchanged.** Do not merge this archive into the old duplicated folder: use a fresh extraction location. No live emails are sent.

## Corrections retained

- Razor `type="date"` now uses explicit `value`/`onchange` rather than an incompatible `@bind` inference.
- Dashboard no longer dereferences a possibly null next-due timestamp.
- EF Core SQLite updated to 10.0.14 with explicit SQLitePCLRaw bundle 2.1.14 (the original 2.1.11 library is listed as vulnerable).
- **All source files and tests are included together**. Missing `BusinessCalendar`, `PlannedFollowups`, `RecordManualActivityAsync`, and `ExportCsvAsync` in a local build indicate old source files are still being used.

## Iteration 3: on-demand website research

- New **Research company website** button on prospect detail screens. Crawls up to six public same-site HTML pages on click, subject to robots.txt and safeguards.
- Saves **source links, excerpts and checked-page records** in the existing ResearchEvidence table, with all automated findings marked **unverified**.
- **Copy into review form** action assists explicit owner verification. Only reviewed outcomes can move a prospect to DraftReady or ApplicationTask.
- No EF schema changes or new NuGet dependencies; existing demo/pilot databases can be retained. Refer to `RESEARCH-SETUP.md` for patch and test instructions.

## Iteration 2 highlights

- **Four queues**: warm leads, action needed, manual outreach after nonresponse, held/closed. "No reply" is never counted as interested.
- **Research first**: portal results become manual application tasks; inquiry-eligible results produce draft emails only after a documented research outcome.
- **Manual handoff**: activity type, audit note, next action/date, and closure are now persisted.
- **Scheduling preview**: the next dry-run step is stored as a UTC instant, calculated using weekday delays and the configured timezone (including DST). This is not a BackgroundService and does not send anything.
- **Sequence snapshots**: follow-up delay and count are captured at first simulation; edits to Workspace settings affect *new* sequences only.
- **Safety restrictions**: opt-out recipients cannot be reimported by renaming their company; a recipient with prior outreach on another prospect cannot be sent a second simulated sequence; an owner note stops simulated follow-ups; no server exposure outside loopback.
- **CSV export**: pipeline metadata with spreadsheet formula-injection mitigation; no message-body export.

## Database schema note

The pilot uses EF Core `EnsureCreated()`, **not migrations**. This iteration does not change the schema from iteration 2. The schema changed between iterations 1 and 2. If you previously ran the first ZIP, **make a backup of `outreachflow.db`** and then remove the *demo* database before starting iteration 2. Do not delete real prospect data you want to keep. A migration path is required before real production data or a hosted deployment.

SQLite files are stored wherever the app's process working directory resolves `Data Source=outreachflow.db`; locate the file before resetting. Never commit SQLite files, backups, API keys, or `.env` secrets.

## What is not implemented

- Broad automated search across the public web: the current provider scans only a few HTML pages on the submitted official website and requires manual review. It may miss vendor instructions. A broader provider and PDF processing are future work.
- Live mailbox sending, reply synchronization, webhook events, deliverability, opt-outs from live inboxes, and sent-mail history.
- Actual due-time scheduling/worker, retries, delivery confirmation, locks, deduplicated event processing, or auto-transition on a timer. **The scheduling date is a preview only**.
- Owner login, production authorization, migrations, backups, secure managed secrets, alerts, production HTTPS / DNS setup, or deployment to Lightsail.
- Full contact-level deduplication by domain/branch, provider API rate limiting, legal opt-out footer, automated application submissions, phone/SMS automation, or AI-generated replies.

See `ROADMAP.md` for the next safe slices. No AWS infrastructure was provisioned and no hosting cost was incurred.
