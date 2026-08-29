import { defineConfig } from "vite";
import { DevTools } from "@vitejs/devtools";
import Inspect from "vite-plugin-inspect";
import fable from "vite-plugin-fable";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  server: {
    port: 4000,
  },
  // `react({ include: /\.fs$/ })` does not make JSX work — vite-plugin-fable runs the JSX
  // transform itself. It widens plugin-react's Fast Refresh filter so `.fs` components become
  // refresh boundaries and hot update in place instead of reloading the page.
  //
  // DevTools is the interactive view: run `bun run dev` and open the app, then `#devframe`.
  // Inspect is the headless one: `build: true` writes `.vite-inspect/reports/` on `bun run build`,
  // where each module has its `__load__` (F# source) and `vite-plugin-fable` (emitted JS) steps as
  // plain JSON — readable without a browser, which is what makes it useful in a terminal or CI.
  plugins: [
    DevTools(),
    Inspect({ build: true }),
    fable({ jsx: "automatic" }),
    react({ include: /\.fs$/ }),
  ],
});
