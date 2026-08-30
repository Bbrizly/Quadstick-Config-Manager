/**
 * The platform boundary.
 *
 * Everything the UI knows about the native side comes from here. Import from
 * `../platform`, never from `@tauri-apps/*`: the import boundary test and the
 * oxlint config both hold that line, and the reason is that every screen has to
 * render in a browser against the mock before its command exists.
 */

export type {
  AppSettings,
  AppSnapshot,
  Capabilities,
  CloseDisposition,
  CloseOutcome,
  EditorOp,
  EditorSnapshot,
  InterfaceScale,
  Issue,
  IssueSeverity,
  Mode,
  ModelChoice,
  PickerGrouping,
  ProfileSource,
  QcmErrorPayload,
  RecoveryAction,
  SaveReceipt,
  SettingsPatch,
  Subscription,
  ThemeChoice,
} from "./contracts";
export { ERROR_CODES, INTERFACE_SCALES } from "./contracts";
export { MockQcmClient } from "./mockQcmClient";
export { asQcmError, isQcmCommandError, QcmCommandError, type QcmClient } from "./qcmClient";
export { resolveQcmClient, runningUnderTauri } from "./resolve";
