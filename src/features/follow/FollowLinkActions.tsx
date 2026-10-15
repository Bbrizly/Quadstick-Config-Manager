import { useEffect, useRef, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import { asQcmError, type EditorSnapshot, type ProfileChange, type QcmClient } from "../../platform";
import { changeText, sheetRefusalText } from "./sheetText";

export interface FollowLinkActionsProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
}

interface Review {
  readonly planId: string;
  readonly changes: readonly ProfileChange[];
  readonly singleTab: boolean;
}

const SHOWN_CHANGES = 12;

/**
 * "Get the latest from the link" and "Forget the link" for a profile that
 * follows a Google Sheet (`MainWindow.Follow.cs`). Asking whether it follows
 * is local; the sheet is fetched only on the press.
 */
export function FollowLinkActions({ client, snapshot, onSnapshot }: FollowLinkActionsProps) {
  const { t } = useI18n();
  const [known, setKnown] = useState<{ readonly key: string; readonly follows: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [askSave, setAskSave] = useState(false);
  const [review, setReview] = useState<Review | null>(null);
  const busyRef = useRef(false);

  const getState = client.getFollowState;
  const check = client.checkLatestFromLink;
  const replace = client.replaceWithLatest;
  const forget = client.forgetFollowLink;
  const { sessionId, saveTarget, dirty } = snapshot;

  // Re-asked when the session, its file, or its saved state changes: a save
  // is what turns a pending link into a followed one.
  const stateKey = `${sessionId}|${saveTarget ?? ""}|${String(dirty)}`;
  useEffect(() => {
    if (getState === undefined || saveTarget === null) return undefined;
    let cancelled = false;
    const key = `${sessionId}|${saveTarget}|${String(dirty)}`;
    void getState.call(client, sessionId).then(
      (state) => {
        if (!cancelled) setKnown({ key, follows: state.follows });
      },
      () => {
        if (!cancelled) setKnown({ key, follows: false });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [client, getState, sessionId, saveTarget, dirty]);
  const follows = saveTarget !== null && known?.key === stateKey && known.follows;
  const setFollows = (value: boolean): void => setKnown({ key: stateKey, follows: value });

  if (getState === undefined || check === undefined || replace === undefined) return null;

  const fail = (reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  };

  const run = async (current: EditorSnapshot): Promise<void> => {
    setMessage(t("Main_DownloadingTheSpreadsheet"));
    const answer = await check.call(client, current.sessionId, current.revision);
    switch (answer.kind) {
      case "busy":
        return;
      case "not_followed":
        setFollows(false);
        setMessage(t("Follow_NotFollowed"));
        return;
      case "unsaved":
        setMessage(t("Follow_ChangedMeanwhile"));
        return;
      case "refused":
        setMessage(t("Follow_CouldNotGet", [sheetRefusalText(answer.refusal, t)]));
        return;
      case "up_to_date":
        setMessage(t("Follow_UpToDate"));
        return;
      case "changes":
        setMessage("");
        setReview({ planId: answer.planId, changes: answer.changes, singleTab: answer.singleTab });
    }
  };

  // One download at a time: a mouth stick can double-fire one press.
  const guarded = async (work: () => Promise<void>): Promise<void> => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    try {
      await work();
    } catch (reason) {
      fail(reason);
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  };

  const getLatest = (): void => {
    // Unsaved work is never thrown away to make room for the sheet: it is
    // saved first, after asking, and a no stops before any request.
    if (dirty) {
      setAskSave(true);
      return;
    }
    void guarded(() => run(snapshot));
  };

  const saveThenGet = (): void => {
    setAskSave(false);
    void guarded(async () => {
      await client.saveProfile(snapshot.sessionId, snapshot.revision);
      const saved = await client.getProfileSnapshot(snapshot.sessionId);
      onSnapshot(saved);
      await run(saved);
    });
  };

  const answerReview = (yes: boolean): void => {
    const pending = review;
    setReview(null);
    if (pending === null) return;
    if (!yes) {
      setMessage(t("Follow_Kept"));
      return;
    }
    void guarded(async () => {
      const result = await replace.call(client, pending.planId);
      if (result.kind === "replaced") {
        onSnapshot(result.snapshot);
        setMessage(t("Follow_Replaced"));
      } else {
        setMessage(t("Follow_ChangedMeanwhile"));
      }
    });
  };

  const stopFollowing = (): void => {
    if (forget === undefined) return;
    void guarded(async () => {
      const state = await forget.call(client, snapshot.sessionId);
      setFollows(state.follows);
      setMessage(t("Follow_Forgotten"));
    });
  };

  if (!follows) {
    return message.length === 0 ? null : <LiveRegion visuallyHidden={false}>{message}</LiveRegion>;
  }

  const shown = review?.changes.slice(0, SHOWN_CHANGES) ?? [];
  return (
    <section className="profile-follow-actions" aria-label={t("Follow_CardNote")}>
      <button
        type="button"
        disabled={busy}
        aria-label={t("Follow_GetLatestHelp")}
        onClick={getLatest}
      >
        {t("Follow_GetLatest")}
      </button>
      {forget === undefined ? null : (
        <button
          type="button"
          className="quiet"
          disabled={busy}
          aria-label={t("Follow_StopFollowingHelp")}
          onClick={stopFollowing}
        >
          {t("Follow_StopFollowing")}
        </button>
      )}
      <LiveRegion visuallyHidden={false}>{message}</LiveRegion>

      <Dialog
        open={askSave}
        title={t("Follow_SaveFirstTitle")}
        onClose={() => setAskSave(false)}
        actions={
          <>
            <button type="button" data-autofocus onClick={() => setAskSave(false)}>
              {t("Device_Cancel")}
            </button>
            <button className="primary" type="button" onClick={saveThenGet}>
              {t("Main_Save")}
            </button>
          </>
        }
      >
        <p>{t("Follow_SaveFirstBody")}</p>
      </Dialog>

      <Dialog
        open={review !== null}
        title={t("Follow_ReviewTitle")}
        onClose={() => answerReview(false)}
        actions={
          <>
            <button type="button" onClick={() => answerReview(true)}>
              {t("Follow_Replace")}
            </button>
            {/* The safe answer is the default, so a stray Enter keeps the copy. */}
            <button className="primary" type="button" data-autofocus onClick={() => answerReview(false)}>
              {t("Follow_Keep")}
            </button>
          </>
        }
      >
        <p>{t("Follow_ReviewBody")}</p>
        <ul>
          {shown.map((change, index) => (
            <li key={`${change.kind}-${String(index)}`}>{changeText(change, t)}</li>
          ))}
          {(review?.changes.length ?? 0) > SHOWN_CHANGES ? <li>…</li> : null}
        </ul>
        {/* A published link sees one tab; replacing a five-mode profile with
            it would drop four modes, so the reason is said beside the list. */}
        {review?.singleTab === true ? <p>{t("Main_ThisLinkIsAPublished")}</p> : null}
      </Dialog>
    </section>
  );
}
