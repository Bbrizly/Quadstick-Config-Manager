/**
 * The client the UI is developed against.
 *
 * Runs in a plain browser with no Tauri, no Rust and no files, so every screen
 * can be built, storybooked and tested before the native command it will
 * eventually call exists. It is a fake, and it says so: it holds a small grid in
 * memory and applies the handful of operations the editor UI needs.
 *
 * What it is not is a second implementation of the profile format. It never
 * validates, never normalizes and never writes CSV. Anything that decides what a
 * profile means belongs in Rust, and a mock that started guessing would be the
 * one place a wrong answer could reach somebody's controller unreviewed.
 *
 * What it does reproduce exactly is the contract: revisions move the way the
 * native side moves them, a stale revision is refused with the same code, a
 * batch is all or nothing, and a cancelled dialog is a `null` rather than a
 * throw.
 */

import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  EditorOp,
  EditorSnapshot,
  Issue,
  Mode,
  ProfileSource,
  QcmErrorPayload,
  RecoveryAction,
  SaveReceipt,
  SettingsPatch,
} from "./contracts";
import { ERROR_CODES, INTERFACE_SCALES } from "./contracts";
import { QcmCommandError, type QcmClient } from "./qcmClient";

/**
 * A two-mode profile, the smallest thing that shows the editor doing its job.
 *
 * Both modes are called Racing on purpose. Two modes sharing a name is normal,
 * because the firmware tells them apart by counting `Profile Name` segments and
 * never reads the name, so the UI has to show the number beside one.
 */
const TEMPLATE: readonly (readonly string[])[] = [
  ["Profile Name", "", "Racing", "", "", "", "", "", "", "", "Comments"],
  ["untitled.csv", "", "", "", "", "", "", "", "", "", ""],
  ["PlayStation Outputs", "Function", "usb", "", "", "", "", "", "", "", ""],
  ["cross", "normal", "sip", "", "", "", "", "", "", "", ""],
  ["circle", "normal", "puff", "", "", "", "", "", "", "", ""],
  ["", "", "", "", "", "", "", "", "", "", ""],
  ["Profile Name", "", "Racing", "", "", "", "", "", "", "", "Comments"],
  ["", "", "", "", "", "", "", "", "", "", ""],
  ["PlayStation Outputs", "Function", "usb", "", "", "", "", "", "", "", ""],
  ["square", "normal", "hard sip", "", "", "", "", "", "", "", ""],
];

const DEFAULT_SETTINGS: AppSettings = {
  revision: 1,
  model: "fps",
  theme: "system",
  language: "system",
  interfaceScalePercent: 100,
  reduceMotion: false,
  rememberWindow: true,
  deviceCards: true,
  pickerGrouping: "detailed",
  tutorialSeen: false,
};

interface MockSession {
  readonly id: string;
  revision: number;
  dirty: boolean;
  grid: string[][];
  undo: string[][][];
  saveTarget: string | null;
  source: ProfileSource;
  title: string;
}

/** What the next dialog does. Unset means "the user picks a file and accepts". */
type PickerAnswer = { readonly cancelled: true } | { readonly cancelled: false; readonly name: string };

function fail(
  code: string,
  message: string,
  action: RecoveryAction,
  recoverable = true,
): QcmCommandError {
  const payload: QcmErrorPayload = {
    code,
    message,
    recoverable,
    action: { kind: action },
    operationId: null,
    targetState: null,
    backup: null,
  };
  return new QcmCommandError(payload);
}

function clone(grid: readonly (readonly string[])[]): string[][] {
  return grid.map((row) => [...row]);
}

/**
 * Mirrors the native projection: only `Profile Name` sheets get a number.
 *
 * The mode name is column C of the keyword row and the channel is column C of
 * the row two below it, which is where the parser reads them from. This is the
 * one piece of format knowledge the fake carries, because a mode rail with no
 * names in it teaches the UI nothing.
 */
function modesOf(grid: readonly (readonly string[])[]): Mode[] {
  const modes: Mode[] = [];
  let counted = 0;
  grid.forEach((row, index) => {
    if (row[0] !== "Profile Name") {
      return;
    }
    counted += 1;
    modes.push({
      index: modes.length,
      number: counted,
      kind: "mode",
      name: row[2] ?? "",
      channel: grid[index + 2]?.[2] ?? "",
      startRow: index + 1,
      bindingCount: bindingsFrom(grid, index),
    });
  });
  return modes;
}

function bindingsFrom(grid: readonly (readonly string[])[], start: number): number {
  let count = 0;
  for (let row = start + 3; row < grid.length; row += 1) {
    const first = grid[row]?.[0] ?? "";
    if (first === "" || first === "Profile Name") {
      break;
    }
    count += 1;
  }
  return count;
}

/**
 * The fake native side.
 *
 * The `will*` methods are the test and story seam: they say what the user does
 * in the next dialog, or make the next save fail. With none of them called it
 * behaves like a machine where everything works, which is what a browser
 * running the UI on its own needs.
 */
export class MockQcmClient implements QcmClient {
  #sessions = new Map<string, MockSession>();
  #nextSession = 1;
  #settings: AppSettings = DEFAULT_SETTINGS;
  #openAnswers: PickerAnswer[] = [];
  #saveAsAnswers: PickerAnswer[] = [];
  #failNextSave = false;
  #dialogsOpened = 0;

  /** What the next Open dialog does. */
  willOpen(name: string): void {
    this.#openAnswers.push({ cancelled: false, name });
  }

  willCancelOpen(): void {
    this.#openAnswers.push({ cancelled: true });
  }

  /** What the next Save As dialog does. */
  willSaveAs(name: string): void {
    this.#saveAsAnswers.push({ cancelled: false, name });
  }

  willCancelSaveAs(): void {
    this.#saveAsAnswers.push({ cancelled: true });
  }

  /** Make the next write fail with nothing written. */
  willFailNextSave(): void {
    this.#failNextSave = true;
  }

  /** How many dialogs the user was actually shown. */
  get dialogsOpened(): number {
    return this.#dialogsOpened;
  }

  getAppSnapshot(): Promise<AppSnapshot> {
    return Promise.resolve({
      version: "0.1.0-mock",
      platform: "browser",
      capabilities: {
        profileEditing: true,
        deviceInstall: false,
        liveInput: false,
        communityCatalog: false,
        googleBackup: false,
        agent: false,
      },
      settings: this.#settings,
    });
  }

  getSettings(): Promise<AppSettings> {
    return Promise.resolve(this.#settings);
  }

  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings> {
    if (expectedRevision !== this.#settings.revision) {
      return Promise.reject(this.#staleSettings(expectedRevision));
    }
    if (patch.interfaceScalePercent !== undefined) {
      const offered: readonly number[] = INTERFACE_SCALES;
      if (!offered.includes(patch.interfaceScalePercent)) {
        return Promise.reject(
          fail(
            ERROR_CODES.requestOutOfRange,
            "interface scale is not one of the values this setting takes.",
            "retry",
          ),
        );
      }
    }
    const next = { ...this.#settings, ...stripUndefined(patch) };
    const changed = Object.keys(next).some(
      (key) =>
        key !== "revision" &&
        next[key as keyof AppSettings] !== this.#settings[key as keyof AppSettings],
    );
    if (changed) {
      this.#settings = { ...next, revision: this.#settings.revision + 1 };
    }
    return Promise.resolve(this.#settings);
  }

  newProfile(name: string): Promise<EditorSnapshot> {
    const grid = clone(TEMPLATE);
    const nameRow = grid[1];
    if (nameRow !== undefined) {
      nameRow[0] = name;
    }
    return Promise.resolve(this.#open({ kind: "new" }, null, name, grid));
  }

  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {
    this.#dialogsOpened += 1;
    const answer = this.#openAnswers.shift() ?? { cancelled: false, name: "Racing.csv" };
    if (answer.cancelled) {
      return Promise.resolve(null);
    }
    const grid = clone(TEMPLATE);
    const nameRow = grid[1];
    if (nameRow !== undefined) {
      nameRow[0] = answer.name;
    }
    return Promise.resolve(
      this.#open({ kind: "local", name: answer.name }, answer.name, answer.name, grid),
    );
  }

  applyEditorOps(
    sessionId: string,
    expectedRevision: number,
    ops: readonly EditorOp[],
  ): Promise<EditorSnapshot> {
    let session: MockSession;
    try {
      session = this.#checked(sessionId, expectedRevision);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (ops.length === 0) {
      return Promise.resolve(snapshot(session));
    }

    // All or nothing. Applied to a copy, so a batch that fails halfway leaves
    // the revision and the dirty flag where they started rather than somewhere
    // in between, which is exactly the lie the revision contract exists to stop.
    const history = session.undo.map(clone);
    let candidate = clone(session.grid);
    for (const op of ops) {
      const applied = apply(candidate, op);
      if (applied === null) {
        return Promise.reject(
          fail(
            ERROR_CODES.profileOperationRejected,
            "That change could not be made, so none of the batch was applied.",
            "retry",
          ),
        );
      }
      history.push(clone(candidate));
      candidate = applied;
    }

    session.undo = history;
    session.grid = candidate;
    session.revision += ops.length;
    session.dirty = true;
    return Promise.resolve(snapshot(session));
  }

  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot> {
    let session: MockSession;
    try {
      session = this.#checked(sessionId, expectedRevision);
    } catch (reason) {
      return Promise.reject(reason);
    }
    const previous = session.undo.pop();
    if (previous === undefined) {
      return Promise.reject(
        fail(ERROR_CODES.profileNothingToUndo, "There is nothing left to undo.", "retry"),
      );
    }
    session.grid = previous;
    session.revision += 1;
    session.dirty = true;
    return Promise.resolve(snapshot(session));
  }

  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt> {
    let session: MockSession;
    try {
      session = this.#checked(sessionId, expectedRevision);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (session.saveTarget === null) {
      return Promise.reject(
        fail(
          ERROR_CODES.profileNeedsSaveTarget,
          "This profile has not been saved anywhere yet.",
          "choose_save_location",
        ),
      );
    }
    return this.#write(session, session.saveTarget);
  }

  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null> {
    let session: MockSession;
    try {
      session = this.#checked(sessionId, expectedRevision);
    } catch (reason) {
      // Checked before the dialog, the way the native command does it: a
      // refusal that could be made first must not cost somebody a file picker.
      return Promise.reject(reason);
    }
    this.#dialogsOpened += 1;
    const answer = this.#saveAsAnswers.shift() ?? {
      cancelled: false,
      name: session.saveTarget ?? "Untitled.csv",
    };
    if (answer.cancelled) {
      return Promise.resolve(null);
    }
    session.saveTarget = answer.name;
    return this.#write(session, answer.name);
  }

  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome> {
    const session = this.#sessions.get(sessionId);
    if (session === undefined) {
      return Promise.reject(this.#unknownSession());
    }
    if (disposition === "if_clean" && session.dirty) {
      return Promise.resolve({ kind: "keptOpenUnsavedChanges" });
    }
    if (disposition === "save") {
      return this.saveProfile(sessionId, session.revision).then((receipt) => {
        this.#sessions.delete(sessionId);
        return { kind: "savedAndClosed", receipt } as const;
      });
    }
    this.#sessions.delete(sessionId);
    return Promise.resolve({ kind: "closed" });
  }

  #write(session: MockSession, name: string): Promise<SaveReceipt> {
    if (this.#failNextSave) {
      this.#failNextSave = false;
      return Promise.reject(
        fail(ERROR_CODES.storageFull, "The drive is full.", "free_space_on_device"),
      );
    }
    session.dirty = false;
    return Promise.resolve({
      sessionId: session.id,
      revision: session.revision,
      name,
      bytes: session.grid.map((row) => row.join(",")).join("\n").length,
    });
  }

  #open(
    source: ProfileSource,
    saveTarget: string | null,
    title: string,
    grid: string[][],
  ): EditorSnapshot {
    // Ids are never reused, so a call that arrives late for a closed profile
    // fails instead of landing on a new one.
    const id = `session-${String(this.#nextSession)}`;
    this.#nextSession += 1;
    const session: MockSession = {
      id,
      revision: 1,
      dirty: false,
      grid,
      undo: [],
      saveTarget,
      source,
      title,
    };
    this.#sessions.set(id, session);
    return snapshot(session);
  }

  #checked(sessionId: string, expectedRevision: number): MockSession {
    const session = this.#sessions.get(sessionId);
    if (session === undefined) {
      throw this.#unknownSession();
    }
    if (session.revision !== expectedRevision) {
      throw fail(
        ERROR_CODES.profileRevisionConflict,
        `This profile changed since the edit was made (expected revision ${String(expectedRevision)}, found ${String(session.revision)}).`,
        "reopen_profile",
      );
    }
    return session;
  }

  #unknownSession(): QcmCommandError {
    return fail(
      ERROR_CODES.profileUnknownSession,
      "That profile is no longer open.",
      "reopen_profile",
    );
  }

  #staleSettings(expectedRevision: number): QcmCommandError {
    return fail(
      ERROR_CODES.profileRevisionConflict,
      `Settings changed since this was chosen (expected revision ${String(expectedRevision)}, found ${String(this.#settings.revision)}).`,
      "reopen_profile",
    );
  }
}

function snapshot(session: MockSession): EditorSnapshot {
  const issues: Issue[] = [];
  return {
    sessionId: session.id,
    revision: session.revision,
    dirty: session.dirty,
    canUndo: session.undo.length > 0,
    source: session.source,
    saveTarget: session.saveTarget,
    title: session.title,
    grid: clone(session.grid),
    issues,
    errorCount: 0,
    modes: modesOf(session.grid),
  };
}

/** `null` for an operation this fake refuses, matching the native rejection. */
function apply(grid: string[][], op: EditorOp): string[][] | null {
  const next = clone(grid);
  switch (op.op) {
    case "set_cell": {
      if (op.row === 0) {
        return null;
      }
      while (next.length < op.row) {
        next.push([]);
      }
      const row = next[op.row - 1];
      if (row === undefined) {
        return null;
      }
      while (row.length <= op.col) {
        row.push("");
      }
      row[op.col] = op.value;
      return next;
    }
    case "set_output": {
      const row = next[op.row - 1];
      if (row === undefined) {
        return null;
      }
      row[0] = op.token;
      return next;
    }
    case "delete_row": {
      if (op.row === 0 || op.row > next.length) {
        return null;
      }
      next.splice(op.row - 1, 1);
      return next;
    }
    case "rename_mode": {
      const mode = modesOf(next)[op.sheet];
      if (mode === undefined) {
        return null;
      }
      // startRow is one-based, and the name lives in column C of the keyword
      // row itself.
      const keyword = next[mode.startRow - 1];
      if (keyword === undefined) {
        return null;
      }
      keyword[2] = op.name;
      return next;
    }
    // Everything else is a real editor operation this fake deliberately does
    // not model. The UI that needs one develops against the native command.
    default:
      return null;
  }
}

function stripUndefined(patch: SettingsPatch): Partial<AppSettings> {
  const kept: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(patch)) {
    if (value !== undefined) {
      kept[key] = value;
    }
  }
  return kept as Partial<AppSettings>;
}
