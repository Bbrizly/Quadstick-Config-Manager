/**
 * The shapes that cross the native boundary.
 *
 * Nothing here carries a host path. Profiles use session ids, devices use
 * opaque device ids + generations, and destructive work uses one-shot plan and
 * confirmation ids.
 */

export type ProfileSource =
  | { readonly kind: "new" }
  | { readonly kind: "local"; readonly name: string }
  | {
      readonly kind: "device";
      readonly device: string;
      readonly generation: number;
      readonly name: string;
    }
  | { readonly kind: "community"; readonly catalogId: string };

export type IssueSeverity = "error" | "warning";

export interface Issue {
  readonly severity: IssueSeverity;
  readonly cell: string;
  readonly message: string;
  readonly fix: string;
  readonly kind: string;
}

export interface Mode {
  readonly index: number;
  readonly number: number | null;
  readonly kind: "mode" | "preferences" | "infrared";
  readonly name: string;
  readonly channel: string;
  readonly startRow: number;
  readonly bindingCount: number;
}

export interface EditorSnapshot {
  readonly sessionId: string;
  readonly revision: number;
  readonly dirty: boolean;
  readonly canUndo: boolean;
  readonly source: ProfileSource;
  readonly saveTarget: string | null;
  readonly title: string;
  readonly grid: readonly (readonly string[])[];
  readonly issues: readonly Issue[];
  readonly errorCount: number;
  readonly modes: readonly Mode[];
}

export interface SaveReceipt {
  readonly sessionId: string;
  readonly revision: number;
  readonly name: string;
  readonly bytes: number;
}

export type CloseDisposition = "if_clean" | "save" | "discard";

export type CloseOutcome =
  | { readonly kind: "closed" }
  | { readonly kind: "savedAndClosed"; readonly receipt: SaveReceipt }
  | { readonly kind: "keptOpenUnsavedChanges" };

export type EditorOp =
  | { readonly op: "set_cell"; readonly row: number; readonly col: number; readonly value: string }
  | {
      readonly op: "set_output";
      readonly row: number;
      readonly token: string;
      readonly action?: string;
    }
  | { readonly op: "add_row"; readonly sheet: number }
  | { readonly op: "delete_row"; readonly row: number }
  | { readonly op: "move_row"; readonly from: number; readonly to: number }
  | { readonly op: "add_mode"; readonly name: string }
  | { readonly op: "rename_mode"; readonly sheet: number; readonly name: string }
  | { readonly op: "set_mode_channel"; readonly sheet: number; readonly channel: string }
  | { readonly op: "normalize" };

export type ThemeChoice = "system" | "light" | "dark";
export type ModelChoice = "fps" | "original" | "singleton";
export type PickerGrouping = "detailed" | "wide" | "flat";

export const INTERFACE_SCALES = [100, 125, 150, 200] as const;
export type InterfaceScale = (typeof INTERFACE_SCALES)[number];

export interface AppSettings {
  readonly revision: number;
  readonly model: ModelChoice;
  readonly theme: ThemeChoice;
  readonly language: string;
  readonly interfaceScalePercent: number;
  readonly reduceMotion: boolean;
  readonly rememberWindow: boolean;
  readonly deviceCards: boolean;
  readonly pickerGrouping: PickerGrouping;
  readonly tutorialSeen: boolean;
}

export interface SettingsPatch {
  readonly model?: ModelChoice;
  readonly theme?: ThemeChoice;
  readonly language?: string;
  readonly interfaceScalePercent?: number;
  readonly reduceMotion?: boolean;
  readonly rememberWindow?: boolean;
  readonly deviceCards?: boolean;
  readonly pickerGrouping?: PickerGrouping;
  readonly tutorialSeen?: boolean;
}

export interface Capabilities {
  readonly profileEditing: boolean;
  readonly deviceInstall: boolean;
  readonly liveInput: boolean;
  readonly communityCatalog: boolean;
  readonly googleBackup: boolean;
  readonly agent: boolean;
}

export interface AppSnapshot {
  readonly version: string;
  readonly platform: string;
  readonly capabilities: Capabilities;
  readonly settings: AppSettings;
}

/** One mounted QuadStick as a window is allowed to know it. */
export interface DeviceSummary {
  readonly deviceId: string;
  readonly generation: number;
  readonly displayName: string;
  readonly writable: boolean;
  readonly freeBytes: number | null;
}

export interface DevicePresenceSnapshot {
  readonly devices: readonly DeviceSummary[];
  readonly changed: boolean;
}

export type LedColour = "purple" | "grey" | "blue" | "red";

export interface DeviceProfileEntry {
  readonly name: string;
  readonly fileNumber: number;
  readonly lights: readonly LedColour[];
  readonly protected: boolean;
}

/** The selectable device library. prefs.csv is deliberately separate. */
export interface DeviceLibrarySnapshot {
  readonly deviceId: string;
  readonly generation: number;
  readonly files: readonly DeviceProfileEntry[];
  readonly protectedFiles: readonly string[];
  readonly unnameable: number;
}

export type ConfirmationKind =
  | "overwrite_default_csv"
  | "overwrite_device_preferences"
  | "overwrite_existing_profile"
  | "delete_device_profile";

export interface ConfirmationRequirement {
  readonly confirmationId: string;
  readonly kind: ConfirmationKind;
  readonly summary: string;
}

export interface InstallPlan {
  readonly planId: string;
  readonly target: string;
  readonly bytes: number;
  readonly confirmation: ConfirmationRequirement | null;
}

export interface InstallReceipt {
  readonly operationId: string;
  readonly deviceId: string;
  readonly target: string;
  readonly bytes: number;
  readonly backup: string | null;
  readonly confirmedOnDevice: boolean;
  readonly stages: readonly string[];
}

export interface DeletePlan {
  readonly planId: string;
  readonly name: string;
  readonly bytes: number;
  readonly confirmation: ConfirmationRequirement;
}

export interface DeleteReceipt {
  readonly operationId: string;
  readonly deviceId: string;
  readonly name: string;
  readonly backup: string;
}

export type RecoveryAction =
  | "retry"
  | "reconnect_device"
  | "refresh_devices"
  | "wait_for_current_operation"
  | "choose_another_file"
  | "choose_another_name"
  | "choose_save_location"
  | "free_space_on_device"
  | "make_device_writable"
  | "confirm_again"
  | "reopen_profile"
  | "fix_profile_problems"
  | "restore_backup_by_hand"
  | "report_bug";

export interface QcmErrorPayload {
  readonly code: string;
  readonly message: string;
  readonly recoverable: boolean;
  readonly action: { readonly kind: RecoveryAction } | null;
  readonly operationId: string | null;
  readonly targetState: "unchanged" | "missing" | "replaced" | "restored" | "uncertain" | null;
  readonly backup: string | null;
}

export const ERROR_CODES = {
  configUnreadable: "QCM_CONFIG_PARSE_UNREADABLE",
  configTooLarge: "QCM_CONFIG_PARSE_TOO_LARGE",
  configHasBlockingProblems: "QCM_CONFIG_VALIDATION_BLOCKING",
  profileUnknownSession: "QCM_PROFILE_UNKNOWN_SESSION",
  profileRevisionConflict: "QCM_PROFILE_REVISION_CONFLICT",
  profileNothingToUndo: "QCM_PROFILE_NOTHING_TO_UNDO",
  profileOperationRejected: "QCM_PROFILE_OPERATION_REJECTED",
  profileNeedsSaveTarget: "QCM_PROFILE_NEEDS_SAVE_TARGET",
  profileSaveTargetOnDevice: "QCM_PROFILE_SAVE_TARGET_ON_DEVICE",
  deviceNotFound: "QCM_DEVICE_NOT_FOUND",
  deviceStale: "QCM_DEVICE_STALE",
  deviceBusy: "QCM_DEVICE_BUSY",
  deviceNotQuadStick: "QCM_DEVICE_NOT_QUADSTICK",
  deviceRemovedDuringWrite: "QCM_DEVICE_REMOVED_DURING_WRITE",
  storagePermissionDenied: "QCM_STORAGE_PERMISSION_DENIED",
  storageReadOnly: "QCM_STORAGE_READ_ONLY",
  storageFull: "QCM_STORAGE_FULL",
  storageBackupFailed: "QCM_STORAGE_BACKUP_FAILED",
  storageVerifyFailed: "QCM_STORAGE_VERIFY_FAILED",
  storageRestoreFailed: "QCM_STORAGE_RESTORE_FAILED",
  storageSwapFailed: "QCM_STORAGE_SWAP_FAILED",
  storageNameRejected: "QCM_STORAGE_NAME_REJECTED",
  storageProtectedFile: "QCM_STORAGE_PROTECTED_FILE",
  storageFileNotFound: "QCM_STORAGE_FILE_NOT_FOUND",
  storageIo: "QCM_STORAGE_IO",
  confirmationRequired: "QCM_CONFIRMATION_REQUIRED",
  confirmationUnknown: "QCM_CONFIRMATION_UNKNOWN",
  confirmationExpired: "QCM_CONFIRMATION_EXPIRED",
  confirmationMismatch: "QCM_CONFIRMATION_MISMATCH",
  confirmationAlreadyUsed: "QCM_CONFIRMATION_ALREADY_USED",
  requestMalformed: "QCM_REQUEST_MALFORMED",
  requestTooLarge: "QCM_REQUEST_TOO_LARGE",
  requestOutOfRange: "QCM_REQUEST_OUT_OF_RANGE",
  cancelled: "QCM_CANCELLED",
  internal: "QCM_INTERNAL",
} as const;

export interface Subscription {
  dispose(): void;
}
