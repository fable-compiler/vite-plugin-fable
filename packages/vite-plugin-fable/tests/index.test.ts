import { describe, expect, test } from "bun:test";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Logger, Plugin, ResolvedConfig } from "vite";
import { createFablePlugin } from "../src/index.js";
import { createStubDaemon, type StubDaemon, type StubDaemonOptions } from "./daemon.stub.js";
import type { Diagnostic, PluginOptions, ProjectRequest } from "../src/types.js";

const sampleProject: string = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../../sample-project",
);
const mathFs = `${sampleProject}/Math.fs`;
const libraryFs = `${sampleProject}/Library.fs`;
const appFsproj = `${sampleProject}/App.fsproj`;

/** A logger that swallows output, so the test runner stays readable. */
const silentLogger = {
  info: () => {},
  warn: () => {},
  error: () => {},
  warnOnce: () => {},
  clearScreen: () => {},
  hasErrorLogged: () => false,
  hasWarned: false,
} as unknown as Logger;

function resolvedConfig(overrides: Partial<ResolvedConfig> = {}): ResolvedConfig {
  return {
    logger: silentLogger,
    env: { MODE: "development" },
    command: "serve",
    root: sampleProject,
    configFile: `${sampleProject}/vite.config.js`,
    ...overrides,
  } as unknown as ResolvedConfig;
}

/** The slice of Vite's plugin context the hooks actually reach for. */
interface PluginContextStub {
  addWatchFile(id: string): void;
  /** Rollup's `this.error` throws, and the plugin relies on that to abort a build. */
  error(message: string): never;
}

/** The shape `handleHotUpdate` receives for each affected module. */
interface HotModule {
  id: string;
  importers: Set<unknown>;
}

/** The slice of `ViteDevServer` the `configureServer` hook reaches for. */
interface ServerStub {
  config: { root: string };
  watcher: { add(id: string): void };
}

/** Records what the plugin pushes to the browser, and what the module graph holds. */
interface EnvironmentStub {
  hot: { send(payload?: unknown): unknown };
  moduleGraph: { getModulesByFile(file: string): Set<HotModule> | undefined };
  sent: unknown[];
}

type LoadOutput = { code: string; map: { mappings: "" }; moduleType: "js" } | undefined;

interface Harness {
  plugin: Plugin;
  daemon: StubDaemon;
  watched: string[];
  /**
   * Runs the startup hooks as Vite would, and waits for the first compile. In dev that wait is the
   * test's, not the server's — see {@link Harness.boot}.
   */
  start(config?: ResolvedConfig): Promise<void>;
  /**
   * Runs the startup hooks and returns as soon as they do, without waiting for the first compile.
   * This is what Vite does before `httpServer.listen`.
   */
  boot(config?: ResolvedConfig): Promise<void>;
  /** Calls the `load` hook the way Vite's plugin container does. */
  load(id: string): Promise<LoadOutput>;
  /** Whether rolldown would call the `load` handler for this id at all. */
  loadFilterMatches(id: string): boolean;
  /** Calls the `watchChange` hook the way Vite's plugin container does. */
  watchChange(id: string): Promise<void>;
  /**
   * Calls `hotUpdate` the way Vite's HMR pipeline does. Vite computes one `timestamp` per file
   * change and then calls the hook once per environment with it, so tests can pass both.
   */
  hotUpdate(
    file: string,
    type?: "create" | "update" | "delete",
    options?: { timestamp?: number; environment?: string },
  ): Promise<HotModule[] | void>;
  /** Payloads the plugin pushed to the browser. */
  sent: unknown[];
  /** Modules the environment's graph will report per file. */
  graph: Map<string, HotModule>;
}

function harness(pluginOptions: PluginOptions = {}, stub: StubDaemonOptions = {}): Harness {
  const daemon: StubDaemon = createStubDaemon(stub);
  const watched: string[] = [];
  const plugin: Plugin = createFablePlugin(
    { fsproj: appFsproj, ...pluginOptions },
    (): StubDaemon => daemon,
  );

  const context: PluginContextStub = {
    addWatchFile: (id: string): void => {
      watched.push(id);
    },
    error: (message: string): never => {
      throw new Error(message);
    },
  };

  // In dev the plugin watches through the server's watcher rather than `this.addWatchFile`, so
  // both land in `watched` and a test does not have to know which hook ran.
  const server: ServerStub = {
    config: { root: sampleProject },
    watcher: {
      add: (id: string): void => {
        watched.push(id);
      },
    },
  };

  const graph: Map<string, HotModule> = new Map();
  const sent: unknown[] = [];
  const environment: EnvironmentStub = {
    hot: {
      send: (payload?: unknown): unknown => {
        sent.push(payload);
        return undefined;
      },
    },
    moduleGraph: {
      getModulesByFile: (file: string): Set<HotModule> | undefined => {
        const mod: HotModule | undefined = graph.get(file);
        return mod ? new Set([mod]) : undefined;
      },
    },
    sent,
  };

  return {
    plugin,
    daemon,
    watched,
    sent,
    graph,
    async hotUpdate(
      file: string,
      type: "create" | "update" | "delete" = "update",
      options: { timestamp?: number; environment?: string } = {},
    ): Promise<HotModule[] | void> {
      const modules: HotModule[] = graph.has(file) ? [graph.get(file)!] : [];
      return (plugin.hotUpdate as any).call(
        { environment: { ...environment, name: options.environment ?? "client" } },
        {
          type,
          file,
          timestamp: options.timestamp ?? Date.now(),
          modules,
          read: async (): Promise<string> => "",
          server: { environments: { client: environment } },
        },
      );
    },
    async boot(config = resolvedConfig()) {
      await (plugin.configResolved as any).call(context, config);
      // Vite calls `configureServer` (server/index.ts:1008) before `buildStart` (:1104), and only
      // for a dev server.
      if (config.command !== "build") {
        (plugin.configureServer as any).call(context, server);
      }
      await (plugin.buildStart as any).call(context, {});
    },
    async start(config = resolvedConfig()) {
      await this.boot(config);
      // A build has already compiled by the time `buildStart` returns; a dev server has not.
      if (config.command !== "build") await afterCoalescing();
    },
    async load(id: string): Promise<LoadOutput> {
      return (plugin.load as any).handler.call(context, id);
    },
    async watchChange(id: string): Promise<void> {
      return (plugin.watchChange as any).call(context, id, { event: "update" });
    },
    loadFilterMatches(id: string): boolean {
      const filter: { include: RegExp[]; exclude: RegExp[] } = (plugin.load as any).filter.id;
      return (
        filter.include.some((pattern: RegExp): boolean => pattern.test(id)) &&
        !filter.exclude.some((pattern: RegExp): boolean => pattern.test(id))
      );
    },
  };
}

/** A config for `vite build`, where failures should be fatal. */
function buildConfig(): ResolvedConfig {
  return resolvedConfig({ env: { MODE: "production" } as any, command: "build" as const });
}

/** An error-severity diagnostic pointing at `file`. */
function errorAt(file: string): Diagnostic {
  return {
    errorNumberText: "FS0001",
    message: "This expression was expected to have type int",
    range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 5 },
    severity: "Error",
    fileName: file,
  };
}

/** Captures everything the plugin sends to the Vite logger, colours stripped. */
function recordingConfig(lines: string[], overrides: Partial<ResolvedConfig> = {}): ResolvedConfig {
  const record: (message: string) => void = (message: string): void => {
    // eslint-disable-next-line no-control-regex
    lines.push(message.replace(/\u001b\[[0-9;]*m/g, ""));
  };
  return resolvedConfig({
    logger: { ...silentLogger, info: record, warn: record, error: record },
    ...overrides,
  } as unknown as Partial<ResolvedConfig>);
}

async function linesFrom(pluginOptions: PluginOptions, stub: StubDaemonOptions): Promise<string[]> {
  const lines: string[] = [];
  const h: Harness = harness(pluginOptions, stub);
  await h.start(recordingConfig(lines));
  return lines;
}

/** Waits past the plugin's 50ms coalescing window. */
function afterCoalescing(): Promise<void> {
  return new Promise((resolve: () => void) => setTimeout(resolve, 120));
}

describe("configResolved", () => {
  test("uses the fsproj given in the plugin options", async () => {
    const h: Harness = harness();
    await h.start();
    expect(h.daemon.projectChangedCalls[0].project).toBe(appFsproj);
  });

  test("finds the fsproj in the Vite root when no option is given", async () => {
    const daemon: StubDaemon = createStubDaemon({});
    // Deliberately not the harness: it always passes an explicit `fsproj`.
    const plugin: Plugin = createFablePlugin({}, (): StubDaemon => daemon);
    const context: PluginContextStub = {
      addWatchFile: (): void => {},
      error: (message: string): never => {
        throw new Error(message);
      },
    };
    await (plugin.configResolved as any).call(context, resolvedConfig());
    (plugin.configureServer as any).call(context, {
      config: { root: sampleProject },
      watcher: { add: (): void => {} },
    });
    await (plugin.buildStart as any).call(context, {});
    await afterCoalescing();
    expect(daemon.projectChangedCalls[0].project).toBe(appFsproj);
  });

  test("fails a build when no fsproj can be found, rather than cracking null", async () => {
    const daemon: StubDaemon = createStubDaemon({});
    const plugin: Plugin = createFablePlugin({}, (): StubDaemon => daemon);
    const context: PluginContextStub = {
      addWatchFile: (): void => {},
      error: (message: string): never => {
        throw new Error(message);
      },
    };
    // `/tmp` has no .fsproj in it.
    const config: ResolvedConfig = {
      ...buildConfig(),
      root: "/tmp",
    } as unknown as ResolvedConfig;
    await (plugin.configResolved as any).call(context, config);

    expect((plugin.buildStart as any).call(context, {})).rejects.toThrow(/No .fsproj was found/);
    expect(daemon.projectChangedCalls).toHaveLength(0);
  });

  test("compiles Release for a production build and Debug otherwise", async () => {
    const release: Harness = harness();
    await release.start(
      resolvedConfig({ env: { MODE: "production" } as any, command: "build" as const }),
    );
    expect(release.daemon.projectChangedCalls[0].configuration).toBe("Release");

    const debug: Harness = harness();
    await debug.start();
    expect(debug.daemon.projectChangedCalls[0].configuration).toBe("Debug");
  });

  test("compiles Release for any build, not just --mode production", async () => {
    const h: Harness = harness();
    // A custom mode is still a production build; the F# should not be compiled Debug.
    await h.start(resolvedConfig({ env: { MODE: "staging" } as any, command: "build" as const }));
    expect(h.daemon.projectChangedCalls[0].configuration).toBe("Release");
  });

  test("honours an explicit configuration option over the command", async () => {
    const h: Harness = harness({ configuration: "Release" });
    await h.start();
    expect(h.daemon.projectChangedCalls[0].configuration).toBe("Release");
  });

  test("passes the Fable options through to the daemon", async () => {
    const h: Harness = harness({ noReflection: true, exclude: ["Foo.Bar"] });
    await h.start();
    const request: ProjectRequest = h.daemon.projectChangedCalls[0];
    expect(request.noReflection).toBe(true);
    expect(request.exclude).toEqual(["Foo.Bar"]);
  });
});

describe("buildStart", () => {
  test("cracks and compiles the project once", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const x = 1;" } },
    );
    await h.start();
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
    expect(h.daemon.initialCompileCalls).toBe(1);
  });

  test("watches source files and MSBuild dependencies during a build", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start(buildConfig());
    expect(h.watched).toContain(mathFs);
    expect(h.watched).toContain(appFsproj);
  });

  test("watches MSBuild dependencies outside the Vite root in dev", async () => {
    // A `Directory.Build.props` above the Vite root still decides what Fable compiles, and the dev
    // watcher does not cover it. Files under the root are already watched, so adding them again
    // would be work for nothing — this is the same rule Vite's own `ensureWatchedFile` applies.
    const outsideRoot = `${path.dirname(sampleProject)}/Directory.Build.props`;
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], dependentFiles: [appFsproj, outsideRoot] },
    );
    await h.start();
    expect(h.watched).toContain(outsideRoot);
    expect(h.watched).not.toContain(mathFs);
  });

  test("does not hold the dev server back while the first compile runs", async () => {
    // Vite awaits `buildStart` before `httpServer.listen`, so a slow crack there means no URL and
    // no overlay. The wait belongs in `load`, which is per request.
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const x = 1;" } },
    );
    const finishCrack: () => void = h.daemon.blockNextProjectChange();

    // Reaching the next line at all is the point: nothing has released the gate, so a `buildStart`
    // that waited for the crack would never return.
    await h.boot();
    await afterCoalescing();

    // The startup hooks are done while the daemon is still cracking.
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
    expect(h.daemon.initialCompileCalls).toBe(0);

    let served = false;
    const request: Promise<LoadOutput> = h.load(mathFs).then((r: LoadOutput) => {
      served = true;
      return r;
    });
    await afterCoalescing();
    expect(served).toBe(false);

    finishCrack();
    expect((await request)?.code).toBe("export const x = 1;");
  });

  test("fails the build when the daemon is unavailable", async () => {
    const h: Harness = harness();
    h.daemon.failWith(new Error("Could not spawn `dotnet`"));
    expect(h.start(buildConfig())).rejects.toThrow(/Could not spawn/);
  });

  test("fails the build on an F# error diagnostic", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(mathFs)] },
    );
    expect(h.start(buildConfig())).rejects.toThrow(/FS0001/);
  });

  test("serves in dev despite an F# error, so the overlay can show it", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(mathFs)] },
    );
    await h.start();
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
  });

  test("keeps building when the project only produces warnings", async () => {
    const warning: Diagnostic = { ...errorAt(mathFs), severity: "Warning" };
    const h: Harness = harness({}, { sourceFiles: [mathFs], projectDiagnostics: [warning] });
    await h.start(buildConfig());
    expect(h.daemon.initialCompileCalls).toBe(1);
  });
});

describe("hotUpdate across environments", () => {
  const project: StubDaemonOptions = {
    sourceFiles: [mathFs],
    compiled: { [mathFs]: "export const x = 1;" },
  };

  test("compiles once when Vite fans one change out to every environment", async () => {
    // `handleHMRUpdate` takes a single timestamp per file change and then calls `hotUpdate` for
    // every environment in `server.environments`. The calls arrive one after another, so the
    // coalescing window cannot merge them — without deduplication a dev server with a `ssr`
    // environment compiles every edit twice.
    const h: Harness = harness({}, project);
    await h.start();
    const timestamp: number = Date.now();

    await h.hotUpdate(mathFs, "update", { timestamp, environment: "client" });
    await h.hotUpdate(mathFs, "update", { timestamp, environment: "ssr" });

    expect(h.daemon.compileCalls).toHaveLength(1);
  });

  test("compiles once per change when two files change at the same time", async () => {
    // The watcher does not await `onFileChange`, so two saves interleave: the second file's
    // change is recorded while the first is still being fanned out to its other environments.
    // Remembering only the most recent change loses the first one, and its `ssr` call then
    // compiles it a second time.
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "export const x = 1;", [libraryFs]: "export const y = 2;" },
      },
    );
    await h.start();
    const math: number = Date.now();
    const library: number = math + 1;

    await Promise.all([
      h.hotUpdate(mathFs, "update", { timestamp: math, environment: "client" }),
      h.hotUpdate(libraryFs, "update", { timestamp: library, environment: "client" }),
    ]);
    await h.hotUpdate(mathFs, "update", { timestamp: math, environment: "ssr" });
    await h.hotUpdate(libraryFs, "update", { timestamp: library, environment: "ssr" });

    // Both files coalesced into one batch, and neither `ssr` call added another.
    expect(h.daemon.compileCalls).toHaveLength(1);
    expect(h.daemon.compileCalls[0]).toEqual([mathFs, libraryFs]);
  });

  test("cracks once when Vite reports an MSBuild change every way it can", async () => {
    // A dev server calls `watchChange` (once, for the client environment) on top of calling
    // `hotUpdate` for every environment, so a single touch of an fsproj arrived three times and
    // cracked the project three times over, each one a full design time build.
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs],
        dependentFiles: [appFsproj],
        compiled: { [mathFs]: "const v = 1;" },
      },
    );
    await h.start();
    const timestamp: number = Date.now();

    await h.watchChange(appFsproj);
    await h.hotUpdate(appFsproj, "update", { timestamp, environment: "client" });
    await h.hotUpdate(appFsproj, "update", { timestamp, environment: "ssr" });

    // One for the initial crack, one for the change.
    expect(h.daemon.projectChangedCalls).toHaveLength(2);
  });

  test("still re-cracks from watchChange during a build", async () => {
    // `hotUpdate` is dev-only, so under `vite build --watch` this hook is the only one that runs.
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start(buildConfig());

    await h.watchChange(appFsproj);

    expect(h.daemon.projectChangedCalls).toHaveLength(2);
  });

  test("still compiles again for a genuinely new change", async () => {
    const h: Harness = harness({}, project);
    await h.start();
    const first: number = Date.now();

    await h.hotUpdate(mathFs, "update", { timestamp: first, environment: "client" });
    await h.hotUpdate(mathFs, "update", { timestamp: first + 1, environment: "client" });

    expect(h.daemon.compileCalls).toHaveLength(2);
  });
});

describe("fable_modules diagnostics", () => {
  const packageFs = `${sampleProject}/fable_modules/Thoth.Json.10.2.0/Decode.fs`;
  const project: StubDaemonOptions = {
    sourceFiles: [mathFs],
    compiled: { [mathFs]: "export const x = 1;" },
  };

  test("says nothing about a warning in a restored package", async () => {
    // Nobody using the plugin wrote Decode.fs or can edit it, so its warnings are noise.
    const warning: Diagnostic = { ...errorAt(packageFs), severity: "Warning" };
    const lines: string[] = await linesFrom({}, { ...project, projectDiagnostics: [warning] });
    expect(lines.join("\n")).not.toContain("This expression was expected to have type int");
  });

  test("reports it once the option asks for it", async () => {
    const warning: Diagnostic = { ...errorAt(packageFs), severity: "Warning" };
    const lines: string[] = await linesFrom(
      { fableModulesDiagnostics: true },
      { ...project, projectDiagnostics: [warning] },
    );
    expect(lines.join("\n")).toContain("This expression was expected to have type int");
  });

  test("keeps reporting diagnostics on the project's own files", async () => {
    const lines: string[] = await linesFrom(
      {},
      { ...project, projectDiagnostics: [errorAt(mathFs)] },
    );
    expect(lines.join("\n")).toContain("This expression was expected to have type int");
  });

  test("drops the diagnostics a compile produced too, not only the crack's", async () => {
    const warning: Diagnostic = { ...errorAt(packageFs), severity: "Warning" };
    const lines: string[] = [];
    const h: Harness = harness({}, project);
    await h.start(recordingConfig(lines));
    h.daemon.setCompileResult({ [mathFs]: "export const x = 2;" }, [warning]);
    await h.hotUpdate(mathFs);

    expect(lines.join("\n")).not.toContain("This expression was expected to have type int");
  });

  test("an error in a restored package no longer fails the build", async () => {
    // The option owns errors as well as warnings, so with it off `vite build` exits 0 even though
    // Fable emitted nothing usable for that file. Turning it on is what surfaces the error again.
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(packageFs)] },
    );
    await h.start(buildConfig());
    expect(h.daemon.initialCompileCalls).toBe(1);

    const loud: Harness = harness(
      { fableModulesDiagnostics: true },
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(packageFs)] },
    );
    expect(loud.start(buildConfig())).rejects.toThrow(/FS0001/);
  });
});

describe("logging", () => {
  const project: StubDaemonOptions = {
    sourceFiles: [mathFs],
    compiled: { [mathFs]: "export const x = 1;" },
  };

  test("prints one line for a compile and nothing else", async () => {
    const lines: string[] = await linesFrom({}, project);
    expect(lines).toHaveLength(1);
    expect(lines[0]).toMatch(/^\[fable\] compiled App\.fsproj in \d+\.\d\ds$/);
  });

  test("logs paths relative to the Vite root", async () => {
    const lines: string[] = await linesFrom({}, project);
    // Vite prints paths relative to the root; an absolute path is noise the user has to read past.
    expect(lines.join("\n")).not.toContain(sampleProject);
  });

  test("prints the detail again when debug is on", async () => {
    const lines: string[] = await linesFrom({ debug: true }, project);
    expect(lines.length).toBeGreaterThan(1);
    expect(lines.join("\n")).toContain("entry project App.fsproj");
    expect(lines.join("\n")).toContain("daemon: starting");
  });

  test("reports diagnostics whether or not debug is on", async () => {
    const lines: string[] = await linesFrom(
      {},
      { ...project, projectDiagnostics: [errorAt(mathFs)] },
    );
    expect(lines.join("\n")).toContain("This expression was expected to have type int");
  });

  test("reports a failed compile", async () => {
    const lines: string[] = [];
    const h: Harness = harness();
    h.daemon.failWith(new Error("daemon exploded"));
    await h.start(recordingConfig(lines));
    expect(lines.join("\n")).toContain("daemon exploded");
  });
});

describe("fast refresh", () => {
  /** A resolved config carrying the `oxc` options `@vitejs/plugin-react` would have set. */
  function configWithOxc(oxc: unknown, warnings: string[]): ResolvedConfig {
    return resolvedConfig({
      oxc,
      logger: {
        ...silentLogger,
        warn: (message: string): void => {
          warnings.push(message);
        },
      },
    } as unknown as Partial<ResolvedConfig>);
  }

  /** What plugin-react sets when it is left on its defaults. */
  const defaultRefreshInclude: RegExp[] = [/\.[tj]sx?(?:\?.*)?$/];

  async function warningsFor(
    pluginOptions: PluginOptions,
    oxc: unknown,
    config?: Partial<ResolvedConfig>,
  ): Promise<string[]> {
    const warnings: string[] = [];
    const h: Harness = harness(pluginOptions);
    const resolved: ResolvedConfig = configWithOxc(oxc, warnings);
    await (h.plugin.configResolved as any).call({}, { ...resolved, ...config });
    return warnings;
  }

  test("warns when plugin-react will not refresh .fs components", async () => {
    // The failure is silent otherwise: the component still renders, it just reloads the page on
    // every edit instead of updating in place.
    const warnings: string[] = await warningsFor(
      { jsx: "automatic" },
      { jsx: { runtime: "automatic", refresh: true }, jsxRefreshInclude: defaultRefreshInclude },
    );
    expect(warnings.join("\n")).toContain("Fast Refresh");
  });

  test("stays quiet when .fs is in plugin-react's filter", async () => {
    const warnings: string[] = await warningsFor(
      { jsx: "automatic" },
      { jsx: { runtime: "automatic", refresh: true }, jsxRefreshInclude: [/\.fs(?:\?.*)?$/] },
    );
    expect(warnings).toEqual([]);
  });

  test("stays quiet when the React Compiler owns refresh", async () => {
    // `react({ compiler: true })` turns the global refresh flag off and applies refresh itself, so
    // the flag says nothing. `jsxRefreshInclude` still reflects the user's `include`.
    const warnings: string[] = await warningsFor(
      { jsx: "automatic" },
      { jsx: { runtime: "automatic", refresh: false }, jsxRefreshInclude: [/\.fs(?:\?.*)?$/] },
    );
    expect(warnings).toEqual([]);
  });

  test("stays quiet when plugin-react is not in use at all", async () => {
    const warnings: string[] = await warningsFor({ jsx: "automatic" }, { jsx: "preserve" });
    expect(warnings).toEqual([]);
  });

  test("stays quiet when the plugin emits no JSX", async () => {
    const warnings: string[] = await warningsFor(
      {},
      { jsx: { runtime: "automatic", refresh: true }, jsxRefreshInclude: defaultRefreshInclude },
    );
    expect(warnings).toEqual([]);
  });

  test("stays quiet during a build, where there is no Fast Refresh", async () => {
    const warnings: string[] = await warningsFor(
      { jsx: "automatic" },
      { jsx: { runtime: "automatic", refresh: true }, jsxRefreshInclude: defaultRefreshInclude },
      { command: "build" as const },
    );
    expect(warnings).toEqual([]);
  });
});

describe("load", () => {
  test("serves the compiled output for a project file", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    const result: LoadOutput = await h.load(mathFs);
    expect(result?.code).toBe("export const sum = 1;");
  });

  test("signals that the source mapping was lost rather than preserved", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    // `null` would have Vite treat the JavaScript as the contents of the `.fs` file, and every
    // map built from it would then point at F# lines that never held that code.
    expect((await h.load(mathFs))?.map).toEqual({ mappings: "" });
  });

  test("keys compiled output off what the daemon returned, not the source list", async () => {
    // The daemon reports signature files as sources but never compiles them.
    const componentFs = `${sampleProject}/Component.fs`;
    const componentFsi = `${sampleProject}/Component.fsi`;
    const h: Harness = harness(
      {},
      {
        sourceFiles: [componentFs, componentFsi],
        compiled: { [componentFs]: "export const c = 1;" },
      },
    );
    await h.start();
    expect((await h.load(componentFs))?.code).toBe("export const c = 1;");
    // Nothing routes a signature file here in the first place; the browser imports the `.fs`.
    expect(h.loadFilterMatches(componentFsi)).toBe(false);
  });

  test("says the module is JavaScript rather than leaving the extension to say", async () => {
    // `vite:oxc` sets this too, but only for ids `@vitejs/plugin-react` claims, so a project
    // without it would leave rolldown to infer a module type from `.fs`.
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    expect((await h.load(mathFs))?.moduleType).toBe("js");
  });

  test("matches an F# id that carries a query", () => {
    const h: Harness = harness();
    expect(h.loadFilterMatches(mathFs)).toBe(true);
    expect(h.loadFilterMatches(`${mathFs}?worker`)).toBe(true);
    expect(h.loadFilterMatches(`${sampleProject}/Script.fsx?v=1`)).toBe(true);
    expect(h.loadFilterMatches(`${sampleProject}/Program.cs`)).toBe(false);
  });

  test("leaves ?raw and ?url to Vite's asset plugin", () => {
    const h: Harness = harness();
    // These ask for the file, not the module it compiles to; Vite's asset plugin already answered.
    expect(h.loadFilterMatches(`${mathFs}?raw`)).toBe(false);
    expect(h.loadFilterMatches(`${mathFs}?url`)).toBe(false);
  });

  test("serves the compiled output for an id that carries a query", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    // The map is keyed by file path, so the lookup has to drop the query first.
    expect((await h.load(`${mathFs}?worker`))?.code).toBe("export const sum = 1;");
  });

  test("errors rather than handing raw F# to the JS parser in dev", async () => {
    // Loading nothing leaves Vite to read the file and hand the F# to the JavaScript parser, so
    // the page breaks either way. A warning only made it break less legibly.
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start();
    expect(h.load(`${sampleProject}/Unknown.fs`)).rejects.toThrow(
      "Unknown.fs is not part of App.fsproj, so Fable did not compile it.",
    );
  });

  test("errors rather than handing raw F# to the JS parser during a build", async () => {
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start(buildConfig());
    expect(h.load(`${sampleProject}/Unknown.fs`)).rejects.toThrow(
      "Unknown.fs is not part of App.fsproj, so Fable did not compile it.",
    );
  });

  test("applies the JSX transform when jsx is automatic", async () => {
    const h: Harness = harness(
      { jsx: "automatic" },
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = <div>hi</div>;" } },
    );
    await h.start();
    const result: LoadOutput = await h.load(libraryFs);
    expect(result?.code).toContain("jsx");
    expect(result?.code).not.toContain("<div>");
  });

  test("leaves JSX alone when the option is off", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = 1;" } },
    );
    await h.start();
    expect((await h.load(libraryFs))?.code).toBe("export const a = 1;");
  });
});

describe("hot updates", () => {
  /** A module node as Vite's graph would hold it, with `importers` deciding HMR propagation. */
  function moduleFor(file: string, importers: number = 1): HotModule {
    return { id: file, importers: new Set(Array.from({ length: importers }, (): object => ({}))) };
  }

  test("recompiles a changed F# file and refreshes its output", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "const v = 1;" } },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));

    h.daemon.setCompileResult({ [mathFs]: "const v = 2;" });
    await h.hotUpdate(mathFs);

    expect(h.daemon.compileCalls).toEqual([[mathFs]]);
    expect((await h.load(mathFs))?.code).toBe("const v = 2;");
  });

  test("ignores files that are not part of the project", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "const v = 1;" } },
    );
    await h.start();
    await h.hotUpdate(`${sampleProject}/README.md`);
    expect(h.daemon.compileCalls).toHaveLength(0);
  });

  test("sends an overlay error and no update when compilation fails", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "const v = 1;" } },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));
    h.daemon.setCompileResult({}, [errorAt(mathFs)]);

    const result: HotModule[] | void = await h.hotUpdate(mathFs);

    expect((h.sent[0] as any).type).toBe("error");
    expect((h.sent[0] as any).err.message).toBe(errorAt(mathFs).message);
    expect(result).toEqual([]);
  });

  test("coalesces concurrent changes into one compile", async () => {
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "const v = 1;", [libraryFs]: "const w = 1;" },
      },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));
    h.graph.set(libraryFs, moduleFor(libraryFs));

    await Promise.all([h.hotUpdate(mathFs), h.hotUpdate(libraryFs)]);

    expect(h.daemon.compileCalls).toHaveLength(1);
    expect(h.daemon.compileCalls[0].toSorted()).toEqual([libraryFs, mathFs].toSorted());
  });

  test("a change arriving mid-compile is not answered by the in-flight batch", async () => {
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "const v = 1;", [libraryFs]: "const w = 1;" },
      },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));
    h.graph.set(libraryFs, moduleFor(libraryFs));

    // Math.fs compiles slowly; Library.fs changes while that is still running.
    h.daemon.setCompileResult({ [mathFs]: "const v = 2;" });
    const releaseMath: () => void = h.daemon.blockNextCompile();
    const mathUpdate: Promise<HotModule[] | void> = h.hotUpdate(mathFs);

    await afterCoalescing();
    h.daemon.setCompileResult({ [libraryFs]: "const w = 2;" });
    const libraryUpdate: Promise<HotModule[] | void> = h.hotUpdate(libraryFs);

    releaseMath();
    await Promise.all([mathUpdate, libraryUpdate]);

    // Each file was compiled by its own batch, and Library.fs really did get compiled.
    expect(h.daemon.compileCalls).toEqual([[mathFs], [libraryFs]]);
    expect((await h.load(libraryFs))?.code).toBe("const w = 2;");
  });

  test("still updates a module nothing imports, rather than doing nothing", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "const v = 1;" } },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs, 0));

    h.daemon.setCompileResult({ [mathFs]: "const v = 2;" });
    const result: HotModule[] | void = await h.hotUpdate(mathFs);

    // Returning [] would make Vite log "no modules matched" and send nothing at all.
    expect(result).not.toEqual([]);
  });

  test("invalidates every module whose output changed, not just the edited file", async () => {
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "const v = 1;", [libraryFs]: "const w = 1;" },
      },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));
    h.graph.set(libraryFs, moduleFor(libraryFs));

    // Editing Math.fs changes the output of Library.fs too.
    h.daemon.setCompileResult({ [mathFs]: "const v = 2;", [libraryFs]: "const w = 2;" });
    const result: HotModule[] | void = await h.hotUpdate(mathFs);

    expect((result as HotModule[]).map((m: HotModule): string => m.id).toSorted()).toEqual(
      [libraryFs, mathFs].toSorted(),
    );
  });

  test("leaves modules alone when recompiling did not change their output", async () => {
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "const v = 1;", [libraryFs]: "const w = 1;" },
      },
    );
    await h.start();
    h.graph.set(mathFs, moduleFor(mathFs));
    h.graph.set(libraryFs, moduleFor(libraryFs));

    // Fable returns Library.fs because it sits downstream, but its output is unchanged. Pulling it
    // into the update would drag a module that cannot accept one into the picture.
    h.daemon.setCompileResult({ [mathFs]: "const v = 2;", [libraryFs]: "const w = 1;" });
    const result: HotModule[] | void = await h.hotUpdate(mathFs);

    expect((result as HotModule[]).map((m: HotModule): string => m.id)).toEqual([mathFs]);
  });

  test("recompiles the implementation file when a signature file changes", async () => {
    const componentFsi = `${sampleProject}/Component.fsi`;
    const componentFs = `${sampleProject}/Component.fs`;
    const h: Harness = harness(
      {},
      { sourceFiles: [componentFs, componentFsi], compiled: { [componentFs]: "const c = 1;" } },
    );
    await h.start();
    h.graph.set(componentFs, moduleFor(componentFs));

    h.daemon.setCompileResult({ [componentFs]: "const c = 2;" });
    await h.hotUpdate(componentFsi);

    expect(h.daemon.compileCalls).toEqual([[componentFsi]]);
    expect((await h.load(componentFs))?.code).toBe("const c = 2;");
  });

  test("re-cracks the project and reloads when an MSBuild dependency changes", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();

    await h.hotUpdate(appFsproj);

    expect(h.daemon.projectChangedCalls).toHaveLength(2);
    expect(h.sent).toContainEqual({ type: "full-reload" });
  });

  test("re-cracks the project when an F# file is added or removed", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();

    await h.hotUpdate(`${sampleProject}/New.fs`, "create");

    expect(h.daemon.projectChangedCalls).toHaveLength(2);
  });
});

describe("buildEnd", () => {
  test("disposes the daemon", async () => {
    const h: Harness = harness();
    await h.start();
    (h.plugin.buildEnd as any).call({});
    expect(h.daemon.disposeCalls).toBe(1);
  });
});
