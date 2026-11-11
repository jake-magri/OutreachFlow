# OutreachFlow acceptance checklist — November 11, 2026

This checklist covers the **merged local prototype through PR #27**, including the controlled Gmail live-send acceptance completed November 11, 2026. Never send to an uncontrolled recipient as a test. Live sending remains disabled by default, requires separate Gmail consent and a distinct per-inquiry owner approval. Back up existing databases and local configuration before upgrades. Keep OAuth tokens private.

## Startup and account bootstrap
- [ ] Fresh temporary test installation (no DB files): start app and open `/account/setup`; create a local password owner and confirm one empty workspace, no demo prospects, owner membership and successful login.
- [ ] Existing installation: after update, confirm all preexisting prospects, workspace IDs, local user accounts and Gmail authorization remain intact. No demo records are silently added.
- [ ] Google-first brand-new test identity gets a separate Owner workspace and cannot view another user's prospects.
- [ ] Existing local user links Google while authenticated; subsequent Google login preserves the original workspace and history.
- [ ] Unknown/unverified identity cannot auto-attach to an existing account merely because of an email address.
- [ ] Protected pages and `/export/prospects.csv` deny unauthenticated access; localhost guard rejects non-loopback.
- [ ] Switching to second workspace does not reveal or mutate another workspace's prospect or mailbox.

## Discovery and public research
- [ ] Search profile settings visibly affect the next **on-demand** Discover search. A normal click consumes up to two Tavily basic search attempts.
- [ ] Indexed candidates remain unverified; classification separates vendor form, generic city procurement, competitor, wholesaler, expired listing and job board; manual override works.
- [ ] Importing one candidate records source and selected next action without marking it official or adding a recipient. Repeating the same selection preserves history.
- [ ] Research company website inspects bounded same-site public HTML and produces source-backed evidence or an honest blocked/inconclusive result.
- [ ] Attempt to research localhost/private IP, blocked portal or unsupported domain fails closed. Browser rendering remains opt-in and cannot submit forms.
- [ ] Discovered public email is not selected without owner approval. Recorded official portal is an application task, not a generic inquiry.

## Prospect state and message history
- [ ] Manual import of same company/site does not create duplicate; suspicious or suppressed recipients do not bypass guards.
- [ ] Reviewed eligible research generates a draft; simulated send/follow-ups record **simulation** and do not transmit email.
- [ ] Human-attested external send records actual activity and pauses simulated scheduling.
- [ ] A real inbound message can be owner-recorded; positive reply creates warm lead; opt-out creates suppression.
- [ ] **Audit PR #12 regression:** changing research cannot reopen Closed records; simulated replies cannot reopen Closed/Suppressed or attach to real contact history.
- [ ] Closed/Suppressed records allow internal notes only, not new outreach action dates or follow-up tasks.
- [ ] Manual takeover cancels preview; no-response goes to manual queue, never warm.
- [ ] CSV export contains only pipeline metadata (no full mail bodies) and escapes formula-like values.

## Google Gmail read-only pilot
- [ ] In Windows Development mode with correctly configured OAuth user secrets, Mailbox shows connected identity after separately granting `gmail.readonly`. Merely signing into Google is not consent for Gmail.
- [ ] Gmail session for a different sender does not connect to this workspace.
- [ ] With a test prospect for which a real external send was attested, manually review a **genuine** matching inbox reply; confirm import updates exactly that prospect, not an unrelated one.
- [ ] Importing the same Gmail message twice is rejected or treated as already recorded. Reopen/restart app and check history persists.
- [ ] Unrelated or ambiguous mail is not automatically treated as a reply. Gmail read-only does not write labels, replies or messages.
- [ ] Disconnect removes local grant without deleting historical reviewed activity. Reconnect explicitly as needed. Revocation in Google Account is a separate action.

## Controlled real Gmail sending (PR #27)
- [x] **Recorded owner acceptance, November 11, 2026:** one explicitly approved real Gmail self-send reached the controlled inbox, one genuine reply imported once as Interested, prospect became WarmLead, and restart plus a repeated inbox review preserved history without duplicate import. See merged [PR #27](https://github.com/jake-magri/OutreachFlow/pull/27). This records the test performed; it is not a new independent test run.
- [ ] On any future code or database change, recheck disabled-by-default `Gmail:EnableSending`, consent scopes and exact sender/recipient/subject/body before sending. Do not repeat a live send unless necessary for an intentionally bounded acceptance test.
- [ ] Confirm unresolved/uncertain results cannot be retried or replayed silently. Inspect Sent reconciliation and state preservation using offline fixtures unless a real provider failure occurs naturally.
- [ ] Confirm closed/suppressed/prohibited/unapproved prospects cannot dispatch; duplicates, revoked scopes, quota, and timeout behavior remain regression tested offline.
- [ ] Before unattended operation, satisfy issue #13: correct sent-thread association, ambiguous email rejection and bounded Gmail/MIME responses. Then issue #15: durable stop-aware follow-up scheduling.

## Engineering and release gates
- [ ] `dotnet restore OutreachFlow.slnx` and `dotnet build OutreachFlow.slnx` complete cleanly.
- [ ] `dotnet run --project OutreachFlow.SmokeTests` passes with no real external API calls.
- [ ] GitHub Actions on the candidate PR passes build, smoke and localhost unauthenticated route tests.
- [ ] Current SQLite DBs, workspace profile JSON, Gmail token key ring and search ledger are backed up before migration or Gmail dispatch schema upgrades; verify the recovery bundle. PRs #24–27 contain migrations.
- [ ] Reviewer confirms diff contains no secrets, **unexpected or unapproved** live email execution, unintended data deletion or public hosting.

## Not accepted as complete

Supervised real Gmail sending **is locally accepted**; full thread-backed correlation, unattended follow-up jobs, bounce/complaint handling, production-grade restore/recovery and tenancy constraints, account recovery, hosted auth and Google restricted-scope verification remain **future milestones**. See `ROADMAP.md` and audit issues #13–#22. A successful OAuth login does not prove a full email workflow.
