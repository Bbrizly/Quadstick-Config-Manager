import { useState } from "react";

import { useI18n } from "../../i18n";

export function BindingInspector({
  row,
  cells,
  disabled,
  onSetCell,
}: {
  readonly row: number;
  readonly cells: readonly string[];
  readonly disabled: boolean;
  readonly onSetCell: (row: number, column: number, value: string) => void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState(() => Array.from({ length: 10 }, (_, column) => cells[column] ?? ""));

  const commit = (column: number): void => {
    const value = draft[column] ?? "";
    if (value !== (cells[column] ?? "")) onSetCell(row, column, value);
  };

  return (
    <div className="binding-inspector" data-testid={`binding-inspector-${String(row)}`}>
      <div className="binding-header" aria-hidden="true">
        <span className="tint-swatch tint-output">{t("Main_OutputGameButton")}</span>
        <span className="tint-swatch tint-function">{t("Main_FunctionBehavior")}</span>
        <span className="tint-swatch tint-input">{t("Main_InputsSipsPuffsJoystick")}</span>
      </div>
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
                onBlur={() => commit(column)}
              />
            </label>
          );
        })}
      </div>
    </div>
  );
}
