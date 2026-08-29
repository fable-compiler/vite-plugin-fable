---
index: 3
categoryindex: 1
category: docs
---

# How does this work?

Everything starts with the Vite dev server. A Vite plugin claims `.fs` files, and behind it a
long-lived `dotnet` process — the daemon — does the actual F# compilation and answers over
[JSON RPC](https://www.jsonrpc.org/).

```js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

// https://vite.dev/config/
export default defineConfig({
  plugins: [fable()],
});
```

## Index.html

The entry point has to be imported from a module script rather than loaded as one:

```html
<body>
  <script type="module">
    import "/Library.fs";
  </script>
</body>
```

`<script type="module" src="/Library.fs">` still does not work, and it is worth knowing why rather
than treating it as folklore. Vite only runs its transform pipeline for requests it recognises as
JavaScript, and `isJSRequest` tests the URL against a fixed list of extensions that does not include
`.fs`. A bare request for `/Library.fs` therefore skips the pipeline and the browser receives F#
source. An `import` from JavaScript is different: Vite appends `?import` to the URL, which does
reach the pipeline. That is why the indirection is needed — see
[vitejs/vite#9981](https://github.com/vitejs/vite/pull/9981).

## Starting Vite

    npm run dev

<div class="mermaid">
sequenceDiagram
    participant Vite
    participant Plugin
    participant Daemon as dotnet daemon
    Vite->>Plugin: configResolved
    Vite->>Plugin: configureServer
    Plugin->>Daemon: spawn
    Plugin-->>Vite: returns immediately
    Note over Vite: server listens, URL printed
    Plugin->>Daemon: fable/project-changed
    Daemon-->>Plugin: source files, diagnostics, MSBuild inputs
    Plugin->>Daemon: fable/initial-compile
    Daemon-->>Plugin: JavaScript per F# file
    Vite->>Plugin: load (per .fs request)
</div>

### Starting the daemon

`configureServer` spawns the daemon and kicks off the first compile, but **does not wait for it**.
That matters: Vite awaits `buildStart` before `httpServer.listen`, so cracking the project there
would hold the dev server off its port for the whole first compile — no URL, no error overlay,
nothing to look at. `configureServer` runs earlier and nothing awaits what it starts, so the server
boots straight away.

`vite build` is the other way round. There the work happens in `buildStart` and blocks, because
nothing should be bundled before the F# has compiled.

### Cracking and compiling

`fable/project-changed` resolves NuGet packages, composes the
[FSharpProjectOptions](https://fsharp.github.io/fsharp-compiler-docs/reference/fsharp-compiler-codeanalysis-fsharpprojectoptions.html)
and type-checks the project. `fable/initial-compile` then transpiles every source file. Both run
inside the daemon on [Fable.Compiler](https://github.com/fable-compiler/Fable), the same code
`dotnet fable` uses.

The daemon also reports which MSBuild files the project depends on. The plugin watches those, so a
change to an `fsproj` or a `Directory.Build.props` triggers a full re-crack rather than an
incremental compile.

The JavaScript for your own source files is held in memory and served from `load`; nothing is
written next to your `.fs` files. Compiled `fable_modules` output is the exception — it is cached to
disk under `obj/` so dependencies do not have to be recompiled on every start. That cache is keyed
on everything that changes what Fable emits, including the plugin options.

### Serving a file

`load` looks the requested id up in the compiled output and returns the JavaScript, so Vite never
reads the `.fs` file it would otherwise have to throw away. It waits on the first compile before
answering, which is the wait that used to sit in `buildStart` — so a request that arrives while the
project is still cracking blocks, and a failure surfaces in the browser overlay instead of only in
the terminal.

An `.fs` file Fable did not compile is an error, in dev as well as in a build. Answering with
nothing would leave Vite to read the F# and hand it to the JavaScript parser, and the syntax error
that follows points at `module Foo` rather than at the file missing from your `fsproj`.

If Fable emitted JSX, the plugin transforms it here too. It has to: Vite's own oxc pass forces
`lang: "js"` for a non-JavaScript extension, which disables JSX parsing. See
[Fable.Core.JSX](./recipes.html#Fable-Core-JSX).

## Editing a file

<div class="mermaid">
sequenceDiagram
    participant Vite
    participant Plugin
    participant Daemon as dotnet daemon
    Vite->>Plugin: hotUpdate (client environment)
    Plugin->>Daemon: fable/compile
    Daemon-->>Plugin: changed file, plus everything downstream
    Plugin-->>Vite: modules to update
    Vite->>Plugin: hotUpdate (ssr environment)
    Note over Plugin: same change, reuses the compile
</div>

Vite decides what changed, not the plugin — a key difference from how `dotnet fable` works. When a
`.fs` file changes, `hotUpdate` asks the daemon to recompile it. Fable uses
[graph-based checking](https://devblogs.microsoft.com/dotnet/a-new-fsharp-compiler-feature-graphbased-typechecking/)
to work out what else needs re-evaluating, so the response contains the edited file **and every file
downstream of it**.

Three things about that path are easy to get wrong, and all three cost real time to find.

**Not everything the daemon returns has actually changed.** Most of the downstream output is byte
for byte what was already served. Reporting all of it would drag modules that cannot accept a hot
update into the update, and one dead end turns the whole thing into a page reload. The plugin
compares each file against what it already holds and reports only the ones that really differ.

**Changes are batched.** Edits within a 50ms window are collected and compiled together, and each
batch carries its own promise, so a caller always learns the result of the compile its own file went
into. Sharing one promise across batches meant a file edited during a slow compile was answered by
the previous batch's diagnostics.

**`hotUpdate` runs once per environment.** Vite computes one timestamp per file change and then
calls the hook for every environment — `client` and `ssr` by default — one after another. The
coalescing window cannot merge those, because the first compile has already finished by the time the
second call arrives. Without deduplication a dev server compiles every edit twice. The plugin keys
the in-flight compile on the timestamp Vite already computed, so one filesystem change means one
compile, while each environment still resolves the result against its own module graph.

A signature file maps to the implementation it describes: editing `Foo.fsi` compiles `Foo.fsi`, but
the browser imports `Foo.fs`, so that is the module the plugin invalidates.

Under `vite build --watch` there is no HMR pipeline, so `watchChange` handles the same job.

[Next]({{fsdocs-next-page-link}})
