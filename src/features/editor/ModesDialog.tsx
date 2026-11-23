import { Dialog } from "../../components/primitives/Dialog";
import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import type { EditorOp, EditorSnapshot, Mode } from "../../platform";
import { MoveGlyph } from "./MoveGlyph";

export interface ModesDialogProps {
  readonly open: boolean;
  readonly snapshot: EditorSnapshot;
  readonly busy: boolean;
  readonly armedDelete: number | null;
  readonly onClose: () => void;
  readonly onApply: (ops: readonly EditorOp[], after?: (next: EditorSnapshot) => void) => void;
  readonly onMove: (mode: Mode, delta: -1 | 1) => void;
  readonly onDelete: (mode: Mode) => void;
  readonly onSelect: (index: number) => void;
  readonly canMove: (mode: Mode, delta: -1 | 1) => boolean;
}

/**
 * The pencil beside "Modes" (`ModesWindow`): rename, reorder, copy and delete
 * every sheet in one place, so the list in the sidebar stays one plain line
 * per mode.
 */
export function ModesDialog({
  open,
  snapshot,
  busy,
  armedDelete,
  onClose,
  onApply,
  onMove,
  onDelete,
  onSelect,
  canMove,
}: ModesDialogProps) {
  const { t } = useI18n();
  const profileModes = snapshot.modes.filter((mode) => mode.kind === "mode");
  return (
    <Dialog
      open={open}
      title={t("Modes_Modes")}
      onClose={onClose}
      actions={
        <button className="primary" type="button" onClick={onClose}>
          {t("Modes_Done")}
        </button>
      }
    >
      <p className="settings-caption">{t("Modes_AModeIsAFull")}</p>
      <ol className="modes-dialog-list">
        {snapshot.modes
          .filter((mode) => mode.kind !== "infrared")
          .map((mode) => {
            const moveButtons = (
              <>
                <button
                  type="button"
                  disabled={busy || !canMove(mode, -1)}
                  aria-label={t("Modes_MoveNameUp", [mode.name])}
                  onClick={() => onMove(mode, -1)}
                >
                  <MoveGlyph up />
                </button>
                <button
                  type="button"
                  disabled={busy || !canMove(mode, 1)}
                  aria-label={t("Modes_MoveNameDown", [mode.name])}
                  onClick={() => onMove(mode, 1)}
                >
                  <MoveGlyph up={false} />
                </button>
              </>
            );
            if (mode.kind !== "mode") {
              return (
                <li className="modes-dialog-row" key={`sheet-${String(mode.index)}`}>
                  <span className="modes-dialog-name">{t("Modes_PreferencesDeviceSettings")}</span>
                  <span className="mode-row-actions">
                    {moveButtons}
                    <span className="mode-delete-slot">
                      <span className="mode-delete-sizer" aria-hidden="true">
                        <Icon name="delete" size={18} />
                        <span>{t("Modes_ReallyDelete")}</span>
                      </span>
                      <button
                        type="button"
                        className={armedDelete === mode.index ? "mode-delete armed" : "mode-delete"}
                        disabled={busy}
                        aria-label={armedDelete === mode.index ? t("Modes_ReallyDelete") : t("Shell_Delete")}
                        onClick={() => onDelete(mode)}
                      >
                        <Icon name="delete" size={18} />
                        {armedDelete === mode.index ? <span aria-hidden="true">{t("Modes_ReallyDelete")}</span> : null}
                      </button>
                    </span>
                  </span>
                </li>
              );
            }
            return (
              <li className="modes-dialog-row" data-testid={`modes-dialog-row-${String(mode.index)}`} key={`mode-${String(mode.index)}`}>
                <span className="mode-number" aria-hidden="true">{mode.number}</span>
                <input
                  key={`${String(snapshot.revision)}-${String(mode.index)}`}
                  className="mode-name-input"
                  aria-label={t("Modes_NameOfModeOrdinal", [mode.number ?? mode.index + 1])}
                  defaultValue={mode.name}
                  maxLength={40}
                  disabled={busy}
                  onBlur={(event) => {
                    const name = event.currentTarget.value.trim();
                    if (name !== "" && name !== mode.name) onApply([{ op: "rename_mode", sheet: mode.index, name }]);
                  }}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") event.currentTarget.blur();
                  }}
                />
                <span className="mode-row-actions">
                  {moveButtons}
                  <button
                    type="button"
                    disabled={busy}
                    aria-label={t("Modes_MakeACopyOfName", [mode.name])}
                    onClick={() =>
                      onApply([{ op: "duplicate_mode", sheet: mode.index, name: `${mode.name} copy` }], (next) => {
                        const copy = next.modes.findLast((candidate) => candidate.kind === "mode");
                        if (copy !== undefined) onSelect(copy.index);
                      })
                    }
                  >
                    <Icon name="template" size={18} />
                  </button>
                  <span className="mode-delete-slot">
                    <span className="mode-delete-sizer" aria-hidden="true">
                      <Icon name="delete" size={18} />
                      <span>{t("Modes_ReallyDelete")}</span>
                    </span>
                    <button
                      type="button"
                      className={armedDelete === mode.index ? "mode-delete armed" : "mode-delete"}
                      disabled={busy || profileModes.length <= 1}
                      aria-label={armedDelete === mode.index ? t("Modes_ReallyDeleteName", [mode.name]) : t("Modes_DeleteName", [mode.name])}
                      onClick={() => onDelete(mode)}
                    >
                      <Icon name="delete" size={18} />
                      {armedDelete === mode.index ? <span aria-hidden="true">{t("Modes_ReallyDelete")}</span> : null}
                    </button>
                  </span>
                </span>
              </li>
            );
          })}
      </ol>
    </Dialog>
  );
}
