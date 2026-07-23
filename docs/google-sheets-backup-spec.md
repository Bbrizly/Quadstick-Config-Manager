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
6. permissions.create (role=reader, type=anyone), once per sheet, the
   first time the user copies a share link.
7. files.list (spreadsheets only, not trashed, paged) for bulk restore;
   drive.file scopes it to sheets this app created.

## Linking and state

Settings JSON gains a map: profile file path -> { spreadsheetId,
lastSeenModifiedTime, backupDirty, linkShared }. Written with the existing atomic write
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

## Sharing

Two actions, offered in the same pair everywhere:

- "Copy share link": puts the sheet's URL on the clipboard, ready to send.
  The friend pastes it into the app's existing import box, or just opens it
  in a browser.
- "Open in Google Sheets": plain browser open of the sheet URL, for looking
  at the sheet, revision history, or Google's own Share dialog.

Entry points:

- Editor: a Share button in the profile name row opens a small flyout with
  the two actions.
- Home: each profile card's menu gets the same two items.
- When backup is off, the actions still show; choosing one explains and
  routes to the backup toggle instead of failing silently.

"Copy share link" sequence:

1. Local save first, always. A never-saved profile has no path and the
   state map is keyed by path, so save (which names the file) before any
   Drive call. No path, no sheet.
2. Not yet linked: run the first backup (create + push). If that first
   push fails, copy nothing and say so; the sheet has never held the
   profile and sharing it would hand the friend a blank.
3. Already linked but backupDirty: push. If the push fails, still copy the
   link but say so: "Link copied. Your latest changes are not uploaded yet
   (backup pending)." A known-good earlier backup beats no link offline.
4. Sheet not yet link-shared (no linkShared flag): confirm once per sheet,
   "Anyone with this link can view this sheet (read only). Turn on link
   sharing and copy?" On yes: permissions.create with role=reader,
   type=anyone, allowFileDiscovery=false, set linkShared, then re-read
   modifiedTime and record it (the grant can bump it; skipping this
   re-read causes a self-inflicted conflict prompt on the next save). On
   network failure here, copy nothing; an unshared link is useless.
5. Copy the URL, toast "Link copied".

Choices made:

- role=reader on purpose. Friends view and import a copy; they never edit
  the owner's sheet. anyone-with-link reader is exactly what the existing
  unauthenticated import path and the community template workflow already
  consume, so a copied link works in the import box with zero sign-in.
  allowFileDiscovery=false keeps it link-only, never searchable.
- Revoking a link, sharing with specific people, or granting edit rights
  all happen in Google's own Share dialog via "Open in Google Sheets". We
  build no permissions UI beyond the one reader grant.
- After the one-time grant, "Copy share link" is a pure clipboard write:
  instant and offline-safe. Ceiling accepted and named: if the user
  revokes link access inside Google, linkShared is stale and the app
  copies a dead link; the fix is Google's own Share dialog, not app code.

## Restore (bulk import from Drive)

The point of the whole feature: machine dies, new machine, get everything
back. One picker dialog serves both bulk restore and cherry-picking, from
three entry points:

- Home: a "Google Drive" button next to the YOUR PROFILES section header
  (shown once backup is connected). Opens the picker; picked sheets land
  in Your profiles.
- Settings: an "Import from Google Drive" button next to the backup
  toggle.
- Onboarding: offered right after connecting, the new-machine moment.

Home stays a local view: no Drive call on home load, no third live
section. The list is fetched when the picker opens. Ceiling accepted: an
inline always-visible Drive pane can come later if users ask.

- files.list with q = spreadsheet mimeType and trashed=false, paged via
  nextPageToken. Under drive.file this returns exactly the sheets this app
  created: the user's backups, nothing else of theirs.
- The picker lists each sheet's name and last-modified date with
  checkboxes. From onboarding everything is pre-checked (restore); from
  home or settings nothing is (cherry-pick). Sheets already linked to a
  local profile (matched by spreadsheetId, so renames don't fool it) show
  greyed with "already in your profiles". The match counts only if the
  mapped local file still exists; a stale entry for a deleted local CSV
  is ignored and pruned, otherwise it would grey out the very sheet
  restore exists to bring back. Import runs the selected ones
  through the same authenticated CSV export fetch and the existing parse
  code, written into the library with the atomic write helper.
- Sheet names are not safe filenames. The existing SafeTemplateName rule
  covers invalid and path characters but not everything Drive allows, so
  restore extends it: blank becomes Untitled, reserved Windows device
  names (CON, NUL, ...) get a suffix, trailing dots and spaces are
  trimmed, length is capped. Duplicates after sanitizing get a numbered
  suffix. Collision checks happen on the sanitized name.
- Each imported profile is linked on the spot: record spreadsheetId and
  modifiedTime, backupDirty=false. Future saves push to the same sheet
  instead of forking a duplicate. If recording the link state fails, the
  just-written CSV is deleted and the file is reported as failed; a
  restored-but-unlinked file would silently fork a duplicate sheet on its
  next save, so restore keeps the invariant that imported means linked.
- Name collision with an existing local file: skip it and say so in the
  result summary ("3 imported, 1 skipped: mygame already exists"). The
  local file is the source of truth; restore never overwrites it. To take
  the online copy instead, rename or delete the local file and re-run
  restore, or paste the sheet's link into the import box.
- Per-file failures do not abort the batch; the summary lists them.
- Ceiling accepted and named: drive.file only lists sheets THIS app
  created. Sheets the user made by hand in Drive are invisible here and
  come in via the existing paste-link import. Listing all their Drive
  would need a restricted scope and Google's heavy security review.

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
