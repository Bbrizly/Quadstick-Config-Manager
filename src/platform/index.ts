/** Everything the UI knows about native code. */
export type { CommunityCatalog, CommunityClient, CommunityProfile } from "./communityContracts";
export type {
  AppSettings, AppSnapshot, Capabilities, CloseDisposition, CloseOutcome, ConfirmationKind,
  ConfirmationRequirement, DeletePlan, DeleteReceipt, DeviceInvalidation, DeviceLibrarySnapshot,
  DevicePresenceSnapshot, DeviceProfileEntry, DeviceSummary, EditorOp, EditorSnapshot, InstallPlan,
  InstallProgress, InstallReceipt, InterfaceScale, Issue, IssueSeverity, LedColour, LiveMotion,
  LiveSnapshot, LiveStatus, Mode, ModelChoice, PickerGrouping, ProfileSource, QcmErrorPayload,
  RecoveryAction, SaveReceipt, SettingsPatch, Subscription, ThemeChoice,
} from "./contracts";
export type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
export type {
  DriveBackupOutcome, DriveConflictChoice, DriveFile, DriveResolution, DriveShare,
} from "./driveContracts";
export type { GoogleAuthStatus } from "./googleContracts";
export type {
  PreferenceCatalog, PreferenceDefinition, PreferenceEditorKind, PreferenceOption,
} from "./preferenceContracts";
export type {
  WorkbookExportReceipt, WorkbookImportReview, WorkbookLimitation, WorkbookMode,
  WorkbookSkippedTab, WorkbookTabRename,
} from "./workbookContracts";
export { ERROR_CODES, INTERFACE_SCALES } from "./contracts";
export { MockQcmClient } from "./mockQcmClient";
export { asQcmError, isQcmCommandError, QcmCommandError, type QcmClient } from "./qcmClient";
export { resolveQcmClient, runningUnderTauri } from "./resolve";
