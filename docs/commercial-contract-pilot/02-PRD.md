# 02 — Product requirements document (PRD)
**Product:** OutreachFlow, localhost supervised commercial account-to-conversation workflow.
**Parent:** #34. **Status:** scope frozen for milestone breakdown, subject to explicit owner revision.

## Problem statement
An owner who sells recurring commercial services needs to discover plausible buyers, identify an appropriate contact/application route, send only approved initial outreach, follow up conservatively, and know when to take over. Existing OutreachFlow implements most Gmail lifecycle plumbing but lacks a polished buyer-first workflow, reliable account-centric evidence presentation, and accurate cross-screen labels. Commercial email safeguards are a release gate for live use.

## Primary success moment (demonstration)
From a clean test workspace, the owner selects buyer search, inspects an unverified candidate, verifies how the prospect accepts vendors, chooses the correct route and tracks an inquiry or application, then sees a real or simulated (clearly labeled) reply/no-response next action. The visible result is a **truthful action queue**, not a falsely promised booked appointment.

## Actors and permissions
- **Workspace Owner:** configures templates/profile; reviews process and recipients; approves actual initial or follow-up Gmail; classifies replies, pauses/cancels; can access own workspace only.
- **Workspace Member:** can read/operate only where current existing membership rules allow; never gains owner send authority through UI changes.
- **Automated worker:** acts only on preapproved durable follow-ups when both Gmail and scheduler toggles permit; fail-closed on uncertain provider result.
- **External source/recipient:** untrusted. Search content and email content never authorize actions or update status without policy checks.

## Requirements with traceability
| ID | Requirement | Existing baseline | Milestone |
| --- | --- | --- | --- |
| BR-01 | One owner-facing, navigable action queue | Four basic queues; stale copy | M5 #39 |
| BR-02 | Explicit BuyerAccounts vs ApplicationProcesses discovery | process-oriented only | M3 #37 |
| BR-03 | Two unrelated workspace profiles yield distinct queries, equal bounded budget | profiles and 2x5 Tavily | M3 #37 |
| BR-04 | Preserve source evidence, uncertainty, reviewer decisions | ResearchEvidence and manual routes | M4 #38 |
| BR-05 | Conservative duplicate handling preserving distinct procurement sites and historical suppression | basic company/site and URL matching | M4 #38 |
| BR-06 | Accurate live/manual/simulated/Gmail-accepted/scheduled statuses | messages, dispatch, followup jobs | M1 #35 + M5 #39 |
| BR-07 | Three reproducible demo workflows without external providers | partial Playwright | M5 #39 |
| BR-08 | Commercial email sender/address/opt-out preflight before every real send | sender & postal address fields, Gmail limits | M2 #36 |
| BR-09 | Human handoff after classified reply, application task or no response | state machine exists | M5 #39 |
| BR-10 | Tenant isolation, no duplicate mail, fail closed on uncertain response | existing security and outbox | all milestones, regression gate |
| BR-11 | Backup-first persistence upgrades and non-destructive fixture cleanup | recovery CLI and tests | if and only if schema changes |
| BR-12 | No manufactured claims of contract awards, verified fleet size or customer traction | product policy | all docs/UI |

## User workflows
### W1 — A private potential buyer
1. Owner chooses **Find potential buyers** with workspace profile; existing search budget is displayed.
2. Results are candidate organizations, each showing unverified evidence, source URL, reason and possible next action.
3. Owner reviews source; if a meaningful procurement route is found, choose application workflow. If no applicable process is found after review, owner may approve a specific contact for inquiry.
4. Review exact inquiry content. Simulation may run in isolated demo; real Gmail requires separate consent, commercial policy and owner confirmation.
5. Reply links to provider-confirmed original thread and blocks scheduled follow-ups. Owner decides classification. An unanswered approved sequence ends in **ManualNoResponse**, not WarmLead.

### W2 — A published supplier portal
1. Indexed source suggests registration.
2. Owner verifies the official City of Charlotte or other supplier page, including whether it applies to this organization.
3. Save original source and application URL; create `ApplicationTask`. No generic Gmail inquiry or automatic portal submission.
4. Owner records external completion and next action manually.

### W3 — Unsafe/unclear lead
1. Candidate is supplier, competitor, guide, jobs, conflicting identity, ambiguous process or suppressed contact.
2. UI explains uncertainty and routes to further review or deprioritization.
3. No claim of official process, vendor interest, response or address consent. No external send.
4. Old provider IDs, suppression and workspace history remain immutable.

## Nonfunctional requirements
- NFR-01: Single-owner localhost pilot; Windows DPAPI Gmail token protection; no public inbound network.
- NFR-02: Existing 2 search calls x max 5 results per user click; budget ledger remains authoritative; no LLM/provider requirement.
- NFR-03: Scoped access for business and auth contexts; must not leak other workspaces.
- NFR-04: SQL/HTTP use existing bounded services; no protected-portal scraping or external form submits.
- NFR-05: No live Gmail in CI. No duplicate send after restart, double-click, timeout or opt-out.
- NFR-06: Fake/test data only in slides, browser screenshots, test logs; test cleanup must verify deletion.
- NFR-07: Accessible labels/keyboard paths and clear empty, disabled, blocked and error states.

## Release gates / no-go
- **D0 Docs ready:** this pack and linked issue specifications merged, requirements-to-tests complete. Not equal to product ready.
- **D1 Demo ready:** M1/M3/M4/M5 passed and three routes reproducible; M2 can be validated offline. Demo must not call real Gmail or make market outcome claims.
- **D2 Limited real commercial pilot ready:** M2 passed in all live paths; owner verifies a real contact/source, opt-out route and final snapshot; backups and OAuth scopes reviewed; low-volume supervised trial separately approved.
- **D3 Hosted SaaS:** deferred to #16/#17/#19/#22 and provider verification; not part of these milestones.

## Explicit non-goals
Hosted account signup, payments/pricing UI, general-purpose CRM, automated vendor forms, purchased contacts, company size prediction, broad LLM agent, unrestricted crawling, live cold-email campaign automation, Gmail account takeover and automatic promise of leads.
