/**
 * The shapes that cross the native boundary.
 *
 * Every type here mirrors a Rust DTO by hand. That is deliberate: generating
 * them would tie the wire to whatever the core's types happen to look like
 * today, and the core's types are the compatibility surface against the frozen
 * C# implementation. Two hand-written halves that a contract test compares is
 * the cheaper mistake.
 *
 * Nothing here carries a path. A profile is named by an opaque session id and a
 * file by a display name, in both directions.
 */

/** Where an open profile came from, in the form a window may print. */
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

/**
 * One section of the profile, listed the way the firmware reads it.
 *
 * `number` is the position the device counts to, not a name. Two modes may
 * share a name and that is normal, so the number is what a window shows beside
 * one. It is null for the sheets the firmware reads on their own keyword and
 * never numbers.
 */
export interface Mode {
  readonly index: number;
  readonly number: number | null;
  readonly kind: "mode" | "preferences" | "infrared";
  readonly name: string;
  readonly channel: string;
  readonly startRow: number;
  readonly bindingCount: number;
}

/**
 * Everything a window needs to draw one open profile.
 *
 * A read-only mirror of native truth, tagged with the revision it was taken at.
 * Never serialize this back to CSV, and never work out `dirty` by comparing
 * objects: both are the native side's job.
 */
export interface EditorSnapshot {
  readonly sessionId: string;
  readonly revision: number;
  readonly dirty: boolean;
  readonly canUndo: boolean;
  readonly source: ProfileSource;
  /** The name Save writes to. `null` means Save has to become Save As. */
  readonly saveTarget: string | null;
  readonly title: string;
  /** The raw grid, which is the canonical state, odd columns and all. */
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

/** What the caller wants done about unsaved work. There is no fourth answer. */
export type CloseDisposition = "if_clean" | "save" | "discard";

export type CloseOutcome =
  | { readonly kind: "closed" }
  | { readonly kind: "savedAndClosed"; readonly receipt: SaveReceipt }
  /** Still open, still dirty. The window has to ask the user. */
  | { readonly kind: "keptOpenUnsavedChanges" };

/** The typed editor operations. The `op` tag is the native enum's own name. */
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

/** The four sizes the app offers. Anything else is refused, never rounded. */
export const INTERFACE_SCALES = [100, 125, 150, 200] as const;
export type InterfaceScale = (typeof INTERFACE_SCALES)[number];

export interface AppSettings {
  readonly revision: number;
  readonly model: ModelChoice;
  readonly theme: ThemeChoice;
  /** `system` follows the machine; anything else is a language tag. */
  readonly language: string;
  readonly interfaceScalePercent: number;
  readonly reduceMotion: boolean;
  readonly rememberWindow: boolean;
  readonly deviceCards: boolean;
  readonly pickerGrouping: PickerGrouping;
  readonly tutorialSeen: boolean;
}

/** A change to some settings and not the others. */
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

/**
 * Which parts of the app are wired, not which parts are planned.
 *
 * A window uses these to decide what to show, so a flag that is true before its
 * commands exist is a button that does nothing.
 */
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

/** The single next thing to offer. Every failure answers with one of these. */
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

/**
 * The only failure shape a window ever sees.
 *
 * `code` is stable and is what recovery UI switches on. `message` is fallback
 * English for a surface with nothing localized yet; it never carries a path.
 */
export interface QcmErrorPayload {
  readonly code: string;
  readonly message: string;
  readonly recoverable: boolean;
  readonly action: { readonly kind: RecoveryAction } | null;
  readonly operationId: string | null;
  /** What is on the device now, where the failure can prove it. */
  readonly targetState: "unchanged" | "missing" | "replaced" | "restored" | "uncertain" | null;
  readonly backup: string | null;
}

/**
 * The codes this task's commands can produce.
 *
 * Not the whole list: device, confirmation and network codes arrive with the
 * commands that raise them. Named so a `switch` over recovery UI is checked.
 */
export const ERROR_CODES = {
  configUnreadable: "QCM_CONFIG_PARSE_UNREADABLE",
  profileUnknownSession: "QCM_PROFILE_UNKNOWN_SESSION",
  profileRevisionConflict: "QCM_PROFILE_REVISION_CONFLICT",
  profileNothingToUndo: "QCM_PROFILE_NOTHING_TO_UNDO",
  profileOperationRejected: "QCM_PROFILE_OPERATION_REJECTED",
  profileNeedsSaveTarget: "QCM_PROFILE_NEEDS_SAVE_TARGET",
  profileSaveTargetOnDevice: "QCM_PROFILE_SAVE_TARGET_ON_DEVICE",
  storageFull: "QCM_STORAGE_FULL",
  storageIo: "QCM_STORAGE_IO",
  requestMalformed: "QCM_REQUEST_MALFORMED",
  requestTooLarge: "QCM_REQUEST_TOO_LARGE",
  requestOutOfRange: "QCM_REQUEST_OUT_OF_RANGE",
  cancelled: "QCM_CANCELLED",
  internal: "QCM_INTERNAL",
} as const;

/**
 * A subscription handle.
 *
 * There are no events or streams yet: TASK-034 adds the low-rate events and the
 * live-input Channel. The type is here so the methods that return one cannot be
 * added without an owner for the teardown, which is what a React effect running
 * twice in StrictMode needs.
 */
export interface Subscription {
  dispose(): void;
}
