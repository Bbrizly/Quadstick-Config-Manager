import { useI18n } from "../../i18n";
import { OutputPrompt, promptSrc } from "../visualizer/OutputPrompt";
import { humanize, tokenLabel, type BindingCells, type ZoneId } from "../visualizer/deviceSummary";
import { DuplicateMark } from "./DuplicateMark";
import { NO_DUPLICATES, type DuplicateCounts } from "./duplicates";

export interface SentenceCardProps {
  readonly row: BindingCells;
  readonly zoneId: ZoneId;
  readonly selected: boolean;
  readonly live?: boolean;
  /** The other rows of a button combo: same inputs, one output each, one card. */
  readonly together?: readonly BindingCells[];
  /** This mode's repeat counts; a token used twice gets a mark. */
  readonly counts?: DuplicateCounts;
  readonly onSelect: () => void;
}

function cardInput(token: string, zoneId: ZoneId): string {
  if (zoneId === "combo") return token.replaceAll("_", " ");
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

const NO_ROWS: readonly BindingCells[] = [];


/** Avalonia SentenceCard compact Input→Output form with tinted pills. */
export function SentenceCard({
  row,
  zoneId,
  selected,
  live = false,
  together = NO_ROWS,
  counts = NO_DUPLICATES,
  onSelect,
}: SentenceCardProps) {
  const { t } = useI18n();
  const fn = row.cells[1]?.trim() ?? "";
  const inputs = row.cells.slice(2, 10).map((value) => value.trim()).filter(Boolean);
  const outputs = [row, ...together].map((member) => {
    const token = member.cells[0]?.trim() ?? "";
    const actionName = member.cells[11]?.trim() ?? "";
    const label =
      actionName.length > 0 ? actionName : token.length > 0 ? tokenLabel(token) : t("Main_NothingYet");
    return { row: member.row, token, label };
  });
  const outputLabel = outputs.map((entry) => entry.label).join(" + ");
  const inputLabels =
    inputs.length > 0 ? inputs.map((input) => cardInput(input, zoneId)) : [t("Main_NoInput")];
  // The combo's other rows are drawn on this card, so they are not repeats.
  const inputRepeats = inputs.map((input) => counts.input(input) - together.length);
  const functionLabel = fn.length > 0 ? humanize(fn) : "";
  // The marks sit in the hidden visual grid, so the card's name carries them.
  const repeats = [
    ...outputs.map((entry) => ({ label: entry.label, count: counts.output(entry.token) })),
    ...inputs.map((input, index) => ({ label: inputLabels[index] ?? input, count: inputRepeats[index] ?? 0 })),
  ]
    .filter((entry) => entry.count >= 2)
    .map((entry) => `${entry.label}: ${t("Main_UsedNTimesInThisMode", [entry.count])}`);
  const sentence = `${t("Main_PressVerb")} ${outputLabel} ${t("Main_WhenYou")} ${inputLabels.join(", ")}`;
  const spoken = repeats.length === 0 ? sentence : `${sentence}. ${repeats.join(". ")}`;

  return (
    <button
      className={[
        "sentence-card",
        selected ? "selected" : "",
        live ? "live" : "",
      ]
        .filter(Boolean)
        .join(" ")}
      type="button"
      data-binding-row={row.row}
      data-testid={`binding-row-${String(row.row)}`}
      data-live={live ? "true" : undefined}
      aria-pressed={selected}
      aria-label={live ? t("Main_RowSendingNow", [spoken]) : spoken}
      onClick={onSelect}
    >
      {live ? (
        <span className="sentence-live-pip" role="img" aria-label={t("Main_SendingNow")} />
      ) : (
        <span className="sentence-live-pip-slot" aria-hidden="true" />
      )}
      <span className="sentence-grid" aria-hidden="true">
        <span className="sentence-word">{t("Main_PressVerb")}</span>
        <span className="sentence-inputs">
          {outputs.map((entry) => (
            <span className="pill tint-output" key={entry.row}>
              <OutputPrompt token={entry.token} label={entry.label} size={30} />
              {promptSrc(entry.token) !== null ? (
                <span className="visually-hidden">{entry.label}</span>
              ) : null}
              <DuplicateMark count={counts.output(entry.token)} />
            </span>
          ))}
        </span>
        <span className="sentence-word">{t("Main_WhenYou")}</span>
        <span className="sentence-inputs">
          {inputLabels.map((label, index) => (
            <span className="pill tint-input" key={label}>
              {label}
              <DuplicateMark count={inputRepeats[index] ?? 0} />
            </span>
          ))}
        </span>
        {functionLabel !== "" && fn.trim().toLowerCase() !== "normal" ? (
          <>
            <span className="sentence-word">{t("Main_AsJoiner")}</span>
            <span className="pill tint-function">{functionLabel}</span>
          </>
        ) : null}
      </span>
    </button>
  );
}
