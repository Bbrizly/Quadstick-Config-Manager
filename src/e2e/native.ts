/**
 * The smoke test's own native calls. Only `pnpm e2e:mac` builds this file
 * (Vite mode `e2e`), and only an `e2e` cargo build registers the commands.
 */
import { invoke } from "@tauri-apps/api/core";

export function log(line: string): Promise<void> {
  return invoke("e2e_log", { line });
}

/** A real close request on the main window, as the title bar's button sends. */
export function requestWindowClose(): Promise<void> {
  return invoke("e2e_request_close");
}

export function writeReport(report: string): Promise<void> {
  return invoke("e2e_report", { report });
}

export function exit(code: number): Promise<void> {
  return invoke("e2e_exit", { code });
}
