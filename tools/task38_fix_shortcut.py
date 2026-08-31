from pathlib import Path

path = Path("src/features/editor/EditorWorkspace.tsx")
text = path.read_text(encoding="utf-8")

old = '''  const undo = useCallback(async (): Promise<void> => {
    if (busy || !snapshot.canUndo) return;
    setBusy(true);
    try {
      const next = await client.undoEditor(snapshot.sessionId, snapshot.revision);
      onSnapshot(next);
      setMessage("");
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot]);
'''

new = '''  const undo = useCallback(async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const current = await client.getProfileSnapshot(snapshot.sessionId);
      if (!current.canUndo) {
        onSnapshot(current);
        return;
      }
      const next = await client.undoEditor(current.sessionId, current.revision);
      onSnapshot(next);
      setMessage("");
    } catch (reason) {
      await refreshAfterConflict(reason);
    } finally {
      setBusy(false);
    }
  }, [busy, client, onSnapshot, refreshAfterConflict, snapshot.sessionId]);
'''

if text.count(old) != 1:
    raise SystemExit("expected the TASK-038 undo block exactly once")

path.write_text(text.replace(old, new), encoding="utf-8")
