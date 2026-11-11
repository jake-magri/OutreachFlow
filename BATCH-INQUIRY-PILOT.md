# Import-to-inquiry batch pilot

This is a **bounded, owner-initiated batch**, not an unattended email campaign. On the Prospects page, paste one to three lines of the same import format:

`Company | Site/branch | https://official-company-website | recipient@company.example`

Choose **Simulation** to import new prospects, generate inquiry drafts and create one simulated outbound record per eligible new row. No Google credentials or external calls are needed. The regular Playwright browser suite runs this from one textarea and one click, then resubmits the same entry to verify no second prospect or outbound attempt.

Choose **Approved live Gmail** only for recipients whose official company process and appropriate contact you personally verified. A single checkbox approves **the exact submitted batch**, current workspace inquiry template, and recipients. This does not approve unrelated contacts, unknown future messages, or follow-ups. The service creates prospects and drafts and attempts exactly one initial Gmail dispatch per new eligible row via the existing approval/outbox pipeline. Existing Gmail sender consent and `Gmail:EnableSending` are still required. The workspace's weekday send window and daily/hourly limits still apply.

## Processing and failure rules

- At most three submitted rows; malformed rows reject the entire input before any creation.
- Repeated company/site or recipient addresses in the workspace are skipped. A suppressed or previously contacted recipient cannot be reintroduced under a different company name.
- A live Gmail batch requires owner workspace authorization; current OAuth sender and `gmail.send` consent are verified before any prospects are created.
- No automatic inference is made about official vendor applications. The owner explicitly attests that their reviewed recipients are appropriate for the inquiry route. This assertion is logged and **is not equivalent to independently verified research evidence**.
- If a message is rejected, uncertain, or ineligible, it is **held**. The existing dispatch record prevents blind retry. Other new rows may finish; the resulting list shows the actual outcomes.
- No follow-ups, recurring worker, or unattended inbox polling are activated.
- A re-submission is a no-op for existing recipients, not an opportunity to resend.
- Simulation fixtures must not be promoted into real delivery history.

## Test coverage

`OutreachFlow.BrowserTests` now exercises a single input string → simulated draft/send and repeats the string to check idempotent import. Existing `OutreachFlow.SmokeTests` exercises the real owner-reviewed Gmail send service: tenant checks, rejected/uncertain results, no repeat sends and quota/suppression controls. No real mail is sent by CI.

**Limitations:** Imported rows alone cannot prove that supplier portals do not exist, that the business email is consented to receive outreach, or that a recipient matches the account being targeted. Live use therefore requires explicit per-batch attestation. The remaining feature work is to automate research and qualification into a *review queue* rather than silently interpreting a partial crawl as permission to send.
