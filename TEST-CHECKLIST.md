# OutreachFlow acceptance checklist — November 11, 2026

This checklist covers **merged local functionality** and audit PR #12. Never test with real external sending; the app still has **zero live email-sending capability**. Back up existing DB and local configuration before switching branches. Keep real OAuth tokens private.

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

## Engineering and release gates
- [ ] `dotnet restore OutreachFlow.slnx` and `dotnet build OutreachFlow.slnx` complete cleanly.
- [ ] `dotnet run --project OutreachFlow.SmokeTests` passes with no real external API calls.
- [ ] GitHub Actions on the candidate PR passes build, smoke and localhost unauthenticated route tests.
- [ ] Current SQLite DBs, workspace profile JSON, Gmail token key ring and search ledger are backed up; no schema changes in PR #12.
- [ ] Reviewer confirms diff contains no secrets, live email sender, unintended data deletion or public hosting.

## Not accepted as complete

Real Gmail sending, message/thread correlation, durable background jobs, provider-native bounce/complaint handling, production EF migrations, restore-tested backups, account recovery, hosted auth and Google restricted-scope verification are **future milestones**. See `ROADMAP.md` and audit issues #13–#22. A successful OAuth login does not prove a full email workflow.
