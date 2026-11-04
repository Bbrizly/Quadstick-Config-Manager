# Remote clinician session, v1 spec (2026-11-04)

Drew Redepenning (UPMC) asked 2026-10-30: both sides click connect, clinician sees live input/output, pulls the client's profile, edits, sends it back. QuadStick only. Free. Nothing personal stored on a server.

## Transport (decided after Codex review)
Cloudflare Worker + Durable Object WebSocket relay, `wss://relay.bassamkamal.dev/r/<room>`. Free tier. Outbound WSS 443 passes hospital firewalls. No WebRTC libraries anywhere: browser `WebSocket`, .NET `ClientWebSocket`. Relay forwards ciphertext in memory, writes nothing to storage, logs nothing about content.
Rejected: WebRTC P2P (needs STUN + TURN anyway for hospital UDP blocks, SIPSorcery licence clause, most moving parts), copy-paste SDP (unusable for patients), iroh (no .NET), Supabase realtime (free projects pause). Upgrade path: same Worker can do WebRTC signaling later if ever needed.

## Pairing code
- Patient QCM: Share with my clinician > Start. QCM makes a code: 12 chars from Crockford base32 (no I L O U), shown `K7QM-4XPA-9RTD` (60 bits). Also "Copy link": `https://clinic.bassamkamal.dev/#join=K7QM4XPA9RTD` (fragment never reaches a server).
- Normalize: uppercase, strip `-` and spaces, map O->0, I/L->1.
- `bits` = PBKDF2-HMAC-SHA256(password = normalized code (utf8), salt = "qcm-session-v2" (utf8), 600000 iterations, 48 bytes). One run per session.
- `room` = lowercase hex of bits[0..16) (32 hex chars). The relay never sees the code.
- `key` = bits[16..48). AES-256-GCM.
- Why one derivation (v2, 2026-11-04, after Codex review): v1 made the room a fast SHA-256 of the code, so anyone who saw room ids (the relay operator, a log) could brute-force the 60-bit code offline cheaply and then derive the key. The room must never be cheaper to compute than the key. Now both come out of the same slow PBKDF2 run, and the room bits and key bits are independent outputs of it.
- Residual risk (Codex, 2026-11-04): the room, like any frame, still lets someone who records it test guesses offline. Each guess now costs 1.2M HMAC-SHA256 rounds (two PBKDF2 blocks), so the 60-bit space averages millions of GPU-years. A recorded session is protected by that cost alone (no forward secrecy). Accepted. Removing it fully needs a PAKE, which stdlib does not have.
- Cost: about 0.1 to 1 s once per session (WebCrypto or .NET). Both apps show "Connecting..." while it runs, and QCM runs it off the UI thread.
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
Every plaintext also carries `"from":"clinician"` (Clinic) or `"from":"patient"` (QCM). Receiver drops a frame whose seq <= last seen from that peer (replay). Decryption failure: drop and count; 3 failures close the session ("Code doesn't match").

## Messages
- `hello {role:"patient"|"clinician", app, version}`. Both send first. Session is "connected" when each side has decrypted the other's hello. QCM shows "Your clinician is connected" + End button.
- `profile.get {id}` (clinician). Patient replies `profile {re:id, name, csv, rev}`. `name` = the profile file name open in QCM (e.g. `default.csv`), `csv` = full QuadStick CSV text of that saved file (all modes, the same text QCM writes to the device), `rev` = hex SHA-256 of csv. If the open profile has unsaved edits, patient side reply `profile.error {re:id, code:"unsaved"}` and QCM prompts the patient to save. If nothing open: offer the device's default.csv if a QuadStick drive is found, else `code:"none"`.
- `profile.propose {id, name, csv, baseRev, note}` (clinician). Patient QCM: if current rev != baseRev -> reply `profile.result {re:id, ok:false, code:"stale"}`. Else run Validator; errors -> `ok:false, code:"invalid", errors:[...]` and show nothing to install. Else show diff dialog (which modes/rows change, clinician note) with Keep (default) / Use this. Use this -> save file (rescue copy of old kept) then offer Install to QuadStick through the existing InstallFlow (backup, verify, swap). Reply `profile.result {re:id, ok:true, rev:<new>, installed:true|false}` or `code:"declined"`. While a proposal dialog is open, a second propose gets `code:"busy"`.
- `live.start` / `live.stop` (clinician). While started and a QuadStick is connected, patient sends `live {t, lit:[{input, output}], buttons, axes}` ONLY on change, coalesced to at most 10 per second. `lit` reuses QCM's practice-light attribution (which profile rows are firing). Patient side shows a visible "Your clinician is watching your inputs" indicator while live is on.
- `bye {}` either side, then close. Closing the QCM window or End also ends.
- Unknown types ignored.
- Reflection guard: a receiver drops, and counts as a bad frame (toward the 3 that close the session), any message whose `from` is missing or equals its own role. Why: one key and one AAD serve both directions, so a frame the relay sends back to its sender would decrypt and look valid (a clinician's `bye` returned to the clinician, a patient's `profile` returned to the patient). `from` inside the ciphertext is authenticated, so the relay cannot change it. The vectors are unchanged: they test the crypto only.

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

## Implementation notes (Clinic, 2026-11-04)
- Control frames: when the second socket joins, the relay sends `{"relay":"peer-joined"}` to both. Each side sends its hello on that frame (or in answer to the other's hello, if that came first). A hello sent before the peer is there is dropped by the relay, so QCM must also send hello on peer-joined.
- A room that has had two peers takes no new peer (4001) until it is empty: no resume.
- The 2 hour cap counts from the first join.
- Busy: a browser cannot read the HTTP status of a failed WebSocket upgrade, so a 503 looks like any failed connection. When the Worker runs but the room's Durable Object cannot be reached, the relay accepts the socket and closes it with 1013 ("busy"); apps show the busy sentence for 1013. A Worker over its own free-tier limit still reads as "Couldn't reach the session service".
- Binary frames are closed with 1003. Frames are text only.
- The clinician refuses to send a frame over 64 KB (the relay would close the session) and says the profile is too big; Google Sheets sharing is the fallback.
- Clinic: `HostConfig.network` stays `false` (native commands only). The page's WebSocket is allowed by CSP `connect-src wss://relay.bassamkamal.dev`; the Tauri dev CSP also allows `ws://localhost:8787` for `wrangler dev`. `pnpm dev` (the website on this machine) carries no CSP; only deployed web builds get head.html, which allows the wss host alone (`CLINIC_RELAY=ws://localhost:8787 pnpm dev`).
- Relay admission: at most 20 new sockets a minute per IP address (Workers rate limiting binding, per Cloudflare location); past it the socket is closed 1013 (busy).
