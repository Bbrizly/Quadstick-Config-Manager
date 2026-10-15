import { useEffect, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
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

/** `DeviceSettingsPage.Explain`: one line, and the long help behind it. */
function Explain({ line, title, help }: { readonly line: MessageKey; readonly title: MessageKey; readonly help: MessageKey }) {
  const { t } = useI18n();
  return (
    <details className="device-preference-adapters">
      <summary>{t(line)}</summary>
      <h4>{t(title)}</h4>
      {t(help)
        .split(/\n\s*\n/u)
        .map((paragraph) => (
          <p key={paragraph}>{paragraph}</p>
        ))}
    </details>
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
        const running = await controller.setControllerBridge(on);
        setOptions({ ...options, bridgeOn: running });
        if (!running && !on) setBridgeState(null);
      } catch {
        setOptions({ ...options });
      } finally {
        setBusy(false);
      }
    };
    const text = bridgeState === null ? "" : t(BRIDGE_TEXT[bridgeState]);
    return (
      <section className="controller-row" aria-label={t("DevicePage_Bridge")}>
        <label className="checkbox-row">
          <input
            type="checkbox"
            checked={options.bridgeOn}
            disabled={busy}
            onChange={(event) => void toggle(event.currentTarget.checked)}
          />
          <span>{t("DevicePage_Bridge")}</span>
        </label>
        <Explain line="DevicePage_BridgeLine" title="DevicePage_Bridge" help="DevicePage_BridgeHelp" />
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
      <h3>{t("DevicePage_MacGames")}</h3>
      <Explain line="DevicePage_MacGamesLine" title="DevicePage_MacGames" help="DevicePage_MacGamesHelp" />
      {options.macSandboxed ? (
        <div>
          <p>{t("DevicePage_MacPadStoreLine")}</p>
          <button type="button" onClick={() => copy(options.macCommand)}>
            {t("DevicePage_MacPadCopy")}
          </button>
          <button type="button" onClick={() => copy(options.macUndoCommand)}>
            {t("DevicePage_MacPadCopyUndo")}
          </button>
        </div>
      ) : (
        <label className="checkbox-row">
          <input
            type="checkbox"
            checked={options.macOn}
            disabled={busy}
            onChange={(event) => void toggleMac(event.currentTarget.checked)}
          />
          <span>{t("DevicePage_MacPad")}</span>
        </label>
      )}
      <Explain line="DevicePage_MacPadLine" title="DevicePage_MacPad" help="DevicePage_MacPadHelp" />
      <LiveRegion visuallyHidden={false}>
        {macStatus.text === "" ? null : (
          <span className={macStatus.warn ? "feature-error" : undefined}>{macStatus.text}</span>
        )}
      </LiveRegion>
    </section>
  );
}
