# 01 — Market, alternatives, and research evidence
**Research date:** 2026-11-11; targeted to commercial services in Charlotte, NC. This is desk research and architecture positioning, not proof of local purchasing interest.

## Problem hypothesis
Owner-operated service businesses pursuing commercial contracts may struggle to (1) identify appropriate commercial accounts, (2) distinguish open vendor applications from generic inquiries, (3) follow up consistently, and (4) interpret which responses demand human action. These are hypotheses requiring first-person validation later. No assumed market size, conversion rate, customer revenue or willingness to pay.

### Target persona (assumption)
- Primary: owner/operations lead of a 2–20 person janitorial, landscaping or exterior-services company, selling recurring B2B contracts.
- Operating conditions: limited sales staff, owner responsible for delivery and business development; prefers a bounded action queue over a full CRM.
- Buyer/decision makers: unknown until actual interviews; do **not** assume everyone with a business email has procurement authority.
- Secondary vertical for generality test: small independent software services team, using the same search engine/profile with different context.

### Alternatives and implications
| Alternative | Verified capability | Consequence for product |
| --- | --- | --- |
| HubSpot Sales Hub | Email sequences, tasks and optional automatic unenrollment on reply | Scheduling and stop-on-reply alone are not unique differentiators |
| Apollo | Sequences with reply-based stopping/pausing and account/contact workflows | Generic sales engagement is crowded; avoid competing on volume |
| Native Gmail + spreadsheet | Gmail sends and replies; spreadsheet holds manually entered leads | Lower complexity but no verified vendor-route research or structured handoff |
| Contractor/job management products | Some support CRM/quote follow-ups | Need to show exactly how commercial vendor onboarding differs, not assume lack of overlap |

Sources (vendor documentation):
- HubSpot sequence creation: https://knowledge.hubspot.com/sequences/create-and-edit-sequences
- HubSpot automatic unenrollment: https://knowledge.hubspot.com/sequences/unenroll-from-sequence
- Apollo sequence overview: https://knowledge.apollo.io/hc/en-us/articles/4409237165837-Sequences-Overview
- Apollo reply handling: https://knowledge.apollo.io/hc/en-us/articles/30919852777229-View-and-Respond-to-Emails

### Charlotte-specific workflow anchor: supplier registration ≠ email
The City of Charlotte publishes a formal supplier registration process, with required supplier fields, contact information, and registration forms. This substantiates a distinct **application route** but does not establish that any particular contractor will win a contract or that a vendor portal is appropriate for every buyer.
Source: https://www.charlottenc.gov/Growth-and-Development/Doing-Business/Supplier-Registration

Design implications:
- The system must distinguish **buyer discovery** from **vendor/procurement process discovery**.
- A found city registration link should create a human application task after verification; it must not automatically turn into a cold email.
- Private accounts may not publish supplier routes; lack of indexed evidence is **Unknown**, not `NoProcessExists`.

### Email policy anchor
FTC states that CAN-SPAM applies to commercial B2B mail, not just high-volume campaigns: accurate headers and subjects, valid postal address, clear opt-out method, and honoring opt-outs are core requirements. This evidence motivates conservative send-time gates; no legal compliance certification is implied.
Source: https://www.ftc.gov/business-guidance/resources/can-spam-act-compliance-guide-business

### Positioning to test
**Commercial account-to-conversation assistant**: investigate likely commercial buyers, identify the appropriate official approach, manage owner-approved inquiries and follow-ups, and surface the next human action. Better than generic sequencing only if customer evidence later shows meaningful time saved or access to otherwise overlooked opportunities.

### Evidence classification
- **Verified from source:** products offer sequence automation; Charlotte has supplier registration; FTC commercial-mail requirements exist.
- **Verified from repository:** localhost Blazor/SQLite, Tavily indexed search, official-site research, owner-approved Gmail and scheduled follow-ups implemented, deterministic CI.
- **Hypothesis:** these workflows are painful and valuable to target contractors; 2–20 employees is the right segment; a monthly software price is justified.
- **Not known:** reachable number of buyers in Charlotte, competitors' relative suitability, conversion, pricing, retention, ROI, acquisition cost, buyer willingness.

## Later customer research, expressly deferred
After the owner declares the software ready: 10–20 non-leading discovery interviews, workflow observations, measurable process benchmarks, and a willingness-to-pay pilot. No reaching out, enrollment, invitations or survey automation in this specification phase.

## Change control
Market evidence can change the target positioning but must **not silently expand M1–M5 engineering**. Log a hypothesis change in 06-DECISIONS and file a separate issue if new work is justified.
