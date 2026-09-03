# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) from version [0.1.0] moving forward.

## [Unreleased]

### Fixed

- An F# file that type-checks but that Fable cannot translate now fails `vite build` instead of passing for a successful compile. `Fable.Compiler` filled a compile's diagnostics from the F# type-check only and discarded Fable's own logs, so a call like `Async.RunSynchronously` produced a module that does nothing while the build printed nothing and exited 0, and the app broke in the browser. Fixed upstream in [fable-compiler/Fable#4923](https://github.com/fable-compiler/Fable/pull/4923), which this release picks up: the daemon now reports what Fable said alongside the F# diagnostics, for the first compile of the project as well as for every recompile after an edit, so these errors reach the terminal, the browser overlay and `/api/diagnostics`. They carry no error number, so they are named by their tag — `ERROR FABLE:` where an F# diagnostic reads `ERROR FS0025:`. Diagnostics on files under `fable_modules` are still dropped unless `fableModulesDiagnostics` is on.

### Changed

- Updated Fable.Compiler to 5.15.0 and `@fable-org/fable-library-js` to 2.6.0.

## [0.3.1] - 2026-08-29

### Changed

- The README is a getting started guide: requirements, install, the minimal `vite.config.js`, how to import an F# entry point from `index.html`, and the React setup, with everything else linked to the documentation site. It used to describe the package as a name reservation on npm and point readers at the source. The plugin options are linked rather than repeated, so the table has one home.

### Fixed

- The package has a `description` and `keywords`. Both were empty, so npm derived the description from the first line of the README and listed the version badge's Markdown as the package summary.

## [0.3.0] - 2026-08-29

### Changed

- The plugin is written in TypeScript. `index.js` and `types.d.ts` became `index.ts` and `types.ts`, compiled to `dist/` by `tsc` during `prepack`; the package now ships type declarations. Behaviour is unchanged.
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
- The repository is a Bun workspace with isolated installs: the plugin lives in `packages/vite-plugin-fable` and `sample-project` depends on it via `workspace:*`. `Directory.*.props` stay at the repo root, which is where the daemon is built from, and the release scripts live in `scripts/`. Shared versions (`vite`, `vite-plugin-inspect`, `@fable-org/fable-library-js`) are declared once in the root workspace catalog and referenced as `catalog:`.
- `vite-plugin-fable` ships the daemon prebuilt and no longer has a `postinstall` script. It used to run `dotnet publish` on the consumer's machine, which package managers increasingly refuse to run by default (bun only for `trustedDependencies`, pnpm behind `onlyBuiltDependencies`, `npm --ignore-scripts` in CI); when it was skipped the install still succeeded, no `bin/` was produced, and the failure surfaced much later as a confusing `buildStart` error. The package now carries a framework-dependent publish of `Fable.Daemon` that runs anywhere the .NET runtime does, so `--ignore-scripts` installs work and nothing is compiled at install time. The .NET 10 SDK is still required, because reading a `.fsproj` means asking `dotnet msbuild` about it. The published bits are portable IL rather than the ReadyToRun build the old `postinstall` produced, which costs roughly 0.7s of JIT per `vite dev` start and per `vite build`. A missing daemon assembly now fails with a message naming it instead of `dotnet`'s own error arriving behind "the daemon stopped unexpectedly". `cracking.fsx` still ships, and works against the bundled `bin/` without anything being built first.
- The daemon's debug server answers JSON under `/api`, so what it cracked, compiled, cached and served can be read by anything that is not a browser: `/api/status`, `/api/project`, `/api/files` (including the JavaScript emitted for one file), `/api/diagnostics` (unfiltered), `/api/cache` (whether the design time build was reused and which input invalidated it), `/api/requests` and `/api/logs`. Read-only, served from a snapshot the message loop publishes so it never queues behind a compile, and every response carries a revision that increments per served request. The log viewer page is unchanged. A running daemon writes `$TMPDIR/vite-plugin-fable/daemon-<pid>.json`, and `VITE_PLUGIN_FABLE_DEBUG_PORT` moves it off 9014 when two dev servers would collide.
- The `debug` plugin option starts the daemon's debug server too. It used to be plugin-side only, so the daemon's own output could only be reached by also setting `VITE_PLUGIN_FABLE_DEBUG`.
- Diagnostics for files under `fable_modules` are no longer reported. They are about the sources Fable restored for the packages a project depends on, which nobody using the plugin wrote or can edit. The new `fableModulesDiagnostics` option reports them again, including errors, which is worth turning on when a package itself is what looks broken.
- `fable-library` is located with `import.meta.resolve` instead of guessing `node_modules` paths.
- Prettier replaced by oxfmt (`bun run format`, `bun run format:check`).
- Releases are cut from CHANGELOG.md. A push to `main` whose changelog names a version that is not on npm dispatches `release.yml`, which publishes the package and creates the GitHub release for it; both steps are skipped when they already happened, so a run that failed halfway can be dispatched again by hand. npm trusted publishing (OIDC) authenticates the upload instead of a long-lived token, which is the one thing here that is not bun: `bun publish` cannot use the workflow's OIDC token yet ([oven-sh/bun#22423](https://github.com/oven-sh/bun/issues/22423)), so `npm publish` uploads a tarball that `bun pm pack` produced. Packing with bun is what resolves the `catalog:` ranges, which npm would otherwise publish verbatim for consumers to choke on. The published version is taken from the changelog at release time rather than committed to `package.json`, the package's `prepublishOnly` became a `prepack` (what `bun pm pack` runs), and it gained the `repository` field that provenance requires and the README and LICENSE that only get published when they sit next to the manifest. `scripts/changelog-updater.ts` is gone, replaced by `scripts/release-detect.ts` and `scripts/release-publish.ts`; it handed the changelog CLI an absolute path that the CLI joined onto its own working directory, so `prepublishOnly` could not have worked.

### Fixed

- Plugin options are validated when the config loads. An unknown or badly typed option now fails with a message naming it (and suggesting the intended one) instead of being merged in and ignored, which in a `vite.config.js` was invisible.
- The package exports its `PluginOptions` and `FableConfiguration` types and declares an `exports` map, so a `vite.config.ts` can name what it passes in. The internal test seam no longer appears in the published type surface.
- All plugin options are documented, including `noReflection` and `exclude`, which were never mentioned anywhere.
- Changing the `noReflection` or `exclude` plugin options invalidates the caches. Both change what Fable emits but neither was part of the design time build cache key, so the previous build was reused and stale JavaScript was served with nothing to indicate it. The cached data now carries a format version too, so caches written before this fix are discarded rather than compared against fields they never stored.
- The MSBuild configuration follows the Vite command rather than `env.MODE`, so `vite build --mode staging` no longer compiles Debug F# into a production bundle. A new `configuration` plugin option overrides it, defaulting to `Release` for `vite build` and `Debug` for `vite dev`.
- The `transform` hook reports `map: { mappings: "" }` instead of `map: null`. `null` claims the previous source mapping still applies, which made later stages emit a map labelling the compiled JavaScript as the contents of a `.fs` file — devtools showed an F# filename containing JavaScript. Real F#-to-JS source maps remain blocked on Fable.
- Compiled output is now keyed off what the daemon returned rather than looked up per source file. The two sets differ (signature files are never compiled), and indexing the daemon's map with an already-normalised path would have yielded `undefined` for every entry had the daemon ever reported a non-POSIX path.
- CI runs the daemon test suite; previously only the plugin tests ran.
- TypeScript `strict` is on. That surfaced a real bug: `configResolved` derived the project directory from `resolvedConfig.configFile`, which is optional, so a project without a Vite config file (or one created programmatically) reached `fs.readdir(undefined)`. It now uses `resolvedConfig.root`, which is always resolved and is also the correct directory when `root` differs from the config file's location. A missing `.fsproj` is now an error rather than a `null` handed to the daemon.
- oxlint warnings fail the lint instead of being reported and ignored.
- `sample-project` runs its scripts on the Bun runtime through its own `bunfig.toml` rather than `bunx --bun` in each script, so the scripts are plain `vite`, `vite build` and `vite preview`. Bun only reads the `bunfig.toml` in the directory a command starts from, so the one at the repo root does not cover it.
- The sample project gained a `Greeting.fsi` / `Greeting.fs` pair whose output is rendered into the page heading, so signature-file behaviour can be exercised by hand: editing the implementation updates the heading in place, and breaking the signature surfaces the error against the implementation.
- Hot updates use Vite's `hotUpdate` hook instead of the deprecated `handleHotUpdate`, which also means created and deleted files now reach the plugin — `handleHotUpdate` only ever fired for updates.
- Fixed a hot-update race. Every in-flight change shared one promise, so a file edited while another was compiling was answered by the previous compile's diagnostics and pushed to the browser before it had been compiled at all; its own diagnostics were then discarded. Changes are now coalesced into batches that each carry their own result.
- Editing a signature file (`.fsi`) recompiles the implementation it describes. Previously nothing happened at all until an implementation file was touched.
- Editing an `.fsproj` or other MSBuild input now reloads the browser after the project is re-cracked, rather than re-cracking silently and leaving stale modules loaded.
- A changed F# file that nothing imports now triggers a reload instead of being silently ignored, and every module whose compiled output actually changed is invalidated rather than only the edited file. Files Fable recompiled without changing their output are left out: they are usually downstream modules that cannot accept a hot update, and one of those turns the whole update into a page reload. Editing an F# React component now hot-updates through Fast Refresh instead of reloading the page.
- `rxjs` and `promise.withresolvers` are no longer dependencies; the coalescing is a small queue.
- `vite build` now fails when F# does. A cracking or compile failure, or any error-severity diagnostic, aborts the build instead of logging and exiting 0 with broken output; an F# file Fable never compiled is reported rather than handed to the JavaScript parser as raw F#. `vite dev` is unchanged: the server stays up so the browser overlay can show the diagnostic.
- Plugin errors are logged through Vite's `logger.error` rather than `logger.warn`.
- oxlint runs over the repository, with `@nojaf/oxlint-plugin-annotate-non-primitives` requiring an explicit type annotation wherever the type is not obvious from the initializer. Every `lint` script runs oxlint before the TypeScript checks, and CI runs them on every PR.
- The scripts in `scripts/` are TypeScript and type-checked by `tsconfig.scripts.json`. `docs/scripts/command.js` stays JavaScript: the docs pages load it directly through an import map with no bundler, so porting it would mean adding a build step to a pipeline that has none.
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
