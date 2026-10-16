import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./app/App";
import { resolveQcmClient } from "./platform";
import "./styles/app.css";

// Line height follows the platform face (app.css): Avalonia steps a Mac's
// Helvetica by its ascent plus descent, tighter than the browser's "normal".
if (/Mac/u.test(navigator.platform)) document.documentElement.dataset["os"] = "mac";

const container = document.getElementById("root");
if (container === null) {
  throw new Error("index.html is missing the #root container");
}

async function mount(): Promise<void> {
  const client = await resolveQcmClient();
  createRoot(container as HTMLElement).render(
    <StrictMode>
      <App client={client} />
    </StrictMode>,
  );
}

void mount();
