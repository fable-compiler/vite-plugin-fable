# Roadmap / action items

Nothing here is done. Items are deleted as they land, so what remains is open work, a question nobody has answered, or a decision recorded so it does not get re-litigated. Finished work belongs in `CHANGELOG.md`, not here. Each item has enough context to be picked up independently.

Item 1 is blocked upstream and can only be tracked and item 2 is on hold. Items 3 and 4 are projects, item 5 is a smaller one, item 6 records a rejected decision and item 7 is loose ends.

References into `~/Projects/Fable` are against Fable 5.14, the version in the workspace catalog.

## 1. Blocked on Fable

Neither of these can be fixed in this repo. Both need an upstream change first.

- **Fable's own errors never reach the plugin.** `CodeServices.compileMultipleFilesToJavaScript` fills `CompileResult.Diagnostics` from FCS's type-check results only, and discards the `CompilerImpl` holding `com.Logs` (`~/Projects/Fable/src/Fable.Compiler/Library.fs:223` upcasts it to the `Compiler` interface, where `Logs` does not exist). A file that type-checks but that Fable cannot translate therefore compiles to `return null` with no diagnostic at all: `vite build` prints nothing, exits 0, and the app breaks at runtime. Reproduced in `sample-project` with `Async.RunSynchronously`, and filed with a proposed direction as [fable-compiler/Fable#4922](https://github.com/fable-compiler/Fable/issues/4922).
  There is a local half waiting on it: `FilesCompiledResult.Success` carries no diagnostics, so `tryCompileProject` has nowhere to put them and `failBuildOnErrors` would never see them.
- **Real F# source maps.** `FileWriter.AddSourceMapping` in `src/Fable.Compiler/Library.fs:84-90` is a no-op with the `SourceMapSharp` generator commented out, so `CliArgs.SourceMaps` does nothing. The plugin returns `{ mappings: '' }`, which is honest about having no mapping, but a real F#-to-JS one needs the Fable change first. Not filed.

## 2. A Vite DevTools panel (on hold)

**On hold, not dropped.** A panel is still worth having; nothing below is being acted on until someone picks the investigation back up. What changed is that this is no longer a replacement for the built-in debug server, because that server now does something a panel cannot.

The daemon ships its own developer tool: `Debug.fs` runs a **Suave** web server on port 9014, gated behind the `debug` plugin option or `VITE_PLUGIN_FABLE_DEBUG`. It serves two things. A WebSocket feed and a hand-written `debug/index.html` for watching the in-memory log, and JSON endpoints under `/api` reporting what the daemon cracked, compiled and cached, for anything that is not a pair of eyes. The second half is the reason this item shrank: a browser panel is worth nothing to a script, a terminal or an agent, and `docs/debug.md` and `CLAUDE.md` both point at `/api` now.

[`@vitejs/devtools`](https://devtools.vite.dev/) is the ecosystem answer to the human half, and it subsumes `vite-plugin-inspect` (it depends on `@devframes/plugin-inspect`). Moving the F# view into a panel there would put the information where people already look, next to the module graph and the per-plugin transform steps.

**What it would delete**, which is now much less than it was

Only `Fable.Daemon/debug/index.html` and the WebSocket feed that fills it. `Debug.fs`, `Debug.fsi`, the `Suave` reference and the `VITE_PLUGIN_FABLE_DEBUG` gate all stay, because the JSON endpoints are served from them. Deleting the HTML page alone does not pay for a bespoke UI, so a panel has to be worth building on its own terms.

**What is worth surfacing**

The plugin already receives some of this over JSON-RPC, so a panel could be fed from the JavaScript side: the current `compilableFiles` map, the last set of diagnostics, cracking versus compile timings, and which files each hot-update batch recompiled. The rest never crosses JSON-RPC — project options, the watched MSBuild inputs, the design-time cache hit or miss with its reason — so a panel wanting those reads the daemon's `/api` rather than duplicating the state.

**How it actually runs**, which is not shaped like `vite-plugin-inspect`

DevTools is a **separate CLI on its own port**, not a route on your dev server. `vite-devtools` (shipped as a bin by `@vitejs/devtools`) reads the Vite config, starts on port 9999 by default and opens a browser. Adding `DevTools()` to `plugins` injects nothing into the app page and mounts no `/__devtools/` route. The embedded panel does work on Bun, but only because of `patches/crossws@0.4.12.patch`; `CLAUDE.md` covers what that patch does and why it is pinned.

**Unverified, and the reason not to start yet**

Whether custom panels are supported at all. `@vitejs/devtools-kit` and `@vitejs/devtools-rpc` exist and are shaped like something third-party panels would use, but the published docs cover installation only, and 0.6.2 is described as "early preview".

**To do**

- [ ] Confirm whether custom panels are supported, and how, before building anything against an early-preview tool.
- [ ] Track [devframes/devframe#317](https://github.com/devframes/devframe/issues/317); the embedded plugin needs it, the standalone CLI does not. Until it lands, a `crossws` bump needs the patch rebased.
- [ ] If both clear, decide whether a panel earns its keep next to `/api`, and if so build it and delete `debug/index.html` and the WebSocket feed.

## 3. Plain F# modules always force a page reload

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

## 4. Replace `postinstall` with a prebuilt daemon package

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
- **Its value has already shrunk.** The dev server no longer blocks on the first compile, so the 0.74s is spent behind a server that is already listening rather than in front of a terminal with no URL. That makes a five-package matrix much harder to justify.

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

## 5. Cache invalidation: an explicit `<Import>` is invisible

- The design-time build cache key (`Caching.fs`) takes its MSBuild inputs from `MSBuildAllProjects` plus the convention imports asked for by name (`DirectoryBuildPropsPath` and friends). Since MSBuild 16.9 an import no longer adds itself to `MSBuildAllProjects`, so a file pulled in by an explicit `<Import>` is in neither list: changing it neither invalidates the cache nor re-cracks the project. Getting the real list means `dotnet msbuild -preprocess` or an equivalent, a much heavier query than the property reads the cache key does today.

## 6. Rejected: writing the plugin in F# / Fable

Recorded so it does not get re-litigated. The motivation was sharing `Types.fs` between daemon and plugin, which the named wire format and the fixtures both sides are tested against now deliver at a fraction of the cost.

- Of the roughly fifteen findings in the Vite review, exactly one (`resolvedConfig.configFile` being optional) was a shape bug a type system catches. The rest are semantics — what Vite and Node _do_ — and no type system encodes "returning an empty array from `hotUpdate` means send nothing".
- Hand-written Fable bindings are unverified assertions about someone else's API, and they read as authoritative once written. Consuming Vite's own `.d.ts` means a hook shape change fails the build; a binding just keeps agreeing with itself.
- `README.md` says the project is up for adoption. The people best equipped to fix a `hotUpdate` bug are Vite people; F# plugin internals shrink that pool to roughly "F# developers who also know Vite's plugin container".

The one argument that survives is dogfooding — a real Vite plugin written in Fable would be genuine exercise for Fable's JS output. That is a project-mission argument rather than an engineering one. Revisit only on those terms.

## 7. Housekeeping (small, can be folded into any of the above)

- `README.md` still says the package "was merely pushed to reserve the name" and that the project is up for adoption; refresh once direction is settled.
- Check whether Fable upstream would accept the design-time build cache and dependent-files tracking now living in `ProjectCracking.fs`, so the plugin can shrink further.
- `ideas.md`: filter diagnostics from `fable_modules` (plugin option) and expose a version property on Fable.Compiler (`Caching.fableCompilerVersion` reads it via reflection today).
- `Fable.Daemon.Tests/DebugTests.fs` references `../../telplin` and `../../fantomas-tools` checkouts; only the sample-project cases are portable.
- The JavaScript tests cover the hooks against a stub daemon (`tests/index.test.ts`). Still missing: an integration test driving `createServer` against `sample-project` with the real daemon.
