import { useI18n } from "../../i18n";
import { humanize, tokenLabel, type BindingCells, type ZoneId } from "../visualizer/deviceSummary";

export interface SentenceCardProps {
  readonly row: BindingCells;
  readonly zoneId: ZoneId;
  readonly selected: boolean;
  readonly onSelect: () => void;
}

function cardInput(token: string, zoneId: ZoneId): string {
  if (zoneId === "combo") return token.replaceAll("_", " ");
  // Strip zone prefix the way Avalonia CardInput / StripInput does for display.
  let s = token;
  for (const prefix of [
    "mp_left_center_",
    "mp_right_center_",
    "mp_left_right_",
    "mp_triple_",
    "mp_right_mode_",
    "mp_left_",
    "mp_center_",
    "mp_right_",
    "right_",
  ]) {
    if (s.startsWith(prefix)) {
      s = s.slice(prefix.length);
      break;
    }
  }
  if (s.endsWith("_soft")) s = `soft ${s.slice(0, -5)}`;
  return s.replaceAll("_", " ");
}

/** Avalonia SentenceCard compact Input→Output form with tinted pills. */
export function SentenceCard({ row, zoneId, selected, onSelect }: SentenceCardProps) {
  const { t } = useI18n();
  const output = row.cells[0]?.trim() ?? "";
  const fn = row.cells[1]?.trim() ?? "";
  const actionName = row.cells[11]?.trim() ?? "";
  const inputs = row.cells.slice(2, 10).map((value) => value.trim()).filter(Boolean);
  const outputLabel = actionName.length > 0 ? actionName : output.length > 0 ? tokenLabel(output) : t("Main_NothingYet");
  const inputLabels =
    inputs.length > 0
      ? inputs.map((input) => cardInput(input, zoneId))
      : [t("Main_NoInput")];
  const functionLabel = fn.length > 0 ? humanize(fn) : "";

  return (
    <button
      className={selected ? "sentence-card selected" : "sentence-card"}
      type="button"
      data-binding-row={row.row}
      data-testid={`binding-row-${String(row.row)}`}
      aria-pressed={selected}
      onClick={onSelect}
    >
      <span className="sentence-grid" aria-hidden="true">
        <span className="sentence-word">{t("Main_PressVerb")}</span>
        <span className="pill tint-output">{outputLabel}</span>
        <span className="sentence-word">{t("Main_WhenYou")}</span>
        <span className="sentence-inputs">
          {inputLabels.map((label) => (
            <span className="pill tint-input" key={label}>
              {label}
            </span>
          ))}
        </span>
        {functionLabel !== "" && functionLabel !== "Normal" ? (
          <>
            <span className="sentence-word">{t("Main_AsJoiner")}</span>
            <span className="pill tint-function">{functionLabel}</span>
          </>
        ) : null}
      </span>
      <span className="visually-hidden">
        {`${t("Main_PressVerb")} ${outputLabel} ${t("Main_WhenYou")} ${inputLabels.join(", ")}`}
      </span>
    </button>
  );
}
