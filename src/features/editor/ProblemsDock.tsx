import { useLayoutEffect, useRef, useState } from "react";

import { Icon, type IconName } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import type { Issue } from "../../platform";

export type StatusKind = "info" | "ready" | "warning" | "error";

export interface EditorStatus {
  readonly kind: StatusKind;
  readonly text: string;
}

const STATUS_ICON: Record<StatusKind, IconName> = {
  info: "chevron",
  ready: "check",
  warning: "warning",
  error: "error",
};

/** `MainWindow.StatusChip`: glyph and text in the state's colour, never colour alone. */
export function StatusChip({ status }: { readonly status: EditorStatus }) {
  if (status.text === "") return null;
  return (
    <span className={`status-chip status-${status.kind}`}>
      <Icon name={STATUS_ICON[status.kind]} size={18} />
      <span>{status.text}</span>
    </span>
  );
}

/** `RefreshIssues`: only errors block install, so warnings alone say it still installs. */
export function useValidationStatus(issues: readonly Issue[]): EditorStatus {
  const { t, plural } = useI18n();
  const errors = issues.filter((issue) => issue.severity === "error").length;
  const warns = issues.length - errors;
  if (errors + warns === 0) return { kind: "ready", text: t("Main_NoProblemsReadyToSave") };
  if (errors === 0) {
    return { kind: "warning", text: plural("Count_Warning", warns, [warns]) + t("Main_TheDeviceSkipsThoseRows") };
  }
  return {
    kind: "error",
    text: `${plural("Count_Error", errors, [errors])}, ${plural("Count_Warning", warns, [warns])}${t("Main_ErrorsBlockInstalling")}`,
  };
}

export interface ProblemsDockProps {
  readonly issues: readonly Issue[];
  readonly status: EditorStatus;
  readonly onFocusIssue: (issue: Issue) => void;
  readonly onStatus: (status: EditorStatus) => void;
  readonly onMoveToNotes?: (issue: Issue) => void;
}

function issueText(issue: Issue, severity: string): string {
  return `${severity} ${issue.cell}: ${issue.message} (${issue.fix})`;
}

/** Lets a stacked workspace pad its scrolling by the pinned bar's height. */
function publishDockHeight(element: HTMLElement | null): void {
  const workspace = element?.closest<HTMLElement>(".editor-workspace");
  workspace?.style.setProperty("--qcm-dock-h", `${String(element?.offsetHeight ?? 0)}px`);
}

/**
 * The bottom problems bar (`ProblemsDock`): one slim line with the count, the
 * full list expanding above it only when asked, and the status line beside it.
 * Picking a problem copies it and jumps to the cell.
 */
export function ProblemsDock({ issues, status, onFocusIssue, onStatus, onMoveToNotes }: ProblemsDockProps) {
  const { t, plural } = useI18n();
  const [expanded, setExpanded] = useState(false);
  const errors = issues.filter((issue) => issue.severity === "error").length;
  const warns = issues.length - errors;
  const clean = errors + warns === 0;
  const parts: string[] = [];
  if (errors > 0) parts.push(plural("Count_Error", errors, [errors]));
  if (warns > 0) parts.push(plural("Count_Warning", warns, [warns]));
  const label = clean
    ? expanded
      ? t("Main_NoProblemsClickToHide")
      : t("Main_NoProblems")
    : parts.join(", ") + (expanded ? t("Main_ClickToHide") : t("Main_ClickToView"));
  const tone = clean ? "ready" : errors > 0 ? "error" : "warning";
  const ordered = issues.toSorted(
    (a, b) => (a.severity === "error" ? 0 : 1) - (b.severity === "error" ? 0 : 1),
  );

  const pick = (issue: Issue, text: string): void => {
    void navigator.clipboard?.writeText(text).then(
      () => onStatus({ kind: "info", text: t("Main_ProblemCopiedToTheClipboard") }),
      () => undefined,
    );
    onFocusIssue(issue);
  };

  // Where the bar is pinned (stacked layouts) a focused control must scroll
  // clear of it, open or closed, so the workspace pads by its real height.
  const dock = useRef<HTMLDivElement>(null);
  // After every render (opening and closing re-render), and on any resize.
  useLayoutEffect(() => publishDockHeight(dock.current));
  useLayoutEffect(() => {
    const element = dock.current;
    if (element === null || typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(() => publishDockHeight(element));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return (
    <div className="problems-dock" ref={dock}>
      {expanded ? (
        <div className="problems-list" role="list" aria-label={t("Shell_ListOfValidationProblemsSelect")}>
          {ordered.length === 0 ? (
            <p className="problems-none" role="listitem">{t("Main_NoProblemsFound")}</p>
          ) : (
            ordered.map((issue, index) => {
              const severity = t(issue.severity === "error" ? "Rewrite_SeverityError" : "Rewrite_SeverityWarning");
              const text = issueText(issue, severity);
              return (
                <div role="listitem" className="problems-item" key={`${issue.cell}-${issue.kind}-${String(index)}`}>
                  <button
                    type="button"
                    className={`issue-link issue-${issue.severity}`}
                    onClick={() => pick(issue, text)}
                  >
                    {text}
                  </button>
                  {issue.kind === "unknown_input" && onMoveToNotes !== undefined ? (
                    <button
                      type="button"
                      className="quiet issue-fix"
                      aria-label={t("Main_MoveTheTextInCell", [issue.cell])}
                      onClick={() => onMoveToNotes(issue)}
                    >
                      {t("Main_MoveToNotes")}
                    </button>
                  ) : null}
                </div>
              );
            })
          )}
        </div>
      ) : null}
      <div className="problems-bar">
        <button
          type="button"
          className={`quiet problems-toggle status-${tone}`}
          aria-expanded={expanded}
          aria-label={t(expanded ? "Main_HidesTheProblems" : "Main_ShowsTheProblems", [label])}
          onClick={() => setExpanded((open) => !open)}
        >
          <Icon name={clean ? "check" : errors > 0 ? "error" : "warning"} size={18} />
          <span>{label}</span>
        </button>
        <div className="problems-status" role="status" aria-live={status.kind === "error" ? "assertive" : "polite"}>
          <StatusChip key={`${status.kind}:${status.text}`} status={status} />
        </div>
        {expanded && errors > 0 ? (
          <button
            type="button"
            className="quiet"
            aria-label={t("Shell_JumpToAndFocusThe")}
            onClick={() => {
              const first = issues.find((issue) => issue.severity === "error");
              if (first === undefined) onStatus({ kind: "ready", text: t("Main_NoErrorsToFix") });
              else onFocusIssue(first);
            }}
          >
            {t("Shell_FixFirstProblem")}
          </button>
        ) : null}
      </div>
    </div>
  );
}
