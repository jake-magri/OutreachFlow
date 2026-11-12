# 03 — Engineering specification
**Scope:** Documentation-first, five incremental PRs. Existing .NET 10 Blazor Interactive Server / EF Core SQLite / ASP.NET Core Identity / Google OAuth / Gmail API / Tavily Search remain authoritative. All paths relative to repository root. This spec does not authorize code implementation until the corresponding issue is selected.

## Baseline implementation (verified from main on 2026-11-11)
- Web pages: `OutreachFlow/Components/Pages/{Home,Discover,Prospects,ProspectDetail,Settings,Mailbox}.razor`.
- Discovery: `Services/Discovery/{SearchDiscovery,OpportunityDiscoveryService,OpportunityTriage,TavilySearchProvider,LocalSearchUsageBudget}.cs`; bounded, on-demand 2 queries x up to 5 listings each, persistent budget.
- Workspace profile: `Services/Research/`; per-workspace profile, no hard-coded Magri contact defaults.
- Prospect lifecycle and evidence: `Models/{Prospect,Workspace}.cs`, `Services/OutreachService.cs`; `ResearchEvidence`, `OutreachMessage`, `ActivityEntry`.
- Actual Gmail initial: `Services/Mailbox/{ApprovedSendService,BatchInquiryService,GmailMailboxService,GmailReplyCorrelation,GmailSending}.cs`, durable `OutboundDispatch`, `ProviderMessage`.
- Scheduled Gmail: `Services/Mailbox/FollowupAutomationService.cs`, durable `FollowupJob`, owner-approved exact snapshots, opt-in hosted worker. `Followups:Enabled` false by default.
- Data: `Data/OutreachDbContext.cs`, `AccountDbContext`, two SQLite files; recovery CLI `OutreachFlow.RecoveryCli` for backup-first migration.
- Tests: `OutreachFlow.SmokeTests` (service/provider failures, fake clock and Gmail), `OutreachFlow.BrowserTests` (disposable Chromium and SQLite), Windows/Linux CI.

## Architecture — proposed thin additions; avoid parallel systems
```text
WorkspaceProfile -----+
                      +--> DiscoveryMode + Existing Planner/Ranker + Tavily budget
Owner UI (Discover) --+             |
                                    v
                         Unverified Candidate+Source
                                    |
                              Owner Review
                                    v
                    Prospect + ResearchEvidence
                      |                    |
                  Verified portal     Approved inquiry route
                      |                    |
                 ApplicationTask     Existing ApprovedSendService
                                           |
                                     Gmail initial Sent
                                           |
                          Existing FollowupAutomationService
                                           |
                              Reply / ManualNoResponse
                                           |
                              Home action queue / audit
```
**No new autonomous decision-making authority.** All source claims remain unverified until owner checks them.

## M1 interface — current-state projections
- Prefer a read-only projection that joins `Prospect`, `OutreachMessage.IsSimulation`, `OutboundDispatch.State`, and `FollowupJob.State` for the active `WorkspaceId`.
- UI must not use `Prospect.Attempts` alone to claim actual Gmail sends (manual/simulated attempts exist).
- Runtime configuration (not static copy) determines state of `Gmail:EnableSending` and `Followups:Enabled`; if Google scope absent, display "not authorized" rather than enabled.
- Required labels: **Simulated**, **Owner-attested external send**, **Gmail accepted (delivery unconfirmed)**, **Queued**, **Held**, **Sending**, **Uncertain**, **Cancelled**, **Needs review**.
- No new state enum; translate provider states to UI labels without overwriting source data.

## M2 contract — enforceable commercial message policy
Proposed pure decision point:
```csharp
public sealed record CommercialSendSnapshot(
    int WorkspaceId, int ProspectId, string From, string To,
    string Subject, string Body, string PostalAddress,
    string OptOutInstructions, string ApprovalHash);
public sealed record CommercialSendDecision(bool Allowed, IReadOnlyList<string> Errors);
```
The specific type names are illustrative; do not create a new abstraction unless it reduces duplicated checks.

Policy should require sender identity, a valid configured postal address, a clear usable opt-out method, non-deceptive approved content and no active suppression/closed/replied state. The **final rendered body, including footer**, is snapshotted *before owner approval*; the provider sends that exact snapshot unchanged. Normalize contact for suppressions using existing mailbox/workspace semantics, not a new speculative global identity system. Review the existing multi-path sends:
- `ApprovedSendService.PreviewAsync/SendAsync`
- `BatchInquiryService.PreviewAsync/RunAsync`
- `FollowupAutomationService.ApproveAsync/DispatchAsync`
- Gmail inbound classification / owner suppression in `OutreachService` and `GmailMailboxService`

**Critical edge condition:** If policy changes after owner approval, block/hold and request new owner preview rather than editing a stored approved job. No silent regeneration of RFC Message-ID; no retry from Sending/Uncertain.

## M3 contract — discovery modes
```csharp
enum DiscoveryMode { ApplicationProcesses, BuyerAccounts }
```
- Add an optional mode parameter to existing `DeterministicQueryPlanner.Plan` and `OpportunityDiscoveryService.DiscoverAsync` while preserving existing caller behavior by default.
- Buyer-mode inputs: `ResearchProfile` and workspace `TargetDescription`/`ServiceArea`; validate lengths and disallow unbounded query construction. No added provider.
- Existing `OpportunityCandidate` retains exact `Title/Url/Summary/Query` and `SourceTier`; an optional nonbinding `BuyerKindSuggestion`+reason may be added.
- Source text is *untrusted data*, never execution instructions; names, addresses, fleet counts, purchasing authority, available contracts and contact roles remain unverified.
- User route stays through `OpportunityDiscoveryService.RouteReviewedAsync`; no direct `ResearchOutcome.NoProcessFound` from a snippet, no automatic Gmail.
- Failure semantics: search quota depleted, partial result and provider error surfaced with evidence; never return "no buyers exist" as a conclusion.

## M4 contract — identity, evidence and process state
Existing unique-ish keys (`WorkspaceId,CompanyName,SiteName`; canonical URL) are imperfect. Start with normalization and a scoped evidence-based resolver, preserving the original URL.
Proposed concepts **not automatically new tables**:
```text
OrganizationCandidate: displayName, evidenceSourceUrl[], suggestedRelationship, confidence=unknown
LocationOrBranch: owner-reviewed local site identifier, location evidence, unique procurement authority=unknown
VerificationDecision: ownerId, UTC timestamp, evidence URL(s), disposition, specific rationale
```
- Distinguish web URL key (strip fragment, normalized host/port) from account key (workspace + reviewed company + site). Shared domain alone is not proof of same owner or decision maker.
- Never change an existing provider-linked prospect's identifier or merge histories. Repeated import must preserve `Suppressed`, `Closed`, existing `ProviderMessage` and `FollowupJob`.
- Route verified portal to `ApplicationTask`; unverified/unclear to `ResearchNeeded` or `NeedsAction`; a human-approved inquiry route must meet existing send guards.
- If `Account` or `VerificationDecision` table becomes necessary, submit a *separate short ADR* demonstrating the failed existing-model fixture first, then EF migration with backup-first `RecoveryCli upgrade` and restore tests. No migration bundled with unrelated visual changes.

## M5 contract — owner queue and demo
- Read-only scoped projections using existing statuses and jobs; select newest pertinent record but do not infer a real Gmail sent from `Attempts`.
- Dashboard sections: **Research**, **Application tasks**, **Awaiting replies**, **Scheduled**, **Held/uncertain**, **Warm/needs human action**, **Manual no response**; avoid claiming 7 queues are all necessary on one page—present a compact layout from data.
- All actionable rows link to current prospect detail and require existing owner permissions for mutation. Test counts by workspace.
- Deterministic fictional fixtures loaded only in temporary browser test databases. Demo script must not require manually typing fake customer email into real Gmail or resetting owner data.

## Core invariants (non-negotiable)
| ID | Invariant |
| --- | --- |
| INV-01 | A source URL/snippet never authorizes an actual Gmail send |
| INV-02 | Sending requires owner permission + exact preview approval + valid verified route + live opt-in |
| INV-03 | A known opt-out/suppression prevents any future outreach by any route in that workspace |
| INV-04 | Provider acceptance ≠ inbox delivery; simulation ≠ real send |
| INV-05 | Unknown Gmail send result blocks retries; never create a second send record |
| INV-06 | Active reply or manual takeover cancels all unattempted follow-ups |
| INV-07 | Separate workspace data never appears in another owner's query or UI |
| INV-08 | Same domain is not sufficient to merge facilities, procurement authorities or accounts |
| INV-09 | Schema changes require a verified backup and successful restore rehearsal |
| INV-10 | No CI test may use a real Gmail token, actual customer contact or production SQLite file |

## Dependencies and ordering
1. **M1 #35**: no prerequisite, UI truth/labels. Can be implemented first.
2. **M2 #36**: before any expanded live external pilot; can be developed separately using existing Gmail abstractions and fake provider. Do **before** changing sender templates to make approval-content immutable.
3. **M3 #37**: depends only on unchanged discovery service; new mode only.
4. **M4 #38**: depends on M3 candidate shape; can decide after initial buyer-mode fixtures which normalization is enough.
5. **M5 #39**: depends on M1, M3 and M4; dashboard/projections and 3 full UI journeys. M2 required for live commercial demo, not isolated simulation.
D1 demo requires M1/M3/M4/M5; D2 live commercial requires M2 also.

## Operational and upgrade rules
Run `dotnet restore OutreachFlow.slnx`, `dotnet build OutreachFlow.slnx -c Release`, `dotnet run --project OutreachFlow.SmokeTests -c Release --no-build`, `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\OutreachFlow.BrowserTests\bin\Release\net10.0\playwright.ps1" install chromium`, `dotnet run --project OutreachFlow.BrowserTests -c Release --no-build`.
- Never delete existing `OutreachFlow/outreachflow.db` or `outreachflow.auth.db`.
- If migration: stop app, run `OutreachFlow.RecoveryCli baseline inspect` and `upgrade` with a **new external backup path**, rerun inspect and verify existing data.
- `Followups:Enabled` and `Gmail:EnableSending` remain false for tests; don't make real sends part of developer acceptance.
