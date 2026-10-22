/**
 * For an app that hosts the editor in its own window: mount `EditorHost`
 * with your own client and root. Import `./styles/app.css` once as well.
 */
export { EditorHost, type EditorHostHandle, type EditorHostProps } from "./app/EditorHost";
export { NETWORK_METHODS, withoutNetwork, type NetworkMethod } from "./platform/network";
export type {
  AppSnapshot, Capabilities, CloseOutcome, EditorSnapshot, QcmClient, SaveReceipt,
} from "./platform";
export { asQcmError, QcmCommandError } from "./platform";
