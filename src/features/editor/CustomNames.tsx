import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import { KNOWN_OUTPUTS, type EditorOp, type EditorSnapshot } from "../../platform";
import { humanize } from "../visualizer/deviceSummary";
import type { EditorStatus } from "./ProblemsDock";

/** Column L: the profile's own word for a row's output (`ProfileFile.ActionColumn`). */
export const ACTION_COLUMN = 11;
/** `ProfileFile.MaxActionName`, in UTF-16 code units like .NET's Length. */
export const MAX_ACTION_NAME = 40;

export interface CustomName {
  readonly name: string;
  readonly token: string;
  readonly rows: readonly number[];
}

/** Every binding row of every mode, as (row, cells). */
function modeRows(snapshot: EditorSnapshot): { row: number; cells: readonly string[] }[] {
  const out: { row: number; cells: readonly string[] }[] = [];
  for (const mode of snapshot.modes) {
    if (mode.kind !== "mode") continue;
    for (let offset = 0; offset < mode.bindingCount; offset += 1) {
      const row = mode.startRow + 3 + offset;
      out.push({ row, cells: snapshot.grid[row - 1] ?? [] });
    }
  }
  return out;
}

const same = (a: string, b: string): boolean => a.toLowerCase() === b.toLowerCase();

/** `CustomNameRows`: names already on rows first, in row order, then drafts. */
export function customNames(snapshot: EditorSnapshot, drafts: ReadonlyMap<string, string>): CustomName[] {
  const names: { name: string; token: string; rows: number[] }[] = [];
  for (const { row, cells } of modeRows(snapshot)) {
    const name = (cells[ACTION_COLUMN] ?? "").trim();
    if (name === "") continue;
    const found = names.find((entry) => same(entry.name, name));
    if (found === undefined) names.push({ name, token: (cells[0] ?? "").trim(), rows: [row] });
    else found.rows.push(row);
  }
  for (const [name, token] of drafts) {
    if (!names.some((entry) => same(entry.name, name))) names.push({ name, token, rows: [] });
  }
  return names;
}

/** `ProfileFile.IsLegalActionName`: 1 to 40 characters and not already an output's name. */
export function legalActionName(name: string): boolean {
  const trimmed = name.trim();
  if (trimmed.length === 0 || trimmed.length > MAX_ACTION_NAME) return false;
  const token = trimmed.replaceAll(" ", "_").toLowerCase();
  return ![...KNOWN_OUTPUTS].some((output) => output.toLowerCase() === token);
}

export interface CustomNamesTableProps {
  readonly snapshot: EditorSnapshot;
  readonly drafts: ReadonlyMap<string, string>;
  readonly busy: boolean;
  readonly onDrafts: (next: ReadonlyMap<string, string>) => void;
  readonly onApply: (ops: readonly EditorOp[], after?: () => void) => void;
  readonly onStatus: (status: EditorStatus) => void;
}

/**
 * The Custom output names table (`CustomNames.cs`): your own word for an
 * output. It is not a sheet in the file; a name in use lives in its rows'
 * column L, which the device ignores. Names no mapping uses yet wait here for
 * the session.
 */
export function CustomNamesTable({ snapshot, drafts, busy, onDrafts, onApply, onStatus }: CustomNamesTableProps) {
  const { t, plural } = useI18n();
  const rows = customNames(snapshot, drafts);

  const rename = (entry: CustomName, typed: string): void => {
    const name = typed.trim();
    if (name === entry.name) return;
    if (name.length === 0 || name.length > MAX_ACTION_NAME) {
      onStatus({ kind: "warning", text: t("Names_ANameHasToBe", [MAX_ACTION_NAME]) });
      return;
    }
    if (!legalActionName(name)) {
      onStatus({ kind: "warning", text: t("Names_NameIsAlreadyWhatThe", [name]) });
      return;
    }
    if (rows.some((other) => same(other.name, name) && !same(other.name, entry.name))) {
      onStatus({ kind: "warning", text: t("Names_ThisProfileAlreadyHasA", [name]) });
      return;
    }
    const said = (): void => onStatus({ kind: "ready", text: t("Names_RenamedOldToNew", [entry.name, name]) });
    if (drafts.has(entry.name)) {
      const next = new Map(drafts);
      next.delete(entry.name);
      next.set(name, entry.token);
      onDrafts(next);
    }
    if (entry.rows.length === 0) {
      said();
      return;
    }
    onApply(
      [{ op: "group", ops: entry.rows.map((row) => ({ op: "set_cell", row, col: ACTION_COLUMN, value: name }) as const) }],
      said,
    );
  };

  const retarget = (entry: CustomName, typed: string): void => {
    const token = typed.trim();
    if (token === "" || token === entry.token) return;
    if (drafts.has(entry.name)) onDrafts(new Map(drafts).set(entry.name, token));
    const said = (): void => onStatus({ kind: "ready", text: t("Names_NameIsNowHumanizeToken", [entry.name, humanize(token)]) });
    if (entry.rows.length === 0) {
      said();
      return;
    }
    onApply([{ op: "group", ops: entry.rows.map((row) => ({ op: "set_cell", row, col: 0, value: token }) as const) }], said);
  };

  const remove = (entry: CustomName): void => {
    if (drafts.has(entry.name)) {
      const next = new Map(drafts);
      next.delete(entry.name);
      onDrafts(next);
    }
    const removed = t("Names_Removed", [entry.name]);
    const said = (): void =>
      onStatus({
        kind: "ready",
        text: entry.rows.length === 0 ? removed : `${removed} ${plural("Names_MappingNowReal", entry.rows.length, [entry.rows.length])}`,
      });
    if (entry.rows.length === 0) {
      said();
      return;
    }
    onApply([{ op: "group", ops: entry.rows.map((row) => ({ op: "set_cell", row, col: ACTION_COLUMN, value: "" }) as const) }], said);
  };

  return (
    <div className="rows-grid custom-names" role="table" aria-label={t("Names_CustomOutputNames")}>
      <div className="rows-grid-line names-line rows-grid-head" role="row">
        <span role="columnheader" className="row-number" aria-hidden="true" />
        <span role="columnheader" className="tint-swatch tint-output">{t("Names_OutputRealButton")}</span>
        <span role="columnheader" className="tint-swatch tint-function">{t("Names_YourNameForIt")}</span>
      </div>
      {rows.map((entry, index) => (
        <div className="rows-grid-line names-line" role="row" key={entry.name}>
          <span role="cell" className="row-number">{index + 1}</span>
          <span role="cell" className="grid-cell tint-output">
            <input
              key={`${String(snapshot.revision)}-${entry.name}-out`}
              defaultValue={entry.token}
              disabled={busy}
              list="qcm-output-names"
              aria-label={t("Names_OutputThatNameStandsFor", [entry.name])}
              onBlur={(event) => retarget(entry, event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") event.currentTarget.blur();
              }}
            />
          </span>
          <span role="cell" className="grid-cell plain">
            <input
              key={`${String(snapshot.revision)}-${entry.name}-name`}
              defaultValue={entry.name}
              maxLength={MAX_ACTION_NAME}
              disabled={busy}
              aria-label={entry.token === "" ? t("Names_YourNameForThisOutput") : t("Names_YourNameForHumanizeToken", [humanize(entry.token)])}
              onBlur={(event) => rename(entry, event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") event.currentTarget.blur();
              }}
            />
          </span>
          <span role="cell">
            <button
              type="button"
              className="icon danger"
              disabled={busy}
              title={t("Names_RemoveThisName")}
              aria-label={t("Names_RemoveTheNameName", [entry.name])}
              onClick={() => remove(entry)}
            >
              <Icon name="delete" size={20} />
            </button>
          </span>
          <span role="cell" className="muted names-used">
            {entry.rows.length === 0 ? t("Names_NotUsedYet") : t("Names_UsedOnCount", [plural("Count_Mapping", entry.rows.length, [entry.rows.length])])}
          </span>
        </div>
      ))}
      {rows.length === 0 ? <p className="muted rows-empty">{t("Names_NoNamesYetClickAdd")}</p> : null}
      <datalist id="qcm-output-names">
        {[...KNOWN_OUTPUTS].map((output) => (
          <option key={output} value={output}>{output}</option>
        ))}
      </datalist>
    </div>
  );
}

/** `AddCustomName`: "New name", then "New name 2", past any taken. */
export function nextDraftName(taken: readonly CustomName[], t: (key: "Names_NewName" | "Names_NewNameI", values?: readonly unknown[]) => string): string {
  let name = t("Names_NewName");
  for (let i = 2; taken.some((entry) => same(entry.name, name)); i += 1) name = t("Names_NewNameI", [i]);
  return name;
}
