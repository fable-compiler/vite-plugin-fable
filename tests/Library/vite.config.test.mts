import { defineConfig } from 'vitest/config';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import fable from 'vite-plugin-fable';

console.log("Loaded tests/Library/vite.config.test.mts");

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fsproj = path.join(currentDir, './tests/Library.Tests.fsproj'); // Path for testing

export default defineConfig({
  plugins: [
    fable({ fsproj }), // Adjusted path for tests
  ],
  test: {
    globals: true,
    include: ['**/*.test.ts', '**/*.test.js', '**/*.test.fs'],
  },
});
