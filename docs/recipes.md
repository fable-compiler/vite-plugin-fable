---
index: 5
categoryindex: 1
category: docs
---

# Recipes

There are a few things you can configure in the plugin configuration.

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

Fable can also produce JSX (see [blog](https://fable.io/blog/2022/2022-10-12-react-jsx.html)). In this case, you need to tell the `fable` plugin it should transform the JSX using Babel.
The `@vitejs/plugin-react` won't interact with any `.fs` files, so that transformation needs to happen in the `fable` plugin:

```js
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    // See `jsx` option from https://esbuild.github.io/api/#transformation
    fable({ jsx: "automatic" }),
    react({ include: /\.(fs|js|jsx|ts|tsx)$/ }),
  ],
});
```

Note that you will still need to tweak the `react` plugin with `include` to enable the fast refresh transformation.

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
