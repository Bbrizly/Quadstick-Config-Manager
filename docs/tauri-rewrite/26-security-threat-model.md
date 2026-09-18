# Security threat model

## Assets

1. User's working QuadStick configuration and backups.
2. QuadStick mounted storage/default/prefs files.
3. Google refresh token/access token and shared sheet permissions.
4. Local files chosen by user.
5. Privacy: paths, profile names, crash details, usage events.
6. Update signing key/release integrity.
7. Native capabilities exposed to WebView.

## Trust boundaries

```text
untrusted CSV/XLSX/community/cloud content
    → Rust parser/import boundary
WebView/TypeScript
    → Tauri IPC validation boundary
Rust core
    → native filesystem/HID/network/secure-store adapters
update endpoint/artifact
    → signature verification boundary
```

## Threats and controls

| Threat | Control |
|---|---|
| path traversal filename | `SafeDeviceFileName`; direct-child join/canonicalization; opaque file IDs |
| stale mount path reused by unrelated drive | generation/fingerprint + marker revalidation immediately before mutation |
| symlink/reparse escape | reject/canonicalize target relationship; platform tests |
| malicious/huge CSV | input size limits + linear parser tests/fuzzing + no panic |
| XLSX zip bomb/XML abuse | compressed/uncompressed size caps, sheet/cell caps, no macro/formula execution |
| HTML/script in profile notes | React text rendering only; never `dangerouslySetInnerHTML` for profile content |
| generic WebView filesystem/shell power | no fs/shell/http capability; domain commands only |
| arbitrary network exfiltration | remote API requests performed by native allowlisted services; strict CSP |
| OAuth code interception/CSRF | loopback 127.0.0.1 random port + PKCE + random state + timeout |
| token theft | Keychain/DPAPI/platform secure store; never IPC/log token |
| malicious cloud conflict replacing good local | validate remote first + rescue local before replacement |
| partial device write | off-device backup + temp write + read-back + replace + restore path |
| telemetry leaking profile/path | closed event/property allowlist + scrubbing + consent |
| supply-chain package compromise | lockfiles, minimal dependencies, cargo/npm audit/review, Dependabot/Renovate policy if chosen |
| updater MITM/artifact tamper | Tauri updater signatures; HTTPS; signing key protected outside repo |
| remote content loaded inside privileged WebView | forbid normal remote navigation; external links system browser |
| IPC malformed/oversized payload | Serde typed DTO + explicit lengths and value validation |
| agent obtains machine power | agent only produces typed editor ops; no shell/filesystem/native pass-through |

## Frontend compromise assumption

Treat WebView JS as **untrusted relative to native privileges**. If an XSS occurs, the attacker should still be limited to QCM's narrow domain commands. Commands themselves enforce state/confirmation/path/device checks; capability config alone is not the authorization model.

## Sensitive logging

Never log:
- OAuth tokens/secrets;
- full Google response bodies containing credentials;
- unrestricted home paths by default;
- raw profile content in analytics;
- crash dumps without explicit report path/consent.

Diagnostics can contain sanitized device/product/version and hashed/relative identifiers when useful.

## Security review gate

Before beta, manually enumerate every registered Tauri command and every capability file. Anything not referenced by API ledger or required core window behavior is removed.