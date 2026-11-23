import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";

import { Icon } from "../../components/primitives/icons";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import { InfoButton } from "../editor/Popover";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type CommunityCatalog,
  type CommunityProfile,
  type QcmClient,
  type WorkbookImportReview,
} from "../../platform";

import "./community.css";

interface CommunityProfilesPageProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
}

// `_status` in CommunityProfilesView. `noMatch` marks the line the filter
// wrote, so the filter can take it back once something matches again.
interface Status {
  readonly text: string;
  readonly error: boolean;
  readonly noMatch?: boolean;
}

const QUIET: Status = { text: "", error: false };

// Long enough that typing a word is announced once, not once a letter.
export const FILTER_ANNOUNCE_DELAY_MS = 450;
const TYPE_AHEAD_RESET_MS = 700;

function matches(profile: CommunityProfile, query: string): boolean {
  if (query.length === 0) return true;
  const needle = query.toLocaleLowerCase();
  return [profile.name, profile.csvName, profile.connection, profile.pointer].some((field) =>
    field.toLocaleLowerCase().includes(needle),
  );
}

function details(profile: CommunityProfile): string[] {
  return [profile.csvName, profile.connection, profile.pointer].filter((part) => part.trim().length > 0);
}

export function CommunityProfilesPage({ client, onReview }: CommunityProfilesPageProps) {
  const { t, plural } = useI18n();
  const listRef = useRef<HTMLDivElement | null>(null);
  const focusSelected = useRef(false);
  const typed = useRef(false);
  const typeAhead = useRef({ text: "", at: 0 });
  const load = client.loadCommunityCatalog;
  const importProfile = client.importCommunityProfile;
  const openSheet = client.openCommunitySheet;
  const [catalog, setCatalog] = useState<CommunityCatalog | null>(null);
  const [query, setQuery] = useState("");
  const [selectedId, setSelectedId] = useState("");
  const [loading, setLoading] = useState(load !== undefined);
  const [importing, setImporting] = useState(false);
  const [opening, setOpening] = useState(false);
  const [status, setStatus] = useState<Status>(QUIET);
  const [announcedCount, setAnnouncedCount] = useState("");
  // Nothing downloaded and nothing saved (`CommunityCatalogException`).
  const [unavailable, setUnavailable] = useState(false);

  const say = (text: string): void => setStatus({ text, error: false });
  const fail = (text: string): void => setStatus({ text, error: true });

  useEffect(() => {
    if (load === undefined) return;
    let disposed = false;
    void load
      .call(client, false)
      .then((next) => {
        if (disposed) return;
        setCatalog(next);
        setSelectedId(next.profiles[0]?.sheetId ?? "");
        setStatus(QUIET);
      })
      .catch(() => {
        if (disposed) return;
        setUnavailable(true);
        setStatus({ text: t("Community_CheckYourInternetConnectionAnd"), error: true });
      })
      .finally(() => {
        if (!disposed) setLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, [client, load, t]);

  const filtered = useMemo(
    () => (catalog?.profiles ?? []).filter((profile) => matches(profile, query.trim())),
    [catalog, query],
  );
  const selected = filtered.find((profile) => profile.sheetId === selectedId) ?? filtered[0] ?? null;
  const selectedIndex = selected === null ? -1 : filtered.indexOf(selected);

  const countText = (() => {
    if (catalog === null || catalog.profiles.length === 0) return "";
    const trimmed = query.trim();
    if (trimmed.length === 0) {
      return t("Community_ShowingAllProfilesAllCount", [
        plural("Count_Profile", catalog.profiles.length, [catalog.profiles.length]),
      ]);
    }
    if (filtered.length === 0) return t("Community_NoProfilesMatchQuery", [trimmed]);
    return t("Community_ShowingMatchesCountOfAll", [filtered.length, catalog.profiles.length, trimmed]);
  })();

  // `ApplyFilter`: an empty result puts "Nothing matches" on the status line,
  // and a result again takes it back. The count is spoken once typing settles.
  const nothingMatches = catalog !== null && catalog.profiles.length > 0 && filtered.length === 0;
  useEffect(() => {
    if (!typed.current) return;
    const timer = setTimeout(() => {
      if (nothingMatches) {
        setAnnouncedCount("");
        setStatus({ text: t("Community_NothingMatchesThatSearchClear"), error: false, noMatch: true });
      } else {
        setAnnouncedCount(countText);
        setStatus((current) => (current.noMatch === true ? QUIET : current));
      }
    }, FILTER_ANNOUNCE_DELAY_MS);
    return () => clearTimeout(timer);
  }, [countText, nothingMatches, t]);

  // Roving focus: the picked row is the list's one tab stop, and a key that
  // moves the pick moves focus with it.
  useEffect(() => {
    if (!focusSelected.current || selectedIndex < 0) return;
    focusSelected.current = false;
    const row = listRef.current?.querySelector<HTMLElement>(`[data-index="${String(selectedIndex)}"]`);
    row?.focus();
    row?.scrollIntoView?.({ block: "nearest" });
  }, [selectedIndex]);

  const pick = (index: number, focus: boolean): void => {
    const next = filtered[Math.max(0, Math.min(filtered.length - 1, index))];
    if (next === undefined) return;
    focusSelected.current = focus;
    setSelectedId(next.sheetId);
    if (focus && next === selected) {
      listRef.current?.querySelector<HTMLElement>(`[data-index="${String(selectedIndex)}"]`)?.focus();
      focusSelected.current = false;
    }
  };

  const refresh = async (): Promise<void> => {
    if (load === undefined || loading) return;
    setLoading(true);
    say(t("Community_CheckingQuadstickComForNew"));
    try {
      const next = await load.call(client, true);
      setCatalog(next);
      setUnavailable(false);
      setSelectedId(next.profiles[0]?.sheetId ?? "");
      // A refresh that fell back to the saved copy says so, or it looks
      // exactly like one that worked.
      setStatus(next.fromCache ? { text: t("Community_CouldNotReachQuadstickCom"), error: true } : QUIET);
    } catch {
      setCatalog(null);
      setUnavailable(true);
      fail(t("Community_CheckYourInternetConnectionAnd"));
    } finally {
      setLoading(false);
    }
  };

  const importSelected = async (): Promise<void> => {
    if (selected === null || importProfile === undefined || importing) {
      if (selected === null) say(t("Community_PickAProfileFromThe"));
      return;
    }
    setImporting(true);
    say(t("Community_ImportingPickedName", [selected.name]));
    try {
      const review = await importProfile.call(client, selected.sheetId, selected.csvName);
      setStatus(QUIET);
      onReview(review);
    } catch (reason) {
      const why = localizedErrorMessage(asQcmError(reason).payload, t);
      fail(t("Community_CouldNotImportPickedName", [selected.name, why]));
    } finally {
      setImporting(false);
    }
  };

  const openSelected = async (): Promise<void> => {
    if (selected === null || openSheet === undefined || opening) {
      if (selected === null) say(t("Community_PickAProfileFromThe2"));
      return;
    }
    setOpening(true);
    try {
      await openSheet.call(client, selected.sheetId);
      say(t("Community_OpenedThePickedNameSheet", [selected.name]));
    } catch (reason) {
      fail(t("Community_CouldNotOpenTheSheet", [localizedErrorMessage(asQcmError(reason).payload, t)]));
    } finally {
      setOpening(false);
    }
  };

  const onListKey = (event: KeyboardEvent<HTMLElement>): void => {
    const list = listRef.current;
    const row = list?.querySelector<HTMLElement>('[role="option"]');
    const page = list && row && row.offsetHeight > 0 ? Math.max(1, Math.floor(list.clientHeight / row.offsetHeight) - 1) : 10;
    const moves: Record<string, number> = {
      ArrowDown: selectedIndex + 1,
      ArrowUp: selectedIndex - 1,
      Home: 0,
      End: filtered.length - 1,
      PageDown: selectedIndex + page,
      PageUp: selectedIndex - page,
    };
    const target = moves[event.key];
    if (target !== undefined) {
      event.preventDefault();
      pick(target, true);
      return;
    }
    if (event.key === "Enter") {
      // Same gate as the button, so holding Enter cannot start two imports.
      event.preventDefault();
      void importSelected();
      return;
    }
    if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey && event.key !== " ") {
      const now = Date.now();
      const buffer = now - typeAhead.current.at > TYPE_AHEAD_RESET_MS ? event.key : typeAhead.current.text + event.key;
      typeAhead.current = { text: buffer, at: now };
      const needle = buffer.toLocaleLowerCase();
      const start = buffer.length === 1 ? selectedIndex + 1 : selectedIndex;
      for (let step = 0; step < filtered.length; step += 1) {
        const index = (start + step) % filtered.length;
        if (filtered[index]!.name.toLocaleLowerCase().startsWith(needle)) {
          event.preventDefault();
          pick(index, true);
          return;
        }
      }
    }
  };

  const summary = (() => {
    if (catalog === null) {
      return unavailable ? t("Community_TheCommunityListCouldNot") : t("Community_LoadingTheCommunityList");
    }
    const count = catalog.profiles.length;
    if (count === 0) {
      return catalog.fromCache
        ? t("Community_TheSavedCopyOfThe")
        : t("Community_TheCommunityListHasNo");
    }
    const profiles = plural("Count_Profile", count, [count]);
    const origin = catalog.fromCache
      ? t("Community_ProfilesCountFromTheCopy", [profiles])
      : t("Community_ProfilesCountDownloadedJustNow", [profiles]);
    return catalog.skippedRows === 0
      ? origin
      : `${origin} ${plural("Community_SkippedRow", catalog.skippedRows, [catalog.skippedRows])}`;
  })();

  if (load === undefined || importProfile === undefined) {
    return (
      <section className="shell-placeholder" aria-labelledby="community-title">
        <h1 id="community-title">{t("Community_CommunityProfiles")}</h1>
        <p>{t("Community_TheCommunityListCouldNot")}</p>
      </section>
    );
  }

  return (
    <section className="community-page" aria-labelledby="community-title">
      <h1 className="section" id="community-title">{t("Community_CommunityProfiles")}</h1>
      {/* `MainWindow.Explain`: the one line, the rest behind its "?". */}
      <div className="explain-row community-explain">
        <span>{t("Community_SharedGoogleSheetsForGame")}</span>
        <InfoButton
          name={t("Rewrite_HowCommunityProfilesWork")}
          title={t("Community_CommunityProfiles")}
          body={t("Community_GameProfilesOtherQuadStickPlayers")}
        />
      </div>

      <label className="community-search">
        <span className="visually-hidden">{t("Community_SearchTheCommunityProfiles")}</span>
        <input
          type="search"
          placeholder={t("Community_SearchByGameFileName")}
          aria-label={t("Community_SearchTheCommunityProfiles")}
          value={query}
          onChange={(event) => {
            typed.current = true;
            setQuery(event.currentTarget.value);
            setSelectedId("");
          }}
          onKeyDown={(event) => {
            // Down walks into the results and Enter imports the pick, so the
            // whole page works from the search box.
            if (event.key === "ArrowDown" && filtered.length > 0) {
              event.preventDefault();
              pick(Math.max(selectedIndex, 0), true);
            } else if (event.key === "Enter") {
              event.preventDefault();
              void importSelected();
            }
          }}
        />
      </label>

      <p className="community-summary" aria-live="polite">{summary}</p>
      {countText.length === 0 ? null : <p className="community-count">{countText}</p>}
      <LiveRegion>{announcedCount}</LiveRegion>

      {/* An empty panel keeps its place; an empty listbox would be announced
          as a list with nothing in it. */}
      {filtered.length === 0 ? <div className="community-list" aria-hidden="true" /> : (
        <div
          className="community-list"
          ref={listRef}
          role="listbox"
          aria-label={t("Community_CommunityProfilesUseTheArrow")}
          tabIndex={-1}
        >
          {filtered.map((profile, index) => {
            const isSelected = index === selectedIndex;
            const more = details(profile);
            return (
              <div
                key={profile.sheetId}
                className="community-row"
                role="option"
                data-index={index}
                aria-selected={isSelected}
                aria-label={more.length === 0 ? profile.name : `${profile.name}. ${more.join(", ")}`}
                tabIndex={isSelected ? 0 : -1}
                onClick={() => pick(index, true)}
                onKeyDown={onListKey}
              >
                <span className="community-row-mark">{isSelected ? <Icon name="check" size={18} /> : null}</span>
                <span className="community-row-text">
                  <span className="community-row-name">{profile.name}</span>
                  {more.length === 0 ? null : <span className="community-row-details">{more.join(" · ")}</span>}
                </span>
              </div>
            );
          })}
        </div>
      )}

      {selected === null ? null : (
        <section className="community-detail" aria-label={selected.name}>
          <h2>{selected.name}</h2>
          <p className="community-detail-meta">{details(selected).join(" · ")}</p>
          {selected.notes.trim().length === 0 ? null : <p>{selected.notes}</p>}
        </section>
      )}

      <div className="community-actions">
        <button
          className="primary-action"
          type="button"
          aria-label={t("Community_ImportTheSelectedProfileInto")}
          disabled={selected === null || importing}
          onClick={() => void importSelected()}
        >
          {t("Community_Import")}
        </button>
        {openSheet === undefined ? null : (
          <button
            type="button"
            disabled={selected === null || opening}
            aria-label={t("Community_OpenTheSelectedProfileS")}
            onClick={() => void openSelected()}
          >
            {t("Community_OpenInSheets")}
          </button>
        )}
        <button type="button" disabled={loading || importing} onClick={() => void refresh()}>
          {t("Community_Refresh")}
        </button>
      </div>

      {/* `_status`: docked under the buttons, on screen and read out. A
          failure carries an icon and "Could not" words, never colour alone. */}
      <div className={status.error ? "community-status is-error" : "community-status"}>
        <LiveRegion visuallyHidden={false}>
          {status.text.length === 0 ? null : (
            <>
              {status.error ? <Icon name="error" size={20} className="community-status-icon" /> : null}
              <span>{status.text}</span>
            </>
          )}
        </LiveRegion>
      </div>
    </section>
  );
}
