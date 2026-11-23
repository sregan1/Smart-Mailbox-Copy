# Changelog

All notable changes to Smart Mailbox Copy are documented here.

---

## 1.0.0 — 2026-11-23

First release. Please [report issues](https://github.com/sregan1/Smart-Mailbox-Copy/issues): attach the errors log from `%LOCALAPPDATA%\SmartMailboxCopy\logs` (it contains no mail content), and leave out any addresses or secrets.

### Added

- **Windows wizard** — nine steps with an explanation on every screen: Welcome, Source tenant, Target tenant, Choose mailboxes, Match mailboxes, Options, Review, Copy and Results.
- **One mailbox, a chosen few, or a whole tenant** — the tool lists the source tenant's mailboxes, with filters for users, shared/room/equipment (unlicensed), guests, enabled accounts, domain and text, and matches each to a target mailbox (domain swap, editable, CSV import and export).
- **Application (app-only) access** — no one signs in as each mailbox owner. Saved app registrations keep their client secrets encrypted for the Windows user. Connecting checks which application permissions the app was actually granted (✓ or ✗ for each), and `list-mailboxes --app-only` prints them.
- **Whole folder tree, two ways** — nested folders copied either into one new folder in each target mailbox, or merged into the real Inbox, Sent Items, Drafts and other folders. The source is only read; the target is only added to; nothing is ever deleted.
- **Duplicate-safe re-runs and verification** — mail whose internet message id is already in the target folder is skipped, a resume journal remembers finished items, and every copied message can be compared with its source field by field.
- **Optional limits** — leave out folders, copy only mail created on or after a date, or skip messages over a size.
- **Calendar events and contacts** — in merge mode, or on their own with "calendar events and contacts only": the default Calendar and Contacts, plus additional calendars and contact folders (recreated in the target by name; an existing one of the same name is reused). The additional ones need the `Calendars.ReadWrite` and `Contacts.ReadWrite` application permissions on the target. Specific additional calendars or contact folders can be left out by name (for example a subscribed holiday calendar), and the Birthdays calendar, which Outlook builds from contacts, is never copied. Tasks, notes and journal folders are not copied (Graph cannot create them).
- **Size and time estimate, progress and results** — an estimate before the copy, an overall progress bar with time remaining, and a Results screen with totals, a mailbox-by-mailbox table and next steps. Exports: a results CSV, a per-item list (copied, skipped, failed, by item id) and the full log. Notes can be selected and copied.
- **User guide and demo mode** — a user guide (`USER-GUIDE.md`) with screenshots, and a built-in demo mode (`--demo`) that runs the wizard on fictional data with no network calls, used to render the screenshots (`tools/TakeScreenshots.ps1`).
- **Command line** — `copy`, `copy-tree`, `list-mailboxes` and `copy-batch`.
- **Safety guards** — every request is checked before it is sent, a never-write-to list is enforced, and logs and reports never contain mail content, addresses or secrets.

### Known limitations

- **Not supported** — in-place archive, group and public-folder mailboxes (not in Microsoft's general release of the API yet), and Exchange settings such as mail rules and mailbox permissions. The user guide has a checklist for moving those separately.
