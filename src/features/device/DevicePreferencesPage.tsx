import { useCallback, useEffect, useMemo, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type EditorOp,
  type EditorSnapshot,
  type InstallPlan,
  type LiveSnapshot,
  type PreferenceCatalog,
  type PreferenceDefinition,
  type QcmClient,
} from "../../platform";

interface DevicePreferencesPageProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
  readonly onClose: () => void;
}

interface PreferenceRow {
  readonly row: number;
  readonly name: string;
  readonly value: string;
  readonly units: string;
  readonly description: string;
}

function rowsOf(snapshot: EditorSnapshot): PreferenceRow[] {
  const sheet = snapshot.modes.find((mode) => mode.kind === "preferences");
  if (sheet === undefined) return [];
  return Array.from({ length: sheet.bindingCount }, (_, offset) => {
    const row = sheet.startRow + 3 + offset;
    const cells = snapshot.grid[row - 1] ?? [];
    return {
      row,
      name: cells[0] ?? "",
      value: cells[1] ?? "",
      units: cells[2] ?? "",
      description: cells[3] ?? "",
    };
  });
}

function exactInteger(value: string): number | null {
  if (!/^-?(0|[1-9][0-9]*)$/u.test(value)) return null;
  const parsed = Number.parseInt(value, 10);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function canUseTypedControl(definition: PreferenceDefinition, value: string): boolean {
  if (definition.editor === "toggle") return value === "0" || value === "1";
  if (definition.editor === "choice") {
    return definition.options.some((option) => option.value === value);
  }
  if (definition.editor === "integer") {
    const parsed = exactInteger(value);
    if (parsed === null) return false;
    if (definition.minimum !== null && parsed < definition.minimum) return false;
    if (definition.maximum !== null && parsed > definition.maximum) return false;
    return true;
  }
  return false;
}

function latestInsertionRow(snapshot: EditorSnapshot): number | null {
  const sheet = snapshot.modes.find((mode) => mode.kind === "preferences");
  if (sheet === undefined) return null;
  const rows = rowsOf(snapshot);
  return rows.at(-1)?.row !== undefined ? (rows.at(-1)?.row ?? 0) + 1 : sheet.startRow + 3;
}

function liveText(frame: LiveSnapshot | null, t: ReturnType<typeof useI18n>["t"]): string {
  if (frame?.status.kind === "reading") {
    return frame.status.motion.buttons.length === 0
      ? t("DevicePage_ReadingProductNothingPressed", [frame.status.product])
      : t("DevicePage_ReadingProductPressedNow", [
          frame.status.product,
          frame.status.motion.buttons.join(", "),
        ]);
  }
  if (frame?.status.kind === "xinputOnly") return t("Main_ThisEmulationModeIsNot");
  return t("DevicePage_NothingIsReadingTheStick");
}

function AxisView({ frame, deadZone }: { readonly frame: LiveSnapshot | null; readonly deadZone: number }) {
  const reading = frame?.status.kind === "reading" ? frame.status.motion : null;
  const x = reading?.x ?? 0;
  const y = reading?.y ?? 0;
  const clampedDeadZone = Math.max(0, Math.min(100, deadZone));
  return (
    <div className="device-axis-view" aria-hidden="true">
      <span
        className="device-axis-dead-zone"
        style={{ width: `${String(clampedDeadZone)}%`, height: `${String(clampedDeadZone)}%` }}
      />
      <span
        className="device-axis-dot"
        style={{
          left: `${String(50 + x * 45)}%`,
          top: `${String(50 + y * 45)}%`,
        }}
      />
    </div>
  );
}

function PreferenceValue({
  definition,
  row,
  value,
  disabled,
  onCommit,
}: {
  readonly definition: PreferenceDefinition | null;
  readonly row: number;
  readonly value: string;
  readonly disabled: boolean;
  readonly onCommit: (row: number, value: string) => void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState(value);
  const typed = definition !== null && canUseTypedControl(definition, value);
  const commitDraft = (): void => {
    if (draft !== value) onCommit(row, draft);
  };

  if (definition?.editor === "toggle" && typed) {
    const checked = value === "1";
    return (
      <label className="preference-toggle">
        <input
          type="checkbox"
          checked={checked}
          disabled={disabled}
          onChange={(event) => onCommit(row, event.currentTarget.checked ? "1" : "0")}
        />
        <span>{checked ? t("Prefs_OnOne") : t("Prefs_OffZero")}</span>
      </label>
    );
  }

  if (definition?.editor === "choice" && typed) {
    return (
      <select
        value={value}
        disabled={disabled}
        aria-label={definition.label}
        onChange={(event) => onCommit(row, event.currentTarget.value)}
      >
        {definition.options.map((option) => (
          <option value={option.value} key={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  if (definition?.editor === "integer" && typed) {
    const minimum = definition.minimum ?? -2147483648;
    const maximum = definition.maximum ?? 2147483647;
    return (
      <div className="preference-number-control">
        <input
          type="range"
          min={minimum}
          max={maximum}
          value={draft}
          disabled={disabled}
          aria-label={definition.label}
          onChange={(event) => setDraft(event.currentTarget.value)}
          onPointerUp={commitDraft}
          onKeyUp={(event) => {
            if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) {
              commitDraft();
            }
          }}
        />
        <input
          type="number"
          min={minimum}
          max={maximum}
          value={draft}
          disabled={disabled}
          aria-label={`${definition.label} ${definition.unit}`.trim()}
          onChange={(event) => setDraft(event.currentTarget.value)}
          onBlur={commitDraft}
        />
      </div>
    );
  }

  return (
    <input
      value={draft}
      disabled={disabled}
      aria-label={definition?.label ?? t("Prefs_SettingValueForRow", [row])}
      onChange={(event) => setDraft(event.currentTarget.value)}
      onBlur={commitDraft}
    />
  );
}

export function DevicePreferencesPage({
  client,
  snapshot,
  onSnapshot,
  onClose,
}: DevicePreferencesPageProps) {
  const { t } = useI18n();
  const [catalog, setCatalog] = useState<PreferenceCatalog | null>(null);
  const [category, setCategory] = useState<string | null>(null);
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [busy, setBusy] = useState(false);
  const [plan, setPlan] = useState<InstallPlan | null>(null);
  const [message, setMessage] = useState("");

  const rows = useMemo(() => rowsOf(snapshot), [snapshot]);
  const rowByName = useMemo(() => new Map(rows.map((row) => [row.name, row])), [rows]);
  const definitions = useMemo(
    () => catalog?.definitions.filter((definition) => category === null || definition.category === category) ?? [],
    [catalog, category],
  );

  const showFailure = useCallback(
    (reason: unknown): void => {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    },
    [t],
  );

  useEffect(() => {
    const getCatalog = client.getPreferenceCatalog;
    if (getCatalog === undefined) return undefined;
    let active = true;
    void getCatalog.call(client).then((next) => {
      if (!active) return;
      setCatalog(next);
      setCategory((current) => current ?? next.categories[0] ?? null);
    }).catch((reason: unknown) => {
      if (active) showFailure(reason);
    });
    return () => {
      active = false;
    };
  }, [client, showFailure]);

  useEffect(() => {
    let active = true;
    let subscription: { dispose(): void } | null = null;
    void client.startLiveInput((frame) => {
      if (active) setLive(frame);
    }).then((next) => {
      if (active) subscription = next;
      else next.dispose();
    }).catch(() => {
      if (active) setLive(null);
    });
    return () => {
      active = false;
      subscription?.dispose();
    };
  }, [client]);

  const apply = async (ops: readonly EditorOp[]): Promise<EditorSnapshot | null> => {
    if (busy || ops.length === 0) return null;
    setBusy(true);
    try {
      const next = await client.applyEditorOps(snapshot.sessionId, snapshot.revision, ops);
      onSnapshot(next);
      setMessage("");
      return next;
    } catch (reason) {
      showFailure(reason);
      return null;
    } finally {
      setBusy(false);
    }
  };

  const commitValue = (row: number, value: string): void => {
    void apply([{ op: "set_cell", row, col: 1, value }]);
  };

  const addMissing = (definition: PreferenceDefinition): void => {
    const sheet = snapshot.modes.find((mode) => mode.kind === "preferences");
    const row = latestInsertionRow(snapshot);
    if (sheet === undefined || row === null) return;
    const value = definition.default ?? "0";
    void apply([
      { op: "add_row", sheet: sheet.index },
      { op: "set_cell", row, col: 0, value: definition.name },
      { op: "set_cell", row, col: 1, value },
      { op: "set_cell", row, col: 2, value: definition.unit },
      { op: "set_cell", row, col: 3, value: definition.description },
    ]);
  };

  const reset = async (): Promise<void> => {
    if (snapshot.source.kind !== "device" || busy) return;
    setBusy(true);
    try {
      await client.closeProfile(snapshot.sessionId, "discard");
      const fresh = await client.openDevicePreferences(snapshot.source.device, snapshot.source.generation);
      onSnapshot(fresh);
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const prepareSave = async (): Promise<void> => {
    if (snapshot.source.kind !== "device" || busy) return;
    setBusy(true);
    try {
      const prepared = await client.prepareInstall(snapshot.sessionId, snapshot.source.device);
      if (prepared.confirmation === null) {
        throw new Error("prefs.csv write did not require confirmation");
      }
      setPlan(prepared);
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const commitSave = async (): Promise<void> => {
    if (plan?.confirmation === null || plan === null || snapshot.source.kind !== "device" || busy) return;
    setBusy(true);
    try {
      const receipt = await client.commitInstall(plan.planId, plan.confirmation.confirmationId);
      if (!receipt.confirmedOnDevice) throw new Error("device read-back did not confirm prefs.csv");
      setPlan(null);
      await client.closeProfile(snapshot.sessionId, "discard");
      const fresh = await client.openDevicePreferences(snapshot.source.device, snapshot.source.generation);
      onSnapshot(fresh);
      setMessage(t("Main_NoProblemsReadyToSave"));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const deadZone = useMemo(() => {
    const candidate = rows.find((row) => row.name.includes("dead_zone") || row.name === "joystick_deflection_minimum");
    const parsed = candidate === undefined ? null : exactInteger(candidate.value);
    return parsed ?? 0;
  }, [rows]);

  return (
    <section className="device-preferences-page" aria-labelledby="device-preferences-title">
      <header className="feature-page-header">
        <div>
          <h1 id="device-preferences-title">{t("DevicePage_YourQuadStickSSettings")}</h1>
          <p>{t("Prefs_DeviceWideSettings")}</p>
        </div>
        <div className="feature-page-actions">
          <button type="button" disabled={busy} onClick={() => void reset()}>
            {t("DevicePage_Reload")}
          </button>
          <button type="button" disabled={busy || !snapshot.dirty} onClick={() => void prepareSave()}>
            {t("Main_Save")}
          </button>
          <button type="button" disabled={busy} onClick={onClose}>
            {t("Main_Done")}
          </button>
        </div>
      </header>

      {message === "" ? null : <output className="feature-status">{message}</output>}

      <section className="device-live-tuning" aria-labelledby="device-live-title">
        <h2 id="device-live-title">{t("DevicePage_JoystickTravel")}</h2>
        <AxisView frame={live} deadZone={deadZone} />
        <output>{liveText(live, t)}</output>
      </section>

      {catalog === null ? (
        <output className="feature-status">{t("Device_LookingForYourQuadStick")}</output>
      ) : (
        <div className="device-preference-layout">
          <nav aria-label={t("Main_DeviceSettings")} className="device-preference-categories">
            {catalog.categories.map((name) => (
              <button
                type="button"
                key={name}
                aria-pressed={category === name}
                onClick={() => setCategory(name)}
              >
                {name}
              </button>
            ))}
          </nav>
          <div className="device-preference-list">
            {definitions.map((definition) => {
              const row = rowByName.get(definition.name);
              return (
                <section key={definition.name} className="device-preference-row" aria-labelledby={`pref-${definition.name}`}>
                  <div className="device-preference-copy">
                    <h3 id={`pref-${definition.name}`}>{definition.label}</h3>
                    <code>{definition.name}</code>
                    {definition.description === "" ? null : <p>{definition.description}</p>}
                    {definition.risk === "" ? null : <p>{definition.risk}</p>}
                    {definition.alsoCalled === "" ? null : <small>{definition.alsoCalled}</small>}
                  </div>
                  {row === undefined ? (
                    <button type="button" disabled={busy} onClick={() => addMissing(definition)}>
                      {t("Main_Add")}
                    </button>
                  ) : (
                    <PreferenceValue
                      key={`${String(snapshot.revision)}-${definition.name}-${row.value}`}
                      definition={definition}
                      row={row.row}
                      value={row.value}
                      disabled={busy}
                      onCommit={commitValue}
                    />
                  )}
                </section>
              );
            })}
            {rows.filter((row) => !catalog.definitions.some((definition) => definition.name === row.name)).map((row) => (
              <section key={`unknown-${String(row.row)}`} className="device-preference-row">
                <div className="device-preference-copy">
                  <h3>{row.name}</h3>
                  {row.description === "" ? null : <p>{row.description}</p>}
                </div>
                <PreferenceValue
                  key={`${String(snapshot.revision)}-unknown-${String(row.row)}-${row.value}`}
                  definition={null}
                  row={row.row}
                  value={row.value}
                  disabled={busy}
                  onCommit={commitValue}
                />
              </section>
            ))}
          </div>
        </div>
      )}

      <Dialog
        open={plan !== null}
        title={t("Main_DeviceSettings")}
        onClose={() => {
          if (!busy) setPlan(null);
        }}
        actions={
          <>
            <button type="button" disabled={busy} onClick={() => setPlan(null)}>
              {t("Device_Cancel")}
            </button>
            <button className="primary-action" type="button" data-autofocus disabled={busy} onClick={() => void commitSave()}>
              {t("Main_Save")}
            </button>
          </>
        }
      >
        <p>{plan?.confirmation?.summary}</p>
      </Dialog>
    </section>
  );
}
