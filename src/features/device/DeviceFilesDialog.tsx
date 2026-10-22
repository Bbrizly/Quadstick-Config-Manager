import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import type { EditorSnapshot, QcmClient } from "../../platform";
import { DeviceLibraryPage } from "./DeviceLibraryPage";

/**
 * `DeviceFilesWindow`: the files on a plugged-in QuadStick, opened from the
 * device page's Manage files and Home's. The page inside is mounted only
 * while the dialog is (open, or fading out), so the drive is read when the
 * window opens and never while it is shut.
 */
export function DeviceFilesDialog({
  client,
  open,
  onClose,
  onOpenProfile,
}: {
  readonly client: QcmClient;
  readonly open: boolean;
  readonly onClose: () => void;
  readonly onOpenProfile: (snapshot: EditorSnapshot) => void;
}) {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      wide
      title={t("Device_FilesOnYourQuadStick")}
      onClose={onClose}
    >
      {/* Close sits beside the page's Refresh, so the page draws both. */}
      <DeviceLibraryPage client={client} onOpenProfile={onOpenProfile} onClose={onClose} inDialog />
    </Dialog>
  );
}
