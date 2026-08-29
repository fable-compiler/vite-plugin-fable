# Roadmap / action items

Captured on 2026-08-29 after a scan of the code base, trimmed as items land. Each item has enough context to be picked up independently.

Items 1-6 were verified against the Vite 8.2.2 source (`~/Projects/vite`, the version pinned in the workspace catalog) and Fable 5.14 (`~/Projects/Fable`); the `packages/vite/src/node` references below point into that checkout.

Order is a rough suggestion: 1-3 are confirmed bugs, 4-7 are smaller contract fixes and design questions, 8-10 are larger. Item 8 (TypeScript) is worth doing before 1, 3 and 4 — see the note there. Item 11 records a decision, not work.

## 1. Migrate `handleHotUpdate` → `hotUpdate` and fix the HMR pipeline

The single biggest cluster. `handleHotUpdate` is on Vite's future-deprecation list (`removePluginHookHandleHotUpdate`, `src/node/deprecations.ts:22`, warning emitted at `src/node/server/hmr.ts:546`) and it is functionally narrower: `hmr.ts:545` only dispatches it for `type === 'update'`, so **created and deleted `.fs` files never reach the plugin**. `hotUpdate` receives all three and gives `this.environment.hot`, which also settles the `server.hot` deprecation (`removeServerHot`, `deprecations.ts:31`).

Everything below wants to be fixed in the same pass.

**a. The shared HMR promise mixes up unrelated compile batches** (`index.js:416-419`, `475-481`)

There is one `state.hotPromiseWithResolvers` for all in-flight changes, but a compile is not instant. Edit `A.fs`: the buffer flushes at t=50ms, the compile runs to t=2s. Edit `B.fs` at t=100ms: `handleHotUpdate(B)` finds a non-null promise and awaits _A's_, which resolves at t=2s carrying _A's_ diagnostics. `B` is then reported as updatable and pushed to the browser before its own compile has written to `compilableFiles`, so the client refetches `B` and gets the previous code. B's own batch finishes afterwards, finds `hotPromiseWithResolvers === null`, and drops its diagnostics — errors in `B` are silently lost.

The promise has to be per-batch, keyed to the files that actually went into that compile.

**b. Returning an empty array disables HMR for the entry module** (`index.js:493`)

`modules.filter(m => m.importers.size !== 0)` returns `[]` for the root `.fs` (the one the `<script>` in `index.html` pulls in — the HTML is not an importer in the module graph). Confirmed at `hmr.ts:632-652`: an empty module list logs `[no modules matched]` and sends **nothing**; full-reload is only sent for `.html`. So editing the entry F# file does nothing at all. Returning `undefined` lets Vite propagate, hit a dead end (Fable output never calls `import.meta.hot.accept`) and full-reload correctly.

**c. An fsproj change never reaches the browser** (`index.js:461-465`)

`watchChange` fires-and-forgets into the subject. Vite awaits `watchChange` and then calls `handleHMRUpdate` (`server/index.ts:945-956`), but the fsproj has no modules in the graph and `handleHotUpdate` bails because `compilableFiles.has(fsproj)` is false. The re-crack happens in the background and nothing invalidates the module graph. Needs an explicit full-reload (or `environment.moduleGraph.invalidateAll()`) once `projectChanged` resolves.

**d. Signature files (`.fsi`) are not handled**

Editing a `.fsi` does nothing today:

1. `compileProject` calls `addWatchFile` for every source file including `.fsi`, so Vite does see the change.
2. `watchChange` only forwards files in `state.dependentFiles` (MSBuild files), so the `.fsi` is ignored there.
3. `handleHotUpdate` only forwards files in `state.compilableFiles`, and `tryCompileProject` (`Program.fs`) filters out `.fsi` files, so a signature file is never a key.
4. No recompile is requested, no diagnostic is shown, and the browser keeps stale output until an implementation file is touched.

The daemon already anticipates this: `tryCompileFiles` has `mapLeadingFile`, which maps `Foo.fs` to `Foo.fsi` before asking `InteractiveChecker.GetDependentFiles`, so once the plugin forwards the change the dependency walk works.

**e. Downstream modules are not invalidated**

When a dependent file recompiles several F# files, only the originally changed module is in `modules`, so downstream files whose _output_ changed are never invalidated. Verify with a change in `Math.fs` that alters the output of `Library.fs`.

**To do**

- [ ] Replace `handleHotUpdate` with `hotUpdate`; use `this.environment.hot` instead of `server.hot`.
- [ ] Make the pending-change coalescing per-batch. Since this is a rewrite anyway, the RxJS `Subject` + `bufferTime(50)` + `Promise.withResolvers` dance can become a small queue with one in-flight promise, dropping `rxjs` and the `promise.withresolvers` shim (Node 22 has `Promise.withResolvers` natively).
- [ ] Handle `create` and `delete`, not just `update`.
- [ ] Treat any file in `state.sourceFiles` as an F# change, not only keys of `compilableFiles`; map a changed `Foo.fsi` to its `Foo.fs` for the module lookup (the browser imports the `.fs`).
- [ ] Return `undefined` rather than `[]` when there is nothing to filter, so entry modules full-reload.
- [ ] Trigger a full reload after a project re-crack.
- [ ] Return every module whose compiled output actually changed, not just the edited one.
- [ ] Add a test in `Fable.Daemon.Tests` that changes `sample-project/Component.fsi` (e.g. removes the `Component` val) and asserts a diagnostic on `Component.fs`.
- [ ] Update `types.d.ts`: `import('vite').HMRPayload` is deprecated in favour of `HotPayload` (`vite/types/hmrPayload.d.ts:1`).

## 2. Harden the daemon process handling

**a. stderr is piped and never drained** (`index.js:384-387`). `stdio: "pipe"` with no `.stderr` reader: once the daemon writes ~64KB to stderr it blocks on the write, forever.

**b. No `error` or `exit` handler on the child.** If `dotnet` is not on the path the shell exits 127, the JSON-RPC call never settles, and `buildStart` hangs. `server/index.ts:1104` awaits `buildStart` _before_ `httpServer.listen`, so the dev server never prints a URL and never errors — this is the mechanism behind the "no clear error when dotnet is missing" symptom.

**c. `shell: true` is wrong here** (`index.js:384`). The daemon path is `path.join(currentDir, "bin/Fable.Daemon.dll")`; one space anywhere in the install path and the shell splits it into two arguments. `state.dotnetProcess.kill()` (`index.js:500`) kills the shell rather than the `dotnet` child, orphaning the daemon on Windows. There is no reason for a shell.

**d. SIGINT.** `buildEnd` _does_ run on a graceful dev shutdown (`server.close()` → `environment.close()` → `pluginContainer.close()` → `buildEnd`, `server/index.ts:643-645`, `pluginContainer.ts:637-650`), so this is not the leak it was thought to be. What is missing is SIGINT — Vite only installs a SIGTERM handler.

**To do**

- [ ] Drop `shell: true`; pass the dll path as an argv entry.
- [ ] Drain and log `stderr`; attach `error` and `exit` handlers that reject the pending RPC with an actionable message ("the .NET 10 runtime was not found on PATH").
- [ ] Kill the daemon on SIGINT as well.

## 3. `vite build` succeeds when F# fails

`projectChanged` catches everything (`index.js:254-259`) and `buildStart` catches again (`index.js:430-432`), so a broken project logs and exits 0. `logError` routes through `logger.warn` (`index.js:97`), so Vite never sets its error flag either. `transform` then falls into the `else` at `index.js:456`, returns `undefined`, and Vite hands the raw F# source to the JS parser — the user sees a syntax error pointing at `module Foo` instead of the actual compile error.

**To do**

- [ ] In build mode, call `this.error(...)` on a failed crack/compile and on any `Error` severity diagnostic.
- [ ] Make `logError` use `logger.error`; keep `logger.warn` for warnings.
- [ ] Make the `transform` fallback for an uncompiled `.fs` an explicit error instead of passing F# through to the JS parser.

## 4. Hook-contract fixes

- **`map: null` is the wrong signal** (`index.js:454`). In the Rollup/Vite contract `null` means "I did not move code, keep the previous map"; the transform replaces F# with JS. The correct value is `{ mappings: '' }` — what Vite's own plugins use for this case (`plugins/css.ts:583`, `plugins/asset.ts:247`).
  Note: real F# source maps are **blocked upstream**. `FileWriter.AddSourceMapping` in `~/Projects/Fable/src/Fable.Compiler/Library.fs:84-90` is a no-op with the `SourceMapSharp` generator commented out; `CliArgs.SourceMaps` exists but does nothing. Needs a Fable PR first.
- **No `load` hook.** Vite reads the whole `.fs` file off disk purely so `transform` can discard it. A `load` for ids in `compilableFiles` skips the I/O and states the intent. Return `moduleType: 'js'` too — `vite:oxc` does (`plugins/oxc.ts:330`) — otherwise rolldown infers the type from the `.fs` extension.
- **`configResolved` throws when there is no config file** (`index.js:367-372`). `resolvedConfig.configFile` is optional (programmatic `createServer`, or no config in the directory); `path.dirname` is guarded but `findFsProjFile(undefined)` then hits `fs.readdir(undefined)`. Use `resolvedConfig.root`, which is also more correct: `root` and the config file's directory differ whenever `root` is set.
- **`configuration` is derived from `env.MODE`** (`index.js:364`), so `vite build --mode staging` compiles Debug F#. `state.isBuild` is already captured from `command === "build"` (`index.js:365`) but unused for this. Make it an explicit plugin option defaulting to `isBuild ? Release : Debug`, and document it.
- **`transform.filter` ignores query strings** (`index.js:435`). Vite's convention is `makeIdFiltersToMatchWithQuery` from `@rolldown/pluginutils`, used by Vite itself (`plugins/asset.ts:205`) and by plugin-react. Low impact for the main path — Vite still appends `?import` to bare module URLs (`src/node/utils.ts:308`) but strips it in `transformRequest.ts:497` before the id reaches the plugin container, so `transform` sees a clean absolute path. Explicit queries like `./Component.fs?raw` still fall straight through, and the `compilableFiles.has(id)` lookups should go through `cleanUrl`.
- **Path normalisation is asymmetric** (`index.js:234-238` vs `282-285`). `fsharpFileChanged` normalises the daemon's keys before storing them; `compileProject` indexes `compiledFSharpFiles` with an already-normalised name instead. If the daemon ever returns a backslash path, every value in `compilableFiles` becomes `undefined`. Normalise on the way in, in one place.
- **`.fsx` is matched but never compiled** (`index.js:15`). Script files are never in `compilableFiles`, so every `.fsx` import warns and then fails to parse. Either drop `.fsx` from the regex or handle it.
- **`console.log(msg)` at `index.js:487`** is leftover debugging.

## 5. The JSX handoff to plugin-react works by accident

plugin-react 6 no longer transforms JSX per file. It sets the global `oxc.jsx` option plus `jsxRefreshInclude` in its `config()` hook; Vite's built-in `vite:oxc` then processes any id matching `jsxRefreshFilter` (`plugins/oxc.ts:310`) and forces `lang: 'js'` for non-JS extensions (`plugins/oxc.ts:266-268`).

So in `sample-project/vite.config.js`, `react({ include: /\.fs$/ })` does **nothing for JSX** — it only widens the _refresh_ filter. The plugin's own `transformWithOxc` call is genuinely required. But react-refresh only stays enabled for a `.fs` file because of `plugins/oxc.ts:257-262`: refresh is disabled unless the filename ends in `x` **or the code already contains `react/jsx-runtime`** — true only because the plugin's transform just injected that import.

Consequences: `jsx: "preserve"`, or an `.fs` component file that happens to contain no JSX, silently loses fast refresh.

**To do**

- [ ] Decide deliberately whether the plugin keeps owning the JSX transform. If yes, pass `refresh` through explicitly rather than relying on Vite's `jsx-runtime` sniff.
- [ ] Comment `sample-project/vite.config.js` and the docs so `include: /\.fs$/` is not mistaken for the thing that makes JSX work.

## 6. Do not block `httpServer.listen` on the first compile

`buildStart` blocks the dev server from listening (`server/index.ts:1104` awaits it before `listen`), so a cold F# compile means no URL printed, no overlay, nothing to look at.

Spawning the daemon in `configureServer`, keeping a `ready` promise, and awaiting it in `load`/`transform` for `.fs` ids only would boot the server instantly and put F# errors in the browser overlay instead of the terminal.

Related: in build mode `buildStart`/`buildEnd` _are_ per-environment (`builder.buildApp`), and the plugin object is shared across environments, so `state.dotnetProcess` would be clobbered if environments ever build concurrently. In dev this is a non-issue — `pluginContainer.ts:334-337` gates `buildStart`/`buildEnd`/`watchChange` to the client environment unless a plugin opts in with `perEnvironmentStartEndDuringDev`.

## 7. Replace `postinstall` with a prebuilt daemon package

**Why this matters most**

Not performance — reliability. `packages/vite-plugin-fable/package.json` `postinstall` runs `dotnet publish` on the consumer's machine, and package managers increasingly refuse to run lifecycle scripts by default: bun only runs them for `trustedDependencies`, pnpm gates them behind `onlyBuiltDependencies`, and `npm --ignore-scripts` is common in CI and locked-down environments. When the script is skipped the install still _succeeds_, no `bin/` is produced, and the failure surfaces much later as a confusing `buildStart` error. Getting off `postinstall` removes a whole class of "it doesn't work on my machine" reports. The compile-on-install cost (full NuGet restore, needs network, needs the right SDK) is a bonus.

**Decision: one prebuilt npm package containing portable IL**

A framework-dependent publish of `Fable.Daemon` is portable IL that runs anywhere the .NET 10 runtime does, so this is a single **plain dependency** — not `optionalDependencies`, no `os`/`cpu` fields, no per-platform publish matrix:

```
vite-plugin-fable  →  dependencies: { "<daemon package>": "<exact plugin version>" }
```

The bits then arrive at `npm install` like everything else, the lockfile pins them so plugin/daemon skew is structurally impossible, and the existing `node_modules` CI cache covers them. The .NET SDK is still required — project cracking shells out to `dotnet msbuild` (`Fable.Daemon/MSBuild.fs:18`, and Fable's own `MSBuildCrackerResolver` does the same) — and that is accepted; say so plainly in the README.

**Naming: leave room for a future split**

Whatever the single package is called now becomes awkward if platform packages arrive later, so pick a name that does not encode the current choice (nothing like `-portable`). The ecosystem convention is a scope for the platform packages — `esbuild` + `@esbuild/darwin-arm64`, `rolldown` + `@rolldown/binding-darwin-arm64`, `oxfmt` + `@oxfmt/binding-darwin-arm64`.

The `@fable-org` scope already exists on npm (`@fable-org/fable-library-js`), so something like `@fable-org/fable-daemon` leaves `@fable-org/fable-daemon-<rid>` free later. The daemon is also not really Vite-specific — it is a Fable compilation daemon that this plugin happens to drive — so a name that does not mention Vite is both more accurate and more reusable. Coordinate with the Fable org before claiming it.

**Deferred: per-RID ReadyToRun packages**

Measured on this repo, `sample-project`, warm caches, four `vite build` runs each:

| variant                                                 | size  | runs                       |
| ------------------------------------------------------- | ----- | -------------------------- |
| ReadyToRun (`--ucr -p:PublishReadyToRun=true`, per-RID) | 77 MB | 1.41s, 1.41s, 1.40s, 1.42s |
| portable IL (framework-dependent)                       | 33 MB | 2.15s, 2.17s, 2.15s, 2.13s |

About **0.74s, roughly 35% faster** with ReadyToRun, very low variance. Caches were warm, so that is JIT across the type-check and compile path, not process startup, and it is paid on every `vite dev` start and every `vite build`.

Not acted on yet, for two reasons:

- **The 35% flatters ReadyToRun.** `sample-project` is five files. JIT cost is roughly fixed — the same FCS code paths get compiled regardless of project size — so on a real project the absolute delta stays near 0.7s while the percentage collapses. Re-measure against something like the telplin or fantomas-tools projects in `DebugTests.fs` before treating 35% as real.
- **Its value depends on item 6.** Today `buildStart` blocks `httpServer.listen`, so 0.74s is spent staring at a terminal with no URL. If item 6 lands and the dev server starts immediately with F# compiling in the background, the same 0.74s is largely invisible and a five-package matrix stops being worth it.

Cost if it is ever taken up: five or so packages at 77 MB each to publish (a consumer downloads one), a per-RID CI matrix, `os`/`cpu` platform packages, a resolution shim on the JS side, and a fallback when no platform package matches.

**Also considered: a multi-RID .NET tool**

.NET 10 can publish a tool carrying several RuntimeIdentifiers with the CLI selecting one at run time, invoked via `dotnet tool exec` / `dnx` without a permanent install (verified working on SDK 10.0.400). It fits, since the SDK is required anyway, and it would move RID selection out of npm entirely — see `baronfel/multi-rid-tool` for `PackAsTool` / `ToolCommandName` / `dotnet pack -p ToolType=<agnostic|specific|self-contained|trimmed|aot>`.

Passed over because the bits would then arrive on first `vite dev` rather than at install, putting a NuGet download in front of a dev server that already blocks on startup, and shifting the failure mode for restricted networks from install time to first run. Worth revisiting only if the per-RID split happens, where the CLI doing RID selection beats maintaining `os`/`cpu` packages. Self-contained, trimmed and AOT are ruled out regardless: the SDK is present anyway, and FSharp.Compiler.Service is reflection-heavy enough that trimming would break things (`Caching.fableCompilerVersion` reads the Fable.Compiler version via reflection).

**To do**

- [ ] Agree the package name with the Fable org, then publish the portable-IL daemon from CI, version-locked to the plugin release.
- [ ] Resolve the daemon through the module system (`import.meta.resolve`), the way `getFableLibrary` already resolves `fable-library`, replacing the hard-coded `path.join(currentDir, "..", "bin", "Fable.Daemon.dll")`.
- [ ] Drop `postinstall`, the F# sources and `Directory.*.props` from the published plugin's `files`.
- [ ] Keep `bun run build:daemon` at the repo root for local work so the sample project runs against the working tree, not the published package.
- [ ] Give a clear error when the daemon package or the .NET SDK is missing (item 2 covers the spawn-side detection).
- [ ] README: state that the .NET 10 SDK is required, and why.

## 8. Move the plugin to TypeScript

Decided 2026-08-29: the plugin stays JavaScript-family and is written the way Vite plugins are normally written, so that the Vite typings are consumed directly rather than re-asserted through bindings. See item 11 for the alternative that was rejected.

`index.js` plus `types.d.ts` plus JSDoc is already TypeScript wearing a coat — `bunx tsc` runs over it today. The conversion is mechanical (rename, move the JSDoc into signatures), which is why it is worth doing **before** items 1, 3 and 4 rather than after: it is not a rewrite, so nothing gets done twice, and every subsequent fix lands in a file where the compiler is helping.

The payoff is upgrade safety. Consuming `vite`'s own `.d.ts` means a Vite major that changes a hook shape fails `tsc` instead of failing silently at runtime.

**To do**

- [ ] Rename `index.js` → `index.ts`, fold `types.d.ts` into it (or keep it as the public surface only), drop the JSDoc type annotations.
- [ ] Turn on `"strict": true` in `tsconfig.json`. It is `false` today, which is why `checkJs` never flagged `resolvedConfig.configFile` being optional (item 4). Consider `noUncheckedIndexedAccess` too — that is the `compiledFSharpFiles[file]` class of bug.
- [ ] Add a build step: `tsconfig.json` currently has `noEmit: true` and the package ships `index.js` as-is, so `prepublishOnly` needs to emit. This is a new cost where there is none today; keep it small.
- [ ] Update `files` and `main` in `package.json`, and ship the generated `.d.ts`.
- [ ] Keep `bunx tsc` as the lint step, now type-checking real TypeScript.

## 9. Tighten the daemon ↔ plugin RPC contract

The contract is hand-mirrored today and the mirroring is positional, which is where it actually hurts. `FSharpDiscriminatedUnion` in `types.d.ts` types `fields` as `any[]`, so every call site indexes blind: `result.fields[0]` / `[1]` / `[2]` in `getProjectFile` (`index.js:154-157`), `fields[0]` in `tryInitialCompile` (`index.js:174`), `fields[0]` / `fields[1]` in `fsharpFileChanged` (`index.js:278-287`). Reordering a field in an F# DU case in `Types.fs` silently breaks the plugin with no compile error on either side.

**To do**

- [ ] Return named records from the daemon's JSON-RPC methods instead of positional DU fields (`FSharp.SystemTextJson` is already a dependency), so the wire format is self-describing.
- [ ] Replace `FSharpDiscriminatedUnion` with a discriminated result type TypeScript can narrow.
- [ ] Validate at the JSON-RPC boundary only — the three response shapes from `fable/project-changed`, `fable/initial-compile` and `fable/compile`. That is the one place untyped JSON enters the process. Zod or similar; note that a hand-written schema is still a mirror of the daemon's contract, it just fails loudly instead of silently. Validation matters more if item 7 goes the `dotnet tool` route, where a plugin and daemon of different versions can genuinely meet.
- [ ] Better: generate the types (and schemas) from `Fable.Daemon/Types.fs` at build time so the two cannot drift at all. If this is cheap it beats hand-written schemas.

## 10. Cache invalidation questions

- The design-time build cache key (`Caching.fs`) hashes MSBuild inputs only. Adding/removing a `<Compile Include>` changes the fsproj hash so that is covered, but `Directory.Build.props` files outside `MSBuildAllProjects` (for instance ones pulled in via `Import` with a condition that is false at evaluation time) are not.
- The `fable_modules` cache (`.vite-plugin-fable-modules`) is written once and only invalidated together with the design-time cache. A change to Fable plugin options (`noReflection`, `exclude`) changes `CliArgs` but not the cache key, so stale `fable_modules` output can be served. `Defines` are in the key; `NoReflection` and `Exclude` are not.
- `tryCompileProject` compiles with `NoCache = true` in `CliArgs` while the daemon maintains its own cache; confirm nothing in newer Fable.Compiler versions relies on that flag for correctness.

## 11. Rejected: writing the plugin in F# / Fable

Recorded so it does not get re-litigated. The motivation was sharing `Types.fs` between daemon and plugin; items 8 and 9 deliver that at a fraction of the cost.

- Of the roughly fifteen findings in items 1-6, exactly one (`resolvedConfig.configFile` being optional) was a shape bug a type system catches. The rest are semantics — what Vite and Node _do_ — and no type system encodes "returning an empty array from `handleHotUpdate` means send nothing".
- Hand-written Fable bindings are unverified assertions about someone else's API, and they read as authoritative once written. Consuming Vite's own `.d.ts` means a hook shape change fails the build; a binding just keeps agreeing with itself.
- `README.md` says the project is up for adoption. The people best equipped to fix a `hotUpdate` bug are Vite people; F# plugin internals shrink that pool to roughly "F# developers who also know Vite's plugin container".

The one argument that survives is dogfooding — a real Vite plugin written in Fable would be genuine exercise for Fable's JS output. That is a project-mission argument rather than an engineering one. Revisit only on those terms.

## 12. Housekeeping (small, can be folded into any of the above)

- `README.md` still says the package "was merely pushed to reserve the name" and that the project is up for adoption; refresh once direction is settled.
- Check whether Fable upstream would accept the design-time build cache and dependent-files tracking now living in `ProjectCracking.fs`, so the plugin can shrink further.
- `ideas.md`: filter diagnostics from `fable_modules` (plugin option) and expose a version property on Fable.Compiler (`Caching.fableCompilerVersion` reads it via reflection today).
- `mailbox.Error.Subscribe (fun _ -> ())` in `Program.fs` swallows mailbox failures silently.
- `Fable.Daemon.Tests/DebugTests.fs` references `../../telplin` and `../../fantomas-tools` checkouts; only the sample-project case is portable.
- There are no tests on the JavaScript side at all. A couple of integration tests driving `createServer` against `sample-project` would de-risk item 1.
