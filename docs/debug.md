---
index: 6
categoryindex: 1
category: docs
---

# Debugging

This plugin is a wonderful piece until it no longer works. Then it just sucks, and who knows where in the rabbit hole the problem lies.  
The biggest fear this plugin can face is when the dotnet process no longer responds.
Even if it is not able to process the incoming request, it should always be able to produce a response.

## What the plugin prints

By default the plugin is quiet: one line per compile, plus diagnostics and errors.

```text
  3:42:12 PM [vite] [fable] compiled App.fsproj in 1.53s
  3:42:29 PM [vite] [fable] compiled Greeting.fs in 0.05s
```

When that is not enough, turn on the `debug` option:

```js
export default defineConfig({
  plugins: [fable({ debug: true })],
});
```

That adds every hook the plugin runs, every file it transforms, where it resolved `fable-library`,
the cracking and type-checking timings, and whatever the daemon writes to stderr. Paths are printed
relative to the Vite root.

## Debug viewer

The `debug` option covers the JavaScript side. To see what happened inside the dotnet process, set
the `VITE_PLUGIN_FABLE_DEBUG` environment variable before running Vite. It turns on the plugin's
debug output as well, and additionally starts the daemon's own log viewer — the one thing the option
cannot do, because that viewer runs inside the compiler process rather than the plugin.

```bash
# bash
export VITE_PLUGIN_FABLE_DEBUG=1
```

```pwsh
# PowerShell
$env:VITE_PLUGIN_FABLE_DEBUG=1
```

When running Vite, you should see this among the debug output:

```text
  3:43:20 PM [vite] [fable] daemon: log viewer at http://localhost:9014
```

Opening [http://localhost:9014](http://localhost:9014) will display a list of log messages that happened inside the dotnet process:

![vite-plugin-fable debug tool](./img/debug-tool.png)

It should receive new log messages via web sockets after the initial page load.

[Next]({{fsdocs-next-page-link}})
