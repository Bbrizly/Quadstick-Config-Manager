# Gate 0 adversarial review

Date: 2026-08-29
Implementation base: `f7783944387202bcafaeb7ff3f67789098fa6a4e`

## Result

**Gate 0 engineering acceptance: PASS**, subject to the normal draft-PR human review before merge/cutover.

The implementation pass re-read the frozen source/test/tool trees, resolved the only explicit `UNASSESSED` porting-ledger row, converted OQ-001 serial from an assumption to an evidence-based deferred decision, established a content-pinned fixture corpus, and made the legacy suite + deterministic oracle executable in CI.

The specification requested a second reviewer/agent. No independent code-review agent is exposed in this execution environment, so this file records a separate adversarial pass by the implementation agent and keeps PR #5 draft for independent human review. This limitation does **not** waive later hardware or release review gates.

## Critical risk → proof map

| Critical risk | Behavior/test proof |
|---|---|
| CSV/parser drift corrupts or silently changes profiles | B-001..B-010 → T-001..T-004 + C# canonical oracle + firmware oracle |
| Wrong path/device or interrupted install damages device config | B-011..B-020 → T-005..T-008 |
| LED/file ordering diverges | B-021 → T-008/exact table tests |
| HID descriptor/report assumptions break live input | B-022..B-025 → T-009/T-010 + hardware matrix |
| JS/WebView gains arbitrary native authority | B-043/B-044 → T-011/T-012 |
| Google auth/backup loses data or leaks credentials | B-026..B-030 → T-017/T-018 |
| telemetry/crash migration violates privacy contract | B-031/B-040 → T-019/T-020/T-026 |
| settings migration loses accessibility/consent/preferences | B-032/B-033 → settings migration tests + T-021 for locale-sensitive behavior |
| import/community network behavior regresses offline/privacy | B-035/B-036 → T-015/T-016 |
| updater/package cutover strands users | B-041 → T-022/T-025 |
| agent can bypass typed profile operations | B-039/B-010 → T-023 + qsf parity |
| performance claims hide regressions | T-024 + `baseline-performance.md` |

## Resolved Gate-0 findings

### Settings persistence

Exact symbols are in `src/QuadStick.App/Theme.cs`:

- `AppSettings`
- `DriveLink`
- `Settings`
- `SettingsJsonContext`

Current semantics:

- default per-user path: `<ApplicationData>/QuadStickConfigManager/settings.json`;
- public-field JSON serialization with case-insensitive legacy reads;
- failed reads return defaults rather than crash;
- failed saves are best-effort/false-returning;
- writes use `ProfileFile.WriteAtomic` to avoid truncated settings after crash;
- settings include theme/language/scale/reduced-motion/window state, recents, Google links, custom names, analytics/crash choices and random install ID.

Target consequence: Rust settings migration must read current JSON field names/casing and preserve consent/Drive/custom-name state; a clean rewrite cannot simply start with a new schema.

### Production serial

Repository search at the frozen SHA found no production `SerialPort` usage. The current app's live device read path is HidSharp/HID and config installation/library path is mass storage. Serial therefore moves from open A-behavior to **deferred/not-current**. A Rust serial crate is forbidden until source/history/hardware evidence demonstrates a production requirement.

## CI evidence

Rewrite Gate 0 run for commit `f16fab62c8bfd1799f7b47491da3ed7c1da2bc3b` passed:

- fixture manifest SHA-256 verification;
- full legacy `.NET` test suite;
- C# oracle compile and deterministic self-check;
- canonical fixture generation.

The next commit strengthens that same gate by validating generated JSON against `fixtures/oracle/schema.json`.

## Permission to proceed

Pure Rust parity work may begin. No Avalonia runtime or device-write path may be retired based on Gate 0 alone.
