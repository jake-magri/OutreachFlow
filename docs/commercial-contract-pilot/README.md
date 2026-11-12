# Commercial contract acquisition — canonical planning pack
**Status:** Proposed engineering specification, 2026-11-11. **Planning only:** no application code, migrations, live data, or new Gmail authorization are changed in this PR.
**Parent epic:** #34. **Implementation work:** #35 M1, #36 M2, #37 M3, #38 M4, #39 M5. Existing master issues #10, #15, #16, #18, #21, #22 remain in place.
**Product hypothesis:** a configurable commercial-services account-to-conversation workflow for owner-led teams; *not validated product-market fit*. Initial verticals for test content are janitorial, landscaping and exterior/fleet washing in the Charlotte market. No external outreach until the owner chooses to do so.

## Read order (one source of truth)
1. [01-MARKET.md](01-MARKET.md) — verified alternatives, local procurement facts, positioning hypotheses, research gaps.
2. [02-PRD.md](02-PRD.md) — personas, problem, business requirements (BR), flows, launch boundaries and explicit non-goals.
3. [03-ENGINEERING.md](03-ENGINEERING.md) — architecture, data contracts, security/invariants, interfaces, migrations and sequencing.
4. [04-ACCEPTANCE.md](04-ACCEPTANCE.md) — acceptance criteria and automated test fixtures with IDs (AT).
5. [05-AI-HANDOFF.md](05-AI-HANDOFF.md) — deterministic next-session execution instructions, PR constraints, stop rules and traceability.
6. [06-DECISIONS.md](06-DECISIONS.md) — binding decisions, unresolved questions and no-expansion register.

## Existing authoritative sources (do not duplicate)
- [README](../../README.md), [ROADMAP](../../ROADMAP.md), [AUDIT-2026-11-11](../../AUDIT-2026-11-11.md), [TEST-CHECKLIST](../../TEST-CHECKLIST.md)
- [GMAIL-SENDING-PILOT](../../GMAIL-SENDING-PILOT.md), [SCHEDULED-GMAIL-PILOT](../../SCHEDULED-GMAIL-PILOT.md), [BROWSER-ACCEPTANCE](../../BROWSER-ACCEPTANCE.md)
- [MIGRATION-BASELINE](../../MIGRATION-BASELINE.md), [RECOVERY-PLAYBOOK](../../RECOVERY-PLAYBOOK.md)
- #10: buyer-first discovery; #18: conservative account identity; #21: lifecycle/UX truth; #15: scheduled Gmail; #22: hosted/privacy gate.

## Success
An owner can demonstrate **three honest, reproducible workflows** through a local application: buyer review → inquiry/reply; official supplier portal → application task; unsafe/ambiguous candidate → no send. Each capability must be genuinely implemented and demonstrably tested, not simulated and described as live.

## Product gates
- **D0 docs-ready:** all requirement IDs, interface assumptions, test fixtures, dependencies and explicit non-goals accepted.
- **D1 demo-ready:** M1, M3, M4, M5 complete and tested in isolated mode; M2 also required before *real commercial* email demo.
- **D2 supervised live pilot-ready:** M2 passed; backup/recovery verified; Gmail permissions explicit; owner approves recipients; #15 failure states reviewed.
- **D3 public hosted readiness:** separate work under #16/#17/#19/#22; explicitly **not** part of this planning pack.

No pricing/traction/investor claims are proof of product-market fit, and no paid external customer acquisition is part of this phase.
