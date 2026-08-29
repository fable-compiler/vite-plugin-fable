---
index: 4
categoryindex: 1
category: docs
---

# Technical implications

When working on this plugin, there are a lot of moving pieces involved:

- vite-plugin-fable
- Fable.Compiler
- the F# fork Fable uses

All these pieces of technologies need to be in tune before any of this can work.

## vite-plugin-fable

The combination of a thin JavaScript wrapped that communicates with a dotnet process via JSON-RPC.

### The plugin

This Vite plugin harnesses the power of both [Vite specific hooks](https://vitejs.dev/guide/api-plugin.html#rollup-plugin-compatibility) and [Rollup hooks](https://rollupjs.org/plugin-development/).  
While both offer documentation, occasionally you might find it doesn't cover every scenario.
In those moments, a bit of trial and error becomes part of the adventure.
Alternatively, reaching out to the vibrant community on the Vite Discord can shine a light on the trickier parts.

### Fable.Daemon

The `dotnet` process leverages [StreamJsonRpc](https://github.com/Microsoft/vs-streamjsonrpc) alongside a mailbox processor for managing incoming requests.
The art lies in ensuring a proper response is always dispatched, even when facing the unexpected, like an issue in the user code.

## Fable.Compiler

`Fable.Compiler` emerges from a [carve-out](https://github.com/fable-compiler/Fable/pull/3656) of the codebase originally part of `Fable.Cli`.
It's worth noting that this NuGet package is in its early stages, crafted specifically for this explorative phase.
Navigating this terrain often involves simultaneous tweaks in both this project and `Fable.Daemon`.
To streamline this process, you can set `<UseLocalFableCompiler>true</UseLocalFableCompiler>` in the `Directory.Build.props` file, ensuring a smoother development experience.

### Trivia

- The plugin is TypeScript, type-checked against Vite's own `.d.ts`, and ships as `dist/`.
- Transpiled F# files for your own sources are kept in memory rather than written next to
  them. Compiled `fable_modules` output is the exception: it is cached under `obj/` so
  dependencies survive a restart.
- The transpiled JavaScript maintains references to the original F# files for imports, e.g., `import { Foo } from "./Bar.fs"`. This approach informs the Vite plugin that it's working with virtual files.
- Presently, only F# is supported, mainly due to the lack of a pressing need to incorporate other languages.

## NCave's F# Fork

Fable takes a unique path by not relying on the official releases of the [F# compiler](https://fsharp.github.io/fsharp-compiler-docs/), sidestepping what you might have installed in your SDK or found on NuGet.
Instead, it embraces [a specialized fork](https://github.com/ncave/fsharp/pull/2) crafted by an enigmatic figure known as NCave.
A little piece of fun trivia: the true identity of NCave remains a mystery, and there's a notable silence between the Fable and F# teams.
So, if you had preconceived notions about their collaboration, consider this a gentle nudge towards the bleak reality.

On a personal note, I've contributed [a handful of PRs](https://github.com/ncave/fsharp/pulls?q=is%3Apr+is%3Aclosed+author%3Anojaf) to NCave's fork, specifically to harness the graph-based checking algorithm.

## Conclusion

Contributing to this project presents a significant challenge, necessitating a broad understanding of the aforementioned topics.
This isn't by design but simply reflects the complex nature of the project.

[Next]({{fsdocs-next-page-link}})
