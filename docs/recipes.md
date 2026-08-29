---
index: 5
categoryindex: 1
category: docs
---

# Recipes

There are a few things you can configure in the plugin configuration.

## Plugin options

Every option the plugin accepts. All of them are optional.

| Option                    | Type                                                    | Default                            | What it does                                                                                                                                  |
| ------------------------- | ------------------------------------------------------- | ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `fsproj`                  | `string`                                                | the single `.fsproj` in the root   | The entry project. See [Alternative fsproj](#Alternative-fsproj).                                                                             |
| `configuration`           | <code>"Debug" &#124; "Release"</code>                   | `Release` on build, `Debug` on dev | MSBuild configuration. See [Debug or Release](#Debug-or-Release).                                                                             |
| `jsx`                     | <code>"automatic" &#124; "transform" &#124; null</code> | `null`                             | Transform JSX that Fable emitted. See [Fable.Core.JSX](#Fable-Core-JSX).                                                                      |
| `noReflection`            | `boolean`                                               | `false`                            | Passed to Fable. Skips emitting reflection info, which produces smaller output.                                                               |
| `exclude`                 | `string[]`                                              | `[]`                               | Passed to Fable. Excludes assemblies from compilation, typically Fable plugins.                                                               |
| `debug`                   | `boolean`                                               | `false`                            | Print what the plugin is doing, and start the daemon's debug server. See [Seeing what the plugin is doing](#Seeing-what-the-plugin-is-doing). |
| `fableModulesDiagnostics` | `boolean`                                               | `false`                            | Report diagnostics for files under `fable_modules`. See [Diagnostics from restored packages](#Diagnostics-from-restored-packages).            |

`noReflection` and `exclude` are handed to Fable.Compiler unchanged; they mean what they mean for
the `dotnet fable` CLI. Changing either invalidates the plugin's build caches, so you do not need
to clear `obj/` yourself.

Unknown or badly typed options are rejected when the config loads, so a misspelled one fails with
a message rather than being quietly ignored:

```
vite-plugin-fable: unknown option "noRefleciton". Did you mean "noReflection"?
Known options: fsproj, jsx, noReflection, exclude, configuration, debug, fableModulesDiagnostics.
```

If you write your Vite config in TypeScript you get the same feedback in the editor. The plugin
exports `PluginOptions` and `FableConfiguration` for when you want to name them:

```ts
import type { FableConfiguration, PluginOptions } from "vite-plugin-fable";
```

## Diagnostics from restored packages

Fable restores the sources of the packages your project depends on into `fable_modules` and
compiles them along with your own files, so their warnings arrive with yours. They are about code
you did not write and cannot edit, so the plugin drops them.

`fableModulesDiagnostics: true` reports them again. It is a debugging aid, for when a package
itself is what looks broken:

```js
fable({ fableModulesDiagnostics: true });
```

The option covers errors as well as warnings. With it off, a package whose sources fail to compile
takes the only signal with it: nothing is printed and `vite build` exits 0, even though Fable
emitted nothing usable for that file. If a build succeeds and the app is broken in a way that
points at a package, turn this on first.

## Seeing what the plugin is doing

By default the plugin prints one line per compile, plus any diagnostics and errors:

```text
  VITE v8.2.2  ready in 376 ms

  ➜  Local:   http://localhost:4000/
  3:42:12 PM [vite] [fable] compiled App.fsproj in 1.53s
```

When that is not enough, turn on `debug`:

```js
export default defineConfig({
  plugins: [fable({ debug: true })],
});
```

That adds every hook the plugin runs, every file it transforms, where it resolved `fable-library`,
the cracking and type-checking timings, and whatever the daemon writes to stderr. Paths stay
relative to the Vite root, so they are readable at a glance.

`VITE_PLUGIN_FABLE_DEBUG=1` does the same without touching the config, and additionally starts the
daemon's own log viewer on <http://localhost:9014>. That is the one thing the `debug` option cannot
do, because the viewer runs inside the compiler process rather than the plugin.

## Alternative fsproj

By default, the plugin will look for a single `.fsproj` file inside your Vite [root](https://vite.dev/config/shared-options.html#root), which is the project folder unless you changed it.
If you deviate from this setup you can specify the entry `fsproj` file:

```js
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fsproj = path.join(currentDir, "fsharp/FantomasTools.fsproj");

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable({ fsproj })],
});
```

## Debug or Release

The plugin compiles your F# in `Release` for `vite build` and `Debug` for `vite dev`. That follows
the command, not `--mode`, so `vite build --mode staging` still compiles `Release`.

Override it when you need the other one — a production bundle with assertions left in, say:

```js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

// https://vite.dev/config/
export default defineConfig({
  plugins: [fable({ configuration: "Debug" })],
});
```

## Using React

There are a couple of ways to deal with React and JSX in Fable.

⚠️ When using the `vite-plugin-fable` in combination with `@vitejs/plugin-react`, you do want to specify the fable plugin first! ⚠️

### Feliz.CompilerPlugins

If you are using [Feliz.CompilerPlugins](https://www.nuget.org/packages/Feliz.CompilerPlugins), Fable output React Classic Runtime code.  
Stuff like `React.createElement`. You will need to tailor your `@vitejs/plugin-react` accordingly:

```js
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable(), react({ include: /\.(fs|js|jsx|ts|tsx)$/, jsxRuntime: "classic" })],
});
```

Note that the `react` plugin will only apply the fast-refresh wrapper when you specify the `fs` extension in the `include`.

### Fable.Core.JSX

Fable can also produce JSX (see [blog](https://fable.io/blog/2022/2022-10-12-react-jsx.html)). Tell
the `fable` plugin to transform it, and tell `@vitejs/plugin-react` that `.fs` counts as a React
file:

```js
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable({ jsx: "automatic" }), react({ include: /\.fs$/ })],
});
```

The two options do different jobs, and it is worth knowing which is which.

**The JSX transform is `fable({ jsx })`.** It has to be the plugin that does it. Vite's
built-in `vite:oxc` forces `lang: "js"` for any id whose extension is not a JavaScript one, which
disables JSX parsing — so JSX left inside a `.fs` module is a parse error there, not something Vite
can pick up. That is also why there is no `preserve` value: with `@vitejs/plugin-react` in the
config the module fails to parse, and without it Vite's import analysis rejects it instead. The
plugin refuses `preserve` when the config loads rather than letting it fail later.

**Fast Refresh is `react({ include: /\.fs$/ })`.** It does nothing for the JSX
transform. Without it an F# component still renders, but every edit reloads the page instead of
updating in place. The plugin warns when it spots this combination, so you do not have to notice it
yourself:

```text
  [fable]: configResolved: @vitejs/plugin-react will not apply Fast Refresh to .fs files, so
  editing an F# component reloads the page instead of updating in place. Add the extension to
  its filter: react({ include: /\.fs$/ }).
```

### React Compiler

`@vitejs/plugin-react` 6 can run the React Compiler through `oxc-transform-react`, and it works on
Fable's output. Install the optional `oxc-transform-react` package and turn it on:

```js
plugins: [fable({ jsx: "automatic" }), react({ include: /\.fs$/, compiler: true })];
```

The compiler memoizes F# components the same way it does JavaScript ones — a `Component.fs` picks
up `react/compiler-runtime` and a `_c(n)` cache — and Fast Refresh keeps working. It is also the
more robust setup of the two: with `compiler: true` the refresh transform is applied explicitly to
everything the `include` matches, whereas otherwise Vite only applies it to modules whose emitted
code imports `react/jsx-runtime`. A component that compiles to no JSX at all therefore keeps Fast
Refresh under `compiler: true` and loses it without.

### Plain Fable.React

If you are for some reason using [Fable.React](https://www.nuget.org/packages/Fable.React) without [Feliz.CompilerPlugins](https://www.nuget.org/packages/Feliz.CompilerPlugins), there is one gotcha to get fast refresh working.

`Fable.React` will use the [old JSX output](https://legacy.reactjs.org/blog/2020/09/22/introducing-the-new-jsx-transform.html).
The `@vitejs/plugin-react` needs to respect that in the configuration:

```js
import react from "@vitejs/plugin-react";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable(), react({ include: /\.(fs|js|jsx|ts|tsx)$/, jsxRuntime: "classic" })],
});
```

However, this is not enough for the fast refresh wrapper to be added.  
⚠️ The React plugin will **specifically** look for a `import React from "react"` statement.

```fsharp
module Component

open Fable.React
open Fable.React.Props

// Super important for fast refresh to work in "classic" mode.
// The [<ReactComponent>] attribute from Feliz.CompilerPlugin will add for you.
// But here, we are not using that and we need to add this ourselves.
Fable.Core.JsInterop.emitJsStatement () "import React from \"react\""

let App () =
    let counterHook = Hooks.useState(0)

    div [] [
        h1 [] [ str "Hey you!" ]
        p [] [
            ofInt counterHook.current
        ]
        button [ OnClick (fun _ -> counterHook.update(fun c -> c + 1))] [
            str "Increase"
        ]
    ]
```

[Next]({{fsdocs-next-page-link}})
