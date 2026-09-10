import react from "@vitejs/plugin-react";
// vitest/config, not vite: the `test` block is not part of Vite's own schema.
import { defineConfig } from "vitest/config";

// The repo root still holds the .NET app, the fixtures and the Rust target
// directory, so the dev watcher is told to stay out of them. Without this the
// watcher walks target/ and obj/ and eats the file handle budget.
const ignored = [
  "**/.git/**",
  "**/node_modules/**",
  "**/target/**",
  "**/bin/**",
  "**/obj/**",
  "**/dist/**",
  "**/dist-ui/**",
  "**/src-tauri/**",
];

export default defineConfig({
  plugins: [react()],
  // dist/ is where make package puts the .NET installers. The UI build gets its
  // own directory so the two never overwrite each other.
  build: {
    outDir: "dist-ui",
    emptyOutDir: true,
    target: "es2023",
    sourcemap: true,
  },
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    watch: { ignored },
  },
  // TAURI_ENV_* is set by the Tauri CLI, so the frontend can read the platform
  // it was built for without a plugin.
  envPrefix: ["VITE_", "TAURI_ENV_"],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["src/test/setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    // css is stubbed to "" by default, which silently empties a `?raw`
    // import too. Only the raw ones are let through, so no stylesheet is
    // ever injected into jsdom.
    css: { include: [/\?raw/] },
  },
});
