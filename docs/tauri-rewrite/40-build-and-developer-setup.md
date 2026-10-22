# Build and developer setup

## Toolchain target

At implementation start pin:
- Rust stable 1.98 (current at audit; advance only deliberately);
- Node 24 LTS;
- pnpm exact version in `packageManager` after verifying current stable;
- Tauri 2.11.x exact patch;
- platform prerequisites from the current Tauri 2 docs.

Commit `rust-toolchain.toml`, `.node-version` (or `.nvmrc`), `package.json` engines/packageManager, `Cargo.lock`, `pnpm-lock.yaml`.

## Target commands

The top-level developer UX should become:

```bash
pnpm install
pnpm dev          # tauri dev
pnpm test         # fast frontend + Rust unit/parity subset
pnpm check        # fmt/lint/typecheck/clippy
pnpm build        # production Tauri build on current OS
pnpm e2e:mac      # smoke test: the real app walks its own UI (macOS)
```

`pnpm e2e:mac` (`scripts/e2e-mac.sh`) builds with the `e2e` cargo feature and
Vite mode `e2e`, then launches the real binary with HOME set to a throwaway
`qcm-e2e-home.*` folder, so real profiles, settings and the rescue draft are
never touched. The WebView walks the UI with real DOM events against the real
Rust backend (consent, Home, new profile, edit, Save As, undo, problems bar,
dialog and menu exits, device pages with a stand-in drive, Settings, Arabic,
the unsaved-changes prompts, quit), checks each screen is idle, writes
`report.json` and quits. Two runs by default (`QCM_E2E_RUNS`); a failed run
keeps its folder. No Accessibility or Screen Recording permission is needed.
Normal builds contain none of it: the feature is off and Vite drops the
`src/e2e` import outside mode `e2e`. The walk brings its window forward first. If the page stays hidden it still
runs, but animation timing is reported as not observable, with the reason (screen locked, display asleep, occluded).

Keep `make` as a compatibility/convenience façade if useful:

```text
make test
make run
make build
make package
make pseudo
make release VERSION=x.y.z
```

During migration, `make test` must run both legacy .NET tests and new Rust/TS tests until legacy retirement.

## Windows setup

Document exact current Tauri prerequisites at TASK-010, including Rust MSVC toolchain, C++ build tools/Windows SDK/WebView2 as required, Node/pnpm. Add `scripts/bootstrap-windows.ps1` only if it genuinely reduces setup ambiguity; it must not install privileged software silently.

## macOS setup

Xcode Command Line Tools, Rust, Node 24 LTS/pnpm, current Tauri prereqs. Local dev should work without signing credentials; signing/notarization only release.

## Linux setup

List exact distro packages for GTK/WebKitGTK/build essentials from current official Tauri docs. Provide Ubuntu baseline command plus note for other supported distros. HID/udev packages/rules only once proven necessary.

## Environment/secrets

Local source build must run with:
- Google backup disabled when client credentials absent;
- analytics disabled when token absent;
- no hard failure for ordinary dev/test due to missing production secrets.

Release CI may deliberately fail if Google credentials are required for the published feature, matching current policy.

## Generated files

Generated TS bindings/oracle outputs/localizations must have deterministic commands and checked-in policy. Never hand-edit generated outputs without source change.

## Migration coexistence

Legacy commands remain documented:

```bash
dotnet test QuadStick.sln --nologo -c Release
dotnet run --project src/QuadStick.App
```

The Tauri beta uses a distinct bundle/app-data identity until cutover so a developer can compare both side-by-side.