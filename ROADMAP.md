# OutreachFlow build roadmap

## Completed in iteration 1
- Blazor Web App on .NET 10, EF Core with SQLite, four-queue dashboard
- Fictional demo records, prospect batch import, recorded vendor-process research
- Inquiry template expansion, simulated initial emails, simulated follow-ups and replies
- Configurable business profile, template text, and cadence

## Added in iteration 2
- Persistent dry-run `NextStepDueUtc` preview with weekday-aware, timezone-aware calculation
- Snapshot of follow-up count and cadence when each sequence begins
- Human ownership / manual activity notes, next action, date, and closure
- CSV export of pipeline metadata with formula-injection protection
- Guards for suppressed addresses and addresses with prior simulated outreach
- Localhost-only access guard + explicit refusal of production startup
- Self-contained smoke-test console app (run locally with .NET SDK)

## Added in iteration 3 — narrow online vendor research
- Read-only on-demand company-website scanner, bounded to six public HTML pages (not a search engine)
- Persisted source-backed candidate links and page-check history in existing ResearchEvidence records
- Explicit owner verification required before a portal/application finding can change outreach routing
- SSRF safeguards and robots handling, plus offline fake-adapter tests; no schema change

## Next milestone: dependable data and broader process research
1. Extend the existing research adapter to support official web search, PDF extraction and third-party supplier portals with review. The current adapter is limited to same-host HTML; external links are suggestions only. No supplier portal signups or auto-submissions.
2. Replace `EnsureCreated()` with EF Core migrations; support safe schema upgrades and real backups.
3. Introduce normalized contacts and a global suppression table. Confirm duplicate rules for company, branch and address; keep original research and outreach history intact.
4. Add deterministic application-task details: application link, requirements, manual completion note, next action, and follow-up date.
5. Add single-owner authentication, authorization enforcement, CSRF checks, and secure secret management **before** remote hosting.

## After research and authentication: live mailbox pilot
1. Identify which provider hosts the user's existing mailbox and whether its APIs and policies permit this type of business inquiry.
2. Implement a verified provider for inbound, sent-message history and outbound, with provider message IDs and unique send intents.
3. Verify suppression from explicit opt-outs, bounces/complaints, and negative replies. Maintain accurate commercial-email identity, physical address and opt-out instructions.
4. Build a persistent BackgroundService worker that leases due jobs, rechecks replies/owner pause/sync freshness just before sending, and holds rather than retries ambiguous accepts.
5. First pilot must use an approved low-volume batch, all configured compliance fields, clear pause controls, and live observation.

## AWS deployment (not now)
- Compare one-instance Lightsail 1–2 GB vs other providers after measuring real .NET memory use.
- For a private app: owner login, HTTPS proxy, firewall, DNS, secure cookie configuration, secret management, offsite backups + restore test and budget alerts.
- Hosted DB remains private. SQLite can suit one running instance; do not run multiple app instances against a network filesystem or rely on a snapshot as the only backup.
- Email and research API spending will be separately budgeted. Never enable arbitrary background emailing just because hosting is available.
