# Remote clinician session, v1 spec (2026-11-04)

Drew Redepenning (UPMC) asked 2026-10-30: both sides click connect, clinician sees live input/output, pulls the client's profile, edits, sends it back. QuadStick only. Free. Nothing personal stored on a server.

## Transport (decided after Codex review)
Cloudflare Worker + Durable Object WebSocket relay, `wss://relay.bassamkamal.dev/r/<room>`. Free tier. Outbound WSS 443 passes hospital firewalls. No WebRTC libraries anywhere: browser `WebSocket`, .NET `ClientWebSocket`. Relay forwards ciphertext in memory, writes nothing to storage, logs nothing about content.
Rejected: WebRTC P2P (needs STUN + TURN anyway for hospital UDP blocks, SIPSorcery licence clause, most moving parts), copy-paste SDP (unusable for patients), iroh (no .NET), Supabase realtime (free projects pause). Upgrade path: same Worker can do WebRTC signaling later if ever needed.

## Pairing code
- Patient QCM: Share with my clinician > Start. QCM makes a code: 12 chars from Crockford base32 (no I L O U), shown `K7QM-4XPA-9RTD` (60 bits). Also "Copy link": `https://clinic.bassamkamal.dev/#join=K7QM4XPA9RTD` (fragment never reaches a server).
- Normalize: uppercase, strip `-` and spaces, map O->0, I/L->1.
- `room` = first 32 hex chars of SHA-256("qcm-room-v1:" + code). The relay never sees the code.
- `key` = PBKDF2-HMAC-SHA256(password = code, salt = "qcm-key-v1:" + room, 200000 iterations, 32 bytes). AES-256-GCM.
- Both sides: WebCrypto (browser), `Rfc2898DeriveBytes.Pbkdf2` + `AesGcm` (.NET). Stdlib only.

## Relay rules (Worker + DO)
- Route `/r/<32 hex>` to DO named by room. Reject other paths (404) and non-hex rooms.
- Max 2 sockets per room. Third gets close 4001 "room full".
- Room lives 10 min waiting for second peer, then closes 4002. Session hard cap 2 hours (4003).
- Frame size cap 64 KB (close 1009). Rate cap 50 frames/sec per socket (close 4008).
- Forward each frame to the other socket verbatim. Peer joined/left: relay sends plaintext control `{"relay":"peer-joined"}` / `{"relay":"peer-left"}`. Those are the only plaintext frames.
- Uses WebSocket hibernation API. No storage writes. Free-tier exhaustion: Worker returns 503, apps show "Session service is busy, try again later."

## Frame format
Text frame = base64url(nonce 12 bytes || AES-GCM ciphertext+tag). AAD = room. Plaintext is JSON:
`{"v":1,"seq":<int, per-sender, starts 1, strictly increasing>,"type":"...", ...}`
Receiver drops a frame whose seq <= last seen from that peer (replay). Decryption failure: drop and count; 3 failures close the session ("Code doesn't match").

## Messages
- `hello {role:"patient"|"clinician", app, version}`. Both send first. Session is "connected" when each side has decrypted the other's hello. QCM shows "Your clinician is connected" + End button.
- `profile.get {id}` (clinician). Patient replies `profile {re:id, name, csv, rev}`. `name` = the profile file name open in QCM (e.g. `default.csv`), `csv` = full QuadStick CSV text of that saved file (all modes, the same text QCM writes to the device), `rev` = hex SHA-256 of csv. If the open profile has unsaved edits, patient side reply `profile.error {re:id, code:"unsaved"}` and QCM prompts the patient to save. If nothing open: offer the device's default.csv if a QuadStick drive is found, else `code:"none"`.
- `profile.propose {id, name, csv, baseRev, note}` (clinician). Patient QCM: if current rev != baseRev -> reply `profile.result {re:id, ok:false, code:"stale"}`. Else run Validator; errors -> `ok:false, code:"invalid", errors:[...]` and show nothing to install. Else show diff dialog (which modes/rows change, clinician note) with Keep (default) / Use this. Use this -> save file (rescue copy of old kept) then offer Install to QuadStick through the existing InstallFlow (backup, verify, swap). Reply `profile.result {re:id, ok:true, rev:<new>, installed:true|false}` or `code:"declined"`. While a proposal dialog is open, a second propose gets `code:"busy"`.
- `live.start` / `live.stop` (clinician). While started and a QuadStick is connected, patient sends `live {t, lit:[{input, output}], buttons, axes}` ONLY on change, coalesced to at most 10 per second. `lit` reuses QCM's practice-light attribution (which profile rows are firing). Patient side shows a visible "Your clinician is watching your inputs" indicator while live is on.
- `bye {}` either side, then close. Closing the QCM window or End also ends.
- Unknown types ignored.

## Reconnect
No resume. Dropped socket = session over; patient clicks Start again for a new code. Pending proposal dialogs stay open locally but can't reply. Never auto-resend.

## Never
- The clinician can never write to the file or device without the patient clicking.
- Nothing about the session is stored on the relay. QCM and Clinic store nothing new except what the user saves.

## Interop and tests (from eng review, 2026-11-04)
- Shared vectors: `remote-vectors.json` (same file committed in both repos as `docs/specs/remote-vectors.json`). Both crypto implementations must reproduce room, key and decrypt every frame, and encrypt the plaintext with the given nonce to the exact frame.
- Relay URL configurable (env/setting) so tests run against `wrangler dev` on localhost. Default `wss://relay.bassamkamal.dev`.
- Connect timeout 10 s: "Couldn't reach the session service. Your network may block it. Try another network, or use Google Sheets sharing." 503 from relay: "Session service is busy, try again later."
- Each side has a fake-socket unit test for every message type and error code, plus the replay/seq rule and 3-bad-frames close.

## Privacy and store
- QCM PRIVACY.md: add "Clinician sessions" section: optional, end-to-end encrypted, relay by Cloudflare sees only encrypted bytes and the connection's IP address, nothing stored.
- Clinic: Tauri `network` on for relay host only, CSP `connect-src 'self' wss://relay.bassamkamal.dev`. PRIVACY.md updated. Reopens spec decision 7.
- Store listing copy does not mention sessions until the relay is deployed.
