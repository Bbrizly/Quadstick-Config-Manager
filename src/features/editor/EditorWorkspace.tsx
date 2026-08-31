import { useCallback, useEffect, useMemo, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  ERROR_CODES,
  asQcmError,
  type EditorOp,
  type EditorSnapshot,
  type Issue,
  type Mode,
  type QcmClient,
} from "../../platform";
import { QuadStickVisualizer } from "../visualizer/QuadStickVisualizer";

interface EditorWorkspaceProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
}

interface BindingRow {
  readonly row: number;
  readonly cells: readonly string[];
}

function bindingRows(snapshot: EditorSnapshot, mode: Mode | null): BindingRow[] {
  if (mode === null || mode.kind !== "mode") return [];
  const rows: BindingRow[] = [];
  for (let offset = 0; offset < mode.bindingCount; offset += 1) {
    const row = mode.startRow + 3 + offset;
    rows.push({ row, cells: snapshot.grid[row - 1] ?? [] });
  }
  return rows;
}

function issueRow(cell: string): number | null {
  const match = /([0-9]+)$/u.exec(cell.trim());
  if (match?.[1] === undefined) return null;
  const parsed = Number.parseInt(match[1], 10);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function sheetForRow(snapshot: EditorSnapshot, row: number): Mode | null {
  let found: Mode | null = null;
  for (const mode of snapshot.modes) {
    if (mode.startRow <= row) found = mode;
    else break;
  }
  return found;
}

function adjacentMovableSheet(snapshot: EditorSnapshot, sheet: number, delta: -1 | 1): number | null {
  let index = sheet + delta;
  while (index >= 0 && index < snapshot.modes.length) {
    const candidate = snapshot.modes[index];
    if (candidate !== undefined && candidate.kind !== "infrared") return index;
    index += delta;
  }
  return null;
}

function columnName(index: number): string {
  let value = index + 1;
  let name = "";
  while (value > 0) {
    const remainder = (value - 1) % 26;
    name = String.fromCharCode(65 + remainder) + name;
    value = Math.floor((value - 1) / 26);
  }
  return name;
}

function BindingInspector({
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
      <label className="editor-field">
        <span>{t("Main_OutputGameButton")}</span>
        <input
          aria-label={t("Main_OutputForRowBRow", [row])}
          disabled={disabled}
          value={draft[0] ?? ""}
          onChange={(event) => setDraft((current) => current.with(0, event.currentTarget.value))}
          onBlur={() => commit(0)}
        />
      </label>
      <label className="editor-field">
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
            <label className="editor-field" key={column}>
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

function RawGrid({
  snapshot,
  disabled,
  onSetCell,
}: {
  readonly snapshot: EditorSnapshot;
  readonly disabled: boolean;
  readonly onSetCell: (row: number, column: number, value: string) => void;
}) {
  const { t } = useI18n();
  const columns = Math.max(12, ...snapshot.grid.map((row) => row.length));

  return (
    <div className="raw-grid-wrap">
      <table className="raw-grid">
        <thead>
          <tr>
            <th scope="col">#</th>
            {Array.from({ length: columns }, (_, column) => (
              <th scope="col" key={column}>{columnName(column)}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {snapshot.grid.map((cells, rowIndex) => {
            const row = rowIndex + 1;
            return (
              <tr key={row}>
                <th scope="row">{row}</th>
                {Array.from({ length: columns }, (_, column) => (
                  <td key={column}>
                    <input
                      aria-label={t("Review_ContentsOfCellWhereMeaning", [columnName(column), row])}
                      defaultValue={cells[column] ?? ""}
                      disabled={disabled}
                      onBlur={(event) => {
                        const value = event.currentTarget.value;
                        if (value !== (cells[column] ?? "")) onSetCell(row, column, value);
                      }}
                    />
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

export function EditorWorkspace({ client, snapshot, onSnapshot }: EditorWorkspaceProps) {
  const { t } = useI18n();
  const profileModes = useMemo(
    () => snapshot.modes.filter((mode) => mode.kind === "mode"),
    [snapshot.modes],
  );
  const [selectedSheet, setSelectedSheet] = useState(() => profileModes[0]?.index ?? 0);
  const [selectedRow, setSelectedRow] = useState<number | null>(null);
  const [raw, setRaw] = useState(false);
  const [busy, setBusy] = useState(false);
  const [armedDelete, setArmedDelete] = useState<number | null>(null);
  const [message, setMessage] = useState("");

  const selectedMode = snapshot.modes.find(
    (mode) => mode.index === selectedSheet && mode.kind === "mode",
  ) ?? profileModes[0] ?? null;
  const rows = useMemo(() => bindingRows(snapshot, selectedMode), [snapshot, selectedMode]);
  const activeRow = selectedRow === null ? null : rows.find((row) => row.row === selectedRow) ?? null;

  useEffect(() => {
    if (selectedMode !== null && selectedMode.index !== selectedSheet) setSelectedSheet(selectedMode.index);
    if (selectedRow !== null && !rows.some((row) => row.row === selectedRow)) setSelectedRow(null);
  }, [rows, selectedMode, selectedRow, selectedSheet]);

  const showFailure = useCallback(
    (reason: unknown): void => {
      const error = asQcmError(reason);
      setMessage(localizedErrorMessage(error.payload, t));
    },
    [t],
  );

  const refreshAfterConflict = useCallback(
    async (reason: unknown): Promise<void> => {
      const error = asQcmError(reason);
      if (error.code !== ERROR_CODES.profileRevisionConflict) {
        showFailure(error);
        return;
      }
      try {
        const current = await client.getProfileSnapshot(snapshot.sessionId);
        onSnapshot(current);
      } catch (refreshReason) {
        showFailure(refreshReason);
        return;
      }
      setMessage(localizedErrorMessage(error.payload, t));
    },
    [client, onSnapshot, showFailure, snapshot.sessionId, t],
  );

  const apply = useCallback(
    async (ops: readonly EditorOp[], after?: (next: EditorSnapshot) => void): Promise<void> => {
      if (busy || ops.length === 0) return;
      setBusy(true);
      try {
        const next = await client.applyEditorOps(snapshot.sessionId, snapshot.revision, ops);
        onSnapshot(next);
        setMessage("");
        after?.(next);
      } catch (reason) {
        await refreshAfterConflict(reason);
      } finally {
        setBusy(false);
      }
    },
    [busy, client, onSnapshot, refreshAfterConflict, snapshot.revision, snapshot.sessionId],
  );

  const setCell = useCallback(
    (row: number, column: number, value: string): void => {
      void apply([{ op: "set_cell", row, col: column, value }]);
    },
    [apply],
  );

  const undo = useCallback(async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const current = await client.getProfileSnapshot(snapshot.sessionId);
      if (!current.canUndo) {
        onSnapshot(current);
        return;
      }
      const next = await client.undoEditor(current.sessionId, current.revision);
      onSnapshot(next);
      setMessage("");
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot.sessionId]);

  const save = useCallback(async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const receipt = snapshot.saveTarget === null
        ? await client.saveProfileAs(snapshot.sessionId, snapshot.revision)
        : await client.saveProfile(snapshot.sessionId, snapshot.revision);
      if (receipt === null) return;
      const next = await client.getProfileSnapshot(snapshot.sessionId);
      onSnapshot(next);
      setMessage(t("Main_SavedToSavePath", [receipt.name]));
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot, t]);

  useEffect(() => {
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === "z") {
        event.preventDefault();
        void undo();
      } else if (key === "s") {
        event.preventDefault();
        void save();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [save, undo]);

  const focusIssue = (issue: Issue): void => {
    const row = issueRow(issue.cell);
    if (row === null) return;
    const mode = sheetForRow(snapshot, row);
    if (mode?.kind === "mode") setSelectedSheet(mode.index);
    setSelectedRow(row);
    requestAnimationFrame(() => {
      document.querySelector<HTMLElement>(`[data-binding-row="${String(row)}"]`)?.focus();
    });
  };

  const moveMode = (mode: Mode, delta: -1 | 1): void => {
    const target = adjacentMovableSheet(snapshot, mode.index, delta);
    if (target === null) return;
    void apply([{ op: "move_mode", sheet: mode.index, delta }], () => setSelectedSheet(target));
  };

  const deleteMode = (mode: Mode): void => {
    if (armedDelete !== mode.index) {
      setArmedDelete(mode.index);
      return;
    }
    setArmedDelete(null);
    void apply([{ op: "delete_mode", sheet: mode.index }], (next) => {
      const remaining = next.modes.filter((candidate) => candidate.kind === "mode");
      const fallback = remaining.find((candidate) => candidate.index >= mode.index) ?? remaining.at(-1);
      if (fallback !== undefined) setSelectedSheet(fallback.index);
    });
  };

  return (
    <section className="editor-workspace" aria-labelledby="editor-title">
      <div className="editor-toolbar">
        <div className="editor-title-wrap">
          <h1 id="editor-title">{snapshot.title || t("Shell_Profile")}</h1>
          {snapshot.dirty ? (
            <span className="dirty-indicator" aria-label={t("Main_ThisProfileHasUnsavedChanges")}>*</span>
          ) : null}
        </div>
        <div className="editor-actions">
          <button type="button" disabled={busy || !snapshot.canUndo} onClick={() => void undo()}>
            {t("Shell_UndoCtrlZ")}
          </button>
          <button className="primary-action" type="button" disabled={busy} onClick={() => void save()}>
            {t("Shell_SaveCtrlS")}
          </button>
          <button type="button" aria-pressed={raw} onClick={() => setRaw((value) => !value)}>
            {raw ? t("Review_GoBackToTheSimple") : t("Review_ShowTheSpreadsheetWithThe")}
          </button>
        </div>
      </div>

      {raw ? (
        <RawGrid snapshot={snapshot} disabled={busy} onSetCell={setCell} />
      ) : (
        <div className="editor-columns">
          <aside className="modes-panel" aria-label={t("Shell_SelectWhichModeToEdit")}>
            <div className="panel-heading-row">
              <h2>{t("Modes_Modes")}</h2>
              <button
                type="button"
                disabled={busy}
                aria-label={t("Modes_AddAMode")}
                onClick={() => {
                  const name = `Mode ${String(profileModes.length + 1)}`;
                  void apply([{ op: "add_mode", name }], (next) => {
                    const added = next.modes.findLast((mode) => mode.kind === "mode");
                    if (added !== undefined) setSelectedSheet(added.index);
                  });
                }}
              >
                {t("Modes_AddMode")}
              </button>
            </div>
            <ol className="mode-list">
              {snapshot.modes.filter((mode) => mode.kind !== "infrared").map((mode) => {
                if (mode.kind !== "mode") {
                  return (
                    <li className="mode-structure-row" key={`sheet-${String(mode.index)}`}>
                      <span>{t("Modes_PreferencesDeviceSettings")}</span>
                      <span className="mode-row-actions">
                        <button type="button" disabled={busy || adjacentMovableSheet(snapshot, mode.index, -1) === null} aria-label={t("Review_MoveItEarlier")} onClick={() => moveMode(mode, -1)}>↑</button>
                        <button type="button" disabled={busy || adjacentMovableSheet(snapshot, mode.index, 1) === null} aria-label={t("Review_MoveItLater")} onClick={() => moveMode(mode, 1)}>↓</button>
                        <button type="button" disabled={busy} aria-label={armedDelete === mode.index ? t("Modes_ReallyDelete") : t("Shell_Delete")} onClick={() => deleteMode(mode)}>×</button>
                      </span>
                    </li>
                  );
                }
                const selected = selectedMode?.index === mode.index;
                return (
                  <li className="mode-row" data-testid={`mode-row-${String(mode.index)}`} key={`mode-${String(mode.index)}`}>
                    <button className="mode-select" type="button" aria-current={selected ? "true" : undefined} onClick={() => { setSelectedSheet(mode.index); setSelectedRow(null); setArmedDelete(null); }}>
                      <span className="mode-number">{mode.number}</span>
                      <span>{mode.name || t("Review_UnnamedMode")}</span>
                    </button>
                    <input
                      className="mode-name-input"
                      aria-label={t("Modes_NameOfModeOrdinal", [mode.number ?? mode.index + 1])}
                      defaultValue={mode.name}
                      disabled={busy}
                      onBlur={(event) => {
                        const name = event.currentTarget.value.trim();
                        if (name !== "" && name !== mode.name) void apply([{ op: "rename_mode", sheet: mode.index, name }]);
                      }}
                    />
                    <div className="mode-row-actions">
                      <button type="button" disabled={busy || adjacentMovableSheet(snapshot, mode.index, -1) === null} aria-label={t("Review_MoveItEarlier")} onClick={() => moveMode(mode, -1)}>↑</button>
                      <button type="button" disabled={busy || adjacentMovableSheet(snapshot, mode.index, 1) === null} aria-label={t("Review_MoveItLater")} onClick={() => moveMode(mode, 1)}>↓</button>
                      <button type="button" disabled={busy} aria-label={t("Modes_MakeACopyOfName", [mode.name])} onClick={() => void apply([{ op: "duplicate_mode", sheet: mode.index, name: `${mode.name} copy` }], (next) => { const copy = next.modes.findLast((candidate) => candidate.kind === "mode"); if (copy !== undefined) setSelectedSheet(copy.index); })}>＋</button>
                      <button type="button" disabled={busy || profileModes.length <= 1} aria-label={armedDelete === mode.index ? t("Modes_ReallyDeleteName", [mode.name]) : t("Shell_Delete")} onClick={() => deleteMode(mode)}>×</button>
                    </div>
                  </li>
                );
              })}
            </ol>
          </aside>

          <section className="bindings-panel" aria-labelledby="rows-title">
            <div className="panel-heading-row">
              <h2 id="rows-title">{t("Shell_Rows")}</h2>
              {selectedMode !== null ? (
                <button type="button" disabled={busy} aria-label={t("Shell_AddANewBindingRow")} onClick={() => void apply([{ op: "add_row", sheet: selectedMode.index }])}>
                  {t("Shell_AddRow")}
                </button>
              ) : null}
            </div>
            <QuadStickVisualizer
              client={client}
              rows={rows}
              selectedRow={selectedRow}
              modeName={selectedMode?.name ?? ""}
              modeNumber={selectedMode?.number ?? null}
              onSelectRow={setSelectedRow}
            />
            {activeRow !== null ? (
              <div className="row-actions">
                <button type="button" disabled={busy || rows[0]?.row === activeRow.row} aria-label={t("Review_MoveItEarlier")} onClick={() => void apply([{ op: "move_row", from: activeRow.row, to: activeRow.row - 1 }], () => setSelectedRow(activeRow.row - 1))}>↑</button>
                <button type="button" disabled={busy || rows.at(-1)?.row === activeRow.row} aria-label={t("Review_MoveItLater")} onClick={() => void apply([{ op: "move_row", from: activeRow.row, to: activeRow.row + 1 }], () => setSelectedRow(activeRow.row + 1))}>↓</button>
                <button type="button" disabled={busy} onClick={() => void apply([{ op: "delete_row", row: activeRow.row }], () => setSelectedRow(null))}>{t("Shell_Delete")}</button>
              </div>
            ) : null}
          </section>

          <aside className="inspector-panel" aria-labelledby="inspector-title">
            <h2 id="inspector-title">{t("Shell_Configuration")}</h2>
            {activeRow === null ? (
              <p className="empty-copy">{t("Main_NoInputYet")}</p>
            ) : (
              <BindingInspector
                key={`${String(snapshot.revision)}-${String(activeRow.row)}`}
                row={activeRow.row}
                cells={activeRow.cells}
                disabled={busy}
                onSetCell={setCell}
              />
            )}
          </aside>
        </div>
      )}

      <section className="issues-panel" aria-labelledby="issues-title">
        <h2 id="issues-title">{t("Shell_ListOfValidationProblemsSelect")}</h2>
        {snapshot.issues.length === 0 ? (
          <p>{t("Main_NoProblemsReadyToSave")}</p>
        ) : (
          <ul>
            {snapshot.issues.map((issue, index) => (
              <li key={`${issue.cell}-${issue.kind}-${String(index)}`}>
                <button type="button" className={`issue-link issue-${issue.severity}`} onClick={() => focusIssue(issue)}>
                  <span>{t("Main_SeverityLabelBaseName", [issue.severity, issue.cell])}</span>
                  <span>{issue.message}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <LiveRegion>{message}</LiveRegion>
      {message !== "" ? <output className="editor-message">{message}</output> : null}
    </section>
  );
}
