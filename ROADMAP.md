# Roadmap / action items

Captured on 2026-08-29 after a scan of the code base, trimmed as items land. Each item has enough context to be picked up independently.

Items 1-3 were verified against the Vite 8.2.2 source (`~/Projects/vite`, the version pinned in the workspace catalog) and Fable 5.14 (`~/Projects/Fable`); the `packages/vite/src/node` references below point into that checkout.

Order is a rough suggestion: 1-3 are contract fixes and design questions, 4-7 are larger. Item 8 records a decision rather than work.

## 1. Hook-contract fixes

- **`map: null` is the wrong signal** (`index.js:454`). In the Rollup/Vite contract `null` means "I did not move code, keep the previous map"; the transform replaces F# with JS. The correct value is `{ mappings: '' }` — what Vite's own plugins use for this case (`plugins/css.ts:583`, `plugins/asset.ts:247`).
  Note: real F# source maps are **blocked upstream**. `FileWriter.AddSourceMapping` in `~/Projects/Fable/src/Fable.Compiler/Library.fs:84-90` is a no-op with the `SourceMapSharp` generator commented out; `CliArgs.SourceMaps` exists but does nothing. Needs a Fable PR first.
  Worth knowing what happens meanwhile: because the plugin returns `map: null`, later stages generate their own map, and the served module ends with a `sourceMappingURL` whose `sources` says `Greeting.fs` while its `sourcesContent` is the compiled **JavaScript**. Devtools therefore shows a file named `.fs` containing JavaScript, which is more misleading than having no map at all. `{ mappings: '' }` fixes that on its own.
- **No `load` hook.** Vite reads the whole `.fs` file off disk purely so `transform` can discard it. A `load` for ids in `compilableFiles` skips the I/O and states the intent. Return `moduleType: 'js'` too — `vite:oxc` does (`plugins/oxc.ts:330`) — otherwise rolldown infers the type from the `.fs` extension.
- **`configuration` is derived from `env.MODE`** (`index.js:364`), so `vite build --mode staging` compiles Debug F#. `state.isBuild` is already captured from `command === "build"` (`index.js:365`) but unused for this. Make it an explicit plugin option defaulting to `isBuild ? Release : Debug`, and document it.
- **`transform.filter` ignores query strings** (`index.js:435`). Vite's convention is `makeIdFiltersToMatchWithQuery` from `@rolldown/pluginutils`, used by Vite itself (`plugins/asset.ts:205`) and by plugin-react. Low impact for the main path — Vite still appends `?import` to bare module URLs (`src/node/utils.ts:308`) but strips it in `transformRequest.ts:497` before the id reaches the plugin container, so `transform` sees a clean absolute path. Explicit queries like `./Component.fs?raw` still fall straight through, and the `compilableFiles.has(id)` lookups should go through `cleanUrl`.
- **Path normalisation is asymmetric** (`index.js:234-238` vs `282-285`). `fsharpFileChanged` normalises the daemon's keys before storing them; `compileProject` indexes `compiledFSharpFiles` with an already-normalised name instead. If the daemon ever returns a backslash path, every value in `compilableFiles` becomes `undefined`. Normalise on the way in, in one place.
- **`.fsx` is matched but never compiled** (`index.js:15`). Script files are never in `compilableFiles`, so every `.fsx` import warns and then fails to parse. Either drop `.fsx` from the regex or handle it.

## 2. The JSX handoff to plugin-react works by accident

plugin-react 6 no longer transforms JSX per file. It sets the global `oxc.jsx` option plus `jsxRefreshInclude` in its `config()` hook; Vite's built-in `vite:oxc` then processes any id matching `jsxRefreshFilter` (`plugins/oxc.ts:310`) and forces `lang: 'js'` for non-JS extensions (`plugins/oxc.ts:266-268`).

So in `sample-project/vite.config.js`, `react({ include: /\.fs$/ })` does **nothing for JSX** — it only widens the _refresh_ filter. The plugin's own `transformWithOxc` call is genuinely required. But react-refresh only stays enabled for a `.fs` file because of `plugins/oxc.ts:257-262`: refresh is disabled unless the filename ends in `x` **or the code already contains `react/jsx-runtime`** — true only because the plugin's transform just injected that import.

Consequences: `jsx: "preserve"`, or an `.fs` component file that happens to contain no JSX, silently loses fast refresh.

**To do**

- [ ] Decide deliberately whether the plugin keeps owning the JSX transform. If yes, pass `refresh` through explicitly rather than relying on Vite's `jsx-runtime` sniff.
- [ ] Comment `sample-project/vite.config.js` and the docs so `include: /\.fs$/` is not mistaken for the thing that makes JSX work.

## 3. The plugin is too noisy

A plain `vite dev` on `sample-project` — five F# files, nothing wrong — prints **26 `[fable]` lines** before the page has loaded. Most of it is progress narration the user did not ask for:

```
[fable]: configResolved: Configuration: Debug
[fable]: configResolved: Entry fsproj /abs/path/App.fsproj
[fable]: buildStart: Starting daemon
[fable]: buildStart: Initial project crack
[fable]: projectChanged: dependent file /abs/path/App.fsproj changed.
[fable]: compileProject: Full compile started of /abs/path/App.fsproj
[fable]: compileProject: fable-library located at /abs/path/node_modules/...
[fable]: compileProject: about to type-checked /abs/path/App.fsproj.
[fable]: compileProject: /abs/path/App.fsproj was type-checked.
[fable]: compileProject: Full compile completed of /abs/path/App.fsproj
[fable]: transform: /abs/path/Library.fs          (one per file, every time)
```

Vite itself prints four lines for a whole dev server. The plugin should be comparable: one line saying which project it compiled and how long it took, with the rest behind a flag.

Problems to fix while there:

- Several of these are `logInfo`/`logDebug` but reach the user identically — `logDebug` uses `logger.info` with dimmed colour, so nothing is actually filtered. There is no debug level; `VITE_PLUGIN_FABLE_DEBUG` only switches the daemon's own web log on port 9014.
- Absolute paths everywhere. Vite prints paths relative to root; these should too.
- `transform` logs one line per file on every request, so a page load with 50 F# files is 50 lines.
- Wording: "about to type-checked", "dependent file X changed." during the initial crack when nothing changed.

**To do**

- [ ] Decide the default output: probably one line on a successful compile (project + duration), plus diagnostics, plus errors. Nothing per file.
- [ ] Make `logDebug` respect a real debug switch (Vite's `logLevel`, `DEBUG=vite:fable`, or the existing `VITE_PLUGIN_FABLE_DEBUG`) rather than always printing.
- [ ] Log paths relative to `config.root`.
- [ ] Fix the wording that reads like a progress trace.

## 4. Replace the built-in debug server with a Vite DevTools panel

The daemon ships its own developer tool: `Debug.fs` runs a **Suave** web server on port 9014 with a WebSocket feed and a hand-written `debug/index.html`, gated behind `VITE_PLUGIN_FABLE_DEBUG`, so you can watch the daemon's in-memory log. It works, but it is a second HTTP server inside the compiler process, a bespoke UI to maintain, and a place users have to be told about separately from the tool they already have open.

[`@vitejs/devtools`](https://devtools.vite.dev/) is the ecosystem answer to the same problem, and it subsumes `vite-plugin-inspect` (it depends on `@devframes/plugin-inspect`). Moving the F# view into a panel there would put the information where people already look, next to the module graph and the per-plugin transform steps.

**What it would delete**

- `Fable.Daemon/Debug.fs` and `Debug.fsi`, and `Fable.Daemon/debug/index.html`.
- The `Suave` package reference — `Debug.fs` is its only consumer. (`protobuf-net` stays; `Caching.fs` uses it.)
- `Fable.Daemon/debug` from the published `files`, and the `VITE_PLUGIN_FABLE_DEBUG` special case.

**What is worth surfacing**

The plugin already receives all of this over JSON-RPC, so a panel could be fed from the JavaScript side without the daemon serving anything itself: the current `compilableFiles` map, the last set of diagnostics, cracking versus compile timings, which files each hot-update batch recompiled, and the design-time cache hit or miss with the reason.

**How it actually runs** — worth knowing, because it is not shaped like `vite-plugin-inspect`

DevTools is a **separate CLI on its own port**, not a route on your dev server. `vite-devtools` (shipped as a bin by `@vitejs/devtools`) reads the Vite config, starts on port 9999 by default and opens a browser: `⬢ Vite DevTools started at http://localhost:9998`. Adding `DevTools()` to `plugins` injects nothing into the app page and mounts no `/__devtools/` route — probing for one only ever hits Vite's SPA fallback, which returns the app's `index.html` with a 200 and looks like success.

**The Bun problem, and the patch that fixes it**

The crash was `[crossws] Using Node.js adapter in an incompatible environment`, from a defensive guard in crossws's Node adapter. `devframe` ships a Bun transport (`devframe/rpc/transports/ws-bun`) but `instance-shell` hardcodes the Node one, and the two are not interchangeable — `attachBunWsTransport` returns `{ handleUpgrade, websocket }` for Bun.serve's fetch-upgrade flow, while the Node transport attaches to an `http.Server`. Filed upstream as [devframes/devframe#317](https://github.com/devframes/devframe/issues/317).

Dropping the guard is enough, and `patches/crossws@0.4.12.patch` does exactly that:

```diff
-	if ("Deno" in globalThis || "Bun" in globalThis) throw new Error(...)
+	if ("Deno" in globalThis) throw new Error(...)
```

The adapter uses `ws` with `noServer: true`, which Bun handles. **With the patch, embedded DevTools works on Bun**: run `bun run dev`, open the app, then `#devframe`. The Build Flow view shows per-module resolve/load/transform steps with timings, and the transform diff shows F# source against the JavaScript `vite-plugin-fable` emitted.

The patch is pinned to `crossws@0.4.12`, so a version bump needs it rebased until #317 lands.

**Seeing the same data without a browser**

The DevTools panel is injected client-side behind a `#devframe_otp=` fragment, so nothing about it is visible over HTTP — the served HTML is byte for byte the same with and without it. That makes it useless from a terminal, CI, or an agent.

`Inspect({ build: true })` covers that gap and is configured alongside it in `sample-project/vite.config.js`. A `bun run build` writes `.vite-inspect/reports/`, where `modules.json` holds the graph (deps, importers, which plugins transformed what) and `transforms/*.json` holds each step's output — `__load__` is the F# source and `vite-plugin-fable` is the emitted JavaScript, as plain JSON. Same information as the panel, greppable.

**Also unverified**

Whether custom panels are supported at all. `@vitejs/devtools-kit` and `@vitejs/devtools-rpc` exist and are shaped like something third-party panels would use, but the published docs cover installation only, and 0.6.2 is described as "early preview".

**To do**

- [ ] Confirm whether custom panels are supported, and how, before building anything against an early-preview tool.
- [ ] Track [devframes/devframe#317](https://github.com/devframes/devframe/issues/317); the embedded plugin needs it, the standalone CLI does not.
- [ ] If both clear, build the panel, then delete `Debug.fs`, the `debug/` folder and the Suave dependency.

## 5. Plain F# modules always force a page reload

Editing an F# React component hot-updates, because `@vitejs/plugin-react` makes it a Fast Refresh boundary. Editing anything else reloads the page. That is correct today, but worth understanding before anyone tries to "fix" it in the plugin.

Vite walks up from the changed module looking for something that accepts the update. Fable emits no `import.meta.hot` at all, so for a plain module there is no boundary anywhere in the chain and Vite falls back to a reload.

**Why a self-accept would be a lie**

The obvious idea — have the plugin append `import.meta.hot.accept()` to every module — does not work, and the reason is in what Fable emits. `Greeting.fs` is pure and looks swappable:

```js
export function greet(name) {
  return concat("Hello from ", name);
}
```

But its importer captured the _result_, not the function, at module init:

```js
export const greeting = greet("F#");
h1Element.textContent = `${greeting} — dynamic Fable text ${r}! ${someJsonString}`;
```

Swapping `Greeting.fs` alone leaves `greeting` holding the old string. The browser would report a successful hot update and show nothing new — strictly worse than a reload, because it looks like it worked.

So the boundary has to be the importer, re-running its top-level code. `Library.fs` cannot: its module body ends in `createRoot(app).render(...)`, and running that twice mounts a second React root. F# modules routinely evaluate `let` bindings and perform effects at init, so re-running them is not idempotent in general.

**Why this is probably not fixable here alone**

Deciding whether a module is safe to re-run needs to know whether its top-level code is effect-free. Fable knows this — it is in the AST — but `Fable.Compiler` does not surface it, so the plugin cannot ask. Options, roughly in order of how much lives outside this repo:

- Have the app author opt in per module, with an explicit `import.meta.hot.accept` escape hatch the plugin leaves alone. Cheapest, but it is a JavaScript concept leaking into F# source.
- A plugin option marking modules as pure, self-accepting only those. Cheap and unsound: nothing checks the claim.
- Ask Fable to expose per-module "has top-level effects" alongside the compiled output, and self-accept only modules it vouches for. The principled version, and it needs an upstream change plus a decision about what the flag means for a language where module init is meaningful.

**To do**

- [ ] Decide whether this is worth pursuing at all. A reload on a non-component edit is not obviously bad, and the component path — the one people iterate on — already works.
- [ ] If yes, raise the "does this module have top-level effects" question with Fable before building anything on this side.

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
- [ ] Give a clear error when the daemon package cannot be resolved. A missing .NET SDK is already detected spawn-side and fails fast.
- [ ] README: state that the .NET 10 SDK is required, and why.

## 8. Tighten the daemon ↔ plugin RPC contract

The contract is hand-mirrored today and the mirroring is positional, which is where it actually hurts. `FSharpDiscriminatedUnion` types `fields` as `any[]`, so the decoding indexes blind: `fields[0]` / `[1]` / `[2]` in `daemon.ts`. Reordering a field in an F# DU case in `Types.fs` silently breaks the plugin with no compile error on either side. That decoding is now confined to `daemon.ts` rather than spread across three call sites, which is what makes this tractable.

**To do**

- [ ] Return named records from the daemon's JSON-RPC methods instead of positional DU fields (`FSharp.SystemTextJson` is already a dependency), so the wire format is self-describing.
- [ ] Replace the positional decoding inside `daemon.ts` with a discriminated result type TypeScript can narrow.
- [ ] Validate at the JSON-RPC boundary only — the three response shapes from `fable/project-changed`, `fable/initial-compile` and `fable/compile`. That is the one place untyped JSON enters the process. Zod or similar; note that a hand-written schema is still a mirror of the daemon's contract, it just fails loudly instead of silently. Validation matters more if item 7 goes the `dotnet tool` route, where a plugin and daemon of different versions can genuinely meet.
- [ ] Better: generate the types (and schemas) from `Fable.Daemon/Types.fs` at build time so the two cannot drift at all. If this is cheap it beats hand-written schemas.

## 9. Cache invalidation questions

- The design-time build cache key (`Caching.fs`) hashes MSBuild inputs only. Adding/removing a `<Compile Include>` changes the fsproj hash so that is covered, but `Directory.Build.props` files outside `MSBuildAllProjects` (for instance ones pulled in via `Import` with a condition that is false at evaluation time) are not.
- The `fable_modules` cache (`.vite-plugin-fable-modules`) is written once and only invalidated together with the design-time cache. A change to Fable plugin options (`noReflection`, `exclude`) changes `CliArgs` but not the cache key, so stale `fable_modules` output can be served. `Defines` are in the key; `NoReflection` and `Exclude` are not.
- `tryCompileProject` compiles with `NoCache = true` in `CliArgs` while the daemon maintains its own cache; confirm nothing in newer Fable.Compiler versions relies on that flag for correctness.

## 10. Rejected: writing the plugin in F# / Fable

Recorded so it does not get re-litigated. The motivation was sharing `Types.fs` between daemon and plugin; items 5 and 6 deliver that at a fraction of the cost.

- Of the roughly fifteen findings in the Vite review, exactly one (`resolvedConfig.configFile` being optional) was a shape bug a type system catches. The rest are semantics — what Vite and Node _do_ — and no type system encodes "returning an empty array from `handleHotUpdate` means send nothing".
- Hand-written Fable bindings are unverified assertions about someone else's API, and they read as authoritative once written. Consuming Vite's own `.d.ts` means a hook shape change fails the build; a binding just keeps agreeing with itself.
- `README.md` says the project is up for adoption. The people best equipped to fix a `hotUpdate` bug are Vite people; F# plugin internals shrink that pool to roughly "F# developers who also know Vite's plugin container".

The one argument that survives is dogfooding — a real Vite plugin written in Fable would be genuine exercise for Fable's JS output. That is a project-mission argument rather than an engineering one. Revisit only on those terms.

## 11. Housekeeping (small, can be folded into any of the above)

- `README.md` still says the package "was merely pushed to reserve the name" and that the project is up for adoption; refresh once direction is settled.
- Check whether Fable upstream would accept the design-time build cache and dependent-files tracking now living in `ProjectCracking.fs`, so the plugin can shrink further.
- `ideas.md`: filter diagnostics from `fable_modules` (plugin option) and expose a version property on Fable.Compiler (`Caching.fableCompilerVersion` reads it via reflection today).
- `mailbox.Error.Subscribe (fun _ -> ())` in `Program.fs` swallows mailbox failures silently.
- `Fable.Daemon.Tests/DebugTests.fs` references `../../telplin` and `../../fantomas-tools` checkouts; only the sample-project case is portable.
- The JavaScript tests cover the hooks against a stub daemon (`tests/index.test.ts`). Still missing: integration tests driving `createServer` against `sample-project` with the real daemon, and a contract test asserting the daemon's responses still match what `src/daemon.ts` decodes — that second one is the cheap half of item 8.
