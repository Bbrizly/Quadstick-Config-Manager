# QCM coding-agent rules

These rules apply to migration work in this repository.

1. Read `docs/tauri-rewrite/00-README.md` before touching the rewrite.
2. Work from a numbered task in `49-implementation-checklist.md` / `50-first-implementation-tasks.md`.
3. Treat current C# behavior as evidence, not architecture to copy.
4. Do not change QuadStick CSV semantics without differential/oracle tests.
5. Preserve the raw-grid/non-device columns unless a documented compatibility decision says otherwise.
6. The TypeScript frontend must not directly open serial/HID devices, arbitrary filesystem paths, keychains, shells, or Google credentials.
7. Only `src/platform/qcmClient.ts` (and its test doubles) may directly call Tauri command/channel/event APIs.
8. Rust domain/config crates must not depend on Tauri or presentation concepts.
9. Recoverable I/O must return typed errors; no `unwrap`/`expect` in production device/network/filesystem paths.
10. Use Tauri Channels for live HID/telemetry streaming; events are for low-frequency invalidation/state only.
11. Keep device writes transactional: validate, backup, temp-write, read-back verify, replace, restore/diagnose on failure.
12. Do not accept a UI-provided arbitrary path as proof that a location is a QuadStick. Revalidate at the native boundary.
13. Update `ledgers/PORTING_LEDGER.md`, `BEHAVIOR_LEDGER.md`, `API_LEDGER.md`, and `TEST_LEDGER.md` with every migration PR.
14. Accessibility acceptance criteria are release criteria, not polish.
15. Never silently turn an existing behavior into an improvement. Classify it A/B/C/D/E in the behavior ledger.
16. Do not delete Avalonia/C# code until its ledger entry is parity-tested and, where applicable, hardware-verified.
17. If behavior is unknown, add an `UNRESOLVED` item with evidence and a resolution experiment instead of guessing.
18. Never force-push migration branches or overwrite unrelated user work.

Source of truth for architecture decisions: `docs/tauri-rewrite/adr/`.