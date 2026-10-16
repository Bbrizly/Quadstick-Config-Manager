import type { WorkbookLimitation } from "./workbookContracts";
/** Pasted Sheets links and Get the latest (`MainWindow.Follow.cs`). */
import type { EditorSnapshot } from "./contracts";
import type { WorkbookImportReview } from "./workbookContracts";

/** Why a link could not become a profile. Words are the frontend's. */
export type SheetRefusal =
  | { readonly kind: "not_a_link" }
  | { readonly kind: "web_page" }
  | { readonly kind: "unreadable" }
  | { readonly kind: "timed_out" }
  | { readonly kind: "download_failed"; readonly detail: string }
  | {
      readonly kind: "no_profile_tab";
      readonly skipped: readonly string[];
      readonly empty: boolean;
      readonly a1: string;
    };

export type SheetsImport =
  | { readonly kind: "review"; readonly review: WorkbookImportReview }
  | { readonly kind: "refused"; readonly refusal: SheetRefusal };

export interface FollowState {
  readonly follows: boolean;
}

export type ProfileChange =
  | { readonly kind: "local_unreadable" }
  | { readonly kind: "mode_added"; readonly mode: string }
  | { readonly kind: "mode_removed"; readonly mode: string }
  | { readonly kind: "mode_changed"; readonly mode: string; readonly added: number; readonly removed: number }
  | { readonly kind: "other_changed" };

export type LatestCheck =
  | { readonly kind: "not_followed" }
  | { readonly kind: "unsaved" }
  | { readonly kind: "busy" }
  | { readonly kind: "refused"; readonly refusal: SheetRefusal }
  | { readonly kind: "up_to_date" }
  | {
      readonly kind: "changes";
      readonly planId: string;
      readonly changes: readonly ProfileChange[];
      readonly singleTab: boolean;
      /** A workbook too large to read whole (`ConfirmLatestAsync` limitation). */
      readonly limitation?: WorkbookLimitation | null;
    };

export type LatestReplace =
  | { readonly kind: "replaced"; readonly snapshot: EditorSnapshot }
  | { readonly kind: "changed_meanwhile" };
