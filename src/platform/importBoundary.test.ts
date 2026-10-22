/**
 * One file may talk to Tauri, and this says which.
 *
 * The rule is not tidiness. A component that calls `invoke` cannot be rendered
 * in a browser, cannot be tested against the mock, and turns the list of things
 * the frontend asks the operating system for into something you have to grep for
 * rather than read. oxlint states the same rule; this holds it even if the lint
 * config is edited.
 *
 * The needles are assembled from pieces rather than written out, so this file
 * does not trip its own check and does not need an exemption for itself. An
 * allowlist with the enforcing test on it is how a rule starts rotting.
 */

import { describe, expect, it } from "vitest";

import { TAURI_COMMANDS } from "./tauriQcmClient";

/** An import of the Tauri API, not a mention of it in a comment. */
const TAURI_IMPORT = new RegExp(`(from|import)\\s*\\(?\\s*["']@tauri${"-"}apps/`);
const IPC_GLOBAL = new RegExp(`__${"TAURI"}_INTERNALS__`);

/** The one file allowed to import the Tauri API. */
const ADAPTER = "./tauriQcmClient.ts";

/** The smoke test's native calls. Built only in Vite mode `e2e`, never shipped. */
const E2E_ADAPTER = "../e2e/native.ts";

/** The one file allowed to look for the IPC global, to pick a client. */
const RESOLVER = "./resolve.ts";

const sources: Record<string, string> = import.meta.glob("../**/*.{ts,tsx}", {
  eager: true,
  query: "?raw",
  import: "default",
});

function offenders(needle: RegExp, allowed: readonly string[]): string[] {
  return Object.entries(sources)
    .filter(([path]) => !allowed.includes(path))
    .filter(([, text]) => needle.test(text))
    .map(([path]) => path)
    .toSorted();
}

function adapterCommandCalls(): string[] {
  const adapter = sources[ADAPTER] ?? "";
  const nativeCall = /\b(?:call|callArgs|disposal)\s*(?:<[^>\n]+>)?\s*\(\s*"([a-z0-9_:-]+)"/gu;
  return [...adapter.matchAll(nativeCall)].map((match) => match[1] ?? "").filter(Boolean).toSorted();
}

describe("import boundary", () => {
  it("finds the frontend sources it is meant to be checking", () => {
    expect(Object.keys(sources).length).toBeGreaterThan(5);
    expect(Object.keys(sources)).toContain(ADAPTER);
  });

  it("keeps Tauri imports inside the platform adapter", () => {
    expect(Object.keys(sources)).toContain(E2E_ADAPTER);
    expect(offenders(TAURI_IMPORT, [ADAPTER, E2E_ADAPTER])).toEqual([]);
  });

  // The smoke test ships in no build but `vite build --mode e2e`: one dynamic
  // import, behind a mode check Vite folds to false everywhere else.
  it("reaches the smoke test only from main.tsx, behind Vite mode e2e", () => {
    const E2E_IMPORT = new RegExp(`(from|import)\\s*\\(?\\s*["'][./]*/${"e2e"}/`);
    const importers = offenders(E2E_IMPORT, []).filter((path) => !path.startsWith("../e2e/"));
    expect(importers).toEqual(["../main.tsx"]);
    const main = sources["../main.tsx"] ?? "";
    const gate = main.indexOf('import.meta.env.MODE === "e2e"');
    expect(gate).toBeGreaterThan(-1);
    expect(main.search(E2E_IMPORT)).toBeGreaterThan(gate);
  });

  // A component reaching for the IPC global directly would be the same bug
  // wearing a different hat.
  it("keeps the raw IPC global out of every file but the resolver", () => {
    expect(offenders(IPC_GLOBAL, [RESOLVER])).toEqual([]);
  });

  // Domain words legitimately appear in contracts and mocks, so scanning every
  // quoted string for a command name produces false positives. Instead, extract
  // only the adapter helpers that can actually reach native code and require
  // that those call sites exactly match the exported command ledger.
  it("keeps native command call sites synchronized with the adapter ledger", () => {
    expect(adapterCommandCalls()).toEqual([...TAURI_COMMANDS].toSorted());
  });
});
