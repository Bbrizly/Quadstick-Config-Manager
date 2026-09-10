# Glossary

**A/B/C/D/E** — behavior classification: required compatibility / intentional improvement / bug fix / unresolved / retire.

**Action name** — QCM-friendly user label stored in CSV column L; firmware does not use it.

**Binding** — a profile row associating output + function + ordered input history cells.

**Canonical snapshot** — deterministic JSON projection used to compare C# and Rust behavior.

**Channel** — Tauri IPC streaming primitive selected for ordered/high-rate live frames/progress; not the same as QuadStick mode channel.

**ConfirmationRequirement** — short-lived core-issued authorization context for a specific destructive operation, not a generic UI boolean.

**Device generation** — native monotonic identifier for one discovery snapshot used to reject stale operations.

**Device-safe CSV** — QCM serialization after firmware-required trimming/newline/header/separator normalization.

**EditorSnapshot** — revision-tagged read-only DTO React renders from Rust canonical profile state.

**FirmwareOracle** — current C# test transcription/model of actual QuadStick firmware reader used to detect app/device disagreement.

**HID capability** — gamepad report interface used by current `LiveInput` for practice/live output state.

**K/L/M+** — CSV columns beyond firmware-used A..J. K is notes, L QCM action name; later columns may contain preserved metadata/comments.

**Logical device** — potential user concept combining capabilities; not automatically created until storage/HID correlation is proven.

**Mass storage capability** — mounted QuadStick filesystem containing `default.csv` and profiles.

**OperationId** — opaque ID for a long/diagnostic operation.

**Opaque ID/ref** — frontend-safe identifier whose underlying filesystem/device/token handle remains native.

**Parity** — observable required behavior matches current/firmware contract; not architectural similarity.

**ProfileDocument** — parsed structured projection of raw CSV grid.

**ProfileSession** — Rust-owned active profile: raw grid, parsed document, issues, source, revision, dirty/undo.

**QcmClient** — sole frontend domain API; Tauri and mock implementations share it.

**qcm-config** — pure Rust crate for QuadStick format/editor semantics; no Tauri/OS/network.

**qcm-core** — Rust application services/state/ports coordinating profile/device/cloud policy.

**qcm-testkit** — fakes, fixtures and fault injection used by core/adapter tests.

**qsf** — existing machine-readable profile tool; target reimplements it atop qcm-config instead of creating a second parser.

**Raw grid** — lossless row/cell representation of CSV retained as canonical persistence basis.

**Revision** — monotonic profile mutation revision used to reject stale UI operations.

**StorageDeviceId** — opaque ID for a validated current QuadStick mount candidate.

**Strangler migration** — old app remains functional while proven slices of new core/UI replace behavior incrementally.

**True blank line** — a physically empty CSV line; critical because firmware does not treat a row of commas as section terminator.

**WebView** — platform web rendering process that runs React UI; treated as lower trust than native privileged layer.