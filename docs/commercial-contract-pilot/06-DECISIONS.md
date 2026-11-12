# 06 — Decision log and unresolved questions
**Authority:** Owner's choice to complete the implementation/spec before local customer outreach. This document prevents an AI implementation session from silently rewriting requirements based on a plausible idea.

| ID | Status | Decision / uncertainty | Impact |
| --- | --- | --- | --- |
| DEC-01 | **LOCKED** | No customer-contact campaign, in-person research event, marketing send, or PMF claim until owner calls the product ready | No external outreach task in M1–M5 |
| DEC-02 | **LOCKED** | Candidate wedge: Charlotte commercial janitorial/landscaping/exterior/fleet wash contractors; hypothesis only | Industry test data, not hardcoded core models |
| DEC-03 | **LOCKED** | Existing C#/.NET 10 Blazor/EF SQLite/Gmail/Tavily architecture stays in place | No rebuild in React, no cloud, no LLM dependency |
| DEC-04 | **LOCKED** | Source discovery results remain unverified until owner confirmation | No auto-contact from search snippets |
| DEC-05 | **LOCKED** | Distinguish buyer account discovery vs official supplier process | M3 mode toggle, M4 owner routing |
| DEC-06 | **LOCKED** | Simulation, manual attestation and Gmail accepted are separate | M1 and M5 UI labels |
| DEC-07 | **LOCKED** | Commercial-email safety is a live-send release gate; no presumed compliance | M2 centralized checks + tests |
| DEC-08 | **LOCKED** | Default localhost-only; scheduler opt-in; no real Google/contacts in CI | M1–M5 hard boundary |
| DEC-09 | **LOCKED** | Conservative normalization; do not merge branches by domain alone or rewrite Gmail provider IDs | M4 |
| DEC-10 | **LOCKED** | One issue/PR at a time, small diffs, pass acceptance and stop; all features must map to BR/AT | AI handoff |
| Q-01 | **OPEN, NON-BLOCKING** | Which commercial-service subsegment has actual pain/willingness to pay? | Later owner-led market discovery only |
| Q-02 | **OPEN, DEFERRED** | Software subscription vs assisted service pricing, packaging, sales channel | Later PMF work; no billing code |
| Q-03 | **OPEN, CONDITIONAL** | Does M4 need a new Account entity, or can scoped evidence + normalization suffice? | Prefer current model, ADR if fixture demands table |
| Q-04 | **OPEN, CONDITIONAL** | Is commercial outreach template policy best expressed as return-email opt-out vs future hosted unsubscribe link? | M2 choose working localhost-compatible path, comply with appropriate policy; do not launch public endpoint |
| Q-05 | **OPEN, DEFERRED** | Public Gmail OAuth verification and production hosted secrets/retention? | #22 hosted gate, outside demo sprint |

## Change-request rule
Any change to a LOCKED decision must be proposed as:
1. What observed test/customer evidence motivates it?
2. Which BR, AT, issue, and code boundaries change?
3. Which previously promised work is removed or deferred to maintain scope?
4. What is the smallest safe alternative?
5. Explicit owner approval before edits.

## Definition of `done`
- **Spec done**: this folder complete and linked, issues traceable, no contradictory active roadmap descriptions.
- **Engineering milestone done**: corresponding issue's AT IDs green and code merged; not simply a green presentation or passing unrelated smoke tests.
- **Product demo ready**: D1 threshold in PRD, honest offline walkthrough, no invented performance results.
- **Ready to talk to customers**: owner decision outside engineering acceptance, not an AI inference.
