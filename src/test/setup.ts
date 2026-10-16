import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";

// findBy and waitFor give up after 1 s by default, which a loaded machine
// overruns while the app is still doing the right thing. A wait that never
// succeeds still fails, just later.
configure({ asyncUtilTimeout: 10000 });

// jsdom reports an unfocused document. The app's window is focused while
// someone uses it, and live input pauses without focus, so tests start focused.
Object.defineProperty(document, "hasFocus", { configurable: true, writable: true, value: () => true });
