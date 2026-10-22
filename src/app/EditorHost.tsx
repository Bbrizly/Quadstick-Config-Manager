import { useCallback, useEffect, useImperativeHandle, useLayoutEffect, useRef, useState, type Ref } from "react";
import { createPortal } from "react-dom";

import { LiveRegion } from "../components/primitives/LiveRegion";
import { InstallProfileDialog } from "../features/device/InstallProfileDialog";
import { EditorWorkspace } from "../features/editor/EditorWorkspace";
import { I18nProvider, useI18n } from "../i18n";
import { localizedErrorMessage } from "../i18n/errors";
import { asQcmError, type EditorSnapshot, type QcmClient } from "../platform";
import { saveThenClose, UnsavedChangesDialog } from "./unsavedChanges";

/** What a host can ask of the editor it mounted. */
export interface EditorHostHandle {
  /**
   * May the host leave this profile? Runs the editor's own unsaved-changes
   * flow: a clean profile closes at once; unsaved work asks Save / Don't save
   * / Cancel. Resolves true once the session is closed (saved or dropped, and
   * `onClosed` has run), false when the person cancels or the close failed.
   * After true the session is gone; mount the next profile afresh.
   */
  confirmLeave(): Promise<boolean>;
}

export interface EditorHostProps {
  /** The host's own client: native, or one of its own on the web. */
  readonly client: QcmClient;
  /** A session the host already opened with `client`. A new `sessionId` replaces the shown one. */
  readonly snapshot: EditorSnapshot;
  /** The session was closed through `confirmLeave`. */
  readonly onClosed?: (() => void) | undefined;
  /** Called with the current dirty state on mount and on every change. */
  readonly onDirtyChange?: ((dirty: boolean) => void) | undefined;
  /** Every new snapshot (edit, undo, save). */
  readonly onSnapshot?: ((snapshot: EditorSnapshot) => void) | undefined;
  /** One line shown in the editor header. Empty or left out: nothing shown. */
  readonly hostBanner?: string | undefined;
  /** The `?` button and F1. Left out, there is no help button. */
  readonly onHelp?: (() => void) | undefined;
  readonly ref?: Ref<EditorHostHandle> | undefined;
}

/**
 * One profile in the editor, for an app that owns its own root, client and
 * navigation. No Home, device or community destinations, no app-wide
 * prompts (usage notice, crash report, tour, links, window close): those
 * belong to the host.
 */
export function EditorHost(props: EditorHostProps) {
  return (
    <I18nProvider>
      <HostedEditor {...props} />
    </I18nProvider>
  );
}

function HostedEditor({
  client,
  snapshot: given,
  onClosed,
  onDirtyChange,
  onSnapshot,
  hostBanner,
  onHelp,
  ref,
}: EditorHostProps) {
  const { t } = useI18n();
  const [editor, setEditor] = useState<EditorSnapshot>(given);
  const [shownSession, setShownSession] = useState(given.sessionId);
  if (given.sessionId !== shownSession) {
    setShownSession(given.sessionId);
    setEditor(given);
  }
  const [closed, setClosed] = useState(false);
  const [installOpen, setInstallOpen] = useState(false);
  const [promptOpen, setPromptOpen] = useState(false);
  const [closing, setClosing] = useState(false);
  const [exportBusy, setExportBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [shareSlot, setShareSlot] = useState<HTMLElement | null>(null);

  // The pending `confirmLeave`, answered by the prompt.
  const answerRef = useRef<((leave: boolean) => void) | null>(null);
  const leavingRef = useRef<Promise<boolean> | null>(null);
  const editorRef = useRef(editor);
  const closedRef = useRef(false);
  const onClosedRef = useRef(onClosed);
  useLayoutEffect(() => {
    editorRef.current = editor;
    onClosedRef.current = onClosed;
  });

  const show = useCallback((next: EditorSnapshot): void => {
    setEditor(next);
    onSnapshot?.(next);
  }, [onSnapshot]);

  const lastDirty = useRef<boolean | null>(null);
  useEffect(() => {
    if (closed || lastDirty.current === editor.dirty) return;
    lastDirty.current = editor.dirty;
    onDirtyChange?.(editor.dirty);
  }, [closed, editor.dirty, onDirtyChange]);

  const showFailure = useCallback((reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  }, [t]);

  const finish = useCallback((leave: boolean): void => {
    const answer = answerRef.current;
    answerRef.current = null;
    leavingRef.current = null;
    setPromptOpen(false);
    if (leave) {
      closedRef.current = true;
      setClosed(true);
      setInstallOpen(false);
      onClosedRef.current?.();
    }
    answer?.(leave);
  }, []);

  useImperativeHandle(ref, () => ({
    confirmLeave(): Promise<boolean> {
      if (closedRef.current) return Promise.resolve(true);
      if (leavingRef.current !== null) return leavingRef.current;
      const leaving = new Promise<boolean>((resolve) => {
        answerRef.current = resolve;
      });
      leavingRef.current = leaving;
      setClosing(true);
      client.closeProfile(editorRef.current.sessionId, "if_clean").then(
        (outcome) => {
          setClosing(false);
          if (outcome.kind === "keptOpenUnsavedChanges") setPromptOpen(true);
          else finish(true);
        },
        (reason: unknown) => {
          setClosing(false);
          showFailure(reason);
          finish(false);
        },
      );
      return leaving;
    },
  }), [client, finish, showFailure]);

  // A host that unmounts mid-question gets a no.
  useEffect(() => () => {
    answerRef.current?.(false);
    answerRef.current = null;
  }, []);

  const save = async (): Promise<void> => {
    if (closing) return;
    setClosing(true);
    try {
      if (await saveThenClose(client, editor)) finish(true);
    } catch (reason) {
      showFailure(reason);
    } finally {
      setClosing(false);
    }
  };

  const discard = async (): Promise<void> => {
    if (closing) return;
    setClosing(true);
    try {
      await client.closeProfile(editor.sessionId, "discard");
      finish(true);
    } catch (reason) {
      showFailure(reason);
    } finally {
      setClosing(false);
    }
  };

  const cancel = (): void => {
    if (closing) return;
    finish(false);
  };

  const exportXlsx = async (): Promise<void> => {
    const exportProfile = client.exportProfileXlsx;
    if (exportProfile === undefined || exportBusy) return;
    setExportBusy(true);
    try {
      const receipt = await exportProfile.call(client, editor.sessionId, editor.revision);
      if (receipt !== null) setMessage(t("Main_SavedToSavePath", [receipt.name]));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setExportBusy(false);
    }
  };

  if (closed) return null;

  return (
    <>
      <section className="editor-route" aria-label={t("Shell_Profile")}>
        {shareSlot === null || client.exportProfileXlsx === undefined
          ? null
          : createPortal(
              <button
                className="menu-item"
                type="button"
                disabled={closing || exportBusy}
                onClick={() => void exportXlsx()}
              >
                {t("Rewrite_SaveXlsx")}
              </button>,
              shareSlot,
            )}
        <EditorWorkspace
          client={client}
          snapshot={editor}
          onSnapshot={show}
          onInstall={() => setInstallOpen(true)}
          onHelp={onHelp}
          onShareSlot={client.exportProfileXlsx === undefined ? undefined : setShareSlot}
          busyExternal={closing}
          hostBanner={hostBanner}
        />
      </section>
      <LiveRegion>{message}</LiveRegion>
      <InstallProfileDialog client={client} profile={editor} open={installOpen} onClose={() => setInstallOpen(false)} />
      <UnsavedChangesDialog
        open={promptOpen}
        closing={closing}
        canSave
        onCancel={cancel}
        onDiscard={() => void discard()}
        onSave={() => void save()}
      />
    </>
  );
}
