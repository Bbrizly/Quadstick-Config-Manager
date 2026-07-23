# Spec: Google Sheets backup and sharing (v1.5)

App: Quadstick Config Manager. C#/.NET 8, Avalonia, macOS + Windows, shipped
through the Mac App Store and Microsoft Store. Profiles are small CSV files
(a grid, a few hundred cells) edited locally and installed onto a QuadStick
device. The app can already IMPORT from any pasted Google Sheets share link
(no auth; it rewrites the link to the CSV export URL and downloads).

## Problem

Users fear losing all profiles if their machine dies. The QuadStick community
already lives in Google Sheets: the official vendor workflow is sheet
templates shared by link. What is missing is getting profiles INTO the user's
Drive and keeping them current there.

## Goals

1. A profile can be backed up to the user's own Google Drive as a real
   Google Sheet.
2. Every successful local save of a linked profile updates its sheet
   automatically, with retry across restarts.
3. Sharing works the way the community already shares: an "Open in Google
   Sheets" button takes the user to the sheet, they hit Share there, the
   friend pastes the link into the app's existing import box.
4. Version history and corruption recovery come free from Drive revision
   history. We build no versioning.

## Non-goals

- Two-way sync. Sheet edits never flow back automatically; at most the user
  is offered a one-time import on conflict.
- Listing or browsing the user's Drive (needs a restricted scope and heavy
  Google verification; drive.file avoids it).
- Real-time collaboration, merge logic, background sync timers.

## Backup format

One spreadsheet per profile. The app reads and writes ONLY the first
worksheet, which holds the profile's CSV grid verbatim, one cell per CSV
field, written with valueInputOption=RAW so pasted "=..." text is stored as
a string, never executed. This matches the community template format, so
the existing single-tab link import reads it unchanged. Extra tabs a user
adds online are never touched (clear and update target the first sheet's
range only); they are simply invisible to the app.

Round-trip contract: semantic, not byte-stable. Sheets trims trailing empty
rows/cells; the CSV parser already tolerates ragged rows. The test is:
grid -> push -> CSV export -> parse gives an equivalent ProfileDocument and
identical non-empty cells.

## Auth

- OAuth 2.0 installed-app flow with PKCE. System browser plus loopback
  redirect on 127.0.0.1 (Google blocks embedded webviews). Random port with
  a small retry-on-bind-failure loop, `state` parameter validated, 2-minute
  timeout with a Cancel in the UI, and a plain "you can close this tab"
  response page.
- Scope: `drive.file` only. Client ID ships in the app; for installed apps
  the client secret is not confidential by design.
- Refresh token at rest: macOS Keychain via Security.framework P/Invoke
  (SecItemAdd/CopyMatching, ~60 lines); Windows DPAPI (ProtectedData) to a
  file under AppData. No third-party dependency.
- invalid_grant / revoked token: backup pauses, settings shows Reconnect.
  Local saves are never blocked.
- Google Cloud console: enable Drive API + Sheets API, external consent
  screen in Production, basic brand verification (drive.file is
  non-sensitive; no security assessment). Start verification on day one; it
  gates nothing during development (test users work unverified).

## HTTP

Plain HttpClient against REST, no Google SDK. Endpoints:

1. Token exchange and refresh (oauth2.googleapis.com/token).
2. spreadsheets.create (first backup of a profile).
3. values.clear then values.update of the whole grid (clear first so a
   shrunken profile leaves no stale cells behind).
4. files.get?fields=modifiedTime (conflict check before push, and again
   after push to record our own write's timestamp).
5. For the "keep online version" path: GET the first worksheet's CSV
   export URL with an Authorization: Bearer header. The fetch is new (the
   existing import fetch is unauthenticated); everything after the bytes
   arrive (CSV parse, validation, open-in-editor) is the existing import
   code unchanged.

## Linking and state

Settings JSON gains a map: profile file path -> { spreadsheetId,
lastSeenModifiedTime, backupDirty }. Written with the existing atomic write
helper; a failed state write surfaces in the status line instead of being
swallowed.

Path is the key. Renames done inside the app update the mapping. A file
moved or renamed outside the app loses its link and simply creates a new
sheet on next save. Ceiling accepted: that can leave an orphan sheet in
Drive; the old sheet and any links to it keep working, nothing is lost.

## Flow

- Settings (and first-run onboarding) get a "Back up to Google Sheets"
  toggle. Turning it on runs the OAuth flow.
- Local save is the source of truth and finishes first: SaveAsync returns
  once the local atomic write succeeds, Dirty clears, the editor never
  waits on the network. Backup then runs in the background.
- Background push for a linked profile:
  - No sheet yet: spreadsheets.create named after the profile, record id,
    push.
  - Sheet exists: files.get modifiedTime. Unchanged since our last write:
    clear + update silently, then record the new modifiedTime. Changed:
    prompt once, "This profile's Google Sheet was edited online. Keep the
    online version or replace it with your copy?" Exact sequences:
    - Keep online: copy the current local file into the rescue folder
      (nothing is ever lost), then download the sheet, overwrite the local
      file atomically, reload the editor. Mapping unchanged; the sheet
      stays linked and the next save pushes normally.
    - Replace with mine: clear + update, record new modifiedTime. The
      online edits remain reachable via Drive revision history.
    False positives (metadata-only changes) cost one extra prompt;
    accepted as blunt-but-safe.
  - Success clears backupDirty; any failure sets it and shows "Backup
    pending" in the status line.
- Retry policy: no in-session timers, no launch-time background sync.
  backupDirty profiles retry on the next save, when that profile is opened,
  and on Reconnect. All Drive traffic happens for a profile the user is
  actively in. Ceiling accepted and named: save once offline, quit forever,
  and the backup never happens; the status line said so.
- 404 on push: never silently recreate (the id may be trashed, revoked, or
  a stale link others share). Prompt once: recreate as a new sheet, or turn
  backup off for this profile.
- 403/429/5xx: treated as generic failure -> backupDirty + status. No
  backoff machinery; the retry-on-next-event policy is the backoff.
- Each backed-up profile shows an "Open in Google Sheets" button (plain
  browser open of the sheet URL) so Share is one click away.

## Store packaging risks (verify in week one, before UI work)

- macOS sandbox: app already has the client network entitlement; the OAuth
  loopback LISTENER additionally needs com.apple.security.network.server.
  Prove the listener works in a sandboxed store build first.
- Microsoft Store: packaged Win32 (full trust) apps allow localhost
  listeners; confirm on the shipped MSIX before building UI.
- Both store privacy questionnaires gain "account info / user content
  uploaded to Google at the user's request".

## Testing

- Unit: grid -> ValueRange -> exported-CSV -> parse semantic round trip.
- Fake HttpMessageHandler: create, clear+update, conflict prompt both
  branches, 404 both branches, refresh renewal, invalid_grant pause,
  failure -> backupDirty -> retry-on-open.
- Headless UI tests: toggle, status line states, prompts.
- One manual pass against a real Google account, on both store builds.

## Estimate

About one week of work: OAuth + keychain interop (2 days), push/conflict
flow and state (2 days), UI + tests (1-2 days), plus Google console setup
and brand verification wall-clock wait. Ships as v1.5 after the current
1.4.2 store review clears.
