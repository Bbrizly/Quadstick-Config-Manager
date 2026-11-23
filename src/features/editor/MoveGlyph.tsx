/** The up or down chevron every move button in the editor draws. */
export function MoveGlyph({ up, size = 20 }: { readonly up: boolean; readonly size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path fill="currentColor" d={up ? "M7.4 15.4 12 10.8l4.6 4.6L18 14l-6-6-6 6z" : "M7.4 8.6 12 13.2l4.6-4.6L18 10l-6 6-6-6z"} />
    </svg>
  );
}
