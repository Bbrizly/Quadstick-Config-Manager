/**
 * For an app that hosts the editor in its own window: mount `EditorHost`
 * with your own client and root. Import `./styles/app.css` once as well.
 * The native client is `./host/tauri`, apart so a web host never loads it;
 * `./host/labels` is the editor's words alone, without the editor.
 */
export { EditorHost, type EditorHostHandle, type EditorHostProps } from "./app/EditorHost";
export { DeviceLinkHost, type DeviceLinkHostProps } from "./app/DeviceLinkHost";
export type { LiveButtons } from "./features/device/DeviceLinkDialog";
export type { ConsoleClient, ConsolePort, ConsoleConnectOutcome, StrengthSetup } from "./platform/consoleContracts";
export { profileLabels, type ProfileLabels } from "./app/profileLabels";
export { LOCALE_TAGS, type LocaleTag } from "./i18n";
export { NETWORK_METHODS, withoutNetwork, type NetworkMethod } from "./platform/network";
export type {
  AppSnapshot, Capabilities, CloseOutcome, DevicePresenceSnapshot, EditorSnapshot, InstallPlan,
  InstallReceipt, LiveSnapshot, QcmClient, SaveReceipt, Subscription,
} from "./platform";
export { asQcmError, QcmCommandError } from "./platform";
