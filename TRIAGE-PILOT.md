# Historical milestone #9 — deterministic discovery-to-action triage pilot

> **Historical accepted search-result fixtures.** PR #9 is merged; latest architectural status and remaining work are recorded in [AUDIT-2026-11-11.md](AUDIT-2026-11-11.md).

## Goal
Turn an on-demand search batch into a grouped and explainable next-step review list without treating search snippets as verified procurement instructions or approving outreach. The system **suggests** where to investigate; only the workspace owner may commit a route to a prospect.

## How automatic suggestions work
- **Review application process:** indexed evidence plausibly describes buyer-side registration, procurement, or, for an employment-oriented workspace, an application for work.
- **Review inquiry eligibility:** indexed evidence points to a vendor/procurement inquiry contact but does not identify a clear application process.
- **Research further:** evidence is too ambiguous, sparse, or mixed to recommend a specific action.
- **Deprioritize / possible false match:** likely wholesaler selling goods, competitor offering fleet washing, third-party guide, business-irrelevant job listing, or search snippet explicitly reporting a past due date. Still visible and manually recoverable.

Suggestions are computed deterministically from indexed title, snippet, URL path and workspace research profile. No LLM or extra search provider calls. Mixed signals resolve conservatively. The relevance score remains a keyword heuristic, not a verified confidence level. A suggested route is not a verified process, approved contact, invitation to bid, or evidence of legal permission to send email.

## Owner review and persistence
- **Discover** groups indexed candidates by suggested next step and presents the reason for each suggestion.
- The owner inspects the original source and explicitly chooses **Approve suggested route** or **Choose a different route**.
- Deprioritized candidates remain visible with an **Override and review this result** option.
- A selected route saves a prospect, source evidence marked `IsOfficial=false`, next action and audited triage activity.
- **Research further:** `ResearchNeeded` queue.
- **Review application process:** `ApplicationTask` queue, `ResearchOutcome.NotReviewed`; actual application still requires manual verification and submission.
- **Review inquiry eligibility:** `NeedsAction` queue, `ResearchOutcome.NotReviewed`; process and recipient must be verified before drafting.
- Existing prospect URLs retain their previous state; repeated discovery choices do not overwrite ownership, suppression, messages or notes.

## Acceptance walkthrough
1. Start the local Development app and sign into the intended workspace; open **Discover**.
2. Execute one on-demand search (two queries maximum, up to five results per query).
3. Confirm results are grouped under suggested routes with specific explanations, and that wholesaler or guide false matches are lower priority but visible.
4. Inspect a plausible official source and approve its suggested route. Check the resulting Prospects entry, NextAction, unverified source evidence, and activity history.
5. On a different result, choose an alternative route. Confirm the override is explicitly initiated by the owner.
6. Test a low-priority result's **Override and review this result** control; it must not silently import.
7. Try routing an already imported URL. Confirm its original status is preserved.
8. Confirm no automatic email, portal submission, account signup, or verified `ProcessUrl` was created.
9. Run `dotnet build OutreachFlow.slnx` and `dotnet run --project OutreachFlow.SmokeTests` locally (CI runs these as well).

## Limits and follow-up work
- Indexed results cannot establish organizational identity, freshness, eligibility, or the authenticity of cross-domain forms. Verified process classification requires separate source inspection.
- Search result suggestions are session-only until expressly routed; no unattended prospect creation.
- No live mailbox integration, scheduler, auto-send, LLM, schema migration or production deployment.
- Single-process local prototype: database-enforced URL canonicalization/uniqueness and concurrent-writer protection are production follow-ups.
- Additional richer research-driven verification and policy tuning should be evaluated with real pilot observations before approving automatic actions.

## November 11 live fleet pilot feedback
The first operator search returned eight indexed suggestions. New fixtures reflect the visible title, URL and snippet for the City procurement hub and generic RFP, Vector Fleet's new-vendor-setup form, a fleet washing supplier, a competing washing provider, a municipal fleet department, a third-party listing with a September 24, 2025 deadline, and an Indeed jobs listing.

Expected after refinement: explicit vendor setup -> **application review**; general city pages -> **further research**; supplier/competitor/old solicitation/job board -> **deprioritize**. These are **suggestions only**, not verification of a buyer, current availability, supplier eligibility, or third-party link authenticity. Past deadlines are detected only when a recognizable date is explicitly labeled as a deadline/due date; absence of a date does not establish currency. Search snapshots are truncated and may omit important details, so direct inspection remains necessary.

This is a conservative keyword/heuristic improvement, not a trained classifier. Recheck with the same workspace profile before merging, ideally using real results already in view. Each new search makes up to two operator-funded Tavily requests.
