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
```

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