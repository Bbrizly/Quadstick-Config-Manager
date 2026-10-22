import { Dialog } from "../components/primitives/Dialog";
import { useI18n } from "../i18n";
import type { EditorSnapshot, QcmClient } from "../platform";

/**
 * The prompt's Save: Save As for a profile that has no file yet, then close.
 * False when the person backs out of the Save As dialog; the session stays.
 */
export async function saveThenClose(client: QcmClient, editor: EditorSnapshot): Promise<boolean> {
  if (editor.saveTarget === null) {
    const receipt = await client.saveProfileAs(editor.sessionId, editor.revision);
    if (receipt === null) return false;
    const outcome = await client.closeProfile(editor.sessionId, "if_clean");
    if (outcome.kind === "keptOpenUnsavedChanges") {
      throw new Error("profile remained dirty after save as");
    }
  } else {
    await client.closeProfile(editor.sessionId, "save");
  }
  return true;
}

interface UnsavedChangesDialogProps {
  readonly open: boolean;
  readonly closing: boolean;
  /** Device preferences save from their own page, so their prompt has no Save. */
  readonly canSave: boolean;
  readonly onCancel: () => void;
  readonly onDiscard: () => void;
  readonly onSave: () => void;
}

/** Save / Don't save / Cancel, asked before unsaved work is left. */
export function UnsavedChangesDialog({ open, closing, canSave, onCancel, onDiscard, onSave }: UnsavedChangesDialogProps) {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      title={t("Shell_Profile")}
      onClose={onCancel}
      actions={
        <>
          <button type="button" disabled={closing} onClick={onCancel}>{t("Device_Cancel")}</button>
          <button type="button" disabled={closing} onClick={onDiscard}>{t("Main_DonTSave")}</button>
          {canSave ? (
            <button className="primary" type="button" data-autofocus disabled={closing} onClick={onSave}>
              {t("Shell_SaveCtrlS")}
            </button>
          ) : null}
        </>
      }
    >
      <p>{t("Main_ThisProfileHasUnsavedChanges")}</p>
      {canSave ? null : <p>{t("Rewrite_SaveFromThePageFirst")}</p>}
    </Dialog>
  );
}
