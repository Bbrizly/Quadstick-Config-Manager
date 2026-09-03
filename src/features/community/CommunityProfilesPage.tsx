import { useEffect, useMemo, useRef, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type CommunityCatalog,
  type CommunityProfile,
  type QcmClient,
  type WorkbookImportReview,
} from "../../platform";

interface CommunityProfilesPageProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
}

function matches(profile: CommunityProfile, query: string): boolean {
  if (query.length === 0) return true;
  const needle = query.toLocaleLowerCase();
  return [profile.name, profile.csvName, profile.connection, profile.pointer].some((field) =>
    field.toLocaleLowerCase().includes(needle),
  );
}

export function CommunityProfilesPage({ client, onReview }: CommunityProfilesPageProps) {
  const { t, plural } = useI18n();
  const listRef = useRef<HTMLSelectElement | null>(null);
  const [catalog, setCatalog] = useState<CommunityCatalog | null>(null);
  const [query, setQuery] = useState("");
  const [selectedId, setSelectedId] = useState("");
  const [loading, setLoading] = useState(false);
  const [importing, setImporting] = useState(false);
  const [status, setStatus] = useState("");

  const load = client.loadCommunityCatalog;
  const importProfile = client.importCommunityProfile;

  useEffect(() => {
    if (load === undefined) return;
    let disposed = false;
    setLoading(true);
    void load
      .call(client, false)
      .then((next) => {
        if (disposed) return;
        setCatalog(next);
        setSelectedId(next.profiles[0]?.sheetId ?? "");
        setStatus("");
      })
      .catch((reason: unknown) => {
        if (!disposed) setStatus(localizedErrorMessage(asQcmError(reason).payload, t));
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

  const refresh = async (): Promise<void> => {
    if (load === undefined || loading) return;
    setLoading(true);
    setStatus(t("Community_CheckingQuadstickComForNew"));
    try {
      const next = await load.call(client, true);
      setCatalog(next);
      setSelectedId(next.profiles[0]?.sheetId ?? "");
      setStatus(next.fromCache ? t("Community_CouldNotReachQuadstickCom") : "");
    } catch (reason) {
      setStatus(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setLoading(false);
    }
  };

  const importSelected = async (): Promise<void> => {
    if (selected === null || importProfile === undefined || importing) {
      if (selected === null) setStatus(t("Community_PickAProfileFromThe"));
      return;
    }
    setImporting(true);
    setStatus(t("Community_ImportingPickedName", [selected.name]));
    try {
      const review = await importProfile.call(client, selected.sheetId, selected.csvName);
      setStatus("");
      onReview(review);
    } catch (reason) {
      setStatus(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setImporting(false);
    }
  };

  const summary = (() => {
    if (catalog === null) return t("Community_LoadingTheCommunityList");
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
      <header>
        <h1 id="community-title">{t("Community_CommunityProfiles")}</h1>
        <p>{t("Community_GameProfilesOtherQuadStickPlayers")}</p>
      </header>

      <label>
        <span className="sr-only">{t("Community_SearchTheCommunityProfiles")}</span>
        <input
          type="search"
          placeholder={t("Community_SearchByGameFileName")}
          aria-label={t("Community_SearchTheCommunityProfiles")}
          value={query}
          onChange={(event) => {
            setQuery(event.currentTarget.value);
            setSelectedId("");
          }}
          onKeyDown={(event) => {
            if (event.key === "ArrowDown" && filtered.length > 0) {
              event.preventDefault();
              listRef.current?.focus();
            }
          }}
        />
      </label>

      <p aria-live="polite">{summary}</p>
      {countText.length === 0 ? null : <p>{countText}</p>}

      <select
        ref={listRef}
        size={Math.min(Math.max(filtered.length, 4), 14)}
        aria-label={t("Community_CommunityProfilesUseTheArrow")}
        value={selected?.sheetId ?? ""}
        onChange={(event) => setSelectedId(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            void importSelected();
          }
        }}
      >
        {filtered.map((profile) => (
          <option key={profile.sheetId} value={profile.sheetId}>
            {[profile.name, profile.csvName, profile.connection, profile.pointer]
              .filter((part) => part.trim().length > 0)
              .join(" · ")}
          </option>
        ))}
      </select>

      {selected === null ? null : (
        <section aria-label={selected.name}>
          <h2>{selected.name}</h2>
          <p>{[selected.csvName, selected.connection, selected.pointer].filter(Boolean).join(" · ")}</p>
          {selected.notes.trim().length === 0 ? null : <p>{selected.notes}</p>}
        </section>
      )}

      <div className="community-actions">
        <button
          className="primary-action"
          type="button"
          disabled={selected === null || importing}
          onClick={() => void importSelected()}
        >
          {t("Community_Import")}
        </button>
        <button type="button" disabled={loading || importing} onClick={() => void refresh()}>
          {t("Community_Refresh")}
        </button>
      </div>

      {filtered.length === 0 && catalog !== null && catalog.profiles.length > 0 ? (
        <p>{t("Community_NothingMatchesThatSearchClear")}</p>
      ) : null}
      <LiveRegion>{status}</LiveRegion>
    </section>
  );
}
