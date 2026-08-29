# Vite plugin for Fable

[![NPM Version](https://img.shields.io/npm/v/vite-plugin-fable)](https://www.npmjs.com/package/vite-plugin-fable)

<img alt="vite-plugin-fable logo" src="https://github.com/fable-compiler/vite-plugin-fable/blob/main/docs/img/logo.png?raw=true" height="300"></img>

> [!IMPORTANT]
> This project is up for adoption. I'm looking for eager people to maintain this.<br>Please open a [discussion](https://github.com/fable-compiler/vite-plugin-fable/discussions) if you are interested!

Compile [F#](https://fsharp.org/) with [Fable](https://fable.io/) from inside [Vite](https://vite.dev/), so a `.fs` file is just another module Vite can import.

The usual setup puts Fable in front of your dev server (`dotnet fable watch --run vite`). This plugin does not. You run `vite`, and F# is compiled on demand and updated over HMR, the same way Vite treats TypeScript, JSX or Sass.

## Requirements

- The **.NET 10 SDK** on your `PATH`. Reading your `.fsproj` means asking MSBuild about it. Check with `dotnet --version`.
- **Vite 8** (peer dependency).

You do not need Fable as a dotnet tool. The compiler ships prebuilt inside the package, so there is no post-install step and installing with `--ignore-scripts` is fine.

## Install

```bash
npm install -D vite-plugin-fable
bun install -D vite-plugin-fable
```

## Getting started

Add the plugin to your Vite config:

```js
// vite.config.js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

export default defineConfig({
  plugins: [fable()],
});
```

The plugin compiles the single `.fsproj` next to your Vite config. If there is more than one, point at the one you want with `fable({ fsproj: "./src/App.fsproj" })`.

Import your F# entry point as a module:

```html
<script type="module">
  import "/App.fs";
</script>
```

`<script type="module" src="/App.fs">` does not work: Vite only resolves the `.fs` extension inside module resolution. See [vitejs/vite#9981](https://github.com/vitejs/vite/pull/9981).

Now start Vite. The plugin stays quiet and prints one line per compile:

```text
  VITE v8.2.2  ready in 376 ms

  ➜  Local:   http://localhost:5173/
  12:32:44 PM [vite] [fable] compiled App.fsproj in 1.53s
```

### With React

The most common setup, using [Fable.Core.JSX](https://fable.io/blog/2022/2022-10-12-react-jsx.html):

```js
// vite.config.js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [fable({ jsx: "automatic" }), react({ include: /\.fs$/ })],
});
```

Two things to know about that line:

- **Order matters.** `fable` goes before `react`.
- **The two options do different jobs.** `fable({ jsx })` is what turns Fable's JSX into JavaScript; Vite cannot do it for a `.fs` module. `react({ include: /\.fs$/ })` is what makes `.fs` components Fast Refresh boundaries, so an edit updates in place instead of reloading the page.

Using [Feliz.CompilerPlugins](https://www.nuget.org/packages/Feliz.CompilerPlugins) or plain [Fable.React](https://www.nuget.org/packages/Fable.React) instead? Those emit classic-runtime React and need a different `react()` filter. The [recipes](https://fable.io/vite-plugin-fable/recipes.html) page has both, plus the React Compiler.

## Documentation

- [Getting started](https://fable.io/vite-plugin-fable/getting-started.html)
- [Plugin options](https://fable.io/vite-plugin-fable/recipes.html#Plugin-options), every option the plugin accepts
- [Recipes](https://fable.io/vite-plugin-fable/recipes.html), including React, an alternative `fsproj`, and Debug versus Release
- [Debugging](https://fable.io/vite-plugin-fable/debug.html), what the plugin prints and how to ask the daemon what it is doing
- [How does this work?](https://fable.io/vite-plugin-fable/how.html)
- [Changelog](https://github.com/fable-compiler/vite-plugin-fable/blob/main/CHANGELOG.md)

## Video

I talked a little bit about this project during this stream:

[![vite-plugin-fable stream](http://img.youtube.com/vi/nVpUaVFNpMk/maxresdefault.jpg)](https://youtu.be/mnqwwtSQfRU?si=VpDDv3SzHikXL5iu&t=141 "vite-plugin-fable")

## License

[Apache-2.0](https://github.com/fable-compiler/vite-plugin-fable/blob/main/LICENSE)
