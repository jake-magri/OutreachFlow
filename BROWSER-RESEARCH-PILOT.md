# PR #6 — experimental browser-render research pilot

## Purpose

Test whether rendering JavaScript changes the outcome for a public website where the ordinary HTML scanner reports incomplete content. The first regression case is the City of Charlotte supplier-registration page.

**Not an anti-bot bypass:** browser rendering is attempted only for incomplete *non-challenge* HTML. An access challenge or explicit block remains unresolved and must be researched manually. The default read-only crawler remains unchanged.

## How to run on Windows

1. Stop the running app. Back up both local SQLite files and the `research-profiles` folder before switching branches.
2. From the solution directory:

   ```powershell
   git fetch origin
   git switch --track origin/feature/browser-render-research-pilot
   dotnet restore OutreachFlow.slnx
   dotnet build OutreachFlow.slnx
   dotnet run --project OutreachFlow.SmokeTests
   ```

   If the branch already exists locally, use `git switch feature/browser-render-research-pilot`.

3. Install Playwright's Chromium binary for your build configuration:

   ```powershell
   pwsh ./OutreachFlow/bin/Debug/net10.0/playwright.ps1 install chromium
   ```

   If `pwsh` is not installed, use Windows PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File ./OutreachFlow/bin/Debug/net10.0/playwright.ps1 install chromium
   ```

   Install only the browser runtime you need; no AWS server or account is required.

4. Run `dotnet run --project OutreachFlow`, open your existing prospect and click **Render with browser (experimental)**. The original **Research company website** button remains HTTP-only.

## What the experiment does

- Reads `robots.txt` through the existing protected HTTP client and respects the same-site constraints, crawl delay and six-page budget.
- Starts a fresh isolated headless Chromium context for an incomplete HTML page (excluding apparent challenge/block pages), waits up to three seconds for JavaScript, then re-runs the content-quality inspection.
- Intercepts HTTP requests and fulfills permitted GET text resources through the existing DNS-pinning public-IP HTTP fetcher with the same site's robots policy. Rejects other hosts, methods, images/binary types, oversized responses and WebSocket requests. Disables service workers.
- Uses strict per-attempt time and request budgets. If rendering fails or returns another incomplete page, records the warning rather than treating missing keywords as a negative research finding.
- Never logs in, submits forms, contacts companies, sends emails, or creates an application record automatically.

**Important:** This is experimental browser execution of third-party code. It is not yet a security-reviewed production browser sandbox, even with the above restrictions. Do not use this adapter on internal, private, authenticated or untrusted arbitrary URLs outside the existing approved public-site policy.

## Diagnostic retest after first City of Charlotte browser scan

The first owner test fetched one HTML page but showed **zero browser attempts**. Because the browser renderer is intentionally skipped for access-challenge HTML, that result is consistent with an access challenge, but the original message did **not** identify the matching text. Do not claim the exact cause until the next retest.

The research screen now displays **Latest page diagnostics** with HTTP status, bounded page title, readable character count, the exact rule ID, the **literal allowlisted challenge phrase** that matched, and the reason browser rendering was skipped/attempted. These details are also recorded as research warning evidence, without saving raw HTML, cookies or arbitrary page body snippets.

Retest the same City of Charlotte prospect with **Render with browser (experimental)**. Check especially:
- `challenge-title` or `challenge-intro` + the matched phrase → browser was intentionally skipped; verify source manually.
- `minimal-readable-text` or `url-content-mismatch` → browser should be attempted; success or failure must be reported.
- HTTP non-200 → response was inaccessible; browser is not automatically retried.
- `none` → content was judged sufficiently readable (not necessarily relevant); browser may not be needed.

A successful HTTP 200 with an access challenge remains **research incomplete**, not evidence of an application process being absent. Do not alter the browser-safety restrictions to make this one target pass.

## Test outcome decision

For the City of Charlotte prospect, compare the two results:

| Check | HTTP-only | Experimental browser |
|---|---|---|
| Pages fetched | Record number | Record number |
| Pages incomplete | Record number | Record number |
| Relevant source-backed process URL | Record URL/none | Record URL/none |
| Browser attempt and success | Not applicable | From research summary |
| Final disposition | Manual review | Manual review if inconclusive |

**Pass:** Browser successfully renders meaningful supplier-registration instructions and records them as *unverified* references. **Acceptable limitation:** Browser still cannot access a challenge/blocked page and clearly reports incomplete research. **Fail:** Browser exposes off-domain resources, silently declares no process, corrupts records, or submits a form.

GitHub CI exercises an offline JavaScript-rendering fixture with Chromium on this branch, independent of the live City website. Real-world site variability requires manual testing.
