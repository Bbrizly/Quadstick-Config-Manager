/**
 * Which client this build is talking to.
 *
 * In the packaged app the answer is always Tauri. In a browser there is no
 * native side at all, so the mock is what the UI develops against. The choice
 * is made once, here, and the Tauri module is imported lazily so a browser
 * never loads it: `@tauri-apps/api` reaches for an IPC global that does not
 * exist outside the WebView.
 */

import { MockQcmClient } from "./mockQcmClient";
import type { QcmClient } from "./qcmClient";

/**
 * True inside the Tauri WebView.
 *
 * Tauri 2 sets `__TAURI_INTERNALS__` on the window. It is checked rather than a
 * build flag so a Vite dev server opened in an ordinary browser gets the mock
 * instead of a page that throws on its first command.
 */
export function runningUnderTauri(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}

export async function resolveQcmClient(): Promise<QcmClient> {
  if (!runningUnderTauri()) {
    return new MockQcmClient();
  }
  const { TauriQcmClient } = await import("./tauriQcmClient");
  return new TauriQcmClient();
}
