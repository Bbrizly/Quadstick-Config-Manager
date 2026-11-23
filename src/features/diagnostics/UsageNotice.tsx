import { useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import type { QcmClient } from "../../platform";

export interface UsageNoticeProps {
  readonly client: QcmClient;
  /** Called once the answer is settled. `saved` is false when it could not be written. */
  readonly onAnswered: (saved: boolean) => void;
}

/**
 * The first-run usage-data question (`ShowTelemetryNoticeAsync`, notice 2).
 * No is the default and what Escape picks. The answer goes through the
 * Settings write, which records the notice as seen; a write that fails is
 * treated as no, and nothing was started, so nothing is sent.
 */
export function UsageNotice({ client, onAnswered }: UsageNoticeProps) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);

  const answer = async (usage: boolean): Promise<void> => {
    if (busy) return;
    setBusy(true);
    let saved = false;
    try {
      const settings = await client.getSettings();
      await client.updateSettings(settings.revision, { usageAnalytics: usage });
      saved = true;
    } catch {
      saved = false;
    }
    setBusy(false);
    onAnswered(saved);
  };

  const openPolicy = (): void => {
    const open = client.openPrivacyPolicy;
    if (open === undefined) return;
    // Best effort: the notice already says what matters.
    void open.call(client).catch(() => undefined);
  };

  return (
    <Dialog
      open
      title={t("Main_HelpImproveQuadStickConfigManager")}
      onClose={() => {
        void answer(false);
      }}
      actions={
        <>
          <button
            className="primary-action"
            type="button"
            data-autofocus
            disabled={busy}
            aria-label={t("Main_NoThanksDoNotShare")}
            onClick={() => {
              void answer(false);
            }}
          >
            {t("Main_NoThanks")}
          </button>
          <button
            type="button"
            disabled={busy}
            aria-label={t("Main_YesShareAnonymousUsageData")}
            onClick={() => {
              void answer(true);
            }}
          >
            {t("Main_YesShareUsageData")}
          </button>
        </>
      }
    >
      <p className="usage-notice-body">{t("Main_ThisAppCanSendAnonymous")}</p>
      {client.openPrivacyPolicy === undefined ? null : (
        <button
          type="button"
          className="link-button"
          aria-label={t("Main_ReadThePrivacyPolicyOpens")}
          onClick={openPolicy}
        >
          {t("Main_ReadThePrivacyPolicy")}
        </button>
      )}
    </Dialog>
  );
}
