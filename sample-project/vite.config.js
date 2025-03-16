import { defineConfig } from "vite";
import Inspect from "vite-plugin-inspect";
import fable from "vite-plugin-fable";
import react from "@vitejs/plugin-react";

// https://vitejs.dev/config/
export default defineConfig({
  server: {
    port: 4000,
  },
  plugins: [
    Inspect(),
    fable({ jsx: "automatic" }),
    react({ include: /\.fs$/ }),
  ],
});
