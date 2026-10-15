import { useState } from "react";

import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import { tokenLabel } from "../visualizer/deviceSummary";
import { DuplicateMark } from "./DuplicateMark";
import { NO_DUPLICATES, type DuplicateCounts } from "./duplicates";

export interface InspectorRow {
  readonly row: number;
  readonly cells: readonly string[];
}

/** One output and its behaviour. A combo card holds one of these per row. */
function OutputFields({
  member,
  disabled,
  removeLabel,
  onSetCell,
  onRemove,
  repeats,
}: {
  readonly member: InspectorRow;
  readonly disabled: boolean;
  readonly removeLabel: string | null;
  readonly onSetCell: (row: number, column: number, value: string) => void;
  readonly onRemove: (() => void) | null;
  readonly repeats: number;
}) {
  const { t } = useI18n();
  const { row, cells } = member;
  const [draft, setDraft] = useState(() => [cells[0] ?? "", cells[1] ?? ""]);

  const commit = (column: 0 | 1): void => {
    const value = draft[column] ?? "";
    if (value !== (cells[column] ?? "")) onSetCell(row, column, value);
  };

  return (
    <div className="combo-output" data-testid={`combo-output-${String(row)}`}>
      <div className="combo-output-line">
        <label className="editor-field output">
          <span>{t("Main_OutputGameButton")}</span>
          <input
            aria-label={t("Main_OutputForRowBRow", [row])}
            disabled={disabled}
            value={draft[0] ?? ""}
            onChange={(event) => setDraft((current) => current.with(0, event.currentTarget.value))}
            onBlur={() => commit(0)}
          />
        </label>
        <DuplicateMark count={repeats} />
        {onRemove !== null && removeLabel !== null ? (
          <button
            type="button"
            className="combo-output-remove"
            disabled={disabled}
            aria-label={removeLabel}
            title={removeLabel}
            onClick={onRemove}
          >
            <Icon name="delete" size={18} />
          </button>
        ) : null}
      </div>
      <label className="editor-field function">
        <span>{t("Main_FunctionForRowBRow", [row, draft[1] ?? ""])}</span>
        <input
          aria-label={t("Main_FunctionForRowBRow", [row, draft[1] ?? ""])}
          disabled={disabled}
          value={draft[1] ?? ""}
          onChange={(event) => setDraft((current) => current.with(1, event.currentTarget.value))}
          onBlur={() => commit(1)}
        />
      </label>
    </div>
  );
}

/**
 * The open mapping. A button combo (rows sharing one set of inputs) is one
 * card: the inputs live on the card and an edit to them lands on every row,
 * while each output keeps its own behaviour because that is what the file holds.
 */
export function BindingInspector({
  row,
  cells,
  members,
  mappingNumber = 1,
  disabled,
  onSetCell,
  onSetInput,
  onAddOutput,
  onRemoveOutput,
  counts = NO_DUPLICATES,
}: {
  readonly row: number;
  readonly cells: readonly string[];
  /** Every row of the combo, this one first. Just this row when it is not a combo. */
  readonly members?: readonly InspectorRow[];
  readonly mappingNumber?: number;
  readonly disabled: boolean;
  readonly onSetCell: (row: number, column: number, value: string) => void;
  /** An input edit; a combo copies it onto every row in one undo step. */
  readonly onSetInput?: (column: number, value: string) => void;
  readonly onAddOutput?: (() => void) | undefined;
  readonly onRemoveOutput?: (row: number) => void;
  readonly counts?: DuplicateCounts;
}) {
  const { t } = useI18n();
  const outputs = members ?? [{ row, cells }];
  const combo = outputs.length > 1;
  const [draft, setDraft] = useState(() => Array.from({ length: 10 }, (_, column) => cells[column] ?? ""));
  const hasInputs = cells.slice(2, 10).some((cell) => cell.trim().length > 0);

  const commitInput = (column: number): void => {
    const value = draft[column] ?? "";
    if (value === (cells[column] ?? "")) return;
    if (onSetInput === undefined) onSetCell(row, column, value);
    else onSetInput(column, value);
  };

  return (
    <div className="binding-inspector" data-testid={`binding-inspector-${String(row)}`}>
      <div className="binding-header" aria-hidden="true">
        <span className="tint-swatch tint-output">{t("Main_OutputGameButton")}</span>
        <span className="tint-swatch tint-function">{t("Main_FunctionBehavior")}</span>
        <span className="tint-swatch tint-input">{t("Main_InputsSipsPuffsJoystick")}</span>
      </div>
      {combo ? <p className="combo-note">{t("Main_TheseArePressedTogether")}</p> : null}
      {outputs.map((member) => {
        const actionName = member.cells[11]?.trim() ?? "";
        const output = member.cells[0]?.trim() ?? "";
        const named = actionName.length > 0 ? actionName : tokenLabel(output);
        return (
          <OutputFields
            key={member.row}
            member={member}
            disabled={disabled}
            removeLabel={combo ? t("Main_RemoveThisOutputFromMapping", [named]) : null}
            onSetCell={onSetCell}
            onRemove={combo && onRemoveOutput !== undefined ? () => onRemoveOutput(member.row) : null}
            repeats={counts.output(output)}
          />
        );
      })}
      {hasInputs && onAddOutput !== undefined ? (
        <button
          type="button"
          className="combo-add-output"
          disabled={disabled}
          aria-label={t("Main_AddAnotherOutputToMapping", [mappingNumber])}
          title={t("Main_AddAnotherOutput")}
          onClick={onAddOutput}
        >
          <Icon name="add" size={18} />
          <span aria-hidden="true">{t("Main_AddAnotherOutput")}</span>
        </button>
      ) : null}
      <div className="editor-input-grid">
        {Array.from({ length: 8 }, (_, index) => {
          const column = index + 2;
          return (
            <label className="editor-field input-col" key={column}>
              <span>{t("Main_InputI1ForRow", [index + 1, row])}</span>
              <input
                aria-label={t("Main_InputI1ForRow", [index + 1, row])}
                disabled={disabled}
                value={draft[column] ?? ""}
                onChange={(event) => setDraft((current) => current.with(column, event.currentTarget.value))}
                onBlur={() => commitInput(column)}
              />
              {/* 3ad22ce: the combo's own rows share this input by design. */}
              <DuplicateMark count={counts.input(cells[column] ?? "") - (outputs.length - 1)} />
            </label>
          );
        })}
      </div>
    </div>
  );
}
