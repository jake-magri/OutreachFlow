# Gmail correlation and payload acceptance — issue #13

This checklist separates **repeatable offline gates** from optional troubleshooting with a real Gmail account. The owner has already completed controlled real Gmail send/reply acceptance in earlier work; no additional manual click-through is necessary to merge this regression-test change. It is not an invitation to send production outreach or weaken the verified-thread policy.

## Automated gates on every PR

Run `dotnet run --project OutreachFlow.SmokeTests -c Release` after a Release build, or rely on GitHub Actions. ProviderMessageTests, GmailCorrelationTests and GmailPayloadTests exercise real EF Core migrations, provider identity and duplication constraints, read-only fake Gmail review and import, strict thread/reference association, malformed headers and MIME, invalid UTF-8, unknown-length/declared oversized JSON, excessive JSON depth, unexpected send acknowledgement and preservation of prospect state after failed imports.

`OutreachFlow.BrowserTests` separately exercises owner registration, prospects, suppression, workspace boundaries and protected UI routes against disposable SQLite databases. It **does not connect to real Google**, and must not be presented as evidence of OAuth/provider acceptance.

The fake provider is deterministic: all test email addresses use `example.org`, no credentials or real Google account are used, and no message is transmitted. All initial sends remain owner-approved and disabled by default.

## Optional real-Gmail troubleshooting (only if a new problem arises)

The procedure below is available if strict threading behavior differs with a real provider. It is **not a merge gate for PR #31** and should not be repeated without an actual regression. It needs the owner's connected Windows Gmail mailbox and a controlled reply in an existing conversation. Never create a second prospect or send another initial inquiry merely to run the test.

1. Pull the candidate branch after passing CI, stop the application, and preserve a verified backup before testing against existing local data. No schema migration is included here.
2. Keep `Gmail:EnableSending=false`; start the app with the previously authorized Gmail account. Do not alter existing provider records.
3. From a separate mailbox you control, reply naturally within an existing Gmail thread originated by a confirmed OutreachFlow Gmail send. The reply should arrive in the authorized inbox with normal provider-generated references. No new message is sent by OutreachFlow.
4. In OutreachFlow **Mailbox → Review matching inbox replies**, verify that the controlled reply appears associated with the expected prospect. Confirm sender, recipient and content in Gmail itself. If it is missing, record **inconclusive/fail**, not a silent success. Some providers rewrite headers or omit the original RFC reference.
5. Review and import once with the appropriate classification; verify no second import is offered on a fresh review/restart. If the prospect is already Suppressed or Closed, do not reopen it or bypass suppression.
6. Unrelated/forwarded/ambiguous mail must not be auto-associated. No background process should send follow-ups.

Record date, test inbox (redacted), expected prospect ID, observed behavior and CI run link in issue #13. Do not commit real message content, OAuth tokens, screenshots of private mail or real customer data.

## Remaining product gates

- Track any concrete provider-specific threading/alias/forwarding regressions if observed.
- No automatic reply classification or unattended follow-ups until separately verified scheduling and suppression gates in issue #15 are satisfied.

The existing controlled Gmail send/reply acceptance is recorded in the project history. Future external-provider verification should be evidence-driven, not repetitive.
