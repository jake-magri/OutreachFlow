# OutreachFlow

**Research-first opportunity discovery, qualification, outreach and human handoff.**

OutreachFlow is a .NET 10 Blazor Web App with EF Core and local SQLite databases. The product is intended to reduce the repetitive work between finding a business opportunity and reaching a meaningful outcome—not merely to send bulk cold email.

## Product blueprint

**One engine, configurable business workspaces.** A workspace provides its own business profile, service keywords, geographic preferences, process requirements, contact rules, email templates and follow-up limits. Initial use cases:

- **Magri Exterior Services:** find commercial fleet-service clients, approved vendor/subcontractor programs, bids and partnership contacts; research each company's process before outreach.
- **Independent software services:** find businesses seeking outsourced .NET / web / automation development, subcontracting, RFPs and IT vendor onboarding. This is **contracting/business development**, not a W-2 job application bot.

Future customers can use the same engine with different service niches. A generic visual workflow builder, SaaS billing and automated job application form submissions are **not** current priorities.

### Intended end-to-end workflow

1. **Discover:** import companies manually now; later add terms-compliant web-search providers and keyword/geography filters. Sources may be search APIs, user lists or approved vertical integrations.
2. **Research:** visit a bounded set of permitted public pages; extract source-backed process signals and published business contact candidates. Evaluate requirements relevant to the workspace.
3. **Decide:** a *verified* official application/portal means **human application handoff**. Where no applicable process is found in the checked sources and a recipient is approved, use the inquiry workflow. **Unknown and inconclusive are not the same as absent.**
4. **Outreach:** with an authorized real mailbox and an explicitly approved campaign, send a legitimate business inquiry and at most the configured number of follow-ups, within policy and compliance limits. **Not yet automated or connected.**
5. **Monitor:** reconcile sent messages and actual replies, stop on opt-out/bounce/negative response/owner takeover; handle ambiguous replies conservatively.
6. **Handoff:** surface warm leads, application tasks, unanswered conversations and uncertain research for human action. Retain an audit trail and source evidence.

Never assume a person has consented or that a contact address is appropriate simply because it was discovered publicly. No anti-bot circumvention, mass scraping of restricted websites or unattended form submission.

## Current implemented features (main + this research branch)

- Identity-backed first-owner sign-in, multiple local workspaces, and membership-scoped prospects and settings.
- Manual import, local four-queue dashboard, read-only bounded site scan, evidence review, configurable inquiry templates, simulated outbound/reply sequences, cadence previews, manual tasks, CSV export, and suppression checks.
- **This research branch:** visits relevant contact pages, extracts public HTML/mailto email candidates with page provenance, and offers **Approve as recipient** after manual verification. Possible vendor links remain unverified until the owner records a sourced process decision. No database schema change.
- **Separate draft PR #2:** human-confirmed real sends and manually recorded replies; that work is **not yet in main** and is not bundled into this branch.

**Not yet implemented:** automatic keyword web-search sourcing, reliable vendor-process classification without human review, PDF / third-party portal research, live mailbox integration, automatic sending or reply monitoring, durable scheduled jobs, production-grade migrations/backups, and public multi-tenant hosting.

## Run locally

Prerequisites: .NET 10 SDK, Git, and Windows/Visual Studio or another supported .NET environment.

```powershell
git switch main
git pull --ff-only origin main
dotnet restore OutreachFlow.slnx
dotnet build OutreachFlow.slnx
dotnet run --project OutreachFlow.SmokeTests
dotnet run --project OutreachFlow
```

Open **https://localhost:7117** (the project also listens on HTTP port 5117, which redirects to HTTPS). For a fresh local installation, use **/account/setup** to register the first owner; subsequent access is through **/account/login**. The setup route is intentionally unavailable after an account exists. If the HTTPS development certificate is untrusted: `dotnet dev-certs https --trust`.

To test this research change after CI passes, use `git fetch origin` and `git switch --track origin/feature/research-triage-contact-discovery` (or `git switch feature/research-triage-contact-discovery` if already present).

**Do not deploy this prototype publicly.** It fails closed outside Development and blocks non-loopback requests. The app performs only on-demand read-only website checks when explicitly requested. The existing sending controls are simulations. Do not enter real mail provider secrets until a reviewed connection is implemented.

## Local storage

`outreachflow.db` stores workspaces, prospects, research evidence, simulated messages and activities. `outreachflow.auth.db` separately holds ASP.NET Core Identity users and workspace memberships. Both are relative SQLite paths created where the app runs. Both databases currently use EF Core `EnsureCreated()`, not production migrations.

Stop the process and **back up both files plus any WAL/SHM companions** before changing branches or upgrading. Git excludes local `*.db` data. There is no hosted database, running background worker, or AWS spend.

## Development and safety

- Run offline tests with `dotnet run --project OutreachFlow.SmokeTests`; GitHub Actions builds and smoke-tests pull requests.
- Use a dedicated PR branch, have CI pass, then exercise the workflow in a browser before merging.
- Source-backed research is provisional and can contain mistakes; robots restrictions, private-network blocks, read limits and same-site restrictions are intentional.
- Production work still requires schema migrations, backup/restore tests, true tenant-level authorization review, secrets handling, email consent/suppression controls, idempotent jobs, monitoring, and recovery.

See [ROADMAP.md](ROADMAP.md), [RESEARCH-SETUP.md](RESEARCH-SETUP.md) (historical iteration notes), and [MULTI-WORKSPACE-SETUP.md](MULTI-WORKSPACE-SETUP.md).
