# Real lead pilot — owner-confirmed outreach (development)

This branch intentionally keeps the temporary local SQLite databases and avoids schema migrations while the business outreach flow is tested with 5–10 genuine prospects.

## Happy path
1. Import a real prospect via the existing company/site/website/email importer.
2. Research their official site; manually verify any application process.
3. If a portal exists, complete the supplier application externally and log the manual activity.
4. If no portal/process exists, record reviewed evidence, generate the inquiry draft and **copy it into your own mailbox**.
5. **Only after actually sending** the email, click **I sent this email myself**. It records an actual (non-simulated) outbound message and creates a manual follow-up date.
6. Check your own mailbox for replies. Paste the actual response into **Record actual incoming reply** and select its classification.
7. Interested replies become warm leads; application instructions become application tasks; opt-outs are suppressed.

**No emails are sent by OutreachFlow; it does not monitor or connect to your mailbox.** Recording a send is a human attestation, not provider verification. The sender remains responsible for sending accurately and complying with applicable requirements.

## Still out of scope
- Outbound message provider, inbound synchronization, automatic replies, live scheduler, persistent job queue
- Automatic follow-up sending; due dates are for manual next actions
- Public internet hosting and multi-customer production security
- DB migrations or other schema changes (intentionally postponed)

## Validation
Run `dotnet build` and `dotnet run --project OutreachFlow.SmokeTests`. GitHub Actions should run both. In the browser, create a disposable test prospect and inspect its history after confirming a manual send.

**Never click "I sent this email myself" before actually sending it.**
