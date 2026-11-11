# Isolated Playwright browser acceptance

The C# browser harness drives **the actual Blazor UI** against a disposable local HTTPS app and fresh SQLite databases. It is intended for repeatable AI-assisted development: CI runs on every PR, and a developer can execute it with one command rather than manually repeating UI clicks.

## Quick start (Windows PowerShell)

Run from the repository root with .NET 10 installed:

```powershell
dotnet build OutreachFlow.slnx -c Release
pwsh ./OutreachFlow.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet dev-certs https --trust
dotnet run --project OutreachFlow.BrowserTests -c Release --no-build
```

If `pwsh` is unavailable on Windows, use the installed PowerShell 7 executable or run the generated `playwright.ps1` with an appropriate PowerShell host. This harness does not require Node, an npm project, a paid testing service, or new app secrets.

The suite starts the compiled `OutreachFlow.dll` in a random loopback HTTPS port with a unique temporary working directory and explicit `ConnectionStrings__*` settings. It creates a test-only owner with a nonproduction password, uses example.org fixture addresses, drives real form submissions and Blazor components, and destroys the temporary data after completion.

**No live Gmail or Tavily**: the harness deliberately overrides provider credentials with empty values and sets `Gmail:EnableSending=false`. It does not call public websites, **send real emails**, read your mailbox or modify your real `outreachflow.db` databases. The test uses the simulated batch mode and never approves Gmail sending. Do not configure this harness to use production databases.

## What runs today

1. First-owner creation and authenticated dashboard navigation.
2. Unconfigured Gmail and Tavily panels, including disabled Discover search.
3. One-input batch import → draft → simulated initial attempt with idempotent replay, plus standard prospect import and deduplication.
4. Review finding → draft → **simulated** send → simulated declined reply → suppressed status.
5. New workspace creation, absence of another workspace's prospects, and return to the original workspace.
6. Logout and protected-page redirect to sign-in.

The browser driver uses role/text selectors where practical, and scopes repeated elements to their forms. The underlying app may change wording or structure: when an intentional UI change breaks a selector, update the test alongside the UI in the same PR. Prefer selectors based on accessible labels and semantics over position or CSS. The current research form still has some unlabelled inputs, so a few selectors are positional; improve app accessibility before extending them.

## CI and failure investigation

`.github/workflows/browser-tests.yml` installs pinned Chromium, builds the solution and runs this harness on every PR. It has no provider credentials. On failure it uploads:

- `failure.png`: full-page screenshot of the failing page.
- `failure-trace.zip`: Playwright trace for step-by-step inspection.
- `app.log`: last 200 lines of disposable localhost app output.

These files are ignored by Git and retained for only seven days in GitHub Actions. Traces can contain fixture form data and page content; **never use real secrets or customer records in browser fixtures**. To inspect a trace, open it in Playwright's local trace viewer with the matching browser tool version.

The harness does not replace existing offline smoke tests, migration tests or the real-Google read-only acceptance for issue #13. Provider-thread logic, quotas, concurrency, malformed MIME and mailbox failure handling belong in deterministic service-level fixtures. Browser tests exercise navigation and state integration, not external Google delivery.

## Maintenance rule

When a PR changes authentication, routing, workspace boundaries, prospect state or critical review actions, add or adjust one deterministic browser scenario in the same PR. Keep browser tests intentionally short (high-value user paths) and keep detailed edge cases in service-level tests. Never introduce test-only bypass routes or write fixture data into a developer's regular database.
