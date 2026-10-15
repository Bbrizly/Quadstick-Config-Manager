import "@testing-library/jest-dom/vitest";

// jsdom reports an unfocused document. The app's window is focused while
// someone uses it, and live input pauses without focus, so tests start focused.
Object.defineProperty(document, "hasFocus", { configurable: true, writable: true, value: () => true });
