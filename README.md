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

## Configurable research profiles (PR #5)

Open **Settings → Website research profile** to choose a preset or edit priority terms, application/process terms, and excluded phrases. Terms are **literal case-insensitive phrases**, not regular expressions. Process terms prioritize likely official application instructions; priority terms reflect the business niche; exclusions demote irrelevant pages. The contact-extraction regex and network-safety checks remain shared and non-configurable.

Built-in starting profiles include general B2B supplier research, fleet services, construction contracting, independent software contracting, junior developer employment research, and delivery driving. Employment profiles provide **research triage only**, not automated applications or messages. The scanner remains a six-page, read-only crawl of a company website supplied by the owner. No Google/LinkedIn/Indeed search API is connected.

Profiles are saved per workspace as **local JSON files** in `research-profiles/workspace-<id>.json`, ignored by Git. This intentionally requires **no EF migration** and does not change either existing SQLite database. Save files along with both SQLite databases when backing up. Invalid/corrupt profile files fail closed rather than silently resetting to defaults.

## Experimental JavaScript-rendered research (PR #6)

The normal **Research company website** action remains unchanged. A separate **Render with browser (experimental)** button can attempt a three-second JavaScript render on pages that returned insufficient HTML content. The browser uses a fresh isolated context and same-site, GET-only resource interception routed through the existing protected C# fetcher. It is deliberately not an anti-bot bypass; blocked/challenge pages remain manual review.

Local Chromium installation is required. See [BROWSER-RESEARCH-PILOT.md](BROWSER-RESEARCH-PILOT.md) for Windows setup, comparison tests, security limits and the City of Charlotte supplier-registration pilot. No new SQLite tables or AWS resources are introduced.

## Search-index discovery (PR #7)

The **Discover** page adds **on-demand Tavily basic web searches** using each workspace's configured research profile and service area. No LLM or agent API is invoked; search queries are generated deterministically. Results are ranked by keyword relevance, deduplicated and URL-screened, and shown as **unverified index candidates**. The owner must inspect a result and click **Add to prospects for verification**; importing never authorizes an email or classifies an official process. The existing research scanner and manually verified process selection remain the next steps.

**Operator pays for and controls search; workspace users never enter API keys.** Local development uses a free Tavily Researcher account (currently 1,000 API credits/month; basic search costs one credit, subject to provider policy). Search is disabled when the server does not have a key. Configure it in .NET development user secrets, not Git:

```powershell
dotnet user-secrets set "Tavily:ApiKey" "<your operator-only key>" --project OutreachFlow
dotnet run --project OutreachFlow
```

Treat your key as a secret: command-line values can enter shell history; use trusted local credential-entry procedures. An alternative is a process-only `TAVILY_API_KEY` environment variable (do not commit `.env`, keys or screenshots). The server does not return the key to the browser.

**Cost controls:** one click makes at most two basic searches and requests up to five results each. The application reserves each request against a shared, durable UTC-month ledger *before* making the provider call. The default cap is 100 attempts/month across all workspaces; the operator may set `Search:MonthlyRequestLimit` (1–1,000). Failed requests count conservatively, and a corrupt usage ledger **blocks searches rather than silently resetting the budget**. `search-usage/search-requests.json` is local, gitignored, and should be backed up with your SQLite DB and `research-profiles` folder. This is a local single-process prototype; distributed production billing and per-tenant quotas require further work.

**Provider content is ephemeral:** titles and snippets are displayed for review but not saved as evidence. When importing, only the URL, truncated title and an unverified origin label are placed into the existing prospect record. Do not treat relevance scores as source authenticity, contract availability or approval to contact the organization. A blocked live site remains **inconclusive**, even if indexed results are visible.

See [DISCOVERY-PILOT.md](DISCOVERY-PILOT.md) for acceptance tests, metrics and provider constraints. No new database tables, migrations, AWS services or LLM subscriptions are introduced.

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

## Workspace contact defaults

New workspace model instances no longer contain any specific person's email address, phone number, or business identity. Set the correct sender, service, location and outreach templates for each workspace in **Settings** before using it. Existing records in local SQLite are unaffected by changes to C# model defaults. No data reset or EF migration is required.

## Local storage

`outreachflow.db` stores workspaces, prospects, research evidence, simulated messages and activities. `outreachflow.auth.db` separately holds ASP.NET Core Identity users and workspace memberships. Both are relative SQLite paths created where the app runs. Both databases currently use EF Core `EnsureCreated()`, not production migrations.

Stop the process and **back up both files plus any WAL/SHM companions** before changing branches or upgrading. Git excludes local `*.db` data. There is no hosted database, running background worker, or AWS spend.

## Development and safety

- Run offline tests with `dotnet run --project OutreachFlow.SmokeTests`; GitHub Actions builds and smoke-tests pull requests.
- Use a dedicated PR branch, have CI pass, then exercise the workflow in a browser before merging.
- Source-backed research is provisional and can contain mistakes; robots restrictions, private-network blocks, read limits and same-site restrictions are intentional.
- Production work still requires schema migrations, backup/restore tests, true tenant-level authorization review, secrets handling, email consent/suppression controls, idempotent jobs, monitoring, and recovery.

See [ROADMAP.md](ROADMAP.md), [RESEARCH-SETUP.md](RESEARCH-SETUP.md) (historical iteration notes), and [MULTI-WORKSPACE-SETUP.md](MULTI-WORKSPACE-SETUP.md).
