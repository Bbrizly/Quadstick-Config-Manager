import type { WorkbookImportReview } from "./workbookContracts";

export type DriveBackupOutcome =
  | { readonly kind: "pushed"; readonly backupDirty: boolean }
  | { readonly kind: "conflict"; readonly resolutionId: string }
  | { readonly kind: "missing"; readonly resolutionId: string }
  | { readonly kind: "disabled" };

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
}

export interface DriveShare {
  readonly url: string;
}
