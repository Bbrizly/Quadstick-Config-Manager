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

Open questions OQ-001..OQ-020 are not ADRs until a decision is made. When resolved, either amend an ADR with date/evidence or add a new ADR; do not bury architectural changes in implementation PR prose.