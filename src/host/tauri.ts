/**
 * The native client, for a host that runs in this app's own Tauri webview.
 * Its own entry, apart from `../host`, so a host on the web never loads the
 * Tauri API.
 */
export { TauriQcmClient } from "../platform/tauriQcmClient";
