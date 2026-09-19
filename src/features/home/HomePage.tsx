import { useEffect, useState } from "react";

import { Icon } from "../../components/primitives/icons";
import { useI18n } from "../../i18n";
import type { PendingRescue, QcmClient } from "../../platform";

export interface HomePageProps {
  readonly client: QcmClient;
  readonly rescue: PendingRescue | null;
  readonly workbookBusy: boolean;
  readonly onNewProfile: () => void;
  readonly onOpenProfile: () => void;
  readonly onImportWorkbook: () => void;
  readonly onOpenCommunity: () => void;
  readonly onManageDevice: () => void;
  readonly onOpenHelp: () => void;
  readonly onOpenRescue: () => void;
  readonly onDismissRescue: () => void;
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
  onOpenCommunity,
  onManageDevice,
  onOpenHelp,
  onOpenRescue,
  onDismissRescue,
}: HomePageProps) {
  const { t } = useI18n();
  const [deviceNames, setDeviceNames] = useState<string[]>([]);
  const [sheetUrl, setSheetUrl] = useState("");

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

  return (
    <div className="home-view">
      <section className="homepanel start" aria-labelledby="home-start-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-start-title">
            {t("Shell_StartAProfile")}
          </h2>
          <p className="homepanel-lead">{t("Shell_BuildOpenOrFindA")}</p>
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
            onClick={onNewProfile}
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
            {client.chooseAndImportWorkbook === undefined ? null : (
              <button
                type="button"
                disabled={workbookBusy}
                aria-label={t("Shell_ImportTheProfileFromThe")}
                onClick={onImportWorkbook}
              >
                <Icon name="link" size={18} />
                <span>{t("Shell_ImportFromSheets")}</span>
              </button>
            )}
          </div>
        </div>
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
            disabled={sheetUrl.trim() === "" || workbookBusy}
            aria-label={t("Shell_ImportTheProfileFromThe")}
            onClick={onImportWorkbook}
          >
            <Icon name="link" size={18} />
            <span>{t("Shell_Import")}</span>
          </button>
        </div>
      </section>

      <p className="home-version">{t("Rewrite_ProductName")}</p>
    </div>
  );
}
