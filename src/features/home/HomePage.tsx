import { useCallback, useEffect, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { Icon } from "../../components/primitives/icons";
import { HomeDriveButton } from "../cloud/HomeDriveButton";
import { Popover } from "../editor/Popover";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type EditorSnapshot,
  type LibraryCard,
  type PendingRescue,
  type QcmClient,
  type TemplateEntry,
  type WorkbookImportReview,
} from "../../platform";

/** What a card's menu asks the editor to do once the profile is open. */
export type CardFollowUp = "copy_link" | "open_sheet" | "latest" | "forget";

export interface HomePageProps {
  readonly client: QcmClient;
  readonly rescue: PendingRescue | null;
  readonly workbookBusy: boolean;
  readonly onNewProfile: () => void;
  readonly onOpenProfile: () => void;
  readonly onImportWorkbook: () => void;
  /** Download a pasted Sheets link. Absent where the native side has none. */
  readonly onImportSheetsLink?: (url: string) => void;
  /** What the last link import said, shown under the link box. */
  readonly sheetsMessage?: string;
  /** A single Drive backup restored into the review (no bulk restore). */
  readonly onDriveReview?: (review: WorkbookImportReview) => void;
  readonly onOpenCommunity: () => void;
  readonly onManageDevice: () => void;
  readonly onOpenHelp: () => void;
  readonly onOpenRescue: () => void;
  readonly onDismissRescue: () => void;
  /** A library card or a template opened; `then` is the menu action to run on it. */
  readonly onOpened?: (snapshot: EditorSnapshot, then?: CardFollowUp) => void;
}

/** `EditedNote`: rough on purpose; "yesterday" is how people sort in their heads. */
function editedNote(editedMs: number, now: number, t: (key: "Main_JustNow" | "Main_IntAgeTotalMinutesMinAgo" | "Main_IntAgeTotalHoursHAgo" | "Main_IntAgeTotalDaysDaysAgo", values?: readonly unknown[]) => string, locale: string): string {
  const age = now - editedMs;
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;
  if (age < 2 * minute) return t("Main_JustNow");
  if (age < hour) return t("Main_IntAgeTotalMinutesMinAgo", [Math.floor(age / minute)]);
  if (age < day) return t("Main_IntAgeTotalHoursHAgo", [Math.floor(age / hour)]);
  // English in Avalonia too (`EditedNote` has no resource for it).
  if (age < 2 * day) return "yesterday";
  if (age < 30 * day) return t("Main_IntAgeTotalDaysDaysAgo", [Math.floor(age / day)]);
  // "d MMM yyyy", in the reader's own month names.
  const date = new Date(editedMs);
  const month = date.toLocaleString(locale, { month: "short" });
  return `${String(date.getDate())} ${month} ${String(date.getFullYear())}`;
}

/** Avalonia MainWindow.TileColors — fixed swatches, FNV name hash. */
const TILE_COLORS = [
  "#1F4E79", "#6B2D5C", "#1B5E4A", "#8A3A1E",
  "#3B3577", "#7A2E2E", "#245563", "#5A4414",
  "#4A2E6B", "#0F5132", "#8A2B4A", "#34495E",
] as const;

function tileColorFor(name: string): string {
  let h = 2166136261;
  for (const ch of name.toLowerCase()) {
    h ^= ch.charCodeAt(0);
    h = Math.imul(h, 16777619);
  }
  return TILE_COLORS[(h >>> 0) % TILE_COLORS.length]!;
}

function initials(name: string): string {
  const cleaned = name.replace(/\.csv$/iu, "").trim();
  const words = cleaned
    .split(/[\s_\-.+]+/u)
    .filter((word) => word.length > 0 && /[0-9A-Za-z]/u.test(word[0]!));
  if (words.length === 0) return "?";
  if (words.length === 1) return words[0]!.slice(0, 2).toUpperCase();
  return `${words[0]!.slice(0, 1)}${words[1]!.slice(0, 1)}`.toUpperCase();
}

export function HomePage({
  client,
  rescue,
  workbookBusy,
  onNewProfile,
  onOpenProfile,
  onImportWorkbook,
  onImportSheetsLink,
  sheetsMessage = "",
  onDriveReview,
  onOpenCommunity,
  onManageDevice,
  onOpenHelp,
  onOpenRescue,
  onDismissRescue,
  onOpened,
}: HomePageProps) {
  const { t, plural, locale } = useI18n();
  const [library, setLibrary] = useState<readonly LibraryCard[]>([]);
  const [homeStatus, setHomeStatus] = useState("");
  const [confirmDelete, setConfirmDelete] = useState<LibraryCard | null>(null);
  const [templates, setTemplates] = useState<readonly TemplateEntry[] | null>(null);
  const [pickedTemplate, setPickedTemplate] = useState<string | null>(null);
  // Read once per visit to Home, as `RefreshHomeCards` does: no timer ticks
  // the labels on while the page sits idle.
  const [now] = useState(() => Date.now());

  const refreshLibrary = useCallback((): void => {
    const list = client.listLibraryProfiles;
    if (list === undefined) return;
    void list.call(client).then(setLibrary, () => setLibrary([]));
  }, [client]);

  useEffect(() => {
    refreshLibrary();
  }, [refreshLibrary]);

  const openCard = async (card: LibraryCard, then?: CardFollowUp): Promise<void> => {
    const open = client.openLibraryProfile;
    if (open === undefined) return;
    setHomeStatus("");
    try {
      onOpened?.(await open.call(client, card.id), then);
    } catch (reason) {
      // Stay on Home: an empty editor showing an error strands the user.
      setHomeStatus(t("Main_CouldNotOpenBareEx", [card.name, localizedErrorMessage(asQcmError(reason).payload, t)]));
    }
  };

  const deleteCard = async (card: LibraryCard): Promise<void> => {
    const remove = client.deleteLibraryProfile;
    setConfirmDelete(null);
    if (remove === undefined) return;
    try {
      await remove.call(client, card.id);
      setHomeStatus(t("Main_DeletedBare", [card.name]));
    } catch (reason) {
      setHomeStatus(t("Main_CouldNotDeleteBareEx", [card.name, localizedErrorMessage(asQcmError(reason).payload, t)]));
    }
    refreshLibrary();
  };

  // `UseTemplateAsync`: the saved templates, or a line saying there are none.
  const useTemplate = async (): Promise<void> => {
    const list = client.listTemplates;
    if (list === undefined) {
      onNewProfile();
      return;
    }
    setHomeStatus("");
    const found = await list.call(client).catch(() => [] as readonly TemplateEntry[]);
    if (found.length === 0) {
      setHomeStatus(t("Main_YouHaveNotSavedAny"));
      return;
    }
    setPickedTemplate(found[0]?.id ?? null);
    setTemplates(found);
  };

  const openTemplate = async (): Promise<void> => {
    const open = client.openTemplate;
    const id = pickedTemplate;
    setTemplates(null);
    if (open === undefined || id === null) return;
    try {
      onOpened?.(await open.call(client, id));
    } catch (reason) {
      setHomeStatus(localizedErrorMessage(asQcmError(reason).payload, t));
    }
  };
  const [deviceNames, setDeviceNames] = useState<string[]>([]);
  const [sheetUrl, setSheetUrl] = useState("");
  const [version, setVersion] = useState<readonly [string, string, string] | null>(null);

  // Avalonia HomeVersionText: "v1.8.0 · MIT · not affiliated with QuadStick".
  useEffect(() => {
    let cancelled = false;
    void client.getAppSnapshot().then(
      (snapshot) => {
        const [major = "0", minor = "0", patch = "0"] = snapshot.version.split(/[.+-]/u);
        if (!cancelled) setVersion([major, minor, patch]);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);

  useEffect(() => {
    let cancelled = false;
    void client.listDevices().then(
      (presence) => {
        if (cancelled) return;
        setDeviceNames(presence.devices.map((device) => device.displayName));
      },
      () => {
        if (!cancelled) setDeviceNames([]);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [client]);

  // Import and the library's Import from Sheets: whatever is in the box.
  const importPasted = (): void => {
    if (onImportSheetsLink !== undefined) onImportSheetsLink(sheetUrl);
    else if (sheetUrl.trim() === "") setHomeStatus(t("Main_ThatDoesNotLookLike"));
    else onImportWorkbook();
  };

  return (
    <div className="home-view">
      <section className="homepanel start" aria-labelledby="home-start-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-start-title">
            {t("Shell_StartAProfile")}
          </h2>
          <p className="homepanel-lead">{t("Shell_BuildOpenOrFindA")}</p>
          {onDriveReview === undefined ? null : (
            <HomeDriveButton client={client} onReview={onDriveReview} />
          )}
        </div>
        <div className="start-cards">
          <button
            className="homeaction featured"
            type="button"
            aria-label={t("Shell_CreateANewProfileFrom")}
            onClick={onNewProfile}
          >
            <Icon name="add" size={32} />
            <span>{t("Shell_NewProfile")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_OpenAProfileCSVFile")}
            onClick={onOpenProfile}
          >
            <Icon name="folder" size={28} />
            <span>{t("Shell_OpenAFile")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_StartANewProfileFrom")}
            onClick={() => void useTemplate()}
          >
            <Icon name="template" size={28} />
            <span>{t("Shell_UseTemplate")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_BrowseTheCommunityListOf")}
            onClick={onOpenCommunity}
          >
            <Icon name="community" size={28} />
            <span>{t("Shell_CommunityProfiles")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_OpenTheQuickGuideThat")}
            onClick={onOpenHelp}
          >
            <Icon name="help" size={28} />
            <span>{t("Shell_HowItWorks")}</span>
          </button>
        </div>
        {homeStatus === "" ? null : (
          <p className="home-status" role="status">
            {homeStatus}
          </p>
        )}
        {rescue === null ? null : (
          <section className="rescue-offer" aria-label={t("Shell_OpenRecoveredWork")}>
            <p>{t("Main_UnsavedWorkFromLastTime", [rescue.displayName])}</p>
            <div className="rescue-actions">
              <button className="primary" type="button" onClick={onOpenRescue}>
                {t("Shell_OpenRecoveredWork")}
              </button>
              <button
                type="button"
                aria-label={t("Shell_DiscardTheRecoveredWorkPermanently")}
                onClick={onDismissRescue}
              >
                {t("Shell_Dismiss")}
              </button>
            </div>
          </section>
        )}
        <p className="visually-hidden" data-testid="boot-state">
          {t("Shell_ProfilesYouSaveWillShow")}
        </p>
      </section>

      <section className="homepanel device" aria-labelledby="home-device-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-device-title">
            {t("Shell_OnYourQuadStick")}
          </h2>
          <button
            className="icon quiet"
            type="button"
            title={t("Shell_OnYourQuadStick")}
            aria-label={t("Shell_OnYourQuadStick")}
            onClick={onOpenHelp}
          >
            ?
          </button>
          <button
            className="quiet manage-files"
            type="button"
            aria-label={t("Shell_ManageTheProfileFilesOn")}
            onClick={onManageDevice}
          >
            <Icon name="files" size={18} />
            <span>{t("Shell_ManageFiles")}</span>
          </button>
        </div>
        <p className="homepanel-caption">{t("Shell_EachOfTheseIsAWhole")}</p>
        {deviceNames.length === 0 ? (
          <p className="device-empty">{t("Shell_PlugInYourQuadStickTo")}</p>
        ) : (
          <div className="card-grid">
            {deviceNames.map((name) => (
              <button className="card" type="button" key={name} onClick={onManageDevice}>
                <span className="card-initials" style={{ background: tileColorFor(name) }}>
                  {initials(name)}
                </span>
                <span className="card-body">
                  <span className="card-heading">{name}</span>
                  <span className="cardsub">{t("Shell_OnYourQuadStick")}</span>
                </span>
              </button>
            ))}
          </div>
        )}
      </section>

      <section className="homepanel" aria-labelledby="home-library-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-library-title">
            {t("Shell_YourProfiles")}
          </h2>
        </div>
        {library.length === 0 ? (
          <div className="library-empty">
            <p className="homepanel-caption">{t("Shell_ProfilesYouSaveWillShow")}</p>
            <div className="library-empty-actions">
              <button type="button" aria-label={t("Shell_CreateANewProfileFrom")} onClick={onNewProfile}>
                <Icon name="add" size={18} />
                <span>{t("Shell_NewProfile")}</span>
              </button>
              <button type="button" aria-label={t("Shell_OpenAProfileCSVFile")} onClick={onOpenProfile}>
                <Icon name="folder" size={18} />
                <span>{t("Shell_OpenAFile")}</span>
              </button>
              <button
                type="button"
                disabled={workbookBusy}
                aria-label={t("Shell_ImportTheProfileFromThe")}
                onClick={importPasted}
              >
                <Icon name="link" size={18} />
                <span>{t("Shell_ImportFromSheets")}</span>
              </button>
            </div>
          </div>
        ) : (
          <div className="card-grid library-cards">
            {library.map((card) => {
              const modes = card.modeNames.length > 1
                ? card.modeNames.slice(0, 3).join(", ") +
                  (card.modeNames.length > 3 ? t("Main_PlusMore", [card.modeNames.length - 3]) : "")
                : "";
              const meta = card.readable
                ? `${card.title === "" ? "" : `${card.title} · `}${plural("Count_Mode", card.modes, [card.modes])} · ${plural("Count_Binding", card.bindings, [card.bindings])}`
                : t("Main_CouldNotReadThisFile");
              const edited = card.editedMs === null ? "" : editedNote(card.editedMs, now, t, locale);
              return (
                <div className="library-card" key={card.id}>
                  <button
                    className="card"
                    type="button"
                    aria-label={t("Main_OpenInLibrary", [card.name, meta + (card.follows ? ` · ${t("Follow_CardNote")}` : "")])}
                    onClick={() => void openCard(card)}
                    onContextMenu={(event) => {
                      event.preventDefault();
                      event.currentTarget.parentElement?.querySelector<HTMLButtonElement>(".card-menu > button")?.click();
                    }}
                  >
                    <span className="card-initials" style={{ background: tileColorFor(card.name) }} aria-hidden="true">
                      {initials(card.name)}
                    </span>
                    <span className="card-body">
                      <span className="card-heading">{card.name}</span>
                      <span className="cardsub">{meta}</span>
                      {modes === "" ? null : <span className="cardsub">{modes}</span>}
                      {edited === "" ? null : <span className="cardsub edited">{t("Main_Edited") + edited}</span>}
                      {card.follows ? <span className="cardsub follows">{t("Follow_CardNote")}</span> : null}
                    </span>
                  </button>
                  <span className="card-menu">
                    <Popover
                      label="⋯"
                      name={t("Rewrite_MoreActionsFor", [card.name])}
                      className="icon quiet"
                      alignEnd
                    >
                      {(close) => (
                        <div className="menu-list">
                          <button type="button" className="menu-item" aria-label={t("Main_CopyALinkToThis")} onClick={() => { close(); void openCard(card, "copy_link"); }}>
                            {t("Main_CopyShareLink")}
                          </button>
                          <button type="button" className="menu-item" aria-label={t("Main_OpenThisProfileSGoogle")} onClick={() => { close(); void openCard(card, "open_sheet"); }}>
                            {t("Main_OpenInGoogleSheets")}
                          </button>
                          {card.follows ? (
                            <>
                              <button type="button" className="menu-item" aria-label={t("Follow_GetLatestHelp")} onClick={() => { close(); void openCard(card, "latest"); }}>
                                {t("Follow_GetLatest")}
                              </button>
                              <button type="button" className="menu-item" aria-label={t("Follow_StopFollowingHelp")} onClick={() => { close(); void openCard(card, "forget"); }}>
                                {t("Follow_StopFollowing")}
                              </button>
                            </>
                          ) : null}
                          <hr className="menu-separator" />
                          <button
                            type="button"
                            className="menu-item danger"
                            aria-label={t("Main_DeleteBareFromYourProfile", [card.name])}
                            onClick={() => {
                              close();
                              setConfirmDelete(card);
                            }}
                          >
                            {t("Main_DeleteProfile")}
                          </button>
                        </div>
                      )}
                    </Popover>
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </section>

      <section className="homepanel" aria-labelledby="home-sheets-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-sheets-title">
            {t("Shell_ImportFromSheets")}
          </h2>
        </div>
        <div className="sheets-row">
          <input
            type="text"
            aria-label={t("Shell_PasteAGoogleSheetsProfile")}
            placeholder={t("Shell_PasteAGoogleSheetsProfile")}
            value={sheetUrl}
            onChange={(event) => setSheetUrl(event.currentTarget.value)}
          />
          <button
            className="primary sheets-import"
            type="button"
            disabled={workbookBusy}
            aria-label={t("Shell_ImportTheProfileFromThe")}
            onClick={importPasted}
          >
            <Icon name="link" size={18} />
            <span>{t("Shell_Import")}</span>
          </button>
        </div>
        {sheetsMessage.length === 0 ? null : (
          <p className="home-sheets-status" role="status">
            {sheetsMessage}
          </p>
        )}
      </section>

      <Dialog
        open={confirmDelete !== null}
        title={confirmDelete === null ? "" : t("Main_DeleteBare", [confirmDelete.name])}
        onClose={() => setConfirmDelete(null)}
        actions={
          <>
            <button type="button" data-autofocus onClick={() => setConfirmDelete(null)}>
              {t("Main_Cancel")}
            </button>
            <button className="danger" type="button" onClick={() => confirmDelete !== null && void deleteCard(confirmDelete)}>
              {t("Main_Delete")}
            </button>
          </>
        }
      >
        <p>{confirmDelete === null ? "" : t("Main_PathGetFileNamePathIsRemoved", [`${confirmDelete.name}.csv`])}</p>
      </Dialog>
      <Dialog
        open={templates !== null}
        title={t("Main_StartFromATemplate")}
        onClose={() => setTemplates(null)}
        actions={
          <>
            <button className="primary" type="button" disabled={pickedTemplate === null} onClick={() => void openTemplate()}>
              {t("Main_UseTemplate")}
            </button>
            <button type="button" onClick={() => setTemplates(null)}>
              {t("Main_Cancel")}
            </button>
          </>
        }
      >
        <p>{t("Main_OpensAFreshCopyYou")}</p>
        <div className="template-list" role="radiogroup" aria-label={t("Main_YourSavedTemplates")}>
          {(templates ?? []).map((entry) => (
            <label className="template-option" key={entry.id}>
              <input
                type="radio"
                name="template"
                checked={pickedTemplate === entry.id}
                onChange={() => setPickedTemplate(entry.id)}
              />
              <span>{entry.name}</span>
            </label>
          ))}
        </div>
      </Dialog>

      <p className="home-version">
        {version === null ? t("Rewrite_ProductName") : t("Main_VVMajorVMinor", [...version])}
      </p>
    </div>
  );
}
