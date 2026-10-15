import { useEffect, useRef } from "react";

/**
 * Runs `onRun` once per request number, when `ready`. A Home card asks the
 * editor to do something on the profile it just opened; the editor's own
 * actions own the work, and this only says when.
 */
export function RunOnce<T extends { readonly n: number }>({
  request,
  ready,
  onRun,
}: {
  readonly request: T | undefined;
  readonly ready: boolean;
  readonly onRun: (request: T) => void;
}) {
  const handled = useRef(0);
  useEffect(() => {
    if (!ready || request === undefined || request.n === handled.current) return;
    handled.current = request.n;
    onRun(request);
  }, [ready, request, onRun]);
  return null;
}
