# Historical PR #8 — Discovery quality and source-linked process evidence

> **Historical development benchmark.** PR #8 is merged and subsequent classification fixes landed in PR #9. For current product status see [ROADMAP.md](ROADMAP.md).

## Purpose

Improve the usability of indexed research without adding an LLM. The first Tavily pilot located useful procurement sources but also surfaced wholesale suppliers selling goods and generated a redundant `supplier supplier` search.

## What changed

- Query planning selects **distinct buyer actions** (vendor registration, subcontractor/prequalification or procurement opportunities) rather than pairing arbitrary keyword arrays. Employment research uses job-oriented actions.
- The Discover page provides a **transparent intent hint** and explanation for each candidate. Buyer-onboarding language ranks above ordinary suppliers; wholesale-only results are heavily down-ranked; third-party guides remain reference material rather than official forms.
- The existing bounded, robots-aware site scan now records likely **process/application links**, including link text, the source HTML page, the destination, and whether the destination is same-site or cross-domain.
- The saved evidence explicitly says **unverified**. No search snippet, source page or hyperlink alone proves that a particular organization owns a process or that the form is still active. Existing owner review remains mandatory.
- There are **no extra Tavily API calls** per search, no third-party form fetches, no email, no LLM, no new database tables and no changes to hosting requirements.

## Offline benchmark

Use these examples from the first November 11, 2026 pilot as qualitative regression tests:

| Sample | Expected classification |
|---|---|
| NC eProcurement — Registering in eVP | Potential buyer registration / procurement; **unverified** |
| Pipe Supply in Charlotte — American Stainless | Selling goods / possible false match; lower rank |
| SupplierDiversity — How to Become a City of Charlotte Vendor | Third-party guide; cannot establish official relationship |
| UNC Charlotte — Vendor Opportunities | Potential procurement page; verify applicability |

## Local acceptance

1. Pull this feature branch and run in Development with the existing operator-owned Tavily secret. No new secret is needed.
2. Try the general B2B and fleet profiles. Review the generated queries; there should be no `supplier supplier` repetition and the two queries should target distinct procurement actions.
3. Compare rank and intent hints for registration pages, wholesale vendors, generic contact pages and third-party articles.
4. On an existing prospect with a source-linked application form, run **Research company website**. Check for a **Process link:** evidence record that names both the linking page and the destination. External linked destinations are *not fetched*.
5. Verify that no such evidence is marked `Owner verified` automatically, and that the app still requires human verification to mark an official application/portal.

## Definition of done and remaining limitations

Offline tests cover planner variety, example buyer-versus-wholesaler ranking, malformed/unsafe links, and cross-domain provenance persistence. UI should present uncertainty explicitly.

**Still not supported:** automated official-domain identity verification, checking that third-party application forms are currently available, verifying individual eligibility, PDFs, trustworthy supplier listings with ambiguous wording, or production-scale benchmarking. Those should be separate work with calibrated metrics and targeted approval controls.

Scope is intentionally narrow to maintain stable local workflows and keep request usage predictable.