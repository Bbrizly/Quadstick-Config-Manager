/** Everything the UI knows about native code. */
export type { CommunityCatalog, CommunityClient, CommunityProfile } from "./communityContracts";
export type {
  AppSettings, AppSnapshot, Capabilities, CloseDisposition, CloseOutcome, ConfirmationKind,
  ConfirmationRequirement, CrashReportChoice, CrashResolveResult, DeletePlan, DeleteReceipt,
  DeviceInvalidation, DeviceLibrarySnapshot, DevicePresenceSnapshot, DeviceProfileEntry,
  DeviceSummary, EditorOp, EditorSnapshot, InstallCancelReason, InstallPlan, InstallProgress, InstallReceipt,
  InterfaceScale, Issue, IssueSeverity, LedColour, LiveMotion, LiveSnapshot, LiveStatus, Mode,
  CardSentenceStyle, ModelChoice, PendingCrashReport, PendingRescue, PickerGrouping, ProfileSource, QcmErrorPayload,
  RecoveryAction, SaveReceipt, SettingsPatch, Subscription, ThemeChoice, UpdateResult,
} from "./contracts";
export type {
  ConsoleClient, ConsoleConnectOutcome, ConsolePort, ConsoleProfilePick, ConsoleSent, StrengthSetup,
} from "./consoleContracts";
export { CONSOLE_ERROR_CODES } from "./consoleContracts";
export type {
  BridgeState, ControllerClient, ControllerOptions, MacGamepadResult,
} from "./controllerContracts";
export type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
export type {
  DriveBackupEvent, DriveBackupOutcome, DriveConflictChoice, DriveFile, DriveResolution, DriveShare,
  RestoreItem, RestoreReason, RestoreSummary,
} from "./driveContracts";
export type {
  FollowState, LatestCheck, LatestReplace, ProfileChange, SheetRefusal, SheetsImport,
} from "./followContracts";
export type { GoogleAuthStatus } from "./googleContracts";
export type {
  PreferenceCatalog, PreferenceDefinition, PreferenceEditorKind, PreferenceOption,
} from "./preferenceContracts";
export type {
  WorkbookExportReceipt, WorkbookImportReview, WorkbookLimitation, WorkbookMode,
  WorkbookSkippedTab, WorkbookTabRename,
} from "./workbookContracts";
export { ERROR_CODES, INTERFACE_SCALES, USAGE_NOTICE_VERSION } from "./contracts";
export { MockQcmClient } from "./mockQcmClient";
export { asQcmError, isQcmCommandError, QcmCommandError, type QcmClient } from "./qcmClient";
export { resolveQcmClient, runningUnderTauri } from "./resolve";
export { FUNCTIONS, INPUTS, KNOWN_OUTPUTS, LEGACY_INPUTS, PICKER_INPUTS, REAR_JOYSTICK } from "./vocab";
export type { CustomNameDrafts, LibraryCard, TemplateEntry, TemplateReceipt, TemplateRename } from "./libraryContracts";
