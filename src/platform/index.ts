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
  DeviceInvalidation,
  DeviceLibrarySnapshot,
  DevicePresenceSnapshot,
  DeviceProfileEntry,
  DeviceSummary,
  EditorOp,
  EditorSnapshot,
  InstallPlan,
  InstallProgress,
  InstallReceipt,
  InterfaceScale,
  Issue,
  IssueSeverity,
  LedColour,
  LiveMotion,
  LiveSnapshot,
  LiveStatus,
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
export type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
export type {
  PreferenceCatalog,
  PreferenceDefinition,
  PreferenceEditorKind,
  PreferenceOption,
} from "./preferenceContracts";
export type {
  WorkbookExportReceipt,
  WorkbookImportReview,
  WorkbookLimitation,
  WorkbookMode,
  WorkbookSkippedTab,
  WorkbookTabRename,
} from "./workbookContracts";
export { ERROR_CODES, INTERFACE_SCALES } from "./contracts";
export { MockQcmClient } from "./mockQcmClient";
export { asQcmError, isQcmCommandError, QcmCommandError, type QcmClient } from "./qcmClient";
export { resolveQcmClient, runningUnderTauri } from "./resolve";
