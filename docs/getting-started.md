---
index: 2
categoryindex: 1
category: docs
---

# Getting started

## Create a new Vite project

First you need a new Vite project:

<vpf-command npm="npm create vite@latest" bun="bun create vite"></vpf-command>

## Add the plugin

Next, you need to install the `vite-plugin-fable` package:

<vpf-command npm="npm install -D vite-plugin-fable" bun="bun install -D --trust vite-plugin-fable"></vpf-command>

It is important that the _post-install script_ of the plugin did run. The first time this runs, it can take some time.

If for some reason it didn't run, please manually invoke:

<vpf-command npm="npm --prefix ./node_modules/vite-plugin-fable/ run postinstall" bun="bun run --cwd ./node_modules/vite-plugin-fable/ postinstall"></vpf-command>

_Note: you don't need to install Fable as a dotnet tool when using this plugin._

## Update your Vite configuration

Lastly, we need to tell Vite to use our plugin:

```js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable()],
});
```

⚠️ Depending on your use-case, you may need to further tweak your configuration.  
Check out the [recipes](./recipes.md) page for more tips.

## Start Vite

We can now start the Vite dev server:

<vpf-command npm="npm run dev" bun="bunx --bun vite"></vpf-command>

The plugin keeps quiet and prints one line when it has compiled:

```text
  VITE v8.2.2  ready in 376 ms

  ➜  Local:   http://localhost:5173/
  12:32:44 PM [vite] [fable] compiled App.fsproj in 1.53s
```

If you need to see more than that, the [Debugging](./debug.md) page covers the `debug` option.

And now we can import our code from F# in our `index.html`:

```html
<script type="module">
  import "/App.fs";
</script>
```

⚠️ We cannot use `<script type="module" src="/App.fs"></script>` because Vite won't recognize the `.fs` extension outside the module resolution.
See [vitejs/vite#9981](https://github.com/vitejs/vite/pull/9981)

[Next]({{fsdocs-next-page-link}})
