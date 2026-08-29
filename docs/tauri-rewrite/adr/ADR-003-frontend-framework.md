# ADR-003 — React 19.2 frontend

**Status:** ACCEPTED

## Context
QCM needs a highly interactive SVG/device editor, strong accessibility tooling, testing ecosystem and future web/mobile UI reuse.

## Options
React, Svelte, Solid.

## Decision
Use **React 19.2 + TypeScript + Vite 8.x**.

## Rationale
React has the broadest accessibility/testing/component ecosystem and mature Tauri examples/integration, while its performance is sufficient if high-rate live frames are isolated from global state. The team can keep architecture library-light.

Svelte/Solid could reduce runtime overhead/boilerplate but offer less ecosystem leverage for this accessibility-heavy long-lived app; no measured QCM requirement justifies that trade.

## Constraints
No Redux/Zustand/React Query by default. No giant component library by default. StrictMode development. Only platform adapter imports Tauri.

## Revisit
If a prototype demonstrates a concrete accessibility/performance blocker, not preference.