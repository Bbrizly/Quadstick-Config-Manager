import { useState, type ReactNode } from "react";

import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import { KNOWN_OUTPUTS, type EditorOp, type Issue } from "../../platform";
import { tokenLabel } from "../visualizer/deviceSummary";
import { inputLabel, type LabelStyle } from "./labels";
import { OutputPrompt, promptSrc } from "../visualizer/OutputPrompt";
import { DuplicateMark } from "./DuplicateMark";
import { NO_DUPLICATES, type DuplicateCounts } from "./duplicates";

/** Column K (`ProfileFile.NoteColumn`). */
const NOTE_COLUMN = 10;
const FIRST_INPUT = 2;
const LAST_INPUT = 9;
/** The firmware's function 0, what a blank cell means. A token, not prose. */
const NORMAL_TOKEN = "normal";

export type { LabelStyle } from "./labels";

export interface GridRow {
  readonly row: number;
  readonly cells: readonly string[];
}

function columnLetter(column: number): string {
  return String.fromCharCode(65 + column);
}

/**
 * One editable cell in its column's tint. It reads as the word (or the
 * controller art) until it is picked; then it is the file's own text, and
 * leaving it writes that text back through `set_cell`.
 */
function TokenCell({
  value,
  display,
  name,
  tint,
  issue,
  disabled,
  placeholder,
  list,
  startEditing = false,
  onCommit,
  extra,
}: {
  readonly value: string;
  readonly display: ReactNode;
  readonly name: string;
  readonly tint: "output" | "function" | "input";
  readonly issue: Issue | undefined;
  readonly disabled: boolean;
  readonly placeholder: string;
  readonly list?: string;
  readonly startEditing?: boolean;
  readonly onCommit: (value: string) => void;
  readonly extra?: ReactNode;
}) {
  const [editing, setEditing] = useState(startEditing);
  const edge = issue === undefined ? "" : ` issue-${issue.severity}`;
  const spoken = issue === undefined ? name : `${issue.severity === "error" ? "Error" : "Warning"}: ${name}`;
  return (
    <span className={`grid-cell tint-${tint}${edge}`}>
      {editing ? (
        <input
          defaultValue={value}
          disabled={disabled}
          aria-label={spoken}
          placeholder={placeholder}
          list={list}
          // A picked cell takes the keyboard at once, like Avalonia's picker.
          ref={(element) => element?.focus()}
          onBlur={(event) => {
            setEditing(false);
            const next = event.currentTarget.value;
            if (next !== value) onCommit(next);
          }}
          onKeyDown={(event) => {
            if (event.key === "Enter") event.currentTarget.blur();
            if (event.key === "Escape") {
              event.currentTarget.value = value;
              event.currentTarget.blur();
            }
          }}
        />
      ) : (
        <button
          type="button"
          className="grid-cell-face"
          disabled={disabled}
          aria-label={value === "" ? spoken : `${spoken}: ${value}`}
          onClick={() => setEditing(true)}
        >
          {value === "" ? <span className="muted">{placeholder}</span> : display}
        </button>
      )}
      {extra}
    </span>
  );
}

export interface RowsGridProps {
  readonly rows: readonly GridRow[];
  readonly issues: readonly Issue[];
  readonly counts?: DuplicateCounts;
  readonly labelStyle: LabelStyle;
  readonly disabled: boolean;
  readonly liveRows: ReadonlySet<number>;
  readonly selecting: boolean;
  readonly selectedRows: ReadonlySet<number>;
  readonly onToggleRow: (row: number) => void;
  readonly onApply: (ops: readonly EditorOp[]) => void;
  readonly onDeleteRow: (row: number) => void;
}

/**
 * Rows view (`RebuildRows`/`BindingRow`): the sheet as numbered lines on one
 * set of columns, so a cell always sits under its header. Numbers are the
 * spreadsheet's own rows. Each line: output, function, inputs (stacked down,
 * never sideways), add input and delete, note, up, down.
 */
export function RowsGrid({
  rows,
  issues,
  counts = NO_DUPLICATES,
  labelStyle,
  disabled,
  liveRows,
  selecting,
  selectedRows,
  onToggleRow,
  onApply,
  onDeleteRow,
}: RowsGridProps) {
  const { t } = useI18n();
  // An "add another input" press opens an empty cell; nothing is written
  // until a value is.
  const [openInput, setOpenInput] = useState<{ row: number; col: number } | null>(null);
  const issueAt = new Map(issues.map((issue) => [issue.cell.trim().toUpperCase(), issue]));
  const set = (row: number, col: number, value: string): void => onApply([{ op: "set_cell", row, col, value }]);

  return (
    <div className="rows-grid" role="table" aria-label={t("Shell_Rows")}>
      <div className="rows-grid-line rows-grid-head" role="row">
        <span role="columnheader" className="row-number" aria-hidden="true" />
        <span role="columnheader" className="tint-swatch tint-output">{t("Main_OutputGameButton")}</span>
        <span role="columnheader" className="tint-swatch tint-function">{t("Main_FunctionBehavior")}</span>
        <span role="columnheader" className="tint-swatch tint-input">{t("Main_InputsSipsPuffsJoystick")}</span>
      </div>
      {rows.length === 0 ? <p className="muted rows-empty">{t("Main_NoBindingsYetClickAdd")}</p> : null}
      {rows.map((binding, index) => {
        const { row, cells } = binding;
        const output = (cells[0] ?? "").trim();
        const action = (cells[11] ?? "").trim();
        const fn = (cells[1] ?? "").trim();
        const inputCols: number[] = [];
        for (let col = FIRST_INPUT; col <= LAST_INPUT; col += 1) {
          if ((cells[col] ?? "").trim() !== "") inputCols.push(col);
        }
        const firstFree = [2, 3, 4, 5, 6, 7, 8, 9].find((col) => !inputCols.includes(col)) ?? null;
        const shownInputs = inputCols.length === 0 ? [FIRST_INPUT] : inputCols;
        const extraOpen = openInput?.row === row && !shownInputs.includes(openInput.col) ? openInput.col : null;
        const outputFace =
          // Art only for a name the device knows (`VisualFor`): an unknown
          // output such as "cross" reads as its word, the way Avalonia shows it.
          labelStyle !== 0 && action === "" && KNOWN_OUTPUTS.has(output) && promptSrc(output) !== null ? (
            <>
              <OutputPrompt token={output} label={tokenLabel(output)} size={30} />
              <span className="visually-hidden">{tokenLabel(output)}</span>
            </>
          ) : (
            <span>{action !== "" ? action : labelStyle === 0 ? output : tokenLabel(output)}</span>
          );
        const lineClass = [
          "rows-grid-line",
          liveRows.has(row) ? "binding-row-live" : "",
          selectedRows.has(row) ? "binding-row-selected" : "",
        ]
          .filter(Boolean)
          .join(" ");
        return (
          <div className={lineClass} role="row" key={row} data-binding-row={row} data-testid={`binding-row-${String(row)}`} tabIndex={-1}>
            <span role="cell" className="row-number">
              {selecting ? (
                <button
                  type="button"
                  className="row-select"
                  aria-pressed={selectedRows.has(row)}
                  aria-label={`${t("Shell_Rows")} ${String(row)}`}
                  onClick={() => onToggleRow(row)}
                >
                  {row}
                </button>
              ) : (
                row
              )}
              {liveRows.has(row) ? <span className="row-live-pip" role="img" aria-label={t("Main_SendingNow")} /> : null}
            </span>
            <span role="cell" className="cell-slot">
              <TokenCell
                key={`${row}-A-${output}-${action}`}
                value={output}
                display={outputFace}
                name={t("Main_OutputForRowBRow", [row])}
                tint="output"
                issue={issueAt.get(`A${String(row)}`)}
                disabled={disabled}
                placeholder={t("Main_AnOutput")}
                list="qcm-row-outputs"
                onCommit={(value) => set(row, 0, value)}
                extra={<DuplicateMark count={counts.output(output)} />}
              />
            </span>
            <span role="cell" className="cell-slot">
              <TokenCell
                key={`${row}-B-${fn}`}
                value={fn}
                display={<span>{fn}</span>}
                name={t("Main_FunctionForRowBRow", [row, ""]).trim()}
                tint="function"
                issue={issueAt.get(`B${String(row)}`)}
                disabled={disabled}
                // Blank is normal on the device (FORMAT.md: "An empty or
                // unrecognized function cell yields code 0 = normal").
                placeholder={NORMAL_TOKEN}
                list="qcm-row-functions"
                onCommit={(value) => set(row, 1, value)}
              />
            </span>
            <span role="cell" className="cell-slot input-stack">
              {[...shownInputs, ...(extraOpen === null ? [] : [extraOpen])].map((col, inputIndex) => {
                const token = (cells[col] ?? "").trim();
                const removable = inputCols.length > 1 && inputCols.includes(col);
                return (
                  <span className="input-line" key={col}>
                    <TokenCell
                      key={`${row}-${columnLetter(col)}-${token}`}
                      value={token}
                      display={<span>{inputLabel(token, labelStyle, t)}</span>}
                      name={t("Main_InputI1ForRow", [inputIndex + 1, row])}
                      tint="input"
                      issue={issueAt.get(`${columnLetter(col)}${String(row)}`)}
                      disabled={disabled}
                      placeholder={t("Main_AnInput")}
                      list="qcm-row-inputs"
                      startEditing={col === extraOpen}
                      onCommit={(value) => {
                        if (col === extraOpen) setOpenInput(null);
                        set(row, col, value);
                      }}
                      extra={<DuplicateMark count={counts.input(token)} />}
                    />
                    {removable ? (
                      <button
                        type="button"
                        className="icon round"
                        disabled={disabled}
                        aria-label={t("Main_RemoveInputI1From", [inputIndex + 1, row])}
                        onClick={() => set(row, col, "")}
                      >
                        <Icon name="delete" size={18} />
                      </button>
                    ) : null}
                  </span>
                );
              })}
            </span>
            <span role="cell" className={inputCols.length > 1 ? "row-buttons stacked" : "row-buttons"}>
              {shownInputs.length < 8 && firstFree !== null ? (
                <button
                  type="button"
                  className="icon round"
                  disabled={disabled}
                  title={t("Main_AddAnotherInput")}
                  aria-label={t("Main_AddAnotherInputToRow", [row])}
                  onClick={() => {
                    const free = [2, 3, 4, 5, 6, 7, 8, 9].find(
                      (col) => !inputCols.includes(col) && !shownInputs.includes(col),
                    );
                    if (free !== undefined) setOpenInput({ row, col: free });
                  }}
                >
                  <Icon name="add" size={16} />
                </button>
              ) : null}
              <button
                type="button"
                className="icon round danger"
                disabled={disabled}
                title={t("Main_DeleteThisWholeRow")}
                aria-label={t("Main_DeleteRowBRow", [row])}
                onClick={() => onDeleteRow(row)}
              >
                <Icon name="delete" size={16} />
              </button>
            </span>
            <span role="cell" className="cell-slot">
              <input
                key={`${row}-K-${cells[NOTE_COLUMN] ?? ""}`}
                className="note-box"
                defaultValue={cells[NOTE_COLUMN] ?? ""}
                disabled={disabled}
                placeholder={t("Main_Note")}
                aria-label={t("Main_NoteForRowBRow", [row])}
                onBlur={(event) => {
                  const value = event.currentTarget.value;
                  if (value !== (cells[NOTE_COLUMN] ?? "")) set(row, NOTE_COLUMN, value);
                }}
                onKeyDown={(event) => {
                  if (event.key === "Enter") event.currentTarget.blur();
                }}
              />
            </span>
            {([-1, 1] as const).map((delta) => {
              const target = rows[index + delta];
              return (
                <span role="cell" key={delta}>
                  <button
                    type="button"
                    className="icon round move"
                    disabled={disabled || target === undefined}
                    aria-label={t("Main_MoveRowBRowWord", [row, delta < 0 ? "up" : "down"])}
                    data-move={`${delta < 0 ? "up" : "down"}-${String(row)}`}
                    onClick={() => {
                      if (target !== undefined) onApply([{ op: "move_row", from: row, to: target.row }]);
                    }}
                  >
                    <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
                      <path fill="currentColor" d={delta < 0 ? "M7.4 15.4 12 10.8l4.6 4.6L18 14l-6-6-6 6z" : "M7.4 8.6 12 13.2l4.6-4.6L18 10l-6 6-6-6z"} />
                    </svg>
                  </button>
                </span>
              );
            })}
          </div>
        );
      })}
    </div>
  );
}
