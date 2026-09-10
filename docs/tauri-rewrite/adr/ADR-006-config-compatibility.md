# ADR-006 — Preserve raw grid; C# + firmware oracle defines parity

**Status:** ACCEPTED / RELEASE-CRITICAL

## Decision
Rust config model keeps lossless raw rows/cells and derives parsed document. We directly port current custom CSV/firmware semantics before considering third-party parser abstractions.

## Why
Firmware is not a normal CSV consumer; blank lines, A..J, 63-char keywords, 1023-byte lines and current K/L preservation are observable/safety behavior. A clean DTO serializer would silently lose information.

## Verification
Cross-language canonical oracle + exact byte golden tests + firmware oracle + property/fuzz tests.

## Rejected
- Deserialize to typed structs and reserialize: lossy.
- Use generic CSV crate first and fix differences later: compatibility risk.

## Consequence
Some historical quirks remain until separately classified B/C.