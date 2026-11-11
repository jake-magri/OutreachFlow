# Diverse real-site research pilot (manual, read-only)

> **Historical real-site research scorecard.** Subsequent PRs implemented on-demand Tavily indexed search and Gmail read-only integration; the statements below that these are absent describe the earlier pilot stage. For current code see [README.md](README.md).

**Purpose:** check whether profile-specific keyword rules surface relevant official process pages and contacts across niches. These links were independently located on November 10, 2026. They are **not** a list of guaranteed contracts, qualified customers or approved outreach recipients. Sites can change; verify each source and its terms before scanning. Do not submit an application, contact anyone, or send real email during this classifier pilot.

## Instructions

1. In **Settings**, select the specified research profile, load the preset and **Save research profile**.
2. Import one prospect in the matching workspace using a company name, branch, and the **exact page URL** below; leave email blank unless verified. Use a separate record for each site.
3. Click **Research company website**. Check recorded pages, candidate links, email source links, errors/robots restrictions, and whether useful terms were surfaced. The scanner does not run scripts, parse PDFs, or follow external domains.
4. Compare the observed result to the expected test signal below. A positive keyword match is **not** proof of eligibility or that a contract/job is open. Record false positives, omissions and sites blocked by protections.
5. Use manual review only; do not initiate outbound sequences for these research test records.

| # | Profile | Company and exact public test URL | Expected signal / negative control |
|---|---|---|---|
| 1 | General B2B | City of Charlotte supplier registration — https://www.charlottenc.gov/Growth-and-Development/Doing-Business/Supplier-Registration | Formal supplier form and requirements. Should hand off to official registration, not a generic inquiry. |
| 2 | Commercial fleet | City of Charlotte contract opportunities — https://www.charlottenc.gov/Growth-and-Development/Doing-Business/Contract-Opportunities/Motorcycle-ATV-and-Utility-Vehicle-Repair-and-Related-Services | Fleet-related solicitation but **repair**, not fleet washing. A useful **false-positive/fit** test; site labels it upcoming. |
| 3 | Construction | QC General Contractors trade partners — https://www.qcgeneralcontractors.com/tradepartners | Trade partner eligibility requirements and business email addresses; verify applicability to relevant services. |
| 4 | Construction | Crowder subcontractor prequalification — https://www.crowderusa.com/subcontractor-prequalification | Explicit email-based *request for access* to formal prequalification portal; do not confuse it with an invitation to send a generic sales email. |
| 5 | Construction | C3 Contracting vendor application — https://c3-contracting.com/vendor-application | Website form requesting company details, W-9 and insurance; handoff, never automated form submission. |
| 6 | Independent software contracting | Yonda Solutions partners — https://www.yondasolutions.com/partners | Independent software/data subcontractor and teaming introduction; may favor contact/intro rather than formal supplier registration. |
| 7 | Technology vendor | Tech-Army vendor network — https://tech-army.org/sign-up | Multi-step vendor onboarding form; verify role and eligibility, do not submit automatically. |
| 8 | Delivery job research | Spur Logistics Charlotte — https://spurlogistics.com/clt/ | Employer driver hiring site with contact details. Manual job application only, not B2B outreach. |
| 9 | Delivery job research | Johnson Brothers driver role — https://johnsonbrothers.wd5.myworkdayjobs.com/JohnsonBrothers/job/Charlotte-NC---Operations/Non-CDL-Delivery-Driver_R-260814-2157 | ATS-hosted application (possible JavaScript/robots limitation); verifies how scanner handles externally hosted/dynamic job pages. |

**Research notes:** City Charlotte supplier registration, QC trade partners, Crowder prequalification, C3 vendor form, Yonda partners, Tech-Army vendor onboarding, Spur hiring and Johnson Brothers Workday job page were directly indexed with the process details described above. No links here establish that a given prospect is actively buying the reader's service.

## Observed pilot issue #1: City of Charlotte supplier registration

**Initial local run (reported by owner):** HTTP 200 HTML was counted as one checked page, but the scanner returned **zero candidate references and zero email contacts**, despite this being a supplier-registration test URL. This is a **false negative / inconclusive extraction**, not evidence that the City lacks a supplier process.

**PR #5 remediation:** pages with unusually little readable content, obvious block/challenge HTML, or a relevant URL with no matching readable terms produce a saved **Research warning: content incomplete** and an explicit inconclusive summary. The scanner no longer treats those pages as meaningful evidence of process absence. It does **not** bypass website defenses, execute browser JavaScript, or guarantee that the City page becomes parseable.

**Retest:** scan the City entry again using the same profile. Expected acceptable results are either a genuine, source-backed supplier-process candidate or an explicit *research incomplete* warning with recorded page title, readable-character count and reason. A bare "no matching process" with zero warnings remains a bug. Verify the real official process in a browser and record an application handoff manually.

## Record this scorecard for each site

- Website reachable? (yes/no; record reason)
- Relevant page found? (yes/no/uncertain)
- Correct process type? (form, portal, contact instruction, unclear)
- Published contacts correctly extracted? (number; appropriate or not)
- False-positive links? (number and why)
- Important missed instructions? (text/link)
- Human minutes spent verifying
- Verdict: useful / needs tuning / inconclusive due to blocked or incomplete scan

**Decision gate:** after at least eight tests across three profiles, update preset terms based on observed misses and false positives. Don't automatically promote `NoProcessFound` or authorize outbound sending based on a bounded crawl.

## Future evaluation criteria

The app currently accepts only manually imported URLs. Live keyword web-search sourcing, web-wide crawling, automated application submissions and actual mailbox delivery remain out of scope for PR #5.
