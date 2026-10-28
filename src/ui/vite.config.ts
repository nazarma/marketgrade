import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
