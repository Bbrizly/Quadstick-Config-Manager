import { useCallback, useEffect, useState } from "react";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { ToastRegion } from "../components/primitives/ToastRegion";
import {
  I18nProvider,
  LOCALE_NAMES,
  LOCALE_TAGS,
  useI18n,
  type LocalePreference,
  type MessageKey,
} from "../i18n";
import { applyThemePreference, type ThemePreference } from "./theme";

const DESTINATION_COPY: Record<ShellDestination, { title: MessageKey; detail: MessageKey }> = {
  home: { title: "Rewrite_ProductName", detail: "Shell_ProfilesYouSaveWillShow" },
  device: { title: "Shell_OnYourQuadStick", detail: "Shell_ManageTheProfileFilesOn" },
  community: {
    title: "Community_CommunityProfiles",
    detail: "Community_GameProfilesOtherQuadStickPlayers",
  },
};

function LocalizedApp() {
  const { t, preference, setPreference } = useI18n();
  const [activeDestination, setActiveDestination] = useState<ShellDestination>("home");
  const [themePreference, setThemePreference] = useState<ThemePreference>("system");
  const [settingsOpen, setSettingsOpen] = useState(false);

  useEffect(() => applyThemePreference(themePreference), [themePreference]);
  const closeSettings = useCallback(() => setSettingsOpen(false), []);
  const copy = DESTINATION_COPY[activeDestination];

  return (
    <>
      <AppShell
        activeDestination={activeDestination}
        onNavigate={setActiveDestination}
        themePreference={themePreference}
        onThemePreferenceChange={setThemePreference}
        onOpenSettings={() => setSettingsOpen(true)}
      >
        <section className="shell-placeholder" aria-labelledby="page-title">
          <h1 id="page-title">{t(copy.title)}</h1>
          <p data-testid="boot-state">{t(copy.detail)}</p>
        </section>
      </AppShell>
      <LiveRegion>{t("Community_PickedNameIsSelected", [t(copy.title)])}</LiveRegion>
      <ToastRegion messages={[]} />
      <Dialog
        open={settingsOpen}
        title={t("Shell_Settings")}
        onClose={closeSettings}
        actions={
          <button className="primary-action" type="button" data-autofocus onClick={closeSettings}>
            {t("Main_Done")}
          </button>
        }
      >
        <div className="settings-foundation">
          <label>
            <span>{t("Settings_Language")}</span>
            <select
              aria-label={t("Settings_Language")}
              value={preference}
              onChange={(event) => setPreference(event.currentTarget.value as LocalePreference)}
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
          <p>{t("Settings_AppearanceHelp")}</p>
        </div>
      </Dialog>
    </>
  );
}

export function App() {
  return (
    <I18nProvider>
      <LocalizedApp />
    </I18nProvider>
  );
}
