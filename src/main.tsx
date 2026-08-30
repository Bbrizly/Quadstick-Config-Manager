import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./app/App";
import "./styles/app.css";

const container = document.getElementById("root");
if (container === null) {
  throw new Error("index.html is missing the #root container");
}

// StrictMode is permanent, not a dev convenience: every live-input subscription
// added later has to survive effects running twice.
createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
