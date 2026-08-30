import type { JSX } from "react";

/**
 * The whole UI, for now. TASK-031 adds the QcmClient contracts and TASK-032 the
 * commands behind them; until then this only has to prove the window renders
 * and that React, Vite and the WebView agree on a build.
 *
 * Nothing here may import `@tauri-apps/*`. Only the platform adapter does.
 */
export function App(): JSX.Element {
  return (
    <main className="boot">
      <h1>QuadStick Config Manager</h1>
      <p data-testid="boot-state">Shell is running. No profile is loaded.</p>
    </main>
  );
}
