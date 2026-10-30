import { readFileSync } from "node:fs";
import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The app version is kept in the root package.json (see tools/release/version.mjs).
const rootPackage = JSON.parse(
  readFileSync(path.resolve(import.meta.dirname, "../../package.json"), "utf8"),
) as { version: string };

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(rootPackage.version),
  },
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "src"),
    },
  },
  // Tauri expects a fixed port and its own error output.
  server: {
    port: 5173,
    strictPort: true,
  },
  clearScreen: false,
  test: {
    environment: "node",
    setupFiles: ["src/test/setup.ts"],
  },
});
