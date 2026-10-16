import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  ERROR_CODES,
  FUNCTIONS,
  KNOWN_OUTPUTS,
  PICKER_INPUTS,
  asQcmError,
  type AppSettings,
  type EditorOp,
  type EditorSnapshot,
  type Issue,
  type Mode,
  type ModelChoice,
  type QcmClient,
  type SettingsPatch,
} from "../../platform";
import { QuadStickVisualizer } from "../visualizer/QuadStickVisualizer";
import { diagramFor } from "../visualizer/deviceDiagram";
import { zoneOf, zonesForRow, type ZoneId } from "../visualizer/deviceSummary";
import { firstMappedCombo, partLabel, partOf } from "../visualizer/parts";
import type { MessageKey } from "../../i18n";
import { BindingInspector } from "./BindingInspector";
import { comboGroups } from "./combos";
import { CustomNamesTable, customNames, nextDraftName } from "./CustomNames";
import { duplicateCounts } from "./duplicates";
import { ModesDialog } from "./ModesDialog";
import { InfoButton, Popover } from "./Popover";
import { ProblemsDock, StatusChip, useValidationStatus, type EditorStatus } from "./ProblemsDock";
import { RowsGrid, type LabelStyle } from "./RowsGrid";
import { SaveTemplateDialog } from "./SaveTemplateDialog";
import { SentenceCard } from "./SentenceCard";
import { UnusedButton } from "./UnusedInputs";

/** Column K, where the device ignores what is written (`ProfileFile.NoteColumn`). */
const NOTE_COLUMN = 10;

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

// `Zone.DefaultInput`: what a part's own add button puts on the new row, so
// the mapping lands in the panel the user is looking at.
const ZONE_DEFAULT_INPUT: Record<ZoneId, string | null> = {
  joystick: "up",
  mp_left: "mp_left_sip",
  mp_center: "mp_center_sip",
  mp_right: "mp_right_sip",
  combo: "mp_left_center_sip",
  side: "right_sip",
  lip: "lip",
  jacks: "digital_in_8",
  other: "usb_1_button_1",
  settings: null,
  unset: null,
};

const MODEL_CHOICES: readonly { readonly value: ModelChoice; readonly label: string }[] = [
  { value: "fps", label: "QuadStick FPS" },
  { value: "original", label: "QuadStick Original" },
  { value: "singleton", label: "QuadStick Singleton" },
];

const MODEL_DESCRIPTION: Record<ModelChoice, MessageKey> = {
  fps: "Main_FPS3HoleMouthpieceSide",
  original: "Main_Original3HoleMouthpieceSide",
  singleton: "Main_SingletonASingleSipPuff",
};

const LABEL_STYLE_TEXT: Record<LabelStyle, { readonly label: MessageKey; readonly name: MessageKey }> = {
  0: { label: "Main_WordsListNames", name: "Main_WordsAreShownAsRaw" },
  1: { label: "Main_WordsPlainEnglish", name: "Main_WordsAreShownInPlain" },
  2: { label: "Main_WordsXboxStyle", name: "Main_WordsAreShownAsXbox" },
};

export interface EditorWorkspaceProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
  /** Install, the destination, pinned at the right of the command bar. */
  readonly onInstall?: () => void;
  /** The quick guide behind `?` (F1). */
  readonly onHelp?: () => void;
  /**
   * The Share flyout's body. The shell owns the Drive and follow actions and
   * puts their buttons into this element while the flyout is open; their
   * dialogs and messages stay mounted in the shell.
   */
  readonly onShareSlot?: (element: HTMLElement | null) => void;
  readonly busyExternal?: boolean;
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

function zoneIdForRow(row: BindingRow): ZoneId {
  return zonesForRow(row)[0] ?? "unset";
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

/** The file name cell: the row under the first sheet's header (`FileNameCellRow`). */
function fileNameRow(snapshot: EditorSnapshot): number {
  return (snapshot.modes[0]?.startRow ?? 1) + 1;
}

/** `BareName`: the user never types or reads ".csv". */
function bareName(name: string): string {
  const trimmed = name.trim();
  return trimmed.toLowerCase().endsWith(".csv") ? trimmed.slice(0, -4) : trimmed;
}

/** The profile name box with its fixed ".csv" (`FileNameBox`). */
function FileNameBox({
  value,
  disabled,
  onCommit,
}: {
  readonly value: string;
  readonly disabled: boolean;
  readonly onCommit: (bare: string) => void;
}) {
  const { t } = useI18n();
  return (
    <span className="filename-box">
      <input
        key={value}
        defaultValue={value}
        disabled={disabled}
        aria-label={t("Shell_ProfileNameEditToRename")}
        onBlur={(event) => {
          const typed = bareName(event.currentTarget.value);
          if (typed === "") {
            event.currentTarget.value = value;
            return;
          }
          event.currentTarget.value = typed;
          if (typed !== value) onCommit(typed);
        }}
        onKeyDown={(event) => {
          if (event.key === "Enter") event.currentTarget.blur();
        }}
      />
      <span className="filename-suffix" dir="ltr" aria-hidden="true">{t("Shell_Csv")}</span>
    </span>
  );
}

/**
 * Whether a QuadStick is here, said in the sidebar (`RefreshDeviceHeaderStatus`):
 * a drive that is mounted, or a stick the live reader hears. Asked on open and
 * again only when the device list says it changed; nothing polls.
 */
function useStickConnected(client: QcmClient, liveHeard: boolean): boolean {
  const [drives, setDrives] = useState(false);
  useEffect(() => {
    let cancelled = false;
    let subscription: { dispose(): void } | null = null;
    const ask = (): void => {
      void client.listDevices().then(
        (snapshot) => {
          if (!cancelled) setDrives(snapshot.devices.length > 0);
        },
        () => undefined,
      );
    };
    ask();
    void client.subscribeDevicesChanged(ask).then(
      (next) => {
        if (cancelled) next.dispose();
        else subscription = next;
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
      subscription?.dispose();
    };
  }, [client]);
  return drives || liveHeard;
}

export function EditorWorkspace({
  client,
  snapshot,
  onSnapshot,
  onInstall,
  onHelp,
  onShareSlot,
  busyExternal = false,
}: EditorWorkspaceProps) {
  const { t, plural } = useI18n();
  const profileModes = useMemo(
    () => snapshot.modes.filter((mode) => mode.kind === "mode"),
    [snapshot.modes],
  );
  const [selectedSheet, setSelectedSheet] = useState(() => profileModes[0]?.index ?? 0);
  // One past the last sheet is the names table, as on Avalonia.
  const [onCustomNames, setOnCustomNames] = useState(false);
  // Names no row uses yet (`CustomNames.cs` `_drafts`), for this session
  // only: another profile's never show here. Kept in `custom-names.json`
  // under the profile's file, as Avalonia keeps them in settings by path.
  const [draftState, setDraftState] = useState<{ readonly session: string; readonly map: ReadonlyMap<string, string> }>(
    () => ({ session: snapshot.sessionId, map: new Map() }),
  );
  const drafts = useMemo(
    () => (draftState.session === snapshot.sessionId ? draftState.map : new Map<string, string>()),
    [draftState, snapshot.sessionId],
  );
  const draftsRef = useRef(drafts);
  useEffect(() => {
    draftsRef.current = drafts;
  }, [drafts]);
  const keepDrafts = client.setCustomNameDrafts;
  const setDrafts = useCallback(
    (next: ReadonlyMap<string, string>): void => {
      setDraftState({ session: snapshot.sessionId, map: next });
      // `PersistDrafts`: untitled, the backend says so and the window holds them.
      void keepDrafts?.call(client, snapshot.sessionId, [...next]).catch(() => undefined);
    },
    [client, keepDrafts, snapshot.sessionId],
  );
  // `LoadDrafts` on open, and after the first Save As, where an untitled
  // profile's names finally get a file to be kept under.
  const loadDrafts = client.getCustomNameDrafts;
  const draftTarget = snapshot.saveTarget;
  useEffect(() => {
    if (loadDrafts === undefined || draftTarget === null) return undefined;
    let cancelled = false;
    const session = snapshot.sessionId;
    void loadDrafts.call(client, session).then(
      (found) => {
        if (cancelled) return;
        const local = draftsRef.current;
        if (found.drafts.length === 0 && local.size > 0) {
          void keepDrafts?.call(client, session, [...local]).catch(() => undefined);
          return;
        }
        setDraftState({ session, map: new Map(found.drafts) });
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client, draftTarget, keepDrafts, loadDrafts, snapshot.sessionId]);
  const [selectedRow, setSelectedRow] = useState<number | null>(null);
  // `FocusNewMappingCell`: which field of a just-added row takes focus once
  // it has rendered (0 output, 2 first input). Focus must not fall to the page.
  const focusNew = useRef<{ readonly row: number; readonly field: number } | null>(null);
  useEffect(() => {
    const pending = focusNew.current;
    if (pending === null) return;
    const fields = document.querySelectorAll<HTMLElement>(`[data-binding-row="${String(pending.row)}"] input`);
    const target = fields[pending.field] ?? fields[0];
    if (target === undefined) return;
    target.focus();
    focusNew.current = null;
  });
  const [selectedZone, setSelectedZoneState] = useState<ZoneId | null>(null);
  const [pickedInput, setPickedInput] = useState<string | null>(null);
  const [wholeZone, setWholeZone] = useState(false);
  const setSelectedZone = useCallback(
    (zone: ZoneId | null): void => {
      if (zone !== selectedZone) {
        setPickedInput(null);
        setWholeZone(false);
      }
      setSelectedZoneState(zone);
    },
    [selectedZone],
  );
  const pickInput = useCallback((input: string | null): void => {
    setPickedInput(input);
    setWholeZone(false);
  }, []);
  const [view, setView] = useState<EditorView>("device");
  const [busy, setBusy] = useState(false);
  const [armedDelete, setArmedDelete] = useState<number | null>(null);
  const [message, setMessage] = useState("");
  // The status line beside the problems count: the last thing said, else the
  // validation summary (`MainWindow.Status`).
  const [statusOverride, setStatusOverride] = useState<EditorStatus | null>(null);
  const validation = useValidationStatus(snapshot.issues);
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [labelStyle, setLabelStyle] = useState<LabelStyle>(1);
  const [liveRows, setLiveRows] = useState<ReadonlySet<number>>(() => new Set());
  const [liveHeard, setLiveHeard] = useState(false);
  const [modesOpen, setModesOpen] = useState(false);
  const [templateOpen, setTemplateOpen] = useState(false);
  const [selecting, setSelecting] = useState(false);
  const [selectedRows, setSelectedRows] = useState<ReadonlySet<number>>(() => new Set());
  const [zoneListSlot, setZoneListSlot] = useState<HTMLDivElement | null>(null);
  const connected = useStickConnected(client, liveHeard);
  const model: ModelChoice = settings?.model ?? "fps";

  useEffect(() => {
    let cancelled = false;
    const load = (): void => {
      void client.getSettings().then(
        (next) => {
          if (!cancelled) setSettings(next);
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

  const patchSettings = useCallback(
    (patch: SettingsPatch): void => {
      if (settings === null) return;
      setSettings({ ...settings, ...patch });
      void client.updateSettings(settings.revision, patch).then(setSettings, () => {
        void client.getSettings().then(setSettings, () => undefined);
      });
    },
    [client, settings],
  );

  const selectedMode = snapshot.modes.find(
    (mode) => mode.index === selectedSheet && mode.kind === "mode",
  ) ?? profileModes[0] ?? null;
  const rows = useMemo(() => bindingRows(snapshot, selectedMode), [snapshot, selectedMode]);
  const wholeZoneRows = useMemo(() => {
    if (selectedZone === null) return rows;
    return rows.filter((row) => zonesForRow(row).includes(selectedZone));
  }, [rows, selectedZone]);
  // Avalonia lands the combo zone on a pairing, so it narrows from the start.
  const effectivePick =
    selectedZone === "combo" && (pickedInput === null || zoneOf(pickedInput) !== "combo")
      ? firstMappedCombo(rows)
      : pickedInput;
  const part =
    !wholeZone && effectivePick !== null && selectedZone !== null && zoneOf(effectivePick) === selectedZone
      ? partOf(effectivePick)
      : null;
  const zoneRows = useMemo(() => {
    if (part === null) return wholeZoneRows;
    return wholeZoneRows.filter((row) =>
      row.cells.slice(2, 10).some((cell) => partOf(cell.trim()) === part),
    );
  }, [part, wholeZoneRows]);
  const narrowed = part !== null && effectivePick !== null && wholeZoneRows.length > zoneRows.length;
  const groups = useMemo(() => comboGroups(rows), [rows]);
  const counts = useMemo(() => duplicateCounts(rows), [rows]);
  const groupOf = (row: BindingRow): readonly BindingRow[] => groups.get(row.row) ?? [row];
  const cardRows = zoneRows.filter((row) => groupOf(row)[0]?.row === row.row);
  const selectionBelongsToCurrentSheet = selectedMode?.index === selectedSheet;
  const visibleSelectedRow = selectionBelongsToCurrentSheet ? selectedRow : null;
  const selectedBinding =
    visibleSelectedRow === null ? null : rows.find((row) => row.row === visibleSelectedRow) ?? null;
  const activeGroup = selectedBinding === null ? null : groupOf(selectedBinding);
  const activeRow = activeGroup?.[0] ?? null;
  const deviceView = !onCustomNames && view !== "rows";
  const cardsHere = deviceView ? settings?.deviceCards ?? true : settings?.rowCards ?? false;
  const inputFirst = (settings?.compactCards ?? false) || settings?.cardSentenceStyle === "input_to_output";

  const status: EditorStatus =
    message !== "" ? { kind: "error", text: message } : statusOverride ?? validation;

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
        setStatusOverride(null);
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

  // The inputs live on the card, so in a combo an edit to them lands on every
  // row the card holds, as one undo step.
  const setInput = useCallback(
    (lead: number, members: readonly BindingRow[], column: number, value: string): void => {
      const edit: EditorOp = { op: "set_cell", row: lead, col: column, value };
      if (members.length < 2) {
        void apply([edit]);
        return;
      }
      void apply([
        {
          op: "group",
          ops: [edit, { op: "copy_inputs", from: lead, to: members.map((member) => member.row) }],
        },
      ]);
    },
    [apply],
  );

  const undo = useCallback(async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const current = await client.getProfileSnapshot(snapshot.sessionId);
      // Avalonia keeps Undo enabled and says when there is nothing to take back.
      if (!current.canUndo) {
        onSnapshot(current);
        setMessage("");
        setStatusOverride({ kind: "info", text: t("Main_NothingToUndo") });
        return;
      }
      const next = await client.undoEditor(current.sessionId, current.revision);
      onSnapshot(next);
      setMessage("");
      setStatusOverride({ kind: "ready", text: t("Main_ChangeUndone") });
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot.sessionId, t]);

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
      setStatusOverride({ kind: "ready", text: t("Main_SavedToSavePath", [receipt.name]) });
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot, t]);

  const saveTemplate = async (name: string): Promise<void> => {
    const write = client.saveTemplate;
    if (write === undefined || busy) return;
    if (name.trim() === "") {
      setTemplateOpen(false);
      setStatusOverride({ kind: "warning", text: t("Main_ATemplateNeedsAName") });
      return;
    }
    setBusy(true);
    try {
      const receipt = await write.call(client, snapshot.sessionId, snapshot.revision, name);
      setTemplateOpen(false);
      setStatusOverride({ kind: "ready", text: t("Main_SavedTemplateFileNameFindIt", [receipt.name]) });
    } catch (reason) {
      setTemplateOpen(false);
      setMessage(t("Main_CouldNotSaveTheTemplate", [localizedErrorMessage(asQcmError(reason).payload, t)]));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      if (document.querySelector('[aria-modal="true"]') !== null) return;
      if (event.key === "F1" && onHelp !== undefined) {
        event.preventDefault();
        onHelp();
        return;
      }
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === "z") {
        event.preventDefault();
        void undo();
      } else if (key === "s") {
        event.preventDefault();
        void save();
      } else if (key === "h" && onHelp !== undefined) {
        event.preventDefault();
        onHelp();
      } else if (key === "i" && onInstall !== undefined) {
        event.preventDefault();
        onInstall();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onHelp, onInstall, save, undo]);

  const moveIssueToNotes = (issue: Issue): void => {
    const row = issueRow(issue.cell);
    const column = issue.cell.trim().toUpperCase().charCodeAt(0) - 65;
    if (row === null || column < 2 || column > 9) return;
    const cells = snapshot.grid[row - 1] ?? [];
    const value = cells[column] ?? "";
    if (value.trim() === "") return;
    const existing = (cells[NOTE_COLUMN] ?? "").trim();
    void apply(
      [
        {
          op: "group",
          ops: [
            { op: "set_cell", row, col: NOTE_COLUMN, value: existing === "" ? value : `${existing}; ${value}` },
            { op: "set_cell", row, col: column, value: "" },
          ],
        },
      ],
      () => setStatusOverride({ kind: "info", text: t("Main_MovedTheTextFromI", [issue.cell]) }),
    );
  };

  const focusIssue = (issue: Issue): void => {
    const row = issueRow(issue.cell);
    if (row === null) return;
    const mode = sheetForRow(snapshot, row);
    if (mode?.kind === "mode") setSelectedSheet(mode.index);
    setOnCustomNames(false);
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
    const label = delta < 0 ? t("Modes_MoveNameUp", [mode.name]) : t("Modes_MoveNameDown", [mode.name]);
    void apply([{ op: "move_mode", sheet: mode.index, delta }], () => {
      setSelectedSheet(target);
      setSelectedRow(null);
      setSelectedZone(null);
      // The moved row is rebuilt under the keyboard: focus follows it, onto
      // the same button while it still works, else the mode's name box.
      requestAnimationFrame(() => {
        const row = document.querySelector<HTMLElement>(`[data-testid="modes-dialog-row-${String(target)}"]`);
        const same = [...(row?.querySelectorAll<HTMLButtonElement>("button") ?? [])].find(
          (button) => button.getAttribute("aria-label") === label && !button.disabled,
        );
        (same ?? row?.querySelector<HTMLElement>("input"))?.focus();
      });
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
    setOnCustomNames(false);
    setSelectedRow(null);
    setSelectedZone(null);
    setArmedDelete(null);
    setSelectedRows(new Set());
  };

  const addMode = (): void => {
    const taken = new Set(profileModes.map((mode) => mode.name));
    let n = profileModes.length + 1;
    while (taken.has(`Mode ${String(n)}`)) n += 1;
    void apply([{ op: "add_mode", name: `Mode ${String(n)}` }], (next) => {
      const added = next.modes.findLast((mode) => mode.kind === "mode");
      if (added !== undefined) selectMode(added.index);
    });
  };

  // `AddMappingWithInput`: a new row on this mode with the input already on it.
  const addRowWithInput = (input: string | null, inputWasChosen = true): void => {
    if (selectedMode === null) return;
    const newRow = selectedMode.startRow + 3 + selectedMode.bindingCount;
    const add: EditorOp = { op: "add_row", sheet: selectedMode.index };
    const ops: EditorOp[] =
      input === null ? [add] : [{ op: "group", ops: [add, { op: "set_cell", row: newRow, col: 2, value: input }] }];
    void apply(ops, () => {
      setSelectedRow(newRow);
      if (input !== null) setSelectedZone(zoneOf(input));
      // The output when the input was picked, else the seeded input.
      focusNew.current = { row: newRow, field: inputWasChosen ? 0 : 2 };
    });
  };

  const addRowCommand = (): void => {
    if (onCustomNames) {
      const name = nextDraftName(customNames(snapshot, drafts), t);
      setDrafts(new Map(drafts).set(name, ""));
      setStatusOverride({ kind: "info", text: t("Names_PickTheOutputThenType") });
      return;
    }
    addRowWithInput(null);
  };

  const deleteListRow = (row: number): void => {
    void apply([{ op: "delete_row", row }], () => {
      setSelectedRows((current) => {
        if (!current.has(row)) return current;
        const next = new Set(current);
        next.delete(row);
        return next;
      });
    });
  };

  const toggleSelecting = (): void => {
    const next = !selecting;
    setSelecting(next);
    setSelectedRows(new Set());
    if (next) setStatusOverride({ kind: "info", text: t("Shell_SelectRowsToMoveDeleteOrClear") });
  };

  const moveSelection = (top: boolean): void => {
    const picked = rows.map((row) => row.row).filter((row) => selectedRows.has(row));
    const first = rows[0]?.row;
    const last = rows.at(-1)?.row;
    if (picked.length === 0 || first === undefined || last === undefined) return;
    const ops: EditorOp[] = top
      ? picked.map((row, index) => ({ op: "move_row", from: row, to: first + index }))
      : picked.toReversed().map((row, index) => ({ op: "move_row", from: row, to: last - index }));
    const moved = top
      ? picked.map((_, index) => first + index)
      : picked.map((_, index) => last - picked.length + 1 + index);
    void apply([{ op: "group", ops }], () => setSelectedRows(new Set(moved)));
  };

  const deleteSelection = (): void => {
    const picked = [...selectedRows];
    if (picked.length === 0) return;
    void apply([{ op: "delete_rows", rows: picked }], () => setSelectedRows(new Set()));
  };

  // Stable, so the dialog's focus trap is not rebuilt on every render.
  const closeModes = useCallback((): void => {
    setModesOpen(false);
    setArmedDelete(null);
  }, []);

  const fileName = bareName(snapshot.grid[fileNameRow(snapshot) - 1]?.[0] ?? "");
  const title = snapshot.title || fileName || t("Shell_Profile");
  const locked = busy || busyExternal;

  const chrome = (
    <div className="editorchrome">
      <div className="editorchrome-left">
        <span className="secondary">{t("Shell_Profile")}</span>
        <FileNameBox
          value={fileName}
          disabled={locked}
          onCommit={(bare) => setCell(fileNameRow(snapshot), 0, `${bare}.csv`)}
        />
        <Popover
          label={<Icon name="share" />}
          name={t("Shell_ShareThisProfileAsA")}
          title={t("Shell_ShareAsAGoogleSheet")}
          className="command quiet"
        >
          {() => <div className="share-menu" ref={onShareSlot} />}
        </Popover>
        <span className="editorchrome-rule" aria-hidden="true" />
        {selectedMode !== null && !onCustomNames ? (
          <UnusedButton
            rows={rows}
            modelZones={diagramFor(model).zones}
            disabled={locked}
            onPick={(input) => addRowWithInput(input)}
          />
        ) : null}
        <button
          className="command"
          type="button"
          disabled={locked}
          aria-label={t("Shell_UndoTheLastChangeShortcut")}
          title={t("Shell_UndoCtrlZ")}
          onClick={() => void undo()}
        >
          <Icon name="undo" />
        </button>
        {deviceView ? null : (
          <button
            className="primary add-row"
            type="button"
            disabled={locked}
            title={onCustomNames ? t("Main_AddName") : t("Shell_AddRow")}
            aria-label={onCustomNames ? t("Main_AddARowToThe") : t("Shell_AddANewBindingRow")}
            onClick={addRowCommand}
          >
            {/* `AddRowContent`: the icon carries the "+", so the label drops it. */}
            <Icon name="add" size={16} />
            <span>{(onCustomNames ? t("Main_AddName") : t("Shell_AddRow")).replace(/^\+ /u, "")}</span>
          </button>
        )}
        <button
          className={selecting ? "command quiet active" : "command quiet"}
          type="button"
          aria-pressed={selecting}
          disabled={onCustomNames}
          aria-label={t("Shell_SelectRowsToMoveDeleteOrClear")}
          title={t("Shell_SelectRowsToMoveDeleteOrClear")}
          onClick={toggleSelecting}
        >
          <Icon name="edit" />
        </button>
        {selecting ? (
          <span className="selection-bar">
            <strong>
              {selectedRows.size > 0
                ? t("Main_SelectedRowsCountSelected", [selectedRows.size])
                : t("Shell_SelectRowsToMoveDeleteOrClear")}
            </strong>
            <Popover
              label={<Icon name="drag" />}
              name={t("Shell_MoveTheSelectedRowsTo")}
              title={t("Shell_Move")}
              className="command quiet"
              disabled={locked || selectedRows.size === 0}
            >
              {(close) => (
                <div className="menu-list">
                  <button type="button" className="menu-item" onClick={() => { close(); moveSelection(true); }}>
                    {t("Main_ToTheTop")}
                  </button>
                  <button type="button" className="menu-item" onClick={() => { close(); moveSelection(false); }}>
                    {t("Main_ToTheBottom")}
                  </button>
                </div>
              )}
            </Popover>
            <button
              className="command danger"
              type="button"
              disabled={locked || selectedRows.size === 0}
              aria-label={t("Shell_DeleteTheSelectedRows")}
              title={t("Shell_Delete")}
              onClick={deleteSelection}
            >
              <Icon name="delete" />
            </button>
            <button
              className="command quiet"
              type="button"
              disabled={selectedRows.size === 0}
              aria-label={t("Shell_ClearTheSelection")}
              title={t("Shell_Clear")}
              onClick={() => setSelectedRows(new Set())}
            >
              <Icon name="clear" />
            </button>
          </span>
        ) : null}
      </div>
      <div className="editorchrome-right">
        {client.saveTemplate === undefined ? null : (
          <button
            className="command quiet"
            type="button"
            disabled={locked}
            aria-label={t("Shell_SaveThisProfileAsA")}
            title={t("Shell_SaveAsATemplate")}
            onClick={() => setTemplateOpen(true)}
          >
            <Icon name="template" />
          </button>
        )}
        <button
          className="command"
          type="button"
          disabled={locked}
          aria-label={t("Shell_SaveThisProfileShortcutControl")}
          title={t("Shell_SaveCtrlS")}
          onClick={() => void save()}
        >
          <Icon name="save" />
        </button>
        <button
          className="primary install"
          type="button"
          disabled={locked || onInstall === undefined}
          aria-label={t("Shell_InstallThisProfileOntoA")}
          onClick={onInstall}
        >
          <Icon name="install" />
          <span>{t("Shell_InstallToQuadStick")}</span>
        </button>
        <button
          className="command quiet"
          type="button"
          disabled={onHelp === undefined}
          aria-label={t("Shell_OpenTheQuickGuideShortcut")}
          title={t("Shell_HowItWorksF1")}
          onClick={onHelp}
        >
          <Icon name="help" />
        </button>
      </div>
    </div>
  );

  const viewKey = (key: EditorView, icon: "viewDevice" | "viewRail" | "viewSheet", label: MessageKey, name: MessageKey, tip?: MessageKey) => {
    const active = !onCustomNames && view === key;
    return (
      <button
        className={active ? "switchkey viewkey active" : "switchkey viewkey"}
        type="button"
        aria-pressed={active}
        disabled={onCustomNames}
        aria-label={t(name)}
        title={tip === undefined ? undefined : t(tip)}
        onClick={() => {
          setView(key);
          setSelectedRow(null);
        }}
      >
        <Icon name={icon} size={22} />
        <span>{t(label)}</span>
      </button>
    );
  };

  const wordsOn = !onCustomNames && selectedMode !== null;
  const sidebar = (
    <aside className="modes-panel" aria-label={t("Shell_SelectWhichModeToEdit")}>
      <div className="modes-panel-body">
        <div className="panel-heading-row">
          <InfoButton
            name={t("Shell_WhatIsAModeAnd")}
            title={t("Main_WhatIsAMode")}
            body={t("Main_AModeIsOneFull")}
            className="mode-help"
            label={
              <>
                <h2>{t("Modes_Modes")}</h2>
                <span className="mode-help-mark" aria-hidden="true">?</span>
              </>
            }
          />
          <span className="panel-heading-actions">
            <button
              className="icon quiet"
              type="button"
              disabled={locked}
              aria-label={t("Shell_EditModesAddRenameReorder")}
              title={t("Shell_EditModesAddRenameReorder")}
              onClick={() => setModesOpen(true)}
            >
              <Icon name="edit" size={22} />
            </button>
            <button
              className="icon primary"
              type="button"
              disabled={locked}
              aria-label={t("Modes_AddAMode")}
              title={t("Modes_AddAMode")}
              onClick={addMode}
            >
              <Icon name="add" size={24} />
            </button>
          </span>
        </div>
        <ol className="mode-list">
          {snapshot.modes.filter((mode) => mode.kind !== "infrared").map((mode) => {
            const selected = !onCustomNames && selectedMode?.index === mode.index;
            const label =
              mode.kind === "mode"
                ? t("Main_ModeNumberAndName", [mode.number ?? mode.index + 1, mode.name || t("Main_UnnamedMode")])
                : "Preferences";
            return (
              <li className="mode-row" data-testid={`mode-row-${String(mode.index)}`} key={`mode-${String(mode.index)}`}>
                <button
                  className="mode-select"
                  type="button"
                  aria-current={selected ? "true" : undefined}
                  disabled={mode.kind !== "mode"}
                  onClick={() => selectMode(mode.index)}
                >
                  {label}
                </button>
              </li>
            );
          })}
          <li className="mode-row" key="names">
            <button
              className="mode-select"
              type="button"
              aria-current={onCustomNames ? "true" : undefined}
              onClick={() => {
                setOnCustomNames(true);
                setSelecting(false);
                setSelectedRows(new Set());
              }}
            >
              {t("Names_CustomOutputNames")}
            </button>
          </li>
        </ol>
        <div className="sidebar-config">
          <h3>{t("Shell_Configuration")}</h3>
          <select
            className="model-picker"
            aria-label={t("Shell_WhichQuadStickModelYouHave")}
            value={model}
            disabled={settings === null}
            onChange={(event) => patchSettings({ model: event.currentTarget.value as ModelChoice })}
          >
            {MODEL_CHOICES.map((choice) => (
              <option key={choice.value} value={choice.value}>{choice.label}</option>
            ))}
          </select>
          <span className="device-status" title={connected ? t("Main_WhatALitRowMeans") : undefined}>
            {connected ? (
              <StatusChip status={{ kind: "ready", text: t("Main_QuadStickConnected") }} />
            ) : (
              <span className="status-chip status-info">
                <span className="status-dot" aria-hidden="true" />
                <span>{t("Main_NoQuadStickDetected")}</span>
              </span>
            )}
          </span>
          <div className="zone-list" ref={setZoneListSlot} hidden={!(deviceView && view === "device")} />
        </div>
      </div>
      <div className="sidebar-view">
        <div className="sidebar-view-head">
          <span className="secondary">{t("Shell_View")}</span>
          <InfoButton
            name={t("Shell_HowDeviceViewWorksAnd")}
            title={t("Main_UsingDeviceView")}
            body={t("Main_ClickAnyPartOfThe") + t(MODEL_DESCRIPTION[model])}
          />
        </div>
        {wordsOn ? (
          <div className="sidebar-toggles">
            <button
              className="quiet sidebar-toggle"
              type="button"
              aria-label={t(LABEL_STYLE_TEXT[labelStyle].name)}
              onClick={() => setLabelStyle(((labelStyle + 1) % 3) as LabelStyle)}
            >
              {t(LABEL_STYLE_TEXT[labelStyle].label)}
            </button>
            <button
              className="quiet sidebar-toggle"
              type="button"
              disabled={settings === null}
              aria-label={cardsHere ? t("Main_MappingsReadAsSimpleSentence") : t("Main_MappingsShowTheDetailedEditor")}
              onClick={() => patchSettings(deviceView ? { deviceCards: !cardsHere } : { rowCards: !cardsHere })}
            >
              {cardsHere ? t("Main_DetailedEditor") : t("Main_SimpleCards")}
            </button>
            <button
              className={settings?.compactCards === true ? "quiet sidebar-toggle format active" : "quiet sidebar-toggle format"}
              type="button"
              aria-pressed={settings?.compactCards === true}
              disabled={settings === null}
              aria-label={settings?.compactCards === true ? t("Main_MappingsAreOnOneLine") : t("Main_MappingsReadAsAFull")}
              title={t("Main_FitEachMappingOnOne")}
              onClick={() => patchSettings({ compactCards: settings?.compactCards !== true })}
            >
              <Icon name="format" size={20} />
            </button>
          </div>
        ) : null}
        <div className="switchtrack" role="group" aria-label={t("Main_UsingDeviceView")}>
          {viewKey("device", "viewDevice", "Shell_Device", "Shell_ShowTheMappingsOnA", "Shell_SeeTheMappingsOnA")}
          {viewKey("parts", "viewRail", "Shell_Parts", "Shell_ShowThePartsAsA")}
          {viewKey("rows", "viewSheet", "Shell_Rows", "Shell_ShowTheMappingsAsA")}
        </div>
      </div>
    </aside>
  );

  const card = (row: BindingRow) => {
    const group = groupOf(row);
    return (
      <SentenceCard
        key={row.row}
        row={row}
        together={group.slice(1)}
        counts={counts}
        zoneId={zoneIdForRow(row)}
        selected={group.some((member) => member.row === visibleSelectedRow)}
        live={group.some((member) => liveRows.has(member.row))}
        inputFirst={inputFirst}
        compact={settings?.compactCards === true}
        onSelect={() => setSelectedRow(row.row)}
      />
    );
  };

  const inspector = (lead: BindingRow, members: readonly BindingRow[]) => (
    // The open mapping is the detailed editor in the card's place (Avalonia's
    // accordion), so it carries the row's handle for focus from a problem.
    <div
      className="mapping-editor"
      key={`edit-${String(lead.row)}`}
      data-binding-row={lead.row}
      data-testid={`binding-row-${String(lead.row)}`}
      tabIndex={-1}
    >
      <div className="row-actions">
        <button type="button" disabled={locked || rows[0]?.row === lead.row} aria-label={t("Review_MoveItEarlier")} onClick={() => void apply([{ op: "move_row", from: lead.row, to: lead.row - 1 }], () => setSelectedRow(lead.row - 1))}>↑</button>
        <button type="button" disabled={locked || rows.at(-1)?.row === lead.row} aria-label={t("Review_MoveItLater")} onClick={() => void apply([{ op: "move_row", from: lead.row, to: lead.row + 1 }], () => setSelectedRow(lead.row + 1))}>↓</button>
        <button
          type="button"
          disabled={locked}
          onClick={() => {
            // The card holds every output on these inputs, so its trash takes
            // the whole combo. Each output has its own.
            const op: EditorOp =
              members.length > 1
                ? { op: "delete_rows", rows: members.map((member) => member.row) }
                : { op: "delete_row", row: lead.row };
            void apply([op], () => setSelectedRow(null));
          }}
        >
          {t("Shell_Delete")}
        </button>
      </div>
      <BindingInspector
        key={`${String(snapshot.revision)}-${String(lead.row)}`}
        row={lead.row}
        cells={lead.cells}
        members={members}
        counts={counts}
        mappingNumber={Math.max(1, cardRows.findIndex((row) => row.row === lead.row) + 1)}
        disabled={locked}
        onSetCell={setCell}
        onSetInput={(column, value) => setInput(lead.row, members, column, value)}
        onAddOutput={
          selectedMode === null
            ? undefined
            : () =>
                void apply(
                  [{ op: "add_combo_output", sheet: selectedMode.index, from: lead.row }],
                  () => setSelectedRow(lead.row),
                )
        }
        onRemoveOutput={(dropRow) => {
          const keep = members.find((member) => member.row !== dropRow);
          if (keep === undefined) return;
          const kept = keep.row > dropRow ? keep.row - 1 : keep.row;
          void apply([{ op: "delete_row", row: dropRow }], () => setSelectedRow(kept));
        }}
      />
    </div>
  );

  let main;
  if (onCustomNames) {
    main = (
      <section className="rows-canvas" aria-label={t("Names_CustomOutputNames")}>
        <CustomNamesTable
          snapshot={snapshot}
          drafts={drafts}
          busy={locked}
          onDrafts={setDrafts}
          onApply={(ops, after) => void apply(ops, after)}
          onStatus={setStatusOverride}
        />
      </section>
    );
  } else if (view === "rows") {
    main = (
      <section className="rows-canvas" aria-label={t("Shell_Rows")}>
        {cardsHere ? (
          <div className="binding-list">
            {rows.filter((row) => groupOf(row)[0]?.row === row.row).map((row) =>
              row.row === activeRow?.row ? inspector(row, groupOf(row)) : card(row),
            )}
          </div>
        ) : (
          <RowsGrid
            rows={rows}
            issues={snapshot.issues}
            counts={counts}
            labelStyle={labelStyle}
            disabled={locked}
            liveRows={liveRows}
            selecting={selecting}
            selectedRows={selectedRows}
            onToggleRow={(row) =>
              setSelectedRows((current) => {
                const next = new Set(current);
                if (next.has(row)) next.delete(row);
                else next.add(row);
                return next;
              })
            }
            onApply={(ops) => void apply(ops)}
            onDeleteRow={deleteListRow}
          />
        )}
        <datalist id="qcm-row-outputs">
          {[...KNOWN_OUTPUTS].map((output) => <option key={output} value={output}>{output}</option>)}
        </datalist>
        <datalist id="qcm-row-inputs">
          {PICKER_INPUTS.map((input) => <option key={input} value={input}>{input}</option>)}
        </datalist>
        <datalist id="qcm-row-functions">
          {FUNCTIONS.map((fn) => <option key={fn} value={fn}>{fn}</option>)}
        </datalist>
      </section>
    );
  } else {
    main = (
      <div className="device-workspace">
        <section className="device-canvas" aria-label={view === "device" ? t("Shell_Device") : t("Shell_Parts")}>
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
            onLiveHeard={setLiveHeard}
            pickedInput={pickedInput}
            onPickInput={pickInput}
            zoneListSlot={view === "device" ? zoneListSlot : null}
          />
        </section>

        <aside className="mapping-panel" aria-labelledby="mapping-title">
          {selectedZone === null ? (
            <>
              <h2 id="mapping-title" className="visually-hidden">{t("Shell_Configuration")}</h2>
              <p className="empty-copy">{t("Main_NothingSelectedNNPickA")}</p>
            </>
          ) : (
            <>
              <div className="mapping-panel-head">
                <h2 id="mapping-title">{t(ZONE_TITLES[selectedZone])}</h2>
                <span className={zoneRows.length === 0 ? "mapping-count muted" : "mapping-count accent"}>
                  {zoneRows.length === 0 ? t("Main_NotMapped") : plural("Count_Mapping", zoneRows.length, [zoneRows.length])}
                </span>
                {selectedMode !== null && selectedZone !== "unset" ? (
                  <button
                    type="button"
                    className="mapping-add"
                    disabled={locked}
                    aria-label={t("Main_AddANewMappingFor", [t(ZONE_TITLES[selectedZone])])}
                    onClick={() =>
                      // The part clicked on the picture, when it belongs here,
                      // or the zone's own default, like Avalonia.
                      addRowWithInput(
                        effectivePick !== null && zoneOf(effectivePick) === selectedZone
                          ? effectivePick
                          : ZONE_DEFAULT_INPUT[selectedZone],
                        false,
                      )
                    }
                  >
                    {t("Shell_AddRow")}
                  </button>
                ) : null}
              </div>
              {narrowed && effectivePick !== null ? (
                <p className="narrowed-note">
                  <span>{t("Main_NarrowedToPart", [partLabel(effectivePick, t)])}</span>
                  <button
                    type="button"
                    className="quiet"
                    aria-label={t("Main_ShowEveryMappingHere")}
                    onClick={() => {
                      setPickedInput(null);
                      setWholeZone(true);
                    }}
                  >
                    {t("Main_ShowAllCount", [wholeZoneRows.length])}
                  </button>
                </p>
              ) : null}
              {zoneRows.length === 0 ? (
                <p className="empty-copy">{t("Main_NoInputYet")}</p>
              ) : cardsHere ? (
                <div className="binding-list">
                  {cardRows.map((row) => (row.row === activeRow?.row ? inspector(row, groupOf(row)) : card(row)))}
                </div>
              ) : (
                <div className="binding-list">{cardRows.map((row) => inspector(row, groupOf(row)))}</div>
              )}
            </>
          )}
        </aside>
      </div>
    );
  }

  return (
    <section className="editor-workspace" aria-labelledby="editor-title">
      <h1 id="editor-title" className="visually-hidden">{title}</h1>
      {snapshot.dirty ? (
        <span className="visually-hidden" role="img" aria-label={t("Main_ThisProfileHasUnsavedChanges")}>*</span>
      ) : null}
      {chrome}
      <div className="editor-plate">
        {sidebar}
        {main}
      </div>
      <ProblemsDock
        issues={snapshot.issues}
        status={status}
        onFocusIssue={focusIssue}
        onStatus={(next) => {
          setStatusOverride(next);
          setMessage("");
        }}
        onMoveToNotes={moveIssueToNotes}
      />
      <ModesDialog
        open={modesOpen}
        snapshot={snapshot}
        busy={locked}
        armedDelete={armedDelete}
        onClose={closeModes}
        onApply={(ops, after) => void apply(ops, after)}
        onMove={moveMode}
        onDelete={deleteMode}
        onSelect={selectMode}
        canMove={(mode, delta) => adjacentMovableSheet(snapshot, mode.index, delta) !== null}
      />
      <SaveTemplateDialog
        open={templateOpen}
        suggested={fileName}
        busy={locked}
        onSave={(name) => void saveTemplate(name)}
        onClose={() => setTemplateOpen(false)}
      />
    </section>
  );
}
