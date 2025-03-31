import { defineConfig } from 'vite';
import fable from 'vite-plugin-fable';
import { configDefaults } from 'vitest/config';

// https://nojaf.com/vite-plugin-fable/recipes.html#Using-React
// https://nojaf.com/vite-plugin-fable/recipes.html#Fable-Core-JSX

export default defineConfig({
 // order of plugins matters, fable needs to be first
  plugins: [
    fable({
      fsproj: "./src/App.fsproj"
    }),
  ],
  root: "./src",
  build: {
    outDir: "../dist",
    sourcemap: 'inline'
  },
  define: {
    // required if u have: `process is undefined` 
    // while loading react jsoncomponents
    'process.env': {}
  },
  test: {
    include: ['**/*.{test,spec}.{js,jsx,ts,tsx,fs}'],
    exclude: [...configDefaults.exclude, 'dist', '.idea', '.git', '.cache'],
    environment: 'jsdom',
    setupFiles: '../vitest.ts',
    transform: {
      '^.+\\.fs$': 'vite-plugin-fable'
    }
  }
})