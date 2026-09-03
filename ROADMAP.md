# Roadmap / action items

Nothing here is done. Items are deleted as they land, so what remains is open work, a question nobody has answered, or a decision recorded so it does not get re-litigated. Finished work belongs in `CHANGELOG.md`, not here. Each item has enough context to be picked up independently.

Item 1 is blocked upstream and can only be tracked and item 2 is on hold. Item 3 is a project, items 4 and 5 are smaller ones, item 6 records a rejected decision and item 7 is loose ends.

References into `~/Projects/Fable` are against Fable 5.15, the version in the workspace catalog.

## 1. Blocked on Fable

This cannot be fixed in this repo. It needs an upstream change first.

- **Real F# source maps.** `FileWriter.AddSourceMapping` in `src/Fable.Compiler/Library.fs:84-90` is a no-op with the `SourceMapSharp` generator commented out, so `CliArgs.SourceMaps` does nothing. The plugin returns `{ mappings: '' }`, which is honest about having no mapping, but a real F#-to-JS one needs the Fable change first. Not filed.

## 2. A Vite DevTools panel (on hold)

**On hold, not dropped.** A panel is still worth having; nothing below is being acted on until someone picks the investigation back up. What changed is that this is no longer a replacement for the built-in debug server, because that server now does something a panel cannot.

The daemon ships its own developer tool: `Debug.fs` runs a **Suave** web server on port 9014, gated behind the `debug` plugin option or `VITE_PLUGIN_FABLE_DEBUG`. It serves two things. A WebSocket feed and a hand-written `debug/index.html` for watching the in-memory log, and JSON endpoints under `/api` reporting what the daemon cracked, compiled and cached, for anything that is not a pair of eyes. The second half is the reason this item shrank: a browser panel is worth nothing to a script, a terminal or an agent, and `docs/debug.md` and `CLAUDE.md` both point at `/api` now.

[`@vitejs/devtools`](https://devtools.vite.dev/) is the ecosystem answer to the human half, and it subsumes `vite-plugin-inspect` (it depends on `@devframes/plugin-inspect`). Moving the F# view into a panel there would put the information where people already look, next to the module graph and the per-plugin transform steps.

**What it would delete**, which is now much less than it was

Only `Fable.Daemon/debug/index.html` and the WebSocket feed that fills it. `Debug.fs`, `Debug.fsi`, the `Suave` reference and the `VITE_PLUGIN_FABLE_DEBUG` gate all stay, because the JSON endpoints are served from them. Deleting the HTML page alone does not pay for a bespoke UI, so a panel has to be worth building on its own terms.

**What is worth surfacing**

The plugin already receives some of this over JSON-RPC, so a panel could be fed from the JavaScript side: the current `compilableFiles` map, the last set of diagnostics, cracking versus compile timings, and which files each hot-update batch recompiled. The rest never crosses JSON-RPC — project options, the watched MSBuild inputs, the design-time cache hit or miss with its reason — so a panel wanting those reads the daemon's `/api` rather than duplicating the state.

**The shape to try first: a panel that is a view over `/api`**

Rather than feeding a panel from the JavaScript side, have it fetch the endpoints directly. The panel then holds no state of its own, there is one source of truth for what the daemon did, and `/api` gets a second consumer keeping it honest. It also makes the panel small enough to be worth building, which the "delete the HTML page" argument on its own no longer is.

Two things in the way, neither investigated:

- **Cross-origin.** DevTools serves its UI from its own port (9999 by default), so a panel fetching `http://127.0.0.1:9014/api` is a cross-origin request, and `Debug.fs` sets no CORS headers. Either the endpoints grow `Access-Control-Allow-Origin` (cheap, and the server is loopback-only and already gated behind `debug`), or the DevTools node side proxies them, which also solves the next point.
- **Finding the port.** A browser cannot read `$TMPDIR/vite-plugin-fable/daemon-<pid>.json`, and the port is whatever `VITE_PLUGIN_FABLE_DEBUG_PORT` said. Something node-side has to hand the panel its URL, and the plugin is the only thing that knows.

What this shape would still not show is the plugin's own view: which files a hot-update batch recompiled, and the coalescing behaviour around it. That is plugin-side state that never reaches the daemon, so a panel wanting it needs both sources after all.

**How it actually runs**, which is not shaped like `vite-plugin-inspect`

DevTools is a **separate CLI on its own port**, not a route on your dev server. `vite-devtools` (shipped as a bin by `@vitejs/devtools`) reads the Vite config, starts on port 9999 by default and opens a browser. The embedded `DevTools()` plugin does mount a `/__devtools/` base on the dev server (connection meta, SSE RPC, viewer assets), but injects nothing visible into the app page. It works on Bun: since devframe 0.9.9 the RPC transport adapts to the runtime, and under Vite it falls back to SSE.

**Unverified, and the reason not to start yet**

Whether custom panels are supported at all. `@vitejs/devtools-kit` and `@vitejs/devtools-rpc` exist and are shaped like something third-party panels would use, but the published docs cover installation only, and 0.6.2 is described as "early preview".

**To do**

- [ ] Confirm whether custom panels are supported, and how, before building anything against an early-preview tool.
- [x] Track [devframes/devframe#317](https://github.com/devframes/devframe/issues/317); fixed in devframe 0.9.9 ([#322](https://github.com/devframes/devframe/pull/322)), which binds a runtime-appropriate RPC transport on Bun/Deno. The `crossws` patch is removed.
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

## 4. Shipping the daemon: what is left

The `postinstall` half of this landed: `vite-plugin-fable` ships a framework-dependent publish of `Fable.Daemon` in `bin/`, there is no lifecycle script, and `--ignore-scripts` installs work. The .NET SDK is still required, because cracking shells out to `dotnet msbuild` (`Fable.Daemon/MSBuild.fs:18`, and Fable's own `MSBuildCrackerResolver` does the same). Roughly 14 MB compressed, 35 MB unpacked.

Two things were considered and are worth not re-deriving.

**A separate `@fable-org/fable-daemon` package: mostly moot now**

The plan was a prebuilt daemon as its own package, a plain dependency pinned to the exact plugin version, so the lockfile made plugin/daemon skew structurally impossible. Bundling gives that for free, since they are one package, so the remaining argument is only about not re-downloading the bits on every plugin patch release, which npm's tarball-per-version model does not avoid anyway. Revisit only if the per-RID split below happens, where a scope for platform packages (`esbuild` + `@esbuild/darwin-arm64`, `rolldown` + `@rolldown/binding-darwin-arm64`) is the ecosystem convention. `@fable-org` already exists on npm, so `@fable-org/fable-daemon-<rid>` is available; coordinate with the Fable org before claiming it, and pick a name that does not mention Vite, since the daemon is not Vite-specific.

**Deferred: per-RID ReadyToRun packages**

What ships is portable IL, so the JIT cost the ReadyToRun build avoided is now paid on every `vite dev` start and every `vite build`. Measured on `sample-project`, warm caches:

| variant                                                 | size  | runs                       |
| ------------------------------------------------------- | ----- | -------------------------- |
| ReadyToRun (`--ucr -p:PublishReadyToRun=true`, per-RID) | 77 MB | 1.41s, 1.41s, 1.40s, 1.42s |
| portable IL (framework-dependent, what ships)           | 33 MB | 2.15s, 2.17s, 2.15s, 2.13s |

Not acted on, for two reasons that still hold:

- **The 35% flatters ReadyToRun.** `sample-project` is five files. JIT cost is roughly fixed — the same FCS code paths get compiled regardless of project size — so on a real project the absolute delta stays near 0.7s while the percentage collapses. Re-measure against something like the telplin or fantomas-tools projects in `DebugTests.fs` before treating 35% as real.
- **Its value has already shrunk.** The dev server no longer blocks on the first compile, so the 0.74s is spent behind a server that is already listening rather than in front of a terminal with no URL.

Cost if it is ever taken up: five or so packages at 77 MB each to publish (a consumer downloads one), a per-RID CI matrix, `os`/`cpu` platform packages, a resolution shim on the JS side, and a fallback when no platform package matches.

**Also considered: a multi-RID .NET tool**

.NET 10 can publish a tool carrying several RuntimeIdentifiers with the CLI selecting one at run time, invoked via `dotnet tool exec` / `dnx` without a permanent install (verified working on SDK 10.0.400). See `baronfel/multi-rid-tool` for `PackAsTool` / `ToolCommandName` / `dotnet pack -p ToolType=<agnostic|specific|self-contained|trimmed|aot>`.

Passed over because the bits would then arrive on first `vite dev` rather than at install, shifting the failure mode for restricted networks from install time to first run. Worth revisiting only if the per-RID split happens, where the CLI doing RID selection beats maintaining `os`/`cpu` packages. Self-contained, trimmed and AOT are ruled out regardless: the SDK is present anyway, and FSharp.Compiler.Service is reflection-heavy enough that trimming would break things (`Caching.fableCompilerVersion` reads the Fable.Compiler version via reflection).

**To do**

- [ ] README: state that the .NET 10 SDK is required, and why. `docs/getting-started.md` says it; the README does not.

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
