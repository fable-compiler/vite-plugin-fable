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
  // The two React-related options do different jobs:
  //
  // `fable({ jsx })` is what turns JSX into JavaScript. It has to be: Vite's own `vite:oxc` forces
  // `lang: "js"` for a non-JavaScript extension, which disables JSX parsing, so JSX left in a `.fs`
  // module is a parse error there rather than something Vite can pick up.
  //
  // `react({ include: /\.fs$/ })` does nothing for JSX. It widens plugin-react's Fast Refresh
  // filter so `.fs` components become refresh boundaries and update in place instead of reloading
  // the page. Drop it and the app still works — it just reloads on every component edit, which is
  // why the plugin warns about it.
  //
  // DevTools is the interactive view: run `bun run dev` and open the app, then `#devframe`.
  // Inspect is the headless one: `build: true` writes `.vite-inspect/reports/` on `bun run build`,
  // where each module's `vite-plugin-fable` step holds the emitted JavaScript as plain JSON —
  // readable without a browser, which is what makes it useful in a terminal or CI.
  plugins: [
    DevTools(),
    Inspect({ build: true }),
    fable({ jsx: "automatic" }),
    react({ include: /\.fs$/ }),
  ],
});
