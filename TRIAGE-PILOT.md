# Milestone #9 — discovery-to-action triage pilot

## Scope
An operator explicitly routes an indexed discovery suggestion into one of three review queues. This does **not** establish that the source is official, a registration is open, the buyer is eligible, a recipient is approved, or that any outreach is permitted.

- **Research further:** saves an unverified prospect with `ResearchNeeded` and a verification next action.
- **Review application process:** saves an `ApplicationTask` queue item with `ResearchOutcome.NotReviewed`. The owner still must verify the actual official route and use the existing research form before considering manual submission.
- **Review inquiry eligibility:** saves a `NeedsAction` queue item; the owner must verify process and recipient and explicitly record the appropriate research result before drafting.
- All three routes persist the original indexed URL, an unverified search observation, and an audited owner triage activity.
- Duplicate URL clicks within a workspace return the existing prospect without overwriting status, attempts, suppression, ownership, or notes.

## Acceptance walkthrough (no Tavily credits required if a search batch is already visible)
1. Start local Development app and sign into the intended workspace; open **Discover**.
2. Run an on-demand search or use a previous in-session result. Review the original source in the browser.
3. Choose **Research further** for a result. Confirm the record appears in Prospects with a next action and unverified evidence.
4. Repeat with distinct results for **Review application process** and **Review inquiry eligibility**.
5. Inspect their detail views: application review is explicitly unverified; inquiry review cannot generate a draft without additional owner research and a valid approved contact.
6. Repeat routing the same URL. Confirm that the original status and history are preserved rather than overwritten.
7. Verify no external form was submitted and no email sent. Export CSV and check status and next action.
8. Run `dotnet run --project OutreachFlow.SmokeTests` and `dotnet build OutreachFlow.slnx`; CI should run both too.

## Deliberate limitations
- Search-index evidence is never auto-upgraded to `IsOfficial=true`.
- This milestone does not verify whether the result's host owns a business or whether an external form belongs to it.
- No live sending, mailbox connection, job scheduler, autonomous form filling, or new database schema.
- Existing direct imports and manual research flows remain intact.
- Duplicate handling is designed for the localhost single-process pilot, not multiple concurrent hosted writers. Production needs database-level canonical identity, an enforced unique key, and migration/locking work.
- This is a local-only prototype, not a public multi-tenant SaaS release.
