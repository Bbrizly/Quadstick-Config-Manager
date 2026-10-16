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
    const mock = new MockQcmClient();
    // `?library` fills "Your profiles" for the side-by-side shots; a plain
    // browser load stays empty like a first run.
    if (new URLSearchParams(window.location.search).has("library")) {
      const day = 86_400_000;
      const now = Date.now();
      mock.seedLibrary([
        { name: "Apex Legends", editedMs: now - 54 * day },
        { name: "Forza Horizon 4", editedMs: now - 52 * day, follows: true },
        { name: "Sea of Thieves", editedMs: now - 77 * day },
        { name: "Starfield", editedMs: now - 59 * day },
      ]);
    }
    if (new URLSearchParams(window.location.search).has("big")) {
      const { bigProfileGrid } = await import("./mockPreviews");
      mock.willOpenGrid("big.csv", bigProfileGrid());
    }
    if (new URLSearchParams(window.location.search).has("device")) {
      const { seedDevicePreview } = await import("./mockPreviews");
      seedDevicePreview(mock);
    }
    return mock;
  }
  const { TauriQcmClient } = await import("./tauriQcmClient");
  return new TauriQcmClient();
}
