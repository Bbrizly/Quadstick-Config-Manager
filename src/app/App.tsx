import { useCallback, useEffect, useState } from "react";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { ToastRegion } from "../components/primitives/ToastRegion";
import { applyThemePreference, type ThemePreference } from "./theme";

const DESTINATION_COPY: Record<ShellDestination, { title: string; detail: string }> = {
  home: {
    title: "QuadStick Config Manager",
    detail: "No profile is loaded.",
  },
  device: {
    title: "Your QuadStick",
    detail: "Device workflows plug into this shell without changing its navigation or focus model.",
  },
  community: {
    title: "Community profiles",
    detail: "Community workflows plug into this shell without adding a second navigation system.",
  },
};

export function App() {
  const [activeDestination, setActiveDestination] = useState<ShellDestination>("home");
  const [themePreference, setThemePreference] = useState<ThemePreference>("system");
  const [settingsOpen, setSettingsOpen] = useState(false);

  useEffect(() => {
    applyThemePreference(themePreference);
  }, [themePreference]);

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
          <h1 id="page-title">{copy.title}</h1>
          <p data-testid="boot-state">{copy.detail}</p>
        </section>
      </AppShell>

      <LiveRegion>{`${copy.title} selected`}</LiveRegion>
      <ToastRegion messages={[]} />

      <Dialog
        open={settingsOpen}
        title="Settings"
        onClose={closeSettings}
        actions={
          <button className="primary-action" type="button" data-autofocus onClick={closeSettings}>
            Done
          </button>
        }
      >
        <p>Appearance follows the System, Light, or Dark choice in the app header.</p>
      </Dialog>
    </>
  );
}
