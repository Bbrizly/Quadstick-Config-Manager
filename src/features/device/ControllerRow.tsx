import { useEffect, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { InfoButton } from "../editor/Popover";
import { useI18n, type MessageKey } from "../../i18n";
import type { BridgeState, ControllerClient, ControllerOptions } from "../../platform";

const BRIDGE_TEXT: Record<BridgeState, MessageKey> = {
  waiting: "DevicePage_BridgeWaiting",
  bridging: "DevicePage_BridgeOn",
  no_access: "DevicePage_BridgeNoAccess",
  no_virtual_pad: "DevicePage_BridgeNoPad",
  unsupported: "DevicePage_BridgeUnsupported",
  stopped: "DevicePage_BridgeStopped",
};

const WARN: ReadonlySet<BridgeState> = new Set(["no_access", "no_virtual_pad", "stopped"]);

/** `MainWindow.Explain`: one line, and a "?" with the long help behind it.
    The "?" is named by the question it answers, not by the heading. */
function Explain({
  line,
  heading,
  ask,
  help,
}: {
  readonly line: MessageKey;
  readonly heading: MessageKey;
  readonly ask: MessageKey;
  readonly help: MessageKey;
}) {
  const { t } = useI18n();
  return (
    <div className="explain-row">
      <span>{t(line)}</span>
      <InfoButton name={t(ask)} title={t(heading)} body={t(help)} />
    </div>
  );
}

/** A Fluent ToggleSwitch with no On/Off words, and the title beside it. */
function Switch({
  checked,
  disabled,
  label,
  onChange,
}: {
  readonly checked: boolean;
  readonly disabled: boolean;
  readonly label: string;
  readonly onChange: (on: boolean) => void;
}) {
  return (
    <label className="switch-row">
      <input
        className="qcm-switch"
        type="checkbox"
        role="switch"
        aria-checked={checked}
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.currentTarget.checked)}
      />
      <span className="switch-title">{label}</span>
    </label>
  );
}

/**
 * The bridge switch on Linux, the Mac games row on macOS, nothing elsewhere
 * (`DeviceSettingsPage.BridgeRow`). The bridge state arrives on a channel
 * only when it changes, so the row costs nothing while it sits there.
 */
export function ControllerRow({ controller }: { readonly controller: ControllerClient }) {
  const { t } = useI18n();
  const [options, setOptions] = useState<ControllerOptions | null>(null);
  const [bridgeState, setBridgeState] = useState<BridgeState | null>(null);
  const [busy, setBusy] = useState(false);
  const [macStatus, setMacStatus] = useState<{ readonly text: string; readonly warn: boolean }>({ text: "", warn: false });

  useEffect(() => {
    let active = true;
    let subscription: { dispose(): void } | null = null;
    void controller.getControllerOptions().then(
      (next) => {
        if (!active) return;
        setOptions(next);
        setBridgeState(next.bridgeState);
        if (next.kind !== "bridge") return;
        void controller.subscribeControllerBridge((state) => {
          if (active) setBridgeState(state);
        }).then(
          (sub) => {
            if (active) subscription = sub;
            else sub.dispose();
          },
          () => undefined,
        );
      },
      () => undefined,
    );
    return () => {
      active = false;
      subscription?.dispose();
    };
  }, [controller]);

  if (options === null || options.kind === "none") return null;

  if (options.kind === "bridge") {
    const toggle = async (on: boolean): Promise<void> => {
      if (busy) return;
      setBusy(true);
      try {
        const saved = await controller.setControllerBridge(on);
        setOptions({ ...options, bridgeOn: saved });
        if (!saved) setBridgeState(null);
      } catch {
        setOptions({ ...options });
      } finally {
        setBusy(false);
      }
    };
    const text = bridgeState === null ? "" : t(BRIDGE_TEXT[bridgeState]);
    return (
      <section className="controller-row" aria-label={t("DevicePage_Bridge")}>
        <Switch checked={options.bridgeOn} disabled={busy} label={t("DevicePage_Bridge")} onChange={(on) => void toggle(on)} />
        <Explain line="DevicePage_BridgeLine" heading="DevicePage_Bridge" ask="Rewrite_HowDoesTheXboxBridgeWork" help="DevicePage_BridgeHelp" />
        <LiveRegion visuallyHidden={false}>
          {text === "" ? null : (
            <span className={bridgeState !== null && WARN.has(bridgeState) ? "feature-error" : undefined}>{text}</span>
          )}
        </LiveRegion>
      </section>
    );
  }

  const say = (text: string, warn = false): void => setMacStatus({ text, warn });

  const copy = (command: string | null): void => {
    if (command === null) return;
    void navigator.clipboard?.writeText(command).then(
      () => say(t("DevicePage_MacPadCopied")),
      () => undefined,
    );
  };

  const toggleMac = async (on: boolean): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const result = await controller.setMacGamepad(on);
      setOptions({ ...options, macOn: result.on });
      if (result.took) say(on ? t("DevicePage_MacPadOn") : "");
      else say(t("DevicePage_MacPadFailed"), true);
    } catch {
      say(t("DevicePage_MacPadFailed"), true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="controller-row" aria-label={t("DevicePage_MacGames")}>
      <h2 className="controller-title">{t("DevicePage_MacGames")}</h2>
      <Explain line="DevicePage_MacGamesLine" heading="DevicePage_MacGames" ask="Rewrite_HowDoGamesWorkOnAMac" help="DevicePage_MacGamesHelp" />
      {options.macSandboxed ? (
        <div className="controller-store">
          <p>{t("DevicePage_MacPadStoreLine")}</p>
          <div className="controller-copy">
            <button type="button" onClick={() => copy(options.macCommand)}>
              {t("DevicePage_MacPadCopy")}
            </button>
            <button type="button" onClick={() => copy(options.macUndoCommand)}>
              {t("DevicePage_MacPadCopyUndo")}
            </button>
          </div>
        </div>
      ) : (
        <Switch checked={options.macOn} disabled={busy} label={t("DevicePage_MacPad")} onChange={(on) => void toggleMac(on)} />
      )}
      <Explain line="DevicePage_MacPadLine" heading="DevicePage_MacPad" ask="Rewrite_WhatDoesGamepadMode0Do" help="DevicePage_MacPadHelp" />
      <LiveRegion visuallyHidden={false}>
        {macStatus.text === "" ? null : (
          <span className={macStatus.warn ? "feature-error" : undefined}>{macStatus.text}</span>
        )}
      </LiveRegion>
    </section>
  );
}
