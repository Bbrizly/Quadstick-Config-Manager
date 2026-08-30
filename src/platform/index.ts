/**
 * The platform boundary.
 *
 * Everything the UI knows about the native side comes from here. Import from
 * `../platform`, never from `@tauri-apps/*`.
 */

export type {
  AppSettings,
  AppSnapshot,
  Capabilities,
  CloseDisposition,
  CloseOutcome,
  ConfirmationKind,
  ConfirmationRequirement,
  DeletePlan,
  DeleteReceipt,
  DeviceLibrarySnapshot,
  DevicePresenceSnapshot,
  DeviceProfileEntry,
  DeviceSummary,
  EditorOp,
  EditorSnapshot,
  InstallPlan,
  InstallReceipt,
  InterfaceScale,
  Issue,
  IssueSeverity,
  LedColour,
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
