# OutreachFlow roadmap

**North star:** a research-first business opportunity pipeline. The owner can initially seed 5–10 targets; the system should eventually discover permitted public opportunities, research their application processes and contacts, queue approved outreach, monitor responses, and hand off meaningful decisions. Commercial services and independent software contracting are two configurations of the same business-development product—not a W-2 application bot.

## Already merged in main
- .NET 10 / Blazor, EF Core + local SQLite, local-first sign-in, workspace membership and data isolation
- Prospect import, four queues, manual source verification, limited same-site research with SSRF/robots restrictions
- Draft templates, simulations, bounded weekday sequence preview, opt-out guards, manual notes, CSV export
- GitHub Actions CI

## Development branches awaiting local review
- PR #2: owner-confirmed external sends and pasted incoming replies, with audit history (no mailbox connection)
- **This research PR:** visit relevant public contact pages, find email candidates in visible HTML and mailto references, save URLs/observations, suggest review priority, and allow explicit owner recipient approval. Keeps existing SQLite schema and simulation-only sending.

## Next: complete a 5–10-company business outreach pilot
1. Improve on-demand research coverage and category-specific qualification rules: service keywords, locality, vendor process requirements, and contact relevance. Use explicit states *verified process*, *possible process*, *no verified process in checked sources*, and *research blocked/incomplete*. No automatic negative conclusion from a sparse crawl.
2. Integrate one authorized mailbox provider for actual outgoing and incoming message history, stable provider IDs, opt-out/bounce/complaint suppression and safe webhook/poll processing. Confirm mailbox provider and API suitability first.
3. Introduce durable queued jobs, idempotent sends, rate/volume limits, sender identity/address, business day scheduling, pause and owner approval of initial campaign. A job must recheck process status, suppression, latest replies and ownership before each send.
4. Run an approved low-volume pilot for 5–10 actual companies. Record time saved, manual interventions, discovered process accuracy, usable contacts, replies and handoffs.
5. Connect a compliant keyword search API to feed candidate websites to the *existing* research step; add relevance and deduplication against historical contacts.

## Later
- PDF and third-party vendor portal discovery, with owner verification, limited supported form prefill for manual submission
- A second **business services** strategy for independent software / .NET / automation contracting, RFPs and subcontract leads
- Flexible outreach policies and configurable campaigns based on proven differences between the first two workflows
- Public hosting: schema migrations, restore-tested backups, account recovery/invitations, audit/authorization review, secret management, operational alerts, and provider quotas
- Subscription billing and self-serve SaaS only after a demonstrably useful and safe engine

## Non-goals now
- Automated W-2 job applications or unapproved LinkedIn/Indeed scraping
- Autonomous account signups or protected-portal submissions
- General-purpose no-code designer and open public registration
- Bulk unsolicited messaging without verified identity, limits and opt-out handling

The current app is a localhost-only Development prototype. Existing SQLite databases are retained to prioritize end-to-end functionality; migrations and deployment hardening are delayed, **not optional** before public launch.
