import { useState, type ReactNode } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import type { EditorSnapshot, QcmClient } from "../../platform";

export interface ShareSetupDialogProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  /** The action the person asked for; the last step's button says it. */
  readonly finishLabel: string;
  /** True when the profile has never been saved, so a step asks for it. */
  readonly needsSave: boolean;
  readonly connected: boolean;
  readonly onConnected: () => void;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
  /** `Completed`: true only from the last step. The caller then runs the action. */
  readonly onClose: (completed: boolean) => void;
}

/**
 * `ShareSetupWindow.cs`: connect, save, then the thing they asked for. Each
 * step says where it stands in words, never only a colour. The dialog never
 * shares anything itself.
 */
export function ShareSetupDialog({
  client,
  snapshot,
  finishLabel,
  needsSave,
  connected,
  onConnected,
  onSnapshot,
  onClose,
}: ShareSetupDialogProps) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState("");
  const total = needsSave ? 3 : 2;
  const saved = snapshot.saveTarget !== null;
  const ready = connected && (!needsSave || saved);

  const connect = async (): Promise<void> => {
    const run = client.connectGoogle;
    if (run === undefined || busy) return;
    setBusy(true);
    setStatus(t("Share_WaitingForGoogleInYour"));
    let ok = false;
    try {
      ok = (await run.call(client)).connected;
    } catch {
      ok = false;
    }
    setStatus(ok ? "" : t("Share_CouldNotConnectToGoogle"));
    if (ok) onConnected();
    setBusy(false);
  };

  const save = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const receipt = await client.saveProfileAs(snapshot.sessionId, snapshot.revision);
      if (receipt === null) setStatus(t("Share_ThisProfileWasNotSaved"));
      else onSnapshot(await client.getProfileSnapshot(snapshot.sessionId));
    } catch {
      setStatus(t("Share_ThisProfileWasNotSaved"));
    } finally {
      setBusy(false);
    }
  };

  const step = (number: number, title: string, done: boolean, action: ReactNode) => (
    <section
      className="share-step"
      aria-label={t(done ? "Share_StepDone" : "Share_StepNotDone", [String(number), String(total), title])}
    >
      <h3>{t("Share_StepNumberOfTotalTitle", [String(number), String(total), title])}</h3>
      <p className="settings-caption">{done ? t("Main_Done") : t("Share_NotDoneYet")}</p>
      {action}
    </section>
  );

  const connectTitle = t("Rewrite_ConnectGoogleDrive");
  const saveTitle = t("Share_SaveThisProfile");

  return (
    <Dialog
      open
      title={t("Share_SetUpGoogleSheets")}
      onClose={() => onClose(false)}
      actions={
        <button type="button" aria-label={t("Share_CloseWithoutSharing")} onClick={() => onClose(false)}>
          {t("Share_Cancel")}
        </button>
      }
    >
      <p>{t("Share_SharingAProfilePutsIt")}</p>
      {step(
        1,
        connectTitle,
        connected,
        <button
          type="button"
          data-autofocus={connected ? undefined : true}
          disabled={busy || connected}
          aria-label={connected ? t("Share_TitleAlreadyDone", [connectTitle]) : connectTitle}
          onClick={() => void connect()}
        >
          {t("Link_Connect")}
        </button>,
      )}
      {needsSave
        ? step(
            2,
            saveTitle,
            saved,
            <button
              type="button"
              disabled={busy || saved}
              aria-label={saved ? t("Share_TitleAlreadyDone", [saveTitle]) : saveTitle}
              onClick={() => void save()}
            >
              {t("Main_Save")}
            </button>,
          )
        : null}
      {step(
        total,
        finishLabel,
        false,
        <button
          className="primary-action"
          type="button"
          disabled={!ready || busy}
          aria-label={ready ? finishLabel : t("Share_FinishLabelNotAvailableYetFinish", [finishLabel])}
          onClick={() => onClose(true)}
        >
          {finishLabel}
        </button>,
      )}
      <LiveRegion visuallyHidden={false}>{status}</LiveRegion>
    </Dialog>
  );
}
