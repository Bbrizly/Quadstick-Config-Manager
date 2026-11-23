import {
  memo,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type KeyboardEvent,
  type MouseEvent,
} from "react";

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

/**
 * Lets a stacked workspace pad its scrolling by the pinned bar's height, and
 * gives the open list the workspace's height and the bar's own, so it can
 * leave room for the control that has focus (B-122).
 */
function publishDockHeight(element: HTMLElement | null): void {
  const workspace = element?.closest<HTMLElement>(".editor-workspace");
  if (workspace === null || workspace === undefined || element === null) return;
  const list = element.querySelector<HTMLElement>(":scope > .problems-list");
  const gap = list === null ? 0 : Number.parseFloat(getComputedStyle(element).rowGap) || 0;
  const bar = element.offsetHeight - (list?.offsetHeight ?? 0) - gap;
  workspace.style.setProperty("--qcm-dock-h", `${String(element.offsetHeight)}px`);
  workspace.style.setProperty("--qcm-dock-bar-h", `${String(bar)}px`);
  workspace.style.setProperty("--qcm-workspace-h", `${String(workspace.clientHeight)}px`);
}

/**
 * The picked row's index, outside React state: a big file has 2320 rows, and
 * a pick in state rebuilt every one of them on each arrow press (10ms a key in
 * a dev build). Each row watches only whether it is the pick.
 */
interface PickStore {
  readonly get: () => number;
  readonly set: (index: number) => void;
  readonly subscribe: (listener: () => void) => () => void;
}

function createPickStore(): PickStore {
  let value = 0;
  const listeners = new Set<() => void>();
  return {
    get: () => value,
    set: (index) => {
      if (index === value) return;
      value = index;
      for (const listener of listeners) listener();
    },
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

interface ProblemRowData {
  readonly issue: Issue;
  readonly text: string;
  readonly fixable: boolean;
}

interface ProblemRowProps {
  readonly index: number;
  readonly text: string;
  readonly severity: Issue["severity"];
  readonly hint: string;
  readonly store: PickStore;
}

const ProblemRow = memo(function ProblemRow({ index, text, severity, hint, store }: ProblemRowProps) {
  const selected = useSyncExternalStore(store.subscribe, () => store.get() === index);
  return (
    <div
      role="option"
      className={`problems-item issue-${severity}`}
      data-index={index}
      aria-selected={selected}
      tabIndex={selected ? 0 : -1}
    >
      {/* Always there and shown by CSS: adding the glyph cost WebKit a 60ms
          relayout of every row on each arrow press. */}
      <span className="problems-item-mark">
        <Icon name="check" size={18} />
      </span>
      <span className="problems-item-text">
        <span className="issue-link">{text}</span>
        {hint === "" ? null : <span className="problems-item-hint">{hint}</span>}
      </span>
    </div>
  );
});

const ProblemRows = memo(function ProblemRows({
  rows,
  hint,
  store,
}: {
  readonly rows: readonly ProblemRowData[];
  readonly hint: string;
  readonly store: PickStore;
}) {
  return rows.map((row, index) => (
    <ProblemRow
      key={`${row.issue.cell}-${row.issue.kind}-${String(index)}`}
      index={index}
      text={row.text}
      severity={row.issue.severity}
      hint={row.fixable ? hint : ""}
      store={store}
    />
  ));
});

/**
 * One fix button for the picked row, outside the list, so the list stays one
 * Tab stop. It stays in place, greyed, on a row it cannot fix, so the bar does
 * not jump while the arrows move.
 */
function MoveToNotesButton({
  rows,
  store,
  onMoveToNotes,
}: {
  readonly rows: readonly ProblemRowData[];
  readonly store: PickStore;
  readonly onMoveToNotes: (issue: Issue) => void;
}) {
  const { t } = useI18n();
  const index = useSyncExternalStore(store.subscribe, store.get);
  const picked = rows[Math.min(index, rows.length - 1)];
  if (picked === undefined) return null;
  return (
    <button
      type="button"
      className="quiet issue-fix"
      aria-disabled={!picked.fixable}
      aria-label={t("Main_MoveTheTextInCell", [picked.issue.cell])}
      onClick={() => {
        if (picked.fixable) onMoveToNotes(picked.issue);
      }}
    >
      {t("Main_MoveToNotes")}
    </button>
  );
}

/** Moves the list, never the workspace: a pinned dock would jump on every arrow. */
export function showRow(list: HTMLElement, row: HTMLElement): void {
  row.focus({ preventScroll: true });
  const box = list.getBoundingClientRect();
  const top = row.getBoundingClientRect().top - box.top - list.clientTop;
  const bottom = top + row.offsetHeight - list.clientHeight;
  if (top < 0) list.scrollTop += top;
  else if (bottom > 0) list.scrollTop += bottom;
}

/**
 * The bottom problems bar (`ProblemsDock`): one slim line with the count, the
 * full list expanding above it only when asked, and the status line beside it.
 * Picking a problem copies it and jumps to the cell.
 */
export function ProblemsDock({ issues, status, onFocusIssue, onStatus, onMoveToNotes }: ProblemsDockProps) {
  const { t, plural } = useI18n();
  const [expanded, setExpanded] = useState(false);
  const [store] = useState(createPickStore);
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
  const canMove = onMoveToNotes !== undefined;
  const rows = useMemo(
    (): readonly ProblemRowData[] =>
      issues
        .toSorted((a, b) => (a.severity === "error" ? 0 : 1) - (b.severity === "error" ? 0 : 1))
        .map((issue) => ({
          issue,
          text: issueText(issue, t(issue.severity === "error" ? "Rewrite_SeverityError" : "Rewrite_SeverityWarning")),
          fixable: canMove && issue.kind === "unknown_input",
        })),
    [issues, t, canMove],
  );
  const anyFixable = rows.some((row) => row.fixable);
  const fixHint = t("Rewrite_ProblemFixBelow");
  // A fixed problem leaves the list; the pick stays at its place, on the next one.
  useLayoutEffect(() => {
    if (store.get() > rows.length - 1) store.set(Math.max(0, rows.length - 1));
  }, [rows, store]);

  const pick = (issue: Issue, text: string): void => {
    void navigator.clipboard?.writeText(text).then(
      () => onStatus({ kind: "info", text: t("Main_ProblemCopiedToTheClipboard") }),
      () => undefined,
    );
    onFocusIssue(issue);
  };

  const list = useRef<HTMLDivElement>(null);
  const toggle = useRef<HTMLButtonElement>(null);

  // Fixing the last fixable problem takes the button away under the
  // keyboard; focus goes back to the pick, or to the toggle when none is left.
  const lastFix = useRef(false);
  useLayoutEffect(() => {
    if (anyFixable || !lastFix.current) return;
    lastFix.current = false;
    if (document.activeElement !== null && document.activeElement !== document.body) return;
    const box = list.current;
    const at = String(Math.min(store.get(), rows.length - 1));
    const row = box?.querySelector<HTMLElement>(`[data-index="${at}"]`);
    if (box !== null && row !== null && row !== undefined) showRow(box, row);
    else toggle.current?.focus();
  }, [anyFixable, rows, store]);

  const onListKey = (event: KeyboardEvent<HTMLDivElement>): void => {
    const box = list.current;
    const current = Math.min(store.get(), rows.length - 1);
    const picked = rows[current];
    if (box === null || picked === undefined) return;
    if (event.key === "Enter") {
      event.preventDefault();
      pick(picked.issue, picked.text);
      return;
    }
    const first = box.querySelector<HTMLElement>('[role="option"]');
    const page = first !== null && first.offsetHeight > 0
      ? Math.max(1, Math.floor(box.clientHeight / first.offsetHeight) - 1)
      : 10;
    const moves: Record<string, number> = {
      ArrowDown: current + 1,
      ArrowUp: current - 1,
      Home: 0,
      End: rows.length - 1,
      PageDown: current + page,
      PageUp: current - page,
    };
    const move = moves[event.key];
    if (move === undefined) return;
    event.preventDefault();
    const target = Math.max(0, Math.min(rows.length - 1, move));
    const row = box.querySelector<HTMLElement>(`[data-index="${String(target)}"]`);
    if (row !== null) showRow(box, row);
    store.set(target);
  };

  const onListClick = (event: MouseEvent<HTMLDivElement>): void => {
    const row = (event.target as HTMLElement).closest<HTMLElement>('[role="option"]');
    const index = Number(row?.dataset["index"]);
    const hit = rows[index];
    if (hit === undefined) return;
    store.set(index);
    pick(hit.issue, hit.text);
  };

  // Where the bar is pinned (stacked layouts) a focused control must scroll
  // clear of it, open or closed, so the workspace pads by its real height.
  const dock = useRef<HTMLDivElement>(null);
  // After every render (opening and closing re-render), and on any resize.
  // Moving the pick does not render the dock.
  useLayoutEffect(() => publishDockHeight(dock.current));
  useLayoutEffect(() => {
    const element = dock.current;
    if (element === null || typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(() => publishDockHeight(element));
    observer.observe(element);
    const workspace = element.closest(".editor-workspace");
    if (workspace !== null) observer.observe(workspace);
    return () => observer.disconnect();
  }, []);

  // Escape anywhere in the open dock closes the list, as the toggle would.
  useEffect(() => {
    const element = dock.current;
    if (!expanded || element === null) return undefined;
    const onKey = (event: globalThis.KeyboardEvent): void => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      setExpanded(false);
      toggle.current?.focus();
    };
    element.addEventListener("keydown", onKey);
    return () => element.removeEventListener("keydown", onKey);
  }, [expanded]);

  return (
    <div className="problems-dock" ref={dock}>
      {expanded ? (
        rows.length === 0 ? (
          <div className="problems-list">
            <p className="problems-none">{t("Main_NoProblemsFound")}</p>
          </div>
        ) : (
          // One Tab stop, as Avalonia's ListBox: the arrows walk it, Enter
          // picks, Escape closes it.
          <div
            className="problems-list"
            ref={list}
            role="listbox"
            aria-label={t("Rewrite_ProblemsListName")}
            tabIndex={-1}
            onKeyDown={onListKey}
            onClick={onListClick}
          >
            <ProblemRows rows={rows} hint={fixHint} store={store} />
          </div>
        )
      ) : null}
      <div className="problems-bar">
        <button
          type="button"
          ref={toggle}
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
        <div className="problems-actions">
          {expanded && anyFixable && onMoveToNotes !== undefined ? (
            <MoveToNotesButton
              rows={rows}
              store={store}
              onMoveToNotes={(issue) => {
                lastFix.current = rows.filter((row) => row.fixable).length === 1;
                onMoveToNotes(issue);
              }}
            />
          ) : null}
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
    </div>
  );
}
