# Feasibility spike: findings

Run in November 2026 against two Microsoft 365 tenants. Test data was synthetic (109 messages, 9 designed cases plus 100 small ones) in one mailbox, then 5 real messages across tenants.

## Summary

Microsoft Graph's **mailbox import/export API** copies mail with no field differences, including across tenants. The simpler MIME route does not work for received mail.

## Round 1: MIME export and re-import (same mailbox)

- `GET /messages/{id}/$value`, then `POST /mailFolders/{id}/messages` with the MIME, preserves attachments (including 8 MB), inline images, and recipients.
- **Imported items always land as drafts.** `PidTagMessageFlags` (0x0E07) gets the "unsent" bit (0x8). Patching the property returns HTTP 200 and changes nothing.
- The received date is stamped with the import time. It can be restored with a PATCH, but the draft state cannot.
- A JSON create with the date and flag properties set at creation does produce non-drafts, but loses raw headers and the original message id.
- **Conclusion:** dead end for received mail.

## Round 1b: import/export API (same mailbox, delegated)

- Flow: `GET /users/{id}/settings/exchange` for the mailbox id, list folders and items under `/admin/exchange/mailboxes/{id}/`, `exportItems` (20 items per call), `createImportSession` on the target mailbox, then POST each exported item to the returned `importUrl` (no Authorization header).
- 109 of 109 items identical on every compared field, including a genuine draft staying a draft, a five-year-old date, an 8 MB attachment, cc and bcc, an attached message, and the internet message id.
- Export about 37 items per second, import about 7 items per second (sequential).

## Round 2: across tenants

- Source mailbox in tenant A (read-only, 5 real messages with attachments), target mailbox in tenant B, each with its own sign-in and its own app registration.
- 5 of 5 imported, 0 failures; all paired by internet message id; identical on every compared field.
- The one informational difference: `PidTagMessageFlags` loses the UNMODIFIED bit (0x2) on import, on all items. Draft, read, and attachment bits matched. No visible effect.

## Throughput (MIME route, one mailbox)

- 4 parallel workers: 14 messages per second, no throttling.
- 8 parallel workers: 5 messages per second, with 429 responses. Slower than 4.
- Microsoft's documented Outlook limits (a model-extracted summary, not verified against the page): 4 concurrent requests, 10,000 requests per 10 minutes, 150 MB of uploads per 5 minutes per app and mailbox. If the upload limit applies to imports, it caps import throughput near 1.8 GB per hour per mailbox.

## Billing

- Not on Microsoft's metered-API list at the time of testing.
- The metered-API overview defines a "high-capacity" category (bulk export or import endpoints) that "might be metered". It does not name this API.
- Re-check before any public release.

## Open questions

- App-only auth with application permissions, admin consent, and whether Exchange RBAC for Applications can restrict these permissions to specific mailboxes. The docs do not say.
- Non-admin delegated consent (`User.Read.All` normally needs admin approval).
- Other people's mailboxes and shared mailboxes with delegated sign-in.
- Nested folders, contacts, calendar, scale (thousands of items), and parallel import.
- Archive mailboxes, group mailboxes, and public folders are not part of the general release.
