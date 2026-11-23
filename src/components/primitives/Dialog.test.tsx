import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { Dialog } from "./Dialog";

function Harness({ tick = 0 }: { readonly tick?: number }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>Opener</button>
      {/* A fresh onClose every render, as most callers pass. */}
      <Dialog
        open={open}
        title="Question"
        onClose={() => setOpen(false)}
        actions={
          <>
            <button type="button" onClick={() => setOpen(false)}>First</button>
            <button type="button" data-autofocus>Second</button>
            <button type="button">Third {tick}</button>
          </>
        }
      >
        <p>Body</p>
      </Dialog>
    </>
  );
}

describe("Dialog focus", () => {
  it("moves focus in, traps Tab, and returns it to the opener on Escape", () => {
    render(<Harness />);
    const opener = screen.getByRole("button", { name: "Opener" });
    opener.focus();
    fireEvent.click(opener);
    expect(screen.getByRole("button", { name: "Second" })).toHaveFocus();

    screen.getByRole("button", { name: /Third/u }).focus();
    fireEvent.keyDown(document, { key: "Tab" });
    expect(screen.getByRole("button", { name: "First" })).toHaveFocus();
    fireEvent.keyDown(document, { key: "Tab", shiftKey: true });
    expect(screen.getByRole("button", { name: /Third/u })).toHaveFocus();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(opener).toHaveFocus();
  });

  it("keeps the user's place when the caller re-renders with a new onClose", () => {
    const view = render(<Harness tick={0} />);
    fireEvent.click(screen.getByRole("button", { name: "Opener" }));
    const third = screen.getByRole("button", { name: /Third/u });
    third.focus();
    view.rerender(<Harness tick={1} />);
    view.rerender(<Harness tick={2} />);
    expect(screen.getByRole("button", { name: "Third 2" })).toHaveFocus();
  });
});

describe("Dialog focus while its content is still loading", () => {
  it("skips a disabled autofocus button and pulls a stray Tab back inside", () => {
    const view = render(
      <>
        <button type="button">Outside</button>
        <Dialog
          open
          title="Install"
          onClose={() => undefined}
          actions={
            <>
              <button type="button">Cancel</button>
              <button type="button" data-autofocus disabled>Install</button>
            </>
          }
        >
          <p>Looking for your QuadStick</p>
        </Dialog>
      </>,
    );
    const dialog = view.getByRole("dialog");
    expect(dialog.contains(document.activeElement)).toBe(true);

    view.getByRole("button", { name: "Outside" }).focus();
    fireEvent.keyDown(document, { key: "Tab" });
    expect(view.getByRole("button", { name: "Cancel" })).toHaveFocus();
  });
});

describe("Dialog focus when its buttons change while open", () => {
  function Swapping({ done }: { readonly done: boolean }) {
    return (
      <Dialog
        open
        title="Installing"
        onClose={() => undefined}
        actions={
          done ? (
            <button type="button" data-autofocus>Close</button>
          ) : (
            <>
              <button type="button">Cancel</button>
              <button type="button" data-autofocus>Install</button>
            </>
          )
        }
      >
        <p>Body</p>
      </Dialog>
    );
  }

  it("moves focus to the new button when the focused one goes away", () => {
    const view = render(<Swapping done={false} />);
    expect(view.getByRole("button", { name: "Install" })).toHaveFocus();
    view.rerender(<Swapping done />);
    expect(view.getByRole("button", { name: "Close" })).toHaveFocus();
  });

  it("leaves focus alone when it is still somewhere in the panel", () => {
    const view = render(<Swapping done={false} />);
    view.getByRole("button", { name: "Cancel" }).focus();
    view.rerender(<Swapping done={false} />);
    expect(view.getByRole("button", { name: "Cancel" })).toHaveFocus();
  });
});

describe("Dialog focus after a busy spell", () => {
  function Busy({ busy, done }: { readonly busy: boolean; readonly done: boolean }) {
    return (
      <Dialog
        open
        title="Installing"
        onClose={() => undefined}
        actions={
          done ? (
            <button type="button" data-autofocus>Close</button>
          ) : (
            <button type="button" data-autofocus disabled={busy}>Install</button>
          )
        }
      >
        <p>Body</p>
      </Dialog>
    );
  }

  it("lands on the new button once nothing is busy", () => {
    const view = render(<Busy busy={false} done={false} />);
    // A browser drops focus off a button as it turns disabled; jsdom does
    // not, so drop it here first.
    (document.activeElement as HTMLElement).blur();
    view.rerender(<Busy busy done={false} />);
    // Every button is off, so the panel holds focus while the install runs.
    expect(view.getByRole("dialog")).toHaveFocus();
    view.rerender(<Busy busy={false} done />);
    expect(view.getByRole("button", { name: "Close" })).toHaveFocus();
  });
});
