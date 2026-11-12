# Scheduled Gmail follow-up pilot (issue #15)

## Intent and activation

OutreachFlow's next milestone allows one owner to review and approve **up to two exact follow-up messages** after a Gmail-confirmed initial inquiry. Those messages are scheduled using workspace business-day delays and appear on the prospect page with their stored states. There is a final manual no-response handoff, never automatic warm-lead classification.

**OFF by default.** Development-only operator setup:

\`\`\`powershell
dotnet user-secrets set "Followups:Enabled" "true" --project OutreachFlow
dotnet user-secrets set "Followups:PollMinutes" "3" --project OutreachFlow
\`\`\`

Gmail must separately be connected, authorized for \`gmail.readonly\` and \`gmail.send\`, and \`Gmail:EnableSending\` must be explicitly enabled. The app must stay running on localhost. Turning on the worker does **not** approve any prospects or send any message by itself.

## User workflow

1. Send a reviewed, approved initial Gmail inquiry. The Gmail dispatch must reach \`Sent\` with provider message and thread ID; a simulated or owner-attested outside send does not qualify.
2. In the prospect page, open **Gmail follow-up schedule → Review proposed follow-up sequence**. Verify the intended sender and recipient, exact snapshotted bodies, template language, first due time, and the maximum of 1–2 steps.
3. Approve the snapshot once. The app stores separate scheduled jobs for each approved step and the final manual handoff. No message is sent by clicking Approve.
4. While the local app is running, the worker checks the authorized Gmail inbox before any scheduled dispatch. New, provider-correlated replies are imported **as Unclear** and handed to the owner (not guessed as Interested). All future sends for that prospect stop.
5. If no reply or blocking signal is found and the due step meets the weekday, sender, recipient, quotas and sequence requirements, Gmail is asked to send the stored message **in the original conversation thread**. It has a unique RFC Message-ID and reply references.
6. Following the last response window, the prospect becomes \`ManualNoResponse\` for personal outreach. No further automated email is sent.
7. Use Pause, Resume, Cancel on the prospect page. A sending/uncertain step cannot be manually resumed or blindly retried.

## Conservative failure rules

- A missing/revoked OAuth permission, inbox failure, ambiguous message, possible bounce/complaint, incomplete Gmail results, changed recipient/sender, or invalid status **holds or cancels** the schedule rather than allowing an unverified send.
- The automatic inbox scan is bounded to recent INBOX items; if the inbox exceeds its coverage limit, sending stops for owner review. **This is not a production-grade Gmail history sync or a guarantee of bounce detection.**
- A job is persisted as \`Sending\` **before** external submission. If a crash or timeout leaves acceptance uncertain, the next worker cycle marks it \`Uncertain\` and prohibits any automatic retry.
- No email is sent for an already closed, suppressed, manually taken-over or replied-to prospect. Human review takes precedence. No claimed delivery guarantee; Gmail accepted is different from inbox delivery.
- If the application was offline long enough that a job is over 24 hours overdue, the job is held for rescheduling rather than blasting overdue mail.
- Reply classification remains a manual decision; detecting a reply only moves the prospect to \`NeedsAction\`.

## Automated acceptance

- \`OutreachFlow.SmokeTests/FollowupAutomationTests.cs\`: fake clock + fake Gmail sender/inbox, expected cadence, immutable approval, duplicate approval prevention, pause/resume, restart persistence, stop on reply, inbox outage, uncertain delivery, and manual handoff.
- \`OutreachFlow.BrowserTests\`: confirms the schedule UI exists and does not offer a live schedule after a simulated send.
- Windows/Linux smoke and EF model-drift checks plus Playwright must pass before PR merge.
- CI never enables real Gmail credentials and does not transmit email.
- On an existing installation use the documented **backup-first RecoveryCli upgrade** before opening the updated app. Do not delete existing databases.

## Not yet a hosted campaign service

The runner executes only while the local application process is running. It is disabled by default, deliberately low-volume, and conservative in busy/ambiguous mailboxes. Gmail quota, OAuth, provider terms and consent requirements continue to apply. Before public deployment, add stronger provider-native history/event handling, bounce verification and operational monitoring.
