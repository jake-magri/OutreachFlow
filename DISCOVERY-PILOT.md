# PR #7 — deterministic opportunity discovery pilot

## Research questions

Can a low-cost indexed web search improve the accuracy of finding the **correct official route** to an opportunity, without an LLM? Can the user distinguish a verified application process from an indexed URL candidate?

## Safe local procedure

1. Configure a free Tavily Researcher key in .NET user secrets on the **operator's machine**. Keep pay-as-you-go disabled at the provider. No workspace user supplies a key.
2. Open the Discover page and run a profile search. A click spends at most two basic-search request reservations and retrieves up to 10 listings. Check the shared monthly budget before and after.
3. Inspect one candidate's URL manually, and add it to prospects. Confirm the new prospect has zero attempts and `ResearchNeeded`/`NotReviewed` rather than automatically assuming an official process.
4. Run **Research company website** or manually inspect the official site. Record the source, whether the site actually links to the application form, and whether the destination is still live. Search snippets alone cannot establish ownership.
5. Repeat across fleet, construction, software-contracting and employment profiles. Never send email or submit a form during this pilot.

## Benchmark cases

| Category | Target | Expected behavior |
|---|---|---|
| Public procurement | City of Charlotte supplier registration | Discover an official supplier source; treat direct blocked page as incomplete; manually verify the registration form relationship |
| Fleet services | Charlotte government vehicle repair opportunity | Search may return relevant vehicle keywords but human should reject as unrelated to washing |
| Construction | QC General Contractors trade partners | Discover trade-partner process; verify requirement details before assigning an application task |
| Construction | Crowder subcontractor prequalification | Differentiate prequalification instructions from ordinary contact email |
| Construction | C3 Contracting vendor application | Find a genuine supplier form and distinguish a web form from a general inquiry |
| Software services | Yonda Solutions partnerships | Determine whether partnership/teaming, not employment, is intended |
| Technology services | Tech-Army vendor network | Distinguish formal technology supplier onboarding from a job listing |
| Employment | Spur Logistics Charlotte delivery | Employment link should lead to manual job application, never cold B2B outreach |
| Employment | Johnson Brothers non-CDL delivery driver | Third-party ATS may require manual browser review; search URL does not prove job is still open |

These are **test targets**, not guaranteed current commercial opportunities or an assertion that any listed employer is hiring.

## Metrics

- Precision@5: number of genuinely relevant inspected URLs among top five suggestions
- Official-source precision: number of correctly associated organizational/process URLs divided by manually inspected suggested URLs
- Wrong-route rate: number of employment/registration/contact types assigned incorrectly
- False absence: number of known official processes incorrectly classified as not found
- Manual review minutes per useful opportunity
- Search credits consumed per verified relevant process

## Provider and scope limits

- Tavily search results are indexed suggestions and must be reviewed against live official sources.
- No full-content search indexing, automatic scraping of restricted sites, LLM synthesis, automatic outreach, or third-party form submission.
- Search titles/snippets are ephemeral in the active UI, not stored as research evidence. Verify provider terms before production or commercial use.
- The application-local monthly budget is one-process only; future hosted use needs an atomic multi-instance quota and rate controls.
- The next milestone should focus on automatic evidence relationships: confirmed official-site links to external application forms, conflicting-source warnings, and freshness metrics. Those are not yet verified by a generic search index.