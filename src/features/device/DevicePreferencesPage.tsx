import { useCallback, useEffect, useMemo, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { ControllerRow } from "./ControllerRow";
import { DeviceBand } from "./DeviceBand";
import { DeviceLinkDialog } from "./DeviceLinkDialog";
import { useDeviceFit } from "./deviceFit";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type EditorOp,
  type EditorSnapshot,
  type InstallPlan,
  type ModelChoice,
  type PreferenceCatalog,
  type PreferenceDefinition,
  type QcmClient,
} from "../../platform";

interface DevicePreferencesPageProps {
  readonly client: QcmClient;
  /** prefs.csv off the stick, or null with no stick (the catalog is drawn). */
  readonly snapshot: EditorSnapshot | null;
  /** Said in place of "Found your QuadStick at" when there is no session. */
  readonly status?: string;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
  /** Find the drive and read it again (`LoadDeviceSettingsAsync`). */
  readonly onReload: () => void;
  /** The files window (`ShowDeviceFilesAsync`). */
  readonly onManageFiles: () => void;
  /** `DriveAccessButton`, offered while no drive is found. */
  readonly onChooseDrive?: () => void;
}

interface PreferenceRow {
  readonly row: number;
  readonly name: string;
  readonly value: string;
  readonly units: string;
  readonly description: string;
}

function rowsOf(snapshot: EditorSnapshot | null): PreferenceRow[] {
  if (snapshot === null) return [];
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

// Drew's adapter table: an adapter that gets the wrong mode just looks dead.
export const ADAPTER_MODE_PREFERENCE = "enable_DS3_emulation";

export function AdapterModes() {
  const { t } = useI18n();
  return (
    <details className="device-preference-adapters">
      <summary>{t("Prefs_AdaptersLine")}</summary>
      <h4>{t("Prefs_Adapters")}</h4>
      {t("Prefs_AdaptersHelp")
        .split(/\n\s*\n/u)
        .map((paragraph) => (
          <p key={paragraph}>{paragraph}</p>
        ))}
    </details>
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
  // B-123: the control stays mounted across commits, so a held or repeated
  // arrow keeps focus. A stored value that changes elsewhere (undo, a reload)
  // replaces the draft; a commit lands on the value the draft already shows.
  const [stored, setStored] = useState(value);
  if (stored !== value) {
    setStored(value);
    setDraft(value);
  }
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
          // A screen reader's increment and switch access move the value
          // with no pointer or arrow key, so leaving the slider commits too.
          onBlur={commitDraft}
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
  status = "",
  onSnapshot,
  onReload,
  onManageFiles,
  onChooseDrive,
}: DevicePreferencesPageProps) {
  const { t, plural } = useI18n();
  const [catalog, setCatalog] = useState<PreferenceCatalog | null>(null);
  const [category, setCategory] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [plan, setPlan] = useState<InstallPlan | null>(null);
  const [message, setMessage] = useState("");

  const rows = useMemo(() => rowsOf(snapshot), [snapshot]);
  // What the file held when it was read, so the save bar can count changes
  // (`RefreshDeviceSaveBar`). A reload or a save opens a new session.
  const [baseline, setBaseline] = useState<{ readonly session: string; readonly values: ReadonlyMap<string, string> }>(
    () => ({ session: snapshot?.sessionId ?? "", values: new Map(rows.map((row) => [row.name, row.value])) }),
  );
  const sessionId = snapshot?.sessionId ?? "";
  if (baseline.session !== sessionId) {
    setBaseline({ session: sessionId, values: new Map(rows.map((row) => [row.name, row.value])) });
  }
  const changed = rows.filter((row) => baseline.values.get(row.name) !== row.value).length;
  const [linkOpen, setLinkOpen] = useState(false);
  // Reload and Undo both throw edits away, so with changes they ask first.
  const [askDiscard, setAskDiscard] = useState(false);
  const [askReload, setAskReload] = useState(false);
  // Avalonia says where the drive is mounted; the path is display text from
  // `device_location`, and every file operation still goes by the opaque id.
  const [deviceName, setDeviceName] = useState("");
  const sourceDevice = snapshot?.source.kind === "device" ? snapshot.source.device : null;
  const [model, setModel] = useState<ModelChoice>("fps");
  useEffect(() => {
    let cancelled = false;
    void client.getSettings().then(
      (settings) => {
        if (!cancelled) setModel(settings.model);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);
  useEffect(() => {
    if (sourceDevice === null) return undefined;
    let cancelled = false;
    const locate = client.deviceLocation;
    void (locate === undefined ? Promise.resolve(null) : locate.call(client, sourceDevice).catch(() => null)).then(
      async (path) => {
        const name =
          path ??
          (await client.listDevices().then(
            (presence) => presence.devices.find((device) => device.deviceId === sourceDevice)?.displayName ?? "",
            () => "",
          ));
        if (!cancelled) setDeviceName(name);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [client, sourceDevice]);
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


  const apply = async (ops: readonly EditorOp[]): Promise<EditorSnapshot | null> => {
    if (snapshot === null || busy || ops.length === 0) return null;
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
    if (snapshot === null) return;
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
    if (snapshot?.source.kind !== "device" || busy) return;
    setBusy(true);
    try {
      await client.closeProfile(snapshot.sessionId, "discard");
      const fresh = await client.openDevicePreferences(snapshot.source.device, snapshot.source.generation, true);
      onSnapshot(fresh);
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const prepareSave = async (): Promise<void> => {
    if (snapshot?.source.kind !== "device" || busy) return;
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
    if (plan?.confirmation === null || plan === null || snapshot?.source.kind !== "device" || busy) return;
    setBusy(true);
    try {
      const receipt = await client.commitInstall(plan.planId, plan.confirmation.confirmationId);
      if (!receipt.confirmedOnDevice) throw new Error("device read-back did not confirm prefs.csv");
      setPlan(null);
      await client.closeProfile(snapshot.sessionId, "discard");
      const fresh = await client.openDevicePreferences(snapshot.source.device, snapshot.source.generation);
      onSnapshot(fresh);
      // It is on the stick now; "ready to save" would say the opposite.
      setMessage(t("Main_SavedToSavePath", [receipt.target]));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  // `DeviceNumber`: the file's value, else the catalog default.
  const numberOf = (name: string, fallback: number): number => {
    const value = exactInteger(rowByName.get(name)?.value ?? "");
    if (value !== null) return value;
    const known = exactInteger(catalog?.definitions.find((definition) => definition.name === name)?.default ?? "");
    return known ?? fallback;
  };

  const countIn = (name: string): number =>
    catalog?.definitions.filter((definition) => definition.category === name).length ?? 0;

  const { page, rail, bar, layout } = useDeviceFit(catalog !== null, snapshot !== null);

  return (
    <section
      ref={page}
      className="device-preferences-page"
      aria-labelledby="device-preferences-title"
      data-fit={layout.fit}
      data-short={layout.short ? "true" : undefined}
      data-pin-rail={layout.pinRail ? "true" : undefined}
      data-pin-bar={layout.pinBar ? "true" : undefined}
    >
      {/* Avalonia DevicePage: the title and where the settings live on the
          left, Reload / Manage files / Bluetooth or cable on the right. */}
      <header className="device-page-head">
        <div className="device-page-copy">
          <h1 id="device-preferences-title">{t("DevicePage_YourQuadStickSSettings")}</h1>
          <p className="secondary">{t("DevicePage_TheseSettingsLiveInPrefs")}</p>
          {/* No stick is a state, not an error, but the first thing to know
              here: bold, as Avalonia's warn line. */}
          {snapshot === null ? (
            status === "" ? null : (
              <p className={sourceDevice === null ? "device-status warn" : "device-status"} role="status">
                {status}
              </p>
            )
          ) : status !== "" ? (
            <p className="device-status" role="status">{status}</p>
          ) : deviceName === "" ? null : (
            <p className="device-status">{t("DevicePage_FoundYourQuadStickAtRoot", [deviceName])}</p>
          )}
          {client.controller === undefined ? null : <ControllerRow controller={client.controller} />}
        </div>
        <div className="device-page-actions">
          {snapshot === null && onChooseDrive !== undefined ? (
            <button type="button" disabled={busy} onClick={onChooseDrive}>
              {t("Install_ChooseTheQuadStickDrive")}
            </button>
          ) : null}
          <button className="device-reload" type="button" disabled={busy} aria-label={t("DevicePage_ReadTheSettingsFromYour")} onClick={() => (changed > 0 ? setAskReload(true) : onReload())}>
            {t("DevicePage_Reload")}
          </button>
          <button className="quiet" type="button" disabled={busy} aria-label={t("Shell_ManageTheProfileFilesOn")} onClick={onManageFiles}>
            {t("Shell_ManageFiles")}
          </button>
          {client.console === undefined ? null : (
            <button className="quiet" type="button" aria-label={t("Link_PageButtonName")} onClick={() => setLinkOpen(true)}>
              {t("Link_PageButton")}
            </button>
          )}
        </div>
      </header>

      {message === "" ? null : <output className="feature-status">{message}</output>}

      {catalog === null ? (
        <output className="feature-status">{t("Device_LookingForYourQuadStick")}</output>
      ) : (
        <div className="device-preference-layout">
          <nav ref={rail} aria-label={t("DevicePage_GroupsOfSettings")} className="device-preference-categories">
            {catalog.categories.map((name) => (
              <button
                type="button"
                key={name}
                aria-pressed={category === name}
                onClick={() => setCategory(name)}
              >
                <span className="category-name">{name}</span>
                <span className="category-count">{plural("DevicePage_Setting", countIn(name), [countIn(name)])}</span>
              </button>
            ))}
          </nav>
          <div className="device-preference-main">
            {category === null ? null : (
              <DeviceBand client={client} category={category} model={model} numberOf={numberOf} />
            )}
            <div className="device-preference-list">
              {definitions.map((definition) => {
                const row = rowByName.get(definition.name);
                const range =
                  definition.minimum !== null && definition.maximum !== null
                    ? t("DevicePage_AnythingFromLoToHi", [definition.minimum, definition.maximum])
                    : "";
                const unit = definition.unit === "" ? "" : t("Prefs_MeasuredInDefUnit", [definition.unit]);
                const called = definition.alsoCalled === "" ? "" : t("Prefs_QMPCallsItDefAlsoCalled", [definition.alsoCalled]);
                const facts = [range, unit, called].filter(Boolean).join(" ");
                return (
                  <section key={definition.name} className="device-preference-row" aria-labelledby={`pref-${definition.name}`}>
                    <h3 id={`pref-${definition.name}`} title={definition.name}>{definition.label}</h3>
                    <div className="device-preference-control">
                      {row === undefined ? (
                        <button
                          type="button"
                          id={`pref-${definition.name}-add`}
                          // "Add" alone, read out, does not say which setting.
                          aria-labelledby={`pref-${definition.name}-add pref-${definition.name}`}
                          disabled={busy || snapshot === null}
                          onClick={() => addMissing(definition)}
                        >
                          {t("Main_Add")}
                        </button>
                      ) : (
                        <PreferenceValue
                          key={definition.name}
                          definition={definition}
                          row={row.row}
                          value={row.value}
                          disabled={busy}
                          onCommit={commitValue}
                        />
                      )}
                    </div>
                    <div className="device-preference-copy">
                      {definition.description === "" ? null : <p>{definition.description}</p>}
                      {facts === "" ? null : <p>{facts}</p>}
                      {definition.risk === "" ? null : <p>{definition.risk}</p>}
                      {definition.name === ADAPTER_MODE_PREFERENCE ? <AdapterModes /> : null}
                    </div>
                  </section>
                );
              })}
              {rows.filter((row) => !catalog.definitions.some((definition) => definition.name === row.name)).map((row) => (
                <section key={`unknown-${String(row.row)}`} className="device-preference-row">
                  <h3>{row.name}</h3>
                  <div className="device-preference-control">
                    <PreferenceValue
                      key={`unknown-${String(row.row)}-${row.name}`}
                      definition={null}
                      row={row.row}
                      value={row.value}
                      disabled={busy}
                      onCommit={commitValue}
                    />
                  </div>
                  <div className="device-preference-copy">
                    {row.description === "" ? null : <p>{row.description}</p>}
                  </div>
                </section>
              ))}
            </div>
          </div>
        </div>
      )}

      {/* `DeviceSaveBar`: always here and the same size; its buttons grey out. */}
      <div ref={bar} className="device-save-bar">
        <span role="status">
          {changed === 0 ? t("DevicePage_NoChangesYet") : plural("DevicePage_ChangedNotSavedYet", changed, [changed])}
        </span>
        <span className="device-save-actions">
          <button type="button" disabled={busy || changed === 0} aria-label={t("DevicePage_PutEverySettingBackTo")} onClick={() => setAskDiscard(true)}>
            {t("DevicePage_UndoChanges")}
          </button>
          <button
            className="primary"
            type="button"
            disabled={busy || changed === 0}
            aria-label={t("DevicePage_WriteTheChangedSettingsTo")}
            onClick={() => void prepareSave()}
          >
            {t("DevicePage_SaveToYourQuadStick")}
          </button>
        </span>
      </div>

      <Dialog
        open={askDiscard}
        title={t("DevicePage_ThrowAwayYourChanges")}
        onClose={() => setAskDiscard(false)}
        actions={
          <>
            <button type="button" data-autofocus onClick={() => setAskDiscard(false)}>
              {t("Device_Cancel")}
            </button>
            <button
              type="button"
              onClick={() => {
                setAskDiscard(false);
                void reset();
              }}
            >
              {t("DevicePage_UndoChanges")}
            </button>
          </>
        }
      >
        <p>{t("DevicePage_TheSettingsYouChangedHere")}</p>
      </Dialog>

      <Dialog
        open={askReload}
        title={t("DevicePage_ThrowAwayYourChanges")}
        onClose={() => setAskReload(false)}
        actions={
          <>
            <button type="button" data-autofocus onClick={() => setAskReload(false)}>
              {t("Device_Cancel")}
            </button>
            <button
              type="button"
              onClick={() => {
                setAskReload(false);
                onReload();
              }}
            >
              {t("DevicePage_Reload")}
            </button>
          </>
        }
      >
        <p>{t("DevicePage_TheSettingsYouChangedHere")}</p>
      </Dialog>

      {client.console === undefined || !linkOpen ? null : (
        <DeviceLinkDialog client={client} console={client.console} open onClose={() => setLinkOpen(false)} />
      )}

      <Dialog
        open={plan !== null}
        title={t("Main_DeviceSettings")}
        onClose={() => {
          if (!busy) setPlan(null);
        }}
        actions={
          <>
            {/* This overwrites prefs.csv on the stick, and Save to your
                QuadStick sat where Save is: two Enters wrote it. */}
            <button type="button" data-autofocus disabled={busy} onClick={() => setPlan(null)}>
              {t("Device_Cancel")}
            </button>
            <button type="button" disabled={busy} onClick={() => void commitSave()}>
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
