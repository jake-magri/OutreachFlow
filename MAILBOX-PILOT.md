> Optional sending is a separate, disabled-by-default pilot. See [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md) for scope upgrade, outbox recovery and acceptance. This guide describes the read-only connection.

# Historical PR #11 — Google sign-in and read-only Gmail pilot

> **Historical implementation/acceptance notes.** PR #11 is merged. Google sign-in and Gmail connection were reported working in the November 11 local pilot; **subsequent merged PR #27 records a genuine Gmail send and an end-to-end imported reply**. This historical read-only guide is superseded for current acceptance by [ROADMAP.md](ROADMAP.md) and [GMAIL-SENDING-PILOT.md](GMAIL-SENDING-PILOT.md). Current status and exit gates: [ROADMAP.md](ROADMAP.md) and [AUDIT-2026-11-11.md](AUDIT-2026-11-11.md).

## Purpose and security boundary

This initial PR connects the **workspace owner's authorized Google Workspace/Gmail account** to the localhost-only OutreachFlow prototype. It can review recent inbox messages from contacts already associated with **real, owner-recorded outbound messages**, and the owner can explicitly classify and import selected replies.

**No email transmission, auto-replies, scheduled polling, Gmail label writes, mass inbox ingestion, or live follow-up workers.** `DryRunEmail` remains the only `IOutboundEmail` implementation. This is not a production-ready Google integration.

The current inbound matching strategy compares the From address with a prospect's recorded business contact and checks that an actual outbound email was already logged. A From address alone cannot prove sender identity or thread membership. Review questionable messages in Gmail before import. In a later live-send milestone, store Gmail provider message/thread IDs to establish stronger correlation.

## Before connecting (Windows localhost only)

1. Back up your existing local OutreachFlow SQLite databases. Verify the application works before adding mailbox settings.
2. In **Google Cloud Console**, create/select a project, enable **Gmail API**, configure an OAuth consent screen, and create an **OAuth 2.0 Web application client**.
3. For your private pilot, set the app to **Testing** and add your authorized Google account to the test-user list as applicable. Workspace organization administrators may need to approve the OAuth app.
4. Add BOTH exact authorized redirect URIs to the same Google OAuth web client:
   - `https://localhost:7117/signin-google` — ASP.NET Google sign-in middleware (basic identity only).
   - `https://localhost:7117/mailbox/google/callback` — separately consented Gmail read-only connection.
   Both must match the running app's HTTPS localhost URL. No Gmail permission is requested at sign-in.
5. In PowerShell, from the repository root, run:
   ```powershell
   dotnet user-secrets set "Gmail:ClientId" "YOUR_GOOGLE_OAUTH_CLIENT_ID" --project OutreachFlow
   dotnet user-secrets set "Gmail:ClientSecret" "YOUR_GOOGLE_OAUTH_CLIENT_SECRET" --project OutreachFlow
   dotnet user-secrets set "Gmail:RedirectUri" "https://localhost:7117/mailbox/google/callback" --project OutreachFlow
   dotnet run --project OutreachFlow
   ```
6. **Choose an onboarding path:** for an existing local OutreachFlow account, first sign in with its password and visit **Switch business / Workspaces → Link Google sign-in** so its data remains attached. For a new user, click **Continue with Google** on the login page to create a separate account and a brand-new private workspace. Google must provide a verified email; an email already used by a local account cannot be silently merged.
7. Sign out, then use **Continue with Google** for subsequent logins. Existing local passwords continue working; a new Google-only account has no local password set.
8. Open **Settings**, set the workspace **Sender email** to the Gmail/Workspace address you intend to authorize, and save. Google sign-in itself does NOT grant Gmail access.
9. Open **Mailbox** in the sidebar, click **Connect Gmail (read only)**, choose the corresponding Google account, and explicitly approve the additional Gmail permission. OAuth uses a short-lived signed state cookie, PKCE S256 and a server-side client secret; authorization tokens are never printed to the UI.
10. Return to Mailbox. Connection is active only for the currently selected workspace. Click **Review matching inbox replies**; the app searches no more than 20 inbox messages from the previous 30 days and only shows replies matching a prospect with owner-recorded real outbound history.
11. Review a matching message, choose its classification and click **Record reviewed reply**. It is imported as a real inbound message with source provider ID/audit marker; `Interested` creates a warm lead, `ApplicationInstructions` creates an application task, `Declined` suppresses further contact, and uncertain categories go to manual action. Simulated follow-ups are paused.
12. Click **Disconnect local authorization** to remove the locally stored refresh token. To revoke Google's grant, additionally remove OutreachFlow's authorization from your Google Account; this button alone does not call Google's revocation endpoint.


## Google sign-in vs Gmail authorization

- **Continue with Google** requests only basic account identity scopes (`openid`, `email`, `profile`). It signs in an existing linked user or registers a first-time, Google-verified identity with a fresh private business workspace and Owner membership.
- **Connect Gmail (read only)** is a different, optional OAuth request. Google consent is required separately because Gmail access is substantially more sensitive than basic authentication. Merely signing into OutreachFlow never grants inbox access.
- The SAME Google Cloud OAuth 2.0 Web client ID/secret may be used for both flows; the two distinct HTTPS callbacks must both be registered in Google Cloud Console. The existing `Gmail:ClientId` and `Gmail:ClientSecret` settings are reused for sign-in unless `GoogleAuth:ClientId` and `GoogleAuth:ClientSecret` are explicitly configured separately.
- New Google subjects may create their own isolated account and workspace (no automatic access to existing business data). Existing local users must explicitly link Google while authenticated; an email match is never sufficient for linking. Google identities without a verified email are rejected.
- Local password sign-in remains enabled for existing users; a newly registered Google-only identity has no local password. Google sign-in does not change the identity/workspace database schema or migrate existing users.
- Production-ready self-service signup, account recovery, abuse controls, invitation-based team membership, and Microsoft 365 support require additional security/product review. This registration prototype is still restricted to localhost Development mode.

## Data handling

- Local file `mailbox-connections/workspace-<id>.protected` stores an ASP.NET DataProtection-protected refresh token. The `mailbox-keys/` key ring is protected with **current Windows user DPAPI**. Both folders are gitignored and must not be committed or shared.
- Access tokens are short lived and held only during requests; this pilot does not persist them.
- Only the workspace owner can access connection and inbox review operations. The authenticated session and active workspace are checked again on each action.
- The initial Gmail permission is **`gmail.readonly`**. The optional sending pilot requires a separate explicit upgrade to add `gmail.send`; `gmail.modify` is never requested.
- The preview is stored only in the current Blazor session. The complete inbox is not copied into SQLite; only owner-approved, matching inbound text is recorded.
- New imported Gmail message/thread identities are stored with workspace and mailbox ownership and unique constraints. Historical audit markers remain a conservative duplicate fallback.
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
