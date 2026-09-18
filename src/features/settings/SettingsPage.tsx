import { useCallback, useEffect, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import {
  LOCALE_NAMES,
  LOCALE_TAGS,
  useI18n,
  type LocalePreference,
} from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  INTERFACE_SCALES,
  asQcmError,
  type AppSettings,
  type ModelChoice,
  type PickerGrouping,
  type QcmClient,
  type SettingsPatch,
  type ThemeChoice,
  type UpdateResult,
} from "../../platform";
import { applyThemePreference, applyInterfaceScale, applyReduceMotion, type ThemePreference } from "../../app/theme";

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

interface SettingsPageProps {
  readonly client: QcmClient;
  readonly onThemeChange?: (theme: ThemePreference) => void;
}

export function SettingsPage({ client, onThemeChange }: SettingsPageProps) {
  const { t, preference, setPreference } = useI18n();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [update, setUpdate] = useState<UpdateResult | null>(null);
  const [feedback, setFeedback] = useState("");

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
      }
      if (patch.reduceMotion !== undefined) {
        applyReduceMotion(patch.reduceMotion);
      }
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  }, [busy, client, onThemeChange, setPreference, settings, t]);

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
    return <p>{t("Settings_Checking")}</p>;
  }

  const scaleValue = INTERFACE_SCALES.includes(
    settings.interfaceScalePercent as (typeof INTERFACE_SCALES)[number],
  )
    ? settings.interfaceScalePercent
    : 100;

  return (
    <div className="settings-page">
      <LiveRegion>{message}</LiveRegion>

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
          {import.meta.env.DEV ? <option value="qps-ploc">{t("Rewrite_PseudoLocaleName")}</option> : null}
        </select>
      </label>

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
              {theme === "system" ? t("Theme_System") : theme === "light" ? t("Theme_Light") : t("Theme_Dark")}
            </option>
          ))}
        </select>
      </label>

      <label>
        <span>{t("Settings_Scale")}</span>
        <select
          aria-label={t("Settings_ScaleHelp")}
          value={scaleValue}
          disabled={busy}
          onChange={(event) => {
            const percent = Number(event.currentTarget.value);
            if (!INTERFACE_SCALES.includes(percent as (typeof INTERFACE_SCALES)[number])) return;
            void patchSettings({ interfaceScalePercent: percent });
          }}
        >
          {INTERFACE_SCALES.map((percent) => (
            <option key={percent} value={percent}>{`${percent}%`}</option>
          ))}
        </select>
      </label>
      <p>{t("Settings_ScaleCaption")}</p>

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

      <label>
        <span>{t("Settings_Grouping")}</span>
        <select
          aria-label={t("Settings_GroupingHelp")}
          value={settings.pickerGrouping}
          disabled={busy}
          onChange={(event) => {
            void patchSettings({ pickerGrouping: event.currentTarget.value as PickerGrouping });
          }}
        >
          {GROUPING_OPTIONS.map((grouping) => (
            <option key={grouping} value={grouping}>{GROUPING_LABELS[grouping]}</option>
          ))}
        </select>
      </label>
      <p>{t("Settings_GroupingCaption")}</p>

      <label>
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
      <p>{t("Settings_ReduceMotionCaption")}</p>

      <label>
        <input
          type="checkbox"
          checked={settings.deviceCards}
          disabled={busy}
          onChange={(event) => {
            void patchSettings({ deviceCards: event.currentTarget.checked });
          }}
        />
        <span>{t("Main_MappingsReadAsSimpleSentence")}</span>
      </label>

      <label>
        <input
          type="checkbox"
          checked={!settings.tutorialSeen}
          disabled={busy}
          onChange={(event) => {
            void patchSettings({ tutorialSeen: !event.currentTarget.checked });
          }}
        />
        <span>{t("Settings_ShowTutorial")}</span>
      </label>

      <label>
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

      <h3>{t("Settings_Privacy_Heading")}</h3>
      <p>{t("Settings_UsageCaption")}</p>
      <label>
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
      <p>{t("Settings_CrashCaption")}</p>
      <label>
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

      <h3>{t("Settings_SendFeedback")}</h3>
      <p>{t("Settings_FeedbackCaption")}</p>
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

      <h3>{t("Settings_Updates")}</h3>
      <p>{t("Settings_UpdatesCaption")}</p>
      <button type="button" disabled={busy || client.checkForUpdate === undefined} onClick={() => void checkUpdates()}>
        {busy ? t("Settings_Checking") : t("Settings_CheckUpdates")}
      </button>
      {update?.isNewer === true && update.downloadUrl !== null ? (
        <button type="button" disabled={busy} onClick={() => void openDownload()}>
          {t("Settings_OpenDownload")}
        </button>
      ) : null}
    </div>
  );
}
