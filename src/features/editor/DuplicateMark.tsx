import { useI18n } from "../../i18n";

/** `DuplicateChip`: nothing under two; the count itself, in words for a reader. */
export function DuplicateMark({ count }: { readonly count: number }) {
  const { t } = useI18n();
  if (count < 2) return null;
  const said = t("Main_UsedNTimesInThisMode", [count]);
  return (
    <span className="duplicate-mark" role="img" aria-label={said} title={said}>
      {t("Main_DuplicateMark", [count])}
    </span>
  );
}
