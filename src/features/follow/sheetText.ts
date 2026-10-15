import type { MessageKey } from "../../i18n";
import type { ProfileChange, SheetRefusal } from "../../platform";

type Translate = (key: MessageKey, values?: readonly unknown[]) => string;

/** `MainWindow.NoProfileTab` and the import refusals, in the shipped words. */
export function sheetRefusalText(refusal: SheetRefusal, t: Translate): string {
  switch (refusal.kind) {
    case "not_a_link":
      return t("Main_ThatDoesNotLookLike");
    case "web_page":
      return t("Main_GoogleReturnedAWebPage");
    case "unreadable":
      return t("Main_CouldNotReadThatSpreadsheet");
    case "timed_out":
      return t("Main_CouldNotDownloadTheSheet", [t("Main_ConnectionTimedOut")]);
    case "download_failed":
      return t("Main_CouldNotDownloadTheSheet", [refusal.detail]);
    case "no_profile_tab": {
      const start = t("Main_AProfileTabStartsWith");
      if (refusal.skipped.length > 0) {
        const naming =
          refusal.skipped.length === 1
            ? t("Main_TheTabSkipped0Name", [refusal.skipped[0]])
            : `${t("Main_TheseTabsWerePassedOver")}${refusal.skipped.map((name) => `"${name}"`).join(", ")}.`;
        return t("Main_NoTabInThatSpreadsheet", [naming, start]);
      }
      if (refusal.empty) return t("Main_ThatSpreadsheetCameBackEmpty");
      return refusal.a1.length === 0
        ? t("Main_ThatSpreadsheetHasNoProfile", [start])
        : t("Main_ThatSpreadsheetHasNoProfile2", [refusal.a1, start]);
    }
  }
}

/** `ProfileChanges.Between` lines. */
export function changeText(change: ProfileChange, t: Translate): string {
  switch (change.kind) {
    case "local_unreadable":
      return t("Follow_LocalUnreadable");
    case "mode_added":
      return t("Follow_ModeAdded", [change.mode]);
    case "mode_removed":
      return t("Follow_ModeRemoved", [change.mode]);
    case "mode_changed":
      return t("Follow_ModeChanged", [change.mode, change.added, change.removed]);
    case "other_changed":
      return t("Follow_OtherChanged");
  }
}
