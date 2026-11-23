/** The nearest ancestor that scrolls up and down; the stage's own sideways scroller does not. */
export function verticalScroller(element: HTMLElement): HTMLElement | null {
  for (let node = element.parentElement; node !== null; node = node.parentElement) {
    const overflow = getComputedStyle(node).overflowY;
    if (overflow === "auto" || overflow === "scroll") return node;
  }
  return null;
}

export interface StageRoomInput {
  /** clientHeight of whatever scrolls the stage, CSS pixels. */
  readonly boxHeight: number;
  /** Stage top minus canvas top from getBoundingClientRect, zoomed pixels. */
  readonly rectDelta: number;
  /** Interface size as a factor: rects are zoomed, client sizes are not. */
  readonly zoom: number;
  /** The canvas's scrollTop when the canvas itself is the scroller, else 0. */
  readonly scrollTop: number;
  readonly paddingBottom: number;
  /** Height of a problems bar pinned over the scroller's bottom. */
  readonly covered: number;
}

/** CSS pixels of height the stage may fill once its canvas is scrolled to the top. */
export function stageRoom({ boxHeight, rectDelta, zoom, scrollTop, paddingBottom, covered }: StageRoomInput): number {
  const top = rectDelta / (zoom > 0 ? zoom : 1) + scrollTop;
  return boxHeight - top - paddingBottom - covered;
}
