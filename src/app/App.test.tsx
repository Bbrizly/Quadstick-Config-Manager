import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { App } from "./App";

describe("App", () => {
  it("renders a named landmark so a screen reader has somewhere to land", () => {
    render(<App />);
    expect(screen.getByRole("main")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "QuadStick Config Manager",
    );
  });

  it("says what state the shell is in rather than showing an empty window", () => {
    render(<App />);
    expect(screen.getByTestId("boot-state")).toHaveTextContent(
      "No profile is loaded",
    );
  });
});
