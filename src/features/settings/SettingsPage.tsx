import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import {
  LOCALE_NAMES,
  LOCALE_TAGS,
  useI18n,
  type LocalePreference,
  type MessageKey,
} from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  INTERFACE_SCALES,
  asQcmError,
  type AppSettings,
  type CardSentenceStyle,
  type ModelChoice,
  type PickerGrouping,
  type QcmClient,
  type SettingsPatch,
  type ThemeChoice,
  type UpdateResult,
} from "../../platform";
import {
  applyThemePreference,
  applyInterfaceScale,
  applyReduceMotion,
  type ThemePreference,
} from "../../app/theme";

const MODEL_LABELS: Record<ModelChoice, string> = {
  fps: "QuadStick FPS",
  original: "QuadStick Original",
  singleton: "QuadStick Singleton",
};

const GROUPING_LABELS: Record<PickerGrouping, string> = {
  detailed: "Detailed",
  wide: "Wide",
  flat: "Flat",
};

const THEME_OPTIONS: readonly ThemeChoice[] = ["system", "light", "dark"];
const MODEL_OPTIONS: readonly ModelChoice[] = ["fps", "original", "singleton"];
const GROUPING_OPTIONS: readonly PickerGrouping[] = ["detailed", "wide", "flat"];

type SettingsTab = "general" | "advanced" | "help" | "contact";

/** `MainWindow.HelpSections`: one ground truth for the quick guide. Two
 * titles are English literals there too ("Renaming", "Keyboard"). */
const HELP_SECTIONS: readonly (readonly [MessageKey | "Renaming" | "Keyboard", MessageKey])[] = [
  ["Main_WhatIsAProfile", "Main_OneCSVFileThatTells"],
  ["Main_TheThreeColumnsSameColors", "Main_YellowOUTPUTTheGameButton"],
  ["Main_StartFromAWorkingProfile", "Main_NewProfileGivesYouThe"],
  ["Main_CommunityProfiles", "Main_TheCommunityProfilesCardOn"],
  ["Renaming", "Main_TheNameBoxAtThe"],
  ["Main_DeviceSettings", "Main_PrefsCsvIsTheQuadStick"],
  ["Main_InstallingSafely", "Main_PlugInTheQuadStickIt"],
  ["Main_ManagingFilesOnYourQuadStick", "Main_TheManageFilesCardUnder"],
  ["Main_QuadStickNotShowingUp", "Main_IfTheDeviceIsIn"],
  ["Keyboard", "Main_TabMovesBetweenFieldsArrows"],
  ["Main_FoundAProblem", "Main_SelectAProblemInThe"],
];

interface SettingsPageProps {
  readonly client: QcmClient;
  readonly onThemeChange?: (theme: ThemePreference) => void;
  readonly extra?: ReactNode;
  /** The tab it opens on: Help for the editor's `?` and F1. */
  readonly initialTab?: SettingsTab;
  /** Help's Replay tutorial: leaves Settings and starts the tour. */
  readonly onReplayTutorial?: () => void;
}

export function SettingsPage({ client, onThemeChange, extra, initialTab = "general", onReplayTutorial }: SettingsPageProps) {
  const { t, preference, setPreference } = useI18n();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [update, setUpdate] = useState<UpdateResult | null>(null);
  // `Settings_YouAreOn`, the line the update check then replaces.
  const [version, setVersion] = useState("");
  useEffect(() => {
    let cancelled = false;
    void client.getAppSnapshot().then(
      (snapshot) => {
        if (!cancelled) setVersion(snapshot.version);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);
  const [feedback, setFeedback] = useState("");
  const [tab, setTab] = useState<SettingsTab>(initialTab);
  const [scalePreview, setScalePreview] = useState<number | null>(null);
  const [scaleCountdown, setScaleCountdown] = useState(0);
  const scaleTimer = useRef<ReturnType<typeof setInterval> | null>(null);

  const clearScalePreview = useCallback(() => {
    if (scaleTimer.current !== null) {
      clearInterval(scaleTimer.current);
      scaleTimer.current = null;
    }
    setScalePreview(null);
    setScaleCountdown(0);
  }, []);

  useEffect(() => () => clearScalePreview(), [clearScalePreview]);

  useEffect(() => {
    let disposed = false;
    void client.getSettings().then((value) => {
      if (disposed) return;
      setSettings(value);
      applyThemePreference(value.theme);
      applyInterfaceScale(value.interfaceScalePercent);
      applyReduceMotion(value.reduceMotion);
      onThemeChange?.(value.theme);
    }).catch((reason: unknown) => {
      if (!disposed) setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    });
    return () => { disposed = true; };
  }, [client, onThemeChange, t]);

  const patchSettings = useCallback(async (patch: SettingsPatch): Promise<void> => {
    if (settings === null || busy) return;
    setBusy(true);
    setMessage("");
    try {
      const next = await client.updateSettings(settings.revision, patch);
      setSettings(next);
      if (patch.theme !== undefined) {
        onThemeChange?.(patch.theme);
        applyThemePreference(patch.theme);
      }
      if (patch.language !== undefined) {
        setPreference(patch.language as LocalePreference);
      }
      if (patch.interfaceScalePercent !== undefined) {
        applyInterfaceScale(patch.interfaceScalePercent);
        clearScalePreview();
      }
      if (patch.reduceMotion !== undefined) {
        applyReduceMotion(patch.reduceMotion);
      }
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  }, [busy, clearScalePreview, client, onThemeChange, setPreference, settings, t]);

  const previewScale = (percent: number): void => {
    if (settings === null) return;
    if (percent === settings.interfaceScalePercent) {
      clearScalePreview();
      applyInterfaceScale(percent);
      return;
    }
    applyInterfaceScale(percent);
    setScalePreview(percent);
    setScaleCountdown(15);
    if (scaleTimer.current !== null) clearInterval(scaleTimer.current);
    scaleTimer.current = setInterval(() => {
      setScaleCountdown((remaining) => {
        if (remaining <= 1) {
          clearScalePreview();
          applyInterfaceScale(settings.interfaceScalePercent);
          return 0;
        }
        return remaining - 1;
      });
    }, 1000);
  };

  const sendFeedback = async (): Promise<void> => {
    const send = client.sendFeedback;
    if (send === undefined || busy || feedback.trim() === "") return;
    setBusy(true);
    setMessage("");
    try {
      await send.call(client, feedback);
      setFeedback("");
      setMessage(t("Settings_FeedbackSent"));
    } catch {
      setMessage(t("Settings_FeedbackFailed"));
    } finally {
      setBusy(false);
    }
  };

  const checkUpdates = async (): Promise<void> => {
    const check = client.checkForUpdate;
    if (check === undefined || busy) return;
    setBusy(true);
    setMessage("");
    try {
      const result = await check.call(client);
      setUpdate(result);
      setMessage(result.message);
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  const openDownload = async (): Promise<void> => {
    const open = client.openExternalUrl;
    if (open === undefined || update?.downloadUrl == null || busy) return;
    setBusy(true);
    try {
      await open.call(client, update.downloadUrl);
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  if (settings === null) {
    return <p className="settings-page">{t("Settings_Checking")}</p>;
  }

  const savedScale = INTERFACE_SCALES.includes(
    settings.interfaceScalePercent as (typeof INTERFACE_SCALES)[number],
  )
    ? settings.interfaceScalePercent
    : 100;
  const scaleValue = scalePreview ?? savedScale;

  const tabs: readonly { id: SettingsTab; label: string }[] = [
    { id: "general", label: t("Settings_TabGeneral") },
    { id: "advanced", label: t("Settings_TabAdvanced") },
    { id: "help", label: t("Settings_TabHelp") },
    { id: "contact", label: t("Settings_TabContact") },
  ];

  return (
    <div className="settings-page">
      <LiveRegion>{message}</LiveRegion>
      <div className="settings-page-header">
        <h1 className="settings-title">{t("Shell_Settings")}</h1>
      </div>

      <div className="settings-tabs" role="tablist" aria-label={t("Shell_Settings")}>
        {tabs.map((entry) => (
          <button
            key={entry.id}
            className="settings-tab"
            type="button"
            role="tab"
            aria-selected={tab === entry.id}
            onClick={() => setTab(entry.id)}
          >
            {entry.label}
          </button>
        ))}
      </div>

      <div className="settings-tab-card" role="tabpanel">
        <div className="settings-fields">
          {tab === "general" ? (
            <>
              <h2 className="settings-heading">{t("Settings_TabGeneral")}</h2>
              <div className="settings-field">
                <label>
                  <span>{t("Settings_Language")}</span>
                  <select
                    aria-label={t("Settings_Language")}
                    value={preference}
                    disabled={busy}
                    onChange={(event) => {
                      const next = event.currentTarget.value as LocalePreference;
                      setPreference(next);
                      void patchSettings({ language: next });
                    }}
                  >
                    <option value="system">{t("Settings_LanguageSystem")}</option>
                    {LOCALE_TAGS.map((tag) => (
                      <option key={tag} value={tag}>{LOCALE_NAMES[tag]}</option>
                    ))}
                    {import.meta.env.DEV ? (
                      <option value="qps-ploc">{t("Rewrite_PseudoLocaleName")}</option>
                    ) : null}
                  </select>
                </label>
              </div>

              <div className="settings-field">
                <label>
                  <span>{t("Settings_Appearance")}</span>
                  <select
                    aria-label={t("Settings_AppearanceHelp")}
                    value={settings.theme}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ theme: event.currentTarget.value as ThemeChoice });
                    }}
                  >
                    {THEME_OPTIONS.map((theme) => (
                      <option key={theme} value={theme}>
                        {theme === "system"
                          ? t("Theme_System")
                          : theme === "light"
                            ? t("Theme_Light")
                            : t("Theme_Dark")}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              <div className="settings-field">
                <label>
                  <span>{t("Settings_Scale")}</span>
                  <select
                    aria-label={t("Settings_ScaleHelp")}
                    value={scaleValue}
                    disabled={busy}
                    onChange={(event) => {
                      const percent = Number(event.currentTarget.value);
                      if (!INTERFACE_SCALES.includes(percent as (typeof INTERFACE_SCALES)[number])) {
                        return;
                      }
                      previewScale(percent);
                    }}
                  >
                    {INTERFACE_SCALES.map((percent) => (
                      <option key={percent} value={percent}>{`${percent}%`}</option>
                    ))}
                  </select>
                </label>
                {scalePreview === null ? null : (
                  <div className="scale-preview-actions">
                    <button
                      type="button"
                      className="primary"
                      aria-label={t("Settings_SaveSizeHelp")}
                      disabled={busy}
                      onClick={() => void patchSettings({ interfaceScalePercent: scalePreview })}
                    >
                      {t("Settings_SaveSize")}
                    </button>
                    <p aria-live="assertive">{t("Settings_Reverting", [scaleCountdown])}</p>
                  </div>
                )}
                <p className="settings-caption">{t("Settings_ScaleCaption")}</p>
              </div>

              <div className="settings-field">
                <label>
                  <span>{t("Settings_Model")}</span>
                  <select
                    aria-label={t("Settings_Model")}
                    value={settings.model}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ model: event.currentTarget.value as ModelChoice });
                    }}
                  >
                    {MODEL_OPTIONS.map((model) => (
                      <option key={model} value={model}>{MODEL_LABELS[model]}</option>
                    ))}
                  </select>
                </label>
              </div>

              <div className="settings-field">
                <label>
                  <span>{t("Settings_CardSentenceStyle")}</span>
                  <select
                    aria-label={t("Settings_CardSentenceStyleHelp")}
                    value={settings.cardSentenceStyle}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ cardSentenceStyle: event.currentTarget.value as CardSentenceStyle });
                    }}
                  >
                    <option value="press_when">{t("Settings_CardSentenceStyleCurrent")}</option>
                    <option value="input_to_output">{t("Settings_CardSentenceStyleInputFirst")}</option>
                  </select>
                </label>
                <p className="settings-caption">{t("Settings_CardSentenceStyleCaption")}</p>
              </div>

              {extra}

              <div className="settings-field">
                <span className="settings-label">{t("Settings_Updates")}</span>
                <p className="settings-caption">{t("Settings_UpdatesCaption")}</p>
                <div className="settings-buttons">
                  <button
                    type="button"
                    disabled={busy || client.checkForUpdate === undefined}
                    aria-label={t("Settings_CheckUpdates")}
                    onClick={() => void checkUpdates()}
                  >
                    {busy ? t("Settings_Checking") : t("Settings_CheckUpdates")}
                  </button>
                  {update?.isNewer === true && update.downloadUrl !== null ? (
                    <button type="button" disabled={busy} aria-label={t("Settings_OpenDownloadHelp")} onClick={() => void openDownload()}>
                      {t("Settings_OpenDownload")}
                    </button>
                  ) : null}
                </div>
                <p className="muted settings-version" aria-live="polite">
                  {update?.message ?? (version === "" ? "" : t("Settings_YouAreOn", [version]))}
                </p>
              </div>
            </>
          ) : null}

          {tab === "advanced" ? (
            <>
              <h2 className="settings-heading">{t("Settings_TabAdvanced")}</h2>
              <div className="settings-field">
                <label className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={settings.reduceMotion}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ reduceMotion: event.currentTarget.checked });
                    }}
                  />
                  <span>{t("Settings_ReduceMotion")}</span>
                </label>
                <p className="settings-caption">{t("Settings_ReduceMotionCaption")}</p>
              </div>

              <div className="settings-field">
                <label>
                  <span>{t("Settings_Grouping")}</span>
                  <select
                    aria-label={t("Settings_GroupingHelp")}
                    value={settings.pickerGrouping}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({
                        pickerGrouping: event.currentTarget.value as PickerGrouping,
                      });
                    }}
                  >
                    {GROUPING_OPTIONS.map((grouping) => (
                      <option key={grouping} value={grouping}>{GROUPING_LABELS[grouping]}</option>
                    ))}
                  </select>
                </label>
                <p className="settings-caption">{t("Settings_GroupingCaption")}</p>
              </div>
              <h3 className="settings-heading">{t("Settings_Privacy_Heading")}</h3>
              <p className="settings-caption">{t("Settings_UsageCaption")}</p>
              <div className="settings-field">
                <label className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={settings.usageAnalytics}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ usageAnalytics: event.currentTarget.checked });
                    }}
                  />
                  <span>{t("Settings_UsageData")}</span>
                </label>
              </div>
              <p className="settings-caption">{t("Settings_CrashCaption")}</p>
              <div className="settings-field">
                <label className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={settings.askAboutCrashes}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ askAboutCrashes: event.currentTarget.checked });
                    }}
                  />
                  <span>{t("Settings_AskCrashes")}</span>
                </label>
              </div>
              <p className="settings-caption">
                {settings.installId === ""
                  ? t("Settings_NoInstallId")
                  : t("Settings_InstallIdIs", [settings.installId])}
              </p>
              <div className="settings-field">
                <button
                  type="button"
                  disabled={settings.installId === ""}
                  aria-label={t("Settings_CopyInstallIdHelp")}
                  onClick={() => {
                    // Best effort, like the Avalonia clipboard write.
                    void navigator.clipboard?.writeText(settings.installId).catch(() => undefined);
                  }}
                >
                  {t("Settings_CopyInstallId")}
                </button>
                {client.openPrivacyPolicy === undefined ? null : (
                  <button
                    type="button"
                    className="link-button"
                    aria-label={t("Settings_PrivacyHelp")}
                    onClick={() => {
                      void client.openPrivacyPolicy?.().catch(() => undefined);
                    }}
                  >
                    {t("Settings_Privacy")}
                  </button>
                )}
              </div>
              <div className="settings-field">
                <label className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={settings.rememberWindow}
                    disabled={busy}
                    onChange={(event) => {
                      void patchSettings({ rememberWindow: event.currentTarget.checked });
                    }}
                  />
                  <span>{t("Settings_RememberWindow")}</span>
                </label>
              </div>
              <div className="settings-field">
                <label className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={!settings.tutorialSeen}
                    disabled={busy}
                    aria-label={t("Settings_ShowTutorial")}
                    onChange={(event) => {
                      void patchSettings({ tutorialSeen: !event.currentTarget.checked });
                    }}
                  />
                  <span>{t("Settings_ShowTutorial")}</span>
                </label>
              </div>
            </>
          ) : null}

          {tab === "help" ? (
            <>
              <h2 className="settings-heading">{t("Settings_QuickGuide")}</h2>
              {onReplayTutorial === undefined ? null : (
                <div className="settings-buttons">
                  <button className="primary" type="button" aria-label={t("Settings_ReplayTutorialHelp")} onClick={onReplayTutorial}>
                    {t("Settings_ReplayTutorial")}
                  </button>
                </div>
              )}
              {HELP_SECTIONS.map(([title, body]) => (
                <div className="help-section" key={title}>
                  <h3 className="settings-heading">{typeof title === "string" && !title.includes("_") ? title : t(title as MessageKey)}</h3>
                  <p className="help-body">{t(body)}</p>
                </div>
              ))}
            </>
          ) : null}

          {tab === "contact" ? (
            <>
              <h2 className="settings-heading">{t("Settings_TabContact")}</h2>
              <p className="settings-caption">{t("Settings_ContactIntro")}</p>
              <h3 className="settings-heading">{t("Settings_SendFeedback")}</h3>
              <p className="settings-caption">{t("Settings_FeedbackCaption")}</p>
              <div className="settings-field">
                <label>
                  <span>{t("Settings_FeedbackLabel")}</span>
                  <textarea
                    aria-label={t("Settings_FeedbackLabel")}
                    placeholder={t("Settings_FeedbackWatermark")}
                    value={feedback}
                    disabled={busy || !settings.usageAnalytics || client.sendFeedback === undefined}
                    rows={4}
                    onChange={(event) => setFeedback(event.currentTarget.value)}
                  />
                </label>
                <button
                  className="primary"
                  type="button"
                  disabled={
                    busy
                    || !settings.usageAnalytics
                    || client.sendFeedback === undefined
                    || feedback.trim() === ""
                  }
                  onClick={() => void sendFeedback()}
                >
                  {t("Settings_SendFeedback")}
                </button>
              </div>
            </>
          ) : null}
        </div>
      </div>
    </div>
  );
}
