export interface WorkbookMode {
  readonly number: number;
  readonly name: string;
  readonly kind: "mode" | "preferences" | "infrared";
  readonly bindingCount: number;
}

export interface WorkbookSkippedTab {
  readonly index: number;
  readonly name: string;
  readonly kind: "unreadable_a1" | "helper";
  readonly rowCount: number;
  readonly repairable: boolean;
  readonly preview: readonly (readonly string[])[];
}

export type WorkbookLimitation =
  | { readonly kind: "sheet_count"; readonly max: number }
  | { readonly kind: "sheet_rows"; readonly tab: string; readonly max: number }
  | {
      readonly kind: "workbook_rows";
      readonly max: number;
      readonly remaining_tabs: number | null;
    };

export interface WorkbookTabRename {
  readonly mode_number: number;
  readonly tab_name: string;
  readonly cell_c1: string;
}

export interface WorkbookImportReview {
  readonly importId: string;
  readonly name: string;
  readonly modes: readonly WorkbookMode[];
  readonly skipped: readonly WorkbookSkippedTab[];
  readonly limitation: WorkbookLimitation | null;
  readonly renamed: readonly WorkbookTabRename[];
  readonly errorCount: number;
  readonly warningCount: number;
}

export interface WorkbookExportReceipt {
  readonly name: string;
  readonly bytes: number;
}
