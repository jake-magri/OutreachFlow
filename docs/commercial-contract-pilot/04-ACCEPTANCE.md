# 04 — Acceptance & test matrix
Use **requirement ID → test ID → exact observable result**. Tests are not a subjective demo impression. Run in clean disposable SQLite/Chromium with fake Gmail/search; never production credentials. Attach CI run links in each implementation PR.

## Milestone acceptance
| AT | Requirement(s) | Scenario and assertion | Suite | Issue |
| --- | --- | --- | --- | --- |
| AT-01 | BR-06 | Disabled flags show disabled; not "not implemented"; enabled/test fixture state shows correct active status | Playwright | #35 |
| AT-02 | BR-06,BR-10 | Simulated send stays labeled simulation, Gmail accepted labeled provider accepted, manual attestation separate | C# + Playwright | #35 |
| AT-03 | BR-08,BR-10 | Missing commercial postal address or opt-out instructions, malformed header, stale exact hash: **0 Gmail sends** in all three paths | C# fake Gmail | #36 |
| AT-04 | BR-08 | Valid exact final commercial template with footer is previewed and persisted unchanged through provider submission | C# fake Gmail | #36 |
| AT-05 | BR-08,BR-10 | Declined/suppressed recipient is blocked even after reimport under a new company and scheduled jobs cancelled | C# fake DB | #36 |
| AT-06 | BR-02,BR-03 | Buyer mode produces fleet buyer queries with no mandatory vendor registration; second unrelated workspace creates distinct queries | C# deterministic planner | #37 |
| AT-07 | BR-02,BR-03 | Two modes visible and mode switch keeps existing 2 x 5 budget; no automatic import/outbound | Playwright/fake search | #37 |
| AT-08 | BR-04 | Eight candidate fixture types have correct cautious categories, original source URL and evidence reason | C# triage | #37 |
| AT-09 | BR-05 | URL fragments/case/trailing slash normalize conservatively, repeated candidate not duplicated; distinct facilities remain separable | C# DB | #38 |
| AT-10 | BR-04,BR-05 | Owner audit keeps original source, verified authority decision, when/why; provider history/suppression unchanged | C# DB | #38 |
| AT-11 | BR-01,BR-06 | Dashboard numbers and queue labels equal scoped DB records including held, uncertain and manual/no reply | C# projection + Playwright | #39 |
| AT-12 | BR-07,BR-09 | W1 Buyer→review→(simulated) inquiry→classified reply; clear simulation flags; correct human action | Playwright | #39 |
| AT-13 | BR-07 | W2 official supplier portal verified→ApplicationTask; 0 Gmail calls | Playwright | #39 |
| AT-14 | BR-07,BR-10 | W3 ambiguous/opt-out→blocked outbound, prior history untouched | Playwright | #39 |
| AT-15 | BR-10 | Revoked owner, cross-workspace, reply race, provider timeout and crash recovery preserve no duplicate-send invariant | Existing + targeted C# | all |
| AT-16 | BR-11 | Only if migration: old DB backup/upgrade, fresh parity, restore, foreign keys, identity and provider IDs preserved | Recovery CLI + CI | #38 |
| AT-17 | BR-12 | Product/demo copy contains no invented revenue, verified fleet size, open contracts, SaaS hosting or Gmail-delivered guarantee | Docs/UI review | #39 |

## Fixture contract
Never use real prospects, live email addresses or public websites in CI.
- **FleetBuyer:** fictional truck operator with real service vehicles in snippet; *unverified size and need*.
- **EquipmentRental:** equipment provider may be a buyer, not necessarily available procurement.
- **DirectCompetitor:** provides fleet washing rather than purchases it.
- **WholesaleSupplier:** sells chemicals, equipment or supplies.
- **EmploymentPost:** job listing only.
- **ThirdPartyGuide:** explanation of registration without official process verification.
- **OfficialSupplierPortal:** locally constructed fixture HTML with attributable official URL; owner explicitly reviews before creating task.
- **AmbiguousContact:** source lacks procurement signal; contact field unknown.
- **MultiDepot:** one company / two independently operated locations; do not merge purely by common domain.
- **SharedHost:** unrelated firms under same third-party supplier portal host; do not merge.
- **SuppressedRecipient:** exact email previously declined; cannot re-enter through alternate prospect.
- **FollowupRace:** reply received during accepted Gmail send; future steps cancelled.
Use `example.org` or otherwise reserved fixture-only domains for emails; do not claim a fabricated fixture represents an actual real supplier form.

## State and failure tests
1. New / unreviewed result → never send.
2. Verified application portal → `ApplicationTask`, never generic email.
3. Reviewed inquiry recipient → exact preview → owner approval → one fake accepted Gmail dispatch.
4. Inbox reply with correct provider thread and RFC references → only matching prospect → `NeedsAction` pending owner classification.
5. Declined/opted out → `Suppressed` and future scheduled jobs cancelled, no subsequent send.
6. Uncertain/timeout → durable `Uncertain`, no retry even after restart; read-only reconciliation only.
7. SMTP provider result `Sent` is Gmail acceptance, not actual inbox delivery.
8. Simulated route can never trigger provider.

## Run and CI gates
```powershell
git status
dotnet restore OutreachFlow.slnx
dotnet build OutreachFlow.slnx -c Release
dotnet run --project .\OutreachFlow.SmokeTests -c Release --no-build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\OutreachFlow.BrowserTests\bin\Release\net10.0\playwright.ps1" install chromium
dotnet run --project .\OutreachFlow.BrowserTests -c Release --no-build
```
- GitHub Actions Windows recovery/smoke, Linux build/smoke and Chromium jobs must all pass at PR HEAD.
- Log test failures by AT ID; no vague "QA passed" criterion.
- On actual Windows install, **never** run Playwright against `OutreachFlow/outreachflow.db`; browser harness owns its isolated DB and deletes it afterward.
- A change in `DbContext` requires migration drift check and backup-first upgrade rehearsal; no EnsureCreated reset on user data.
- A failed acceptance criterion **blocks merge**; a feature idea discovered during testing becomes a separately scoped issue, not a new acceptance criterion mid-PR.

## Completion rule
Issue is done only when its AT IDs pass, branch diff stays within allowed files, docs match observable behavior, no unapproved changes to external sending, and reviewer can reproduce results using the prescribed commands.
