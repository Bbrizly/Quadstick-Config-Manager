import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { StrictMode, createRef } from "react";
import { describe, expect, it, vi } from "vitest";

import { MockQcmClient, type EditorSnapshot } from "../platform";
import { EditorHost, type EditorHostHandle } from "../host";

async function hosted(extra: { hostBanner?: string; onInstall?: (p: EditorSnapshot) => void } = {}, strict = false) {
  const client = new MockQcmClient();
  const snapshot: EditorSnapshot = await client.newProfile("untitled.csv");
  const ref = createRef<EditorHostHandle>();
  const onClosed = vi.fn();
  const onDirtyChange = vi.fn();
  const editor = (
    <EditorHost
      ref={ref}
      client={client}
      snapshot={snapshot}
      onClosed={onClosed}
      onDirtyChange={onDirtyChange}
      {...extra}
    />
  );
  const view = render(strict ? <StrictMode>{editor}</StrictMode> : editor);
  await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
  return { client, snapshot, ref, onClosed, onDirtyChange, view };
}

async function edit(): Promise<void> {
  fireEvent.click(screen.getByRole("button", { name: /^Left\. Soft Puff/u }));
  fireEvent.click(await screen.findByRole("button", { name: "Add a new mapping for the Left mouthpiece hole" }));
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
}

function leave(ref: React.RefObject<EditorHostHandle | null>): Promise<boolean> {
  let answer!: Promise<boolean>;
  act(() => {
    answer = ref.current!.confirmLeave();
  });
  return answer;
}

describe("EditorHost (S1)", () => {
  it("shows the given profile without the app's destinations", async () => {
    await hosted();
    expect(screen.queryByRole("navigation")).toBeNull();
    expect(screen.queryByRole("button", { name: /community/iu })).toBeNull();
    expect(screen.getByRole("button", { name: /^Install this profile onto a/u })).toBeInTheDocument();
  });
});

describe("leave guard (S2)", () => {
  it("reports dirty state as it changes", async () => {
    const { onDirtyChange } = await hosted();
    expect(onDirtyChange).toHaveBeenLastCalledWith(false);
    await edit();
    await waitFor(() => expect(onDirtyChange).toHaveBeenLastCalledWith(true));
  });

  it("lets a clean profile go without asking", async () => {
    const { ref, onClosed, client, snapshot } = await hosted();
    await expect(leave(ref)).resolves.toBe(true);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onClosed).toHaveBeenCalledTimes(1);
    await expect(client.getProfileSnapshot(snapshot.sessionId)).rejects.toBeTruthy();
  });

  it("asks with unsaved work; Cancel says no and keeps the edit", async () => {
    const { ref, onClosed } = await hosted();
    await edit();
    const answer = leave(ref);
    const prompt = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(prompt).getByRole("button", { name: "Cancel" }));
    await expect(answer).resolves.toBe(false);
    expect(onClosed).not.toHaveBeenCalled();
    expect(screen.getByLabelText("This profile has unsaved changes. Save them before leaving?")).toBeInTheDocument();
  });

  it("Don't save drops the work and says yes", async () => {
    const { ref, onClosed, client, snapshot } = await hosted();
    await edit();
    const answer = leave(ref);
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Profile" })).getByRole("button", { name: "Don't save" }));
    await expect(answer).resolves.toBe(true);
    expect(onClosed).toHaveBeenCalledTimes(1);
    await expect(client.getProfileSnapshot(snapshot.sessionId)).rejects.toBeTruthy();
  });

  it("Save writes the profile first; backing out of Save As stays", async () => {
    const { ref, onClosed, client } = await hosted();
    await edit();
    const answer = leave(ref);
    const prompt = await screen.findByRole("dialog", { name: "Profile" });
    client.willCancelSaveAs();
    fireEvent.click(within(prompt).getByRole("button", { name: /^Save/u }));
    await waitFor(() => expect(within(prompt).getByRole("button", { name: /^Save/u })).toBeEnabled());
    expect(onClosed).not.toHaveBeenCalled();
    client.willSaveAs("kept.csv");
    const save = vi.spyOn(client, "saveProfileAs");
    fireEvent.click(within(prompt).getByRole("button", { name: /^Save/u }));
    await expect(answer).resolves.toBe(true);
    expect(save).toHaveBeenCalledTimes(1);
    expect(onClosed).toHaveBeenCalledTimes(1);
  });

  it("works under StrictMode", async () => {
    const { ref, onClosed } = await hosted({}, true);
    await edit();
    const answer = leave(ref);
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Profile" })).getByRole("button", { name: "Don't save" }));
    await expect(answer).resolves.toBe(true);
    expect(onClosed).toHaveBeenCalledTimes(1);
  });

  it("a host that unmounts while the prompt waits gets a no", async () => {
    const { ref, view, onClosed } = await hosted();
    await edit();
    const answer = leave(ref);
    await screen.findByRole("dialog", { name: "Profile" });
    view.unmount();
    await expect(answer).resolves.toBe(false);
    expect(onClosed).not.toHaveBeenCalled();
  });

  it("a second ask while the prompt is up is the same question", async () => {
    const { ref } = await hosted();
    await edit();
    const first = leave(ref);
    await screen.findByRole("dialog", { name: "Profile" });
    const second = leave(ref);
    expect(second).toBe(first);
    fireEvent.click(within(screen.getByRole("dialog", { name: "Profile" })).getByRole("button", { name: "Cancel" }));
    await expect(first).resolves.toBe(false);
  });
});

describe("host banner (S3)", () => {
  it("shows the host's line in the editor header", async () => {
    await hosted({ hostBanner: "Editing for someone else" });
    expect(screen.getByTestId("editor-host-banner")).toHaveTextContent("Editing for someone else");
  });

  it("shows nothing when left out", async () => {
    await hosted();
    expect(screen.queryByTestId("editor-host-banner")).toBeNull();
  });
});

describe("host install", () => {
  it("hands Install to QuadStick to the host, with the profile as it is now", async () => {
    const onInstall = vi.fn();
    const { snapshot } = await hosted({ onInstall });
    fireEvent.click(screen.getByText("Install to QuadStick"));
    expect(onInstall).toHaveBeenCalledWith(expect.objectContaining({ sessionId: snapshot.sessionId }));
    expect(screen.queryByRole("dialog", { name: "Installing profile" })).toBeNull();
  });
});
