/// <reference types="vitest/config" />
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// In development the API runs on :5080; proxying keeps the app same-origin, as it is behind nginx.
const apiOrigin = process.env.MARKETLENS_API_ORIGIN ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // Built files go to /static/ so they never collide with the app's own /assets/<key> pages.
  build: { assetsDir: "static" },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: apiOrigin, changeOrigin: false },
      "/hubs": { target: apiOrigin, changeOrigin: false, ws: true },
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
