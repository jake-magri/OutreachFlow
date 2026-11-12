# 05 — AI implementation handoff and anti-context-drift protocol
**This document is the next-session control file.** Treat it as the authoritative task protocol, not an invitation to expand the roadmap.

## Mandatory opening steps
1. Read `docs/commercial-contract-pilot/{README,02-PRD,03-ENGINEERING,04-ACCEPTANCE,05-AI-HANDOFF,06-DECISIONS}.md` and linked GitHub issue.
2. Confirm current `main` SHA, open PRs/branches, issue state, last CI. Never assume an earlier branch was merged.
3. Pick the **first uncompleted milestone** in order: #35 → #36 → #37 → #38 → #39. Review existing code before modifying it; do not create another implementation for functionality already merged.
4. Summarize in one paragraph: exact business requirement IDs, issue number, files to touch, files deliberately untouched, CI acceptance IDs, dependencies and risks. Then implement without asking routine re-confirmation.

## One PR per milestone
- Start from updated `main`; branch `feature/<issue-number>-<specific-name>`.
- Commit only files in scoped issue plus necessary directly related tests/docs. Never refactor full architecture, replace working providers, or introduce a new library incidentally.
- Tests should be offline, deterministic and teardown-friendly; reuse existing `OutreachFlow.SmokeTests`, Playwright harness, fake clock, Gmail fakes and recovery tools.
- Run build, smoke, Windows recovery, Playwright at PR head. Fix failures before merge.
- PR description includes: requirement IDs, AT IDs, exact changed files, migration consequences, state transition risks, how tested, and what remains out of scope.
- If a hidden dependency arises: create a **blocking question** under the issue and a minimal ADR; do not silently implement an adjacent epic.
- Do not close feature issue until its DoD and tests pass. **Docs-only PR may merge while feature issues remain open.**

## Stop conditions — absolute
- New functionality not in issue scope: **stop adding it** and log as follow-up.
- Customer data, OAuth secrets, real Gmail send or live customer discovery requested by CI/test: **do not run**; use fake and report blocker.
- Existing data migration needed without verified backup/restore: stop, draft ADR and get migration approval.
- Attempt to convert `Unknown` evidence into verified buyer/vendor, add unsupported fleet counts, or re-enable suppressed contacts: fail closed.
- Green CI but test scenario mismatches observed workflow: not done.
- Mid-PR desire to redesign UI, adopt AI agent, add hosting, charge cards, gather emails: out of scope.

## Phase-by-phase handoff
| Order | Issue | Goal | Ready when | Stop when |
| --- | --- | --- | --- | --- |
| 1 | #35 M1 | Truthful dashboard/settings state | main builds; existing Gmail scheduler merged | AT-01/02 green |
| 2 | #36 M2 | Commercial email policy gate | M1 state labels available, sender policy understood | AT-03/04/05 plus AT-15 green |
| 3 | #37 M3 | Buyer-first search mode | existing 2x5 planner and budget tests reviewed | AT-06/07/08 green |
| 4 | #38 M4 | Conservative account/evidence grouping | M3 candidate shape settled | AT-09/10; AT-16 if schema change green |
| 5 | #39 M5 | Owner queue and walkthrough | previous milestone interfaces stable | AT-11/12/13/14/17 green |

## Fast checkpoint template (append to each PR/issue)
```md
### Milestone checkpoint
Issue: #__
Requirement IDs: BR-__
Acceptance IDs: AT-__
Base SHA:
Branch / PR:
Implemented in this PR:
Explicitly NOT implemented:
Tests passed (with run link):
Data migration / backup effects:
Observed limitations / blockers:
Next uncompleted milestone:
```

## User-facing message template
"Implemented <issue> on <branch>/<PR>; verified AT IDs; CI green or precise failing gate. No live Gmail was contacted. No other features expanded. Ready for review/merge."

## Project continuity
- `README.md` and `ROADMAP.md` remain product-level entry points; this folder is the canonical implementation specification for these milestones.
- `AUDIT-2026-11-11.md` is a historical audit, not a current product truth snapshot.
- Older pilot notes sometimes claim "no real Gmail" or "no scheduled sending" because they predate merged PRs; use merged source and latest docs to resolve, and correct misleading *active* UI labels under M1 only.
- Keep #10/#18/#21 as existing master backlog issues; #37/#38/#35 are narrow milestone slices, not duplicate business capabilities.
- If a GitHub Projects board is unavailable through connector, issues and this ordered table are the deterministic project board; never invent board edits.

**AI autonomy:** implement all within selected ticket and fix CI regressions autonomously. **AI limits:** do not expand requirements, use external real customer systems, or imply future work will complete without explicit action.
