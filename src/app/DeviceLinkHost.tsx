import { DeviceLinkDialog, type LiveButtons } from "../features/device/DeviceLinkDialog";
import { I18nProvider, type LocaleTag } from "../i18n";
import type { QcmClient } from "../platform";
import type { ConsoleClient } from "../platform/consoleContracts";

export interface DeviceLinkHostProps {
  readonly client: QcmClient;
  readonly console: ConsoleClient;
  readonly open: boolean;
  readonly onClose: () => void;
  /** The host's language, as for `EditorHost`. */
  readonly locale?: LocaleTag | undefined;
  /** Held buttons from the host's own reading. Left out: `client.startLiveInput`. */
  readonly live?: LiveButtons | undefined;
}

/**
 * The QuadStick console window (connect, strength test, and Send profile when
 * the console has it), for an app that hosts the editor. A finished strength
 * test reaches the host through `console.recordStrengthMeasured`.
 */
export function DeviceLinkHost({ locale, live, ...props }: DeviceLinkHostProps) {
  return (
    <I18nProvider locale={locale}>
      {live === undefined ? <DeviceLinkDialog {...props} /> : <DeviceLinkDialog {...props} live={live} />}
    </I18nProvider>
  );
}
