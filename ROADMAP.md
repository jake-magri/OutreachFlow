# OutreachFlow roadmap — assessed November 11, 2026

## North star

Build a **configurable B2B opportunity-to-conversation pipeline**: find likely buyers or relevant official processes, record source-backed evidence, verify the correct approach, obtain sender/recipient approval, send a small number of legitimate inquiries, detect replies, stop inappropriate follow-ups and hand off meaningful decisions. Fleet washing is the first proof of value, not a hard-coded product restriction.

**Current position — November 11, 2026:** local Google sign-in, Gmail authorization, owner-recorded sending and reviewed Gmail reply import have been exercised on the owner's Windows installation. The test reply was classified as declined, recorded with its Gmail audit ID and moved to Suppressed. PRs #23–25 added recovery bundles, EF baselines and migration-aware fresh startup. PR #26 added provider identity and was validated on Windows. Controlled initial Gmail dispatch is implemented behind a disabled local toggle; real-mail acceptance remains pending.

### Current stages and exit gates

| Stage | Current status | Definition of done (DoD) | Blocking dependencies |
| --- | --- | --- | --- |
| 0. Foundation: .NET, auth, workspaces | Local implementation complete; production hardening not done | Two independent users/workspaces, blocked cross-access, local auth/cookie checks and passing smoke tests | Hosted use still gated by #16, #17, #22 |
| 1. Search → process research → owner triage | Functionally implemented and live-pilot classified eight results | User can discover, identify false positives, inspect sources, route without auto-approval or messages, and preserve original evidence | Precision and account-level dedupe improvement #10, #18 |
| 2. Google identity + authorized Gmail read-only | Owner tested connection, matching reply import and decline suppression; concurrent import protection covered by offline tests | Existing-account link and Google-first registration preserve separate workspaces; correct owner connects Gmail; one genuine reply reviewed, imported **once**, and audited; zero sends | Gmail correlation/reliability review #13 |
| 3. Controlled real communication | Initial approved-send pilot implemented; live acceptance pending | Explicit, approved inquiry sends once from correct Gmail sender to authorized test mailbox; provider IDs persist; opt-out/closed/unknown delivery halt; full read/response loop reconciles | #13, #14, #16, #21 |
| 4. Low-volume recurring outreach | **Not started** | 5–10 real, eligible, manually approved pilot accounts; idempotent job queue, send-time rechecks, 1–2 capped follow-ups, replies/negative signals immediately stop schedule, human dashboard matches audit | #15, #14, #13, business qualification #10/#18 |
| 5. Hosted multi-tenant beta | **Not started** | Migration/backup/restore, auth/role recovery, rate limits, privacy and provider compliance, secure secrets, incident monitoring, threat-modelled crawler, isolated tenants, tested deployment/rollback | #16, #17, #19, #20, #22 |
| 6. General SaaS | Out of current build scope | Multiple supported customer verticals, onboarding, billing, support, reliability metrics and successful private beta; external provider approvals complete | Depends on Stage 5 product/market validation |

## Persistence recovery progress (issue #16)

The first recovery slice introduces an **operator-only** `OutreachFlow.RecoveryCli` that makes consistent SQLite snapshots, includes local profile/usage/mailbox files, verifies SHA-256 digests and database integrity, and restores only into a new directory. The operator must stop the app to obtain a coherent *multi-file* snapshot and must encrypt the backup. See [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md).

The initial migrations and backup-first adoption CLI are in place. Fresh installations use EF migrations at startup. Existing installations use an explicit recovery CLI `upgrade` command to validate their recorded schema, back up both databases and auxiliary state, adopt legacy baselines, and apply incremental migrations. See [MIGRATION-BASELINE.md](MIGRATION-BASELINE.md).

The provider identity slice adds mailbox/message/thread identity, unique import constraints and consistent workspace/prospect/message references. New Gmail imports store that identity atomically with the reply, audit and state transition. Existing imports retain their activity markers and conservative duplicate detection. #16 remains open for broader tenant constraints, cross-database provisioning repair and operational recovery. #13 remains open for sent-thread correlation and bounded Gmail payload handling.

## Recommended build sequence

1. Provider identity upgrade validated on Windows. Preserve both the historical reply and new structured identity.
2. Validate the controlled Gmail send pilot (#14) using [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md). Explicit permission, immutable approval, persistent outbox, limits and uncertain-result reconciliation are implemented; real Gmail acceptance is still pending.
3. Link incoming replies to the provider-confirmed sent conversation (#13), with ambiguous matches presented for review.
4. Add durable, capped follow-ups (#15) and clarify real-message controls (#21).
5. Improve buyer-first discovery (#10) and account qualification/deduplication (#18) using pilot results.
6. Run the limited fleet-washing pilot, then address hosted deployment work (#16, #17, #19, #20, #22).

## Scope and business-value definitions

**Local research MVP (usable today):** A human can research 5–10 companies, verify vendor processes, route prospects, prepare reviewed drafts, manually send from their own Gmail, log actual replies and manage next actions. This is a usable manual-assisted workflow, not full automation.

**Communication MVP (next target):** Given one approved, inquiry-eligible contact and a verified Gmail sender, OutreachFlow sends exactly one authorized message and tracks a genuine response without duplicates or wrong-tenant access. Unsafe, ambiguous or undeliverable cases stop for review.

**Automation MVP:** The app runs a limited, consented sequence reliably across restarts and cancels all future sends on reply, opt-out, bounce, owner action or eligibility change. The user can inspect every action and stop the sequence.

**Private beta:** An external non-developer workspace owner can onboard and recover access, discover/qualify leads, connect mailbox, run a supervised pilot, restore their data, and understand privacy and sending limits without requiring direct database repair.

### Suggested pilot scorecard

Measure actual **time-to-verified-approach**, relevant accounts vs false matches, contacts/source provenance, approval interventions, actual sent/delivered/replied outcomes, duplicate-send count (target **zero**), opt-out handling (target **zero** post-opt-out sends), missed replies and warm-lead handoffs. A reply or sale is a business outcome, not a test precondition: evaluate workflow correctness even if no buyer responds.

## Forecast — planning ranges, not commitments

With one active developer and AI-assisted implementation, Stage 2 validation/hygiene can fit a short focused iteration. **Provider-backed real send + durable data model** may take roughly 2–4 focused engineering weeks, and safe scheduled automation plus a supervised pilot another 2–5, depending on testing, OAuth/provider behavior and scope. Public hosting and Google restricted-scope compliance can take substantially longer and are **not date-certain**; avoid estimating a public release date from today's working local OAuth connection.

The true critical path is **persistent provider identity → safe dispatch → reply correlation → pause/reconcile → automated follow-ups**, not visual UI polish.

## Current known maintenance and audit evidence

See [AUDIT-2026-11-11.md](AUDIT-2026-11-11.md) for all reviewed source areas, findings, impact and remediation. GitHub issues #10 and #13–#22 are the tracked backlog. One-off notes describing old branches remain historical context, not the source of truth.

## Explicit non-goals until the pilot succeeds

No unattended job applications, login-gated scraping, automated portal submission, unauthorized email campaigns, blanket Gmail send grants, arbitrary tenants in public internet Development mode, or billing/subscription infrastructure before a demonstrable working and safe low-volume pipeline.
