# Tauri capabilities and permission plan

## Implemented default-deny posture

The main WebView is deliberately lower privilege than the native application. `src-tauri/capabilities/main.json` is local-only, bound to the `main` window, and grants **zero plugin permissions**. Filesystem, shell, opener, HTTP, process, updater, secure-store and HID access stay behind native domain code.

Custom QCM commands are the only privileged surface the frontend uses. They accept typed/bounded request shapes, resolve opaque identities in Rust, revalidate device state, and return redacted DTOs rather than host paths or raw operating-system errors.

## Production browser boundary

`src-tauri/tauri.conf.json` enforces:

- `withGlobalTauri: false`;
- prototype freezing enabled;
- asset protocol disabled with an empty scope;
- production CSP with `default-src 'self'` and `script-src 'self'`;
- frontend network limited to Tauri IPC (`'self'`, `ipc:`, `http://ipc.localhost`);
- objects, frames, frame ancestors and form submission disabled;
- no remote script/style/network wildcard.

The native shell additionally registers an `on_navigation` guard. Packaged builds accept only QCM's own `tauri://localhost` or `http://tauri.localhost` origins. Debug development additionally accepts exactly `http://localhost:1420`. HTTP(S) remote sites, `file:` and `data:` navigations are rejected before they commit.

## No broad frontend plugins

The shipped rewrite dependency manifests intentionally do **not** include Tauri fs, shell, HTTP, opener, process, store/stronghold or updater plugins. Native file picking uses `rfd` inside a QCM command; it does not give the WebView a generic dialog/path API. HID uses the native Rust adapter and never exposes HID handles or paths over IPC.

## Enforcement

`src/platform/securityBoundary.test.ts` is the static security gate. It fails if:

- the main capability gains a permission;
- the Tauri global or asset protocol is enabled;
- the production CSP gains broad network/script sources;
- one of the forbidden plugin packages enters Cargo/npm manifests;
- the frontend command ledger exposes a `plugin:*` escape hatch.

`src/platform/importBoundary.test.ts` separately ensures only the platform adapter may import `@tauri-apps/*` or know native command names. Rust tests pin the allowed and forbidden navigation origins and assert the registered QCM command list contains no `plugin:` command.

## Future permissions

A future feature does not get a generic plugin permission merely because a Tauri plugin exists. Add privilege only when all of the following are true:

1. a domain command cannot safely express the operation;
2. the permission is scoped to the exact window/resource needed;
3. the API ledger and threat model are updated;
4. the security harness gains a positive reason and a negative abuse test.

Secondary windows must receive their own capability instead of inheriting `main`. OAuth remains a system-browser flow, not a privileged embedded remote page.

## TASK-035 state

Implementation: **DONE**.

Automated verification code: **DONE, execution deferred with the current CI sweep**.

Physical hardware validation: **N/A**.
