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

/** Records what the plugin pushes to the browser, and what the module graph holds. */
interface EnvironmentStub {
  hot: { send(payload?: unknown): unknown };
  moduleGraph: { getModulesByFile(file: string): Set<HotModule> | undefined };
  sent: unknown[];
}

type TransformOutput = { code: string; map: { mappings: "" } } | undefined;

interface Harness {
  plugin: Plugin;
  daemon: StubDaemon;
  watched: string[];
  /** Runs `configResolved` then `buildStart`, as Vite would. */
  start(config?: ResolvedConfig): Promise<void>;
  /** Calls the `transform` hook the way Vite's plugin container does. */
  transform(id: string): Promise<TransformOutput>;
  /** Whether rolldown would call the `transform` handler for this id at all. */
  transformFilterMatches(id: string): boolean;
  /** Calls `hotUpdate` the way Vite's HMR pipeline does. */
  hotUpdate(file: string, type?: "create" | "update" | "delete"): Promise<HotModule[] | void>;
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
    ): Promise<HotModule[] | void> {
      const modules: HotModule[] = graph.has(file) ? [graph.get(file)!] : [];
      return (plugin.hotUpdate as any).call(
        { environment },
        {
          type,
          file,
          timestamp: Date.now(),
          modules,
          read: async (): Promise<string> => "",
          server: { environments: { client: environment } },
        },
      );
    },
    async start(config = resolvedConfig()) {
      await (plugin.configResolved as any).call(context, config);
      await (plugin.buildStart as any).call(context, {});
    },
    async transform(id: string): Promise<TransformOutput> {
      return (plugin.transform as any).handler.call(context, "", id);
    },
    transformFilterMatches(id: string): boolean {
      const filter: { include: RegExp[]; exclude: RegExp[] } = (plugin.transform as any).filter.id;
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

/** Waits past the plugin's 50ms coalescing window. */
function afterCoalescing(): Promise<void> {
  return new Promise((resolve: () => void) => setTimeout(resolve, 120));
}

describe("configResolved", () => {
  test("uses the fsproj given in the plugin options", async () => {
    const h: Harness = harness();
    await (h.plugin.configResolved as any).call({}, resolvedConfig());
    await (h.plugin.buildStart as any).call({ addWatchFile: () => {} }, {});
    expect(h.daemon.projectChangedCalls[0].project).toBe(appFsproj);
  });

  test("finds the fsproj in the Vite root when no option is given", async () => {
    const daemon: StubDaemon = createStubDaemon({});
    const plugin: Plugin = createFablePlugin({}, (): StubDaemon => daemon);
    const context: PluginContextStub = {
      addWatchFile: (): void => {},
      error: (message: string): never => {
        throw new Error(message);
      },
    };
    await (plugin.configResolved as any).call(context, resolvedConfig());
    await (plugin.buildStart as any).call(context, {});
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

  test("watches source files and MSBuild dependencies", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();
    expect(h.watched).toContain(mathFs);
    expect(h.watched).toContain(appFsproj);
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

describe("transform", () => {
  test("serves the compiled output for a project file", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    const result: TransformOutput = await h.transform(mathFs);
    expect(result?.code).toBe("export const sum = 1;");
  });

  test("signals that the source mapping was lost rather than preserved", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    // `null` would tell Vite the previous mapping still applies, and it would then build a map
    // claiming the JavaScript is the contents of a `.fs` file.
    expect((await h.transform(mathFs))?.map).toEqual({ mappings: "" });
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
    expect((await h.transform(componentFs))?.code).toBe("export const c = 1;");
    expect(await h.transform(componentFsi)).toBeUndefined();
  });

  test("matches an F# id that carries a query", () => {
    const h: Harness = harness();
    expect(h.transformFilterMatches(mathFs)).toBe(true);
    expect(h.transformFilterMatches(`${mathFs}?worker`)).toBe(true);
    expect(h.transformFilterMatches(`${sampleProject}/Script.fsx?v=1`)).toBe(true);
    expect(h.transformFilterMatches(`${sampleProject}/Program.cs`)).toBe(false);
  });

  test("leaves ?raw and ?url to Vite's asset plugin", () => {
    const h: Harness = harness();
    // These ask for the file, not the module it compiles to; Vite's asset plugin already answered.
    expect(h.transformFilterMatches(`${mathFs}?raw`)).toBe(false);
    expect(h.transformFilterMatches(`${mathFs}?url`)).toBe(false);
  });

  test("serves the compiled output for an id that carries a query", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    // The map is keyed by file path, so the lookup has to drop the query first.
    expect((await h.transform(`${mathFs}?worker`))?.code).toBe("export const sum = 1;");
  });

  test("returns nothing for an F# file the daemon never compiled", async () => {
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start();
    expect(await h.transform(`${sampleProject}/Unknown.fs`)).toBeUndefined();
  });

  test("errors rather than handing raw F# to the JS parser during a build", async () => {
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start(buildConfig());
    expect(h.transform(`${sampleProject}/Unknown.fs`)).rejects.toThrow(
      `${sampleProject}/Unknown.fs was not compiled by Fable, so it cannot be bundled.`,
    );
  });

  test("applies the JSX transform when jsx is automatic", async () => {
    const h: Harness = harness(
      { jsx: "automatic" },
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = <div>hi</div>;" } },
    );
    await h.start();
    const result: TransformOutput = await h.transform(libraryFs);
    expect(result?.code).toContain("jsx");
    expect(result?.code).not.toContain("<div>");
  });

  test("leaves JSX alone when the option is off", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = 1;" } },
    );
    await h.start();
    expect((await h.transform(libraryFs))?.code).toBe("export const a = 1;");
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
    expect((await h.transform(mathFs))?.code).toBe("const v = 2;");
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
    expect((await h.transform(libraryFs))?.code).toBe("const w = 2;");
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
    expect((await h.transform(componentFs))?.code).toBe("const c = 2;");
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
