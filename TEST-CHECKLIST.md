# Manual acceptance checklist

Start with a **backup** of any existing database. Use fictional `.example` addresses for email simulation; use a real public company website ONLY for the read-only scan test, never for sending.

- [ ] F5 opens Dashboard; browser works on localhost; remote access remains blocked.
- [ ] Import a real public company website; click **Research company website**; inspect source-linked findings or a clear access/robots error.
- [ ] Scan findings remain unverified and **cannot** directly generate an inquiry draft.
- [ ] Click **Copy into review form**; verify the official source and approve an outcome explicitly.
- [ ] Scan the same site twice; identical evidence isn't duplicated.
- [ ] Try a `localhost` or private IP website; research stops without fetching it.
- [ ] Importing the same company/site twice skips the duplicate.
- [ ] Recording a confirmed official application portal makes an application task and does not generate a generic inquiry draft.
- [ ] Recording "no process found" requires notes naming checked sources; generate an inquiry draft afterward.
- [ ] Simulated initial email creates one outbound history item and a next-step UTC preview (zero emails transmitted).
- [ ] First/second follow-up simulations enforce the configured maximum; schedule preview skips weekends.
- [ ] Editing Settings after initial simulation does not alter that prospect's captured follow-up limit.
- [ ] After final simulated follow-up, no-response handoff creates the **manual** queue entry, not a warm lead.
- [ ] Late interested reply promotes no-response into warm; automation remains paused.
- [ ] Opt-out creates Suppressed status and is never assigned to manual outreach; reimport with same email is skipped.
- [ ] Log manual call/note/application, next action and date; reload page and verify persistence; simulated follow-ups remain blocked.
- [ ] CSV export includes the statuses and excludes email bodies; a company starting `=` appears escaped in spreadsheet software.
- [ ] `dotnet run --project OutreachFlow.SmokeTests/OutreachFlow.SmokeTests.csproj` reports all PASS.

Blockers to live deployment: owner authentication, EF migrations, production authorization, compliant email provider and suppression, inbound sync, secure webhook verification, backups and confirmed legal/provider rules.
