# OutreachFlow — website research increment

## Apply the update to the solution that already builds

Your Visual Studio solution root is the directory containing `OutreachFlow.slnx`, the `OutreachFlow/` web project, and the `OutreachFlow.SmokeTests/` test project.

**Option A — small patch (recommended):**

1. Close the running application and stop debugging. Back up your project and any `outreachflow.db` you have been using; do not delete an existing database.
2. Extract `OutreachFlow-research-patch.zip` **into the solution root**, keeping its `OutreachFlow/` and `OutreachFlow.SmokeTests/` subfolders. Approve replacement of the listed source files. Do not extract it into the *inner* `OutreachFlow/` web-project folder.
3. Reopen `OutreachFlow.slnx` and run:

   ```powershell
   dotnet restore OutreachFlow.slnx
   dotnet build OutreachFlow.slnx
   dotnet run --project OutreachFlow.SmokeTests
   dotnet run --project OutreachFlow
   ```

**Option B — full clean source:** Extract `OutreachFlow-research-full.zip` into a **fresh** directory. Open `OutreachFlow.slnx` in the extracted folder. This full ZIP deliberately does not include an existing database.

This increment **does not change the EF Core model or schema**. Existing `outreachflow.db` files are not intentionally modified or reset by packaging, but data should still be backed up before replacing application files.

## Exercise actual website research

1. Go to **Prospects**, import one legitimate public company website using `Company | Branch | https://company.example-real-domain.com | contact@example-real-domain.com` (substitute actual published public website and valid business recipient). **No email will be sent.**
2. Open that prospect. Select **Research company website**.
3. The site scanner attempts to read `robots.txt`, then visits at most **six** public HTML pages **on the supplied hostname or its www variant**, looking for procurement/vendor links and relevant page text.
4. Inspect stored references. The scanner marks *all* its discoveries **unverified**. Click **View source** to check original pages. **Copy into review form** only after checking the source.
5. Select the correct research outcome. If you've verified a published supplier portal or email instructions, check **Confirmed official company source** and save the reviewed URL. An official application route creates an **ApplicationTask** and blocks a generic inquiry draft.
6. If you cannot establish a process, you may record **NoProcessFound** only after separately checking the relevant sources and writing down what was checked. This label means **not found in reviewed sources**, not no vendor process exists.
7. When inquiry-eligible, generate a draft. **Simulate** email sending and replies only. Real mailbox access is not in the project.

## Boundaries and limitations

- **Live external web requests begin with an explicit user click.** The app does not background-crawl, submit forms, register vendors, or contact prospects.
- The scanner uses a replaceable `ICompanyResearcher` interface and `OfficialSiteResearcher` adapter. No AI API keys, paid search subscription, or search-engine integration are required.
- Does not run JavaScript. Does not follow external supplier portals or parse PDFs. Does not search Google/Bing, so absence of findings is not meaningful negative evidence.
- Restricts URL schemes, ports, and hostnames; checks resolved IP addresses at connection time, disallows private/metadata endpoints and cross-domain redirects, caps responses, and checks common `robots.txt` directives. These are prototype safeguards, not a complete production security assessment. It cannot parse every robots.txt extension, HTML format, or site's terms of service; review website policies and do not crawl forbidden sites.
- Sites with Cloudflare/bot restrictions, dynamic JavaScript, long redirect chains or restrictive robots may return an error. Record the process manually instead of attempting to bypass restrictions.
- The app remains **localhost-only and unauthenticated**. Do not deploy it to AWS or expose it publicly until authentication, authorization, migrations, backups, SSRF testing, and other security work is completed.

## Smoke tests

The offline smoke test uses an injected fake research provider. It tests that candidate evidence is unverified, saved, not duplicated when rescanned, and **does not authorize a draft** without an explicit owner decision. Existing outreach and suppression checks remain.

**Verification note:** This source release has been statically inspected for file layout and syntax patterns, but the build and live network tests could not be executed here because no .NET SDK or network is available in the artifact environment. Verify locally before using it with real businesses.
