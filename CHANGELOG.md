# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) from version [0.1.0] moving forward.

## [Unreleased]

### Changed

- The plugin is written in TypeScript. `index.js` and `types.d.ts` became `index.ts` and `types.ts`, compiled to `dist/` by `tsc` during `prepublishOnly`; the package now ships type declarations. Behaviour is unchanged.
- `bun install` at the repo root now builds the daemon and the plugin (`bun run build`), so a fresh clone works without a manual build step. The plugin's own `postinstall` is now only the consumer hook and delegates to `build:daemon`.
- Fable.Daemon now targets `net10.0`; the .NET 10 runtime is required.
- Updated Fable.Compiler to 5.14.1 and `@fable-org/fable-library-js` to 2.5.1.
- Project cracking now delegates to Fable's own `MSBuildCrackerResolver`. The design time build cache and watched MSBuild files are kept in `ProjectCracking.fs`; `CoolCatCracking.fs` was removed.
- `cracking.fsx` is executable and accepts an fsproj path: `./cracking.fsx path/to/Project.fsproj`.
- MSBuild output is decoded with `System.Text.Json`; the `Thoth.Json.*` dependencies were removed.
- Sample project targets `net10.0`.
- GitHub Actions updated to their latest major versions and the workflows read the SDK version from `global.json`.
- dotnet tools updated: Fantomas 8.0.0-alpha-025, fsdocs-tool 22.1.0, dotnet-outdated-tool 4.8.1. Sources reformatted with Fantomas 8 and CI now runs `dotnet fantomas check .`.
- Vite 8 is the peer dependency. The JSX transform after Fable compilation uses Vite's `transformWithOxc` (rolldown/oxc); `esbuild` is no longer a peer dependency.
- All JavaScript dependencies updated (`@babel/code-frame` 8, `ts-lsp-client` 1.1.1, TypeScript 7, React 19 and `@vitejs/plugin-react` 6 in the sample).
- The repository is a Bun workspace with isolated installs: the plugin lives in `packages/vite-plugin-fable` and `sample-project` depends on it via `workspace:*`. `Directory.*.props` stay at the repo root and are copied into the package by `prepublishOnly`; the changelog updater lives in `scripts/`. Shared versions (`vite`, `vite-plugin-inspect`, `@fable-org/fable-library-js`) are declared once in the root workspace catalog and referenced as `catalog:`.
- `fable-library` is located with `import.meta.resolve` instead of guessing `node_modules` paths.
- Prettier replaced by oxfmt (`bun run format`, `bun run format:check`).

### Fixed

- `vite build` now fails when F# does. A cracking or compile failure, or any error-severity diagnostic, aborts the build instead of logging and exiting 0 with broken output; an F# file Fable never compiled is reported rather than handed to the JavaScript parser as raw F#. `vite dev` is unchanged: the server stays up so the browser overlay can show the diagnostic.
- Plugin errors are logged through Vite's `logger.error` rather than `logger.warn`.
- oxlint runs over the repository, with `@nojaf/oxlint-plugin-annotate-non-primitives` requiring an explicit type annotation wherever the type is not obvious from the initializer. Every `lint` script runs oxlint before the TypeScript checks, and CI runs them on every PR.
- `scripts/changelog-updater.js` is now TypeScript and type-checked by `tsconfig.scripts.json`. `docs/scripts/command.js` stays JavaScript: the docs pages load it directly through an import map with no bundler, so porting it would mean adding a build step to a pipeline that has none.
- The plugin package is laid out as `src/` and `tests/`, with `bun test` covering the Vite hooks against a stub daemon. `bun run test:plugin` runs them; CI runs them on every PR.
- The daemon lives behind a `FableDaemon` interface in its own module. Process lifetime, the JSON-RPC endpoint and the positional wire format no longer leak into the plugin, which can now be run against a stub daemon in tests.
- The daemon process is spawned without a shell, so a `dotnet` that is not on `PATH` now fails immediately with an actionable message instead of hanging the dev server forever. Its stderr is drained (an undrained pipe would deadlock the daemon once the buffer filled), requests fail fast if the daemon exits, and the daemon is also stopped on `SIGINT`.
- Removed a leftover `console.log` that printed the whole HMR error payload to the terminal.
- `dotnet msbuild` invocations for the cache key no longer give up after 5 seconds and now fail on a non-zero exit code.

## [0.2.1] - 2025-10-23

### Changed

- Removed caret range from ts-lsp-client to use version 1.0.4, pr ([#59](https://github.com/fable-compiler/vite-plugin-fable/pull/59)), targets issue ([#58](https://github.com/fable-compiler/vite-plugin-fable/issues/58))

## [0.2.0] - 2025-06-24

### Changed

- Upgrade to Vite 7.0.0 as peer dependency and adjust transform hook to use latest filter property, pr ([#49](https://github.com/fable-compiler/vite-plugin-fable/pull/49)), targets issue ([#39](<[#39](https://github.com/fable-compiler/vite-plugin-fable/issues/39)>)

## [0.1.1] - 2025-06-03

### Fixed

- Support `major` roll forward dotnet versions of runtime for Fable.Daemon, pr ([#46](https://github.com/fable-compiler/vite-plugin-fable/pull/46)), targets issue ([#44](https://github.com/fable-compiler/vite-plugin-fable/issues/44))

## [0.1.0] - 2025-05-22

### Changed

- bumping version and package release for changelog sync

## [0.0.37] - 2025-05-10

### Changed

- Fable.Compiler updated to 5.0.0-alpha.13 ([#38](https://github.com/fable-compiler/vite-plugin-fable/pull/38))
- Added caret range to fable-library-js ([#38](https://github.com/fable-compiler/vite-plugin-fable/pull/38))
- Updated fable-library-js to 2.0.0-beta.3 ([#38](https://github.com/fable-compiler/vite-plugin-fable/pull/38))

## [0.0.36] - 2025-05-07

### Changed

- Updated fable-library-js to ^2.0.0-beta.3 ([#38](https://github.com/fable-compiler/vite-plugin-fable/pull/38))

## [0.0.35] - 2025-04-30

### Added

- Added README for Fable Daemon ([#34](https://github.com/fable-compiler/vite-plugin-fable/pull/34))

## [0.0.34] - 2025-04-20

### Changed

- Upgrade to latest Fable.Compiler and Fable.AST ([#13](https://github.com/fable-compiler/vite-plugin-fable/pull/13))

## [0.0.33] - 2025-04-10

### Changed

- Update node dependencies and bump version

## [0.0.32] - 2025-03-30

### Fixed

- Fix Thoth.Json usage ([#23](https://github.com/fable-compiler/vite-plugin-fable/pull/23))

## [0.0.31] - 2025-03-15

### Added

- Error overlay for development ([#8](https://github.com/fable-compiler/vite-plugin-fable/pull/8))

## [0.0.30] - 2025-03-01

### Added

- Vite 6 support ([#11](https://github.com/fable-compiler/vite-plugin-fable/pull/11))

## [0.0.29] - 2025-02-20

### Changed

- Improved diagnostics ([#3](https://github.com/fable-compiler/vite-plugin-fable/pull/3))

## [0.0.28] - 2025-02-10

### Added

- Debug viewer ([#5](https://github.com/fable-compiler/vite-plugin-fable/pull/5))

## [0.0.27] - 2025-01-30

### Changed

- Combine file changes for faster rebuilds ([#6](https://github.com/fable-compiler/vite-plugin-fable/pull/6))

## [0.0.26] - 2025-01-15

### Added

- Project options cache ([#2](https://github.com/fable-compiler/vite-plugin-fable/pull/2))

## [0.0.25] - 2025-01-05

### Added

- Support for arm64 architecture in postinstall ([#1](https://github.com/fable-compiler/vite-plugin-fable/pull/1))

## [0.0.24] - 2024-03-02

### Changed

- Improved endpoint call control via shared pending changes subscription.
- Various internal improvements and bug fixes.

## [0.0.22] - 2024-02-28

### Changed

- Update Fable.Compiler to 4.0.0-alpha-008.
- Update TypeScript and include debug folder.

## [0.0.20] - 2024-02-26

### Changed

- Handle F# changes via handleHotUpdate callback.
- Improved file change tracking and project cache key logic.

## [0.0.18] - 2024-02-25

### Changed

- Only send sourceFiles list of FSharpProjectOptions to plugin.
- Additional logging and reuse of CrackerOptions.

## [0.0.16] - 2024-02-24

### Added

- Debug documentation and error overlay prototype.
- Initial debug page setup.

## [0.0.7] - 2024-02-13

### Added

- Diagnostics support ([#3](https://github.com/fable-compiler/vite-plugin-fable/pull/3)).
- Use @fable-org/fable-library-js.

## [0.0.3] - 2024-02-05

### Added

- Thoth.Json support.
- Initial cache key setup for project configuration.
- Initial caching for design time build.

## [0.0.1] - 2023-10-28

### Added

- Initial implementation of Vite plugin for Fable.
- Basic F# file compilation and integration with Vite build.
- Early support for project file watching and hot reload.
- Initial project setup, configuration, and documentation.
