# PR #10 — Google Workspace mailbox pilot (read-only first slice)

## Purpose and security boundary

This initial PR connects the **workspace owner's authorized Google Workspace/Gmail account** to the localhost-only OutreachFlow prototype. It can review recent inbox messages from contacts already associated with **real, owner-recorded outbound messages**, and the owner can explicitly classify and import selected replies.

**No email transmission, auto-replies, scheduled polling, Gmail label writes, mass inbox ingestion, or live follow-up workers.** `DryRunEmail` remains the only `IOutboundEmail` implementation. This is not a production-ready Google integration.

The current inbound matching strategy compares the From address with a prospect's recorded business contact and checks that an actual outbound email was already logged. A From address alone cannot prove sender identity or thread membership. Review questionable messages in Gmail before import. In a later live-send milestone, store Gmail provider message/thread IDs to establish stronger correlation.

## Before connecting (Windows localhost only)

1. Back up your existing local OutreachFlow SQLite databases. Verify the application works before adding mailbox settings.
2. In **Google Cloud Console**, create/select a project, enable **Gmail API**, configure an OAuth consent screen, and create an **OAuth 2.0 Web application client**.
3. For your private pilot, set the app to **Testing** and add your authorized Google account to the test-user list as applicable. Workspace organization administrators may need to approve the OAuth app.
4. Add the exact authorized redirect URI `https://localhost:7117/mailbox/google/callback` to the Google OAuth client. It must exactly match your locally running app URL and the secret value below.
5. In PowerShell, from the repository root, run:
   ```powershell
   dotnet user-secrets set "Gmail:ClientId" "YOUR_GOOGLE_OAUTH_CLIENT_ID" --project OutreachFlow
   dotnet user-secrets set "Gmail:ClientSecret" "YOUR_GOOGLE_OAUTH_CLIENT_SECRET" --project OutreachFlow
   dotnet user-secrets set "Gmail:RedirectUri" "https://localhost:7117/mailbox/google/callback" --project OutreachFlow
   dotnet run --project OutreachFlow
   ```
6. Sign in as the owner, open **Settings**, and make the workspace **Sender email** exactly match the Google account you intend to authorize. Save.
7. Open **Mailbox** in the sidebar, click **Connect Gmail (read only)**, choose the correct account, and explicitly approve the OAuth request. OAuth uses a short-lived signed state cookie, PKCE S256 and a server-side client secret; authorization tokens are never printed to the UI.
8. Return to Mailbox. Connection is active only for the currently selected workspace. Click **Review matching inbox replies**; the app searches no more than 20 inbox messages from the previous 30 days and only shows replies matching a prospect with owner-recorded real outbound history.
9. Review a matching message, choose its classification and click **Record reviewed reply**. It is imported as a real inbound message with source provider ID/audit marker; `Interested` creates a warm lead, `ApplicationInstructions` creates an application task, `Declined` suppresses further contact, and uncertain categories go to manual action. Simulated follow-ups are paused.
10. Click **Disconnect local authorization** to remove the locally stored refresh token. To revoke Google's grant, additionally remove OutreachFlow's authorization from your Google Account; this button alone does not call Google's revocation endpoint.

## Data handling

- Local file `mailbox-connections/workspace-<id>.protected` stores an ASP.NET DataProtection-protected refresh token. The `mailbox-keys/` key ring is protected with **current Windows user DPAPI**. Both folders are gitignored and must not be committed or shared.
- Access tokens are short lived and held only during requests; this pilot does not persist them.
- Only the workspace owner can access connection and inbox review operations. The authenticated session and active workspace are checked again on each action.
- The Gmail permission is **`gmail.readonly`**, not `gmail.send` or `gmail.modify`. No additional provider call occurs until the owner explicitly clicks connect or review.
- The preview is stored only in the current Blazor session. The complete inbox is not copied into SQLite; only owner-approved, matching inbound text is recorded.
- A Gmail message ID is stored as an audit marker after import to avoid duplicate imports for the same prospect in this local prototype.
- The local prototype remains restricted to localhost and ASP.NET Core Development environment; Linux CI cannot enable live Gmail token storage because Windows DPAPI is required.
- If the sender address in Settings differs from the connected Gmail identity, the app refuses to connect or read.

## Compliance / product decision

Google classifies `gmail.readonly` as a **restricted scope**. A public SaaS rollout may need OAuth verification, additional policy review and potentially third-party security assessment. This pilot is for limited testing and should **not** be treated as approval to commercialize Gmail integration. Google OAuth testing mode may impose test-user and refresh-token restrictions.

This is one reason to keep the eventual mailbox gateway provider-neutral and avoid assuming Gmail is universally suitable for all future OutreachFlow customers.

Official references:
- https://developers.google.com/identity/protocols/oauth2/web-server
- https://developers.google.com/workspace/gmail/api/auth/scopes
- https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification

## Review and acceptance

- [ ] GitHub CI restores and builds the .NET 10 solution; all offline smoke tests pass.
- [ ] With no OAuth user-secrets configured, Mailbox page clearly reports disabled integration; app still starts.
- [ ] A non-owner workspace member cannot connect, disconnect, review or import another workspace's mail.
- [ ] Connect rejects invalid state, changed workspace, expired state and mismatched sender identity.
- [ ] New Gmail connection uses the correct owner email, stores no plaintext token and persists across app restarts on that Windows user profile.
- [ ] Inbox preview is bounded and excludes unrelated messages; malformed/mismatched senders do not become prospects.
- [ ] Manual reply classification records real inbound history exactly once, pauses simulations, routes interested/application/declined appropriately, and never transmits an email.
- [ ] Disconnect removes local authorization without erasing historical manually reviewed messages.
- [ ] `DryRunEmail` remains registered; unattended sending is explicitly deferred.

## Subsequent PR(s) after this acceptance gate

Provider-neutral message IDs/thread association, durable authorization-aware polling, proper queue/idempotency ledger, explicit owner approval of initial campaign, delivery limits, bounce/complaint/opt-out handling, CSRF-safe controls, refresh/reconnection UX, and only then separately gated *live outbound sending*. Public hosting requires migrations, backup/restore testing, security review and provider compliance.
