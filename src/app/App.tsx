import { useCallback, useEffect, useState } from "react";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { ToastRegion } from "../components/primitives/ToastRegion";
import { DeviceLibraryPage } from "../features/device/DeviceLibraryPage";
import { DevicePreferencesPage } from "../features/device/DevicePreferencesPage";
import { InstallProfileDialog } from "../features/device/InstallProfileDialog";
import { EditorWorkspace } from "../features/editor/EditorWorkspace";
import { WorkbookImportReviewDialog } from "../features/import/WorkbookImportReview";
import {
  I18nProvider,
  LOCALE_NAMES,
  LOCALE_TAGS,
  useI18n,
  type LocalePreference,
  type MessageKey,
} from "../i18n";
import { localizedErrorMessage } from "../i18n/errors";
import {
  MockQcmClient,
  asQcmError,
  type EditorSnapshot,
  type QcmClient,
  type WorkbookImportReview,
} from "../platform";
import { applyThemePreference, type ThemePreference } from "./theme";

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
  const { t, preference, setPreference } = useI18n();
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
  const [pendingDestination, setPendingDestination] = useState<ShellDestination | null>(null);
  const [closing, setClosing] = useState(false);
  const [message, setMessage] = useState("");

  useEffect(() => applyThemePreference(themePreference), [themePreference]);
  const closeSettings = useCallback(() => setSettingsOpen(false), []);
  const copy = DESTINATION_COPY[activeDestination];

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
    setInstallOpen(false);
    setClosePromptOpen(false);
    setPendingDestination(null);
    setActiveDestination(destination);
    setMessage("");
  }, []);

  const closeDevicePreferences = useCallback(async (): Promise<boolean> => {
    if (devicePreferences === null) return true;
    try {
      const outcome = await client.closeProfile(devicePreferences.sessionId, "if_clean");
      if (outcome.kind === "keptOpenUnsavedChanges") {
        setMessage(t("Main_ThisProfileHasUnsavedChanges"));
        return false;
      }
      setDevicePreferences(null);
      setMessage("");
      return true;
    } catch (reason) {
      showFailure(reason);
      return false;
    }
  }, [client, devicePreferences, showFailure, t]);

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
    if (editor === null || closing) return;
    setClosing(true);
    try {
      await client.closeProfile(editor.sessionId, "discard");
      finishEditorClose(pendingDestination ?? "home");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setClosing(false);
    }
  }, [client, closing, editor, finishEditorClose, pendingDestination, showFailure]);

  const cancelClose = useCallback((): void => {
    if (closing) return;
    setClosePromptOpen(false);
    setPendingDestination(null);
  }, [closing]);

  const navigate = useCallback(
    (destination: ShellDestination): void => {
      if (devicePreferences !== null && destination !== "device") {
        void closeDevicePreferences().then((closed) => {
          if (closed) setActiveDestination(destination);
        });
        return;
      }
      if (editor !== null && destination !== "home") {
        void requestEditorClose(destination);
        return;
      }
      setActiveDestination(destination);
    },
    [closeDevicePreferences, devicePreferences, editor, requestEditorClose],
  );

  let content;
  if (activeDestination === "home" && editor !== null) {
    content = (
      <section className="editor-route" aria-label={t("Shell_Profile")}>
        <div className="editor-route-actions">
          {client.exportProfileXlsx === undefined ? null : (
            <button type="button" disabled={closing || workbookExportBusy} onClick={() => void exportWorkbook()}>
              {t("Main_Save")} .xlsx
            </button>
          )}
          <button type="button" disabled={closing} onClick={() => setInstallOpen(true)}>
            {t("Shell_InstallToQuadStick")}
          </button>
          <button type="button" disabled={closing} onClick={() => void requestEditorClose("home")}>
            {t("Community_Close")}
          </button>
        </div>
        <EditorWorkspace client={client} snapshot={editor} onSnapshot={setEditor} />
      </section>
    );
  } else if (activeDestination === "home") {
    content = (
      <section className="shell-placeholder home-start" aria-labelledby="page-title">
        <h1 id="page-title">{t(copy.title)}</h1>
        <p data-testid="boot-state">{t(copy.detail)}</p>
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
        onClose={() => void closeDevicePreferences()}
      />
    );
  } else if (activeDestination === "device") {
    content = <DeviceLibraryPage client={client} onOpenProfile={showEditor} />;
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
      <ToastRegion messages={[]} />
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
              {import.meta.env.DEV ? <option value="qps-ploc">{t("Rewrite_PseudoLocaleName")}</option> : null}
            </select>
          </label>
          <p>{t("Settings_AppearanceHelp")}</p>
        </div>
      </Dialog>
      <Dialog
        open={closePromptOpen}
        title={t("Shell_Profile")}
        onClose={cancelClose}
        actions={
          <>
            <button type="button" disabled={closing} onClick={cancelClose}>{t("Device_Cancel")}</button>
            <button type="button" disabled={closing} onClick={() => void discardAndClose()}>{t("Main_DonTSave")}</button>
            <button className="primary-action" type="button" data-autofocus disabled={closing} onClick={() => void saveAndClose()}>
              {t("Shell_SaveCtrlS")}
            </button>
          </>
        }
      >
        <p>{t("Main_ThisProfileHasUnsavedChanges")}</p>
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
