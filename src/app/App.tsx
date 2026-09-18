import { useCallback, useEffect, useState } from "react";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { GoogleDriveSettings, ProfileDriveActions } from "../features/cloud/GoogleDrivePanel";
import { CommunityProfilesPage } from "../features/community/CommunityProfilesPage";
import { CrashReportPrompt } from "../features/diagnostics/CrashReportPrompt";
import { DeviceLibraryPage } from "../features/device/DeviceLibraryPage";
import { DevicePreferencesPage } from "../features/device/DevicePreferencesPage";
import { InstallProfileDialog } from "../features/device/InstallProfileDialog";
import { EditorWorkspace } from "../features/editor/EditorWorkspace";
import { WorkbookImportReviewDialog } from "../features/import/WorkbookImportReview";
import { SettingsPage } from "../features/settings/SettingsPage";
import { TutorialTour } from "../features/tutorial/TutorialTour";
import {
  I18nProvider,
  useI18n,
  type MessageKey,
} from "../i18n";
import { localizedErrorMessage } from "../i18n/errors";
import {
  MockQcmClient,
  asQcmError,
  type EditorSnapshot,
  type PendingCrashReport,
  type PendingRescue,
  type QcmClient,
  type WorkbookImportReview,
} from "../platform";
import {
  applyInterfaceScale,
  applyReduceMotion,
  applyThemePreference,
  type ThemePreference,
} from "./theme";

const DESTINATION_COPY: Record<ShellDestination, { title: MessageKey; detail: MessageKey }> = {
  home: { title: "Rewrite_ProductName", detail: "Shell_ProfilesYouSaveWillShow" },
  device: { title: "Shell_OnYourQuadStick", detail: "Shell_ManageTheProfileFilesOn" },
  community: {
    title: "Community_CommunityProfiles",
    detail: "Community_GameProfilesOtherQuadStickPlayers",
  },
};

const DEFAULT_CLIENT = new MockQcmClient();

interface AppProps {
  readonly client?: QcmClient;
}

function isDevicePreferences(snapshot: EditorSnapshot): boolean {
  return snapshot.source.kind === "device" && snapshot.source.name.toLowerCase() === "prefs.csv";
}

function LocalizedApp({ client }: { readonly client: QcmClient }) {
  const { t } = useI18n();
  const [activeDestination, setActiveDestination] = useState<ShellDestination>("home");
  const [themePreference, setThemePreference] = useState<ThemePreference>("system");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [installOpen, setInstallOpen] = useState(false);
  const [editor, setEditor] = useState<EditorSnapshot | null>(null);
  const [devicePreferences, setDevicePreferences] = useState<EditorSnapshot | null>(null);
  const [workbookReview, setWorkbookReview] = useState<WorkbookImportReview | null>(null);
  const [workbookBusy, setWorkbookBusy] = useState(false);
  const [workbookExportBusy, setWorkbookExportBusy] = useState(false);
  const [closePromptOpen, setClosePromptOpen] = useState(false);
  // Which open session the close prompt is asking about. Device preferences
  // are written back through the page's own confirmed device transaction, so
  // their prompt cannot offer a one-press Save.
  const [closePromptFor, setClosePromptFor] = useState<"editor" | "prefs">("editor");
  const [pendingDestination, setPendingDestination] = useState<ShellDestination | null>(null);
  const [closing, setClosing] = useState(false);
  const [message, setMessage] = useState("");
  const [crashReport, setCrashReport] = useState<PendingCrashReport | null>(null);
  const [rescue, setRescue] = useState<PendingRescue | null>(null);
  const [tourOpen, setTourOpen] = useState(false);

  useEffect(() => applyThemePreference(themePreference), [themePreference]);
  useEffect(() => {
    let cancelled = false;
    void client.getSettings().then(
      (settings) => {
        if (cancelled) return;
        setThemePreference(settings.theme);
        applyThemePreference(settings.theme);
        applyInterfaceScale(settings.interfaceScalePercent);
        applyReduceMotion(settings.reduceMotion);
        if (!settings.tutorialSeen) setTourOpen(true);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);
  useEffect(() => {
    const getPending = client.getPendingCrashReport;
    if (getPending === undefined) return;
    let cancelled = false;
    void getPending.call(client).then(
      (pending) => {
        if (!cancelled && pending !== null) setCrashReport(pending);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);
  useEffect(() => {
    const getRescue = client.getPendingRescue;
    if (getRescue === undefined) return;
    let cancelled = false;
    void getRescue.call(client).then(
      (pending) => {
        if (!cancelled && pending !== null) setRescue(pending);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client]);
  const closeSettings = useCallback(() => setSettingsOpen(false), []);
  const copy = DESTINATION_COPY[activeDestination];

  const openRescue = async (): Promise<void> => {
    const open = client.openRescueProfile;
    if (open === undefined || rescue === null) return;
    try {
      const opened = await open.call(client, rescue.rescueId);
      setRescue(null);
      showEditor(opened);
      setMessage(t("Main_RecoveredProfileOpenedSaveIt"));
    } catch (reason) {
      showFailure(reason);
    }
  };

  const dismissRescue = async (): Promise<void> => {
    const discard = client.discardPendingRescues;
    if (discard !== undefined) {
      try {
        await discard.call(client);
      } catch {
        /* best effort */
      }
    }
    setRescue(null);
  };

  const showFailure = useCallback(
    (reason: unknown): void => {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    },
    [t],
  );

  const showEditor = useCallback((snapshot: EditorSnapshot): void => {
    if (isDevicePreferences(snapshot)) {
      setEditor(null);
      setDevicePreferences(snapshot);
      setActiveDestination("device");
    } else {
      setDevicePreferences(null);
      setEditor(snapshot);
      setActiveDestination("home");
    }
    setMessage("");
  }, []);

  const finishEditorClose = useCallback((destination: ShellDestination): void => {
    setEditor(null);
    setDevicePreferences(null);
    setInstallOpen(false);
    setClosePromptOpen(false);
    setPendingDestination(null);
    setActiveDestination(destination);
    setMessage("");
  }, []);

  const requestPreferencesClose = useCallback(
    async (destination: ShellDestination): Promise<void> => {
      if (devicePreferences === null || closing) return;
      setClosing(true);
      try {
        const outcome = await client.closeProfile(devicePreferences.sessionId, "if_clean");
        if (outcome.kind === "keptOpenUnsavedChanges") {
          setClosePromptFor("prefs");
          setPendingDestination(destination);
          setClosePromptOpen(true);
        } else {
          finishEditorClose(destination);
        }
      } catch (reason) {
        showFailure(reason);
      } finally {
        setClosing(false);
      }
    },
    [client, closing, devicePreferences, finishEditorClose, showFailure],
  );

  const openProfile = async (): Promise<void> => {
    try {
      const opened = await client.chooseAndOpenProfile();
      if (opened !== null) showEditor(opened);
    } catch (reason) {
      showFailure(reason);
    }
  };

  const newProfile = async (): Promise<void> => {
    try {
      showEditor(await client.newProfile("untitled.csv"));
    } catch (reason) {
      showFailure(reason);
    }
  };

  const importWorkbook = async (): Promise<void> => {
    const choose = client.chooseAndImportWorkbook;
    if (choose === undefined || workbookBusy) return;
    setWorkbookBusy(true);
    try {
      const review = await choose.call(client);
      if (review !== null) {
        setWorkbookReview(review);
        setMessage("");
      }
    } catch (reason) {
      showFailure(reason);
    } finally {
      setWorkbookBusy(false);
    }
  };

  const repairWorkbookTab = async (tabIndex: number): Promise<void> => {
    const repair = client.repairWorkbookTab;
    if (repair === undefined || workbookReview === null || workbookBusy) return;
    setWorkbookBusy(true);
    try {
      setWorkbookReview(await repair.call(client, workbookReview.importId, tabIndex));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setWorkbookBusy(false);
    }
  };

  const acceptWorkbook = async (): Promise<void> => {
    const accept = client.acceptWorkbookImport;
    if (accept === undefined || workbookReview === null || workbookBusy) return;
    setWorkbookBusy(true);
    try {
      const opened = await accept.call(client, workbookReview.importId);
      setWorkbookReview(null);
      showEditor(opened);
    } catch (reason) {
      showFailure(reason);
    } finally {
      setWorkbookBusy(false);
    }
  };

  const cancelWorkbook = async (): Promise<void> => {
    if (workbookReview === null || workbookBusy) return;
    const importId = workbookReview.importId;
    setWorkbookReview(null);
    const cancel = client.cancelWorkbookImport;
    if (cancel === undefined) return;
    try {
      await cancel.call(client, importId);
    } catch (reason) {
      showFailure(reason);
    }
  };

  const exportWorkbook = async (): Promise<void> => {
    const exportXlsx = client.exportProfileXlsx;
    if (editor === null || exportXlsx === undefined || workbookExportBusy) return;
    setWorkbookExportBusy(true);
    try {
      const receipt = await exportXlsx.call(client, editor.sessionId, editor.revision);
      if (receipt !== null) setMessage(t("Main_SavedToSavePath", [receipt.name]));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setWorkbookExportBusy(false);
    }
  };

  const requestEditorClose = useCallback(
    async (destination: ShellDestination): Promise<void> => {
      if (editor === null || closing) return;
      setClosing(true);
      try {
        const outcome = await client.closeProfile(editor.sessionId, "if_clean");
        if (outcome.kind === "keptOpenUnsavedChanges") {
          setClosePromptFor("editor");
          setPendingDestination(destination);
          setClosePromptOpen(true);
        } else {
          finishEditorClose(destination);
        }
      } catch (reason) {
        showFailure(reason);
      } finally {
        setClosing(false);
      }
    },
    [client, closing, editor, finishEditorClose, showFailure],
  );

  const saveAndClose = useCallback(async (): Promise<void> => {
    if (editor === null || closing) return;
    setClosing(true);
    try {
      if (editor.saveTarget === null) {
        const receipt = await client.saveProfileAs(editor.sessionId, editor.revision);
        if (receipt === null) return;
        const outcome = await client.closeProfile(editor.sessionId, "if_clean");
        if (outcome.kind === "keptOpenUnsavedChanges") {
          throw new Error("profile remained dirty after save as");
        }
      } else {
        await client.closeProfile(editor.sessionId, "save");
      }
      finishEditorClose(pendingDestination ?? "home");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setClosing(false);
    }
  }, [client, closing, editor, finishEditorClose, pendingDestination, showFailure]);

  const discardAndClose = useCallback(async (): Promise<void> => {
    const session = closePromptFor === "prefs" ? devicePreferences : editor;
    if (session === null || closing) return;
    setClosing(true);
    try {
      await client.closeProfile(session.sessionId, "discard");
      finishEditorClose(pendingDestination ?? (closePromptFor === "prefs" ? "device" : "home"));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setClosing(false);
    }
  }, [client, closePromptFor, closing, devicePreferences, editor, finishEditorClose, pendingDestination, showFailure]);

  const cancelClose = useCallback((): void => {
    if (closing) return;
    setClosePromptOpen(false);
    setPendingDestination(null);
  }, [closing]);

  const navigate = useCallback(
    (destination: ShellDestination): void => {
      if (devicePreferences !== null && destination !== "device") {
        void requestPreferencesClose(destination);
        return;
      }
      if (editor !== null && destination !== "home") {
        void requestEditorClose(destination);
        return;
      }
      setActiveDestination(destination);
    },
    [devicePreferences, editor, requestEditorClose, requestPreferencesClose],
  );

  let content;
  if (activeDestination === "home" && editor !== null) {
    content = (
      <section className="editor-route" aria-label={t("Shell_Profile")}>
        <div className="editor-route-actions">
          {client.exportProfileXlsx === undefined ? null : (
            <button type="button" disabled={closing || workbookExportBusy} onClick={() => void exportWorkbook()}>
              {t("Rewrite_SaveXlsx")}
            </button>
          )}
          <button type="button" disabled={closing} onClick={() => setInstallOpen(true)}>
            {t("Shell_InstallToQuadStick")}
          </button>
          <button type="button" disabled={closing} onClick={() => void requestEditorClose("home")}>
            {t("Community_Close")}
          </button>
        </div>
        <ProfileDriveActions
          client={client}
          snapshot={editor}
          onSnapshot={setEditor}
          onReview={(review) => {
            setWorkbookReview(review);
            setMessage("");
          }}
        />
        <EditorWorkspace client={client} snapshot={editor} onSnapshot={setEditor} />
      </section>
    );
  } else if (activeDestination === "home") {
    content = (
      <section className="shell-placeholder home-start" aria-labelledby="page-title">
        <h1 id="page-title">{t(copy.title)}</h1>
        <p data-testid="boot-state">{t(copy.detail)}</p>
        {rescue === null ? null : (
          <section className="rescue-offer" aria-label={t("Shell_OpenRecoveredWork")}>
            <p>{t("Main_UnsavedWorkFromLastTime", [rescue.displayName])}</p>
            <div className="home-start-actions">
              <button className="primary-action" type="button" onClick={() => void openRescue()}>
                {t("Shell_OpenRecoveredWork")}
              </button>
              <button
                type="button"
                aria-label={t("Shell_DiscardTheRecoveredWorkPermanently")}
                onClick={() => void dismissRescue()}
              >
                {t("Shell_Dismiss")}
              </button>
            </div>
          </section>
        )}
        <div className="home-start-actions">
          <button className="primary-action" type="button" onClick={() => void newProfile()}>
            {t("Shell_NewProfile")}
          </button>
          <button type="button" onClick={() => void openProfile()}>
            {t("Shell_OpenAProfileFile")}
          </button>
          {client.chooseAndImportWorkbook !== undefined ? (
            <button type="button" disabled={workbookBusy} onClick={() => void importWorkbook()}>
              {t("Community_Import")}
            </button>
          ) : null}
        </div>
      </section>
    );
  } else if (activeDestination === "device" && devicePreferences !== null) {
    content = (
      <DevicePreferencesPage
        client={client}
        snapshot={devicePreferences}
        onSnapshot={setDevicePreferences}
        onClose={() => void requestPreferencesClose("device")}
      />
    );
  } else if (activeDestination === "device") {
    content = <DeviceLibraryPage client={client} onOpenProfile={showEditor} />;
  } else if (activeDestination === "community") {
    content = (
      <CommunityProfilesPage
        client={client}
        onReview={(review) => {
          setWorkbookReview(review);
          setMessage("");
        }}
      />
    );
  } else {
    content = (
      <section className="shell-placeholder" aria-labelledby="page-title">
        <h1 id="page-title">{t(copy.title)}</h1>
        <p data-testid="boot-state">{t(copy.detail)}</p>
      </section>
    );
  }

  return (
    <>
      <AppShell
        activeDestination={activeDestination}
        onNavigate={navigate}
        themePreference={themePreference}
        onThemePreferenceChange={setThemePreference}
        onOpenSettings={() => setSettingsOpen(true)}
      >
        {content}
      </AppShell>
      <LiveRegion>{message}</LiveRegion>
      {crashReport === null ? null : (
        <CrashReportPrompt
          client={client}
          report={crashReport}
          onResolved={(statusMessage) => {
            setCrashReport(null);
            setMessage(statusMessage);
          }}
          onDismiss={() => setCrashReport(null)}
        />
      )}
      <TutorialTour
        open={tourOpen}
        onDone={() => {
          setTourOpen(false);
          void client.getSettings().then((settings) => {
            void client.updateSettings(settings.revision, { tutorialSeen: true });
          });
        }}
      />
      {editor === null ? null : (
        <InstallProfileDialog client={client} profile={editor} open={installOpen} onClose={() => setInstallOpen(false)} />
      )}
      <WorkbookImportReviewDialog
        review={workbookReview}
        busy={workbookBusy}
        onRepair={(tabIndex) => void repairWorkbookTab(tabIndex)}
        onAccept={() => void acceptWorkbook()}
        onCancel={() => void cancelWorkbook()}
      />
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
        <SettingsPage client={client} onThemeChange={setThemePreference} />
        <GoogleDriveSettings
          client={client}
          onReview={(review) => {
            setWorkbookReview(review);
            setSettingsOpen(false);
            setMessage("");
          }}
        />
      </Dialog>
      <Dialog
        open={closePromptOpen}
        title={t("Shell_Profile")}
        onClose={cancelClose}
        actions={
          <>
            <button type="button" disabled={closing} onClick={cancelClose}>{t("Device_Cancel")}</button>
            <button type="button" disabled={closing} onClick={() => void discardAndClose()}>{t("Main_DonTSave")}</button>
            {closePromptFor === "prefs" ? null : (
              <button className="primary-action" type="button" data-autofocus disabled={closing} onClick={() => void saveAndClose()}>
                {t("Shell_SaveCtrlS")}
              </button>
            )}
          </>
        }
      >
        <p>{t("Main_ThisProfileHasUnsavedChanges")}</p>
        {closePromptFor === "prefs" ? <p>{t("Rewrite_SaveFromThePageFirst")}</p> : null}
      </Dialog>
    </>
  );
}

export function App({ client = DEFAULT_CLIENT }: AppProps = {}) {
  return (
    <I18nProvider>
      <LocalizedApp client={client} />
    </I18nProvider>
  );
}
