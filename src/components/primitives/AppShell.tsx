import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";

import type { ThemePreference } from "../../app/theme";
import { useI18n, type MessageKey } from "../../i18n";
import { handleDocumentTab, keepFocusThroughBusy, rememberTabStart } from "./focus";
import { Icon, type IconName } from "./icons";

export type ShellDestination = "home" | "device" | "community";

export interface AppShellProps {
  /** Null while a page no tab owns is open (the editor), as on Avalonia. */
  readonly activeDestination: ShellDestination | null;
  readonly onNavigate: (destination: ShellDestination) => void;
  readonly themePreference: ThemePreference;
  readonly onThemePreferenceChange: (preference: ThemePreference) => void;
  readonly onOpenSettings: () => void;
  readonly settingsOpen?: boolean;
  /** Destinations this client cannot serve (no network: no community). */
  readonly hidden?: readonly ShellDestination[];
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

/** At or under this many CSS pixels the header drops the caption and picker. */
export const NARROW_SHELL = 820;

export function shellIsNarrow(width: number): boolean {
  return width > 0 && width <= NARROW_SHELL;
}

/**
 * Interface size is root `zoom`, which media queries never see (B-096), so
 * the shell measures its own CSS width. offsetWidth is CSS pixels under zoom.
 */
function useShellNarrow() {
  const shell = useRef<HTMLDivElement>(null);
  const [narrow, setNarrow] = useState(false);
  useLayoutEffect(() => {
    const element = shell.current;
    if (element === null) return undefined;
    const measure = () => setNarrow(shellIsNarrow(element.offsetWidth));
    measure();
    window.addEventListener("resize", measure);
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    observer?.observe(element);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, []);
  return { shell, narrow };
}

export function AppShell({
  activeDestination,
  onNavigate,
  themePreference,
  onThemePreferenceChange,
  onOpenSettings,
  settingsOpen = false,
  hidden = [],
  children,
}: AppShellProps) {
  const { t } = useI18n();
  const { shell, narrow } = useShellNarrow();
  useEffect(() => {
    document.addEventListener("keydown", handleDocumentTab);
    document.addEventListener("pointerdown", rememberTabStart, true);
    document.addEventListener("focusin", rememberTabStart);
    const stopKeeping = keepFocusThroughBusy();
    return () => {
      stopKeeping();
      document.removeEventListener("keydown", handleDocumentTab);
      document.removeEventListener("pointerdown", rememberTabStart, true);
      document.removeEventListener("focusin", rememberTabStart);
    };
  }, []);
  return (
    <div className="app-shell" ref={shell} data-narrow={narrow ? "true" : undefined}>
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
          {DESTINATIONS.filter((destination) => !hidden.includes(destination.id)).map((destination) => (
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
            // One control opens Settings and leaves it, so the page to go back
            // to cannot be lost. Icon and name swap with it: colour is never
            // the only signal.
            aria-label={settingsOpen ? t("Settings_BackHelp") : t("Shell_OpenSettings")}
            title={settingsOpen ? t("Main_Back") : t("Shell_Settings")}
            onClick={onOpenSettings}
          >
            <Icon name={settingsOpen ? "back" : "settings"} size={40} />
          </button>
        </div>
      </header>
      <main className="shell-main" id="qcm-main" tabIndex={-1}>
        {children}
      </main>
    </div>
  );
}
