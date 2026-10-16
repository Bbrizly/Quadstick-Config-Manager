# Decision ledger

| ADR | Decision | Status | Revisit trigger |
|---|---|---|---|
| ADR-001 | Tauri 2 shell | ACCEPTED | hardware/a11y/signing blocker |
| ADR-002 | Rust canonical core | ACCEPTED | parity infeasible even with oracle |
| ADR-003 | React 19.2 | ACCEPTED | measured blocker |
| ADR-004 | opaque UI/core boundary | ACCEPTED | presentation-only exception only |
| ADR-005 | commands/events/Channels | ACCEPTED | Tauri IPC limitation |
| ADR-006 | raw-grid + oracle compatibility | ACCEPTED CRITICAL | firmware contract changes |
| ADR-007 | independent device capabilities | ACCEPTED | stable physical correlation proven |
| ADR-008 | separate state machines | ACCEPTED | serial/device architecture proven different |
| ADR-009 | minimal React state | ACCEPTED | demonstrated cache complexity |
| ADR-010 | stable error codes | ACCEPTED | none expected |
| ADR-011 | structured local diagnostics/privacy | ACCEPTED | privacy/product policy revision |
| ADR-012 | strangler migration | ACCEPTED | project cancellation/re-scope |
| ADR-013 | mobile later/native adapters | ACCEPTED/DEFERRED | physical mobile proof |
| ADR-014 | signed Tauri updater | ACCEPTED IN PRINCIPLE | store-only distribution/endpoint decision |
| ADR-015 | live input coalesces to one latest snapshot | ACCEPTED | a consumer that needs every intermediate report |
| ADR-016 | motion only on interaction: transitions <=150 ms on hover/press, press lands at once, nothing loops while idle, all off under reduced motion | ACCEPTED (phase 5) | an animation that has to run with no input |
| ADR-017 | the browser mock validates only unknown outputs/inputs, worded as Rust, so the problems bar is true on the fixture; the app's issues always come from `qcm_config::validate` | ACCEPTED (phase 6) | the mock needing any other rule |
| ADR-018 | Home library cards and templates: opaque per-listing card ids, paths stay in `library_shell.rs`; card menu actions open the profile, then run the editor's own action | ACCEPTED (phase 6) | a card action that must not open the profile || ADR-019 | the mount path crosses as display text only (`device_location`); every operation still takes the opaque device id | ACCEPTED (phase 7) | a feature needing the path for anything but showing it |
| ADR-020 | supersedes ADR-018's card rule: card menu actions run on a session held out of sight, closed when done; the editor does not open | ACCEPTED (phase 7) | an action that needs the editor |

Open questions OQ-001..OQ-020 are not ADRs until a decision is made. When resolved, either amend an ADR with date/evidence or add a new ADR; do not bury architectural changes in implementation PR prose.