# ADR-009 — Minimal frontend state layer

**Status:** ACCEPTED

## Decision
Use React local/context/controller state initially; no Redux/Zustand/React Query. Rust is authoritative local backend; snapshots are revisioned.

High-rate live frames use a local ref/external latest-state mechanism and animation-frame rendering, not global React context.

## Why
Adding a general cache/store would create a second state architecture before need is demonstrated.

## Revisit
If Phase 6 shows repeated server-state caching/invalidation bugs that a library measurably simplifies.