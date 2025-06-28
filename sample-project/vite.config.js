import { defineConfig } from "vite";
import Inspect from "vite-plugin-inspect";
import fable from "../index.js";
import react from "@vitejs/plugin-react";

// https://vitejs.dev/config/
export default defineConfig({
  server: {
    port: 4000,
  },
  plugins: [
    Inspect(),
    fable(),
    react({ include: /\.fs$/, jsxRuntime: "classic" }),
  ],
});
