import type { ReactNode } from "react";

import type { ThemePreference } from "../../app/theme";

export type ShellDestination = "home" | "device" | "community";

export interface AppShellProps {
  readonly activeDestination: ShellDestination;
  readonly onNavigate: (destination: ShellDestination) => void;
  readonly themePreference: ThemePreference;
  readonly onThemePreferenceChange: (preference: ThemePreference) => void;
  readonly onOpenSettings: () => void;
  readonly children: ReactNode;
}

interface IconProps {
  readonly name: ShellDestination | "settings";
}

function Icon({ name }: IconProps) {
  const common = {
    viewBox: "0 0 24 24",
    fill: "none",
    strokeWidth: 1.8,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    "aria-hidden": true,
    focusable: false,
  };

  if (name === "home") {
    return (
      <svg {...common}>
        <path d="M3.5 10.5 12 3.75l8.5 6.75" />
        <path d="M5.5 9.25V20h13V9.25" />
        <path d="M9.5 20v-6h5v6" />
      </svg>
    );
  }

  if (name === "device") {
    return (
      <svg {...common}>
        <rect x="5" y="3.5" width="14" height="17" rx="4" />
        <path d="M9 8h6M9 12h6M10 16h4" />
      </svg>
    );
  }

  if (name === "community") {
    return (
      <svg {...common}>
        <circle cx="9" cy="8" r="3" />
        <circle cx="16.5" cy="9.5" r="2.25" />
        <path d="M3.75 19c.5-3.25 2.25-5 5.25-5s4.75 1.75 5.25 5" />
        <path d="M14.25 14.5c2.75-.5 5 .9 5.75 3.75" />
      </svg>
    );
  }

  return (
    <svg {...common}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19 12a7 7 0 0 0-.1-1.15l2-1.55-2-3.45-2.45 1a7 7 0 0 0-2-1.15L14.1 3h-4.2l-.35 2.7a7 7 0 0 0-2 1.15l-2.45-1-2 3.45 2 1.55A7 7 0 0 0 5 12c0 .4.03.78.1 1.15l-2 1.55 2 3.45 2.45-1a7 7 0 0 0 2 1.15l.35 2.7h4.2l.35-2.7a7 7 0 0 0 2-1.15l2.45 1 2-3.45-2-1.55c.07-.37.1-.75.1-1.15Z" />
    </svg>
  );
}

const DESTINATIONS: readonly { id: ShellDestination; label: string }[] = [
  { id: "home", label: "Home" },
  { id: "device", label: "Manage files on your QuadStick" },
  { id: "community", label: "Browse community profiles" },
];

export function AppShell({
  activeDestination,
  onNavigate,
  themePreference,
  onThemePreferenceChange,
  onOpenSettings,
  children,
}: AppShellProps) {
  return (
    <div className="app-shell">
      <a className="skip-link" href="#qcm-main">
        Skip to main content
      </a>

      <header className="shell-header">
        <button
          className="shell-brand"
          type="button"
          aria-label="QuadStick Config Manager, go to Home"
          onClick={() => onNavigate("home")}
        >
          <span className="shell-brand-mark" aria-hidden="true">
            Q
          </span>
          <span className="shell-brand-copy">
            <span className="shell-brand-name">QCM</span>
            <span className="shell-brand-caption">QuadStick Config Manager</span>
          </span>
        </button>

        <nav className="shell-nav" aria-label="Primary">
          {DESTINATIONS.map((destination) => (
            <button
              className="shell-nav-button"
              type="button"
              key={destination.id}
              aria-label={destination.label}
              aria-current={activeDestination === destination.id ? "page" : undefined}
              onClick={() => onNavigate(destination.id)}
            >
              <Icon name={destination.id} />
            </button>
          ))}
        </nav>

        <div className="shell-utilities">
          <label>
            <span className="visually-hidden">Appearance</span>
            <select
              className="appearance-picker"
              aria-label="Appearance"
              value={themePreference}
              onChange={(event) =>
                onThemePreferenceChange(event.currentTarget.value as ThemePreference)
              }
            >
              <option value="system">System</option>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
            </select>
          </label>
          <button
            className="shell-settings-button"
            type="button"
            aria-label="Open settings"
            onClick={onOpenSettings}
          >
            <Icon name="settings" />
          </button>
        </div>
      </header>

      <main className="shell-main" id="qcm-main" tabIndex={-1}>
        {children}
      </main>
    </div>
  );
}
