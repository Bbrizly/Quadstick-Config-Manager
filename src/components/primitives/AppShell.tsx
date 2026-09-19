import type { ReactNode } from "react";

import type { ThemePreference } from "../../app/theme";
import { useI18n, type MessageKey } from "../../i18n";
import { Icon, type IconName } from "./icons";

export type ShellDestination = "home" | "device" | "community";

export interface AppShellProps {
  readonly activeDestination: ShellDestination;
  readonly onNavigate: (destination: ShellDestination) => void;
  readonly themePreference: ThemePreference;
  readonly onThemePreferenceChange: (preference: ThemePreference) => void;
  readonly onOpenSettings: () => void;
  readonly settingsOpen?: boolean;
  readonly children: ReactNode;
}

const DESTINATIONS: readonly {
  id: ShellDestination;
  labelKey: MessageKey;
  icon: IconName;
}[] = [
  { id: "home", labelKey: "Shell_Home", icon: "home" },
  { id: "device", labelKey: "Shell_ManageFilesOnYourQuadStick", icon: "device" },
  { id: "community", labelKey: "Shell_BrowseCommunityProfiles", icon: "community" },
];

export function AppShell({
  activeDestination,
  onNavigate,
  themePreference,
  onThemePreferenceChange,
  onOpenSettings,
  settingsOpen = false,
  children,
}: AppShellProps) {
  const { t } = useI18n();
  return (
    <div className="app-shell">
      <a className="skip-link" href="#qcm-main">
        {t("Rewrite_SkipToMainContent")}
      </a>
      <header className="shell-header appchrome">
        <button
          className="shell-brand shellbrandbutton"
          type="button"
          aria-label={t("Shell_QuadStickConfigManagerGoTo")}
          onClick={() => onNavigate("home")}
        >
          <img className="shell-brand-mark" src="/assets/app-icon.png" alt="" width={38} height={38} />
          <span className="shell-brand-copy">
            <span className="shell-brand-name">QCM</span>
            <span className="shell-brand-caption">{t("Rewrite_ProductName")}</span>
          </span>
        </button>
        <nav className="shell-nav" aria-label={t("Rewrite_PrimaryNavigation")}>
          {DESTINATIONS.map((destination) => (
            <button
              className={
                activeDestination === destination.id && !settingsOpen
                  ? "shell-nav-button shellnav active"
                  : "shell-nav-button shellnav"
              }
              type="button"
              key={destination.id}
              aria-label={t(destination.labelKey)}
              aria-current={
                activeDestination === destination.id && !settingsOpen ? "page" : undefined
              }
              onClick={() => onNavigate(destination.id)}
            >
              <Icon name={destination.icon} size={32} />
            </button>
          ))}
        </nav>
        <div className="shell-utilities">
          <label>
            <span className="visually-hidden">{t("Settings_Appearance")}</span>
            <select
              className="appearance-picker"
              aria-label={t("Settings_Appearance")}
              value={themePreference}
              onChange={(event) =>
                onThemePreferenceChange(event.currentTarget.value as ThemePreference)
              }
            >
              <option value="system">{t("Theme_System")}</option>
              <option value="light">{t("Theme_Light")}</option>
              <option value="dark">{t("Theme_Dark")}</option>
            </select>
          </label>
          <button
            className={settingsOpen ? "shell-settings-button shellutility active" : "shell-settings-button shellutility"}
            type="button"
            aria-label={t("Shell_OpenSettings")}
            aria-pressed={settingsOpen}
            onClick={onOpenSettings}
          >
            <Icon name="settings" size={40} />
          </button>
        </div>
      </header>
      <main className="shell-main" id="qcm-main" tabIndex={-1}>
        {children}
      </main>
    </div>
  );
}
