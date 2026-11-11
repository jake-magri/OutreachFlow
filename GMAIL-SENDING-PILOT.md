# Controlled Gmail sending pilot

This Windows localhost pilot sends **one explicitly approved initial inquiry**. It stays disabled by default. Google sign-in and an existing read-only Gmail connection do not enable sending. Simulation continues to use `DryRunEmail` through its separate API.

## Upgrade and enable locally

Stop the app. From the repository root in PowerShell, preserve your existing databases and run:

```powershell
dotnet run --project .\OutreachFlow.SmokeTests
dotnet run --project .\OutreachFlow.RecoveryCli -- upgrade .\OutreachFlow .\OutreachFlow "$HOME\OutreachFlowBackups\approved-gmail-send-001"
dotnet user-secrets set "Gmail:EnableSending" "true" --project .\OutreachFlow
dotnet user-secrets set "Gmail:HourlySendLimit" "2" --project .\OutreachFlow
dotnet run --project .\OutreachFlow
```

Use a new backup directory for each upgrade attempt. The new `ApprovedGmailDispatch` migration adds an outbox table; existing reply/provider identities and suppression remain intact. Keep the verified recovery bundle.

In Google Cloud's consent configuration, make `https://www.googleapis.com/auth/gmail.send` available alongside the existing `gmail.readonly` scope. Keep the existing local callback. In OutreachFlow **Mailbox**, click **Grant Gmail sending permission**, choose the workspace sender account and grant both requested permissions. The callback verifies granted scopes and the Gmail profile address before replacing the stored connection. Declining the upgrade retains the old connection. A normal read-only reconnect deliberately disables the app's stored send approval.

## One-message acceptance test

1. Verify existing prospects, suppression and Gmail connection after upgrade. Use a fresh prospect addressed to **another inbox you control**, with no prior outreach or suppression in this workspace. Avoid sending to the connected mailbox itself, which can confuse sender-based reply review.
2. Save an honest research finding. For a fixture, explicitly note that this is a controlled mailbox test and that no company research was performed; do not mark a fabricated source official. Generate an inquiry draft.
3. Check Settings for the correct sender, local time zone, weekday sending window and a small daily limit. The daily and hourly limits are rolling 24-hour/one-hour windows for this sender mailbox, including uncertain and rejected attempts across workspaces using it. Default hourly limit is 10, configurable from 1–100.
4. Click **Review Gmail send**. Check the exact From, To, subject, body and the saved research decision. Tick the approval checkbox and click **Send approved inquiry via Gmail** once.
5. Verify the Gmail delivery record shows **Sent**, the prospect shows one attempt and AwaitingReply, and history has one real Gmail-accepted outbound entry. In the recipient inbox verify exactly one message with the intended content. Gmail acceptance does not guarantee inbox delivery.
6. Reload the prospect. There must be no second send action for that inquiry. Reply from the controlled recipient inbox, use Mailbox review, classify and record it, and verify import happens once. The matching algorithm remains sender-based pending issue #13; review each candidate manually.
7. Restart the app and verify the send/reply history remains. Run the inbox review again and confirm the already-imported reply is absent.

Stop after each step if anything differs and capture the current view. Live acceptance is pending until the owner completes these steps. No real mail is sent by the offline tests or by enabling the toggle alone.

## Delivery states and recovery

| State | Meaning | Action |
| --- | --- | --- |
| Sending | Durable approval committed; the request may still be running, or the app stopped before recording the outcome | Wait for the request; after interruption use Check Gmail Sent mail |
| Sent | Gmail supplied a message/thread ID, or an exact Sent match was reconciled | Review replies; no automatic follow-ups |
| Rejected | Gmail returned a definite 4xx rejection, except ambiguous HTTP 408 | Inspect the displayed HTTP status, permissions and quota; this first pilot does not retry the inquiry |
| Uncertain | Timeout, 5xx, malformed success or persistence failure prevented confirmation | Check Gmail Sent mail; never assume missing search results prove no send occurred |

The saved dispatch identity is unique per prospect. Approval snapshots retain sender, recipient, exact content, owner, research/evidence and policy. A SQLite transaction revalidates eligibility and reserves the dispatch before the one provider request. Manual/simulated initial sends cannot bypass this reservation. The request is never replayed after restart, cancellation or double-click.

**Check Gmail Sent mail** is read-only. It searches a stable RFC Message-ID and requires a single exact match with SENT label, sender, recipient, subject and body. Empty or mismatched results leave the send blocked. A confirmed match atomically adds Gmail message/thread identity and history, without reopening closed or suppressed prospects. Ordinary Gmail transformations may prevent an exact match and require manual investigation. Gmail does not provide an idempotency key for `messages.send`; the application deliberately sacrifices automatic retry to prevent silent duplicates.

There is no automatic retry worker, follow-up scheduler, bounce processing or unblocking button in this slice. Rejected or unresolved dispatches require operator investigation. Do not delete their rows or create another prospect to bypass the guard. A close or suppression received after the send has already been handed to Gmail cannot recall that message; completion preserves the newer state.

Disable future sends with:

```powershell
dotnet user-secrets set "Gmail:EnableSending" "false" --project .\OutreachFlow
```

Restart the app after changing configuration. Existing delivery records and read-only reconciliation remain available. Local disconnection removes authorization; revoke the Google grant separately if desired.

## Validation and references

Offline coverage includes owner/workspace checks, exact approvals, double-click races, immutable history, restart/retry rejection, timeout reconciliation, suppression, quotas, revoked authorization, malformed addresses, legacy read-only grants, MIME encoding and mismatched Sent results. CI also builds/runs on Windows and Linux and checks migration drift and startup. These fixtures do not establish real Google delivery.

- [Google sending guide](https://developers.google.com/workspace/gmail/api/guides/sending)
- [Google OAuth server flow and granted scopes](https://developers.google.com/identity/protocols/oauth2/web-server)
- [Gmail error handling](https://developers.google.com/workspace/gmail/api/guides/handle-errors)

Issue #14 stays open until controlled real-mail acceptance. Thread-backed reply correlation (#13), scheduling (#15), fuller UX (#21) and hosted security/recovery work remain separate gates.
