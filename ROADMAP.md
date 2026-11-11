# OutreachFlow roadmap — assessed November 11, 2026

## North star

Build a **configurable B2B opportunity-to-conversation pipeline**: find likely buyers or relevant official processes, record source-backed evidence, verify the correct approach, obtain sender/recipient approval, send a small number of legitimate inquiries, detect replies, stop inappropriate follow-ups and hand off meaningful decisions. Fleet washing is the first proof of value, not a hard-coded product restriction.

**Current position: late local research and authorization prototype, before the first real provider-backed outbound communication milestone.** PRs #1–9 and #11 are merged; read-only Gmail OAuth has had an owner-reported successful connection. All latest main CI passed. Full inbound Gmail import and real send lifecycle are **not yet accepted**. Issue #10 tracks buyer/account-first discovery.

### Current stages and exit gates

| Stage | Current status | Definition of done (DoD) | Blocking dependencies |
| --- | --- | --- | --- |
| 0. Foundation: .NET, auth, workspaces | Local implementation complete; production hardening not done | Two independent users/workspaces, blocked cross-access, local auth/cookie checks and passing smoke tests | Hosted use still gated by #16, #17, #22 |
| 1. Search → process research → owner triage | Functionally implemented and live-pilot classified eight results | User can discover, identify false positives, inspect sources, route without auto-approval or messages, and preserve original evidence | Precision and account-level dedupe improvement #10, #18 |
| 2. Google identity + authorized Gmail read-only | Connection tested by owner; provider-backed reply import unverified in live test | Existing-account link and Google-first registration preserve separate workspaces; correct owner connects Gmail; one genuine reply reviewed, imported **once**, and audited; zero sends | Gmail correlation/reliability review #13 |
| 3. Controlled real communication | **Not started** | Explicit, approved inquiry sends once from correct Gmail sender to authorized test mailbox; provider IDs persist; opt-out/closed/unknown delivery halt; full read/response loop reconciles | #13, #14, #16, #21 |
| 4. Low-volume recurring outreach | **Not started** | 5–10 real, eligible, manually approved pilot accounts; idempotent job queue, send-time rechecks, 1–2 capped follow-ups, replies/negative signals immediately stop schedule, human dashboard matches audit | #15, #14, #13, business qualification #10/#18 |
| 5. Hosted multi-tenant beta | **Not started** | Migration/backup/restore, auth/role recovery, rate limits, privacy and provider compliance, secure secrets, incident monitoring, threat-modelled crawler, isolated tenants, tested deployment/rollback | #16, #17, #19, #20, #22 |
| 6. General SaaS | Out of current build scope | Multiple supported customer verticals, onboarding, billing, support, reliability metrics and successful private beta; external provider approvals complete | Depends on Stage 5 product/market validation |

## Persistence recovery progress (issue #16)

The first recovery slice introduces an **operator-only** `OutreachFlow.RecoveryCli` that makes consistent SQLite snapshots, includes local profile/usage/mailbox files, verifies SHA-256 digests and database integrity, and restores only into a new directory. The operator must stop the app to obtain a coherent *multi-file* snapshot and must encrypt the backup. See [RECOVERY-PLAYBOOK.md](RECOVERY-PLAYBOOK.md).

**This PR's next persistence slice** establishes initial EF Core 10 migrations and model snapshots for both contexts, verifies fresh vs legacy schemas, and offers an explicit, backed-up adoption command for pre-existing `EnsureCreated` databases. The web app still does **not** run migrations at startup. See [MIGRATION-BASELINE.md](MIGRATION-BASELINE.md). #16 remains open for controlled migration rollout, new ownership/provider-ID constraints, and actual old-data recovery acceptance. Issue #13 tracks Gmail message/thread identity; issue #14 tracks approval-gated sending.

## Recommended build sequence (next PRs)

1. **Merge audit hygiene PR #12 after review**: stop startup demo seeding, close lifecycle loopholes, correct README/roadmap and add tests. Verify a fresh local password-owner setup and existing database.
2. **Close Stage 2 acceptance** with a real *owner-confirmed* outbound message to a test recipient, matching Gmail inbox reply, manual one-time import and audit review. Do not call the inbox feature reliable synchronization yet. Work #13 is the long-term correction.
3. **Data-model baseline before sending** (#16): introduce provider message IDs, unique constraints, approved-recipient/suppression model, backup/restore procedure and non-destructive migrations.
4. **One controlled `gmail.send` action** (#14): separate consent, explicit review and confirmation, authenticated sender, idempotent outbox and manual reconciliation on ambiguous failure.
5. **Reconcile inbox thread identity** (#13), then design durable paused follow-ups (#15) and clarify simulated-vs-real UI (#21).
6. **Buyer-first account discovery** (#10) and shared dedupe/qualification (#18) should be prioritized from real pilot results; do not block mailbox correctness on speculative discovery feature complexity.
7. **Limited measurable fleet-washing pilot**, followed by hosting/security gates (#16, #17, #19, #20, #22). Do not enable unsupervised prospect outreach before a low-volume explicit approval pilot succeeds.

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
