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

function initials(name: string): string {
  const cleaned = name.replace(/\.csv$/iu, "").trim();
  const parts = cleaned.split(/[\s_-]+/u).filter(Boolean);
  if (parts.length >= 2) {
    return `${parts[0]!.slice(0, 1)}${parts[1]!.slice(0, 1)}`.toUpperCase();
  }
  return cleaned.slice(0, 2).toUpperCase() || "?";
}

function hueFor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i += 1) hash = (hash * 31 + name.charCodeAt(i)) | 0;
  const hue = Math.abs(hash) % 360;
  return `hsl(${String(hue)} 42% 42%)`;
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
      <section className="homepanel" aria-labelledby="home-start-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-start-title">
            {t("Shell_StartAProfile")}
          </h2>
        </div>
        <div className="start-cards">
          <button className="homeaction featured" type="button" onClick={onNewProfile}>
            <Icon name="add" />
            <span>{t("Shell_NewProfile")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_OpenAProfileCSVFile")}
            onClick={onOpenProfile}
          >
            <Icon name="folder" />
            <span>{t("Shell_OpenAProfileFile")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_StartANewProfileFrom")}
            onClick={onNewProfile}
          >
            <Icon name="template" />
            <span>{t("Main_StartFromATemplate")}</span>
          </button>
          <button className="homeaction" type="button" onClick={onOpenCommunity}>
            <Icon name="community" />
            <span>{t("Shell_Community")}</span>
          </button>
          <button
            className="homeaction"
            type="button"
            aria-label={t("Shell_HowItWorksF1")}
            onClick={onOpenHelp}
          >
            <Icon name="help" />
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

      <section className="homepanel" aria-labelledby="home-device-title">
        <div className="homepanel-header">
          <h2 className="section" id="home-device-title">
            {t("Shell_OnYourQuadStick")}
          </h2>
          <button className="icon quiet" type="button" aria-label={t("Shell_HowItWorks")} onClick={onOpenHelp}>
            <Icon name="help" size={18} />
          </button>
          <button className="quiet" type="button" onClick={onManageDevice}>
            <Icon name="files" size={18} />
            <span>{t("Shell_ManageFiles")}</span>
          </button>
        </div>
        {deviceNames.length === 0 ? (
          <p className="device-empty">{t("Shell_ProfilesYouSaveWillShow")}</p>
        ) : (
          <div className="card-grid">
            {deviceNames.map((name) => (
              <button className="card" type="button" key={name} onClick={onManageDevice}>
                <span className="card-initials" style={{ background: hueFor(name) }}>
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
            <button type="button" aria-label={t("Shell_StartANewProfileFrom")} onClick={onNewProfile}>
              <Icon name="add" size={18} />
              <span>{t("Shell_NewProfile")}</span>
            </button>
            <button type="button" onClick={onOpenProfile}>
              <Icon name="folder" size={18} />
              <span>{t("Shell_OpenAProfileFile")}</span>
            </button>
            {client.chooseAndImportWorkbook === undefined ? null : (
              <button type="button" disabled={workbookBusy} onClick={onImportWorkbook}>
                <Icon name="link" size={18} />
                <span>{t("Community_Import")}</span>
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
            onClick={onImportWorkbook}
          >
            <Icon name="link" size={18} />
            <span>{t("Community_Import")}</span>
          </button>
        </div>
      </section>

      <p className="home-version">{t("Rewrite_ProductName")}</p>
    </div>
  );
}
