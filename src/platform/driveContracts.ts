import type { WorkbookImportReview } from "./workbookContracts";

export type DriveBackupOutcome =
  | { readonly kind: "pushed"; readonly backupDirty: boolean }
  | { readonly kind: "conflict"; readonly resolutionId: string }
  | { readonly kind: "missing"; readonly resolutionId: string }
  | { readonly kind: "disabled" };

/** What the background push after a save, replace, or open came to. */
export type DriveBackupEvent =
  | { readonly kind: "pushed" }
  | { readonly kind: "pending" }
  | { readonly kind: "conflict"; readonly resolutionId: string }
  | { readonly kind: "missing"; readonly resolutionId: string };

export type DriveConflictChoice =
  | "replace_with_mine"
  | "keep_online"
  | "recreate"
  | "disable";

export type DriveResolution =
  | { readonly kind: "finished"; readonly result: DriveBackupOutcome }
  | { readonly kind: "review"; readonly review: WorkbookImportReview };

export interface DriveFile {
  readonly cloudRef: string;
  readonly name: string;
  readonly modifiedTime: string;
  /** Linked to a profile file that still exists here: dimmed, not importable. */
  readonly alreadyLinked?: boolean;
}

export type RestoreReason =
  | "already_exists"
  | "could_not_read_the_sheet"
  | "not_a_valid_profile"
  | "download_failed"
  | "could_not_write_the_file"
  | "could_not_save_the_link";

export interface RestoreItem {
  readonly name: string;
  readonly reason: RestoreReason;
}

/** `RestoreSummary`: what a bulk restore into the profile folder did. */
export interface RestoreSummary {
  readonly imported: readonly string[];
  readonly skipped: readonly RestoreItem[];
  readonly failed: readonly RestoreItem[];
}

export interface DriveShare {
  readonly url: string;
}
