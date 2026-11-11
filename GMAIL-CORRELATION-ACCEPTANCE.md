# Gmail correlation and payload acceptance — issue #13

This checklist separates **repeatable offline gates** from the one remaining real-Gmail observation. It is not an invitation to send production outreach or weaken the verified-thread policy.

## Automated gates on every PR

Run `dotnet run --project OutreachFlow.SmokeTests -c Release` after a Release build, or rely on GitHub Actions. ProviderMessageTests, GmailCorrelationTests and GmailPayloadTests exercise real EF Core migrations, provider identity and duplication constraints, read-only fake Gmail review and import, strict thread/reference association, malformed headers and MIME, invalid UTF-8, unknown-length/declared oversized JSON, excessive JSON depth, unexpected send acknowledgement and preservation of prospect state after failed imports.

`OutreachFlow.BrowserTests` separately exercises owner registration, prospects, suppression, workspace boundaries and protected UI routes against disposable SQLite databases. It **does not connect to real Google**, and must not be presented as evidence of OAuth/provider acceptance.

The fake provider is deterministic: all test email addresses use `example.org`, no credentials or real Google account are used, and no message is transmitted. All initial sends remain owner-approved and disabled by default.

## Final one-time read-only Windows observation

This step is **not yet accepted**. It needs the owner's connected Windows Gmail mailbox and a real, controlled reply in a previously confirmed sent conversation. Do not create a second prospect or send another initial inquiry just to make this test pass.

1. Pull the candidate branch after passing CI, stop the application, and preserve a verified backup before testing against existing local data. No schema migration is included here.
2. Keep `Gmail:EnableSending=false`; start the app with the previously authorized Gmail account. Do not alter existing provider records.
3. From a separate mailbox you control, reply naturally within an existing Gmail thread originated by a confirmed OutreachFlow Gmail send. The reply should arrive in the authorized inbox with normal provider-generated references. No new message is sent by OutreachFlow.
4. In OutreachFlow **Mailbox → Review matching inbox replies**, verify that the controlled reply appears associated with the expected prospect. Confirm sender, recipient and content in Gmail itself. If it is missing, record **inconclusive/fail**, not a silent success. Some providers rewrite headers or omit the original RFC reference.
5. Review and import once with the appropriate classification; verify no second import is offered on a fresh review/restart. If the prospect is already Suppressed or Closed, do not reopen it or bypass suppression.
6. Unrelated/forwarded/ambiguous mail must not be auto-associated. No background process should send follow-ups.

Record date, test inbox (redacted), expected prospect ID, observed behavior and CI run link in issue #13. Do not commit real message content, OAuth tokens, screenshots of private mail or real customer data.

## Remaining issue gates

- Real Google read-only acceptance above.
- Any further genuine-provider malformed/alias/forwarded cases discovered by observation.
- No automatic reply classification or follow-ups until issue #13 is accepted and issue #15 introduces separately verified scheduling and suppression gates.

Issue #13 is not complete merely because deterministic tests pass. Keep the issue open until the real observation is recorded.
