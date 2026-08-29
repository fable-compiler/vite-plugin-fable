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

## `patches/`

`crossws@0.4.12.patch` drops a guard that refuses to run crossws's Node WebSocket adapter under
Bun. Without it, Vite DevTools cannot start on the Bun runtime. Pinned to that exact version, so a
bump needs it rebased; upstream fix tracked at devframes/devframe#317.
