/**
 * Browser-side contract fake.
 *
 * It mirrors command semantics that UI code depends on: revisions, cancellation,
 * opaque device identity, stale generations and one-shot destructive plans. It
 * deliberately does not parse, validate or normalize QuadStick CSV; Rust owns
 * those rules.
 */

import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  DeletePlan,
  DeleteReceipt,
  DeviceLibrarySnapshot,
  DevicePresenceSnapshot,
  DeviceProfileEntry,
  DeviceSummary,
  EditorOp,
  EditorSnapshot,
  InstallPlan,
  InstallReceipt,
  Issue,
  LedColour,
  Mode,
  ProfileSource,
  QcmErrorPayload,
  RecoveryAction,
  SaveReceipt,
  SettingsPatch,
} from "./contracts";
import { ERROR_CODES, INTERFACE_SCALES } from "./contracts";
import { QcmCommandError, type QcmClient } from "./qcmClient";

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

interface MockDevice {
  readonly id: string;
  generation: number;
  displayName: string;
  writable: boolean;
  readonly files: Map<string, string>;
}

interface MockInstallPlan {
  readonly planId: string;
  readonly deviceId: string;
  readonly generation: number;
  readonly target: string;
  readonly text: string;
  readonly confirmationId: string | null;
}

interface MockDeletePlan {
  readonly planId: string;
  readonly deviceId: string;
  readonly generation: number;
  readonly name: string;
  readonly confirmationId: string;
}

type PickerAnswer = { readonly cancelled: true } | { readonly cancelled: false; readonly name: string };
type DevicePickerAnswer =
  | { readonly cancelled: true }
  | { readonly cancelled: false; readonly label: string };

function fail(
  code: string,
  message: string,
  action: RecoveryAction,
  recoverable = true,
  operationId: string | null = null,
): QcmCommandError {
  const payload: QcmErrorPayload = {
    code,
    message,
    recoverable,
    action: { kind: action },
    operationId,
    targetState: null,
    backup: null,
  };
  return new QcmCommandError(payload);
}

function clone(grid: readonly (readonly string[])[]): string[][] {
  return grid.map((row) => [...row]);
}

function modesOf(grid: readonly (readonly string[])[]): Mode[] {
  const modes: Mode[] = [];
  let counted = 0;
  grid.forEach((row, index) => {
    if (row[0] !== "Profile Name") return;
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
    if (first === "" || first === "Profile Name") break;
    count += 1;
  }
  return count;
}

const MOCK_LIGHTS: readonly (readonly LedColour[])[] = [
  ["purple", "grey", "grey", "grey", "grey"],
  ["grey", "purple", "grey", "grey", "grey"],
  ["grey", "grey", "purple", "grey", "grey"],
  ["grey", "grey", "grey", "purple", "grey"],
  ["grey", "grey", "grey", "grey", "purple"],
];

function deviceNameIsPlain(name: string): boolean {
  return (
    name.length > 4 &&
    name.length <= 255 &&
    name === name.trim() &&
    !name.startsWith(".") &&
    name.toLowerCase().endsWith(".csv") &&
    !/[\\/:\u0000-\u001f]/u.test(name)
  );
}

function gridText(grid: readonly (readonly string[])[]): string {
  return grid.map((row) => row.join(",")).join("\n");
}

export class MockQcmClient implements QcmClient {
  #sessions = new Map<string, MockSession>();
  #nextSession = 1;
  #settings: AppSettings = DEFAULT_SETTINGS;
  #openAnswers: PickerAnswer[] = [];
  #saveAsAnswers: PickerAnswer[] = [];
  #failNextSave = false;
  #dialogsOpened = 0;

  #devices = new Map<string, MockDevice>();
  #nextDevice = 1;
  #nextGeneration = 1;
  #nextPlan = 1;
  #nextConfirmation = 1;
  #nextBackup = 1;
  #installPlans = new Map<string, MockInstallPlan>();
  #deletePlans = new Map<string, MockDeletePlan>();
  #devicePickerAnswers: DevicePickerAnswer[] = [];
  #lastDeviceSignature = "";

  willOpen(name: string): void {
    this.#openAnswers.push({ cancelled: false, name });
  }

  willCancelOpen(): void {
    this.#openAnswers.push({ cancelled: true });
  }

  willSaveAs(name: string): void {
    this.#saveAsAnswers.push({ cancelled: false, name });
  }

  willCancelSaveAs(): void {
    this.#saveAsAnswers.push({ cancelled: true });
  }

  willFailNextSave(): void {
    this.#failNextSave = true;
  }

  get dialogsOpened(): number {
    return this.#dialogsOpened;
  }

  /** Test/story seam: add a mounted QuadStick without inventing a host path. */
  plugDevice(displayName = "QUADSTICK", files: Readonly<Record<string, string>> = {}): string {
    const id = `dev-${String(this.#nextDevice)}`;
    this.#nextDevice += 1;
    const generation = this.#nextGeneration;
    this.#nextGeneration += 1;
    const stored = new Map<string, string>([["default.csv", "QuadStick Configuration File,\n"]]);
    for (const [name, text] of Object.entries(files)) stored.set(name, text);
    this.#devices.set(id, { id, generation, displayName, writable: true, files: stored });
    return id;
  }

  unplugDevice(deviceId: string): void {
    this.#devices.delete(deviceId);
  }

  /** Same opaque device after a remount, but a new generation. */
  remountDevice(deviceId: string): void {
    const device = this.#devices.get(deviceId);
    if (device === undefined) return;
    device.generation = this.#nextGeneration;
    this.#nextGeneration += 1;
  }

  setDeviceFile(deviceId: string, name: string, text: string): void {
    const device = this.#devices.get(deviceId);
    if (device !== undefined) device.files.set(name, text);
  }

  setDeviceWritable(deviceId: string, writable: boolean): void {
    const device = this.#devices.get(deviceId);
    if (device !== undefined) device.writable = writable;
  }

  willCancelDeviceFolder(): void {
    this.#devicePickerAnswers.push({ cancelled: true });
  }

  willChooseDeviceFolder(label = "QUADSTICK"): void {
    this.#devicePickerAnswers.push({ cancelled: false, label });
  }

  getAppSnapshot(): Promise<AppSnapshot> {
    return Promise.resolve({
      version: "0.1.0-mock",
      platform: "browser",
      capabilities: {
        profileEditing: true,
        deviceInstall: true,
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
    if (changed) this.#settings = { ...next, revision: this.#settings.revision + 1 };
    return Promise.resolve(this.#settings);
  }

  newProfile(name: string): Promise<EditorSnapshot> {
    const grid = clone(TEMPLATE);
    const nameRow = grid[1];
    if (nameRow !== undefined) nameRow[0] = name;
    return Promise.resolve(this.#open({ kind: "new" }, null, name, grid));
  }

  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {
    this.#dialogsOpened += 1;
    const answer = this.#openAnswers.shift() ?? { cancelled: false, name: "Racing.csv" };
    if (answer.cancelled) return Promise.resolve(null);
    const grid = clone(TEMPLATE);
    const nameRow = grid[1];
    if (nameRow !== undefined) nameRow[0] = answer.name;
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
    if (ops.length === 0) return Promise.resolve(snapshot(session));

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
      return Promise.reject(reason);
    }
    this.#dialogsOpened += 1;
    const answer = this.#saveAsAnswers.shift() ?? {
      cancelled: false,
      name: session.saveTarget ?? "Untitled.csv",
    };
    if (answer.cancelled) return Promise.resolve(null);
    session.saveTarget = answer.name;
    return this.#write(session, answer.name);
  }

  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome> {
    const session = this.#sessions.get(sessionId);
    if (session === undefined) return Promise.reject(this.#unknownSession());
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

  listDevices(): Promise<DevicePresenceSnapshot> {
    return Promise.resolve({ devices: this.#deviceSummaries(), changed: false });
  }

  refreshDevices(): Promise<DevicePresenceSnapshot> {
    const devices = this.#deviceSummaries();
    const signature = devices
      .map((device) => `${device.deviceId}:${String(device.generation)}:${String(device.writable)}`)
      .join("|");
    const changed = signature !== this.#lastDeviceSignature;
    this.#lastDeviceSignature = signature;
    return Promise.resolve({ devices, changed });
  }

  chooseDeviceFolder(): Promise<DevicePresenceSnapshot | null> {
    const answer = this.#devicePickerAnswers.shift() ?? { cancelled: false, label: "QUADSTICK" };
    if (answer.cancelled) return Promise.resolve(null);
    this.plugDevice(answer.label);
    return this.refreshDevices();
  }

  getDeviceLibrary(deviceId: string): Promise<DeviceLibrarySnapshot> {
    let device: MockDevice;
    try {
      device = this.#device(deviceId);
    } catch (reason) {
      return Promise.reject(reason);
    }
    const names = [...device.files.keys()]
      .filter((name) => !name.startsWith(".") && name.toLowerCase().endsWith(".csv"))
      .filter((name) => name.toLowerCase() !== "prefs.csv")
      .toSorted((a, b) => {
        if (a.toLowerCase() === "default.csv") return -1;
        if (b.toLowerCase() === "default.csv") return 1;
        return a.localeCompare(b, "en", { sensitivity: "base" });
      });
    const files: DeviceProfileEntry[] = names.map((name, index) => ({
      name,
      fileNumber: index + 1,
      lights: MOCK_LIGHTS[index] ?? [],
      protected: name.toLowerCase() === "default.csv",
    }));
    return Promise.resolve({
      deviceId,
      generation: device.generation,
      files,
      protectedFiles: ["default.csv", "prefs.csv"],
      unnameable: 0,
    });
  }

  prepareInstall(sessionId: string, deviceId: string): Promise<InstallPlan> {
    const session = this.#sessions.get(sessionId);
    if (session === undefined) return Promise.reject(this.#unknownSession());
    let device: MockDevice;
    try {
      device = this.#device(deviceId);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (!device.writable) {
      return Promise.reject(
        fail(ERROR_CODES.storageReadOnly, "The QuadStick is read-only.", "make_device_writable"),
      );
    }
    const target = session.grid[1]?.[0] ?? "";
    if (!deviceNameIsPlain(target)) {
      return Promise.reject(
        fail(ERROR_CODES.storageNameRejected, "That is not a safe device file name.", "choose_another_name"),
      );
    }
    const planId = this.#mintPlan();
    const lower = target.toLowerCase();
    const confirmationKind =
      lower === "default.csv"
        ? "overwrite_default_csv"
        : lower === "prefs.csv"
          ? "overwrite_device_preferences"
          : null;
    const confirmationId = confirmationKind === null ? null : this.#mintConfirmation();
    const text = gridText(session.grid);
    const plan: MockInstallPlan = {
      planId,
      deviceId,
      generation: device.generation,
      target,
      text,
      confirmationId,
    };
    this.#installPlans.set(planId, plan);
    return Promise.resolve({
      planId,
      target,
      bytes: text.length,
      confirmation:
        confirmationId === null || confirmationKind === null
          ? null
          : {
              confirmationId,
              kind: confirmationKind,
              summary:
                lower === "default.csv"
                  ? "Replace default.csv on the QuadStick. It is the profile the device falls back to."
                  : "Replace prefs.csv on the QuadStick. It changes device-wide settings.",
            },
    });
  }

  commitInstall(planId: string, confirmationId?: string): Promise<InstallReceipt> {
    const plan = this.#installPlans.get(planId);
    if (plan === undefined) {
      return Promise.reject(
        fail(ERROR_CODES.requestOutOfRange, "That install plan is no longer available.", "retry", true, planId),
      );
    }
    this.#installPlans.delete(planId);
    let device: MockDevice;
    try {
      device = this.#device(plan.deviceId, plan.generation);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (plan.confirmationId !== null && confirmationId === undefined) {
      return Promise.reject(
        fail(ERROR_CODES.confirmationRequired, "This install needs confirmation.", "confirm_again", true, planId),
      );
    }
    if (plan.confirmationId !== null && confirmationId !== plan.confirmationId) {
      return Promise.reject(
        fail(ERROR_CODES.confirmationMismatch, "That confirmation belongs to another operation.", "confirm_again", true, planId),
      );
    }
    const existed = device.files.has(plan.target);
    const backup = existed
      ? `QuadStickBackups/mock-${String(this.#nextBackup++)}-${plan.target}`
      : null;
    device.files.set(plan.target, plan.text);
    return Promise.resolve({
      operationId: planId,
      deviceId: plan.deviceId,
      target: plan.target,
      bytes: plan.text.length,
      backup,
      confirmedOnDevice: true,
      stages: ["revalidate", "temp_write", "temp_read_back", "replace_after_displace"],
    });
  }

  prepareDeleteDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<DeletePlan> {
    if (!deviceNameIsPlain(name)) {
      return Promise.reject(
        fail(ERROR_CODES.storageNameRejected, "That is not a safe device file name.", "choose_another_name"),
      );
    }
    const lower = name.toLowerCase();
    if (lower === "default.csv" || lower === "prefs.csv") {
      return Promise.reject(
        fail(ERROR_CODES.storageProtectedFile, "That device file is protected.", "retry"),
      );
    }
    let device: MockDevice;
    try {
      device = this.#device(deviceId, expectedGeneration);
    } catch (reason) {
      return Promise.reject(reason);
    }
    const text = device.files.get(name);
    if (text === undefined) {
      return Promise.reject(
        fail(ERROR_CODES.storageFileNotFound, "That profile is no longer on the QuadStick.", "refresh_devices"),
      );
    }
    const planId = this.#mintPlan();
    const confirmationId = this.#mintConfirmation();
    this.#deletePlans.set(planId, {
      planId,
      deviceId,
      generation: device.generation,
      name,
      confirmationId,
    });
    return Promise.resolve({
      planId,
      name,
      bytes: text.length,
      confirmation: {
        confirmationId,
        kind: "delete_device_profile",
        summary: `Delete ${name} from the QuadStick after making a backup.`,
      },
    });
  }

  commitDeleteDeviceProfile(planId: string, confirmationId: string): Promise<DeleteReceipt> {
    const plan = this.#deletePlans.get(planId);
    if (plan === undefined) {
      return Promise.reject(
        fail(ERROR_CODES.requestOutOfRange, "That delete plan is no longer available.", "retry", true, planId),
      );
    }
    this.#deletePlans.delete(planId);
    if (confirmationId !== plan.confirmationId) {
      return Promise.reject(
        fail(ERROR_CODES.confirmationMismatch, "That confirmation belongs to another operation.", "confirm_again", true, planId),
      );
    }
    let device: MockDevice;
    try {
      device = this.#device(plan.deviceId, plan.generation);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (!device.files.has(plan.name)) {
      return Promise.reject(
        fail(ERROR_CODES.storageFileNotFound, "That profile is no longer on the QuadStick.", "refresh_devices", true, planId),
      );
    }
    device.files.delete(plan.name);
    const backup = `QuadStickBackups/mock-${String(this.#nextBackup++)}-${plan.name}`;
    return Promise.resolve({ operationId: planId, deviceId: plan.deviceId, name: plan.name, backup });
  }

  openDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<EditorSnapshot> {
    if (!deviceNameIsPlain(name)) {
      return Promise.reject(
        fail(ERROR_CODES.storageNameRejected, "That is not a safe device file name.", "choose_another_name"),
      );
    }
    let device: MockDevice;
    try {
      device = this.#device(deviceId, expectedGeneration);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (!device.files.has(name)) {
      return Promise.reject(
        fail(ERROR_CODES.storageFileNotFound, "That profile is no longer on the QuadStick.", "refresh_devices"),
      );
    }
    return Promise.resolve(this.#openDeviceCopy(device, name));
  }

  openDevicePreferences(deviceId: string, expectedGeneration: number): Promise<EditorSnapshot> {
    let device: MockDevice;
    try {
      device = this.#device(deviceId, expectedGeneration);
    } catch (reason) {
      return Promise.reject(reason);
    }
    if (!device.files.has("prefs.csv")) {
      return Promise.reject(
        fail(ERROR_CODES.storageFileNotFound, "prefs.csv is no longer on the QuadStick.", "refresh_devices"),
      );
    }
    return Promise.resolve(this.#openDeviceCopy(device, "prefs.csv"));
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
      bytes: gridText(session.grid).length,
    });
  }

  #open(
    source: ProfileSource,
    saveTarget: string | null,
    title: string,
    grid: string[][],
  ): EditorSnapshot {
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

  #openDeviceCopy(device: MockDevice, name: string): EditorSnapshot {
    const grid = clone(TEMPLATE);
    const nameRow = grid[1];
    if (nameRow !== undefined) nameRow[0] = name;
    return this.#open(
      { kind: "device", device: device.id, generation: device.generation, name },
      null,
      name,
      grid,
    );
  }

  #checked(sessionId: string, expectedRevision: number): MockSession {
    const session = this.#sessions.get(sessionId);
    if (session === undefined) throw this.#unknownSession();
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

  #device(deviceId: string, expectedGeneration?: number): MockDevice {
    const device = this.#devices.get(deviceId);
    if (device === undefined) {
      throw fail(ERROR_CODES.deviceNotFound, "That QuadStick is no longer connected.", "reconnect_device");
    }
    if (expectedGeneration !== undefined && device.generation !== expectedGeneration) {
      throw fail(ERROR_CODES.deviceStale, "That drive changed since it was shown.", "refresh_devices");
    }
    return device;
  }

  #deviceSummaries(): DeviceSummary[] {
    return [...this.#devices.values()].map((device) => ({
      deviceId: device.id,
      generation: device.generation,
      displayName: device.displayName,
      writable: device.writable,
      freeBytes: null,
    }));
  }

  #mintPlan(): string {
    const id = `op-${String(this.#nextPlan)}`;
    this.#nextPlan += 1;
    return id;
  }

  #mintConfirmation(): string {
    const id = `cnf-${String(this.#nextConfirmation)}`;
    this.#nextConfirmation += 1;
    return id;
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

function apply(grid: string[][], op: EditorOp): string[][] | null {
  const next = clone(grid);
  switch (op.op) {
    case "set_cell": {
      if (op.row === 0) return null;
      while (next.length < op.row) next.push([]);
      const row = next[op.row - 1];
      if (row === undefined) return null;
      while (row.length <= op.col) row.push("");
      row[op.col] = op.value;
      return next;
    }
    case "set_output": {
      const row = next[op.row - 1];
      if (row === undefined) return null;
      row[0] = op.token;
      return next;
    }
    case "delete_row": {
      if (op.row === 0 || op.row > next.length) return null;
      next.splice(op.row - 1, 1);
      return next;
    }
    case "rename_mode": {
      const mode = modesOf(next)[op.sheet];
      if (mode === undefined) return null;
      const keyword = next[mode.startRow - 1];
      if (keyword === undefined) return null;
      keyword[2] = op.name;
      return next;
    }
    default:
      return null;
  }
}

function stripUndefined(patch: SettingsPatch): Partial<AppSettings> {
  const kept: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(patch)) {
    if (value !== undefined) kept[key] = value;
  }
  return kept as Partial<AppSettings>;
}
