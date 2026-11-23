import { useCallback, useEffect, useRef, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { InfoButton } from "../editor/Popover";
import { useI18n, type MessageKey } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  CONSOLE_ERROR_CODES,
  type ConsoleClient,
  type ConsolePort,
  type LiveSnapshot,
  type QcmClient,
  type QcmErrorPayload,
  type Subscription,
} from "../../platform";
import { SWEEP_LOWEST, strengthSweep } from "./strengthSweep";

/**
 * The QuadStick's own console, for the two things a drive cannot do: reach a
 * QuadStick across the room over Bluetooth, and measure how hard somebody can
 * sip and puff. Ported from `DeviceLinkWindow.cs`; the protocol is native.
 *
 * Idle cost is nothing: no timer runs and live input is not subscribed until
 * a measurement starts, and both stop when it ends.
 */

export interface DeviceLinkTiming {
  /** How long each breath, and each "let go", may take. */
  readonly breathMs: number;
  /** Pause after each breath, and twice that before zeroing the sensors. */
  readonly settleMs: number;
  /** How long to wait for the first USB reading before saying there is none. */
  readonly firstReadingMs: number;
  /** How often a wait looks at the reading. */
  readonly pollMs: number;
}

const DEFAULT_TIMING: DeviceLinkTiming = { breathMs: 5000, settleMs: 700, firstReadingMs: 1500, pollMs: 30 };

/** Buttons held right now on the USB gamepad, or null when there is no reading. */
export type LiveButtons = () => readonly number[] | null;

interface DeviceLinkDialogProps {
  readonly client: QcmClient;
  readonly console: ConsoleClient;
  readonly open: boolean;
  readonly onClose: () => void;
  readonly timing?: DeviceLinkTiming;
  /** Read the held buttons from here instead of `client.startLiveInput` (a host
   *  with its own gamepad reading, or a test). */
  readonly live?: LiveButtons;
  /** Test seam: the last strength measured, puff then sip. */
  readonly onMeasured?: (puff: number, sip: number) => void;
}

interface Ask {
  readonly title: string;
  readonly body: string;
  readonly resolve: (yes: boolean) => void;
}

class Stopped extends Error {}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

type T = (key: MessageKey, values?: readonly unknown[]) => string;

export function consoleFailureMessage(error: QcmErrorPayload, t: T): string {
  const subject = error.subject ?? "";
  switch (error.code) {
    case CONSOLE_ERROR_CODES.noAnswer:
      return t("DeviceConsole_NoAnswer");
    case CONSOLE_ERROR_CODES.settingRefused:
      return t("DeviceConsole_SettingRefused", [subject]);
    case CONSOLE_ERROR_CODES.couldNotOpen:
      return t("DeviceConsole_CouldNotOpen", [subject]);
    case CONSOLE_ERROR_CODES.writeRefused:
      return t("DeviceConsole_WriteRefused", [subject]);
    case CONSOLE_ERROR_CODES.couldNotLoad:
      return t("DeviceConsole_CouldNotLoad", [subject]);
    case CONSOLE_ERROR_CODES.readBackDiffers:
      return t("DeviceConsole_ReadBackDiffers", [subject, error.backup ?? t("DeviceConsole_NoEarlierFile")]);
    default:
      return localizedErrorMessage(error, t);
  }
}

export function DeviceLinkDialog({
  client,
  console: link,
  open,
  onClose,
  timing = DEFAULT_TIMING,
  live,
  onMeasured,
}: DeviceLinkDialogProps) {
  const { t } = useI18n();
  const [ports, setPorts] = useState<readonly ConsolePort[]>([]);
  const [portId, setPortId] = useState("");
  const [connected, setConnected] = useState(false);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState("");
  const [prompt, setPrompt] = useState<string | null>(null);
  const [ask, setAsk] = useState<Ask | null>(null);
  const stopAsked = useRef(false);
  const latest = useRef<LiveSnapshot | null>(null);
  // Closed while a send or a test runs, the port would go out from under it
  // and the QuadStick would be left with the test file loaded and its
  // thresholds moved (6934bbf). So a close waits: it stops the test, the
  // device is put back, then the window goes.
  const closeWhenDone = useRef(false);
  // Set once the window has been told to close, so a picker or question that
  // answers afterwards cannot start a send on a window that is gone.
  const gone = useRef(false);
  useEffect(() => {
    if (open) gone.current = false;
  }, [open]);
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  });
  const closeIfAsked = (): void => {
    if (!closeWhenDone.current) return;
    closeWhenDone.current = false;
    gone.current = true;
    onCloseRef.current();
  };

  const say = (key: MessageKey, values?: readonly unknown[]): void => setStatus(t(key, values));
  const failed = (reason: unknown): void => setStatus(consoleFailureMessage(asQcmError(reason).payload, t));

  const fillPorts = useCallback(async (): Promise<void> => {
    let found: readonly ConsolePort[] = [];
    try {
      found = await link.listConsolePorts();
    } catch {
      found = [];
    }
    setPorts(found);
    setPortId(found[0]?.id ?? "");
    if (found.length === 0) setStatus(t("Link_NoPorts"));
  }, [link, t]);

  // Refs so a re-render (a new `t`, a new client object) cannot look like the
  // window closing and drop a port mid-send.
  const fillRef = useRef(fillPorts);
  const linkRef = useRef(link);
  useEffect(() => {
    fillRef.current = fillPorts;
    linkRef.current = link;
  });

  useEffect(() => {
    if (!open) return undefined;
    void fillRef.current();
    return () => {
      // Closing the window lets go of the port.
      void linkRef.current.closeConsole().catch(() => undefined);
    };
  }, [open]);

  const confirm = (title: string, body: string): Promise<boolean> =>
    new Promise((resolve) => {
      setAsk({ title, body, resolve });
    });

  const answer = (yes: boolean): void => {
    ask?.resolve(yes);
    setAsk(null);
  };

  const close = useCallback((): void => {
    if (ask !== null) {
      ask.resolve(false);
      setAsk(null);
      return;
    }
    if (!busy) {
      gone.current = true;
      onClose();
      return;
    }
    stopAsked.current = true;
    closeWhenDone.current = true;
  }, [ask, busy, onClose]);

  const connect = async (): Promise<void> => {
    const port = ports.find((p) => p.id === portId);
    if (port === undefined || busy) return;
    setBusy(true);
    setConnected(false);
    say("Link_Connecting");
    try {
      const outcome = await link.connectConsole(port.id);
      if (outcome.kind === "connected") {
        setConnected(true);
        say("Link_Connected", [outcome.build]);
      } else if (outcome.kind === "notAQuadStick") {
        say("Link_NotAQuadStick");
      } else {
        say("Link_CouldNotOpen", [port.label, outcome.detail]);
      }
    } catch (reason) {
      failed(reason);
    } finally {
      setBusy(false);
      closeIfAsked();
    }
  };

  const choose = link.chooseConsoleProfile;
  const sendProfile = link.consoleSendProfile;
  const canSend = choose !== undefined && sendProfile !== undefined;
  const send = async (): Promise<void> => {
    if (!connected || busy || !canSend) return;
    let pick;
    try {
      pick = await choose.call(link);
    } catch (reason) {
      failed(reason);
      return;
    }
    if (pick === null || gone.current) return;
    // The same two questions a drive install asks, asked the same way.
    let yesDefault = false;
    let yesPrefs = false;
    if (pick.isDefault && !(yesDefault = await confirm(t("Install_OverwriteDefaultCsv"), t("Install_AWrongDefaultCsvCan")))) return;
    if (pick.isPreferences && !(yesPrefs = await confirm(t("Link_OverwritePrefs"), t("Link_OverwritePrefsMore")))) return;
    if (gone.current) return;

    setBusy(true);
    say("Link_Sending", [pick.name ?? ""]);
    try {
      const sent = await sendProfile.call(link, pick.pickId, yesDefault, yesPrefs);
      if (sent.backup === null) say("Link_SentNew", [sent.name]);
      else say("Link_Sent", [sent.name, sent.backup]);
    } catch (reason) {
      failed(reason);
    } finally {
      setBusy(false);
      closeIfAsked();
    }
  };

  // ---- measuring strength ----

  const buttons: LiveButtons =
    live ??
    (() => {
      const reading = latest.current?.status;
      return reading?.kind === "reading" ? reading.motion.buttons : null;
    });

  const waitFor = async (until: (held: readonly number[]) => boolean, within: number): Promise<boolean> => {
    const end = Date.now() + within;
    while (Date.now() < end) {
      if (stopAsked.current) throw new Stopped();
      const held = buttons();
      if (held !== null && until(held)) return true;
      // oxlint-disable-next-line no-await-in-loop -- a wait is sequential by definition
      await sleep(timing.pollMs);
    }
    return false;
  };

  const percent = (v: number): string =>
    v < SWEEP_LOWEST ? t("Link_UnderLowest", [SWEEP_LOWEST]) : t("Link_PercentValue", [v]);

  const measure = async (): Promise<void> => {
    if (!connected || busy) return;
    if (!(await confirm(t("Link_MeasureConfirmTitle"), t("Link_MeasureConfirm")))) return;

    setBusy(true);
    stopAsked.current = false;
    let subscription: Subscription | null = null;
    let finished = false;
    let puff = SWEEP_LOWEST - 1;
    let sip = SWEEP_LOWEST - 1;
    let threshold = 0;
    let started = false;
    try {
      // The breaths are seen as button presses on the USB gamepad. Without a
      // reading there is nothing to see them with.
      if (live === undefined) {
        latest.current = null;
        subscription = await client.startLiveInput((frame) => {
          latest.current = frame;
        });
        await waitFor(() => true, timing.firstReadingMs);
      }
      if (buttons() === null) {
        say("Link_NeedsUsb");
        return;
      }

      started = true;
      threshold = (await link.consoleStrengthBegin()).threshold;

      setPrompt(t("Link_HandsOff"));
      await sleep(timing.settleMs * 2);
      if (stopAsked.current) throw new Stopped();
      await link.consoleCalibrate();

      let attempt = 0;
      const tryOnce = async (isPuff: boolean, value: number): Promise<boolean> => {
        if (stopAsked.current) throw new Stopped();
        await link.consoleSetThreshold(isPuff, value);
        // Nothing held going in, or a stuck button would score as a breath.
        await waitFor((held) => held.length === 0, timing.breathMs);
        attempt += 1;
        setPrompt(t(isPuff ? "Link_PuffNow" : "Link_SipNow", [attempt]));
        const fired = await waitFor((held) => held.length > 0, timing.breathMs);
        setPrompt(null);
        await waitFor((held) => held.length === 0, timing.breathMs);
        await sleep(timing.settleMs);
        return fired;
      };

      puff = await strengthSweep((v) => tryOnce(true, v));
      attempt = 0;
      sip = await strengthSweep((v) => tryOnce(false, v));
      finished = true;
    } catch (reason) {
      if (!(reason instanceof Stopped)) failed(reason);
    } finally {
      setPrompt(null);
      subscription?.dispose();
      latest.current = null;
      if (started) {
        // Restart, always: it puts the person's own profile and settings back.
        // The restart drops the port, so the window is no longer connected.
        await link.consoleStrengthEnd().catch(() => undefined);
        setConnected(false);
      }
      setBusy(false);
    }

    if (finished) {
      onMeasured?.(puff, sip);
      void link.recordStrengthMeasured?.(puff, sip).catch(() => undefined);
      say("Link_Result", [percent(puff), percent(sip), percent(threshold)]);
    } else if (stopAsked.current) {
      say("Link_Cancelled");
    }
    closeIfAsked();
  };

  const hasPorts = ports.length > 0;

  if (ask !== null) {
    return (
      <Dialog
        open={open}
        title={ask.title}
        onClose={close}
        actions={
          <>
            <button type="button" data-autofocus onClick={() => answer(false)}>
              {t("Main_Cancel")}
            </button>
            {/* Every question here overwrites a file or restarts the stick:
                plain, so the accent never sits on the risk. */}
            <button type="button" onClick={() => answer(true)}>
              {t("Main_YesContinue")}
            </button>
          </>
        }
      >
        <p>{ask.body}</p>
      </Dialog>
    );
  }

  return (
    <Dialog
      open={open}
      title={t("Link_Title")}
      onClose={close}
      actions={
        <button type="button" className="link-close" onClick={close}>
          {t("Link_Close")}
        </button>
      }
    >
      {/* `MainWindow.Explain`: the sentence, and the rest behind its "?". */}
      <div className="link-explain">
        <p>{t("Link_Explain")}</p>
        <InfoButton name={t("Rewrite_HowDoesTheLinkWork")} title={t("Link_Title")} body={t("Link_ExplainMore")} />
      </div>
      {/* Ports, Refresh and Connect on one line, as the Avalonia window has them. */}
      <div className="link-port-row">
        <label className="link-port">
          <select
            aria-label={t("Link_Port")}
            value={portId}
            disabled={busy || !hasPorts}
            onChange={(event) => setPortId(event.currentTarget.value)}
          >
            {ports.map((port) => (
              <option key={port.id} value={port.id}>
                {port.label}
              </option>
            ))}
          </select>
        </label>
        <button type="button" disabled={busy} data-autofocus={hasPorts ? undefined : true} onClick={() => void fillPorts()}>
          {t("Link_Refresh")}
        </button>
        <button
          className="primary-action"
          type="button"
          disabled={busy || portId === ""}
          data-autofocus={hasPorts ? true : undefined}
          onClick={() => void connect()}
        >
          {t("Link_Connect")}
        </button>
      </div>
      <div className="link-actions">
        {canSend ? (
          <button type="button" disabled={busy || !connected} onClick={() => void send()}>
            {t("Link_SendProfile")}
          </button>
        ) : null}
        <button type="button" disabled={busy || !connected} onClick={() => void measure()}>
          {t("Link_Measure")}
        </button>
        {busy ? (
          <button
            type="button"
            onClick={() => {
              stopAsked.current = true;
            }}
          >
            {t("Link_Stop")}
          </button>
        ) : null}
      </div>
      {prompt === null ? null : (
        <p className="link-prompt" role="alert" data-testid="link-prompt">
          <strong>{prompt}</strong>
        </p>
      )}
      <output className="feature-status" aria-live="polite" data-testid="link-status">
        {status}
      </output>
    </Dialog>
  );
}
