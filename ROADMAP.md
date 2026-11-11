# OutreachFlow roadmap — assessed November 11, 2026

## North star

Build a **configurable B2B opportunity-to-conversation pipeline**: find likely buyers or relevant official processes, record source-backed evidence, verify the correct approach, obtain sender/recipient approval, send a small number of legitimate inquiries, detect replies, stop inappropriate follow-ups and hand off meaningful decisions. Fleet washing is the first proof of value, not a hard-coded product restriction.

**Current position — November 11, 2026 (after merged PR #27):** Google sign-in, separately consented Gmail read access, owner-reviewed inbound imports, backup-first EF upgrades and provider message identity have been exercised locally. PR #27's Windows acceptance records one explicitly approved real Gmail inquiry delivered to a controlled recipient, a genuine reply imported once as Interested, a WarmLead transition, persistence across restart and no repeat import on fresh review. The sender remains disabled by default; each first send requires explicit owner approval. This is a **validated local, supervised communication pilot**, not automatic follow-ups, unattended inbox reconciliation or hosted readiness.

### Current stages and exit gates

| Stage | Current status | Definition of done (DoD) | Blocking dependencies |
| --- | --- | --- | --- |
| 0. Foundation: .NET, auth, workspaces | Local implementation complete; production hardening not done | Two independent users/workspaces, blocked cross-access, local auth/cookie checks and passing smoke tests | Hosted use still gated by #16, #17, #22 |
| 1. Search → process research → owner triage | Functionally implemented and live-pilot classified eight results | User can discover, identify false positives, inspect sources, route without auto-approval or messages, and preserve original evidence | Precision and account-level dedupe improvement #10, #18 |
| 2. Google identity + authorized Gmail read-only | Owner tested connection, matching reply import and decline suppression; concurrent import protection covered by offline tests | Existing-account link and Google-first registration preserve separate workspaces; correct owner connects Gmail; one genuine reply reviewed, imported **once**, and audited; zero sends | Gmail correlation/reliability review #13 |
| 3. Controlled real communication | **Local supervised acceptance completed** in merged PR #27; no unattended activity | One explicitly approved inquiry sent through Gmail to a controlled recipient, one real reply imported, provider identity and history persisted after restart, no duplicate import; offline failure/duplicate protections covered | #13 thread-based correlation and payload bounds remain before unattended use; #21 UX polish; #16 broader persistence work |
| 4. Low-volume recurring outreach | **Not started** | 5–10 real, eligible, manually approved pilot accounts; idempotent job queue, send-time rechecks, 1–2 capped follow-ups, replies/negative signals immediately stop schedule, human dashboard matches audit | #15, #14, #13, business qualification #10/#18 |
| 5. Hosted multi-tenant beta | **Not started** | Migration/backup/restore, auth/role recovery, rate limits, privacy and provider compliance, secure secrets, incident monitoring, threat-modelled crawler, isolated tenants, tested deployment/rollback | #16, #17, #19, #20, #22 |
| 6. General SaaS | Out of current build scope | Multiple supported customer verticals, onboarding, billing, support, reliability metrics and successful private beta; external provider approvals complete | Depends on Stage 5 product/market validation |

## Persistence recovery progress (issue #16)

The first recovery slice introduces an **operator-only** `OutreachFlow.RecoveryCli` that makes consistent SQLite snapshots, includes local profile/usage/mailbox files, verifies SHA-256 digests and database integrity, and restores only into a new directory. The operator must stop the app to obtain a coherent *multi-file* snapshot and must encrypt the backup. See [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md).

The initial migrations and backup-first adoption CLI are in place. Fresh installations use EF migrations at startup. Existing installations use an explicit recovery CLI `upgrade` command to validate their recorded schema, back up both databases and auxiliary state, adopt legacy baselines, and apply incremental migrations. See [MIGRATION-BASELINE.md](MIGRATION-BASELINE.md).

The provider identity slice adds mailbox/message/thread identity, unique import constraints and consistent workspace/prospect/message references. New Gmail imports store that identity atomically with the reply, audit and state transition. Existing imports retain their activity markers and conservative duplicate detection. #16 remains open for broader tenant constraints, cross-database provisioning repair and operational recovery. #13 remains open for sent-thread correlation and bounded Gmail payload handling.

## Recommended build sequence

1. **Completed:** migration-backed provider identity (#16 partial, PR #26) and supervised approved Gmail send/reply acceptance (PR #27, #14 implementation). Preserve the historical imported reply alongside structured provider identities; confirm backups before any future schema change.
2. **Current engineering gate (#13, in progress):** require unambiguous, provider-confirmed sent-thread references before importing Gmail replies and bound Gmail JSON/MIME. Historical sender-only matches remain manual. Full CI and Windows acceptance are required before marking this complete; never trigger unattended actions from headers alone.
3. Simplify live/manual/simulated controls and test lifecycle transitions (#21), without refactoring unrelated code.
4. Implement durable, capped follow-up jobs with explicit pause/stop and safe reconciliation (#15), then pilot 5–10 qualified, approved accounts. No automatic sequence before #13.
5. Based on real pilot friction, improve buyer-first discovery (#10) and account qualification/deduplication (#18).
6. Defer hosted deployment until persistence, account recovery, security, CI and privacy gates (#16, #17, #19, #20, #22) are satisfied.

## Scope and business-value definitions

**Local research MVP (usable today):** A human can research 5–10 companies, verify vendor processes, route prospects, prepare reviewed drafts, manually send from their own Gmail, log actual replies and manage next actions. This is a usable manual-assisted workflow, not full automation.

**Supervised communication MVP (locally accepted):** For one owner-approved, inquiry-eligible controlled recipient, OutreachFlow sent one Gmail inquiry and imported one genuine reply with durable identity/history, including a restart check. Offline tests cover failure and duplicate scenarios; this does **not** prove production-grade delivery, unattended reply correlation or all exceptional provider states.

**Automation MVP:** The app runs a limited, consented sequence reliably across restarts and cancels all future sends on reply, opt-out, bounce, owner action or eligibility change. The user can inspect every action and stop the sequence.

**Private beta:** An external non-developer workspace owner can onboard and recover access, discover/qualify leads, connect mailbox, run a supervised pilot, restore their data, and understand privacy and sending limits without requiring direct database repair.

### Suggested pilot scorecard

Measure actual **time-to-verified-approach**, relevant accounts vs false matches, contacts/source provenance, approval interventions, actual sent/delivered/replied outcomes, duplicate-send count (target **zero**), opt-out handling (target **zero** post-opt-out sends), missed replies and warm-lead handoffs. A reply or sale is a business outcome, not a test precondition: evaluate workflow correctness even if no buyer responds.

## Forecast — planning ranges, not commitments

Provider identity and one supervised real dispatch/reply cycle are now implemented and locally accepted. Remaining duration is driven by thread-backed inbox correlation, provider error handling, durable scheduler tests and 5–10 account pilot quality, not by the number of code commits. Public hosting and Google restricted-scope compliance are separate, non-date-certain gates. The critical path is now **thread correlation and bounded payloads → stop/reconcile controls → idempotent follow-ups → real pilot evidence**.

## Current known maintenance and audit evidence

See [AUDIT-2026-11-11.md](AUDIT-2026-11-11.md) for all reviewed source areas, findings, impact and remediation. GitHub issues #10 and #13–#22 are the tracked backlog. One-off notes describing old branches remain historical context, not the source of truth.

## Explicit non-goals until the pilot succeeds

No unattended job applications, login-gated scraping, automated portal submission, unauthorized email campaigns, blanket Gmail send grants, arbitrary tenants in public internet Development mode, or billing/subscription infrastructure before a demonstrable working and safe low-volume pipeline.
