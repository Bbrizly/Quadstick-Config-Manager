import { useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";

export interface SaveTemplateDialogProps {
  readonly open: boolean;
  readonly suggested: string;
  readonly busy: boolean;
  readonly onSave: (name: string) => void;
  readonly onClose: () => void;
}

/** `SaveAsTemplateAsync`: a name, then a copy under Use template on Home. */
export function SaveTemplateDialog({ open, suggested, busy, onSave, onClose }: SaveTemplateDialogProps) {
  const { t } = useI18n();
  const [name, setName] = useState(suggested);
  const [seen, setSeen] = useState(suggested);
  if (seen !== suggested) {
    setSeen(suggested);
    setName(suggested);
  }
  return (
    <Dialog
      open={open}
      title={t("Main_SaveAsTemplate")}
      onClose={onClose}
      actions={
        <>
          <button type="button" disabled={busy} onClick={onClose}>
            {t("Main_Cancel")}
          </button>
          <button className="primary" type="button" disabled={busy} onClick={() => onSave(name)}>
            {t("Main_SaveTemplate")}
          </button>
        </>
      }
    >
      <p>{t("Main_KeepsACopyYouCan")}</p>
      <input
        className="template-name"
        aria-label={t("Main_NameForThisTemplate")}
        value={name}
        data-autofocus
        onChange={(event) => setName(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") onSave(name);
        }}
      />
    </Dialog>
  );
}
