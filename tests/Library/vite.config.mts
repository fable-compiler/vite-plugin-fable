import { defineConfig } from 'vite'
import path from "node:path";
import { fileURLToPath } from "node:url";
import fable from "vite-plugin-fable";
import dts from 'vite-plugin-dts';

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fsproj = path.join(currentDir, "src/Library/Library.fsproj");

export default defineConfig({
  plugins: [
    fable({fsproj}),
    dts()
  ],
  build: {
    lib: {
      name: 'vite-plugin-fable-library-example',
      entry: ['src/Library/index.fs'],
      fileName: (format, entryName) => `vite-plugin-fable-library-example-${entryName}.${format}.js`
    },
  },
})