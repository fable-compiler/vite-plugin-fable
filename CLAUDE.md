# Notes for agents

Only things that are slow or impossible to work out from the repo itself. Everything derivable —
scripts, dependencies, layout — read from the files.

Use Bun for everything, including ad-hoc registry lookups (`bun pm view`, not `npm info`). The
sample deliberately runs on the Bun runtime rather than Node — that is the only place the plugin is
exercised under Bun, which matters because it spawns a child process and normalises paths.

## Check Vite and Fable behaviour against the source, not from memory

Both are often checked out as **siblings of this repo**, so `../vite` and `../Fable` resolve from
the repo root. That is a local convention rather than a guarantee — a fresh clone or CI will not
have them. Test for the directory before relying on it, and if it is missing, say so rather than
answering from memory.

When they are there, consult them freely; it is the fastest way to settle a question about someone
else's contract:

- `../vite` — the exact version in the workspace catalog. `packages/vite/src/node` answers
  hook-contract questions definitively. Several plugin bugs were misdiagnosed from assumptions
  about Vite that the source contradicted, and several fixes in the git history cite line numbers
  from it.
- `../Fable` — Fable 5.14. Notably `src/Fable.Compiler/Library.fs`, where
  `FileWriter.AddSourceMapping` is a no-op, so `CliArgs.SourceMaps` does nothing.

## Seeing what the plugin actually emitted, without a browser

`bun run build` in `sample-project` writes `.vite-inspect/reports/` (gitignored):

- `modules.json` — graph, deps, importers, which plugins transformed each module
- `transforms/*.json` — per module, the `vite-plugin-fable` step is the JavaScript it emitted. That
  is its `load`, so there is no `__load__` step holding the F# source; the F# is the file on disk

Use this instead of guessing about transform output. The Vite DevTools panel shows the same thing
but is injected client-side behind a `#devframe_otp=` fragment, so it is invisible to `curl` —
the served HTML is byte-identical with and without it. Do not conclude DevTools is broken from
HTTP probes; that mistake has already been made twice.

## Asking the daemon what it is doing

With `fable({ debug: true })` or `VITE_PLUGIN_FABLE_DEBUG=1`, the daemon serves JSON on
`http://127.0.0.1:9014/api` (move it with `VITE_PLUGIN_FABLE_DEBUG_PORT`). `curl .../api` lists
every endpoint. It is read-only and answers from a snapshot the message loop publishes, so it
never blocks behind a compile and never changes what it is reporting on.

Reach for it before reconstructing anything from the outside:

- `/api/files?path=Greeting.fs` is the JavaScript Fable emitted for one file, without a build or
  `.vite-inspect/`. `/api/files` lists them with sizes.
- `/api/diagnostics` is unfiltered, so it still shows the `fable_modules` ones the plugin drops.
- `/api/cache` says whether the design time build was reused and, if not, which input changed.
- `/api/project` is the crack result: source files in compile order, watched MSBuild inputs,
  `?include=args,references` for the compiler arguments.
- `/api/requests` is the last 100 JSON-RPC calls with durations, `/api/logs` the daemon's log as
  JSON with a `nextSince` cursor.

Every response carries a `revision` that increments per served message, which is how you tell
whether what you are reading already includes the edit you just made. A daemon you did not start
announces itself in `$TMPDIR/vite-plugin-fable/daemon-<pid>.json`.

## Traps that have cost real time

- **Vite's SPA fallback returns 200 with `index.html` for any unmatched path.** A status code
  proves nothing when probing whether a route exists — compare the body against a deliberately
  nonsense path.
- **`bunfig.toml` is read from the directory a command runs in and does not propagate.** That is
  why `sample-project` has its own with `[run] bun = true`; a `[run]` block at the repo root looks
  right and silently does nothing.
- **`bun test`: `expect(...).rejects.toThrow()` is typed `void`.** Awaiting it does nothing, and
  bun asserts it without the await. Also give any plugin-context stub a real `error()` that
  throws — otherwise `this.error(...)` raises a `TypeError` whose message quotes the call
  expression, which can match a `toThrow` regex and pass for the wrong reason.
- **The stub daemon must return files downstream of the edited one**, because the real daemon
  does. Tests that only return the requested file agree with bugs the plugin no longer has.
- **On Bun, the embedded DevTools RPC runs over SSE, not WebSocket.** Vite owns the `node:http`
  server, and devframe cannot re-host a foreign server on a native runtime, so it advertises
  `"backend":"sse"` in `/__devtools/__connection.json` and the RPC rides `/__devtools/__sse`. The
  `crossws/adapters/bun` transport only binds in servers devframe owns (hub CLI, sidecar), so a
  `ws://localhost:4000/__ws` probe timing out proves nothing. Routes sit under `/__devtools/`, and
  Vite's SPA fallback answers 200 with `index.html` for wrong paths. Fixed by devframe 0.9.9
  (devframes/devframe#322); before that it needed `patches/crossws@0.4.12.patch`, since removed.
