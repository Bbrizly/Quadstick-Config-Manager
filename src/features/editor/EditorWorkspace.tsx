import { useCallback, useEffect, useMemo, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  ERROR_CODES,
  asQcmError,
  type EditorOp,
  type EditorSnapshot,
  type Issue,
  type Mode,
  type ModelChoice,
  type QcmClient,
} from "../../platform";
import { QuadStickVisualizer } from "../visualizer/QuadStickVisualizer";
import { zonesForRow, type ZoneId } from "../visualizer/deviceSummary";
import { BindingInspector } from "./BindingInspector";
import { SentenceCard } from "./SentenceCard";
import type { MessageKey } from "../../i18n";

const ZONE_TITLES: Record<ZoneId, MessageKey> = {
  joystick: "Main_Joystick",
  mp_left: "Main_LeftMouthpieceHole",
  mp_center: "Main_CenterMouthpieceHole",
  mp_right: "Main_RightMouthpieceHole",
  side: "Main_SideTube",
  lip: "Main_LipSwitch",
  combo: "Main_HoleCombos",
  jacks: "Main_SwitchJacks",
  other: "Main_USBDevices",
  settings: "Main_ModeSettings",
  unset: "Main_NoInputYet",
};

interface EditorWorkspaceProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
}

type EditorView = "device" | "parts" | "rows";

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
                      key={`${String(snapshot.revision)}-${String(row)}-${String(column)}`}
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
  const { t, plural } = useI18n();
  const profileModes = useMemo(
    () => snapshot.modes.filter((mode) => mode.kind === "mode"),
    [snapshot.modes],
  );
  const [selectedSheet, setSelectedSheet] = useState(() => profileModes[0]?.index ?? 0);
  const [selectedRow, setSelectedRow] = useState<number | null>(null);
  const [selectedZone, setSelectedZone] = useState<ZoneId | null>(null);
  const [view, setView] = useState<EditorView>("device");
  const [raw, setRaw] = useState(false);
  const [busy, setBusy] = useState(false);
  const [armedDelete, setArmedDelete] = useState<number | null>(null);
  const [message, setMessage] = useState("");
  const [model, setModel] = useState<ModelChoice>("fps");
  const [liveRows, setLiveRows] = useState<ReadonlySet<number>>(() => new Set());

  useEffect(() => {
    let cancelled = false;
    const load = (): void => {
      void client.getSettings().then(
        (settings) => {
          if (!cancelled) setModel(settings.model);
        },
        () => undefined,
      );
    };
    load();
    const onFocus = (): void => {
      load();
    };
    window.addEventListener("focus", onFocus);
    return () => {
      cancelled = true;
      window.removeEventListener("focus", onFocus);
    };
  }, [client]);

  const selectedMode = snapshot.modes.find(
    (mode) => mode.index === selectedSheet && mode.kind === "mode",
  ) ?? profileModes[0] ?? null;
  const rows = useMemo(() => bindingRows(snapshot, selectedMode), [snapshot, selectedMode]);
  const zoneRows = useMemo(() => {
    if (selectedZone === null) return rows;
    return rows.filter((row) => zonesForRow(row).includes(selectedZone));
  }, [rows, selectedZone]);
  const selectionBelongsToCurrentSheet = selectedMode?.index === selectedSheet;
  const visibleSelectedRow = selectionBelongsToCurrentSheet ? selectedRow : null;
  const activeRow = visibleSelectedRow === null ? null : rows.find((row) => row.row === visibleSelectedRow) ?? null;

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
      // A modal owns the keyboard while it is open: Ctrl+S inside the close
      // prompt must not race a background save against the prompt's own.
      if (document.querySelector('[aria-modal="true"]') !== null) return;
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
    const cells = snapshot.grid[row - 1] ?? [];
    const zones = zonesForRow({ row, cells });
    setSelectedZone(zones[0] ?? null);
    setSelectedRow(row);
    requestAnimationFrame(() => {
      document.querySelector<HTMLElement>(`[data-binding-row="${String(row)}"]`)?.focus();
    });
  };

  const moveMode = (mode: Mode, delta: -1 | 1): void => {
    const target = adjacentMovableSheet(snapshot, mode.index, delta);
    if (target === null) return;
    void apply([{ op: "move_mode", sheet: mode.index, delta }], () => {
      setSelectedSheet(target);
      setSelectedRow(null);
      setSelectedZone(null);
    });
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
      setSelectedRow(null);
      setSelectedZone(null);
    });
  };

  const selectMode = (index: number): void => {
    setSelectedSheet(index);
    setSelectedRow(null);
    setSelectedZone(null);
    setArmedDelete(null);
  };

  const zoneIdForRow = (row: BindingRow): ZoneId => zonesForRow(row)[0] ?? "unset";

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
          <button
            className="command"
            type="button"
            disabled={busy || !snapshot.canUndo}
            aria-label={t("Shell_UndoCtrlZ")}
            onClick={() => void undo()}
          >
            <Icon name="undo" />
          </button>
          <button
            className="command"
            type="button"
            disabled={busy}
            aria-label={t("Shell_SaveCtrlS")}
            onClick={() => void save()}
          >
            <Icon name="save" />
          </button>
          <button type="button" aria-pressed={raw} onClick={() => setRaw((value) => !value)}>
            {raw ? t("Review_GoBackToTheSimple") : t("Review_ShowTheSpreadsheetWithThe")}
          </button>
        </div>
      </div>

      {raw ? (
        <RawGrid snapshot={snapshot} disabled={busy} onSetCell={setCell} />
      ) : (
        <div className="editor-plate">
          <aside className="modes-panel" aria-label={t("Shell_SelectWhichModeToEdit")}>
            <div className="modes-panel-body">
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
                      setSelectedRow(null);
                      setSelectedZone(null);
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
                      <button
                        className="mode-select"
                        type="button"
                        aria-current={selected ? "true" : undefined}
                        onClick={() => selectMode(mode.index)}
                      >
                        <span className="mode-number">{mode.number}</span>
                        <span>{mode.name || t("Review_UnnamedMode")}</span>
                      </button>
                      <input
                        key={`${String(snapshot.revision)}-${String(mode.index)}`}
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
                        <button type="button" disabled={busy} aria-label={t("Modes_MakeACopyOfName", [mode.name])} onClick={() => void apply([{ op: "duplicate_mode", sheet: mode.index, name: `${mode.name} copy` }], (next) => { const copy = next.modes.findLast((candidate) => candidate.kind === "mode"); if (copy !== undefined) setSelectedSheet(copy.index); setSelectedRow(null); setSelectedZone(null); })}>＋</button>
                        <button type="button" disabled={busy || profileModes.length <= 1} aria-label={armedDelete === mode.index ? t("Modes_ReallyDeleteName", [mode.name]) : t("Shell_Delete")} onClick={() => deleteMode(mode)}>×</button>
                      </div>
                    </li>
                  );
                })}
              </ol>
            </div>
            <div className="switchtrack" role="group" aria-label={t("Main_UsingDeviceView")}>
              <button
                className={view === "device" ? "switchkey viewkey active" : "switchkey viewkey"}
                type="button"
                aria-pressed={view === "device"}
                aria-label={t("Shell_ShowTheMappingsOnA")}
                title={t("Shell_SeeTheMappingsOnA")}
                onClick={() => setView("device")}
              >
                <Icon name="viewDevice" size={22} />
                <span>{t("Shell_Device")}</span>
              </button>
              <button
                className={view === "parts" ? "switchkey viewkey active" : "switchkey viewkey"}
                type="button"
                aria-pressed={view === "parts"}
                aria-label={t("Shell_ShowThePartsAsA")}
                onClick={() => setView("parts")}
              >
                <Icon name="viewRail" size={22} />
                <span>{t("Shell_Parts")}</span>
              </button>
              <button
                className={view === "rows" ? "switchkey viewkey active" : "switchkey viewkey"}
                type="button"
                aria-pressed={view === "rows"}
                aria-label={t("Shell_ShowTheMappingsAsA")}
                onClick={() => setView("rows")}
              >
                <Icon name="viewSheet" size={22} />
                <span>{t("Shell_Rows")}</span>
              </button>
            </div>
          </aside>

          <div className="device-workspace">
            <section className="device-canvas">
              <div className="panel-heading-row">
                <h2>
                  {view === "device"
                    ? t("Shell_Device")
                    : view === "parts"
                      ? t("Shell_Parts")
                      : t("Shell_Rows")}
                </h2>
                {selectedMode !== null ? (
                  <button
                    type="button"
                    disabled={busy}
                    aria-label={t("Shell_AddANewBindingRow")}
                    onClick={() => void apply([{ op: "add_row", sheet: selectedMode.index }])}
                  >
                    {t("Shell_AddRow")}
                  </button>
                ) : null}
              </div>
              <QuadStickVisualizer
                client={client}
                rows={rows}
                selectedRow={visibleSelectedRow}
                selectedZone={selectedZone}
                modeName={selectedMode?.name ?? ""}
                modeNumber={selectedMode?.number ?? null}
                model={model}
                view={view}
                onSelectRow={setSelectedRow}
                onSelectZone={setSelectedZone}
                onLiveRows={setLiveRows}
              />
            </section>

            <aside className="mapping-panel" aria-labelledby="mapping-title">
              <div className="mapping-panel-head">
                <h2 id="mapping-title">
                  {selectedZone === null
                    ? t("Shell_Configuration")
                    : t(ZONE_TITLES[selectedZone])}
                </h2>
                <span
                  className={
                    zoneRows.length === 0 ? "mapping-count muted" : "mapping-count accent"
                  }
                >
                  {zoneRows.length === 0
                    ? t("Main_NotMapped")
                    : plural("Count_Mapping", zoneRows.length, [zoneRows.length])}
                </span>
                {selectedMode !== null && selectedZone !== null && selectedZone !== "unset" ? (
                  <button
                    type="button"
                    className="mapping-add"
                    disabled={busy}
                    aria-label={t("Shell_AddANewBindingRow")}
                    onClick={() => void apply([{ op: "add_row", sheet: selectedMode.index }])}
                  >
                    {t("Shell_AddRow")}
                  </button>
                ) : null}
              </div>
              {selectedZone === null && zoneRows.length === 0 ? (
                <p className="empty-copy">{t("Main_NothingSelectedNNPickA")}</p>
              ) : zoneRows.length === 0 ? (
                <p className="empty-copy">{t("Main_NoInputYet")}</p>
              ) : (
                <div className="binding-list">
                  {zoneRows.map((row) => (
                    <SentenceCard
                      key={row.row}
                      row={row}
                      zoneId={zoneIdForRow(row)}
                      selected={visibleSelectedRow === row.row}
                      live={liveRows.has(row.row)}
                      onSelect={() => setSelectedRow(row.row)}
                    />
                  ))}
                </div>
              )}
              {activeRow !== null ? (
                <>
                  <div className="row-actions">
                    <button type="button" disabled={busy || rows[0]?.row === activeRow.row} aria-label={t("Review_MoveItEarlier")} onClick={() => void apply([{ op: "move_row", from: activeRow.row, to: activeRow.row - 1 }], () => setSelectedRow(activeRow.row - 1))}>↑</button>
                    <button type="button" disabled={busy || rows.at(-1)?.row === activeRow.row} aria-label={t("Review_MoveItLater")} onClick={() => void apply([{ op: "move_row", from: activeRow.row, to: activeRow.row + 1 }], () => setSelectedRow(activeRow.row + 1))}>↓</button>
                    <button type="button" disabled={busy} onClick={() => void apply([{ op: "delete_row", row: activeRow.row }], () => { setSelectedRow(null); setSelectedZone(null); })}>{t("Shell_Delete")}</button>
                  </div>
                  <BindingInspector
                    key={`${String(snapshot.revision)}-${String(activeRow.row)}`}
                    row={activeRow.row}
                    cells={activeRow.cells}
                    disabled={busy}
                    onSetCell={setCell}
                  />
                </>
              ) : null}
            </aside>
          </div>
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
                  <span>{t("Main_SeverityLabelBaseName", [t(issue.severity === "error" ? "Rewrite_SeverityError" : "Rewrite_SeverityWarning"), issue.cell])}</span>
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
