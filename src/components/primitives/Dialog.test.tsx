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
