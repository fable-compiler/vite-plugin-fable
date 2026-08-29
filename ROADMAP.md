# Roadmap / action items

Captured on 2026-08-29 after a scan of the code base. Each item has enough context to be picked up independently.
Order is a rough suggestion: the first three unblock building locally and shrink the code base, the rest are larger design questions.

## 1. Move to the .NET 10 SDK — done (2026-08-29)

`global.json` is on 10.0.400, the daemon and tests target `net10.0` (forced: Fable.Compiler 5.14.1 only ships `net10.0`), CI reads the SDK version from `global.json`. Users now need the .NET 10 runtime.

**Original notes**

- `global.json` pins SDK `9.0.102` with `rollForward: latestMinor`. Only 10.0.300 / 10.0.400 are installed locally (via mise), so every `dotnet` command in this repo currently fails.
- `Fable.Daemon.fsproj` targets `net8.0` with `RollForward=LatestMajor` (introduced in 0.1.1 so the daemon runs on whatever runtime the user has).
- `Directory.Packages.props` pins `FSharp.Core 8.0.200` and `System.Text.Json 9.0.0`.
- CI (`.github/workflows/*.yml`) uses `actions/setup-dotnet@v4` with no version input, so it picks up whatever the runner image provides.

**To do**

- [ ] `global.json` → `10.0.x`, keep a permissive `rollForward`.
- [ ] Decide the daemon TFM: `net10.0` (simplest; users need the .NET 10 runtime) or keep `net8.0` + `LatestMajor` for maximum runtime compatibility. Document the decision in the changelog.
- [ ] Bump `FSharp.Core`, `System.Text.Json`, and the test packages in `Directory.Packages.props`.
- [ ] Pin the SDK version in both workflows (`dotnet-version: 10.x` or read from `global.json`).
- [ ] Verify `dotnet fantomas` / `dotnet fsdocs` tool manifest still restores on .NET 10.

## 2. Rethink `postinstall`

**Current state**

- `package.json` `postinstall` runs `dotnet publish Fable.Daemon/Fable.Daemon.fsproj -c Release --ucr -p:PublishReadyToRun=true -o ./bin` on the consumer's machine. The npm package therefore ships F# sources (`files` includes `Fable.Daemon/**/*.fs`, `Directory.*.props`) rather than a built daemon.
- Consequences: a full NuGet restore + compile on every `npm install` (slow, needs network, fails hard without the exact SDK), `bun install` runs lifecycle scripts only for trusted deps, and pnpm/yarn have similar opt-in rules so installs can silently produce no `bin/`.
- `index.js:19` hard-codes `bin/Fable.Daemon.dll` and spawns it with `dotnet`.

**Options to evaluate**

- Ship a prebuilt, framework-dependent `bin/` in the npm package (built in CI at publish time). Portable IL runs on any OS/arch that has the runtime; ReadyToRun would need per-RID packages, probably not worth it.
- Publish the daemon as a .NET tool (`dotnet tool install fable-daemon`) and have the plugin locate it; less coupling to npm but adds an install step for users.
- Lazy build on first `vite dev` with a clear error message when the SDK is missing, as a fallback rather than the primary path.

**To do**

- [ ] Pick a distribution model (leaning: prebuilt framework-dependent output in the npm package).
- [ ] Update `files` in `package.json` and the `prepublishOnly` step accordingly; drop the `postinstall` script.
- [ ] Keep a dev-time `bun run build:daemon` for local work and the sample project.
- [ ] Improve the error when `dotnet` is not on the path or the runtime is missing (today the plugin just logs "Unexpected failure during buildStart").

## 3. Replace `CoolCatCracking` with Fable's `MSBuildCrackerResolver` — done (2026-08-29)

`Fable.Daemon/ProjectCracking.fs` holds `CachedMSBuildCrackerResolver`: it computes the cache key, reuses the cached design time build when valid, and otherwise delegates to `Fable.Compiler.MSBuildCrackerResolver`. `MSBuild.fs` remains only for the cache-key `--getProperty` query. `NoRestore` is now `false` in `CliArgs` because Fable's resolver uses it to decide whether to pass `/restore`. `cracking.fsx` stays as a scratch script (`./cracking.fsx MyProject.fsproj`). `Caching.fs` decodes the MSBuild JSON with `System.Text.Json`, so the `Thoth.Json.*` packages are gone. Still open: upstreaming the cache to Fable.

**Original notes**

- `Fable.Daemon/CoolCatCracking.fs` (`CoolCatResolver`) implements `ProjectCrackerResolver` by running `dotnet msbuild --getItem:FscCommandLineArgs …` as a design-time build.
- Fable.Compiler now ships `Fable.Compiler.MSBuildCrackerResolver` (`~/Projects/Fable/src/Fable.Compiler/MSBuildCrackerResolver.fs`) which is the same approach line-for-line (same targets, same `NonExistentFile` trick, same NuGet lock-file workaround).
- The plugin pins `Fable.Compiler 5.0.0-alpha.13`; Fable is at `5.14.1` (2026-08-25). Upgrading is a prerequisite and may bring API changes in `CliArgs` / `CompilerOptions` / `CodeServices`.

**What CoolCat does that Fable's resolver does not** (must be preserved or consciously dropped)

- Design-time build cache (`Caching.fs`): serialises the cracker result to `obj/<fsproj>.vite-plugin-design-time`, keyed on hashes of the fsproj, every file in `MSBuildAllProjects`, the NuGet `*.nuget.g.props`, the Fable defines, and the Fable.Compiler version. This is what makes a warm `vite dev` start fast.
- `fable_modules` compile cache (`obj/<fsproj>.vite-plugin-fable-modules`): reuses compiled JS for NuGet-sourced F# files across restarts.
- `MSBuildProjectFiles`: the list of MSBuild files that the plugin adds to Vite's watcher (`dependentFiles` in `index.js`) so that editing the fsproj or a props file triggers a full re-crack.
- Structured logging through `ILogger` (Fable's version has no logging and uses `Async.RunSynchronously` internally, same as ours).
- Fable's version passes defines as `/p:DEFINE=True` in addition to the `DefineConstants` environment variable; ours only uses the env var. Worth checking which one actually works for `Condition="'$(FABLE_COMPILER)' == 'true'"` style fsproj conditions.

**To do**

- [ ] Upgrade `Fable.Compiler` / `Fable.AST` / `fable-library-js` to current versions and fix the fallout in `Program.fs`.
- [ ] Wrap `MSBuildCrackerResolver` in a thin decorator that keeps the cache + dependent-files behaviour (`Caching.fs` can stay largely as is; only the `mkProjectCacheKey` MSBuild query and `MSBuildProjectFiles` need a home).
- [ ] Delete `CoolCatCracking.fs`, `MSBuild.fs`, `cracking.fsx`, and the `Thoth.Json.*` package references if nothing else uses them.
- [ ] Check whether Fable upstream would accept the caching / dependent-files features so the plugin can shrink further.

## 4. Audit the Vite integration

Questions to answer with the Vite 7 docs and, ideally, a small experiment in `sample-project`.

**Lifecycle**

- `buildStart` spawns the daemon and blocks on the full initial compile. In dev mode this happens once; in build mode `buildStart` can run per environment (Vite 6+ environments API, SSR builds), which would spawn multiple daemons. `buildEnd` kills the process, which is only ever reached in build mode; on `vite dev` shutdown the daemon is killed implicitly when Node exits. Consider `configureServer` + `server.httpServer.once('close')` for dev, and check for leaked `dotnet` processes after `vite build --watch`.
- The plugin uses `enforce: "pre"` and relies on `@vitejs/plugin-react` also matching `.fs` files (`react({ include: /\.fs$/ })` in `sample-project/vite.config.js`). Verify this still composes correctly with plugin-react 5 / React Compiler and with the `transform.filter` object introduced in 0.2.0.

**HMR**

- `handleHotUpdate` is the pre-environments API; Vite 6+ prefers `hotUpdate` (per-environment). Decide whether to migrate now or keep `handleHotUpdate` for compatibility.
- The RxJS `Subject` + `bufferTime(50)` + shared `Promise.withResolvers` dance exists to coalesce concurrent hot updates. Check whether Vite's own debouncing makes this unnecessary; if not, it could be a ~30-line hand-written queue and RxJS (`rxjs`, `promise.withresolvers` shim) could be dropped. Node 22+ has `Promise.withResolvers` natively.
- `console.log(msg)` at `index.js:521` is leftover debugging.
- `handleHotUpdate` returns `modules.filter(m => m.importers.size !== 0)`; when a dependent file recompiles several F# files only the originally changed module is in `modules`, so downstream files that changed content are never invalidated. Verify with a change in `Math.fs` that alters the output of `Library.fs`.

**Other**

- `configResolved` derives Debug/Release from `env.MODE === "production"`; should probably follow `command === "build"` or an explicit option, and be documented.
- Source maps: `transform` returns `map: null`. Fable can emit source maps (`SourceMaps` in `CliArgs`), which would make the browser devtools show F# instead of JS.
- `types.d.ts` + JSDoc is the type story today; if the plugin stays in JS, switching `index.js` to `.ts` (Vite 7 supports TS config/plugins out of the box via esbuild/rolldown) removes the JSDoc friction.
- Rolldown-vite: Vite 7 makes rolldown opt-in and Vite 8 makes it the default. The plugin only uses standard hooks, but the `transform.filter` shape and `this.addWatchFile` semantics should be checked against rolldown.

## 5. Write the JavaScript side in F# / Fable

An experiment worth a spike. The plugin (`index.js`) is ~540 lines and mostly state machine + RPC glue, which is comfortable F#.

**Considerations**

- Bootstrapping: the plugin would be compiled with `dotnet fable` (or the plugin itself, once stable) as part of `prepublishOnly`; the npm package ships the emitted JS. Consumers are unaffected.
- Bindings needed: `vite` (Plugin, ResolvedConfig, HMR types), `node:child_process`, `node:fs/promises`, `esbuild.transform`, `@babel/code-frame`, `ts-lsp-client`'s `JSONRPCEndpoint`. Fable.Node / Glutinum can cover Node; Vite bindings would need to be hand-written or generated with Glutinum from `vite`'s `.d.ts`.
- Payoff: shared `Types.fs` between daemon and plugin (no more hand-mirrored `FSharpDiscriminatedUnion { case, fields }` decoding in JS), and the plugin becomes a real-world dogfooding project for Fable's JS output.
- Cost: contributors from the Vite side can no longer read/patch the plugin without F# tooling; debugging emitted JS in Vite's plugin pipeline; `tsc` lint step goes away.

**To do**

- [ ] Spike: port `index.js` to `src/Plugin.fs`, keep behaviour identical, measure friction with Vite typings.
- [ ] Decide based on the spike; if going ahead, the RPC message types move to a shared project.

## 6. Cache invalidation: signature files (`.fsi`) are not handled

**Confirmed problem**

Editing a `.fsi` file does nothing today. The flow:

1. `compileProject` in `index.js` calls `addWatchFile` for every source file, including `.fsi`, so Vite does see the change.
2. `watchChange` only forwards files in `state.dependentFiles` (MSBuild files), so the `.fsi` is ignored there.
3. `handleHotUpdate` only forwards files in `state.compilableFiles`. That map is populated from the daemon's compile output, and `tryCompileProject` (`Program.fs`) explicitly filters out `.fsi` files, so a signature file is never a key.
4. Result: no recompile is requested, no diagnostic is shown, and the browser keeps stale output until an implementation file is touched.

The daemon side already anticipates this: `tryCompileFiles` has `mapLeadingFile`, which maps `Foo.fs` to `Foo.fsi` before asking `InteractiveChecker.GetDependentFiles`, so once the plugin forwards the change the dependency walk should work.

**To do**

- [ ] In `handleHotUpdate` (or `hotUpdate`), treat any file in `state.sourceFiles` as an F# change, not only keys of `compilableFiles`.
- [ ] Map a changed `Foo.fsi` to its `Foo.fs` for the HMR module lookup (the browser imports the `.fs`), and make sure the recompiled `.fs` module is what gets invalidated.
- [ ] Add a test in `Fable.Daemon.Tests` that changes `sample-project/Component.fsi` (e.g. removes the `Component` val) and asserts a diagnostic on `Component.fs`.

**Related cache questions to double-check while there**

- The design-time build cache key (`Caching.fs`) hashes MSBuild inputs only. Adding/removing a `<Compile Include>` changes the fsproj hash so that is covered, but `Directory.Build.props` files outside `MSBuildAllProjects` (for instance ones pulled in via `Import` with a condition that is false at evaluation time) are not.
- The `fable_modules` cache (`.vite-plugin-fable-modules`) is written once and only invalidated together with the design-time cache. A change to Fable plugin options (`noReflection`, `exclude`) changes `CliArgs` but not the cache key, so stale `fable_modules` output can be served. `Defines` are in the key; `NoReflection` and `Exclude` are not.
- `tryCompileProject` compiles with `NoCache = true` in `CliArgs` while the daemon maintains its own cache; confirm nothing in newer Fable.Compiler versions relies on that flag for correctness.

## 7. Housekeeping (small, can be folded into any of the above)

- `README.md` still says the package "was merely pushed to reserve the name" and that the project is up for adoption; refresh once direction is settled.
- `ideas.md`: filter diagnostics from `fable_modules` (plugin option) and expose a version property on Fable.Compiler (`Caching.fableCompilerVersion` reads it via reflection today).
- `MSBuild.fs`: `WaitForExit(5s)` only logs on timeout and then continues with partial output; a real design-time build with restore easily exceeds 5s on a cold machine.
- `mailbox.Error.Subscribe (fun _ -> ())` in `Program.fs` swallows mailbox failures silently.
- `Fable.Daemon.Tests/DebugTests.fs` references `../../telplin` and `../../fantomas-tools` checkouts; only the sample-project case is portable.
- Sample project pins React 18 / plugin-react 4.3.4 / vite-plugin-inspect 0.8.8; bump alongside the Vite audit.
- `package.json` has a typo: `"fundinding"` should be `"funding"`.
