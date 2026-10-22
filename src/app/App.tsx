import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { DriveBackupNotices } from "../features/cloud/DriveBackupNotices";
import { GoogleDriveSettings, ProfileDriveActions } from "../features/cloud/GoogleDrivePanel";
import { CommunityProfilesPage } from "../features/community/CommunityProfilesPage";
import { CrashReportPrompt } from "../features/diagnostics/CrashReportPrompt";
import { UsageNotice } from "../features/diagnostics/UsageNotice";
import { DeviceFilesDialog } from "../features/device/DeviceFilesDialog";
import { DeviceSettingsRoute } from "../features/device/DeviceSettingsRoute";
import { InstallProfileDialog } from "../features/device/InstallProfileDialog";
import { EditorWorkspace } from "../features/editor/EditorWorkspace";
import { FollowLinkActions } from "../features/follow/FollowLinkActions";
import { sheetRefusalText } from "../features/follow/sheetText";
import { HomePage } from "../features/home/HomePage";
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
  USAGE_NOTICE_VERSION,
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
  const { t, plural } = useI18n();
  const [activeDestination, setActiveDestination] = useState<ShellDestination>("home");
  const [themePreference, setThemePreference] = useState<ThemePreference>("system");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [installOpen, setInstallOpen] = useState(false);
  const [editor, setEditor] = useState<EditorSnapshot | null>(null);
  const [shareSlot, setShareSlot] = useState<HTMLElement | null>(null);
  const [settingsTab, setSettingsTab] = useState<"general" | "help">("general");
  // A Home card's menu choice: the profile opens, then the action runs once.
  // A Home card's menu action, run on the card's file without opening it in
  // the editor (`ShareMenu(path)`, `GetLatestAsync(path)`, `StopFollowing(path)`).
  // The session is the file, held out of sight, and closed when it is done with.
  const [cardAction, setCardAction] = useState<{
    readonly snapshot: EditorSnapshot;
    readonly kind: "copy_link" | "open_sheet" | "latest" | "forget";
    readonly n: number;
  } | null>(null);
  const [libraryKey, setLibraryKey] = useState(0);
  const cardSession = cardAction?.snapshot.sessionId ?? null;
  useEffect(() => {
    if (cardSession === null) return undefined;
    return () => {
      void client.closeProfile(cardSession, "discard").catch(() => undefined);
    };
  }, [client, cardSession]);
  const [devicePreferences, setDevicePreferences] = useState<EditorSnapshot | null>(null);
  // `DeviceFilesWindow`, opened from the device page and from Home.
  const [filesOpen, setFilesOpen] = useState(false);
  const [workbookReview, setWorkbookReview] = useState<WorkbookImportReview | null>(null);
  // A community profile is an anonymous Sheets download (1950d85): saving can
  // never reach that sheet, so the editor says it holds a copy.
  const [reviewFromSheet, setReviewFromSheet] = useState(false);
  const [workbookBusy, setWorkbookBusy] = useState(false);
  const [sheetsMessage, setSheetsMessage] = useState("");
  const [workbookExportBusy, setWorkbookExportBusy] = useState(false);
  const [closePromptOpen, setClosePromptOpen] = useState(false);
  // Which open session the close prompt is asking about. Device preferences
  // are written back through the page's own confirmed device transaction, so
  // their prompt cannot offer a one-press Save.
  const [closePromptFor, setClosePromptFor] = useState<"editor" | "prefs">("editor");
  const [pendingDestination, setPendingDestination] = useState<ShellDestination | null>(null);
  // A profile waiting to replace the open one once its unsaved work is
  // saved or dropped. Cancel closes it instead. A second arrival while the
  // prompt is up closes the first one.
  const pendingReplacementRef = useRef<EditorSnapshot | null>(null);
  // The link listener outlives renders; it reaches the current replaceEditor.
  const replaceEditorRef = useRef<(opened: EditorSnapshot) => Promise<void>>(() => Promise.resolve());
  const [closing, setClosing] = useState(false);
  const [message, setMessage] = useState("");
  const [crashReport, setCrashReport] = useState<PendingCrashReport | null>(null);
  const [rescue, setRescue] = useState<PendingRescue | null>(null);
  const [tourOpen, setTourOpen] = useState(false);
  // The usage notice comes before the crash prompt and the tour, so nobody
  // is asked about a crash before being told what this app sends.
  const [notice, setNotice] = useState<"checking" | "show" | "done">("checking");

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
    let cancelled = false;
    void client.getAppSnapshot().then(
      (snapshot) => {
        if (cancelled) return;
        const ask = snapshot.capabilities.usageData
          && snapshot.settings.telemetryNoticeVersion < USAGE_NOTICE_VERSION;
        setNotice(ask ? "show" : "done");
      },
      () => {
        if (!cancelled) setNotice("done");
      },
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
  const copy = DESTINATION_COPY[activeDestination];

  const openRescue = async (): Promise<void> => {
    const open = client.openRescueProfile;
    if (open === undefined || rescue === null) return;
    try {
      const opened = await open.call(client, rescue.rescueId);
      setRescue(null);
      await replaceEditor(opened);
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

  // A website's "Open in QCM" link: resolve the registry id, fetch the
  // reviewed snapshot and open it in the editor. It never writes a device.
  // A link can arrive at launch or, through the OS or a second launch, while
  // the app runs. Like `App.axaml.cs`, a running app opens it straight away.
  useEffect(() => {
    const take = client.takeLaunchProfileLink;
    const open = client.openRegistryProfile;
    if (open === undefined) return undefined;
    let cancelled = false;
    const openLink = async (profileId: string): Promise<void> => {
      setMessage(t("Community_LoadingTheCommunityList2"));
      try {
        const opened = await open.call(client, profileId);
        if (cancelled) await client.closeProfile(opened.sessionId, "discard").catch(() => undefined);
        else await replaceEditorRef.current(opened);
      } catch {
        if (!cancelled) setMessage(t("Community_TheCommunityListCouldNot"));
      }
    };
    if (take !== undefined) {
      void take.call(client).then(
        (profileId) => {
          if (!cancelled && profileId !== null) void openLink(profileId);
        },
        () => undefined,
      );
    }
    let subscription: { dispose(): void } | null = null;
    const subscribe = client.subscribeProfileLinks;
    if (subscribe !== undefined) {
      void subscribe
        .call(client, (profileId) => {
          if (!cancelled) void openLink(profileId);
        })
        .then(
          (next) => {
            if (cancelled) next.dispose();
            else subscription = next;
          },
          () => undefined,
        );
    }
    return () => {
      cancelled = true;
      subscription?.dispose();
    };
  }, [client, t]);

  // The window's close button while a profile has unsaved work
  // (`MainWindow.Closing`): the same prompt as leaving the profile, and the
  // app quits once it is answered. Cancel keeps the window.
  const quitAfterCloseRef = useRef(false);
  const closeRequestRef = useRef<() => void>(() => undefined);
  const finishEditorClose = useCallback((destination: ShellDestination): void => {
    if (quitAfterCloseRef.current) {
      quitAfterCloseRef.current = false;
      void client.quitApp?.().catch(() => undefined);
    }
    setEditor(null);
    setDevicePreferences(null);
    setInstallOpen(false);
    setClosePromptOpen(false);
    setPendingDestination(null);
    setActiveDestination(destination);
    setMessage("");
    const replacement = pendingReplacementRef.current;
    pendingReplacementRef.current = null;
    if (replacement !== null) showEditor(replacement);
  }, [client, showEditor]);

  // Every way a profile arrives (a link, a workbook, a file, recovered
  // work) goes through here. The open profile is closed first, its session
  // with it; unsaved work is asked about (Save / Don't save / Cancel)
  // instead of being dropped. `dropPreferences` is the files window's
  // `OpenDeviceProfile`: device settings edits go without asking.
  const replaceEditor = async (
    opened: EditorSnapshot,
    dropPreferences = false,
  ): Promise<void> => {
    const ask = (forWhat: "editor" | "prefs"): void => {
      const earlier = pendingReplacementRef.current;
      if (earlier !== null && earlier.sessionId !== opened.sessionId) {
        void client.closeProfile(earlier.sessionId, "discard").catch(() => undefined);
      }
      pendingReplacementRef.current = opened;
      setPendingDestination(null);
      setClosePromptFor(forWhat);
      setClosePromptOpen(true);
    };
    const refuse = async (reason: unknown): Promise<void> => {
      await client.closeProfile(opened.sessionId, "discard").catch(() => undefined);
      showFailure(reason);
    };
    if (editor !== null && editor.sessionId !== opened.sessionId) {
      let outcome;
      try {
        outcome = await client.closeProfile(editor.sessionId, "if_clean");
      } catch (reason) {
        await refuse(reason);
        return;
      }
      if (outcome.kind === "keptOpenUnsavedChanges") {
        ask("editor");
        return;
      }
    }
    if (devicePreferences !== null && devicePreferences.sessionId !== opened.sessionId) {
      if (dropPreferences) {
        await client.closeProfile(devicePreferences.sessionId, "discard").catch(() => undefined);
      } else {
        let outcome;
        try {
          outcome = await client.closeProfile(devicePreferences.sessionId, "if_clean");
        } catch (reason) {
          await refuse(reason);
          return;
        }
        if (outcome.kind === "keptOpenUnsavedChanges") {
          ask("prefs");
          return;
        }
      }
    }
    showEditor(opened);
  };
  useEffect(() => {
    replaceEditorRef.current = replaceEditor;
  });

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

  // A file opened from the files window replaces whatever is open, as
  // `OpenDeviceProfile` does: device settings edits are dropped (Avalonia
  // reads them again on the next visit), editor work is asked about first.
  const openFromDeviceFiles = async (opened: EditorSnapshot): Promise<void> => {
    setFilesOpen(false);
    await replaceEditor(opened, true);
  };

  const openProfile = async (): Promise<void> => {
    try {
      const opened = await client.chooseAndOpenProfile();
      if (opened !== null) await replaceEditor(opened);
    } catch (reason) {
      showFailure(reason);
    }
  };

  const newProfile = async (): Promise<void> => {
    try {
      await replaceEditor(await client.newProfile("untitled.csv"));
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
        setReviewFromSheet(false);
        setMessage("");
      }
    } catch (reason) {
      showFailure(reason);
    } finally {
      setWorkbookBusy(false);
    }
  };

  // The pasted link on Home: the same review as a workbook, then the profile
  // remembers the link once it is saved (`ImportSheetsAsync`).
  const importSheetsLink = async (url: string): Promise<void> => {
    const importLink = client.importSheetsLink;
    if (importLink === undefined || workbookBusy) return;
    setWorkbookBusy(true);
    setSheetsMessage(t("Main_DownloadingTheSpreadsheet"));
    try {
      const result = await importLink.call(client, url);
      if (result.kind === "refused") {
        setSheetsMessage(sheetRefusalText(result.refusal, t));
        return;
      }
      setSheetsMessage("");
      setWorkbookReview(result.review);
      setReviewFromSheet(true);
      setMessage("");
    } catch (reason) {
      setSheetsMessage(localizedErrorMessage(asQcmError(reason).payload, t));
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
      const modes = workbookReview.modes.filter((mode) => mode.kind === "mode").length;
      setWorkbookReview(null);
      await replaceEditor(opened);
      if (reviewFromSheet) {
        const line = t("Main_ImportedModesImportedFromThe", [plural("Count_Mode", modes, [modes])]);
        setMessage(`${line} ${t("Main_ImportIsACopy")}`);
      }
      setReviewFromSheet(false);
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
    quitAfterCloseRef.current = false;
    setClosePromptOpen(false);
    setPendingDestination(null);
    const replacement = pendingReplacementRef.current;
    pendingReplacementRef.current = null;
    if (replacement !== null) {
      void client.closeProfile(replacement.sessionId, "discard").catch(() => undefined);
    }
  }, [client, closing]);

  const navigate = useCallback(
    (destination: ShellDestination): void => {
      if (devicePreferences !== null && destination !== "device") {
        void requestPreferencesClose(destination);
        return;
      }
      // Avalonia: every shell destination leaves the editor, Home included,
      // asking first when there is unsaved work.
      if (editor !== null) {
        void requestEditorClose(destination);
        return;
      }
      setActiveDestination(destination);
    },
    [devicePreferences, editor, requestEditorClose, requestPreferencesClose],
  );

  useEffect(() => {
    closeRequestRef.current = () => {
      quitAfterCloseRef.current = true;
      if (closePromptOpen) return;
      if (devicePreferences !== null) {
        void requestPreferencesClose("device");
      } else if (editor !== null) {
        void requestEditorClose("home");
      } else {
        // Nothing on screen holds the unsaved work, so there is nothing to ask.
        quitAfterCloseRef.current = false;
        void client.quitApp?.().catch(() => undefined);
      }
    };
  });
  useEffect(() => {
    const subscribe = client.subscribeCloseRequests;
    if (subscribe === undefined) return undefined;
    let cancelled = false;
    let subscription: { dispose(): void } | null = null;
    void subscribe
      .call(client, () => {
        if (cancelled) return;
        void client.ackCloseRequest?.().catch(() => undefined);
        closeRequestRef.current();
      })
      .then(
        (next) => {
          if (cancelled) next.dispose();
          else subscription = next;
        },
        () => undefined,
      );
    return () => {
      cancelled = true;
      subscription?.dispose();
    };
  }, [client]);

  let content;
  if (settingsOpen) {
    content = (
      <SettingsPage
        key={settingsTab}
        client={client}
        initialTab={settingsTab}
        onReplayTutorial={() => {
          setSettingsOpen(false);
          setTourOpen(true);
        }}
        onThemeChange={setThemePreference}
        extra={
          <GoogleDriveSettings
            client={client}
            onReview={(review) => {
              setWorkbookReview(review);
              setReviewFromSheet(false);
              setSettingsOpen(false);
              setMessage("");
            }}
          />
        }
      />
    );
  } else if (activeDestination === "home" && editor !== null) {
    content = (
      <section className="editor-route" aria-label={t("Shell_Profile")}>
        <ProfileDriveActions
          client={client}
          snapshot={editor}
          onSnapshot={setEditor}
          buttonsSlot={shareSlot}
          onReview={(review) => {
            setWorkbookReview(review);
            setReviewFromSheet(false);
            setMessage("");
          }}
        />
        <FollowLinkActions
          client={client}
          snapshot={editor}
          onSnapshot={setEditor}
          buttonsSlot={shareSlot}
        />
        {shareSlot === null || client.exportProfileXlsx === undefined
          ? null
          : createPortal(
              <button
                className="menu-item"
                type="button"
                disabled={closing || workbookExportBusy}
                onClick={() => void exportWorkbook()}
              >
                {t("Rewrite_SaveXlsx")}
              </button>,
              shareSlot,
            )}
        <EditorWorkspace
          client={client}
          snapshot={editor}
          onSnapshot={setEditor}
          onInstall={() => setInstallOpen(true)}
          onHelp={() => {
            setSettingsTab("help");
            setSettingsOpen(true);
          }}
          onShareSlot={setShareSlot}
          busyExternal={closing}
        />
      </section>
    );
  } else if (activeDestination === "home") {
    const cardActions =
      cardAction === null ? null : (
        <section className="card-action" aria-label={t("Shell_Profile")}>
          <ProfileDriveActions
            client={client}
            snapshot={cardAction.snapshot}
            onSnapshot={(next) => setCardAction((current) => (current === null ? null : { ...current, snapshot: next }))}
            buttonsSlot={null}
            shareRequest={
              cardAction.kind === "copy_link" || cardAction.kind === "open_sheet"
                ? { n: cardAction.n, open: cardAction.kind === "open_sheet" }
                : undefined
            }
            onReview={(review) => {
              setWorkbookReview(review);
              setReviewFromSheet(false);
              setMessage("");
            }}
          />
          <FollowLinkActions
            client={client}
            snapshot={cardAction.snapshot}
            onSnapshot={(next) => setCardAction((current) => (current === null ? null : { ...current, snapshot: next }))}
            onChanged={() => setLibraryKey((key) => key + 1)}
            buttonsSlot={null}
            request={
              cardAction.kind === "latest" || cardAction.kind === "forget"
                ? { n: cardAction.n, kind: cardAction.kind }
                : undefined
            }
          />
        </section>
      );
    content = (
      <>
      {cardActions}
      <HomePage
        client={client}
        rescue={rescue}
        workbookBusy={workbookBusy}
        onNewProfile={() => void newProfile()}
        onOpenProfile={() => void openProfile()}
        onImportWorkbook={() => void importWorkbook()}
        {...(client.importSheetsLink === undefined
          ? {}
          : { onImportSheetsLink: (url: string) => void importSheetsLink(url) })}
        sheetsMessage={sheetsMessage}
        onDriveReview={(review) => {
          setWorkbookReview(review);
          setReviewFromSheet(false);
          setMessage("");
        }}
        onOpenCommunity={() => setActiveDestination("community")}
        onManageDevice={() => setFilesOpen(true)}
        onOpenHelp={() => {
          setSettingsTab("help");
          setSettingsOpen(true);
        }}
        onOpenRescue={() => void openRescue()}
        onDismissRescue={() => void dismissRescue()}
        refreshKey={libraryKey}
        onOpened={(opened, then, status) => {
          if (then === undefined) {
            setCardAction(null);
            void replaceEditor(opened).then(() => {
              if (status !== undefined) setMessage(status);
            });
            return;
          }
          setCardAction({ snapshot: opened, kind: then, n: Date.now() });
        }}
      />
      </>
    );
  } else if (activeDestination === "device") {
    // Avalonia: the device nav is the settings page; files are a window.
    content = (
      <DeviceSettingsRoute
        client={client}
        snapshot={devicePreferences}
        onSnapshot={setDevicePreferences}
        onManageFiles={() => setFilesOpen(true)}
      />
    );
  } else if (activeDestination === "community") {
    content = (
      <CommunityProfilesPage
        client={client}
        onReview={(review) => {
          setWorkbookReview(review);
          setReviewFromSheet(true);
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
        activeDestination={activeDestination === "home" && editor !== null ? null : activeDestination}
        onNavigate={(destination) => {
          if (settingsOpen) setSettingsOpen(false);
          navigate(destination);
        }}
        themePreference={themePreference}
        onThemePreferenceChange={setThemePreference}
        onOpenSettings={() => {
          setSettingsTab("general");
          setSettingsOpen((open) => !open);
        }}
        settingsOpen={settingsOpen}
      >
        {content}
      </AppShell>
      <LiveRegion>{message}</LiveRegion>
      <DriveBackupNotices
        client={client}
        onMessage={setMessage}
        onReview={(review) => {
          setWorkbookReview(review);
          setReviewFromSheet(false);
        }}
      />
      {notice === "show" ? (
        <UsageNotice
          client={client}
          onAnswered={(saved) => {
            setNotice("done");
            if (!saved) setMessage(t("Main_CouldNotSaveThatPreference"));
          }}
        />
      ) : null}
      {crashReport === null || notice !== "done" ? null : (
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
        open={tourOpen && notice === "done"}
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
        open={closePromptOpen}
        title={t("Shell_Profile")}
        onClose={cancelClose}
        actions={
          <>
            <button type="button" disabled={closing} onClick={cancelClose}>{t("Device_Cancel")}</button>
            <button type="button" disabled={closing} onClick={() => void discardAndClose()}>{t("Main_DonTSave")}</button>
            {closePromptFor === "prefs" ? null : (
              <button className="primary" type="button" data-autofocus disabled={closing} onClick={() => void saveAndClose()}>
                {t("Shell_SaveCtrlS")}
              </button>
            )}
          </>
        }
      >
        <p>{t("Main_ThisProfileHasUnsavedChanges")}</p>
        {closePromptFor === "prefs" ? <p>{t("Rewrite_SaveFromThePageFirst")}</p> : null}
      </Dialog>
      <DeviceFilesDialog
        client={client}
        open={filesOpen}
        onClose={() => setFilesOpen(false)}
        onOpenProfile={(opened) => void openFromDeviceFiles(opened)}
      />
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
