import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./app/App";
import { resolveQcmClient } from "./platform";
import "./styles/app.css";

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
